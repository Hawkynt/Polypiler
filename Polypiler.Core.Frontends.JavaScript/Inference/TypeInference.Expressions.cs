using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>How a binary operator is computed, which its operand types decide.</summary>
internal enum Plan {

  /// <summary>The operator is not defined on the operands, or an operand has no type.</summary>
  Invalid,

  /// <summary>A <c>BigInt</c> operator whose result nothing bounds.</summary>
  Unbounded,

  /// <summary>A bitwise operator or a shift: <c>ToInt32</c> of both operands, an <c>int32</c> result.</summary>
  Bitwise,

  /// <summary><c>&gt;&gt;&gt;</c>: <c>ToUint32</c> of the left operand, a <c>uint32</c> result.</summary>
  ShiftLogical,

  /// <summary><c>+</c> or <c>-</c> of two 32-bit integers under a coercion: the wrapping operation of the IR.</summary>
  WrapAdd,

  /// <summary><c>/</c> or <c>%</c> of two integers of one 32-bit type under a coercion: the division of the IR, which traps.</summary>
  IntegerDivide,

  /// <summary>Double arithmetic.</summary>
  Float,

  /// <summary>A comparison of two values of one integer type, or of two truth values.</summary>
  Compare,

  /// <summary>A comparison of two doubles.</summary>
  CompareFloat,

  /// <summary>A <c>BigInt</c> operator directly under <c>BigInt.asIntN(64, …)</c> or <c>asUintN</c>: computed modulo 2^64.</summary>
  Big,

  /// <summary><c>&gt;&gt;</c> of a bounded <c>BigInt</c> by a literal count, under a wrapper.</summary>
  BigShiftRight,

  /// <summary><c>/</c> or <c>%</c> of two bounded <c>BigInt</c>s of one type, under a wrapper.</summary>
  BigDivide,

  /// <summary><c>**</c> of two numbers: ECMA-262's exponentiation of doubles, which is <c>Math.pow</c>.</summary>
  Power,

  /// <summary>Two strings joined.</summary>
  Concat,

  /// <summary>Two strings compared by code unit.</summary>
  CompareString,

  /// <summary><c>===</c> or <c>!==</c> of references to objects of one class, or of one and <c>null</c>: identity.</summary>
  Identity

}

/// <summary>How an operator is computed and what it yields.</summary>
/// <param name="Plan">How.</param>
/// <param name="Result">The type of the value.</param>
/// <param name="Operand">The IR type the operands are computed in, where the plan has one.</param>
internal readonly record struct Operation(Plan Plan, JsType Result, IrType? Operand = null);

internal sealed partial class TypeInference {

  /// <summary>
  /// How many <c>+</c> and <c>-</c> may stand inside one another under one coercion and still be
  /// computed modulo 2^32: the exact value of such a tree of 32-bit integers stays below 2^53, so
  /// that JavaScript's doubles compute it exactly and the coercion of it is the wrapped sum.
  /// </summary>
  public const int ExactSumDepth = 20;

  private static readonly JsType _i64 = JsType.Of(IrType.I64);
  private static readonly JsType _u64 = JsType.Of(IrType.U64);

  /// <summary>The typed arrays and the type of their elements.</summary>
  public static IReadOnlyDictionary<string, IrType> TypedArrays { get; } = new Dictionary<string, IrType> {
    ["Int8Array"] = IrType.I8,
    ["Uint8Array"] = IrType.U8,
    ["Int16Array"] = IrType.I16,
    ["Uint16Array"] = IrType.U16,
    ["Int32Array"] = IrType.I32,
    ["Uint32Array"] = IrType.U32,
    ["Float32Array"] = IrType.F32,
    ["Float64Array"] = IrType.F64,
    ["BigInt64Array"] = IrType.I64,
    ["BigUint64Array"] = IrType.U64
  };

