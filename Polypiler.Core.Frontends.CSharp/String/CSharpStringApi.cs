using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>One construct of C# on strings and what the frontend makes of it.</summary>
/// <param name="Construct">The construct as C# writes it: a member with its parameter types, an operator or a statement.</param>
/// <param name="Ir">The operations of the IR it becomes, separated by commas; empty for a construct that is refused.</param>
/// <param name="Refusal">Why the construct is refused, which the diagnostic adds to its name; empty for one that is imported.</param>
public sealed record CSharpStringMember(string Construct, string Ir, string Refusal = "");

/// <summary>
/// The constructs of C# on strings the frontend imports, each as the operations of the IR it means,
/// and those it refuses, each with the reason - the table the tests check against the lowering. A
/// member that compares by culture is refused rather than read as ordinal; a member of
/// <c>string</c> or <c>char</c> that is listed nowhere is refused by name.
/// </summary>
public static class CSharpStringApi {

  /// <summary>What every member that compares by culture is told.</summary>
  public const string Culture = "which compares by culture: pass StringComparison.Ordinal";

  /// <summary>What a conversion between numbers and text is told until the IR has one.</summary>
  public const string NumberText = "which converts a number to text, not supported until number-to-text conversion";

  /// <summary>Every construct the table states.</summary>
  public static IReadOnlyList<CSharpStringMember> Members { get; } = [
    new("string local, parameter, result, const, static readonly, string[]", "str"),
    new("literal", "IrStrConst"),
    new("s.Length", "StrLength"),
    new("s[i]", "StrCodeUnitAt"),
    new("string + string", "StrConcat"),
    new("string + char", "StrConcat, StrFromCodeUnit"),
    new("interpolated string", "StrConcat"),
    new("string.Concat(string, string, ...)", "StrConcat"),
    new("==", "StrEquals"),
    new("!=", "StrEquals"),
    new("string.Equals(string, string)", "StrEquals"),
    new("Equals(string)", "StrEquals"),
    new("Equals(string, StringComparison.Ordinal)", "StrEquals"),
    new("string.CompareOrdinal(string, string) against 0", "StrCompareOrdinal"),
    new("string.Compare(string, string, StringComparison.Ordinal) against 0", "StrCompareOrdinal"),
    new("Substring(int)", "StrSubstring"),
    new("Substring(int, int)", "StrSubstring"),
    new("IndexOf(char)", "StrIndexOf"),
    new("IndexOf(char, int)", "StrIndexOf"),
    new("IndexOf(string, StringComparison.Ordinal)", "StrIndexOf"),
    new("IndexOf(string, int, StringComparison.Ordinal)", "StrIndexOf"),
    new("LastIndexOf(char)", "StrLastIndexOf"),
    new("LastIndexOf(string, StringComparison.Ordinal)", "StrLastIndexOf"),
    new("Contains(char)", "StrContains"),
    new("Contains(string)", "StrContains"),
    new("StartsWith(char)", "StrStartsWith"),
    new("StartsWith(string, StringComparison.Ordinal)", "StrStartsWith"),
    new("EndsWith(char)", "StrEndsWith"),
    new("EndsWith(string, StringComparison.Ordinal)", "StrEndsWith"),
    new("Replace(char, char)", "StrReplaceAll"),
    new("Replace(string, string)", "StrReplaceAll"),
    new("Split(char)", "StrSplit"),
    new("Split(string) with a constant separator", "StrSplit"),
    new("string.Join(string, string[])", "StrJoin"),
    new("Trim()", "StrTrim"),
    new("TrimStart()", "StrTrimStart"),
    new("TrimEnd()", "StrTrimEnd"),
    new("PadLeft(int)", "StrPadStart"),
    new("PadLeft(int, char)", "StrPadStart, StrFromCodeUnit"),
    new("PadRight(int)", "StrPadEnd"),
    new("PadRight(int, char)", "StrPadEnd, StrFromCodeUnit"),
    new("new string(char, int)", "StrRepeat"),
    new("new string(char[])", "StrFromCodeUnits"),
    new("new string(char[], int, int)", "StrFromCodeUnits"),
    new("ToCharArray()", "StrToCodeUnits"),
    new("char.ToString()", "StrFromCodeUnit"),
    new("switch on a string", "StrEquals"),
    new("foreach (char c in s)", "StrLength, StrCodeUnitAt"),
    new("IndexOf(string)", "", Culture),
    new("LastIndexOf(string)", "", Culture),
    new("StartsWith(string)", "", Culture),
    new("EndsWith(string)", "", Culture),
    new("string.Compare(string, string)", "", Culture),
    new("CompareTo(string)", "", Culture),
    new("a StringComparison other than Ordinal", "", Culture),
    new("string.CompareOrdinal(string, string) other than against 0", "", "the value of CompareOrdinal other than its sign, which .NET documents only by its sign"),
    new("Split(char[], int)", "", "whose last part keeps the rest of the string"),
    new("ToUpper()", "", "case mapping"),
    new("ToLower()", "", "case mapping"),
    new("ToUpperInvariant()", "", "case mapping"),
    new("ToLowerInvariant()", "", "case mapping"),
    new("Normalize()", "", "normalization"),
    new("string.Format", "", "string formatting"),
    new("StringBuilder", "", "a mutable string"),
    new("null of the type string", "", "a string that is no string"),
    new("new string[n]", "", "an array of strings without its elements, which .NET fills with null"),
    new("string + number", "", NumberText),
    new("interpolated string with a number", "", NumberText)
  ];

  /// <summary>The entries the frontend imports.</summary>
  public static IEnumerable<CSharpStringMember> Supported => Members.Where(member => member.Refusal.Length == 0);

  /// <summary>The entries the frontend refuses.</summary>
  public static IEnumerable<CSharpStringMember> Refused => Members.Where(member => member.Refusal.Length > 0);

}
