using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Tooling.Corpus;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Tooling.Licensing;

/// <summary>
/// <c>licences.yaml</c> at the root of a scanned tree: the licences its files are published under,
/// which select the variant of a licence text shared by several identifiers, as the declared licence
/// of a fixture does in the corpus. <c>declared</c> is a list of <c>{glob, licence}</c>; the last entry
/// whose glob matches a file wins, so a general <c>**</c> comes first and a sub-folder after it.
/// </summary>
public sealed class LicenceDeclarations {

  /// <summary>The file's name at the scan root.</summary>
  public const string FileName = "licences.yaml";

  private const string What = "licence declarations";

  private readonly IReadOnlyList<(GlobPattern Glob, string Licence)> _entries;

  private LicenceDeclarations(IReadOnlyList<(GlobPattern Glob, string Licence)> entries) => this._entries = entries;

  /// <summary>Declarations that declare nothing.</summary>
  public static LicenceDeclarations None { get; } = new([]);

  /// <summary>The licence declared for a path, or <see langword="null"/>.</summary>
  public string? For(string path) => this._entries.LastOrDefault(entry => LicencePolicy.Matches(entry.Glob, path)).Licence;

  /// <summary>Reads the declarations of a scan root; a root without the file declares nothing.</summary>
  /// <exception cref="CorpusException">The file cannot be read or is invalid (<c>PLY0026</c>).</exception>
  public static LicenceDeclarations Load(string root) {
    var path = Path.Combine(root, FileName);
    if (!File.Exists(path))
      return None;

    try {
      return Parse(File.ReadAllText(path), path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new CorpusException(LicenceDiagnostics.Invalid(What, $"cannot read '{path}': {exception.Message}"));
    }
  }

  /// <summary>Checks the text of a declarations file.</summary>
  /// <exception cref="CorpusException">The text is invalid (<c>PLY0026</c>).</exception>
  public static LicenceDeclarations Parse(string text, string file) {
    var stream = new YamlStream();
    try {
      stream.Load(new StringReader(text));
    } catch (YamlException exception) {
      throw new CorpusException(LicenceDiagnostics.Invalid(What, $"not YAML: {exception.Message}", file, (int)exception.Start.Line, (int)exception.Start.Column));
    }

    if (stream.Documents.Count != 1
        || stream.Documents[0].RootNode is not YamlMappingNode root
        || root.Children.Count != 2
        || !root.Children.TryGetValue(new YamlScalarNode("schema"), out var schema) || schema is not YamlScalarNode { Value: "1" }
        || !root.Children.TryGetValue(new YamlScalarNode("declared"), out var declared) || declared is not YamlSequenceNode list)
      throw new CorpusException(LicenceDiagnostics.Invalid(What, "the file must be a mapping of exactly 'schema: 1' and the list 'declared'", file));

    var entries = new List<(GlobPattern, string)>();
    var problems = new List<Diagnostic>();
    foreach (var item in list.Children) {
      if (item is YamlMappingNode { Children.Count: 2 } mapping
          && mapping.Children.TryGetValue(new YamlScalarNode("glob"), out var glob) && glob is YamlScalarNode { Value.Length: > 0 } globText
          && mapping.Children.TryGetValue(new YamlScalarNode("licence"), out var licence) && licence is YamlScalarNode { Value.Length: > 0 } licenceText) {
        try {
          SpdxExpression.Parse(licenceText.Value!);
          entries.Add((GlobPattern.Parse(globText.Value!), licenceText.Value!));
        } catch (FormatException exception) {
          problems.Add(LicenceDiagnostics.Invalid(What, $"'{licenceText.Value}' is not an SPDX expression: {exception.Message}", file, (int)licence.Start.Line, (int)licence.Start.Column));
        }

        continue;
      }

      problems.Add(LicenceDiagnostics.Invalid(What, "an entry of 'declared' must be a mapping of exactly 'glob' and 'licence'", file, (int)item.Start.Line, (int)item.Start.Column));
    }

    if (problems.Count > 0)
      throw new CorpusException(problems);
    return new(entries);
  }

}
