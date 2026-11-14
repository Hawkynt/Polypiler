using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// Object literals as value records. A literal of names and values that are numbers, truth values
/// or such literals is a struct, as long as nothing in the file can tell a value from a reference:
/// the object is never written after it is built, never given a new property, never compared by
/// identity, and never handed to anything but a function of the file. Every construct that would
/// break one of those conditions is refused by name, so what is imported is exactly what a copy
/// would compute.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>An object literal as the value of a struct type: its values in the order of its keys, which is the order of the fields.</summary>
  private IrExpr ObjectLiteral(ObjectExpression literal, IrType type) {
    var values = new List<(string Key, Node Value)>();
    foreach (var property in literal.Properties) {
      if (TypeInference.Plain(property) is not { } entry)
        throw this.Unsupported(property, Describe(property, "an object literal"));

      if (values.Exists(value => value.Key == entry.Key))
        throw this.Unsupported(property, $"the key {entry.Key} twice in an object literal");

      var kind = types.Of(entry.Value);
      if (kind.Kind is not (JsKind.Number or JsKind.Boolean or JsKind.Record or JsKind.Unknown))
        throw this.Unsupported(entry.Value, $"an object literal with a {kind.Describe()} value");

      values.Add(entry);
    }

    var fields = type.StructType!.Fields;
    return new IrStructNew(type, [.. fields.Select((field, index) => this.Value(values[index].Value, field.Type))]);
  }

  /// <summary>What a property of an object literal or of a pattern is that the subset does not take.</summary>
  private static string Describe(Node property, string where) => property switch {
    SpreadElement or RestElement => $"a spread in {where} (...)",
    Property { Computed: true } => $"a computed key in {where}",
    Property { Kind: PropertyKind.Get } => $"a getter in {where}",
    Property { Kind: PropertyKind.Set } => $"a setter in {where}",
    Property { Method: true } => $"a method in {where}",
    Property { Key: not Identifier } => $"a key that is no name in {where}",
    _ => $"a default value or a nested pattern in {where}"
  };

  /// <summary>A key of a record read: the field of the struct.</summary>
  private IrExpr FieldOf(MemberExpression member, RecordShape shape) {
    var target = this.Expression(member.Object);
    var key = ((Identifier)member.Property).Name;
    return target.Type.StructType!.Field(key) is { } field
      ? new IrFieldLoad(target, field)
      : throw this.Unsupported(member, $"the property {key}, which {shape} does not have");
  }

  /// <summary>
  /// A write to a property of a record, which would make the object a value that can change - and
  /// then two names of it could be told from two copies.
  /// </summary>
  private Refusal Written(MemberExpression member, RecordShape shape, Node node)
    => this.Unsupported(node, shape.TypeOf(((Identifier)member.Property).Name) is null
      ? $"a new property of an object literal ({types.Text(member)})"
      : $"an object literal written after it is built ({types.Text(member)})");

  /// <summary>
  /// <c>const { x, y } = p</c>: every name a local with the value of its key. The record is
  /// evaluated once - into a temporary unless it is a variable already - and every key is read from it.
  /// </summary>
  private void Destructure(VariableDeclarator declarator, ObjectPattern pattern, List<IrStmt> into) {
    var parts = new List<(string Key, Identifier Name)>();
    foreach (var property in pattern.Properties) {
      if (TypeInference.Plain(property) is not { Value: Identifier name } entry)
        throw this.Unsupported(property, Describe(property, "a destructuring declaration"));

      parts.Add((entry.Key, name));
    }

    var record = this.Evaluate(declarator.Init!);
    if (record.Type.Shape is not { } shape)
      throw this.Unsupported(declarator.Init!, $"a destructuring of {record.Type.Describe()}");

    var source = this.Once(record.Expr, into);
    foreach (var (key, name) in parts) {
      if (name.Name.Contains('$'))
        throw this.Unsupported(name, $"the name {name.Name}");

      if (this._model.Refused.TryGetValue(name, out var refused))
        throw this.Unsupported(name, refused);

      var field = source.Type.StructType!.Field(key) ?? throw this.Unsupported(name, $"the property {key}, which {shape} does not have");
      var variable = (Variable)this._model.Bindings[name];
      var local = new IrLocal(name.Name, this.Storable(variable.Type, name));
      variable.Local = local;
      this._locals.Add(local);
      into.Add(new IrDeclare(local, this.To(new Typed(new IrFieldLoad(source, field), shape.TypeOf(key)!), local.Type, name)));
    }
  }

  /// <summary>
  /// A record as the value of a struct of the same keys whose values have other types - a literal
  /// that holds an integer where the place holds a double: a new value of the place's struct, each
  /// value converted as a number is. The record is read once per key, so it has to be a new value
  /// or a variable.
  /// </summary>
  private IrExpr? Reshaped(Typed value, IrType type, Node node) {
    if (value.Type.Shape is not { } shape || type.StructType is not { } target || !target.Fields.Select(field => field.Name).SequenceEqual(shape.Keys))
      return null;

    var from = value.Expr.Type.StructType!;
    IrExpr Read(int index) => value.Expr is IrStructNew creation ? creation.Arguments[index] : new IrFieldLoad(value.Expr, from.Fields[index]);
    if (value.Expr is not (IrStructNew or IrLocalRef))
      return null;

    return new IrStructNew(type, [.. target.Fields.Select((field, index) => this.To(new Typed(Read(index), shape.Types[index]), field.Type, node))]);
  }

  /// <summary>The structs the functions of a module use, in the order they were made: every type of a variable, a result or a node, and the fields of those.</summary>
  public IReadOnlyList<IrTypeDeclaration> Structs(IEnumerable<IrFunction> functions, Shapes shapes) {
    var used = new HashSet<IrStructType>(ReferenceEqualityComparer.Instance);
    void Use(IrType type) {
      if ((type.Element ?? type).StructType is { } declared && used.Add(declared))
        foreach (var field in declared.Fields)
          Use(field.Type);
    }

    foreach (var function in functions) {
      Use(function.ReturnType);
      foreach (var local in function.Parameters.Concat(function.Locals))
        Use(local.Type);

      foreach (var expression in IrTree.Nodes(function.Body))
        Use(expression.Type);
    }

    return [.. shapes.Declared.Where(used.Contains)];
  }

}