  /// <summary>
  /// The type of an expression where the given coercion is applied to it. Under a coercion only the
  /// value modulo 2^32 or 2^64 matters, which is what lets <c>+</c> and <c>-</c> be integer operations.
  /// </summary>
  public JsType Of(Node node, Coercion coercion = Coercion.None) {
    // A copy of a function shares its nodes with the function, so a type is known per function.
    // A value read from a place of stated integers is the integer it is: only the place takes whole doubles (#1416).
    if (!this._known.TryGetValue((this._scope, node, coercion), out var type))
      this._known[(this._scope, node, coercion)] = type = this.Compute(node, coercion).Read;

    return type;
  }

  private JsType Compute(Node node, Coercion coercion) {
    if (this.StringType(node) is { } text)
      return text;
    if (this.NumberTextType(node) is { } converted)
      return converted;

    if (this.KeyedType(node) is { } keyed)
      return keyed;

    if (this.Inherited(node) is { } inherited)
      return inherited;

    switch (node) {
      case NumericLiteral literal:
        return JsType.Literal(literal.Value);
      case BigIntLiteral literal:
        return JsType.BigLiteral(literal.Value);
      case BooleanLiteral:
        return JsType.Boolean;
      case Identifier identifier:
        return this._scope!.Bindings.GetValueOrDefault(identifier) switch {
          Variable variable => this._narrowed.GetValueOrDefault(variable) ?? variable.Type,
          ModuleConstant constant => constant.Type,
          _ => identifier.Name == "undefined" ? JsType.UndefinedValue
            : this._scope.FunctionValues.TryGetValue(identifier, out var function) ? JsType.Function(function.Shape) : JsType.Unknown
        };
      case ChainExpression chain:
        // a?.b is undefined where a is null or undefined.
        var chained = this.Of(chain.Expression);
        return chained.Kind is JsKind.Unknown or JsKind.Conflict ? chained : JsType.Join(chained, JsType.UndefinedValue);
      case UpdateExpression step:
        var stepped = this.Of(step.Argument);
        return step.Argument is not Identifier ? stepped : stepped.Kind is JsKind.Number or JsKind.BigInt ? Stepped(stepped) : JsType.Unknown;
      case UnaryExpression unary:
        return this.Unary(unary, coercion);
      case LogicalExpression logical:
        if (logical.Operator == Operator.NullishCoalescing) {
          // The left value where it is present, the right one where it is absent.
          var present = this.Of(logical.Left);
          if (present.Kind == JsKind.Null)
            return this.Of(logical.Right);

          if (!present.IsAbsent && present.Kind is not (JsKind.Object or JsKind.Array))
            return present;

          var coalesced = JsType.Join(present.Present, this.Of(logical.Right));
          return coalesced.Kind == JsKind.Conflict ? JsType.Unknown : coalesced;
        }

        // Values of two kinds are no value of the IR: the expression has no type, and the lowering says why.
        // a || b is a only where a counts as true, which an absent value never does (#1370).
        if (this.Leftmost(logical) is { } leftmost)
          return leftmost;

        var left = this.Of(logical.Left);
        var joined = JsType.Join(logical.Operator == Operator.LogicalOr && left.Kind != JsKind.Null ? left.Present : left, this.Of(logical.Right));
        return joined.Kind == JsKind.Conflict ? JsType.Unknown : joined;
      case BinaryExpression binary:
        return this.Decide(binary.Operator, binary.Left, binary.Right, coercion).Result;
      case ConditionalExpression choice:
        // The branch that reads what the test proves present reads it present (#1409).
        var proven = this.PresentBranch(choice);
        JsType Branch(Node branch) => branch == proven ? this.Of(branch).Present : this.Of(branch);
        var either = this.Narrowed(choice, Branch);
        return either.Kind == JsKind.Conflict ? JsType.Unknown : either;
      case ThisExpression:
        return this._scope!.Candidate.ThisClass is { } owner ? JsType.Object(owner) : JsType.Unknown;
      case ArrowFunctionExpression or FunctionExpression or FunctionDeclaration when this._scope!.Nested.TryGetValue(node, out var nested):
        return JsType.Function(nested.Shape);
      case NullLiteral:
        return JsType.Null;
      case NewExpression creation when this.Names.ClassNamed(creation.Callee) is { } created:
        return JsType.Object(created);
      case MemberExpression member when this._scope!.Bindings.GetValueOrDefault(member) is Variable field:
        return field.Type;
      case MemberExpression held when this._scope!.FunctionValues.TryGetValue(held, out var property):
        return JsType.Function(property.Shape);
      case MemberExpression { Computed: false } member when this.Of(member.Object).Class is { } holder:
        return this.MemberType(holder, member);
      case MemberExpression member when this.Names.Library(member) is { } library:
        // A constant of the library is typed as the literal of its value would be.
        return JavaScriptFloatApi.FindConstant(library) is { } value ? JsType.Literal(value.Value) : JsType.Unknown;
      case MemberExpression { Computed: false, Object: Identifier enumerated, Property: Identifier { Name: var label } } when this._scope!.Bindings.GetValueOrDefault(enumerated) is EnumObject named:
        // A member of a frozen object of records is an object of its class (#1371).
        if (named.Record is { Frozen: { } members } record)
          return members.ContainsKey(label) ? JsType.Object(record) : JsType.Unknown;

        // A member of an enum object is the literal of its value, which stays the enum's while only members join it.
        return named.Type?.Member(label) is { } found ? JsType.Member(named.Type, found.Value) : JsType.Unknown;
      case MemberExpression { Computed: false, Property: Identifier { Name: var key } } member when this.Of(member.Object).Shape is { } shape:
        return shape.TypeOf(key) ?? JsType.Unknown;
      // o[key] of the key of a loop over Object.keys(o) is any of its fields: the type every one of them has (#1373).
      case MemberExpression { Computed: true, Property: Identifier } keyedMember when this._scope!.Bindings.GetValueOrDefault(keyedMember.Property) is Variable { KeysOf: not null }:
        var keyedType = this.Of(keyedMember.Object);
        var fields = KeyLoop.KeysOf(keyedType)?.Select(key => keyedType.Class?.Field(key)?.Variable.Type ?? keyedType.Shape?.TypeOf(key) ?? JsType.Unknown).ToList() ?? [];
        var any = fields.Count == 0 ? JsType.Unknown : fields.Aggregate(JsType.Join);
        return any.Kind == JsKind.Conflict ? JsType.Unknown : any;
      case MemberExpression member:
        var array = this.Of(member.Object);
        if (array.Kind != JsKind.Array)
          return JsType.Unknown;

        if (!member.Computed)
          return member.Property is Identifier { Name: "length" } ? JsType.I32 : JsType.Unknown;

        // A binary32 element is read as the double it is.
        return array.Element!.Float == 32 ? JsType.F64 : array.Element;
      case CallExpression call:
        return this.Call(call);
      case AwaitExpression awaited:
        return this.AwaitedType(awaited);
      case NewExpression creation:
        return creation.Callee is Identifier { Name: var name } constructor && !this._scope!.Bindings.ContainsKey(constructor)
          ? TypedArrays.TryGetValue(name, out var element) ? JsType.ArrayOf(JsType.Of(element), false)
            : name == "Array" ? JsType.ArrayOf(JsType.Unknown, true)
            : JsType.Unknown
          : JsType.Unknown;
      case ArrayExpression literal:
        // A spread array adds its elements (#1328).
        return JsType.ArrayOf(literal.Elements.Aggregate(JsType.Unknown, (type, element) => element switch {
          null => type,
          SpreadElement spread => this.Of(spread.Argument) is { Kind: JsKind.Array, Element: { } spreadElement } ? JsType.Join(type, spreadElement) : type,
          _ => JsType.Join(type, this.Of(element))
        }), true);
      case ObjectExpression literal:
        return this.Record(literal);
      default:
        return JsType.Unknown;
    }
  }

