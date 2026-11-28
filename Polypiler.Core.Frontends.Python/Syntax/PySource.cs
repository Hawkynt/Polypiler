using System;
using System.Collections.Generic;
using System.Text;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>
/// The bytes the tokenizer reads, as CPython prepares them (<c>Parser/tokenizer/string_tokenizer.c</c>,
/// <c>helpers.c</c>): every <c>\r\n</c> and <c>\r</c> a <c>\n</c>, a final newline added, and for a
/// source given as bytes the UTF-8 BOM and the encoding declaration of PEP 263 on the first two lines
/// honoured, the text decoded and encoded as UTF-8 again.
/// </summary>
internal static class PySource {

  /// <summary>What a source decodes to.</summary>
  /// <param name="Utf8">The bytes for the tokenizer, or <see langword="null"/> where there is an error.</param>
  /// <param name="Error">Why the source cannot be read.</param>
  /// <param name="Undecided">Whether the source declares an encoding the frontend has no decoder for, so that it gives no verdict.</param>
  public sealed record Prepared(byte[]? Utf8, PyError? Error, bool Undecided = false);

  // Python's names of the encodings the frontend decodes, normalized as encodings.normalize_encoding does
  // (lower case, '-' and ' ' as '_'), with the name of the .NET encoding: those whose tables are the
  // same in both, every byte defined, and ASCII and cp1252, whose undefined bytes are checked here.
  private static readonly Dictionary<string, string> _encodings = new(StringComparer.Ordinal) {
    ["utf_8"] = "utf-8", ["utf8"] = "utf-8", ["u8"] = "utf-8", ["utf"] = "utf-8", ["cp65001"] = "utf-8",
    ["latin_1"] = "iso-8859-1", ["latin1"] = "iso-8859-1", ["iso_8859_1"] = "iso-8859-1", ["iso8859_1"] = "iso-8859-1", ["8859"] = "iso-8859-1",
    ["cp819"] = "iso-8859-1", ["l1"] = "iso-8859-1", ["latin"] = "iso-8859-1", ["iso_ir_100"] = "iso-8859-1", ["iso_8859_1:1987"] = "iso-8859-1",
    ["ascii"] = "us-ascii", ["us_ascii"] = "us-ascii", ["646"] = "us-ascii", ["us"] = "us-ascii", ["ansi_x3.4_1968"] = "us-ascii",
    ["cp1252"] = "windows-1252", ["windows_1252"] = "windows-1252",
    ["koi8_r"] = "koi8-r", ["koi8_u"] = "koi8-u",
    ["iso_8859_2"] = "iso-8859-2", ["iso8859_2"] = "iso-8859-2", ["latin2"] = "iso-8859-2", ["l2"] = "iso-8859-2",
    ["iso_8859_5"] = "iso-8859-5", ["iso8859_5"] = "iso-8859-5", ["cyrillic"] = "iso-8859-5",
    ["iso_8859_15"] = "iso-8859-15", ["iso8859_15"] = "iso-8859-15", ["latin9"] = "iso-8859-15", ["l9"] = "iso-8859-15"
  };

  private static readonly Lazy<bool> _codePages = new(() => {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    return true;
  });

  /// <summary>Prepares a source given as text, as <c>ast.parse(str)</c> does: its encoding declaration is ignored.</summary>
  public static Prepared FromText(string text) {
    if (PyUnicode.EncodeError(PyUnicode.CodePoints(text)) is { } surrogates)
      return new(null, new PyError("UnicodeEncodeError", surrogates, 1, 1, 1, 1));

    var bytes = Encoding.UTF8.GetBytes(text);
    return Array.IndexOf(bytes, (byte)0) >= 0 ? NullBytes() : new(TranslateNewlines(bytes), null);
  }

  private static Prepared NullBytes() => new(null, new PyError("SyntaxError", "source code string cannot contain null bytes", 1, 1, 1, 1));

  /// <summary>Prepares a source given as bytes, as <c>ast.parse(bytes)</c> does.</summary>
  public static Prepared FromBytes(byte[] input) {
    if (Array.IndexOf(input, (byte)0) >= 0)
      return NullBytes();

    var translated = TranslateNewlines(input);
    var offset = 0;
    var bom = false;
    if (translated.Length >= 3 && translated[0] == 0xEF && translated[1] == 0xBB && translated[2] == 0xBF) {
      offset = 3;
      bom = true;
    }

    var body = translated.AsSpan(offset);
    var firstEnd = body.IndexOf((byte)'\n');
    string? declared = null;
    if (firstEnd >= 0) {
      (declared, var normal) = CodingSpec(body[..(firstEnd + 1)]);
      if (declared is null && !normal) {
        var rest = body[(firstEnd + 1)..];
        var secondEnd = rest.IndexOf((byte)'\n');
        if (secondEnd >= 0) {
          (declared, _) = CodingSpec(rest[..(secondEnd + 1)]);
        }
      }
    }

    if (declared is not null && bom && declared != "utf-8")
      return new(null, new PyError("SyntaxError", $"encoding problem: {declared} with BOM", 1, 1, 1, 1));

    if (declared is null or "utf-8")
      return EnsureUtf8(body) is { } invalid ? new(null, invalid) : new(body.ToArray(), null);

    var normalized = declared.ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
    if (!_encodings.TryGetValue(normalized, out var dotnet))
      return new(null, new PyError("SyntaxError", $"the encoding {declared} is not supported by the Python frontend", 1, 1, 1, 1), true);

    // The error of a byte the codec does not define is the codec's own message, without a position (raise_init_error).
    for (var i = 0; i < body.Length; i++) {
      var undefined = dotnet switch {
        "us-ascii" => body[i] >= 0x80 ? "ordinal not in range(128)" : null,
        "windows-1252" => body[i] is 0x81 or 0x8D or 0x8F or 0x90 or 0x9D ? "character maps to <undefined>" : null,
        _ => null
      };
      if (undefined is not null)
        return new(null, new PyError("SyntaxError", $"'{(dotnet == "us-ascii" ? "ascii" : "charmap")}' codec can't decode byte 0x{body[i]:x2} in position {i}: {undefined}", 0, -1, 0, -1));
    }

    _ = _codePages.Value;
    var text = Encoding.GetEncoding(dotnet).GetString(body);

    // The input has no null byte and its newlines are translated already; these codecs keep both so.
    return new(Encoding.UTF8.GetBytes(text), null);
  }


