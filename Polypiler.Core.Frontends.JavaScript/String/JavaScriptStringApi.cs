using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>One construct of JavaScript on strings and what the frontend makes of it.</summary>
/// <param name="Construct">The construct as JavaScript writes it.</param>
/// <param name="Ir">The operations of the IR it becomes, separated by commas; empty for a construct that is refused.</param>
/// <param name="Refusal">Why the construct is refused; empty for one that is imported.</param>
public sealed record JavaScriptStringMember(string Construct, string Ir, string Refusal = "");

/// <summary>
/// The constructs of JavaScript on strings the frontend imports, each as the operations of the IR it
/// means, and those it refuses, each with the reason - the table the tests check against the
/// lowering. A method of a string that is listed nowhere is refused by name.
/// </summary>
public static class JavaScriptStringApi {

  /// <summary>What a conversion between numbers and text is told until the IR has one.</summary>
  public const string NumberText = "which converts a number to text, not supported until number-to-text conversion";

  /// <summary>Every construct the table states.</summary>
  public static IReadOnlyList<JavaScriptStringMember> Members { get; } = [
    new("string literal", "IrStrConst"),
    new("template literal", "IrStrConst, StrConcat"),
    new("s.length", "StrLength"),
    new("s.charCodeAt(i)", "StrLength, StrCodeUnitAt"),
    new("s[i]", "StrCodeUnitAt, StrFromCodeUnit"),
    new("s.charAt(i)", "StrLength, StrCodeUnitAt, StrFromCodeUnit"),
    new("s.at(i)", "StrLength, StrCodeUnitAt, StrFromCodeUnit"),
    new("a + b", "StrConcat"),
    new("a += b", "StrConcat"),
    new("s.concat(...)", "StrConcat"),
    new("===, !==, ==, !=", "StrEquals"),
    new("<, <=, >, >=", "StrCompareOrdinal"),
    new("s.substring(a[, b])", "StrSubstring, Min, Max"),
    new("s.slice(a[, b])", "StrSubstring, Min, Max"),
    new("s.indexOf(n[, from])", "StrIndexOf"),
    new("s.lastIndexOf(n[, from])", "StrLastIndexOf"),
    new("s.includes(n)", "StrContains"),
    new("s.startsWith(n)", "StrStartsWith"),
    new("s.endsWith(n)", "StrEndsWith"),
    new("s.replace(string, string)", "StrReplaceFirst"),
    new("s.replaceAll(string, string)", "StrReplaceAll"),
    new("s.split(string[, limit])", "StrSplit"),
    new("a.join(sep)", "StrJoin"),
    new("s.trim()", "StrTrim"),
    new("s.trimStart()", "StrTrimStart"),
    new("s.trimEnd()", "StrTrimEnd"),
    new("s.padStart(n[, f])", "StrPadStart"),
    new("s.padEnd(n[, f])", "StrPadEnd"),
    new("s.repeat(n)", "StrRepeat"),
    new("String.fromCharCode(...)", "StrFromCodeUnit, StrConcat"),
    new("s.codePointAt(i)", "StrCodePointAt"),
    new("for (const ch of s)", "StrLength, StrNextCodePoint, StrSubstring"),
    new("switch on a string", "StrEquals"),
    new("s.localeCompare(t)", "", "which depends on the locale"),
    new("s.toUpperCase()", "", "which is case mapping"),
    new("s.toLowerCase()", "", "which is case mapping"),
    new("s.normalize()", "", "normalization"),
    new("String.raw", "", "a tagged template"),
    new("regular expression", "", "a regular expression"),
    new("s.replace with $` $' $n", "", "a pattern for a part of the string around the match"),
    new("s.replace with a replacement that is no constant", "", "which may hold a $ pattern"),
    new("s.replaceAll with a needle that may be empty", "", "JavaScript inserts between every two code units"),
    new("String.fromCodePoint", "", "code points"),
    new("new String", "", "a string object"),
    new("\"x\" + number", "", NumberText),
    new("String(n)", "", NumberText),
    new("template literal with a number hole", "", NumberText)
  ];

  /// <summary>The entries the frontend imports.</summary>
  public static IEnumerable<JavaScriptStringMember> Supported => Members.Where(member => member.Refusal.Length == 0);

  /// <summary>The entries the frontend refuses.</summary>
  public static IEnumerable<JavaScriptStringMember> Refused => Members.Where(member => member.Refusal.Length > 0);

}
