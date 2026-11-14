using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The family of <c>spec/recognition/</c>: one document per recognition rule of the block
/// <c>O0100</c>–<c>O0169</c>, or of the bit-permutation recovery block <c>O0200</c>–<c>O0229</c>, with
/// its pattern, the replacement and the documents it introduces, preconditions, proof obligations,
/// counterexamples, disabling switch, optimization levels and optimization page (PRD §4.1, §4.5). The
/// body schema is <c>spec/schemas/recognition.schema.json</c>.
/// </summary>
public sealed partial class RecognitionSpecFamily : ISpecFamily {

  /// <summary>The blocks a recognition rule is numbered in: recognition, and bit-permutation recovery.</summary>
  private static readonly (int First, int Last)[] _blocks = [(100, 169), (200, 229)];

  /// <inheritdoc />
  public string Folder => "recognition";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/recognition.schema.json";

  /// <summary>
  /// The rules <c>layout</c> (the file is <c>recognition/O&lt;4 digits&gt;.yaml</c>, and the ID, the
  /// optimization and the page follow from it; a broken layout stops the other rules),
  /// <c>id-in-block</c>, <c>switch-matches</c>, <c>counterexample-differs</c>,
  /// <c>smt-needs-budget</c>, <c>exhaustive-width</c> and, for a normative document,
  /// <c>document-exists</c>, each as <c>PLY0046</c> at the offending value. Every ID under
  /// <c>replacement.introduces</c> must name a document (<c>PLY0043</c>).
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree the introduced documents are resolved in.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    ArgumentNullException.ThrowIfNull(document);
    ArgumentNullException.ThrowIfNull(tree);
    var layout = Layout(document);
    return layout.Count > 0 ? layout : Rules(document, tree);
  }

  private static IEnumerable<Diagnostic> Rules(SpecDocument document, SpecTree tree) {
    var body = (JsonObject)document.Body!;
    var optimization = (string)body["optimization"]!;

    var number = int.Parse(optimization.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
    if (!Array.Exists(_blocks, block => number >= block.First && number <= block.Last))
      yield return SpecDiagnostics.Rule(document, "/spec/optimization", $"{optimization} is outside the recognition blocks O0100-O0169 and O0200-O0229", "id-in-block");

    var @switch = (string)body["switch"]!;
    if (@switch != "--disable-opt " + optimization)
      yield return SpecDiagnostics.Rule(document, "/spec/switch", $"switch '{@switch}' does not disable {optimization}", "switch-matches");

    var counterexamples = body["counterexamples"]!.AsArray();
    for (var index = 0; index < counterexamples.Count; ++index) {
      var naive = ((string)counterexamples[index]!["naive"]!).Trim();
      var correct = ((string)counterexamples[index]!["correct"]!).Trim();
      if (string.Equals(naive, correct, StringComparison.OrdinalIgnoreCase))
        yield return SpecDiagnostics.Rule(document, $"/spec/counterexamples/{index}/correct", $"counterexamples[{index}] has equal naive and correct results ({correct})", "counterexample-differs");
    }

    var obligations = body["proofObligations"]!.AsArray();
    for (var index = 0; index < obligations.Count; ++index) {
      var obligation = obligations[index]!;
      var id = (string)obligation["id"]!;
      var @class = (string)obligation["class"]!;
      if (@class == "smt-bitvector" && obligation["budgetSeconds"] is null)
        yield return SpecDiagnostics.Rule(document, $"/spec/proofObligations/{index}", $"obligation '{id}' of class smt-bitvector has no budgetSeconds", "smt-needs-budget");

      var widths = obligation["widths"]!.AsArray();
      for (var position = 0; position < widths.Count; ++position) {
        var width = (long)widths[position]!;
        if (@class == "exhaustive-8bit" ? width != 8 : @class == "exhaustive-16bit" && width > 16)
          yield return SpecDiagnostics.Rule(document, $"/spec/proofObligations/{index}/widths/{position}", $"obligation '{id}' of class {@class} lists width {width}", "exhaustive-width");
      }
    }

    var introduces = body["replacement"]!["introduces"]!.AsArray();
    for (var index = 0; index < introduces.Count; ++index)
      if (tree.Resolve(document, $"/spec/replacement/introduces/{index}", (string)introduces[index]!) is { } unknown)
        yield return unknown;

    var page = (string)body["document"]!;
    if (document.Meta!.Status == "normative" && !File.Exists(Path.Combine(tree.RepositoryRoot, page)))
      yield return SpecDiagnostics.Rule(document, "/spec/document", $"document {page} does not exist", "document-exists");
  }

  /// <summary>
  /// Rule <c>layout</c>: the file is <c>recognition/O&lt;4 digits&gt;.yaml</c>, and the ID, the
  /// optimization and the optimization page all follow from its name.
  /// </summary>
  private static List<Diagnostic> Layout(SpecDocument document) {
    var match = LayoutPattern().Match(document.Path);
    if (!match.Success)
      return [SpecDiagnostics.Rule(document, "", $"'{document.Path}' is not recognition/O<4 digits>.yaml", "layout")];

    var problems = new List<Diagnostic>();
    var stem = match.Groups["number"].Value;
    var fileName = stem + ".yaml";
    if (document.Meta!.Id != "RECOGNITION." + stem)
      problems.Add(SpecDiagnostics.Rule(document, "/id", $"id '{document.Meta.Id}' does not match file name '{fileName}'", "layout"));

    var optimization = (string)document.Body!["optimization"]!;
    if (optimization != stem)
      problems.Add(SpecDiagnostics.Rule(document, "/spec/optimization", $"optimization '{optimization}' does not match file name '{fileName}'", "layout"));

    var page = (string)document.Body!["document"]!;
    if (!page.StartsWith($"docs/optimizations/{stem}-", StringComparison.Ordinal))
      problems.Add(SpecDiagnostics.Rule(document, "/spec/document", $"document '{page}' is not the page of {stem}", "layout"));

    return problems;
  }

  [GeneratedRegex("^recognition/(?<number>O[0-9]{4})\\.yaml$", RegexOptions.CultureInvariant)]
  private static partial Regex LayoutPattern();

}
