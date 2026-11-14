using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Polypiler.Tooling.Licensing;

/// <summary>Which rule determined the licence of a file.</summary>
public enum LicenceSource {

  /// <summary>An <c>SPDX-License-Identifier</c> line of the file.</summary>
  Header,

  /// <summary>An override of the policy.</summary>
  Override,

  /// <summary>The nearest licence file.</summary>
  LicenceFile

}

/// <summary>What the detector found for one file.</summary>
/// <param name="Licence">The licence expression, the family of an ambiguous licence text, or <see langword="null"/>.</param>
/// <param name="Source">The rule that determined it, or <see langword="null"/> when none did.</param>
/// <param name="Undetermined">Why no licence could be determined, or <see langword="null"/>.</param>
/// <param name="Disagreement">How the declared licence disagrees with the licence file, or <see langword="null"/>.</param>
public sealed record LicenceDetection(string? Licence, LicenceSource? Source, string? Undetermined = null, string? Disagreement = null) {

  /// <summary>The expression, when the licence was determined and parses.</summary>
  public SpdxExpression? Expression { get; init; }

}

/// <summary>
/// Determines the licence of one file; the first rule that yields a result wins:
/// <list type="number">
/// <item>an <c>SPDX-License-Identifier: &lt;expression&gt;</c> within the first <see cref="HeaderLines"/>
/// lines, alone on its line after an optional comment marker (a closing <c>*/</c>, <c>--&gt;</c> or <c>*)</c> is not part of the expression);</item>
/// <item>an override of the policy whose glob matches the file;</item>
/// <item>the nearest licence file (<see cref="LicenceFileNames"/>, the first of them present in a
/// folder), walking up to the scan root, identified by <see cref="LicenceTexts"/>. When its text is
/// shared by several identifiers, the declared licence selects one and must be one of them; with
/// nothing declared the family is reported and the licence is undetermined, since the
/// <c>-or-later</c> variant cannot be read from a text.</item>
/// </list>
/// </summary>
/// <param name="policy">The overrides.</param>
/// <param name="texts">The reference licence texts.</param>
public sealed class LicenceDetector(LicencePolicy policy, LicenceTexts texts) {

  /// <summary>How many lines from the top of a file a header is looked for in.</summary>
  public const int HeaderLines = 30;

  /// <summary>How many bytes from the top of a file a header is looked for in, so that a large binary file is not read whole.</summary>
  public const int HeaderBytes = 64 * 1024;

  /// <summary>
  /// The names of licence files, matched without regard to case, in the order a folder's own are
  /// preferred: <c>COPYING.LESSER</c> comes before <c>COPYING</c> because a project under the LGPL keeps
  /// the GPL it builds on in <c>COPYING</c>.
  /// </summary>
  public static IReadOnlyList<string> LicenceFileNames { get; } = ["LICENSE", "LICENSE.txt", "LICENSE.md", "COPYING.LESSER", "COPYING"];

  /// <summary>
  /// A header line: optionally a comment marker, then the tag and the expression to the end of the
  /// line. Nothing else may precede the tag, so a line that merely mentions it — documentation, a test
  /// case, a string literal — is no header.
  /// </summary>
  private static readonly Regex _header = new(@"^\s*(?:(?://+|#+|'|;+|/\*+|\*|[Rr][Ee][Mm]\b|<!--|--|%+|\(\*|::|!)\s*)?SPDX-License-Identifier:(.*)$", RegexOptions.CultureInvariant);

  private readonly Dictionary<string, string?> _licenceFiles = new(StringComparer.Ordinal);
  private readonly Dictionary<string, (IReadOnlyList<string>? Licences, string? Problem)> _identified = new(StringComparer.Ordinal);

  /// <summary>The expression of the first <c>SPDX-License-Identifier</c> line within the first <see cref="HeaderLines"/> lines, or <see langword="null"/>.</summary>
  public static string? Header(string text) {
    var lines = text.Split('\n');
    foreach (var line in lines.Take(HeaderLines)) {
      var match = _header.Match(line.TrimEnd('\r'));
      if (!match.Success)
        continue;

      var expression = match.Groups[1].Value.Trim();
      foreach (var end in (string[])["*/", "-->", "*)"])
        if (expression.EndsWith(end, StringComparison.Ordinal))
          expression = expression[..^end.Length].TrimEnd();
      return expression;
    }

    return null;
  }

