using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>
/// The Unicode properties CPython's tokenizer and string decoder use, from the Unicode Character
/// Database 16.0.0 that CPython 3.14 carries: XID_Start and XID_Continue for identifiers (PEP 3131),
/// NFKC for their normalization, and the character names of <c>\N{...}</c>. The tables are generated
/// from the UCD by <c>Unicode/generate.py</c>; the runtime's own normalization is not used, because an
/// invariant-globalization process leaves non-ASCII text unnormalized.
/// </summary>
internal static class PyUnicode {

  private const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7;
  private const int LCount = 19, VCount = 21, TCount = 28, NCount = VCount * TCount, SCount = LCount * NCount;

  // The short names of the Hangul jamo, column by column (leading, vowel, trailing), as unicodedata.c has them.
  private static readonly string[] _leading = ["G", "GG", "N", "D", "DD", "R", "M", "B", "BB", "S", "SS", "", "J", "JJ", "C", "K", "T", "P", "H"];
  private static readonly string[] _vowels = ["A", "AE", "YA", "YAE", "EO", "E", "YEO", "YE", "O", "WA", "WAE", "OE", "YO", "U", "WEO", "WE", "WI", "YU", "EU", "YI", "I"];
  private static readonly string[] _trailing = ["", "G", "GG", "GS", "N", "NJ", "NH", "D", "L", "LG", "LM", "LB", "LS", "LT", "LP", "LH", "M", "B", "BS", "S", "SS", "NG", "J", "C", "K", "T", "P", "H"];

  // The prefixes CPython derives a name from the code point for, besides the Hangul syllables.
  private static readonly string[] _derivedPrefixes = ["CJK UNIFIED IDEOGRAPH-", "TANGUT IDEOGRAPH-"];

  private static readonly Lazy<Properties> _properties = new(LoadProperties);
  private static readonly Lazy<Names> _names = new(LoadNames);

  private sealed class Properties {
    public readonly List<(int Low, int High)> Start = [];
    public readonly List<(int Low, int High)> Continue = [];
    public readonly List<(int Low, int High)> Excluded = [];
    public readonly Dictionary<int, byte> Combining = [];
    public readonly Dictionary<int, (bool Compatibility, int[] Mapping)> Decompositions = [];
    public readonly Dictionary<(int, int), int> Compositions = [];
  }

  private sealed class Names {
    public readonly Dictionary<string, int> Exact = new(StringComparer.Ordinal);
    public readonly List<(int Low, int High, string Prefix)> Patterns = [];
  }

  private static IEnumerable<string> Lines(string resource) {
    using var stream = typeof(PyUnicode).Assembly.GetManifestResourceStream("Polypiler.Core.Frontends.Python.Unicode." + resource)!;
    using var unzipped = new GZipStream(stream, CompressionMode.Decompress);
    using var reader = new StreamReader(unzipped, Encoding.UTF8);
    while (reader.ReadLine() is { } line)
      yield return line;
  }

  private static int Hex(string text) => int.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

  private static Properties LoadProperties() {
    var properties = new Properties();
    foreach (var line in Lines("properties.txt.gz")) {
      var parts = line.Split(' ');
      switch (parts[0]) {
        case "S":
          properties.Start.Add((Hex(parts[1]), Hex(parts[2])));
          break;
        case "C":
          properties.Continue.Add((Hex(parts[1]), Hex(parts[2])));
          break;
        case "X":
          properties.Excluded.Add((Hex(parts[1]), Hex(parts[2])));
          break;
        default:
          var point = Hex(parts[1]);
          var combining = byte.Parse(parts[2], CultureInfo.InvariantCulture);
          if (combining != 0)
            properties.Combining[point] = combining;

          if (parts[4] != "-")
            properties.Decompositions[point] = (parts[3] == "K", Array.ConvertAll(parts[4].Split(','), Hex));

          break;
      }
    }

    foreach (var (point, (compatibility, mapping)) in properties.Decompositions)
      if (!compatibility && mapping.Length == 2 && !In(properties.Excluded, point))
        properties.Compositions[(mapping[0], mapping[1])] = point;

    return properties;
  }

