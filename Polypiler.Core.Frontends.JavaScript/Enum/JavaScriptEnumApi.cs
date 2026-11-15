using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The constructs of JavaScript on enum objects - a <c>const</c> of the scope that encloses the
/// functions whose value is <c>Object.freeze({ A: 0, … })</c> of distinct 32-bit integers - each as what
/// the IR makes of it, and those the frontend refuses, each with the reason: the table the tests check
/// against the lowering.
/// </summary>
public static class JavaScriptEnumApi {

  /// <summary>Every construct the table states.</summary>
  public static IReadOnlyList<JavaScriptStringMember> Members { get; } = [
    new("const X = Object.freeze({ A: <integer literal>, … }); X.A", "IrEnumType over i32, IrConst"),
    new("a variable, parameter or result that only members of one enum object reach, and literals equal to a member", "the enum type"),
    new("x === X.A, x !== X.A, switch (x) { case X.A: … } on such a value", "IrBinary on the enum, IrSwitch"),
    new("any other use of a member: arithmetic, X.A | X.B, x < X.A", "IrConvert to i32"),
    new("export const X = Object.freeze({ … }), export { X }, an enum object of a script", "IrEnumType.IsPublic"),
    new("const X = { A: 0, … } that is not frozen", "", "which is not frozen"),
    new("Object.freeze({ A: \"a\", … }): a string member", "", "an enum object holds numbers"),
    new("Object.freeze({ A: 1, B: 1 }): two members of the same value", "", "of the same value"),
    new("Object.freeze({ A: 1.5 }): a member that is no 32-bit integer", "", "which is no 32-bit integer"),
    new("Object.freeze({ [k]: 0 }), a method or a spread in the object", "", "that is not a name and a number"),
    new("X[k]: a computed key", "", "a computed key into the enum object"),
    new("X used as a value: Object.keys(X), f(X), y = X, a TypeScript-style reverse mapping", "", "used other than by its members"),
    new("X.Q of a name that is no member", "", "which is no member of the enum object")
  ];

  /// <summary>The entries the frontend imports.</summary>
  public static IEnumerable<JavaScriptStringMember> Supported => Members.Where(member => member.Refusal.Length == 0);

  /// <summary>The entries the frontend refuses.</summary>
  public static IEnumerable<JavaScriptStringMember> Refused => Members.Where(member => member.Refusal.Length > 0);

}
