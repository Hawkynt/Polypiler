using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The family <c>rewrites</c>: one semantics-preserving transformation per document,
/// <c>spec/rewrites/O####.yaml</c> with the ID <c>REWRITES.O####</c>, carrying its before and after
/// forms, predicates, proof class, floating-point claims, negative fixtures, disabling switch and
/// interactions (PRD §4.1, §4.8, §7.2). The body schema is <c>spec/schemas/rewrites.schema.json</c>.
/// </summary>
public sealed partial class RewritesSpecFamily : ISpecFamily {

  private const string _prefix = "REWRITES.";

  /// <inheritdoc />
  public string Folder => "rewrites";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/rewrites.schema.json";

  /// <summary>
  /// The rules of the family, each a <c>PLY0046</c> at the offending value: <c>layout</c> (the path is
  /// <c>rewrites/O####.yaml</c>, and the ID, the optimization and the page follow from it; a broken layout
  /// stops the other rules), <c>id-in-block</c>, <c>no-unproven-reassociation</c>,
  /// <c>float-flags-consistent</c>, <c>interaction-acyclic</c> over every document of the family,
  /// <c>switch-matches</c> and, for a normative document, <c>negative-fixture-exists</c>. Every
  /// optimization named under <c>interactions</c> must have a document (<c>PLY0043</c>).
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree the interactions are resolved in.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    ArgumentNullException.ThrowIfNull(document);
    ArgumentNullException.ThrowIfNull(tree);
    var layout = Layout(document, out var optimization);
    if (layout.Count > 0)
      return layout;

    var body = document.Body!;
    var problems = new List<Diagnostic>();
    if (!InBlock(optimization))
      problems.Add(SpecDiagnostics.Rule(document, "/spec/optimization", $"{optimization} is outside the rewrite blocks O0001-O0099 and O0230-O0299", "id-in-block"));

    problems.AddRange(FloatingPoint(document, body));
    if (Cycle(document, optimization, tree) is { } cycle)
      problems.Add(cycle);

    var disabled = ((string)body["switch"]!)["--disable-opt ".Length..];
    if (disabled != optimization)
      problems.Add(SpecDiagnostics.Rule(document, "/spec/switch", $"switch '--disable-opt {disabled}' does not disable {optimization}", "switch-matches"));

    if (document.Meta!.Status == "normative")
      problems.AddRange(MissingNegativeFixtures(document, body, tree));

    foreach (var list in new[] { "after", "enables" }) {
      var names = (JsonArray)body["interactions"]![list]!;
      for (var index = 0; index < names.Count; ++index)
        if (tree.Resolve(document, $"/spec/interactions/{list}/{index}", _prefix + (string)names[index]!) is { } broken)
          problems.Add(broken);
    }