  private JsType Unary(UnaryExpression unary, Coercion coercion) {
    switch (unary.Operator) {
      case Operator.LogicalNot:
        return JsType.Boolean;
      case Operator.Void:
        return JsType.UndefinedValue;
      case Operator.TypeOf:
        return JsType.String;
      case Operator.BitwiseNot:
        return this.IsBig(unary.Argument, coercion) ? Wide(coercion) ?? JsType.UnboundedBigInt : JsType.I32;
      case Operator.UnaryNegation when unary.Argument is NumericLiteral literal:
        return JsType.Literal(-literal.Value);
      case Operator.UnaryNegation when unary.Argument is BigIntLiteral literal:
        return JsType.BigLiteral(-literal.Value);
      case Operator.UnaryNegation:
        if (this.IsBig(unary.Argument, coercion))
          return Wide(coercion) ?? JsType.UnboundedBigInt;

        if (Word(coercion) is { } word && Depth(unary) <= ExactSumDepth && this.Of(unary.Argument, Modulo(coercion)).IsWord)
          return word;

        return this.Of(unary.Argument).Kind == JsKind.Number ? JsType.F64 : JsType.Unknown;
      case Operator.UnaryPlus:
        var operand = this.Of(unary.Argument);
        return operand.Kind == JsKind.Number ? operand : JsType.Unknown;
      default:
        return JsType.Unknown;
    }
  }

