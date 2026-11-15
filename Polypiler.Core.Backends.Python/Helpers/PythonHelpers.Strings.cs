namespace Polypiler.Core.Backends.Python;

internal static partial class PythonHelpers {

  /// <summary>The code unit at an index; the bounds trap for a negative one, which Python would count from the end.</summary>
  public const string UnitAt = "_ply_unit_at";

  /// <summary>The string of a range of code units; the bounds trap outside the array.</summary>
  public const string FromUnits = "_ply_from_units";

  /// <summary>A string repeated; the overflow trap for a negative count.</summary>
  public const string Repeat = "_ply_repeat";

  /// <summary>The part of a string from an index on; the bounds trap outside it.</summary>
  public const string SubstringFrom = "_ply_substring_from";

  /// <summary>A part of a string; the bounds trap outside it.</summary>
  public const string Substring = "_ply_substring";

  /// <summary>-1, 0 or 1 by the first code unit that differs.</summary>
  public const string CompareOrdinal = "_ply_compare";

  /// <summary>The first start of a needle at or after a position; the bounds trap outside the string.</summary>
  public const string IndexOf = "_ply_index_of";

  /// <summary>The last start of a needle at or before a position; the bounds trap outside the string.</summary>
  public const string LastIndexOf = "_ply_last_index_of";

  /// <summary>Every occurrence of a needle replaced; the invalid-operation trap for an empty one.</summary>
  public const string ReplaceAll = "_ply_replace_all";

  /// <summary>The parts between the separators, at most a limit of them.</summary>
  public const string Split = "_ply_split";

  /// <summary>A string without the white space of ECMA-262 at its start, its end or both.</summary>
  public const string TrimEcma = "_ply_trim_ecma";

  /// <summary>A string without the white space of .NET at its start, its end or both.</summary>
  public const string TrimDotNet = "_ply_trim_net";

  /// <summary>A string padded with a filler at its start or end, as ECMA-262's <c>padStart</c> and <c>padEnd</c>.</summary>
  public const string Pad = "_ply_pad";

  /// <summary>ECMA-262's <c>codePointAt</c>; the bounds trap outside the string.</summary>
  public const string CodePointAt = "_ply_code_point_at";

  /// <summary>The code units of the code point at an index, 1 or 2; the bounds trap outside the string.</summary>
  public const string NextCodePoint = "_ply_next_code_point";

  /// <summary>
  /// The string operations of the IR where Python would count from the end, clamp, or split and trim
  /// otherwise. A string is a <c>str</c> of UTF-16 code units, one character each.
  /// </summary>
  private const string StringsSource = """
    def _ply_unit_at(text: str, index: int) -> int:
        if index < 0:
            raise IndexError("index out of range")
        return ord(text[index])


    def _ply_from_units(units: list, start: int, length: int) -> str:
        if start < 0 or length < 0 or start + length > len(units):
            raise IndexError("index out of range")
        return "".join(map(chr, units[start:start + length]))


    def _ply_repeat(text: str, count: int) -> str:
        if count < 0:
            raise OverflowError("integer overflow")
        return text * count


    def _ply_substring_from(text: str, start: int) -> str:
        if start < 0 or start > len(text):
            raise IndexError("index out of range")
        return text[start:]


    def _ply_substring(text: str, start: int, length: int) -> str:
        if start < 0 or length < 0 or start + length > len(text):
            raise IndexError("index out of range")
        return text[start:start + length]


    def _ply_compare(left: object, right: object) -> int:
        if left < right:
            return -1
        return 1 if left > right else 0


    def _ply_index_of(text: str, needle: str, start: int) -> int:
        if start < 0 or start > len(text):
            raise IndexError("index out of range")
        return text.find(needle, start)


    def _ply_last_index_of(text: str, needle: str, start: int) -> int:
        if start < 0 or start > len(text):
            raise IndexError("index out of range")
        return text.rfind(needle, 0, start + len(needle))


    def _ply_replace_all(text: str, needle: str, replacement: str) -> str:
        if needle == "":
            raise ArithmeticError("invalid operation")
        return text.replace(needle, replacement)


    def _ply_split(text: str, separator: str, limit: int) -> list:
        parts = list(text) if separator == "" else text.split(separator)
        return parts if limit < 0 else parts[:limit]


    def _ply_trim_ecma(text: str, start: bool, end: bool) -> str:
        spaces = "\t\n\x0b\x0c\r \xa0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff"
        if start and end:
            return text.strip(spaces)
        return text.lstrip(spaces) if start else text.rstrip(spaces)


    def _ply_trim_net(text: str, start: bool, end: bool) -> str:
        spaces = "\t\n\x0b\x0c\r \x85\xa0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000"
        if start and end:
            return text.strip(spaces)
        return text.lstrip(spaces) if start else text.rstrip(spaces)


    def _ply_pad(text: str, length: int, filler: str, at_start: bool) -> str:
        if length <= len(text) or filler == "":
            return text
        count = length - len(text)
        padding = (filler * (count // len(filler) + 1))[:count]
        return padding + text if at_start else text + padding


    def _ply_next_code_point(text: str, index: int) -> int:
        if index < 0 or index >= len(text):
            raise IndexError("index out of range")
        if "\ud800" <= text[index] <= "\udbff" and index + 1 < len(text) and "\udc00" <= text[index + 1] <= "\udfff":
            return 2
        return 1


    def _ply_code_point_at(text: str, index: int) -> int:
        if _ply_next_code_point(text, index) == 2:
            return 0x10000 + (ord(text[index]) - 0xD800 << 10) + ord(text[index + 1]) - 0xDC00
        return ord(text[index])
    """;

}
