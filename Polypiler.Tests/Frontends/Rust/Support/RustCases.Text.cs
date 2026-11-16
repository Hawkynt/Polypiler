using System.Collections.Generic;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The calls of the fixture of text (<c>strings</c>): ASCII, two-, three- and four-byte characters
/// (<c>é</c>, <c>€</c>, an emoji that is a surrogate pair in UTF-16), byte ranges on and off a char
/// boundary and past the end, the code points where UTF-8 order and UTF-16 order part, Rust's
/// whitespace against ECMAScript's, number texts at the limits of their types and malformed ones.
/// </summary>
internal static partial class RustCases {

  private const string Mixed = "aé€😀";

  private static IReadOnlyList<PrototypeCall> Text() {
    var calls = new List<PrototypeCall>();
    foreach (var name in new[] { "utf8_len", "char_count", "byte_sum", "code_point_sum", "reversed" })
      foreach (var text in new[] { "", "a", Mixed, "😀😀" })
        calls.Add(Call(name, text));

    foreach (var at in new ulong[] { 0, 1, 9, 10 })
      calls.Add(Call("byte_at", Mixed, at));

    foreach (var (from, to) in new (ulong, ulong)[] { (0, 1), (1, 3), (3, 6), (6, 10), (0, 10), (2, 3), (0, 11), (3, 1), (10, 10) })
      calls.Add(Call("sliced", Mixed, from, to));

    foreach (var from in new ulong[] { 0, 6, 10, 7, 11 })
      calls.Add(Call("tail_chars", Mixed, from));

    foreach (var (text, code) in new (string, uint)[] { ("aé€😀aa", 'a'), ("aé€😀aa", 0x1F600), ("x", 0xD800), ("", 'a') })
      calls.Add(Call("count_char", text, code));

    foreach (var text in new[] { "abc é", "", "MiXeD 9" })
      calls.Add(Call("shout", text));

    foreach (var (a, b) in new (string, string)[] { ("a", "b"), ("", "😀"), ("x", "") })
      calls.Add(Call("joined", a, b));

    foreach (var (a, b) in new (string, string)[] { ("a", "b"), ("b", "a"), ("a", "a"), ("", "a"), ("ab", "a"), ("", "😀"), ("😀", "\uFFFF") })
      calls.Add(Call("compare", a, b));

    foreach (var (a, b) in new (string, string)[] { ("a", "a"), ("a", "b"), ("", ""), ("é", "e") })
      calls.Add(Call("same", a, b));

    foreach (var (haystack, needle) in new (string, string)[] { (Mixed + "x", "😀"), ("abc", ""), ("abc", "d"), (Mixed + "x", "x"), ("", "a"), ("aaa", "aa") })
      calls.Add(Call("find_at", haystack, needle));

    foreach (var (text, part) in new (string, string)[] { ("abcab", "ab"), ("abc", "bc"), ("abc", "x"), ("", ""), ("ab", "abc") })
      calls.Add(Call("affixes", text, part));

    foreach (var (x, y) in new (int, byte)[] { (-5, 255), (0, 0), (int.MinValue, 1), (int.MaxValue, 16) })
      calls.Add(Call("numbers_text", x, y));

    foreach (var width in new ulong[] { 10, 0, 7, ulong.MaxValue })
      calls.Add(Call("named_text", width));

    foreach (var text in new[] { "255", "256", "-1", "+7", " 7", "", "0x10", "007", "+", "٣" })
      calls.Add(Call("parse_u8", text));

    foreach (var text in new[] { "-9223372036854775808", "9223372036854775807", "9223372036854775808", "-", "12a", "-0" })
      calls.Add(Call("parse_i64", text));

    foreach (var (a, b) in new (string, string)[] { ("1", "2"), ("x", "2"), ("4294967295", "1") })
      calls.Add(Call("parse_sum", a, b));

    foreach (var n in new ulong[] { 0, 9876543210, ulong.MaxValue })
      calls.Add(Call("digit_sum", n));

    foreach (var (x, flag, code) in new (long, bool, uint)[] { (-42, true, 0x1F600), (0, false, 0xD800), (long.MinValue, true, 'é') })
      calls.Add(Call("described", x, flag, code));

    foreach (var code in new uint[] { 'q', 'Q', '5', ' ', '\n', 0xE9, 0x110000, 0xD800, 0x3000 })
      calls.Add(Call("classify", code));

    foreach (var code in new uint[] { 0x41, 0x7F, 0x80, 0x7FF, 0x800, 0xFFFF, 0x10000, 0x10FFFF, 0xD800, 0x110000 })
      calls.Add(Call("char_width", code));

    foreach (var text in new[] { "fn", "let", "const", "x", "", "fnn" })
      calls.Add(Call("keyword", text));

    foreach (var text in new[] { " \t pad \n", "\u3000x\u0085", "\uFEFFa", "", "   ", "\u00A0a b\u2029" })
      calls.Add(Call("trimmed", text));

    foreach (var (text, n) in new (string, ulong)[] { ("ab", 3), ("é", 0), ("", 5), ("😀", 2) })
      calls.Add(Call("repeated", text, n));

    foreach (var text in new[] { "a,b,,c", "", ",", "abc" })
      calls.Add(Call("field_count", text));

    foreach (var text in new[] { "a, bcd, ef", "", "😀😀, a", ", , " })
      calls.Add(Call("longest_field", text));

    foreach (var text in new[] { "x", "" })
      calls.Add(Call("is_empty_after_clear", text));

    return calls;
  }

}
