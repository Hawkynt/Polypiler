using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>
/// The values of literals as CPython makes them: numbers (<c>parsenumber</c> of <c>Parser/pegen.c</c>)
/// and strings with their escapes (<c>Parser/string_parser.c</c> and the <c>unicode_escape</c> and
/// bytes escape decoders). A string is a list of code points, as a Python <c>str</c> is.
/// </summary>
internal static class PyLiterals {

  /// <summary>The longest decimal integer CPython converts (<c>sys.get_int_max_str_digits()</c>).</summary>
  public const int MaxStrDigits = 4300;

  /// <summary>An error of a literal: CPython's <c>(unicode error) …</c> or <c>(value error) …</c>.</summary>
  public sealed class LiteralException(string message) : Exception(message);

  /// <summary>The value of a <c>NUMBER</c> token: a <see cref="BigInteger"/>, a <see cref="double"/> or a <see cref="PyComplex"/>.</summary>
  /// <exception cref="FormatException">A decimal integer of more than <see cref="MaxStrDigits"/> digits.</exception>
  public static object Number(string text) {
    var s = text.Replace("_", "", StringComparison.Ordinal);
    if (s[^1] is 'j' or 'J')
      return new PyComplex(0, Float(s[..^1]));

    if (s.Length > 1 && s[0] == '0' && s[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B') {
      var radix = s[1] is 'x' or 'X' ? 16 : s[1] is 'o' or 'O' ? 8 : 2;
      var value = BigInteger.Zero;
      foreach (var digit in s.AsSpan(2))
        value = value * radix + HexValue(digit);

      return value;
    }

    if (s.AsSpan().IndexOfAny('.', 'e', 'E') >= 0)
      return Float(s);

    if (s.Length > MaxStrDigits)
      throw new FormatException($"Exceeds the limit ({MaxStrDigits} digits) for integer string conversion: value has {s.Length} digits; use sys.set_int_max_str_digits() to increase the limit");

    return BigInteger.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
  }

  private static int HexValue(char digit) => digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10;

  private static double Float(string text) => double.Parse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);

  /// <summary>The value of a <c>STRING</c> token: its code points, or the bytes of a bytes literal.</summary>
  /// <exception cref="LiteralException">An escape CPython cannot decode.</exception>
  public static object String(string token, out bool nonAsciiBytes) {
    nonAsciiBytes = false;
    var i = 0;
    var bytes = false;
    var raw = false;
    while (token[i] is not ('\'' or '"')) {
      if (token[i] is 'b' or 'B')
        bytes = true;
      else if (token[i] is 'r' or 'R')
        raw = true;

      i++;
    }

    var quote = token[i];
    var body = token.Length - i >= 6 && token[i + 1] == quote && token[i + 2] == quote ? token[(i + 3)..^3] : token[(i + 1)..^1];
    raw = raw || !body.Contains('\\', StringComparison.Ordinal);
    if (bytes) {
      foreach (var c in body) {
        if (c >= 0x80) {
          nonAsciiBytes = true;
          return Array.Empty<byte>();
        }
      }

      return raw ? Encoding.ASCII.GetBytes(body) : DecodeBytes(body);
    }

    return raw ? PyUnicode.CodePoints(body) : DecodeText(body);
  }

  /// <summary>A part of an f-string or t-string decoded (<c>_PyPegen_decode_string</c>): as written when raw.</summary>
  public static List<int> Text(string text, bool raw) => raw || !text.Contains('\\', StringComparison.Ordinal) ? PyUnicode.CodePoints(text) : DecodeText(text);

  private static LiteralException Escape(int start, int end, string what) => new($"(unicode error) 'unicodeescape' codec can't decode bytes in position {start}-{end - 1}: {what}");