  /// <summary>Whether a folder name is a licence file's.</summary>
  public static bool IsLicenceFile(string name) => LicenceFileNames.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));

  /// <summary>The licence file a folder holds itself, by <see cref="LicenceFileNames"/> preference, or <see langword="null"/>.</summary>
  public string? LicenceFileOf(string folder) {
    if (this._licenceFiles.TryGetValue(folder, out var cached))
      return cached;

    var names = Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().ToList();
    var found = LicenceFileNames
      .Select(candidate => names.Where(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).FirstOrDefault())
      .FirstOrDefault(name => name is not null);
    return this._licenceFiles[folder] = found is null ? null : Path.Combine(folder, found);
  }

  /// <summary>Determines the licence of a file.</summary>
  /// <param name="root">The scan root; the walk for a licence file stops there.</param>
  /// <param name="file">The file's full path, below <paramref name="root"/>.</param>
  /// <param name="policyPath">The path overrides are matched against.</param>
  /// <param name="declared">The licence declared for the file, as an SPDX expression, or <see langword="null"/>.</param>
  public LicenceDetection Detect(string root, string file, string policyPath, string? declared) {
    string head;
    try {
      head = ReadHead(file);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return new(null, null, $"the file cannot be read: {exception.Message}");
    }

    if (Header(head) is { } header)
      return Parsed(header, LicenceSource.Header);
    if (policy.Override(policyPath) is { } entry)
      return Parsed(entry.Licence, LicenceSource.Override);
    return this.FromLicenceFile(root, file, declared);
  }

  private static LicenceDetection Parsed(string licence, LicenceSource source) {
    try {
      return new(licence, source) { Expression = SpdxExpression.Parse(licence) };
    } catch (FormatException exception) {
      return new(licence, source, $"'{licence}' is not an SPDX expression: {exception.Message}");
    }
  }

  private static string ReadHead(string file) {
    using var stream = File.OpenRead(file);
    var buffer = new byte[HeaderBytes];
    var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
    return Encoding.UTF8.GetString(buffer, 0, length);
  }

  private LicenceDetection FromLicenceFile(string root, string file, string? declared) {
    var top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
    for (var folder = Path.GetDirectoryName(Path.GetFullPath(file)); folder is not null; folder = Path.GetDirectoryName(folder)) {
      if (this.LicenceFileOf(folder) is { } licenceFile)
        return this.Identified(top, licenceFile, declared);
      if (folder.Length <= top.Length)
        break;
    }

    return new(null, null);
  }

  private LicenceDetection Identified(string top, string licenceFile, string? declared) {
    var name = Path.GetRelativePath(top, licenceFile).Replace(Path.DirectorySeparatorChar, '/');
    if (!this._identified.TryGetValue(licenceFile, out var identified)) {
      try {
        var licences = texts.Identify(File.ReadAllText(licenceFile));
        identified = (licences, licences is null ? $"the text of '{name}' is not a known licence text" : null);
      } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
        identified = (null, $"'{name}' cannot be read: {exception.Message}");
      }

      this._identified[licenceFile] = identified;
    }

    if (identified.Licences is not { } candidates)
      return new(null, null, identified.Problem);

    var family = LicenceTexts.Family(candidates);
    if (declared is null)
      return candidates.Count == 1
        ? Parsed(candidates[0], LicenceSource.LicenceFile)
        : new(family, LicenceSource.LicenceFile, $"'{name}' holds the {family} text and nothing declares which of {string.Join(", ", candidates)} applies");

    IEnumerable<string> named;
    try {
      named = SpdxExpression.Parse(declared).Licences;
    } catch (FormatException) {
      named = [declared];
    }

    return named.FirstOrDefault(candidates.Contains) is { } chosen
      ? Parsed(chosen, LicenceSource.LicenceFile)
      : new(family, LicenceSource.LicenceFile, Disagreement: $"declared licence '{declared}' disagrees with detected '{family}'");
  }

}
