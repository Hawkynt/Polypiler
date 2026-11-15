using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The constructs of JavaScript that convert numbers and truth values to text and back, each as the
/// operations of the IR it means, and those the frontend refuses, each with the reason - the table
/// the tests check against the lowering. The rules are those of ECMA-262.
/// </summary>
public static class JavaScriptNumberTextApi {

  /// <summary>Every construct the table states.</summary>
  public static IReadOnlyList<JavaScriptStringMember> Members { get; } = [
    new("\"…\" + n, n + \"…\", template literal hole, String(n), n.toString(), n.toString(10)", "FormatEcma, StrConcat"),
    new("\"…\" + b, String(b), b.toString()", "FormatBoolEcma"),
    new("n.toString(radix) of a proven integer or a BigInt, radix a literal from 2 to 36", "FormatRadix"),
    new("n.toFixed(d), d a literal from 0 to 100", "FixedEcma"),
    new("Number(s), +s", "ParseNumberEcma"),
    new("parseFloat(s)", "ParseFloatEcma"),
    new("parseInt(s[, radix]), radix a literal", "ParseIntEcma"),
    new("n.toString(radix) of a number with a fraction", "", "toString with a radix of a number with a fraction"),
    new("n.toString(radix) with a computed radix", "", "a radix that is no integer literal from 2 to 36"),
    new("n.toFixed(d) with computed digits", "", "digits that are no integer literal from 0 to 100"),
    new("parseInt(s, radix) with a computed radix", "", "a radix that is no integer literal"),
    new("n.toPrecision, n.toExponential, n.toLocaleString", "", "which is a format of its own"),
    new("parseFloat, parseInt and String with another number of arguments, or of a value that is no string", "", "which JavaScript converts to text first")
  ];

  /// <summary>The entries the frontend imports.</summary>
  public static IEnumerable<JavaScriptStringMember> Supported => Members.Where(member => member.Refusal.Length == 0);

  /// <summary>The entries the frontend refuses.</summary>
  public static IEnumerable<JavaScriptStringMember> Refused => Members.Where(member => member.Refusal.Length > 0);

}
