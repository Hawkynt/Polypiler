using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The family of <c>spec/recovery/</c>: one document per recovery rule of the block
/// <c>O0170</c>–<c>O0199</c>, the rules that turn lifted machine code back into readable structure
/// (de-unrolling, expression slicing, compiler idioms, loop and structure reconstruction). Every rule
/// says whether it is <c>proven</c> or <c>speculative-guarded</c>, and a speculative one names its
/// run-time guard and the original form it falls back to. The body schema is
/// <c>spec/schemas/recovery.schema.json</c>.
/// </summary>
public sealed partial class RecoverySpecFamily : ISpecFamily {

  private const int _blockFirst = 170;
  private const int _blockLast = 199;

  /// <summary>
  /// The recovery blocks of other families, where a recovery rule numbered here belongs instead
  /// (<c>backlog/README.md</c>): bit-permutation recovery in <c>spec/recognition</c>, object-model
  /// lowering and recovery in <c>spec/rewrites</c>.
  /// </summary>
  private static readonly (int First, int Last, string Folder)[] _otherRecoveryBlocks = [
    (200, 229, "spec/recognition"),
    (230, 259, "spec/rewrites"),
  ];

  /// <inheritdoc />
  public string Folder => "recovery";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/recovery.schema.json";

  /// <summary>
  /// The rules <c>layout</c>, <c>id-in-block</c>, <c>speculation-matches-mode</c>,
  /// <c>proven-needs-obligation</c>, <c>deunroll-needs-repetitions</c>, <c>effects-preserved</c> and
  /// <c>switch-matches</c>, each as <c>PLY0046</c> at the offending value.
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree; a recovery body names no other document.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    ArgumentNullException.ThrowIfNull(document);
    var body = (JsonObject)document.Body!;
    var optimization = (string)body["optimization"]!;
    var proofMode = (string)body["proofMode"]!;
    var technique = (string)body["technique"]!;
    var preserves = body["preserves"]!.AsArray().Select(value => (string)value!).ToHashSet(StringComparer.Ordinal);

    foreach (var diagnostic in Layout(document, optimization))
      yield return diagnostic;

    var number = int.Parse(optimization.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
    if (number is < _blockFirst or > _blockLast) {
      var hint = _otherRecoveryBlocks.Where(block => number >= block.First && number <= block.Last).Select(block => $"; {optimization} belongs to {block.Folder}").FirstOrDefault("");
      yield return SpecDiagnostics.Rule(document, "/spec/optimization", $"{optimization} is outside the recovery block O{_blockFirst:D4}-O{_blockLast:D4}{hint}", "id-in-block");
    }

    var speculative = proofMode == "speculative-guarded";
    if (speculative && body["speculation"] is null)
      yield return SpecDiagnostics.Rule(document, "/spec/proofMode", "proofMode 'speculative-guarded' requires 'speculation' with guard and fallback", "speculation-matches-mode");
    else if (!speculative && body["speculation"] is not null)
      yield return SpecDiagnostics.Rule(document, "/spec/speculation", $"proofMode '{proofMode}' must not carry 'speculation'", "speculation-matches-mode");

    if (!speculative && body["proofObligations"] is not JsonArray { Count: > 0 })
      yield return SpecDiagnostics.Rule(document, body["proofObligations"] is null ? "/spec/proofMode" : "/spec/proofObligations", $"proofMode '{proofMode}' requires at least one proof obligation", "proven-needs-obligation");

    var deUnroll = technique == "de-unroll";
    if (deUnroll && body["minRepetitions"] is null)
      yield return SpecDiagnostics.Rule(document, "/spec/technique", "technique 'de-unroll' requires 'minRepetitions'", "deunroll-needs-repetitions");
    else if (!deUnroll && body["minRepetitions"] is not null)
      yield return SpecDiagnostics.Rule(document, "/spec/minRepetitions", $"technique '{technique}' does not take 'minRepetitions'", "deunroll-needs-repetitions");

    var rewritten = Effectful((string)body["before"]!, (string)body["after"]!);
    if (rewritten is not null)
      foreach (var effect in new[] { "effect-order", "memory-effects" }.Where(effect => !preserves.Contains(effect)))
        yield return SpecDiagnostics.Rule(document, "/spec/preserves", $"the rule rewrites {rewritten} but does not preserve '{effect}'", "effects-preserved");

    var @switch = (string)body["switch"]!;
    if (@switch != "--disable-opt " + optimization)
      yield return SpecDiagnostics.Rule(document, "/spec/switch", $"switch '{@switch}' does not disable {optimization}", "switch-matches");
  }

  /// <summary>
  /// Rule <c>layout</c>: the file is <c>recovery/O&lt;4 digits&gt;.yaml</c>, and the ID, the optimization
  /// and the optimization page all follow from its name.
  /// </summary>
  private static IEnumerable<Diagnostic> Layout(SpecDocument document, string optimization) {
    var match = LayoutPattern().Match(document.Path);
    if (!match.Success) {
      yield return SpecDiagnostics.Rule(document, "", $"'{document.Path}' is not recovery/O<4 digits>.yaml", "layout");
      yield break;
    }

    var stem = match.Groups["number"].Value;
    var fileName = stem + ".yaml";
    if (document.Meta!.Id != "RECOVERY." + stem)
      yield return SpecDiagnostics.Rule(document, "/id", $"id '{document.Meta.Id}' does not match file name '{fileName}'", "layout");
    if (optimization != stem)
      yield return SpecDiagnostics.Rule(document, "/spec/optimization", $"optimization '{optimization}' does not match file name '{fileName}'", "layout");

    var page = (string)document.Body!["document"]!;
    if (!page.StartsWith($"docs/optimizations/{optimization}-", StringComparison.Ordinal))
      yield return SpecDiagnostics.Rule(document, "/spec/document", $"document '{page}' is not the page of {optimization}", "layout");
  }

  /// <summary>What a rule rewrites that has an effect: <c>stores</c> when either form holds a <c>Store</c>, else <c>calls</c> when either holds a <c>Call</c>, else <see langword="null"/>.</summary>
  private static string? Effectful(string before, string after) {
    if (before.Contains("Store", StringComparison.Ordinal) || after.Contains("Store", StringComparison.Ordinal))
      return "stores";
    return before.Contains("Call", StringComparison.Ordinal) || after.Contains("Call", StringComparison.Ordinal) ? "calls" : null;
  }

  [GeneratedRegex("^recovery/(?<number>O[0-9]{4})\\.yaml$", RegexOptions.CultureInvariant)]
  private static partial Regex LayoutPattern();

}
