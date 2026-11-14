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

  /// <summary>A numeric literal, negated or not, or <see langword="null"/>.</summary>
  private static double? Number(Node node) => node switch {
    NumericLiteral literal => literal.Value,
    UnaryExpression { Operator: Operator.UnaryNegation, Argument: NumericLiteral literal } => -literal.Value,
    _ => null
  };

}
