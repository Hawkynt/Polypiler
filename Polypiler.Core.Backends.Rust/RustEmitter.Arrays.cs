using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Rust.Emit;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary>The code for two views of one array passed where one of them is written, which safe Rust cannot borrow twice.</summary>
  public const string MayAlias = "PLY1566";

  /// <summary>The registered diagnostic of <see cref="MayAlias"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MayAliasDescriptor = new(MayAlias, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "rust.backend.aliasing");

  private sealed partial class Writer {

    /// <summary>What every function of the module does with arrays.</summary>
    private IReadOnlyDictionary<string, FunctionArrays> _arrays = new Dictionary<string, FunctionArrays>();

    /// <summary>The facts of the function being written.</summary>
    private FunctionArrays _arrayFacts = new();

    /// <summary>The parameters of the function being written that are arrays, with their positions.</summary>
    private Dictionary<IrLocal, int> _arrayParameters = [];

    /// <summary>Whether the array a function returns is borrowed exclusively.</summary>
    private static bool ResultIsMutable(IrFunction function, FunctionArrays facts)
      => facts.Result == ArrayHolder.Borrowed && facts.ResultParameters.All(facts.MutableParameters.Contains);

    /// <summary>The Rust type of the elements of an array type; refused for any but a scalar.</summary>
    private string ElementType(IrType array) => array.Element is { } element && (TypeName(element) is not null || element.IsStruct || element.IsEnum || element.IsString || element.IsFunction) ? this.Type(element) : ((RsPath)this.Refuse($"an array of {array.Element}")).Name;

    /// <summary>The lifetime the result of a function borrows with, where Rust cannot elide it: <c>'a</c>, or empty.</summary>
    private static string Lifetime(IrFunction function, FunctionArrays facts)
      => facts.Result == ArrayHolder.Borrowed && function.Parameters.Count(parameter => parameter.Type.IsArray) > 1 ? "'a " : "";

    /// <summary>The type of a parameter: an array borrowed shared or exclusive, with the result's lifetime where it borrows from it.</summary>
    private string ParameterType(IrFunction function, int index) {
      var parameter = function.Parameters[index];
      if (parameter.Type.IsString)
        return BorrowedString;
      if (IsError(parameter.Type))
        return ((RsPath)this.Refuse("an exception passed as a value")).Name;
      if (parameter.Type.IsObject)
        return this.ObjectParameterType(parameter);
      if (parameter.Type.IsView)
        return this.ViewType(parameter.Type, this.WritesView(parameter));
      if (parameter.Type.IsCollection)
        return this.CollectionParameterType(function, index);
      if (!parameter.Type.IsArray)
        return this.Type(parameter.Type);
      if (this.IsNullableArray(parameter))
        return this._arrayFacts.MutableParameters.Contains(index) ? ((RsPath)this.Refuse("an array that may be null and is written")).Name : this.NullableArrayType(parameter.Type, true);

      var lifetime = this._arrayFacts.ResultParameters.Contains(index) ? Lifetime(function, this._arrayFacts) : "";
      return $"&{lifetime}{(this._arrayFacts.MutableParameters.Contains(index) ? "mut " : "")}[{this.ElementType(parameter.Type)}]";
    }

    /// <summary>The result type of a function, <see langword="null"/> for none.</summary>
    private string? ResultType(IrFunction function) {
      if (function.ReturnType == IrType.Void)
        return null;
      if (IsError(function.ReturnType))
        return ((RsPath)this.Refuse("an exception returned as a value")).Name;
      if (function.ReturnType.IsObject)
        return this.ObjectType(function.ReturnType, this._nullness.ReturnsNull(function.Name));
      if (function.ReturnType.IsCollection)
        return this.CollectionType(function.ReturnType);
      if (!function.ReturnType.IsArray)
        return this.Type(function.ReturnType);
      if (this._arrayNulls.ReturnsNull(function.Name))
        return this._arrayFacts.Result == ArrayHolder.Owned ? this.NullableArrayType(function.ReturnType, false) : ((RsPath)this.Refuse("a borrowed array result that may be null")).Name;

      var element = this.ElementType(function.ReturnType);
      return this._arrayFacts.Result == ArrayHolder.Borrowed
        ? $"&{Lifetime(function, this._arrayFacts)}{(ResultIsMutable(function, this._arrayFacts) ? "mut " : "")}[{element}]"
        : $"Vec<{element}>";
    }

    /// <summary>The type of a local: an array owned as a vector or borrowed.</summary>
    private string? LocalType(IrLocal local) {
      if (local.Type.IsArray && this.IsFieldAlias(local))
        return null;

      if (IsError(local.Type))
        return ErrorType;
      if (local.Type.IsObject)
        return this.ObjectType(local.Type, this.MayBeNull(local));
      if (local.Type.IsView)
        return this.ViewType(local.Type, this.WritesView(local));
      if (local.Type.IsCollection)
        return this.CollectionType(local.Type);
      if (!local.Type.IsArray)
        return this.Type(local.Type);
      if (this.IsNullableArray(local))
        return this.NullableArrayType(local.Type, this.IsReference(local));

      var element = this.ElementType(local.Type);
      return this._arrayFacts.Locals.GetValueOrDefault(local) == ArrayHolder.Borrowed
        ? $"&{(this._arrayFacts.MutableLocals.Contains(local) ? "mut " : "")}[{element}]"
        : $"Vec<{element}>";
    }

    /// <summary>Whether a local is an owned array that is written, which needs <c>let mut</c>.</summary>
    private bool IsWrittenVector(IrLocal local)
      => local.Type.IsArray && this._arrayFacts.Locals.GetValueOrDefault(local) == ArrayHolder.Owned && this._arrayFacts.MutableLocals.Contains(local);

    /// <summary>Whether an array variable is a reference already: a parameter, or a local that borrows.</summary>
    private bool IsReference(IrLocal local)
      => !this._owned.Contains(local) && this._arrayParameters.TryGetValue(local, out var position) ? !(this._reassigned.Contains(local) && this._arrayFacts.CopiedParameters.Contains(position))
        : this._arrayFacts.Locals.GetValueOrDefault(local) == ArrayHolder.Borrowed;

    /// <summary>The value a local of an array type is given: the vector itself, or a borrow.</summary>
    private RsExpression ArrayInitial(IrLocal local, IrExpr value)
      => this.IsFieldAlias(local) ? this.FieldAlias(local)
        : this.IsNullableArray(local) ? this.NullableArray(value, this.IsReference(local))
        : !this.IsReference(local) ? this.Vector(value, false) : this.Borrow(value, this._arrayFacts.MutableLocals.Contains(local));

    /// <summary>The array a function returns: a vector it owns, or the borrow of a parameter.</summary>
    private RsExpression ArrayResult(IrFunction function, IrExpr value)
      => this._arrayNulls.ReturnsNull(function.Name) ? this.NullableArray(value, false)
        : this._arrayFacts.Result == ArrayHolder.Owned ? this.Vector(value, true) : this.Borrow(value, ResultIsMutable(function, this._arrayFacts));

    /// <summary>
    /// An array as a vector the receiving place owns: a new one, the result of a call that makes one,
    /// an owned local - moved where it is returned, cloned where it changes hands - or a borrowed one
    /// copied, which the ownership analysis allows only for an array nothing writes.
    /// </summary>
    private RsExpression Vector(IrExpr array, bool move = true) {
      switch (array) {
        case IrGlobalRef:
          return this.Refuse("a global array used as a whole");
        case IrCollection { Op: IrCollectionOp.ListCopyToArray }:
          return this.Expression(array);
        case IrLocalRef { Local: var alias } when this.IsFieldAlias(alias):
          // A copy would part the variable from the field, which .NET shares with whoever takes it.
          return this.Refuse("a variable that names a field's array given away as a whole");
        case IrLocalRef or IrBind or IrCall when this.IsNullableArray(array):
          return new RsMethodCall(this.Receiver(this.NonNullArray(array, false)), "to_vec", []) { Type = array.Type };
        case IrNewArray creation:
          return new RsMacro("vec", [new RsArray([this.Zero(creation.Type.Element!)]) { Count = this.Length(creation.Length) }]) { Bracketed = true, Type = array.Type };
        case IrIntrinsic { Op: IrIntrinsicOp.ArrayOf, Arguments.Count: 0 }:
          return new RsCall(new RsPath("Vec::new"), []) { Type = array.Type };
        case IrIntrinsic { Op: IrIntrinsicOp.ArrayOf } literal:
          return new RsMacro("vec", [new RsArray([.. literal.Arguments.Select(this.ElementValue)])]) { Bracketed = true, Type = array.Type };
        case IrLocalRef { Local: var local } when !this.IsReference(local):
          var vector = new RsPath(this._locals[local]) { Type = array.Type };
          return move ? vector : new RsMethodCall(vector, "clone", []) { Type = array.Type };
        case IrLocalRef { Local: var local }:
          return new RsMethodCall(new RsPath(this._locals[local]), "to_vec", []) { Type = array.Type };
        case IrCall call when this._arrays.TryGetValue(call.Function, out var callee) && callee.Result == ArrayHolder.Owned:
          return this.Expression(call);
        case IrFieldLoad { Target.Type.IsObject: true } load:
          return new RsMethodCall(this.FieldArray(load, false), "clone", []) { Type = array.Type };
        case IrIntrinsic { Op: IrIntrinsicOp.StrSplit or IrIntrinsicOp.StrToCodeUnits or IrIntrinsicOp.EnumNames or IrIntrinsicOp.EnumValues or IrIntrinsicOp.ViewToArray }:
          return this.Expression(array);
        case IrCall call:
          return new RsMethodCall(this.Expression(call), "to_vec", []) { Type = array.Type };
        case IrConditional choice:
          return this.Choice(choice, branch => this.Vector(branch, move));
        default:
          return this.Refuse("an array that is both owned and borrowed");
      }
    }

    /// <summary>
    /// An array as a borrow, <c>&amp;[T]</c> or <c>&amp;mut [T]</c>: a reference as it is, a vector
    /// or a table borrowed (a table only where it is read: the source of a copy, an element loop), a
    /// temporary array borrowed for the statement.
    /// </summary>
    private RsExpression Borrow(IrExpr array, bool mutable) {
      switch (array) {
        case IrGlobalRef:
          return this.Refuse("a global array used as a whole");
        case IrLocalRef { Local: var alias } when this.IsFieldAlias(alias):
          return this.FieldAliasSlice(alias, mutable);
        case IrLocalRef or IrBind or IrCall when this.IsNullableArray(array):
          return mutable ? this.Refuse("an array that may be null and is written") : this.NonNullArray(array, true);
        case IrFieldLoad { Target.Type.IsObject: true } load:
          return new RsReference(mutable, this.FieldArray(load, mutable)) { Type = array.Type };
        case IrLocalRef { Local: var local } when this.IsReference(local):
          return new RsPath(this._locals[local]) { Type = array.Type };
        case IrLocalRef reference:
          return new RsReference(mutable, new RsPath(this._locals[reference.Local]) { Type = array.Type }) { Type = array.Type };
        case IrConstRef table when this._constants.TryGetValue(table.Constant.Name, out var name):
          return new RsReference(false, new RsPath(name) { Type = array.Type }) { Type = array.Type };
        case IrIntrinsic { Op: IrIntrinsicOp.ArrayOf } literal:
          return new RsReference(mutable, new RsArray([.. literal.Arguments.Select(argument => Suffixed(this.Expression(argument)))]) { Type = array.Type }) { Type = array.Type };
        case IrCall call when this._arrays.TryGetValue(call.Function, out var callee) && callee.Result != ArrayHolder.Owned:
          return this.Expression(call);
        case IrConditional choice when this.HoldsNew(choice) && mutable:
          return this.Refuse("an exclusive borrow of a choice between a new array and another one");
        case IrConditional choice when this.HoldsNew(choice):
          // A temporary borrowed in a branch would not outlive the branch: the choice owns the vector, the borrow takes it all.
          return new RsReference(false, new RsParenthesized(this.Choice(choice, branch => this.Vector(branch, false)))) { Type = array.Type };
        case IrConditional choice:
          return this.Choice(choice, branch => this.Borrow(branch, mutable));
        default:
          return new RsReference(mutable, this.Vector(array)) { Type = array.Type };
      }
    }

    /// <summary>An array as the target of an index or a method: a variable or a table as it is, a literal as an array, anything else borrowed.</summary>
    private RsExpression ArrayTarget(IrExpr array, bool mutable = false) => array switch {
      IrLocalRef or IrBind or IrCall when this.IsNullableArray(array) => mutable ? this.Refuse("an array that may be null and is written") : this.NonNullArray(array, false),
      IrLocalRef reference => new RsPath(this._locals[reference.Local]) { Type = array.Type },
      IrFieldLoad { Target.Type.IsObject: true } load => this.FieldArray(load, mutable),
      IrConstRef table when this._constants.TryGetValue(table.Constant.Name, out var name) => new RsPath(name) { Type = array.Type },
      IrIntrinsic { Op: IrIntrinsicOp.ArrayOf } literal when !mutable => new RsArray([.. literal.Arguments.Select(argument => Suffixed(this.Expression(argument)))]) { Type = array.Type },
      IrCall => this.Expression(array),
      IrNewArray or IrIntrinsic { Op: IrIntrinsicOp.ViewToArray } => this.Vector(array),
      // A listing of an enum only read is an array, not a vector (clippy's useless_vec).
      IrIntrinsic { Op: IrIntrinsicOp.EnumNames or IrIntrinsicOp.EnumValues } listing when this.Expression(listing) is RsMacro { Bracketed: true } vector => vector.Arguments[0],
      IrConditional choice when this.HoldsNew(choice) && !mutable => new RsParenthesized(this.Choice(choice, branch => this.Vector(branch, false))) { Type = array.Type },
      _ => new RsParenthesized(this.Borrow(array, mutable)) { Type = array.Type }
    };

    /// <summary>
    /// An array an element loop runs over: a shared borrow as it is, an exclusive one through
    /// <c>iter()</c>, which leaves the borrow usable after the loop, a vector or a table borrowed.
    /// </summary>
    private RsExpression Iterated(IrExpr array) => array switch {
      IrLocalRef { Local: var local } when this._arrayParameters.TryGetValue(local, out var position) && this._arrayFacts.MutableParameters.Contains(position)
        || this._arrayFacts.Locals.GetValueOrDefault(local) == ArrayHolder.Borrowed && this._arrayFacts.MutableLocals.Contains(local)
        => new RsMethodCall(new RsPath(this._locals[local]), "iter", []),
      _ => this.Borrow(array, false)
    };

    /// <summary>Whether an array expression may give a new array: a new one, a literal, a call that makes one, a choice of such.</summary>
    private bool HoldsNew(IrExpr array) => array switch {
      IrNewArray or IrIntrinsic { Op: IrIntrinsicOp.ArrayOf } => true,
      IrCall call => this._arrays.TryGetValue(call.Function, out var callee) && callee.Result == ArrayHolder.Owned,
      IrConditional choice => this.HoldsNew(choice.WhenTrue) || this.HoldsNew(choice.WhenFalse),
      _ => false
    };

    /// <summary><c>if c { a } else { b }</c> of two arrays written the same way.</summary>
    private RsIf Choice(IrConditional choice, Func<IrExpr, RsExpression> branch) {
      var otherwise = branch(choice.WhenFalse);
      static RsBlock Of(RsExpression value) => value is RsBlockExpression { Block: var block } ? block : new RsBlock([], value);
      return new RsIf(this.Expression(choice.Condition), Of(branch(choice.WhenTrue)),
        otherwise is RsIf nested ? nested : new RsBlockExpression(Of(otherwise))) { Type = choice.Type };
    }

    /// <summary>The zero of an element type: <c>0</c>, <c>0.0</c>, <c>false</c>.</summary>
    private RsExpression Zero(IrType element) => element.IsFunction ? new RsPath("None")
      : element.IsStruct || element.IsEnum
      ? new RsCall(new RsPath(this.Type(element) + "::default"), []) { Type = element }
      : TypeName(element) is null ? this.Refuse($"an array of {element}") : Literal(new IrConst(element, 0)) with { Type = null };

    /// <summary>A length as a <c>usize</c>: a constant as it is, the length of an array as <c>len()</c>, a negative one trapping with an overflow, as a new array's does.</summary>
    private RsExpression Length(IrExpr length) {
      switch (length) {
        case IrConst { Value: >= 0 } constant:
          return new RsLiteral(constant.Value.ToString(CultureInfo.InvariantCulture));
        case IrArrayLength measured:
          return new RsMethodCall(this.ArrayTarget(measured.Array), "len", []);
      }

      var value = this.Expression(length);
      if (!length.Type.IsSigned)
        return this.Cast(value, IrType.U64) with { Target = "usize" };

      return this.Checked(new RsCall(new RsPath("usize::try_from"), [Suffixed(value)]), IrTrap.IntegerOverflow);
    }

    /// <summary>The number of elements of an array, as the IR's <c>i32</c>.</summary>
    private RsExpression ArrayLength(IrArrayLength length) => length.Array is IrIntrinsic { Op: IrIntrinsicOp.EnumNames or IrIntrinsicOp.EnumValues } listing
      ? Literal(new IrConst(IrType.I32, EnumOperations.Of(listing).Members.Count))
      : this.Cast(new RsMethodCall(this.ArrayTarget(length.Array), "len", []), length.Type);

    /// <summary>
    /// <c>a[i] = v</c>. Rust evaluates the value before the place, the IR the index first: an index
    /// that is no variable or constant, beside a value that is none either, is bound before.
    /// </summary>
    private void Store(IrStore store, List<RsStatement> into) {
      var target = this.ArrayTarget(store.Array, true);
      RsExpression index;
      if (store.Index is IrStep { Target: IrLocalRef counter } step) {
        // a[i++] = v: the index, then the step, as statements of their own.
        var name = this.Temporary("index");
        if (step.Postfix)
          into.Add(new RsLet(name, false, null, this.Index(counter)));
        into.Add(new RsExpressionStatement(this.StepAssignment(counter, step)));
        if (!step.Postfix)
          into.Add(new RsLet(name, false, null, this.Index(counter)));
        index = new RsPath(name);
      } else if (store.Index is not (IrLocalRef or IrConst) && (store.Value is not (IrLocalRef or IrConst or IrConstRef) || HasEffect(store.Index) || IsFieldArray(store.Array))) {
        var name = this.Temporary("index");
        into.Add(new RsLet(name, false, null, this.Index(store.Index)));
        index = new RsPath(name);
      } else {
        index = this.Index(store.Index);
      }

      // The value of an element of a field first, so that it holds no borrow of the field when the element is written.
      if (IsFieldArray(store.Array) && store.Value is not (IrLocalRef or IrConst or IrConstRef)) {
        var name = this.Temporary("value");
        into.Add(new RsLet(name, false, null, this.ElementValue(store.Value)));
        into.Add(new RsExpressionStatement(new RsAssign(this.Element(target, index, store.Value.Type, true), "=", new RsPath(name))));
        return;
      }

      var element = this.Element(target, index, store.Value.Type, true);
      if (store.Value is IrBinary { Left: IrArrayLoad self } binary && Same(self.Array, store.Array) && Same(self.Index, store.Index) && store.Index is IrLocalRef or IrConst
        && Compound(binary) is { } op) {
        into.Add(new RsExpressionStatement(new RsAssign(element, op + "=", this.Operand(this.Expression(binary.Right), 2))));
        return;
      }

      into.Add(new RsExpressionStatement(new RsAssign(element, "=", this.ElementValue(store.Value))));
    }

    /// <summary>
    /// A step of an element: the index evaluated once into a binding, the element read, stepped and
    /// written back; as an expression a block that gives the old or the new value.
    /// </summary>
    private RsExpression ElementStep(IrArrayLoad load, IrStep step, bool statement) {
      var statements = new List<RsStatement>();
      var index = this.Index(load.Index);
      if (load.Index is not (IrLocalRef or IrConst)) {
        var name = this.Temporary("index");
        statements.Add(new RsLet(name, false, null, index));
        index = new RsPath(name);
      }

      var element = this.Element(this.ArrayTarget(load.Array, true), index, load.Type, true);
      if (statement && load.Type.IsFloat) {
        statements.Add(new RsExpressionStatement(new RsAssign(element, step.Increment ? "+=" : "-=", Literal(new IrConst(load.Type, IrOperators.Bits(load.Type, 1.0))))));
        return new RsBlockExpression(new RsBlock(statements, null));
      }

      if (statement) {
        statements.Add(new RsExpressionStatement(new RsAssign(element, "=", this.Stepped(element, step))));
        return new RsBlockExpression(new RsBlock(statements, null));
      }

      var old = this.Temporary("old");
      statements.Add(new RsLet(old, false, null, element));
      var oldValue = new RsPath(old) { Type = load.Type };
      var stepped = this.Stepped(oldValue, step);
      statements.Add(new RsExpressionStatement(new RsAssign(element, "=", stepped)));
      return new RsBlockExpression(new RsBlock(statements, step.Postfix ? oldValue : element)) { Type = load.Type };
    }

    /// <summary>The value an element of an array is given: a string as a vector of its own.</summary>
    private RsExpression ElementValue(IrExpr value) => value.Type.IsString ? this.StringValue(value) : this.Argument(value);

    /// <summary>Whether two expressions are the same variable, table or constant.</summary>
    private static bool Same(IrExpr first, IrExpr second) => (first, second) switch {
      (IrLocalRef a, IrLocalRef b) => a.Local == b.Local,
      (IrConstRef a, IrConstRef b) => a.Constant == b.Constant,
      (IrConst a, IrConst b) => a == b,
      _ => false
    };

    /// <summary>Whether evaluating an expression may change a variable or call a function.</summary>
    private static bool HasEffect(IrExpr expression) => IrTree.Nodes(expression).Any(node => node is IrStep or IrCall or IrMethodCall or IrInvoke);

    /// <summary>A name for a binding the translation adds, unused by the function.</summary>
    private string Temporary(string stem) {
      var name = stem;
      for (var suffix = 1; this._taken.Contains(name); ++suffix)
        name = stem + suffix.ToString(CultureInfo.InvariantCulture);

      this._taken.Add(name);
      return name;
    }

    /// <summary>The names the function being written has taken.</summary>
    private HashSet<string> _taken = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>Array.Copy</c>: within one array the spelling function <c>ply_copy_within</c>
    /// (<c>copy_within</c>), between two <c>ply_copy</c> (<c>copy_from_slice</c>); each checks both
    /// ranges with <c>ply_range</c> before it copies anything.
    /// </summary>
    private RsExpression ArrayCopy(IrIntrinsic copy) {
      var (source, sourceIndex, target, targetIndex, count) = (copy.Arguments[0], copy.Arguments[1], copy.Arguments[2], copy.Arguments[3], copy.Arguments[4]);
      if (source is IrLocalRef { Local: var a } && target is IrLocalRef { Local: var b } && a == b)
        return this.Spelled(new RsCall(new RsPath(this.CopyHelper(true)), [this.Borrow(target, true), this.Expression(sourceIndex), this.Expression(targetIndex), this.Expression(count)]));

      return this.Spelled(new RsCall(new RsPath(this.CopyHelper(false)), [this.Borrow(source, false), this.Expression(sourceIndex), this.Borrow(target, true), this.Expression(targetIndex), this.Expression(count)]));
    }

    /// <summary>The spelling function of a copy between two arrays, or within one.</summary>
    private string CopyHelper(bool within) {
      var name = within ? "ply_copy_within" : "ply_copy";
      var range = this.RangeHelper();
      this.Spell(name, () => this.SpelledFallible(() => {
        RsExpression Range(string start, string array) => this.Spelled(new RsCall(new RsPath(range), [new RsPath(start), new RsPath("length"), new RsMethodCall(new RsPath(array), "len", [])]));
        if (within)
          return new RsFunction(name, "", [new RsParameter("data", "&mut [T]"), new RsParameter("from", "i32"), new RsParameter("to", "i32"), new RsParameter("length", "i32")], null, new RsBlock([
            new RsLet("source", false, null, Range("from", "data")),
            new RsLet("target", false, null, Range("to", "data")),
            new RsExpressionStatement(new RsMethodCall(new RsPath("data"), "copy_within", [new RsPath("source"), new RsField(new RsPath("target"), "start")]))
          ], null)) { Generics = "<T: Copy>" };

        return new RsFunction(name, "", [new RsParameter("source", "&[T]"), new RsParameter("from", "i32"), new RsParameter("target", "&mut [T]"), new RsParameter("to", "i32"), new RsParameter("length", "i32")], null, new RsBlock([
          new RsLet("from", false, null, Range("from", "source")),
          new RsLet("to", false, null, Range("to", "target")),
          new RsExpressionStatement(new RsMethodCall(new RsIndex(new RsPath("target"), new RsPath("to")), "copy_from_slice", [new RsReference(false, new RsIndex(new RsPath("source"), new RsPath("from")))]))
        ], null)) { Generics = "<T: Copy>" };
      }));
      return name;
    }

    /// <summary>The spelling function <c>ply_range</c>: the range of <c>length</c> elements from <c>start</c> in a sequence of <c>size</c>, which panics with <c>IndexOutOfRange</c> unless it lies inside.</summary>
    private string RangeHelper() {
      const string name = "ply_range";
      this.Spell(name, () => this.SpelledFallible(() => {
        RsExpression Converted(string value) => this.Checked(new RsCall(new RsPath("usize::try_from"), [new RsPath(value)]), IrTrap.IndexOutOfRange);
        var (start, length, size) = (new RsPath("start"), new RsPath("length"), new RsPath("size"));
        var outside = new RsBinary(new RsBinary(length, ">", size), "||", new RsBinary(start, ">", new RsBinary(size, "-", length)));
        return new RsFunction(name, "", [new RsParameter("start", "i32"), new RsParameter("length", "i32"), new RsParameter("size", "usize")], "std::ops::Range<usize>", new RsBlock([
          new RsLet("start", false, null, Converted("start")),
          new RsLet("length", false, null, Converted("length")),
          new RsExpressionStatement(new RsIf(outside, new RsBlock([new RsExpressionStatement(this.Trap(IrTrap.IndexOutOfRange))], null), null))
        ], new RsRange(start, new RsBinary(start, "+", length), false)));
      }));
      return name;
    }

    /// <summary>Whether two array arguments are the same variable, where one of them is written.</summary>
    private bool Aliases(IrExpr first, IrExpr second)
      => first is IrLocalRef { Local: var a } && second is IrLocalRef { Local: var b } && a == b;

    /// <summary>Records that one array is passed twice where it is written, and returns a stand-in.</summary>
    private RsExpression Alias(IrExpr read, IrExpr written) {
      var what = $"views '{((IrLocalRef)read).Local.Name}' and '{((IrLocalRef)written).Local.Name}'";
      if (this._refused.Add(what))
        this._problems.Add(MayAliasDescriptor.At($"{what} may alias and '{((IrLocalRef)written).Local.Name}' is written in {this._construct}; safe Rust cannot express this - provide a no-alias fact or select a copying target profile"));

      return new RsPath("_refused");
    }

    /// <summary>The bytes an endian access reads or writes: the width of its number.</summary>
    private static int Width(IrType type) => type.Kind switch {
      IrTypeKind.F32 => 4,
      IrTypeKind.F64 => 8,
      _ => type.Bits / 8
    };

    /// <summary>
    /// <c>u32::from_le_bytes(d[ply_range(i, 4, d.len())].try_into().expect("4 bytes"))</c>: a number
    /// from the bytes of a <c>u8[]</c>, the lowest or the highest first, the range checked by
    /// <c>ply_range</c>, which traps with an index out of range as the IR does.
    /// </summary>
    private RsExpression EndianLoad(IrIntrinsic load) {
      var width = Width(load.Type);
      var array = this.ArrayTarget(load.Arguments[0]);
      var range = this.Spelled(new RsCall(new RsPath(this.RangeHelper()), [this.Expression(load.Arguments[1]), new RsLiteral(width.ToString(CultureInfo.InvariantCulture)), new RsMethodCall(array, "len", [])]));
      var bytes = new RsMethodCall(new RsMethodCall(new RsIndex(array, range), "try_into", []), "expect", [new RsString($"{width} bytes")]);
      return new RsCall(new RsPath($"{TypeName(load.Type)}::{(load.Op == IrIntrinsicOp.LoadLittle ? "from_le_bytes" : "from_be_bytes")}"), [bytes]) { Type = load.Type };
    }

    /// <summary>
    /// <c>let at = ply_range(i, 4, d.len()); d[at].copy_from_slice(&amp;v.to_le_bytes());</c>: the value
    /// evaluated first, the range checked before any byte is written.
    /// </summary>
    private RsExpression EndianStore(IrIntrinsic store) {
      var statements = new List<RsStatement>();
      var type = store.Arguments[2].Type;
      var width = Width(type);
      var array = this.ArrayTarget(store.Arguments[0], true);
      var index = this.Expression(store.Arguments[1]);
      var value = this.Expression(store.Arguments[2]);
      if (store.Arguments[2] is not (IrLocalRef or IrConst)) {
        var bound = this.Temporary("value");
        statements.Add(new RsLet(bound, false, null, value));
        value = new RsPath(bound) { Type = type };
      }

      var at = this.Temporary("at");
      statements.Add(new RsLet(at, false, null, this.Spelled(new RsCall(new RsPath(this.RangeHelper()), [index, new RsLiteral(width.ToString(CultureInfo.InvariantCulture)), new RsMethodCall(array, "len", [])]))));
      var bytes = new RsMethodCall(this.Receiver(value), store.Op == IrIntrinsicOp.StoreLittle ? "to_le_bytes" : "to_be_bytes", []);
      statements.Add(new RsExpressionStatement(new RsMethodCall(new RsIndex(array, new RsPath(at)), "copy_from_slice", [new RsReference(false, bytes)])));
      return new RsBlockExpression(new RsBlock(statements, null));
    }

    /// <summary><c>Array.Fill</c> and <c>Array.Clear</c>: <c>fill</c> with the value or the zero.</summary>
    private RsExpression ArrayFill(IrIntrinsic fill) {
      var array = fill.Arguments[0];
      var value = fill.Op == IrIntrinsicOp.ArrayClear ? this.Zero(array.Type.Element!) : this.Expression(fill.Arguments[1]);
      return new RsMethodCall(this.ArrayTarget(array, true), "fill", [value]);
    }

    /// <summary>The arguments of a call: an array borrowed as the callee's parameter takes it, refused where one array is passed twice and written.</summary>
    private IReadOnlyList<RsExpression> Arguments(IrCall call) {
      if (!this._arrays.TryGetValue(call.Function, out var callee))
        return [.. call.Arguments.Select((argument, index) => argument.Type.IsObject ? this.ObjectArgument(call.Function, index, argument)
          : argument.Type.IsCollection ? this.CollectionPassed(argument, this._changedCollections.GetValueOrDefault(call.Function)?.Contains(index) == true)
          : this.Argument(argument))];

      var arguments = new List<RsExpression>();
      for (var index = 0; index < call.Arguments.Count; ++index) {
        var argument = call.Arguments[index];
        if (argument.Type.IsObject) {
          arguments.Add(this.ObjectArgument(call.Function, index, argument));
          continue;
        }

        if (argument.Type.IsView) {
          arguments.Add(this.ViewSource(argument, this.ViewParameterWritten(call.Function, index)));
          continue;
        }

        if (argument.Type.IsCollection) {
          arguments.Add(this.CollectionPassed(argument, this._changedCollections.GetValueOrDefault(call.Function)?.Contains(index) == true));
          continue;
        }

        if (!argument.Type.IsArray && !argument.Type.IsString) {
          arguments.Add(this.Argument(argument));
          continue;
        }

        if (argument.Type.IsString) {
          arguments.Add(this.StringArgument(argument));
          continue;
        }

        if (this._module.Functions.FirstOrDefault(function => function.Name == call.Function) is { } target && index < target.Parameters.Count
          && this._arrayNulls.MayBeNull(call.Function, target.Parameters[index])) {
          arguments.Add(this.NullableArray(argument, true));
          continue;
        }

        var mutable = callee.MutableParameters.Contains(index);
        var other = call.Arguments.Select((value, position) => (value, position))
          .FirstOrDefault(pair => pair.position != index && pair.value.Type.IsArray && (mutable || callee.MutableParameters.Contains(pair.position)) && this.Aliases(argument, pair.value));
        if (other.value is not null) {
          arguments.Add(mutable ? this.Alias(other.value, argument) : this.Alias(argument, other.value));
          continue;
        }

        arguments.Add(this.Borrow(argument, mutable));
      }

      return arguments;
    }

  }

}
