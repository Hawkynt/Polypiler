using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Polypiler.Tooling.Licensing;

/// <summary>
/// The reference licence texts of <c>tooling/licensing/texts/</c>, each file named by the SPDX
/// identifier it is the text of, in any sub-folder: <c>spdx/MIT.txt</c>, <c>gnu.org/LGPL-3.0-only.txt</c>.
/// A text is known by its <see cref="Fingerprint"/>, so one identifier may have several texts (the
/// SPDX list's and the publisher's own wording), and several identifiers one text (the <c>-only</c>
/// and <c>-or-later</c> variants of a GNU licence share it).
/// </summary>
public sealed class LicenceTexts {

  private static readonly Regex _notice = new(@"^\s*(copyright\s*(\(c\)|©|\d)|\(c\)\s*\d|©)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
  private static readonly Regex _whitespace = new(@"\s+", RegexOptions.CultureInvariant);

  private readonly Dictionary<string, IReadOnlyList<string>> _identifiers;

  /// <summary>Creates the texts from pairs of an identifier and its text.</summary>
  public LicenceTexts(IEnumerable<(string Licence, string Text)> texts)
    => this._identifiers = texts
      .GroupBy(text => Fingerprint(text.Text), text => text.Licence)
      .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)[.. group.Distinct().Order(StringComparer.Ordinal)], StringComparer.Ordinal);

  /// <summary>The number of distinct texts.</summary>
  public int Count => this._identifiers.Count;

  /// <summary>Reads every <c>*.txt</c> below a folder; a folder that does not exist holds no texts.</summary>
  public static LicenceTexts Load(string folder)
    => new(Directory.Exists(folder)
      ? Directory.EnumerateFiles(folder, "*.txt", SearchOption.AllDirectories).Select(file => (Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)))
      : []);

  /// <summary>
  /// The SHA-256, as lower-case hexadecimal digits, of a licence text after its copyright notice lines
  /// (a line that starts with <c>Copyright (c)</c>, <c>Copyright ©</c>, <c>Copyright</c> and a digit,
  /// <c>(c)</c> and a digit, or <c>©</c>) are dropped, typographic quotation marks are made straight,
  /// every run of whitespace is collapsed to one space and the ends are trimmed. Dropping the notice
  /// lets one reference text stand for every holder's copy of a licence; the line ends, the wrapping
  /// and the quotation marks a copy happens to use do not change what it grants.
  /// </summary>
  public static string Fingerprint(string text) {
    var kept = text.TrimStart((char)0xFEFF).Split('\n').Where(line => !_notice.IsMatch(line));
    var straight = string.Join(' ', kept).Replace((char)0x201C, '"').Replace((char)0x201D, '"').Replace((char)0x2018, '\'').Replace((char)0x2019, '\'');
    return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_whitespace.Replace(straight, " ").Trim())));
  }

  /// <summary>The identifiers whose text a licence file holds, in ordinal order, or <see langword="null"/> for an unknown text.</summary>
  public IReadOnlyList<string>? Identify(string text) => this._identifiers.GetValueOrDefault(Fingerprint(text));

  /// <summary>
  /// What a set of identifiers sharing one text is called: the identifier itself when there is one,
  /// the family when they are the <c>-only</c> and <c>-or-later</c> variants of it (<c>LGPL-3.0</c>),
  /// otherwise the identifiers joined by <c>|</c>.
  /// </summary>
  public static string Family(IReadOnlyList<string> licences) {
    if (licences.Count == 1)
      return licences[0];

    var families = licences.Select(licence => licence.EndsWith("-only", StringComparison.Ordinal) ? licence[..^5]
      : licence.EndsWith("-or-later", StringComparison.Ordinal) ? licence[..^9] : licence).Distinct().ToList();
    return families.Count == 1 ? families[0] : string.Join(" | ", licences);
  }

}
