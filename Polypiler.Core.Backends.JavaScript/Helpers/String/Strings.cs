using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The helpers of the string operations, in the notation of the other helpers: the checks ECMAScript
/// does not make - an index or a range outside the string throws the <c>RangeError</c> of the IR's
/// trap, where <c>charCodeAt</c> would return a NaN and <c>substring</c> would clamp - and the methods
/// a level lacks, written with those of ECMAScript 5. The two white-space sets are the tables of
/// <c>StringOperations</c>, spelled out code unit by code unit.
/// </summary>
internal static partial class JavaScriptHelpers {

  private const string Position = "  if (from < 0 || from > s.length) " + IndexOutOfRange;
  private const string At = "  if (i < 0 || i >= s.length) " + IndexOutOfRange;

  /// <summary>Every string helper with its text for a profile.</summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> StringHelpers => [
    Same("$unitAt", Lines("function $unitAt(s, i) {", At, "  return s.charCodeAt(i);", "}")),
    Same("$fromUnits", Lines(
      "function $fromUnits(units, start, length) {",
      "  if (start < 0 || length < 0 || start > units.length - length) " + IndexOutOfRange,
      "  @let s = \"\";",
      "  for (@let k = start; k < start + length; ++k) s += String.fromCharCode(units[k]);",
      "  return s;",
      "}")),
    Same("$fromAllUnits", Lines(
      "function $fromAllUnits(units) {",
      "  @let s = \"\";",
      "  for (@let k = 0; k < units.length; ++k) s += String.fromCharCode(units[k]);",
      "  return s;",
      "}")),
    ("$toUnits", native => Lines(
      "function $toUnits(s) {",
      native("Uint16Array") ? "  @const units = new Uint16Array(s.length);" : "  @const units = [];",
      "  for (@let k = 0; k < s.length; ++k) units[k] = s.charCodeAt(k);",
      "  return units;",
      "}")),
    ("$repeat", native => native(".repeat")
      ? Lines("function $repeat(s, n) {", "  if (n < 0) " + Overflow, "  return s.repeat(n);", "}")
      : Lines("function $repeat(s, n) {", "  if (n < 0) " + Overflow, "  @let r = \"\";", "  for (@let k = 0; k < n; ++k) r += s;", "  return r;", "}")),
    Same("$substr", Lines(
      "function $substr(s, start, length) {",
      "  if (start < 0 || length < 0 || start > s.length - length) " + IndexOutOfRange,
      "  return s.substring(start, start + length);",
      "}")),
    Same("$substrFrom", Lines(
      "function $substrFrom(s, start) {",
      "  if (start < 0 || start > s.length) " + IndexOutOfRange,
      "  return s.substring(start);",
      "}")),
    Same("$cmp", Lines("function $cmp(a, b) {", "  return a < b ? -1 : a > b ? 1 : 0;", "}")),
    Same("$indexOf", Lines("function $indexOf(s, n, from) {", Position, "  return s.indexOf(n, from);", "}")),
    Same("$lastIndexOf", Lines("function $lastIndexOf(s, n, from) {", Position, "  return s.lastIndexOf(n, from);", "}")),
    Same("$startsWith", Lines("function $startsWith(s, n) {", "  return s.lastIndexOf(n, 0) === 0;", "}")),
    Same("$endsWith", Lines("function $endsWith(s, n) {", "  @const at = s.length - n.length;", "  return at >= 0 && s.indexOf(n, at) === at;", "}")),
    Same("$replaceAll", Lines("function $replaceAll(s, n, r) {", "  if (n === \"\") throw new RangeError(\"invalid operation\");", "  return s.split(n).join(r);", "}")),
    Same("$replaceFirst", Lines(
      "function $replaceFirst(s, n, r) {",
      "  @const at = s.indexOf(n);",
      "  return at < 0 ? s : s.substring(0, at) + r + s.substring(at + n.length);",
      "}")),
    Trim("$trimNet", "\\u0009\\n\\u000B\\u000C\\u000D \\u0085\\u00A0\\u1680\\u2000\\u2001\\u2002\\u2003\\u2004\\u2005\\u2006\\u2007\\u2008\\u2009\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000"),
    Trim("$trimEcma", "\\u0009\\n\\u000B\\u000C\\u000D \\u00A0\\u1680\\u2000\\u2001\\u2002\\u2003\\u2004\\u2005\\u2006\\u2007\\u2008\\u2009\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000\\uFEFF"),
    Same("$pad", Lines(
      "function $pad(s, length, filler, atStart) {",
      "  if (length <= s.length || filler === \"\") return s;",
      "  @let padding = \"\";",
      "  while (padding.length < length - s.length) padding += filler;",
      "  padding = padding.substring(0, length - s.length);",
      "  return atStart ? padding + s : s + padding;",
      "}")),
    ("$codePointAt", native => native(".codePointAt")
      ? Lines("function $codePointAt(s, i) {", At, "  return s.codePointAt(i);", "}")
      : Lines(
        "function $codePointAt(s, i) {",
        At,
        "  @const high = s.charCodeAt(i);",
        "  if (high < 0xD800 || high > 0xDBFF || i + 1 >= s.length) return high;",
        "  @const low = s.charCodeAt(i + 1);",
        "  return low < 0xDC00 || low > 0xDFFF ? high : (high - 0xD800) * 0x400 + (low - 0xDC00) + 0x10000;",
        "}")),
    Same("$nextCodePoint", Lines(
      "function $nextCodePoint(s, i) {",
      At,
      "  @const high = s.charCodeAt(i);",
      "  if (high < 0xD800 || high > 0xDBFF || i + 1 >= s.length) return 1;",
      "  @const low = s.charCodeAt(i + 1);",
      "  return low < 0xDC00 || low > 0xDFFF ? 1 : 2;",
      "}"))
  ];

  /// <summary>The names of the string helpers, in the order a module lists them, after those of the integers.</summary>
  private static IEnumerable<string> StringOrder => StringHelpers.Select(helper => helper.Name);

  private static readonly Dictionary<string, Func<Func<string, bool>, string>> _string = StringHelpers.ToDictionary(helper => helper.Name, helper => helper.Text);

  /// <summary>The text of a string helper for the profile, or <see langword="null"/> for a name that is none.</summary>
  private static string? StringSource(string name, Func<string, bool> native) => _string.TryGetValue(name, out var text) ? text(native) : null;

  /// <summary>A trim of the code units of a set at the start, the end or both, the set written out as a literal.</summary>
  private static (string, Func<Func<string, bool>, string>) Trim(string name, string set) => Same(name, Lines(
    $"function {name}(s, start, end) {{",
    "  @let first = 0;",
    "  @let last = s.length;",
    $"  while (start && first < last && \"{set}\".indexOf(s.charAt(first)) >= 0) ++first;",
    $"  while (end && last > first && \"{set}\".indexOf(s.charAt(last - 1)) >= 0) --last;",
    "  return s.substring(first, last);",
    "}"));

}
