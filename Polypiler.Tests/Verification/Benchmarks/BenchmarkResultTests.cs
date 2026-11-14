using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Core.Verification.Benchmarks;
using Polypiler.Prototype.Testing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using static Polypiler.Tests.Verification.Benchmarks.BenchTesting;

namespace Polypiler.Tests.Verification.Benchmarks;

/// <summary>
/// The result format: canonical serialization, reading it back, and both schemas checked by
/// JsonSchema.Net, which shares no code with the reader and writer.
/// </summary>
[TestFixture]
public sealed class BenchmarkResultTests {

  private static string Schema(string name) => Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", name);

  private static readonly JsonSchema _resultSchema = JsonSchema.FromText(File.ReadAllText(Schema("bench-result.schema.json")));
  private static readonly JsonSchema _definitionSchema = JsonSchema.FromText(File.ReadAllText(Schema("bench.schema.json")));

  private static BenchmarkResult Full() => Result("loop", "opt-speed", null, 10, 12, 11, 50, 13) with {
    BuildNs = 7,
    BinaryBytes = 4096,
    AllocatedBytes = 120,
    Allocations = 3,
    Extra = new Dictionary<string, long> { ["instructions"] = 42, ["branches"] = 7 }
  };

  [TestCase("bench.schema.json")]
  [TestCase("bench-result.schema.json")]
  public void TheSchemas_AreValidAgainstTheMetaSchema(string name) {
    using var document = JsonDocument.Parse(File.ReadAllText(Schema(name)));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  /// <summary>Measurements: the seven metrics of PRD §13.2 are each a property of the result schema.</summary>
  [TestCase("execution time", "wallNs")]
  [TestCase("allocation count", "allocations")]
  [TestCase("peak memory", "peakRssBytes")]
  [TestCase("generated source size", "sourceBytes")]
  [TestCase("binary size", "binaryBytes")]
  [TestCase("build time", "buildNs")]
  [TestCase("compilation cost", "allocatedBytes")]
  public void EveryPrdMetric_IsInTheResultSchema(string prdMetric, string property) {
    var schema = File.ReadAllText(Schema("bench-result.schema.json"));
    Assert.That(schema, Does.Contain($"\"{property}\": {{"), prdMetric);
    Assert.That(BenchmarkResult.Metrics, Does.Contain(property), prdMetric);
  }

  [Test]
  public void AWrittenResult_ConformsToTheSchema_AndReadsBack() {
    var result = Full();
    var json = result.ToJson();
    using var document = JsonDocument.Parse(json);
    Assert.That(_resultSchema.Evaluate(document.RootElement).IsValid, Is.True, json);

    var read = BenchmarkResult.Parse(json, "r.json");
    Assert.That(read.ToJson(), Is.EqualTo(json), "the serialization is canonical");
    Assert.That(read.Extra, Is.EqualTo(new Dictionary<string, long> { ["branches"] = 7, ["instructions"] = 42 }));
    Assert.That(read.Environment.Fingerprint, Is.EqualTo(result.Environment.Fingerprint));
  }

  [Test]
  public void TheCanonicalForm_HasLfLineEnds_SortedMaps_AndAFinalLineEnd() {
    var json = Full().ToJson();
    Assert.That(json, Does.Not.Contain("\r"));
    Assert.That(json, Does.EndWith("}\n"));
    Assert.That(json.IndexOf("\"branches\"", System.StringComparison.Ordinal), Is.LessThan(json.IndexOf("\"instructions\"", System.StringComparison.Ordinal)));
    Assert.That(json, Does.Contain("\"wallNs\": {\n      \"median\": 12000000,\n      \"mad\": 1000000,"));
  }

  [Test]
  public void TheStatistics_CoverOnlyMetricsEverySampleHas() {
    var result = Result("loop", "default", null, 1, 2) with { Samples = [new(10, null, 5), new(20, 3, 7)] };
    Assert.That(result.Stats.Keys, Is.EqualTo(new[] { "wallNs", "peakRssBytes" }));
    Assert.That(result.Value("cpuNs"), Is.Null);
    Assert.That((result.Value("wallNs"), result.Value("peakRssBytes")), Is.EqualTo((15L, 6L)));
  }

  [Test]
  public void AResultWithoutSamples_HasNoStatistics()
    => Assert.That(Result("loop", "default", null).Stats, Is.Empty);

  /// <summary>Test design 10 and 14: every metric and an extra one has a value to compare.</summary>
  [Test]
  public void EveryMetric_HasAValue() {
    var result = Full();
    Assert.That(result.PresentMetrics, Is.EqualTo(new[] { "wallNs", "cpuNs", "peakRssBytes", "buildNs", "sourceBytes", "binaryBytes", "allocatedBytes", "allocations", "branches", "instructions" }));
    Assert.That(new[] { "buildNs", "sourceBytes", "binaryBytes", "allocatedBytes", "allocations", "instructions", "unknown" }.Select(result.Value),
      Is.EqualTo(new long?[] { 7, 3, 4096, 120, 3, 42, null }));
  }

  [Test]
  public void TheFixtureResults_ConformToTheSchema_AndRead() {
    foreach (var file in Directory.EnumerateFiles(Path.Combine(FixtureFolder, "results"), "*.json", SearchOption.AllDirectories)) {
      using var document = JsonDocument.Parse(File.ReadAllText(file));
      Assert.That(_resultSchema.Evaluate(document.RootElement).IsValid, Is.True, file);
      var result = BenchmarkResult.Load(file);
      Assert.That(document.RootElement.GetProperty("environment").GetProperty("fingerprint").GetString(), Is.EqualTo(result.Environment.Fingerprint), file);
    }
  }

  [TestCase("{", "not valid JSON")]
  [TestCase("[]", "missing property 'schemaVersion'")]
  [TestCase("""{"schemaVersion": 2}""", "'schemaVersion' must be 1")]
  [TestCase("""{"schemaVersion": "1"}""", "'schemaVersion' must be an integer")]
  [TestCase("""{"schemaVersion": 1, "environment": []}""", "'environment' must be an object")]
  [TestCase("""{"schemaVersion": 1, "environment": {"toolchains": {"node": 1}}}""", "'environment.toolchains.node' must be text")]
  [TestCase("""{"schemaVersion": 1, "environment": {"toolchains": {}}, "id": 1}""", "'id' must be text")]
  public void AnInvalidResult_IsRefused(string text, string problem)
    => Assert.That(() => BenchmarkResult.Parse(text, "r.json"), Throws.TypeOf<BenchmarkFileException>().With.Message.Contains(problem));

  [Test]
  public void AResultWithAWrongField_IsRefused_WithTheFieldNamed() {
    var json = JsonNode.Parse(Full().ToJson())!;
    json["samples"] = new JsonObject();
    Assert.That(() => BenchmarkResult.Parse(json.ToJsonString(), "r.json"), Throws.TypeOf<BenchmarkFileException>().With.Message.EqualTo("'samples' must be an array"));
    json = JsonNode.Parse(Full().ToJson())!;
    json["buildNs"] = 1.5;
    Assert.That(() => BenchmarkResult.Parse(json.ToJsonString(), "r.json"), Throws.TypeOf<BenchmarkFileException>().With.Message.EqualTo("'buildNs' must be an integer"));
  }

  [Test]
  public void AMissingResultFile_IsAFileProblem()
    => Assert.That(() => BenchmarkResult.Load(Path.Combine(FixtureFolder, "results", "none.json")), Throws.TypeOf<BenchmarkFileException>().With.Message.StartsWith("cannot be read"));

  [Test]
  public void TheNullMaps_AreRefused()
    => Assert.That(() => Full() with { Extra = null! }, Throws.ArgumentNullException);

  /// <summary>The definition fixtures conform to the definition schema; an invalid kind and zero runs do not.</summary>
  [Test]
  public void TheDefinitionSchema_AcceptsTheFixture_AndRefusesWhatTheReaderRefuses() {
    Assert.That(_definitionSchema.Evaluate(Yaml(File.ReadAllText(Path.Combine(FixtureFolder, "sum-loop.bench.yaml")))).IsValid, Is.True);
    foreach (var file in Directory.EnumerateFiles(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "benchmarks"), "*.bench.yaml"))
      Assert.That(_definitionSchema.Evaluate(Yaml(File.ReadAllText(file))).IsValid, Is.True, file);
    const string minimal = "id: loop\ndescription: d\ntoolchain: node\nrun: [a]\n";
    Assert.That(_definitionSchema.Evaluate(Yaml(minimal + "kind: native\n")).IsValid, Is.False);
    Assert.That(_definitionSchema.Evaluate(Yaml(minimal + "kind: process\nruns: 0\n")).IsValid, Is.False);
    Assert.That(_definitionSchema.Evaluate(Yaml(minimal + "kind: process\nsources: [../a]\n")).IsValid, Is.False);
  }

  /// <summary>A YAML document as JSON: plain integers become numbers, everything else strings.</summary>
  private static JsonElement Yaml(string text) {
    var stream = new YamlStream();
    stream.Load(new StringReader(text));
    return JsonSerializer.SerializeToElement(Convert(stream.Documents[0].RootNode));
  }

  private static JsonNode? Convert(YamlNode node) => node switch {
    YamlMappingNode mapping => new JsonObject(mapping.Children.Select(pair => new KeyValuePair<string, JsonNode?>(((YamlScalarNode)pair.Key).Value!, Convert(pair.Value)))),
    YamlSequenceNode sequence => new JsonArray([.. sequence.Children.Select(Convert)]),
    YamlScalarNode { Style: ScalarStyle.Plain } plain when int.TryParse(plain.Value, out var number) => JsonValue.Create(number),
    YamlScalarNode scalar => JsonValue.Create(scalar.Value),
    _ => null
  };

}
