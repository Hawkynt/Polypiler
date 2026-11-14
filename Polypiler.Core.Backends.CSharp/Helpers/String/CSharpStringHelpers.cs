namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The helpers of the string operations: what no framework has, in C# 2 for .NET Framework 2.0 like
/// every helper. A range or a position outside the string throws an <c>IndexOutOfRangeException</c>, a
/// negative count an <c>OverflowException</c>, as the IR's traps are written in generated C#. The two
/// white-space sets are the tables of <c>StringOperations</c>, spelled out code unit by code unit.
/// </summary>
internal static class CSharpStringHelpers {

  private const string OutOfRange = "throw new global::System.IndexOutOfRangeException();";
  private const string Ordinal = "global::System.StringComparison.Ordinal";
  private const string Parts = "global::System.Collections.Generic.List<string>";

  /// <summary>The string of a range of code units of an array.</summary>
  public static CSharpHelper FromCodeUnits { get; } = new("FromCodeUnits", "string", "ushort[] units, int start, int length", [
    "if (start < 0 || length < 0 || start > units.Length - length) " + OutOfRange,
    "char[] text = new char[length];",
    "for (int index = 0; index < length; ++index) text[index] = (char)units[start + index];",
    "return new string(text);"
  ]);

  /// <summary>A new array of the code units of a string.</summary>
  public static CSharpHelper ToCodeUnits { get; } = new("ToCodeUnits", "ushort[]", "string text", [
    "ushort[] units = new ushort[text.Length];",
    "for (int index = 0; index < text.Length; ++index) units[index] = text[index];",
    "return units;"
  ]);

  /// <summary>A string the given number of times; a negative count throws.</summary>
  public static CSharpHelper Repeat { get; } = new("Repeat", "string", "string text, int count", [
    "if (count < 0) throw new global::System.OverflowException();",
    "global::System.Text.StringBuilder result = new global::System.Text.StringBuilder();",
    "for (int pass = 0; pass < count; ++pass) result.Append(text);",
    "return result.ToString();"
  ]);

  /// <summary>The last start at or before a position of a needle, as ECMA-262's <c>lastIndexOf</c> counts it.</summary>
  public static CSharpHelper LastIndexOf { get; } = new("LastIndexOf", "int", "string text, string needle, int from", [
    "if (from < 0 || from > text.Length) " + OutOfRange,
    "for (int start = global::System.Math.Min(from, text.Length - needle.Length); start >= 0; --start) if (string.CompareOrdinal(text, start, needle, 0, needle.Length) == 0) return start;",
    "return -1;"
  ]);

  /// <summary>The last position at or before a position of a code unit.</summary>
  public static CSharpHelper LastIndexOfUnit { get; } = new("LastIndexOfUnit", "int", "string text, char unit, int from", [
    "if (from < 0 || from > text.Length) " + OutOfRange,
    "for (int index = global::System.Math.Min(from, text.Length - 1); index >= 0; --index) if (text[index] == unit) return index;",
    "return -1;"
  ]);

  /// <summary>Whether a string begins with a code unit, which String.StartsWith(Char) says from .NET Standard 2.1 on.</summary>
  public static CSharpHelper StartsWithUnit { get; } = new("StartsWithUnit", "bool", "string text, char unit", [
    "return text.Length != 0 && text[0] == unit;"
  ]);

  /// <summary>Whether a string ends with a code unit, which String.EndsWith(Char) says from .NET Standard 2.1 on.</summary>
  public static CSharpHelper EndsWithUnit { get; } = new("EndsWithUnit", "bool", "string text, char unit", [
    "return text.Length != 0 && text[text.Length - 1] == unit;"
  ]);

  /// <summary>The first occurrence of a needle replaced; an empty needle stands at 0.</summary>
  public static CSharpHelper ReplaceFirst { get; } = new("ReplaceFirst", "string", "string text, string needle, string replacement", [
    $"int at = text.IndexOf(needle, {Ordinal});",
    "return at < 0 ? text : text.Substring(0, at) + replacement + text.Substring(at + needle.Length);"
  ]);

  /// <summary>
  /// ECMA-262's <c>split</c> with a string: an empty separator splits between every two code units,
  /// and a limit that is not negative keeps the first parts only.
  /// </summary>
  public static CSharpHelper Split { get; } = new("Split", "string[]", "string text, string separator, int limit", [
    $"{Parts} parts = new {Parts}();",
    "if (separator.Length == 0) {",
    "  for (int index = 0; index < text.Length; ++index) parts.Add(text.Substring(index, 1));",
    "} else {",
    "  int start = 0;",
    $"  int at = text.IndexOf(separator, {Ordinal});",
    "  while (at >= 0) {",
    "    parts.Add(text.Substring(start, at - start));",
    "    start = at + separator.Length;",
    $"    at = text.IndexOf(separator, start, {Ordinal});",
    "  }",
    "  parts.Add(text.Substring(start));",
    "}",
    "if (limit >= 0 && parts.Count > limit) parts.RemoveRange(limit, parts.Count - limit);",
    "return parts.ToArray();"
  ]);

  /// <summary>A trim of what char.IsWhiteSpace accepts, which String.Trim() trims from .NET Framework 4 on.</summary>
  public static CSharpHelper TrimDotNet { get; } = Trim("TrimDotNet", "\\u0009\\u000A\\u000B\\u000C\\u000D \\u0085\\u00A0\\u1680\\u2000\\u2001\\u2002\\u2003\\u2004\\u2005\\u2006\\u2007\\u2008\\u2009\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000");

  /// <summary>A trim of ECMA-262's WhiteSpace and LineTerminator.</summary>
  public static CSharpHelper TrimEcma { get; } = Trim("TrimEcma", "\\u0009\\u000A\\u000B\\u000C\\u000D \\u00A0\\u1680\\u2000\\u2001\\u2002\\u2003\\u2004\\u2005\\u2006\\u2007\\u2008\\u2009\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000\\uFEFF");

  /// <summary>
  /// ECMA-262's <c>padStart</c> and <c>padEnd</c>: the filler repeated up to the length, the last copy
  /// cut to fit; the string itself when it is as long or the filler is empty.
  /// </summary>
  public static CSharpHelper Pad { get; } = new("Pad", "string", "string text, int length, string filler, bool atStart", [
    "if (length <= text.Length || filler.Length == 0) return text;",
    "global::System.Text.StringBuilder padding = new global::System.Text.StringBuilder();",
    "while (padding.Length < length - text.Length) padding.Append(filler);",
    "padding.Length = length - text.Length;",
    "return atStart ? padding.ToString() + text : text + padding.ToString();"
  ]);

  /// <summary>ECMA-262's <c>codePointAt</c>: a pair of surrogates as its code point, any other code unit, a lone surrogate too, as itself.</summary>
  public static CSharpHelper CodePointAt { get; } = new("CodePointAt", "int", "string text, int index", [
    "char unit = text[index];",
    "if (char.IsHighSurrogate(unit) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) return char.ConvertToUtf32(unit, text[index + 1]);",
    "return unit;"
  ]);

  /// <summary>A trim of the code units of a set, written out as a literal.</summary>
  private static CSharpHelper Trim(string name, string set) => new(name, "string", "string text, bool start, bool end", [
    "int first = 0;",
    "int last = text.Length;",
    $"while (start && first < last && \"{set}\".IndexOf(text[first]) >= 0) ++first;",
    $"while (end && last > first && \"{set}\".IndexOf(text[last - 1]) >= 0) --last;",
    "return text.Substring(first, last - first);"
  ]);

}
