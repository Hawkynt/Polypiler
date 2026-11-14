using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The family <c>spec/permutations/</c> (PRD §4.1, §23): bit mappings with their test vectors, the
/// grammars that synthesize them and the cost tables of a CPU, each in its own folder
/// <c>mappings</c>, <c>grammars</c> or <c>costs</c>. The schema
/// <c>spec/schemas/permutations.schema.json</c> states the shapes; this class the rules across fields
/// and files. A mapping is exactly its <c>outputs</c> list, least significant output bit first: an
/// input bit index, an inverted input bit, a constant or a don't-care.
/// </summary>
public sealed class PermutationsSpecFamily : ISpecFamily {

  /// <inheritdoc />
  public string Folder => "permutations";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/permutations.schema.json";

  /// <summary>The folder under <c>docs/</c> that a <c>measured</c> cost names its benchmark run in.</summary>
  public const string BenchmarksFolder = "docs/benchmarks";

  /// <summary>
  /// The rules: <c>layout</c> for every document; <c>index-in-range</c>, <c>bijective-consistent</c>,
  /// <c>vector-width</c> and <c>vector-consistent</c> for a mapping; <c>unique-primitive</c> and
  /// <c>cost-evidence</c> for a cost table (all <c>PLY0046</c>); the <c>target</c> of a grammar
  /// resolves to a spec document (<c>PLY0043</c>).
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree the references and evidence files are resolved in.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    ArgumentNullException.ThrowIfNull(document);
    ArgumentNullException.ThrowIfNull(tree);
    var body = (JsonObject)document.Body!;
    var kind = (string)body["kind"]!;
    var problems = new List<Diagnostic>();
    if (Layout(document, kind) is { } misplaced)
      problems.Add(misplaced);

    switch (kind) {
      case "mapping":
        problems.AddRange(CheckMapping(document, body));
        break;
      case "grammar":
        if (body["target"] is { } target && tree.Resolve(document, "/spec/target", (string)target!) is { } broken)
          problems.Add(broken);
        break;
      default:
        problems.AddRange(CheckCosts(document, body, tree));
        break;
    }