  private JsType Call(CallExpression call) {
    if (this.CallbackType(call) is { } callback)
      return callback;

    if (this.ListCall(call) is { } listed)
      return listed;

    var builtin = this.Names.Builtin(call);
    if (this.IntegerCall(call, builtin) is { } integer)
      return integer;

    switch (builtin) {
      case "Math.imul" or "Math.clz32":
        return JsType.I32;
      case "Array.isArray":
        return JsType.Boolean;
      case "Math.fround":
        return JsType.F32;
      // Math.min, max and abs of whole doubles and integers are whole doubles (#1416).
      case "Math.min" or "Math.max" or "Math.abs" when this.WholeCall(builtin, call) is { } whole:
        return whole;
      // A number rounded to an integer is a whole double of no bound, which % of a literal bounds (#1413).
      case "Math.trunc" or "Math.floor" or "Math.ceil" or "Math.round" when call.Arguments is [var rounded] && this.Of(rounded).Kind == JsKind.Number:
        return this.Quotient(rounded) ?? JsType.Whole(System.Int128.MinValue, System.Int128.MaxValue);
      case "Math.trunc" or "Math.floor" or "Math.ceil" or "Math.round" or "Math.abs" or "Math.sign" or "Math.sqrt" or "Math.min" or "Math.max":
        return JsType.F64;
      case "BigInt.asUintN":
        return _u64;
      case "BigInt.asIntN" or "BigInt":
        return _i64;
      case { } library when JavaScriptFloatApi.Find(library) is { } member:
        return JsType.Of(member.Result);
      // A view of a typed array or of a view, and the copy of a part of one (#1325).
      case null when call.Callee is MemberExpression { Computed: false, Property: Identifier { Name: "subarray" or "slice" } method } member
        && this.Of(member.Object) is { Kind: JsKind.Array, IsPlain: false } source:
        // The copy of a stated T[] nothing has decided yet is as undecided as it is (#1374).
        return method.Name == "subarray" ? JsType.ViewOf(source.Element!) : source.IsStated ? JsType.ArrayOf(source.Element!, false).AsStated() : JsType.ArrayOf(source.Element!, false);
      // fill yields the array it fills: an Array whose length never changes holds the value as well.
      case null when call.Callee is MemberExpression { Computed: false, Property: Identifier { Name: "fill" } } member
        && this.Of(member.Object) is { Kind: JsKind.Array, IsGrowable: false, IsView: false } filled && call.Arguments.Count > 0:
        return filled.IsPlain ? JsType.ArrayOf(JsType.Join(filled.Element!, this.Of(call.Arguments[0])), true) : filled;
      // The keys of an object, which only its shape gives (#1373).
      case null when KeyLoop.Keys(call) is not null:
        return JsType.ArrayOf(JsType.String, true);
      // The copy of an Array whose length never changes, and the Array of two or more of them joined.
      case null when call.Callee is MemberExpression { Computed: false, Property: Identifier { Name: "slice" } } member
        && this.Of(member.Object) is { Kind: JsKind.Array, IsPlain: true, IsGrowable: false, IsView: false } copied:
        return JsType.ArrayOf(copied.Element!, true);
      case null when call.Callee is MemberExpression { Computed: false, Property: Identifier { Name: "concat" } } member
        && this.Of(member.Object) is { Kind: JsKind.Array, IsPlain: true, IsGrowable: false, IsView: false } first:
        return call.Arguments.Select(argument => this.Of(argument)).Aggregate(first, (joined, next) => joined.Kind == JsKind.Array && next is { Kind: JsKind.Array, IsPlain: true, IsGrowable: false, IsView: false }
          ? JsType.ArrayOf(JsType.Join(joined.Element!, next.Element!), true) : JsType.Unknown);
      // A field of the object is the function value it holds, which hides a method of the same key (#1408).
      case null when call.Callee is MemberExpression { Computed: false } invoked && this.Of(invoked.Object).Class is { } owner && (Binder.KeyOf(invoked) is not { } field || owner.Field(field) is null):
        return Binder.KeyOf(invoked) is { } key && owner.Member(MemberKind.Method, key) is { } called ? this.ResultOf(this.ModelOf(called)) : JsType.Unknown;
      case null when this.Names.Callee(call.Callee) is { } callee:
        var model = this.ModelOf(callee);
        return this.ResultOf(model);
      case null when this.Of(call.Callee) is { Kind: JsKind.Function } function:
        return function.Callable!.Members.Select(member => this.ResultOf(this.ModelOf(member))).Aggregate(JsType.Join);
      default:
        return JsType.Unknown;
    }
  }