  /// <summary>The encoding a line declares (<c>get_coding_spec</c>, <c>check_coding_spec</c>), and whether the line ends the search.</summary>
  private static (string? Name, bool Normal) CodingSpec(ReadOnlySpan<byte> line) {
    var size = line.Length;
    var i = 0;
    for (; i < size - 6; i++) {
      if (line[i] == '#')
        break;

      if (line[i] is not ((byte)' ' or (byte)'\t' or (byte)'\f'))
        return (null, true);
    }

    for (; i < size - 6; i++) {
      if (!line[i..].StartsWith("coding"u8))
        continue;

      var t = i + 6;
      if (line[t] is not ((byte)':' or (byte)'='))
        continue;

      do {
        t++;
      } while (t < size && line[t] is (byte)' ' or (byte)'\t');

      var begin = t;
      while (t < size && (char.IsAsciiLetterOrDigit((char)line[t]) || line[t] is (byte)'-' or (byte)'_' or (byte)'.'))
        t++;

      if (begin < t)
        return (NormalName(Encoding.ASCII.GetString(line[begin..t])), true);
    }

    for (var j = 0; j < size; j++) {
      if (line[j] is (byte)'#' or (byte)'\n')
        break;

      if (line[j] is not ((byte)' ' or (byte)'\t' or (byte)'\f'))
        return (null, true);
    }

    return (null, false);
  }

  /// <summary>The name of the encoding as <c>get_normal_name</c> gives it: <c>utf-8</c> and <c>iso-8859-1</c> for their variants.</summary>
  private static string NormalName(string name) {
    var buffer = new StringBuilder();
    for (var i = 0; i < 12 && i < name.Length; i++)
      buffer.Append(name[i] == '_' ? '-' : char.ToLowerInvariant(name[i]));

    var b = buffer.ToString();
    if (b == "utf-8" || b.StartsWith("utf-8-", StringComparison.Ordinal))
      return "utf-8";

    if (b is "latin-1" or "iso-8859-1" or "iso-latin-1" || b.StartsWith("latin-1-", StringComparison.Ordinal)
      || b.StartsWith("iso-8859-1-", StringComparison.Ordinal) || b.StartsWith("iso-latin-1-", StringComparison.Ordinal))
      return "iso-8859-1";

    return name;
  }

  /// <summary>The error for bytes that are no UTF-8, as <c>_PyTokenizer_ensure_utf8</c> reports them; <see langword="null"/> for valid UTF-8.</summary>
  private static PyError? EnsureUtf8(ReadOnlySpan<byte> text) {
    var line = 1;
    var lineStart = 0;
    for (var i = 0; i < text.Length;) {
      var length = ValidUtf8(text, i);
      if (length == 0)
        return new PyError("SyntaxError",
          $"Non-UTF-8 code starting with '\\x{text[i]:x2}' on line {line}, but no encoding declared; see https://peps.python.org/pep-0263/ for details",
          line, i - lineStart + 1, line, i - lineStart + 1);

      if (text[i] == '\n') {
        line++;
        lineStart = i + 1;
      }

      i += length;
    }

    return null;
  }

  private static int ValidUtf8(ReadOnlySpan<byte> s, int at) {
    static int At(ReadOnlySpan<byte> s, int at, int k) => at + k < s.Length ? s[at + k] : 0;
    var first = s[at];
    int expected;
    if (first < 0x80)
      return 1;

    if (first < 0xE0) {
      if (first < 0xC2)
        return 0;

      expected = 1;
    } else if (first < 0xF0) {
      if ((first == 0xE0 && At(s, at, 1) < 0xA0) || (first == 0xED && At(s, at, 1) >= 0xA0))
        return 0;

      expected = 2;
    } else if (first < 0xF5) {
      if (At(s, at, 1) < 0x90 ? first == 0xF0 : first == 0xF4)
        return 0;

      expected = 3;
    } else {
      return 0;
    }

    for (var k = 1; k <= expected; k++)
      if (At(s, at, k) is < 0x80 or >= 0xC0)
        return 0;

    return expected + 1;
  }

  /// <summary><c>\r\n</c> and <c>\r</c> as <c>\n</c>, and a final <c>\n</c> where the source has none (<c>_PyTokenizer_translate_newlines</c>).</summary>
  public static byte[] TranslateNewlines(ReadOnlySpan<byte> input) {
    var output = new List<byte>(input.Length + 1);
    for (var i = 0; i < input.Length; i++) {
      var c = input[i];
      if (c == '\r') {
        output.Add((byte)'\n');
        if (i + 1 < input.Length && input[i + 1] == '\n')
          i++;

        continue;
      }

      output.Add(c);
    }

    if (output.Count > 0 && output[^1] != '\n')
      output.Add((byte)'\n');

    return [.. output];
  }

}