  private static Names LoadNames() {
    var names = new Names();
    foreach (var line in Lines("names.txt.gz")) {
      if (line.StartsWith("P ", StringComparison.Ordinal)) {
        var parts = line.Split(' ', 4);
        names.Patterns.Add((Hex(parts[1]), Hex(parts[2]), parts[3]));
        continue;
      }

      var space = line.IndexOf(' ');
      names.Exact.TryAdd(line[(space + 1)..], Hex(line[..space]));
    }

    return names;
  }

  private static bool In(List<(int Low, int High)> ranges, int point) {
    var (low, high) = (0, ranges.Count - 1);
    while (low <= high) {
      var middle = (low + high) / 2;
      if (point < ranges[middle].Low)
        high = middle - 1;
      else if (point > ranges[middle].High)
        low = middle + 1;
      else
        return true;
    }

    return false;
  }

  /// <summary>Whether a code point may start an identifier: XID_Start or the underscore.</summary>
  public static bool IsXidStart(int point) => point == '_' || (point < 128 ? char.IsAsciiLetter((char)point) : In(_properties.Value.Start, point));

  /// <summary>Whether a code point may continue an identifier: XID_Continue.</summary>
  public static bool IsXidContinue(int point) => point < 128 ? point == '_' || char.IsAsciiLetterOrDigit((char)point) : In(_properties.Value.Continue, point);