  /// <summary>The <c>unicode_escape</c> decoding of a text, which keeps every character outside ASCII as it is.</summary>
  private static List<int> DecodeText(string body) {
    var points = PyUnicode.CodePoints(body);
    // Where each code point is in the bytes CPython decodes: a character outside ASCII is written as a
    // \U escape of 10 bytes, a backslash before one or at the end as \u005c, of 6.
    var offsets = new int[points.Count + 1];
    for (var k = 0; k < points.Count; k++)
      offsets[k + 1] = offsets[k] + (points[k] >= 0x80 ? 10 : points[k] == '\\' && (k + 1 == points.Count || points[k + 1] >= 0x80) ? 6 : 1);

    var result = new List<int>(points.Count);
    for (var i = 0; i < points.Count; i++) {
      var c = points[i];
      if (c != '\\') {
        result.Add(c);
        continue;
      }

      if (i + 1 >= points.Count || points[i + 1] >= 0x80) {
        // A backslash at the end, or before a character outside ASCII, stays a backslash.
        result.Add('\\');
        continue;
      }

      var start = i;
      c = points[++i];
      switch (c) {
        case '\n':
          break;
        case '\\' or '\'' or '"':
          result.Add(c);
          break;
        case 'b':
          result.Add('\b');
          break;
        case 'f':
          result.Add('\f');
          break;
        case 't':
          result.Add('\t');
          break;
        case 'n':
          result.Add('\n');
          break;
        case 'r':
          result.Add('\r');
          break;
        case 'v':
          result.Add('\v');
          break;
        case 'a':
          result.Add('\a');
          break;
        case >= '0' and <= '7': {
          var value = c - '0';
          for (var k = 0; k < 2 && i + 1 < points.Count && points[i + 1] is >= '0' and <= '7'; k++)
            value = value * 8 + (points[++i] - '0');

          result.Add(value);
          break;
        }
        case 'x' or 'u' or 'U': {
          var count = c == 'x' ? 2 : c == 'u' ? 4 : 8;
          var value = 0L;
          for (var k = 0; k < count; k++) {
            if (i + 1 >= points.Count || !IsHex(points[i + 1]))
              throw Escape(offsets[start], offsets[i + 1], $"truncated {(c == 'x' ? "\\xXX" : c == 'u' ? "\\uXXXX" : "\\UXXXXXXXX")} escape");

            value = value * 16 + HexValue((char)points[++i]);
          }

          if (value > 0x10FFFF)
            throw Escape(offsets[start], offsets[i + 1], "illegal Unicode character");

          result.Add((int)value);
          break;
        }
        case 'N': {
          var close = -1;
          if (i + 1 < points.Count && points[i + 1] == '{')
            for (var k = i + 2; k < points.Count; k++)
              if (points[k] == '}') {
                close = k;
                break;
              }

          if (close < 0 || close == i + 2) {
            var at = i + 1 >= points.Count || points[i + 1] != '{' ? i + 1 : close < 0 ? points.Count : close;
            throw Escape(offsets[start], offsets[at], "malformed \\N character escape");
          }

          var name = PyUnicode.Text(points.GetRange(i + 2, close - i - 2));
          if (PyUnicode.Lookup(name) is not { } point)
            throw Escape(offsets[start], offsets[close + 1], "unknown Unicode character name");

          result.Add(point);
          i = close;
          break;
        }
        default:
          // An unknown escape keeps the backslash; CPython warns.
          result.Add('\\');
          result.Add(c);
          break;
      }
    }

    return result;
  }

  private static bool IsHex(int c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

  /// <summary>The bytes escape decoding (<c>_PyBytes_DecodeEscape2</c>).</summary>
  private static byte[] DecodeBytes(string body) {
    var result = new List<byte>(body.Length);
    for (var i = 0; i < body.Length; i++) {
      var c = body[i];
      if (c != '\\') {
        result.Add((byte)c);
        continue;
      }

      if (++i >= body.Length)
        throw new LiteralException("(value error) Trailing \\ in string");

      c = body[i];
      switch (c) {
        case '\n':
          break;
        case '\\' or '\'' or '"':
          result.Add((byte)c);
          break;
        case 'b':
          result.Add(8);
          break;
        case 'f':
          result.Add(12);
          break;
        case 't':
          result.Add(9);
          break;
        case 'n':
          result.Add(10);
          break;
        case 'r':
          result.Add(13);
          break;
        case 'v':
          result.Add(11);
          break;
        case 'a':
          result.Add(7);
          break;
        case >= '0' and <= '7': {
          var value = c - '0';
          for (var k = 0; k < 2 && i + 1 < body.Length && body[i + 1] is >= '0' and <= '7'; k++)
            value = value * 8 + (body[++i] - '0');

          result.Add((byte)value);
          break;
        }
        case 'x':
          if (i + 2 < body.Length + 0 && IsHex(body[i + 1]) && IsHex(body[i + 2])) {
            result.Add((byte)(HexValue(body[i + 1]) * 16 + HexValue(body[i + 2])));
            i += 2;
            break;
          }

          throw new LiteralException($"(value error) invalid \\x escape at position {i - 1}");
        default:
          result.Add((byte)'\\');
          result.Add((byte)c);
          break;
      }
    }

    return [.. result];
  }

}
