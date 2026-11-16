using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>
/// An enum of JavaScript: a <c>const</c> of the scope that encloses the functions whose value is
/// <c>Object.freeze({ A: 0, B: 5 })</c>, each member named and a distinct 32-bit integer. It is an
/// <see cref="IrEnumType"/> over <c>i32</c>, and the module may use it only as <c>X.A</c>; an object of
/// numbers that is not one has a problem instead, which every use reports.
/// </summary>
/// <param name="name">The name of the constant.</param>
/// <param name="declaration">The declarator.</param>
internal sealed class EnumObject(string name, Node declaration) {

  /// <summary>The name of the constant.</summary>
  public string Name { get; } = name;

  /// <summary>The declarator, where a diagnostic about the object points.</summary>
  public Node Declaration { get; } = declaration;

  /// <summary>The enum, or <see langword="null"/> where the object is none.</summary>
  public IrEnumType? Type { get; private init; }

  /// <summary>Why the object is no enum, or <see langword="null"/>.</summary>
  public string? Problem { get; private init; }

  /// <summary>
  /// For a frozen object whose members are object literals of one shape of strings, numbers and truth
  /// values - metadata, as the Cipher framework keeps it (#1371) - the class of its members: a read
  /// of <c>X.K</c> is a new object of it with the values of the member, which nothing can tell from the
  /// member itself, since it is never written and never compared by identity.
  /// </summary>
  public ClassModel? Record { get; private init; }

  /// <summary>
  /// The enum object a declarator declares, or <see langword="null"/> where its value is no object of
  /// numbers: a frozen object literal is an enum or has a problem; a plain object literal whose
  /// properties are all numbers is an enum that is not frozen, which is a problem.
  /// </summary>
  /// <param name="name">The name of the constant.</param>
  /// <param name="declarator">The declarator.</param>
  /// <param name="value">The value of the declarator.</param>
  /// <param name="isPublic">Whether the file makes the constant visible to others.</param>
  /// <returns>The enum object, or <see langword="null"/>.</returns>
  public static EnumObject? Of(string name, Node declarator, Node value, bool isPublic) {
    if (value is ObjectExpression { Properties.Count: > 0 } plain && plain.Properties.All(property => property is Property { Value: var item } && Number(item) is not null))
      return new EnumObject(name, declarator) { Problem = $"the object {name}, which is not frozen: an enum object is Object.freeze({{ … }})" };

    if (value is not CallExpression { Callee: MemberExpression { Computed: false, Object: Identifier { Name: "Object" }, Property: Identifier { Name: "freeze" } }, Arguments: [ObjectExpression literal] })
      return null;

    if (literal.Properties.Count > 0 && literal.Properties.All(property => property is Property { Value: ObjectExpression }))
      return Frozen(name, declarator, literal, isPublic);

    var members = new List<IrEnumMember>();
    foreach (var item in literal.Properties) {
      if (item is not Property { Computed: false, Shorthand: false, Method: false, Kind: PropertyKind.Init, Key: Identifier { Name: var key }, Value: var member })
        return new EnumObject(name, declarator) { Problem = $"a member of the enum object {name} that is not a name and a number" };

      if (member is StringLiteral)
        return new EnumObject(name, declarator) { Problem = $"the string member {name}.{key}: an enum object holds numbers" };

      if (Number(member) is not { } number || number != System.Math.Floor(number) || number < int.MinValue || number > int.MaxValue || (number == 0 && double.IsNegative(number)))
        return new EnumObject(name, declarator) { Problem = $"the member {name}.{key}, which is no 32-bit integer" };

      if (members.Find(earlier => earlier.Name == key) is not null)
        return new EnumObject(name, declarator) { Problem = $"the member {name}.{key} twice" };

      if (members.Find(earlier => earlier.Value == (long)number) is { } same)
        return new EnumObject(name, declarator) { Problem = $"the members {name}.{same.Name} and {name}.{key} of the same value" };

      members.Add(new IrEnumMember(key, (long)number));
    }

    return new EnumObject(name, declarator) { Type = new IrEnumType(name, IrType.I32, members) { IsPublic = isPublic } };
  }

  /// <summary>
  /// A frozen object of object literals: a class whose read-only fields are the keys of the members,
  /// in the order the first member names them, each of the type every member's value of it has; a
  /// member of other keys, a value that is no literal and a key of values of two kinds are problems.
  /// </summary>
  private static EnumObject Frozen(string name, Node declarator, ObjectExpression literal, bool isPublic) {
    EnumObject Refused(string problem) => new(name, declarator) { Problem = problem };
    var values = new Dictionary<string, IReadOnlyList<Expression>>();
    List<string>? keys = null;
    var types = new List<JsType>();
    var evidence = new List<Node>();
    foreach (var item in literal.Properties.Cast<Property>()) {
      if (item is not { Computed: false, Shorthand: false, Method: false, Kind: PropertyKind.Init, Key: Identifier { Name: var key } } || values.ContainsKey(key))
        return Refused($"a member of the frozen object {name} that is not a name of its own and an object literal");

      var entries = new List<(string Key, Expression Value)>();
      foreach (var part in ((ObjectExpression)item.Value).Properties) {
        if (part is not Property { Computed: false, Shorthand: false, Method: false, Kind: PropertyKind.Init, Value: Expression value } property
            || KeyOf(property.Key) is not { } field || Literal(value) is not { } type)
          return Refused($"the member {name}.{key}, which is no object literal of strings, numbers and truth values");

        entries.Add((field, value));
        if (keys is null) {
          types.Add(type);
          evidence.Add(value);
        } else if (keys.IndexOf(field) is var at and >= 0) {
          types[at] = JsType.Join(types[at], type);
        }
      }

      keys ??= [.. entries.Select(entry => entry.Key)];
      if (!entries.Select(entry => entry.Key).SequenceEqual(keys))
        return Refused($"the members of the frozen object {name}, which have different keys");

      values[key] = [.. entries.Select(entry => entry.Value)];
    }

    // The object has a member, so the first one named the keys.
    var fields = keys!;
    if (fields.Select((key, index) => (Key: key, Index: index)).FirstOrDefault(entry => types[entry.Index].Kind == JsKind.Conflict) is { Key: { } mixed })
      return Refused($"the key {mixed} of the members of the frozen object {name}, which holds values of two kinds");

    var owner = new ClassModel(name, declarator) { IsPublic = isPublic, Frozen = values };
    for (var index = 0; index < fields.Count; ++index)
      owner.Fields.Add(new ClassField(fields[index], new Variable(ClassModel.IrName(fields[index]), evidence[index], false) { Type = types[index], Evidence = evidence[index] }, null));

    return new EnumObject(name, declarator) { Record = owner };
  }

  /// <summary>The key of a property: a name or a string.</summary>
  private static string? KeyOf(Node key) => key switch {
    Identifier identifier => identifier.Name,
    StringLiteral text => text.Value,
    _ => null
  };

  /// <summary>The type of a literal a frozen record holds: a string, a number or a truth value; <see langword="null"/> for anything else.</summary>
  private static JsType? Literal(Expression value) => value switch {
    StringLiteral => JsType.String,
    BooleanLiteral => JsType.Boolean,
    _ when Number(value) is { } number => JsType.Literal(number),
    _ => null
  };

  /// <summary>A numeric literal, negated or not, or <see langword="null"/>.</summary>
  private static double? Number(Node node) => node switch {
    NumericLiteral literal => literal.Value,
    UnaryExpression { Operator: Operator.UnaryNegation, Argument: NumericLiteral literal } => -literal.Value,
    _ => null
  };

}