    return problems;
  }

  /// <summary>Whether an optimization number lies in a block of this family: <c>O0001</c>–<c>O0099</c> or <c>O0230</c>–<c>O0299</c>.</summary>
  /// <param name="optimization">The number, <c>O</c> and four digits.</param>
  /// <returns><see langword="true"/> inside a block.</returns>
  public static bool InBlock(string optimization) {
    ArgumentNullException.ThrowIfNull(optimization);
    var number = int.Parse(optimization.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
    return number is >= 1 and <= 99 or >= 230 and <= 299;
  }

  /// <summary>Rule <c>layout</c>: the path, and the ID, the optimization and the page that follow from the file name.</summary>
  private static List<Diagnostic> Layout(SpecDocument document, out string optimization) {
    var match = FilePattern().Match(document.Path);
    optimization = match.Groups[1].Value;
    if (!match.Success)
      return [SpecDiagnostics.Rule(document, "/id", $"file '{document.Path}' does not follow the layout rewrites/O<4 digits>.yaml", "layout")];

    var file = $"{optimization}.yaml";
    var body = document.Body!;
    var problems = new List<Diagnostic>();
    if (document.Meta!.Id != _prefix + optimization)
      problems.Add(SpecDiagnostics.Rule(document, "/id", $"id '{document.Meta.Id}' does not match file name '{file}'", "layout"));
    if ((string)body["optimization"]! is var named && named != optimization)
      problems.Add(SpecDiagnostics.Rule(document, "/spec/optimization", $"optimization '{named}' does not match file name '{file}'", "layout"));
    if ((string)body["document"]! is var page && !page.StartsWith($"docs/optimizations/{optimization}-", StringComparison.Ordinal))
      problems.Add(SpecDiagnostics.Rule(document, "/spec/document", $"document '{page}' does not belong to {optimization}", "layout"));
    return problems;
  }

  /// <summary>Rules <c>no-unproven-reassociation</c> and <c>float-flags-consistent</c>.</summary>
  private static IEnumerable<Diagnostic> FloatingPoint(SpecDocument document, JsonNode body) {
    var flags = body["floatingPoint"]!;
    var reassociates = (bool)flags["reassociates"]!;
    var contracts = (bool)flags["contracts"]!;
    if (!reassociates && !contracts)
      yield break;

    var what = reassociates ? "reassociation" : "contraction";
    var proof = (string)body["proof"]!["class"]!;
    if (proof != "float-exactness")
      yield return SpecDiagnostics.Rule(document, "/spec/proof/class", $"floating-point {what} requires proof class float-exactness, found {proof}", "no-unproven-reassociation");
    else if (!((JsonArray)body["predicates"]!).Any(predicate => (string)predicate!["fact"]! == "float-exactness"))
      yield return SpecDiagnostics.Rule(document, "/spec/predicates", $"floating-point {what} requires a predicate with fact float-exactness", "no-unproven-reassociation");

    if (!(bool)flags["touchesFloat"]!)
      yield return SpecDiagnostics.Rule(document, $"/spec/floatingPoint/{(reassociates ? "reassociates" : "contracts")}", $"floatingPoint.{(reassociates ? "reassociates" : "contracts")} is true but touchesFloat is false", "float-flags-consistent");
  }

  /// <summary>
  /// Rule <c>interaction-acyclic</c>: a cycle of the <c>interactions.after</c> relation, reported once,
  /// at the document of its smallest optimization, at the entry that starts it; the shortest such cycle.
  /// </summary>
  private static Diagnostic? Cycle(SpecDocument document, string optimization, SpecTree tree) {
    var graph = AfterGraph(tree);
    var after = graph[optimization];
    for (var index = 0; index < after.Count; ++index) {
      var path = PathBack(after[index], optimization, graph);
      if (path is not null)
        return SpecDiagnostics.Rule(document, $"/spec/interactions/after/{index}", $"interaction cycle {string.Join(" -> ", [optimization, .. path])}", "interaction-acyclic");
    }

    return null;
  }

  /// <summary>The shortest path from a node back to the smallest node of a cycle through nodes above it, both ends included, or <see langword="null"/>.</summary>
  private static List<string>? PathBack(string start, string smallest, IReadOnlyDictionary<string, IReadOnlyList<string>> graph) {
    if (start == smallest)
      return [start];
    if (string.CompareOrdinal(start, smallest) < 0)
      return null;

    var cameFrom = new Dictionary<string, string?>(StringComparer.Ordinal) { [start] = null };
    var queue = new Queue<string>([start]);
    while (queue.TryDequeue(out var node))
      foreach (var next in graph.GetValueOrDefault(node) ?? []) {
        if (next == smallest) {
          var path = new List<string> { smallest };
          for (string? step = node; step is not null; step = cameFrom[step])
            path.Insert(0, step);
          return path;
        }

        if (string.CompareOrdinal(next, smallest) > 0 && cameFrom.TryAdd(next, node))
          queue.Enqueue(next);
      }

    return null;
  }

  /// <summary>The <c>interactions.after</c> lists of every document of the family laid out as <c>rewrites/O####.yaml</c>, by the optimization of its file name; strings only, whatever else the document holds.</summary>
  private static Dictionary<string, IReadOnlyList<string>> AfterGraph(SpecTree tree) {
    var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    foreach (var other in tree.Documents)
      if (FilePattern().Match(other.Path) is { Success: true } match && other.Body?["interactions"]?["after"] is JsonArray after)
        graph[match.Groups[1].Value] = [.. after.OfType<JsonValue>().Where(value => value.GetValueKind() == JsonValueKind.String).Select(value => (string)value!)];
    return graph;
  }

  /// <summary>Rule <c>negative-fixture-exists</c>: every negative fixture of a normative document is a file inside the repository.</summary>
  private static IEnumerable<Diagnostic> MissingNegativeFixtures(SpecDocument document, JsonNode body, SpecTree tree) {
    var fixtures = (JsonArray)body["negativeFixtures"]!;
    for (var index = 0; index < fixtures.Count; ++index) {
      var fixture = (string)fixtures[index]!;
      var full = Path.GetFullPath(Path.Combine(tree.RepositoryRoot, fixture));
      var inside = !(Path.GetRelativePath(tree.RepositoryRoot, full) + Path.DirectorySeparatorChar).StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
      if (!inside || !File.Exists(full))
        yield return SpecDiagnostics.Rule(document, $"/spec/negativeFixtures/{index}", $"negative fixture {fixture} does not exist", "negative-fixture-exists");
    }
  }

  [GeneratedRegex("^rewrites/(O[0-9]{4})\\.yaml$")]
  private static partial Regex FilePattern();

}