  /// <summary>The 32-bit integer a coercion yields, or <see langword="null"/>.</summary>
  public static JsType? Word(Coercion coercion) => coercion switch {
    Coercion.Int32 or Coercion.Modulo32 => JsType.I32,
    Coercion.Uint32 or Coercion.ModuloU32 => JsType.U32,
    _ => null
  };

  /// <summary>The coercion the operands of a wrapped sum stand under: only their value modulo 2^32 matters, but nothing converts them.</summary>
  public static Coercion Modulo(Coercion coercion) => coercion is Coercion.Int32 or Coercion.Modulo32 ? Coercion.Modulo32 : Coercion.ModuloU32;

  /// <summary>The 64-bit integer a wrapper yields, or <see langword="null"/>.</summary>
  public static JsType? Wide(Coercion coercion) => coercion switch {
    Coercion.Int64 => _i64,
    Coercion.Uint64 => _u64,
    _ => null
  };

  /// <summary>Whether an operand makes its operator a <c>BigInt</c> operator: it is one, or the operator stands under a wrapper.</summary>
  private bool IsBig(Node operand, Coercion coercion) => Wide(coercion) is not null || this.Of(operand).Kind == JsKind.BigInt;

  /// <summary>How many <c>+</c>, <c>-</c> and negations stand inside one another at an expression.</summary>
  private static int Depth(Node node) => node switch {
    BinaryExpression { Operator: Operator.Addition or Operator.Subtraction } sum => 1 + System.Math.Max(Depth(sum.Left), Depth(sum.Right)),
    UnaryExpression { Operator: Operator.UnaryNegation } negation => 1 + Depth(negation.Argument),
    _ => 0
  };

  /// <summary>The 32-bit type that holds the values of both integers, or <see langword="null"/>.</summary>
  private static IrType? Unify(JsType left, JsType right, IrType signed, IrType unsigned) {
    var kind = signed.IsLong ? JsKind.BigInt : JsKind.Number;
    if (left.Kind != kind || right.Kind != kind || left.Float != 0 || right.Float != 0 || left.IsUnbounded || right.IsUnbounded)
      return null;

    return left.Within(signed) && right.Within(signed) ? signed : left.Within(unsigned) && right.Within(unsigned) ? unsigned : null;
  }

