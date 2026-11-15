using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The constructs of C# on enums, each as the operations of the IR it means, and those the frontend
/// refuses, each with the reason - the table the tests check against the lowering. A member of
/// <c>System.Enum</c> listed nowhere is refused by name.
/// </summary>
public static class CSharpEnumApi {

  /// <summary>Every construct the table states.</summary>
  public static IReadOnlyList<CSharpStringMember> Members { get; } = [
    new("enum E [: integer type] { … } at the top of the file or in the static class, [Flags], explicit and implicit values", "IrEnumType"),
    new("E.A, locals, parameters, results, fields, constants and arrays of E, casts to and from integers", "IrConst, IrConvert"),
    new("&, |, ^, ~ of E; e + n, e - n, e - e; ==, !=, <, <=, >, >=; compound assignments, ++ and --", "IrBinary, IrUnary"),
    new("switch on E", "IrSwitch"),
    new("e.ToString(), string + e, interpolation {e}", "EnumToString"),
    new("Enum.IsDefined(typeof(E), v), Enum.IsDefined<E>(v)", "EnumIsDefined"),
    new("(E)Enum.Parse(typeof(E), s[, ignoreCase]), Enum.Parse<E>(s[, ignoreCase])", "EnumParse"),
    new("e.HasFlag(f)", "EnumHasFlag"),
    new("Enum.GetNames(typeof(E)), Enum.GetNames<E>()", "EnumNames"),
    new("(E[])Enum.GetValues(typeof(E)), Enum.GetValues<E>()", "EnumValues"),
    new("e.ToString(\"D\" | \"X\" | \"G\" | \"F\")", "", "with a format"),
    new("Enum.TryParse(s, out e)", "", "whose result is an out parameter (#216)"),
    new("Enum.Format, Enum.GetName, Enum.GetUnderlyingType and the other members of System.Enum", "", "Enum.Format"),
    new("e.ToString(), Enum.GetNames and Enum.GetValues of an enum whose members repeat a value", "", "whose members repeat a value"),
    new("Enum.IsDefined(typeof(E), v) with v of another type than E and its underlying type", "", "for which .NET throws"),
    new("an attribute on an enum other than [Flags]", "", "on an enum")
  ];

  /// <summary>The entries the frontend imports.</summary>
  public static IEnumerable<CSharpStringMember> Supported => Members.Where(member => member.Refusal.Length == 0);

  /// <summary>The entries the frontend refuses.</summary>
  public static IEnumerable<CSharpStringMember> Refused => Members.Where(member => member.Refusal.Length > 0);

}