    return problems;
  }

  /// <summary>Rule <c>layout</c>: <c>permutations/&lt;kind&gt;s/&lt;name&gt;.yaml</c> with the ID <c>PERMUTATIONS.&lt;kind&gt;.&lt;name&gt;</c>.</summary>
  private static Diagnostic? Layout(SpecDocument document, string kind) {
    var folder = $"permutations/{kind}s";
    var slash = document.Path.LastIndexOf('/');
    var found = document.Path[..slash];
    if (found != folder)
      return SpecDiagnostics.Rule(document, "/spec/kind", $"kind '{kind}' must live in spec/{folder}, found spec/{found}", "layout");

    var name = document.Path[(slash + 1)..];
    var expected = $"PERMUTATIONS.{kind}.{name[..^".yaml".Length]}";
    return document.Meta!.Id == expected
      ? null
      : SpecDiagnostics.Rule(document, "/id", $"id '{document.Meta.Id}' must be '{expected}' in spec/{document.Path}", "layout");
  }

  private static IEnumerable<Diagnostic> CheckMapping(SpecDocument document, JsonObject body) {
    var width = Integer(body["inputWidth"]);
    var outputs = (JsonArray)body["outputs"]!;
    var entries = new Entry[outputs.Count];
    var outOfRange = new List<Diagnostic>();
    for (var index = 0; index < outputs.Count; ++index) {
      entries[index] = Entry.From(outputs[index]!);
      if (entries[index].Source is { } source && source >= width) {
        var pointer = $"/spec/outputs/{index}" + (entries[index].Inverted ? "/not" : "");
        outOfRange.Add(SpecDiagnostics.Rule(document, pointer, $"outputs[{index}] reads input bit {source}, inputWidth is {width}", "index-in-range"));
      }
    }

    if (outOfRange.Count > 0)
      return outOfRange;

    var problems = new List<Diagnostic>();
    if (Bijective(document, (bool)body["bijective"]!, width, entries) is { } claim)
      problems.Add(claim);

    var vectors = (JsonArray)body["vectors"]!;
    for (var index = 0; index < vectors.Count; ++index)
      problems.AddRange(CheckVector(document, index, (JsonObject)vectors[index]!, width, entries));
    return problems;
  }

  /// <summary>Rule <c>bijective-consistent</c>: the claim holds exactly when every input bit is taken, plainly, by exactly one output bit.</summary>
  private static Diagnostic? Bijective(SpecDocument document, bool declared, int width, Entry[] entries) {
    string? reason = null;
    var pointer = "/spec/outputs";
    var used = new HashSet<int>();
    for (var index = 0; index < entries.Length && reason is null; ++index) {
      var entry = entries[index];
      if (entry.Source is not { } source || entry.Inverted)
        (reason, pointer) = ($"outputs[{index}] is not a plain input bit", $"/spec/outputs/{index}");
      else if (!used.Add(source))
        (reason, pointer) = ($"input bit {source} is used twice", $"/spec/outputs/{index}");
    }

    if (reason is null && entries.Length != width)
      reason = $"it has {entries.Length} outputs for inputWidth {width}";

    return (declared, reason) switch {
      (true, not null) => SpecDiagnostics.Rule(document, pointer, $"mapping is declared bijective but {reason}", "bijective-consistent"),
      (false, null) => SpecDiagnostics.Rule(document, "/spec/bijective", "mapping is a bijection of its input bits but is declared not bijective", "bijective-consistent"),
      _ => null,
    };
  }

  /// <summary>Rules <c>vector-width</c> and, for a vector that fits, <c>vector-consistent</c>.</summary>
  private static IEnumerable<Diagnostic> CheckVector(SpecDocument document, int index, JsonObject vector, int width, Entry[] entries) {
    var input = Hex(vector["input"]);
    var output = Hex(vector["output"]);
    var mask = vector["mask"] is { } given ? Hex(given) : (BigInteger.One << entries.Length) - 1;
    var problems = new List<Diagnostic>();
    void Fits(string field, BigInteger value, int bits, string limit) {
      if (value.GetBitLength() > bits)
        problems.Add(SpecDiagnostics.Rule(document, $"/spec/vectors/{index}/{field}", $"vectors[{index}].{field} {(string)vector[field]!} does not fit {limit}", "vector-width"));
    }

    Fits("input", input, width, $"inputWidth {width}");
    Fits("output", output, entries.Length, $"{entries.Length} outputs");
    if (vector.ContainsKey("mask"))
      Fits("mask", mask, entries.Length, $"{entries.Length} outputs");
    if (problems.Count > 0)
      return problems;

    var result = BigInteger.Zero;
    for (var bit = 0; bit < entries.Length; ++bit) {
      var entry = entries[bit];
      if (entry.DontCare)
        mask &= ~(BigInteger.One << bit);
      else if (entry.Source is { } source ? !((input >> source).IsEven) != entry.Inverted : entry.Constant)
        result |= BigInteger.One << bit;
    }

    if ((result & mask) == (output & mask))
      return problems;

    var digits = (entries.Length + 3) / 4;
    var gives = "0x" + (result & mask).ToString("X" + digits, CultureInfo.InvariantCulture)[^digits..];
    problems.Add(SpecDiagnostics.Rule(document, $"/spec/vectors/{index}/output",
      $"vectors[{index}]: the mapping gives {gives} for input {(string)vector["input"]!}, the document says {(string)vector["output"]!}", "vector-consistent"));
    return problems;
  }

  private static IEnumerable<Diagnostic> CheckCosts(SpecDocument document, JsonObject body, SpecTree tree) {
    var costs = (JsonArray)body["costs"]!;
    var seen = new HashSet<string>(StringComparer.Ordinal);
    for (var index = 0; index < costs.Count; ++index) {
      var primitive = (string)costs[index]!["primitive"]!;
      if (!seen.Add(primitive))
        yield return SpecDiagnostics.Rule(document, $"/spec/costs/{index}/primitive", $"costs[{index}]: primitive '{primitive}' is listed twice", "unique-primitive");
    }

    var evidence = body["evidence"]!;
    var reference = (string)evidence["reference"]!;
    var reason = (string)evidence["kind"]! == "measured" ? Measured(reference, tree.RepositoryRoot) : string.IsNullOrWhiteSpace(reference) ? "documented cost evidence needs a citation" : null;
    if (reason is not null)
      yield return SpecDiagnostics.Rule(document, "/spec/evidence/reference", reason, "cost-evidence");
  }

  /// <summary>Why a <c>measured</c> reference names no benchmark run, or <see langword="null"/> when it names a file under <see cref="BenchmarksFolder"/>.</summary>
  private static string? Measured(string reference, string repositoryRoot) {
    var benchmarks = Path.GetFullPath(Path.Combine(repositoryRoot, BenchmarksFolder)) + Path.DirectorySeparatorChar;
    var full = Path.GetFullPath(Path.Combine(repositoryRoot, reference));
    if (!full.StartsWith(benchmarks, StringComparison.Ordinal))
      return $"measured cost evidence {reference} is not a file under {BenchmarksFolder}/";
    return File.Exists(full) ? null : $"measured cost evidence {reference} does not exist";
  }

  /// <summary>A number the schema has checked to be an integer, whether YAML wrote it <c>8</c> or <c>8.0</c>.</summary>
  private static int Integer(JsonNode? value) => (int)double.Parse(value!.ToJsonString(), CultureInfo.InvariantCulture);

  private static BigInteger Hex(JsonNode? value) => BigInteger.Parse("0" + ((string)value!)[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

  /// <summary>One entry of <c>outputs</c>, as the schema allows it.</summary>
  private readonly record struct Entry(int? Source, bool Inverted, bool Constant, bool DontCare) {

    public static Entry From(JsonNode node) => node switch {
      JsonObject entry when entry["not"] is { } source => new(Integer(source), true, false, false),
      JsonObject entry when entry["const"] is { } constant => new(null, false, Integer(constant) == 1, false),
      JsonObject => new(null, false, false, true),
      _ => new(Integer(node), false, false, false),
    };

  }

}