  /// <summary>Decides how a binary operator is computed on two operands where a coercion is applied to its value.</summary>
  public Operation Decide(Operator op, Node left, Node right, Coercion coercion) {
    if (this.DecideString(op, left, right) is { } text)
      return text;

    if (this.IsBig(left, coercion) || this.Of(right).Kind == JsKind.BigInt)
      return this.DecideBig(op, left, right, coercion);

    switch (op) {
      case Operator.BitwiseOr or Operator.BitwiseXor or Operator.BitwiseAnd or Operator.LeftShift or Operator.RightShift:
        var first = this.Of(left, Coercion.Int32);
        var second = this.Of(right, Coercion.Int32);
        return IsNumber(first) && IsNumber(second) ? new(Plan.Bitwise, Masked(op, first, second), IrType.I32) : new(Plan.Invalid, JsType.I32);
      case Operator.UnsignedRightShift:
        return IsNumber(this.Of(left, Coercion.Uint32)) && IsNumber(this.Of(right, Coercion.Int32)) ? new(Plan.ShiftLogical, JsType.U32, IrType.U32) : new(Plan.Invalid, JsType.U32);
      case Operator.Addition or Operator.Subtraction when Word(coercion) is { } word:
        if (System.Math.Max(Depth(left), Depth(right)) < ExactSumDepth && this.Of(left, Modulo(coercion)).IsWord && this.Of(right, Modulo(coercion)).IsWord)
          return new(Plan.WrapAdd, word, word.Ir);

        break;
      case Operator.Division or Operator.Remainder when coercion is Coercion.Int32 or Coercion.Uint32:
        if (Unify(this.Of(left), this.Of(right), IrType.I32, IrType.U32) is { } unified)
          return new(Plan.IntegerDivide, Word(coercion)!, unified);

        break;
    }

    var (a, b) = (this.Of(left), this.Of(right));
    switch (op) {
      // The remainder of two integers that are never negative is an integer below the divisor, of the type that holds both (#1375).
      case Operator.Remainder when a.Low >= 0 && b.Low >= 0 && Unify(a, b, IrType.I32, IrType.U32) is { } unsigned:
        return new(Plan.IntegerDivide, JsType.Of(unsigned), unsigned);
      case Operator.Addition or Operator.Subtraction or Operator.Multiplication or Operator.Division or Operator.Remainder:
        return a.Kind == JsKind.Number && b.Kind == JsKind.Number ? new(Plan.Float, WholeResult(op, a, b, right) ?? JsType.F64, IrType.F64) : new(Plan.Invalid, JsType.Unknown);
      case Operator.Exponentiation:
        return a.Kind == JsKind.Number && b.Kind == JsKind.Number ? new(Plan.Power, JsType.F64, IrType.F64) : new(Plan.Invalid, JsType.Unknown);
      case Operator.StrictEquality or Operator.StrictInequality when a.Kind is JsKind.Object or JsKind.Null && b.Kind is JsKind.Object or JsKind.Null:
        return JsType.Join(a, b).Kind == JsKind.Conflict || a.Kind == b.Kind && a.Kind == JsKind.Null ? new(Plan.Invalid, JsType.Boolean) : new(Plan.Identity, JsType.Boolean);
      case Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality when a.Kind == JsKind.Boolean && b.Kind == JsKind.Boolean:
        return new(Plan.Compare, JsType.Boolean, IrType.Bool);
      // Two values of one enum - members, and literals that are members - are compared as the enum.
      case Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality when JsType.Join(a, b).Enum is { } named:
        return new(Plan.Compare, JsType.Boolean, named.Type);
      case Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality
        or Operator.LessThan or Operator.LessThanOrEqual or Operator.GreaterThan or Operator.GreaterThanOrEqual:
        if (Unify(a, b, IrType.I32, IrType.U32) is { } compared)
          return new(Plan.Compare, JsType.Boolean, compared);

        return a.Kind == JsKind.Number && b.Kind == JsKind.Number ? new(Plan.CompareFloat, JsType.Boolean, IrType.F64) : new(Plan.Invalid, JsType.Boolean);
      default:
        return new(Plan.Invalid, JsType.Unknown);
    }
  }

