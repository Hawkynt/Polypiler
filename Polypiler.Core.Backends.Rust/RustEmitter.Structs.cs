using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary>The array a place of fields is an element of, or <see langword="null"/>.</summary>
  internal static IrExpr? ElementOf(IrExpr place) => Writer.ElementArray(place);

  private sealed partial class Writer {

    /// <summary>The Rust name of every type the module declares.</summary>
    private readonly Dictionary<IrTypeDeclaration, string> _types = new(ReferenceEqualityComparer.Instance);

    /// <summary>The locals and parameters whose fields the function being written writes: <c>let mut</c>.</summary>
    private HashSet<IrLocal> _fieldWritten = [];

    /// <summary>Names the declared types: each by the last part of its name in upper camel case, made unique.</summary>
    private void NameTypes(IrModule module) {
      var names = module.Types.Select(type => type.Name).ToList();
      var taken = new HashSet<string>(StringComparer.Ordinal);
      foreach (var (type, rust) in module.Types.Zip(RustNames.Unique(names, name => RustNames.UpperCamel(name[(name.LastIndexOfAny(['.', '+']) + 1)..], this.Edition), taken)))
        this._types[type] = rust;
    }

    /// <summary>
    /// The items of a struct: <c>pub struct Name { pub field: T }</c> with the derives that hold - <c>Clone</c>,
    /// <c>Copy</c> and <c>Debug</c> always, since every field is a value; <c>Default</c> where every
    /// field has a zero; <c>PartialEq</c> and <c>Eq</c> where no field is a floating-point number,
    /// otherwise an <c>impl PartialEq</c> of the IR's equality, under which a NaN equals a NaN and
    /// the two zeros are equal. A field of any other type is refused by name.
    /// </summary>
    private IEnumerable<RsItem> Struct(IrStructType type) {
      this._construct = $"the struct '{type.Name}'";
      var name = this._types[type];
      foreach (var field in type.Fields.Where(field => !IsValue(field.Type)))
        this.Refuse($"a struct field of the type {field.Type}");

      var fields = type.Fields.Select(field => new RsFieldDeclaration(this.FieldName(field), field.Visibility == IrVisibility.Private ? "" : "pub", this.Type(field.Type))).ToList();
      var floats = HasFloat(type);
      var derives = new List<string> { "Clone", "Copy", "Debug" };
      if (type.HasDefault)
        derives.Add("Default");
      if (!floats)
        derives.AddRange(["PartialEq", "Eq"]);

      yield return new RsStruct(name, "pub", fields) { Attributes = [$"derive({string.Join(", ", derives)})"] };
      if (!floats)
        yield break;

      // The IR's equality: field by field, a floating-point number as double.Equals compares it.
      RsExpression Equal(IrField field) {
        var mine = new RsField(new RsPath("self"), this.FieldName(field)) { Type = field.Type };
        var theirs = new RsField(new RsPath("other"), this.FieldName(field)) { Type = field.Type };
        if (!field.Type.IsFloat)
          return new RsBinary(mine, "==", theirs) { Type = IrType.Bool };

        var nan = new RsBinary(new RsMethodCall(mine, "is_nan", []), "&&", new RsMethodCall(theirs, "is_nan", [])) { Type = IrType.Bool };
        return new RsParenthesized(new RsBinary(new RsBinary(mine, "==", theirs), "||", new RsParenthesized(nan)) { Type = IrType.Bool });
      }

      var test = type.Fields.Count == 0 ? new RsLiteral("true") : type.Fields.Select(Equal).Aggregate((left, right) => new RsBinary(left, "&&", right) { Type = IrType.Bool });
      if (type.Fields.Count == 1 && test is RsParenthesized { Inner: var only })
        test = only;

      yield return new RsImpl("PartialEq", name, [new RsFunction("eq", "", [new RsParameter("&self", ""), new RsParameter("other", "&Self")], "bool", new RsBlock([], test))]);
    }

    /// <summary>Whether a value of a type is copied as a whole: a number, a truth value, or a struct of them.</summary>
    private static bool IsValue(IrType type) => TypeName(type) is not null || type.StructType is { } nested && nested.Fields.All(field => IsValue(field.Type));

    /// <summary>Whether a struct has a floating-point number among its fields or theirs.</summary>
    private static bool HasFloat(IrStructType type) => type.Fields.Any(field => field.Type.IsFloat || field.Type.StructType is { } nested && HasFloat(nested));

    /// <summary>The Rust name of a field.</summary>
    private string FieldName(IrField field) => RustNames.Snake(field.Name.Trim('_'), this.Edition);

    /// <summary><c>Name { a: x, b }</c>, the fields in declaration order, which is the order the IR evaluates them in.</summary>
    private RsExpression StructNew(IrStructNew creation) {
      var type = creation.Type.StructType!;
      var fields = type.Fields.Zip(creation.Arguments, (field, value) => new RsFieldValue(this.FieldName(field), this.Expression(value))).ToList();
      return new RsStructLiteral(this._types[type], fields) { Type = creation.Type };
    }

    /// <summary>The place a field of which is read or written: a variable, an array element or a field of a place.</summary>
    private RsExpression Place(IrExpr target, bool write) => target switch {
      IrArrayLoad load => new RsIndex(this.ArrayTarget(load.Array, write), this.Index(load.Index)) { Type = load.Type },
      IrFieldLoad load => new RsField(this.Place(load.Target, write), this.FieldName(load.Field)) { Type = load.Type },
      _ => this.Operand(this.Expression(target), 15)
    };

    /// <summary>The variable a place of fields and elements is rooted in, or <see langword="null"/>.</summary>
    private static IrLocal? Root(IrExpr place) => place switch {
      IrLocalRef reference => reference.Local,
      IrFieldLoad load => Root(load.Target),
      _ => null
    };

    /// <summary>The array a place of fields is an element of, or <see langword="null"/>.</summary>
    internal static IrExpr? ElementArray(IrExpr place) => place switch {
      IrArrayLoad load => load.Array,
      IrFieldLoad load => ElementArray(load.Target),
      _ => null
    };

    /// <summary>
    /// <c>place.field = value;</c>, a bitwise or floating-point update of the field itself as a compound
    /// assignment. The IR checks an element's index before it evaluates the value, Rust after: a place
    /// in an array is written only with a value that can neither trap nor change anything.
    /// </summary>
    private void FieldStore(IrFieldStore store, List<RsStatement> into) {
      if (ElementArray(store.Target) is not null && (!Settled(store.Value) || !SettledPlace(store.Target)))
        this.Refuse("a field of an array element written with a value that may trap or have an effect");

      var place = new RsField(this.Place(store.Target, true), this.FieldName(store.Field)) { Type = store.Field.Type };
      if (store.Value is IrBinary { Left: IrFieldLoad self } binary && self.Field == store.Field && SamePlace(self.Target, store.Target) && Compound(binary) is { } op) {
        into.Add(new RsExpressionStatement(new RsAssign(place, op + "=", this.Operand(this.Expression(binary.Right), 2))));
        return;
      }

      into.Add(new RsExpressionStatement(new RsAssign(place, "=", this.Expression(store.Value))));
    }

    /// <summary>A step of a field: as a statement the field stepped in place, as an expression a block that gives the old or the new value.</summary>
    private RsExpression FieldStep(IrFieldLoad load, IrStep step, bool statement) {
      if (ElementArray(load) is not null && !SettledPlace(load.Target))
        this.Refuse("a step of a field of an array element whose index may trap or have an effect");

      var place = new RsField(this.Place(load.Target, true), this.FieldName(load.Field)) { Type = load.Type };
      var statements = new List<RsStatement>();
      if (statement && load.Type.IsFloat) {
        statements.Add(new RsExpressionStatement(new RsAssign(place, step.Increment ? "+=" : "-=", Literal(new IrConst(load.Type, IrOperators.Bits(load.Type, 1.0))))));
        return new RsBlockExpression(new RsBlock(statements, null));
      }

      if (statement) {
        statements.Add(new RsExpressionStatement(new RsAssign(place, "=", this.Stepped(place, step))));
        return new RsBlockExpression(new RsBlock(statements, null));
      }

      var old = this.Temporary("old");
      statements.Add(new RsLet(old, false, null, place));
      var oldValue = new RsPath(old) { Type = load.Type };
      statements.Add(new RsExpressionStatement(new RsAssign(place, "=", this.Stepped(oldValue, step))));
      return new RsBlockExpression(new RsBlock(statements, step.Postfix ? oldValue : place)) { Type = load.Type };
    }

    /// <summary>Whether two places are the same: one variable, field or element of a constant or variable index.</summary>
    private static bool SamePlace(IrExpr first, IrExpr second) => (first, second) switch {
      (IrLocalRef a, IrLocalRef b) => a.Local == b.Local,
      (IrFieldLoad a, IrFieldLoad b) => a.Field == b.Field && SamePlace(a.Target, b.Target),
      (IrArrayLoad a, IrArrayLoad b) => SamePlace(a.Array, b.Array) && a.Index is IrLocalRef or IrConst && SamePlace(a.Index, b.Index),
      (IrConst a, IrConst b) => a == b,
      _ => false
    };

    /// <summary>
    /// Whether evaluating an expression can neither change anything nor trap but with an index out of
    /// range, so that it may move past the check of an index, whose trap is the same: constants,
    /// variables, fields and elements of them, struct values of such parts, and the operators and
    /// conversions that cannot fail.
    /// </summary>
    private static bool Settled(IrExpr expression) => IrTree.Nodes(expression).All(node => node switch {
      IrConst or IrLocalRef or IrFieldLoad or IrStructNew or IrStructDefault or IrConditional or IrArrayLoad { Array: IrLocalRef or IrConstRef } => true,
      IrConvert conversion => conversion.Contract != IrConversion.Trap,
      IrUnary unary => unary.Op is IrUnaryOp.LogicalNot or IrUnaryOp.BitNot || unary.Op == IrUnaryOp.Negate,
      IrBinary binary => binary.Op is not (IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked)
        && (binary.Type.IsFloat || binary.Op is not (IrBinaryOp.Divide or IrBinaryOp.Remainder)),
      _ => false
    });

    /// <summary>Whether naming a place can change nothing and trap only at its own checks: a variable, its fields, elements of an array variable at settled indices.</summary>
    private static bool SettledPlace(IrExpr place) => place switch {
      IrLocalRef => true,
      IrFieldLoad load => SettledPlace(load.Target),
      IrArrayLoad load => load.Array is IrLocalRef && Settled(load.Index),
      _ => false
    };

    /// <summary>The locals and parameters a function writes a field of, directly or through a field of a field.</summary>
    private static HashSet<IrLocal> FieldWrites(IrFunction function) {
      var written = new HashSet<IrLocal>();
      foreach (var statement in Polypiler.Core.Backends.Rust.Emit.ArrayOwnership.Statements(function.Body)) {
        if (statement is IrFieldStore store && Root(store.Target) is { } local)
          written.Add(local);
        foreach (var node in IrTree.Expressions(statement).SelectMany(IrTree.Nodes))
          if (node is IrStep { Target: IrFieldLoad load } && Root(load.Target) is { } stepped)
            written.Add(stepped);
      }

      return written;
    }

  }

}
