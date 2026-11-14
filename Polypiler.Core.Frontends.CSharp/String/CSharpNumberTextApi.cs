using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The constructs of C# that convert numbers and truth values to text and back, each as the
/// operations of the IR it means, and those the frontend refuses, each with the reason - the table
/// the tests check against the lowering. The rules are those of .NET Core 3.0 and later with the
/// invariant culture; a construct that leaves the culture to the thread is imported with the caveat
/// <see cref="CSharpFrontend.InvariantCulture"/>.
/// </summary>
public static class CSharpNumberTextApi {

  /// <summary>Every construct the table states.</summary>
  public static IReadOnlyList<CSharpStringMember> Members { get; } = [
    new("x.ToString(CultureInfo.InvariantCulture), x.ToString(\"G\" | \"R\", CultureInfo.InvariantCulture)", "FormatDotNet"),
    new("x.ToString(), x.ToString(\"G\" | \"R\") without a provider", "FormatDotNet"),
    new("string + x, interpolation {x}, string.Concat(…, x)", "FormatDotNet, StrConcat"),
    new("b.ToString(), string + b", "FormatBoolDotNet"),
    new("Convert.ToString(x[, provider]) of a number or a bool", "FormatDotNet, FormatBoolDotNet"),
    new("x.ToString(\"F<n>\"[, provider]), interpolation {x:F<n>}, n from 0 to 15", "FixedDotNet"),
    new("x.ToString(\"D<n>\"[, provider]) of an integer", "FormatRadix"),
    new("x.ToString(\"x<n>\" | \"X<n>\"[, provider]) of an integer", "FormatRadix"),
    new("Convert.ToString(v, 2 | 8 | 10 | 16)", "FormatRadix"),
    new("T.Parse(s[, CultureInfo.InvariantCulture]) of every integer type, float and double", "ParseDotNet"),
    new("x.ToString(provider) with a culture other than the invariant one", "", "which is a culture other than the invariant one"),
    new("a format other than G, R, F<n>, D<n> and x<n>: N2, E3, G17, C, P, custom", "", "a format string outside the subset"),
    new("a format string that is no constant", "", "a format that is no constant"),
    new("T.Parse(s, NumberStyles…)", "", "NumberStyles"),
    new("T.TryParse(s, out x)", "", "whose result is an out parameter (#216)"),
    new("Convert.ToString(v, radix) with another or a computed radix", "", "a radix other than the constant 2, 8, 10 or 16"),
    new("an interpolation with an alignment", "", "an alignment")
  ];

  /// <summary>The entries the frontend imports.</summary>
  public static IEnumerable<CSharpStringMember> Supported => Members.Where(member => member.Refusal.Length == 0);

  /// <summary>The entries the frontend refuses.</summary>
  public static IEnumerable<CSharpStringMember> Refused => Members.Where(member => member.Refusal.Length > 0);

}