  /// <summary>
  /// Whether a code point is printable as <c>str.isprintable</c> has it: not of the categories Cc, Cf,
  /// Cs, Co, Cn, Zl, Zp, and of Zs only the space. CPython's messages choose their wording by it.
  /// </summary>
  public static bool IsPrintable(int point) {
    if (point == ' ')
      return true;

    var category = point is >= 0xD800 and <= 0xDFFF ? UnicodeCategory.Surrogate : CharUnicodeInfo.GetUnicodeCategory(point);
    return category is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
      or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator);
  }

  /// <summary>The code points of a text; a lone surrogate is a code point of its own.</summary>
  public static List<int> CodePoints(string text) {
    var points = new List<int>(text.Length);
    for (var i = 0; i < text.Length; i++) {
      if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
        points.Add(char.ConvertToUtf32(text[i], text[i + 1]));
        i++;
      } else {
        points.Add(text[i]);
      }
    }

    return points;
  }

  /// <summary>
  /// The message of the <c>UnicodeEncodeError</c> encoding a text with a lone surrogate as UTF-8 raises,
  /// which names the first run of surrogates; <see langword="null"/> for a text without one.
  /// </summary>
  public static string? EncodeError(IReadOnlyList<int> points) {
    static bool Surrogate(int point) => point is >= 0xD800 and <= 0xDFFF;
    var first = -1;
    for (var i = 0; i < points.Count && first < 0; i++)
      if (Surrogate(points[i]))
        first = i;

    if (first < 0)
      return null;

    var last = first;
    while (last + 1 < points.Count && Surrogate(points[last + 1]))
      last++;

    return first == last
      ? $"'utf-8' codec can't encode character '\\u{points[first]:x4}' in position {first}: surrogates not allowed"
      : $"'utf-8' codec can't encode characters in position {first}-{last}: surrogates not allowed";
  }

  /// <summary>The text of code points.</summary>
  public static string Text(IEnumerable<int> points) {
    var text = new StringBuilder();
    foreach (var point in points)
      text.Append(point is >= 0xD800 and <= 0xDFFF ? ((char)point).ToString() : char.ConvertFromUtf32(point));

    return text.ToString();
  }

  /// <summary>The NFKC normalization of a text, as <c>unicodedata.normalize("NFKC", text)</c> gives it.</summary>
  public static string Nfkc(string text) {
    var properties = _properties.Value;
    var decomposed = new List<int>();
    foreach (var point in CodePoints(text))
      Decompose(point, properties, decomposed);

    // Canonical ordering: a stable sort of every run of non-starters by their combining class.
    for (var i = 1; i < decomposed.Count; i++) {
      var current = decomposed[i];
      var combining = Combining(properties, current);
      if (combining == 0)
        continue;

      var j = i;
      while (j > 0 && Combining(properties, decomposed[j - 1]) > combining) {
        decomposed[j] = decomposed[j - 1];
        j--;
      }

      decomposed[j] = current;
    }

    return Text(Compose(decomposed, properties));
  }

  private static int Combining(Properties properties, int point) => properties.Combining.TryGetValue(point, out var combining) ? combining : 0;

  private static void Decompose(int point, Properties properties, List<int> into) {
    if (point is >= SBase and < SBase + SCount) {
      var index = point - SBase;
      into.Add(LBase + index / NCount);
      into.Add(VBase + index % NCount / TCount);
      if (index % TCount != 0)
        into.Add(TBase + index % TCount);

      return;
    }

    if (properties.Decompositions.TryGetValue(point, out var decomposition)) {
      foreach (var part in decomposition.Mapping)
        Decompose(part, properties, into);

      return;
    }

    into.Add(point);
  }

  private static List<int> Compose(List<int> points, Properties properties) {
    var result = new List<int>(points.Count);
    // The last starter, and the combining class of the last character after it (-1: none, it is adjacent).
    var starter = -1;
    var last = -1;
    foreach (var point in points) {
      var combining = Combining(properties, point);
      if (starter >= 0 && (last == -1 || last < combining) && Composite(result[starter], point, properties) is { } composite) {
        result[starter] = composite;
        continue;
      }

      if (combining == 0) {
        starter = result.Count;
        last = -1;
      } else {
        last = combining;
      }

      result.Add(point);
    }

    return result;
  }

  private static int? Composite(int first, int second, Properties properties) {
    if (first is >= LBase and < LBase + LCount && second is >= VBase and < VBase + VCount)
      return SBase + ((first - LBase) * VCount + (second - VBase)) * TCount;

    if (first is >= SBase and < SBase + SCount && (first - SBase) % TCount == 0 && second is > TBase and < TBase + TCount)
      return first + (second - TBase);

    return properties.Compositions.TryGetValue((first, second), out var composite) ? composite : null;
  }

  /// <summary>
  /// The code point a name of <c>\N{...}</c> names, as CPython looks it up: case-insensitively among the
  /// names and aliases of the UCD, the Hangul syllables and the ideographs whose name is their code
  /// point; a named sequence is no character. <see langword="null"/> for no such name.
  /// </summary>
  public static int? Lookup(string name) {
    if (name.Length > 256)
      return null;

    var upper = name.ToUpperInvariant();
    if (upper.StartsWith("HANGUL SYLLABLE ", StringComparison.Ordinal)) {
      var position = 16;
      var leading = Syllable(upper, ref position, _leading);
      var vowel = Syllable(upper, ref position, _vowels);
      var trailing = Syllable(upper, ref position, _trailing);
      return leading >= 0 && vowel >= 0 && trailing >= 0 && position == upper.Length ? SBase + (leading * VCount + vowel) * TCount + trailing : null;
    }

    var names = _names.Value;
    foreach (var prefix in _derivedPrefixes) {
      if (!upper.StartsWith(prefix, StringComparison.Ordinal))
        continue;

      var digits = upper[prefix.Length..];
      if (digits.Length is < 4 or > 6 || digits[0] == '0' || !int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var point) || point > 0x10FFFF)
        return null;

      foreach (var (low, high, pattern) in names.Patterns)
        if (pattern == prefix && point >= low && point <= high)
          return point;

      return null;
    }

    if (names.Exact.TryGetValue(upper, out var exact))
      return exact;

    // The other names derived from the code point (Egyptian hieroglyphs, Khitan, Nushu, CJK compatibility
    // ideographs) CPython keeps as explicit names, which are their pattern with the code point in hexadecimal.
    foreach (var (low, high, pattern) in names.Patterns) {
      if (Array.IndexOf(_derivedPrefixes, pattern) >= 0 || !upper.StartsWith(pattern, StringComparison.Ordinal))
        continue;

      var digits = upper[pattern.Length..];
      if (int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var point) && point >= low && point <= high
        && digits == point.ToString("X4", CultureInfo.InvariantCulture))
        return point;
    }

    return null;
  }

  private static int Syllable(string name, ref int position, string[] column) {
    var (found, length) = (-1, -1);
    for (var i = 0; i < column.Length; i++) {
      var part = column[i];
      if (part.Length > length && string.CompareOrdinal(name, position, part, 0, part.Length) == 0 && position + part.Length <= name.Length) {
        length = part.Length;
        found = i;
      }
    }

    position += Math.Max(length, 0);
    return found;
  }

}