  private static bool IsNumber(JsType type) => type.Kind is JsKind.Number or JsKind.Unknown;

  /// <summary>
  /// The type of a bitwise result. <c>&amp;</c> with an operand that is never negative yields at most
  /// that operand; <c>|</c> and <c>^</c> of two 8- or 16-bit operands stay in 8 or 16 bits.
  /// </summary>
  private static JsType Masked(Operator op, JsType left, JsType right) {
    bool Small(JsType type, IrType holder) => type.IsInteger && type.Low >= 0 && type.Within(holder);
    switch (op) {
      case Operator.BitwiseAnd:
        foreach (var holder in new[] { IrType.U8, IrType.U16 })
          if (Small(left, holder) || Small(right, holder))
            return JsType.Of(holder);

        break;
      case Operator.BitwiseOr or Operator.BitwiseXor:
        foreach (var holder in new[] { IrType.U8, IrType.U16 })
          if (Small(left, holder) && Small(right, holder))
            return JsType.Of(holder);

        break;
    }

    return JsType.I32;
  }

  /// <summary>
  /// A <c>BigInt</c> operator. Directly under <c>BigInt.asIntN(64, …)</c> or <c>asUintN</c> the
  /// operators that are the same modulo 2^64 - <c>+ - * &amp; | ^</c> and <c>&lt;&lt;</c> by a literal -
  /// are 64-bit operations; <c>&gt;&gt;</c>, <c>/</c> and <c>%</c> need the values of their operands,
  /// which must be bounded themselves. Anywhere else the result has no bound.
  /// </summary>
  private Operation DecideBig(Operator op, Node left, Node right, Coercion coercion) {
    var wide = Wide(coercion);
    var count = right is BigIntLiteral { Value: var places } && places >= 0 && places <= 63;

    // Under a wrapper a BigInt and a number do not mix: JavaScript throws, and there is nothing to
    // compute. Without one the result has no bound whatever the operands are.
    var mixed = this.Of(left).Kind is not (JsKind.BigInt or JsKind.Unknown) || this.Of(right).Kind is not (JsKind.BigInt or JsKind.Unknown);
    if (mixed && (wide is not null || op is not (Operator.Addition or Operator.Subtraction or Operator.Multiplication or Operator.Division or Operator.Remainder
        or Operator.BitwiseAnd or Operator.BitwiseOr or Operator.BitwiseXor or Operator.LeftShift or Operator.RightShift)))
      return new(Plan.Invalid, JsType.Unknown);

    switch (op) {
      case Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality
        or Operator.LessThan or Operator.LessThanOrEqual or Operator.GreaterThan or Operator.GreaterThanOrEqual:
        return Unify(this.Of(left), this.Of(right), IrType.I64, IrType.U64) is { } compared ? new(Plan.Compare, JsType.Boolean, compared) : new(Plan.Invalid, JsType.Boolean);
      case Operator.Addition or Operator.Subtraction or Operator.Multiplication or Operator.BitwiseAnd or Operator.BitwiseOr or Operator.BitwiseXor:
        return wide is null ? new(Plan.Unbounded, JsType.UnboundedBigInt) : new(Plan.Big, wide, wide.Ir);
      case Operator.LeftShift or Operator.RightShift when wide is null:
        return new(Plan.Unbounded, JsType.UnboundedBigInt);
      case Operator.LeftShift when count:
        return new(Plan.Big, wide!, wide!.Ir);
      case Operator.RightShift when count:
        return this.Of(left) is { Kind: JsKind.BigInt, Ir: { } shifted } ? new(Plan.BigShiftRight, wide!, shifted) : new(Plan.Invalid, wide!);
      case Operator.Division or Operator.Remainder:
        return wide is null ? new(Plan.Unbounded, JsType.UnboundedBigInt)
          : Unify(this.Of(left), this.Of(right), IrType.I64, IrType.U64) is { } divided ? new(Plan.BigDivide, wide, divided)
          : new(Plan.Invalid, wide);
      default:
        return new(Plan.Invalid, JsType.Unknown);
    }
  }

}
