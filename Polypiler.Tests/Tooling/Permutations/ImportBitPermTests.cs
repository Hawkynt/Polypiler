using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Spec;
using Polypiler.Tooling;
using Polypiler.Tooling.Permutations;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Tooling.Permutations;

/// <summary>
/// <see cref="ImportBitPermCommand"/>: the documents it generates from the checked-in BitPerm fixtures,
/// validated by the permutations family; the cost tables compared field by field with
/// <c>presets.json</c>; the command line, <c>--check</c> and every way an import fails.
/// </summary>
[TestFixture]
public sealed class ImportBitPermTests {

  private const string SchemaPath = "spec/schemas/permutations.schema.json";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private static readonly string _fixtures = Path.Combine(_root, "tests", "fixtures", "bitperm");

  private static readonly Lazy<IReadOnlyList<GeneratedFile>> _generated = new(() => ImportBitPermCommand.Generate(_fixtures));

  private static GeneratedFile File(string path) => _generated.Value.Single(file => file.Path == path);

  private static JsonObject Body(string path) => (JsonObject)SpecDocument.Parse("permutations/" + path, File(path).Content).Body!;

  private static Dictionary<string, int> CostsOf(string preset)
    => Body($"costs/bitperm-{preset}.yaml")["costs"]!.AsArray().ToDictionary(cost => (string)cost!["primitive"]!, cost => (int)(long)cost!["cycles"]!);

  private static JsonElement[] Presets() {
    using var document = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(_fixtures, "presets.json")));
    return [.. document.RootElement.EnumerateArray().Select(preset => preset.Clone())];
  }

  /// <summary>A repository with the family's schema, the seed document and, unless left out, every generated document.</summary>
  private static SpecRepository Repository(bool generated = true) {
    var repository = new SpecRepository();
    repository.Write(SchemaPath, System.IO.File.ReadAllText(Path.Combine(_root, SchemaPath)));
    repository.Document("permutations/mappings/reverse8.yaml", System.IO.File.ReadAllText(Path.Combine(_root, "spec", "permutations", "mappings", "reverse8.yaml")));
    if (generated)
      foreach (var file in _generated.Value)
        repository.Document("permutations/" + file.Path, file.Content);
    return repository;
  }

  /// <summary>A copy of the fixtures in the repository at <c>fixtures/</c> with only the named mappings and their vectors.</summary>
  private static string CopyFixtures(SpecRepository repository, params string[] mappings) {
    foreach (var name in new[] { "pin.json", "limits.json", "presets.json" })
      repository.Write("fixtures/" + name, System.IO.File.ReadAllText(Path.Combine(_fixtures, name)));
    Directory.CreateDirectory(Path.Combine(repository.Root, "fixtures", "mappings"));
    foreach (var mapping in mappings) {
      repository.Write($"fixtures/mappings/{mapping}.json", System.IO.File.ReadAllText(Path.Combine(_fixtures, "mappings", mapping + ".json")));
      repository.Write($"fixtures/vectors/{mapping}.json", System.IO.File.ReadAllText(Path.Combine(_fixtures, "vectors", mapping + ".json")));
    }
    return Path.Combine(repository.Root, "fixtures");
  }

  private static (int Exit, string Output, string Error) Run(string directory, params string[] args) {
    var (output, error) = (new StringWriter(), new StringWriter());
    var exit = ImportBitPermCommand.Run(args, output, error, directory);
    return (exit, output.ToString(), error.ToString());
  }

  // --- 1: the full import ---

  [Test]
  public void Generate_YieldsFortyFourMappingsFourGrammarsAndTwentyFiveCostTables() {
    var paths = _generated.Value.Select(file => file.Path).ToArray();
    Assert.That(paths.Count(path => path.StartsWith("mappings/bitperm-", StringComparison.Ordinal)), Is.EqualTo(44));
    Assert.That(paths.Count(path => path.StartsWith("grammars/bitperm-", StringComparison.Ordinal)), Is.EqualTo(4));
    Assert.That(paths.Count(path => path.StartsWith("costs/bitperm-", StringComparison.Ordinal)), Is.EqualTo(25));
    Assert.That(paths, Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal));
    Assert.That(_generated.Value.All(file => !file.Content.Contains('\r', StringComparison.Ordinal)), Is.True);
  }

  [Test, Category("Slow")]
  public void Generate_TheTreeWithTheSeedValidates_SeventyFourDocumentsNoErrors() {
    using var repository = Repository();
    var result = repository.Validate("permutations");
    Assert.That(result.Diagnostics.Select(DiagnosticRenderer.ToText), Is.Empty);
    Assert.That(result.Documents, Is.EqualTo(74));
  }

  [Test]
  public void Generate_IsDeterministic() {
    var again = ImportBitPermCommand.Generate(_fixtures);
    Assert.That(again, Is.EqualTo(_generated.Value));
  }

  [Test]
  public void CheckedInTree_IsUpToDateWithTheFixtures() {
    var (exit, output, error) = Run(_root, "import-bitperm", "--check");
    Assert.That((exit, error), Is.EqualTo((0, "")));
    Assert.That(output, Does.Contain("73 documents up to date"));
  }

  // --- 2, 5, 6: the cost tables against presets.json ---

  [Test]
  public void Costs_EqualPresetsJsonFieldByField_ZeroDifferences() {
    var differences = new List<string>();
    foreach (var preset in Presets()) {
      var id = preset.GetProperty("id").GetString()!;
      var body = Body($"costs/bitperm-{id}.yaml");
      var costs = CostsOf(id);
      if ((string)body["cpu"]! != preset.GetProperty("name").GetString())
        differences.Add($"{id}: cpu");
      var expected = new Dictionary<string, int>();
      foreach (var (key, primitives) in ImportBitPermCommand.CostKeys) {
        var value = preset.GetProperty("costs").GetProperty(key).GetInt32();
        var enabled = !preset.GetProperty("enables").TryGetProperty(key, out var flag) || flag.GetBoolean();
        if (value != 999 && enabled)
          foreach (var primitive in primitives)
            expected[primitive] = value;
      }

      if (!costs.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
        differences.Add($"{id}: {string.Join(", ", costs)} != {string.Join(", ", expected)}");
    }

    Assert.That(differences, Is.Empty);
  }

  [Test]
  public void Custom_HasEveryMappedPrimitiveWithThePresetsValue() {
    var preset = Presets().Single(entry => entry.GetProperty("id").GetString() == "custom");
    var costs = CostsOf("custom");
    foreach (var (key, primitives) in ImportBitPermCommand.CostKeys)
      foreach (var primitive in primitives)
        Assert.That(costs[primitive], Is.EqualTo(preset.GetProperty("costs").GetProperty(key).GetInt32()), primitive);
  }

  [TestCase("z80", 7)]
  [TestCase("custom", 16)]
  public void Costs_FewestAndMostOperations(string preset, int entries) => Assert.That(CostsOf(preset), Has.Count.EqualTo(entries));

  [TestCase("modern", "pext", 3)]
  [TestCase("zen1_2", "pdep", 18)]
  [TestCase("6502", "lookup-table", 5)]
  [TestCase("arm_a64", "bit-reverse", 1)]
  [TestCase("gamecube", "rotate-mask", 1)]
  public void Costs_CarryThePresetsCycles(string preset, string primitive, int cycles) => Assert.That(CostsOf(preset)[primitive], Is.EqualTo(cycles));

  [Test]
  public void Costs_6502_ListsNoUnavailableOperation_AndNothingAnywhereIs999() {
    Assert.That(CostsOf("6502").Keys, Has.None.AnyOf("multiply", "pext", "pdep", "byte-shuffle", "byte-swap"));
    Assert.That(CostsOf("6502"), Is.EquivalentTo(new Dictionary<string, int> { ["shift"] = 2, ["rotate"] = 2, ["lookup-table"] = 5, ["and"] = 2, ["or"] = 2, ["xor"] = 2, ["not"] = 2 }));
    Assert.That(_generated.Value.Where(file => file.Path.StartsWith("costs/", StringComparison.Ordinal)).SelectMany(file => CostsOf(file.Path[14..^5]).Values), Has.None.EqualTo(999));
  }

  [Test]
  public void Costs_AnEnabledOperationCosting999_IsOmitted() {
    using var document = JsonDocument.Parse("""{"id": "x", "costs": {"shift": 1, "bmi": 999}, "enables": {"shift": true, "bmi": true}}""");
    Assert.That(ImportBitPermCommand.Costs(document.RootElement).Select(pair => pair.Key), Is.EqualTo(new[] { "shift" }));
  }

  [Test]
  public void Costs_ADisabledOperation_IsOmitted_AndAKeyWithoutFlagCounts() {
    using var document = JsonDocument.Parse("""{"id": "x", "costs": {"mul": 3, "lut": 4}, "enables": {"mul": false}}""");
    Assert.That(ImportBitPermCommand.Costs(document.RootElement), Is.EqualTo(new[] { new KeyValuePair<string, int>("lookup-table", 4) }));
  }

  [Test]
  public void Costs_AnUnknownKey_IsRefusedWithItsName() {
    using var document = JsonDocument.Parse("""{"id": "modern", "costs": {"shift": 1, "clmul": 2}, "enables": {}}""");
    Assert.That(() => ImportBitPermCommand.Costs(document.RootElement),
      Throws.TypeOf<BitPermImportException>().With.Message.EqualTo("preset 'modern' has unknown cost key 'clmul'"));
  }

  // --- 3, 4, 7: the documents' shapes ---

  [TestCase("pattern-reverse-8", true, false)]
  [TestCase("pattern-set_all_0-8", false, false)]
  [TestCase("pattern-clear-8", false, true)]
  [TestCase("test-33-duplicate-same-source-to-multiple-destinations", false, true)]
  public void Mapping_BijectiveAndMaskByClass(string fixture, bool bijective, bool masked) {
    var body = Body($"mappings/bitperm-{fixture}.yaml");
    Assert.That((bool)body["bijective"]!, Is.EqualTo(bijective));
    Assert.That(body["vectors"]!.AsArray().Select(vector => ((JsonObject)vector!).ContainsKey("mask")), Has.All.EqualTo(masked));
  }

  [Test]
  public void Mapping_VectorsAreZeroAllOnesAndTheFirstTwoSingleBitInputs_FromBitPermsEvaluation() {
    var vectors = Body("mappings/bitperm-pattern-reverse-8.yaml")["vectors"]!.AsArray().Select(vector => ((string)vector!["input"]!, (string)vector!["output"]!));
    Assert.That(vectors, Is.EqualTo(new[] { ("0x00", "0x00"), ("0xFF", "0xFF"), ("0x01", "0x80"), ("0x02", "0x40") }));
  }

  [Test]
  public void Mapping_IdAndMetadata_NameTheFixtureAndThePin() {
    var file = File("mappings/bitperm-pattern-reverse-8.yaml");
    var document = SpecDocument.Parse("permutations/" + file.Path, file.Content);
    Assert.That(document.Meta!.Id, Is.EqualTo("PERMUTATIONS.mapping.bitperm-pattern-reverse-8"));
    Assert.That(file.Content, Does.Contain("status: draft\n").And.Contain("issue: PERM-SPEC\n"));
    Assert.That(file.Content, Does.Contain("mappings/pattern-reverse-8.json").And.Contain("Hawkynt/Hawkynt.github.io@b3a8a8e92cbca83475986391c3cac56f0b185d29:BitPerm/index.html"));
  }

  [Test]
  public void Mapping_AllDontCare_EveryMaskIsZero() {
    var vectors = Body("mappings/bitperm-pattern-clear-8.yaml")["vectors"]!.AsArray();
    Assert.That(vectors.Select(vector => (string)vector!["mask"]!), Has.All.EqualTo("0x00"));
  }

  [Test]
  public void Mapping_WideOutputs_AreWrappedSixteenToALine_AndReadBack() {
    var body = Body("mappings/bitperm-pattern-reverse-128.yaml");
    Assert.That(body["outputs"]!.AsArray().Select(entry => (int)(long)entry!), Is.EqualTo(Enumerable.Range(0, 128).Reverse()));
    Assert.That(File("mappings/bitperm-pattern-reverse-128.yaml").Content.Split('\n').Count(line => line.StartsWith("    ", StringComparison.Ordinal) && !line.StartsWith("    -", StringComparison.Ordinal)), Is.EqualTo(7));
  }

  [Test]
  public void SchemaFeatures_CostsWithoutBytes_GrammarsWithoutTarget_ArmUsesBitReverse() {
    var costs = _generated.Value.Where(file => file.Path.StartsWith("costs/", StringComparison.Ordinal)).SelectMany(file => Body(file.Path)["costs"]!.AsArray());
    Assert.That(costs.Select(cost => ((JsonObject)cost!).ContainsKey("bytes")), Has.All.False);
    var grammars = _generated.Value.Where(file => file.Path.StartsWith("grammars/", StringComparison.Ordinal)).Select(file => Body(file.Path));
    Assert.That(grammars.Select(grammar => grammar.ContainsKey("target")), Has.All.False);
    Assert.That(CostsOf("arm_a64"), Contains.Key("bit-reverse"));
  }

  [TestCase("composite", new[] { "shift", "rotate", "and", "or", "xor", "multiply" }, 64)]
  [TestCase("bmi2", new[] { "pext", "pdep", "or", "xor" }, 4)]
  [TestCase("byte-shuffle", new[] { "byte-shuffle" }, 1)]
  [TestCase("lut", new[] { "lookup-table" }, 1)]
  public void Grammar_HasItsPrimitivesAndDepth_AndReferencesTheProofRequirements(string name, string[] primitives, int depth) {
    var body = Body($"grammars/bitperm-{name}.yaml");
    Assert.That(body["primitives"]!.AsArray().Select(primitive => (string)primitive!), Is.EqualTo(primitives));
    Assert.That((long)body["maxDepth"]!, Is.EqualTo(depth));
    Assert.That(File($"grammars/bitperm-{name}.yaml").Content, Does.Contain("spec/permutations/proof-requirements.md"));
    Assert.That(System.IO.File.Exists(Path.Combine(_root, "spec", "permutations", "proof-requirements.md")), Is.True);
  }

  [Test]
  public void Cost_EvidenceIsDocumented_CitingThePagesComment() {
    var evidence = Body("costs/bitperm-zen1_2.yaml")["evidence"]!;
    Assert.That((string)evidence["kind"]!, Is.EqualTo("documented"));
    Assert.That((string)evidence["reference"]!, Is.EqualTo("BitPerm PRESETS at b3a8a8e9, citing AMD Zen1/2: PEXT/PDEP microcoded (~18 cycles) - Source: uops.info, chessprogramming.org/BMI2"));
  }

  [Test]
  public void Cost_APresetWithoutComment_CitesOnlyThePin() {
    using var repository = new SpecRepository();
    var fixtures = CopyFixtures(repository);
    var presets = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(fixtures, "presets.json")))!.AsArray();
    ((JsonObject)presets[0]!).Remove("comment");
    System.IO.File.WriteAllText(Path.Combine(fixtures, "presets.json"), presets.ToJsonString());
    var file = ImportBitPermCommand.Generate(fixtures).Single(generated => generated.Path == "costs/bitperm-modern.yaml");
    Assert.That((string)SpecDocument.Parse("permutations/" + file.Path, file.Content).Body!["evidence"]!["reference"]!, Is.EqualTo("BitPerm PRESETS at b3a8a8e9"));
  }

  // --- 9: a tampered vector ---

  [Test, Category("Slow")]
  public void TamperedVector_IsRefusedByVectorConsistent() {
    using var repository = Repository();
    var path = "permutations/mappings/bitperm-pattern-reverse-8.yaml";
    repository.Document(path, File("mappings/bitperm-pattern-reverse-8.yaml").Content.Replace("{input: \"0x01\", output: \"0x80\"}", "{input: \"0x01\", output: \"0x40\"}", StringComparison.Ordinal));
    Assert.That(repository.Lines("permutations"), Has.Exactly(1).Contains("PLY0046").And.Contains("the mapping gives 0x80 for input 0x01, the document says 0x40 (rule: vector-consistent)"));
  }

  // --- 8, 10: the command line ---

  [Test]
  public void Run_WritesTheDocuments_ThenFindsThemUpToDate_ThenWritesNothing() {
    using var repository = new SpecRepository();
    var fixtures = CopyFixtures(repository, "pattern-reverse-8");
    var first = Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "out");
    Assert.That(first, Is.EqualTo((0, "permutations import: 30 documents, 30 written, 0 deleted\n".Replace("\n", Environment.NewLine, StringComparison.Ordinal), "")));
    Assert.That(Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "out", "--check").Exit, Is.Zero);
    Assert.That(Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "out").Output, Does.Contain("30 documents, 0 written, 0 deleted"));
  }

  [Test]
  public void Run_DeletesGeneratedDocumentsNoLongerProduced_AndLeavesOthersAlone() {
    using var repository = new SpecRepository();
    var fixtures = CopyFixtures(repository, "pattern-reverse-8");
    repository.Write("out/mappings/bitperm-gone.yaml", "x").Write("out/mappings/reverse8.yaml", "kept");
    Assert.That(Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "out").Output, Does.Contain("1 deleted"));
    Assert.That(System.IO.File.Exists(Path.Combine(repository.Root, "out", "mappings", "bitperm-gone.yaml")), Is.False);
    Assert.That(System.IO.File.ReadAllText(Path.Combine(repository.Root, "out", "mappings", "reverse8.yaml")), Is.EqualTo("kept"));
  }

  [Test]
  public void Check_AnEditedCostDocument_ExitsOneNamingTheFile() {
    using var repository = new SpecRepository();
    var fixtures = CopyFixtures(repository);
    Assert.That(Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "out").Exit, Is.Zero);
    var cost = Path.Combine(repository.Root, "out", "costs", "bitperm-6502.yaml");
    System.IO.File.WriteAllText(cost, System.IO.File.ReadAllText(cost).Replace("cycles: 5", "cycles: 4", StringComparison.Ordinal));
    var (exit, _, error) = Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "out", "--check");
    Assert.That(exit, Is.EqualTo(1));
    Assert.That(error, Does.Contain("permutations import: out/costs/bitperm-6502.yaml differs from the fixtures"));
    Assert.That(System.IO.File.ReadAllText(cost), Does.Contain("cycles: 4"), "--check writes nothing");
  }

  [Test]
  public void Check_CrLfCheckout_IsUpToDate() {
    using var repository = new SpecRepository();
    var fixtures = CopyFixtures(repository);
    Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "out");
    var cost = Path.Combine(repository.Root, "out", "costs", "bitperm-6502.yaml");
    System.IO.File.WriteAllText(cost, System.IO.File.ReadAllText(cost).Replace("\n", "\r\n", StringComparison.Ordinal));
    Assert.That(Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "out", "--check").Exit, Is.Zero);
  }

  [Test]
  public void Check_MissingAndStaleDocuments_AreReported() {
    using var repository = new SpecRepository();
    var fixtures = CopyFixtures(repository);
    Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "out");
    System.IO.File.Delete(Path.Combine(repository.Root, "out", "grammars", "bitperm-lut.yaml"));
    repository.Write("out/costs/bitperm-old.yaml", "x");
    var (exit, _, error) = Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "out/", "--check");
    Assert.That(exit, Is.EqualTo(1));
    Assert.That(error, Does.Contain("out/grammars/bitperm-lut.yaml is missing").And.Contain("out/costs/bitperm-old.yaml is no longer generated"));
  }

  [Test]
  public void Check_AnEmptyOutputDirectory_ReportsEveryDocumentMissing() {
    using var repository = new SpecRepository();
    var fixtures = CopyFixtures(repository);
    var (exit, _, error) = Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "nowhere", "--check");
    Assert.That(exit, Is.EqualTo(1));
    Assert.That(error.Split('\n', StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(29));
  }

  [Test]
  public void Run_AnUnwritableOutput_ExitsOne() {
    using var repository = new SpecRepository();
    var fixtures = CopyFixtures(repository);
    repository.Write("out", "a file, not a directory");
    var (exit, _, error) = Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "out");
    Assert.That(exit, Is.EqualTo(1));
    Assert.That(error, Does.StartWith("permutations import: the output directory 'out' cannot be written: "));
  }

  [Test]
  public void Run_AnUnknownCostKey_StopsTheImportWithTheKeyNamed() {
    using var repository = new SpecRepository();
    var fixtures = CopyFixtures(repository);
    var presets = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(fixtures, "presets.json")))!.AsArray();
    presets.Single(preset => (string)preset!["id"]! == "modern")!["costs"]!["clmul"] = 2;
    System.IO.File.WriteAllText(Path.Combine(fixtures, "presets.json"), presets.ToJsonString());
    var (exit, _, error) = Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "out");
    Assert.That((exit, error.TrimEnd()), Is.EqualTo((1, "permutations import: preset 'modern' has unknown cost key 'clmul'")));
    Assert.That(Directory.Exists(Path.Combine(repository.Root, "out")), Is.False, "nothing is written");
  }

  private static IEnumerable<TestCaseData> BrokenFixtures() {
    yield return new TestCaseData("presets.json", null, "fixture '").SetName("Run_BrokenFixture_MissingFile");
    yield return new TestCaseData("presets.json", "[", "").SetName("Run_BrokenFixture_MalformedJson");
    yield return new TestCaseData("limits.json", "{}", "limits.json has no 'MAX_SOLVER_ITERATIONS'").SetName("Run_BrokenFixture_MissingProperty");
    yield return new TestCaseData("limits.json", """{"MAX_SOLVER_ITERATIONS": "64"}""", "").SetName("Run_BrokenFixture_WrongKind");
    yield return new TestCaseData("mappings/m.json", """{"name": "m", "source": {"line": 1}, "width": 2, "mapping": [{"type": "collision"}]}""", "").SetName("Run_BrokenFixture_InvalidMapping");
    yield return new TestCaseData("vectors/m.json", """{"vectors": [{"input": "0x0", "output": "0x0", "mask": "0x3"}, {"input": "0x1", "output": "0x1", "mask": "0x3"}, {"input": "0x2", "output": "0x2", "mask": "0x3"}]}""", "vectors/m.json has no vector for the all-ones input").SetName("Run_BrokenFixture_NoAllOnesVector");
    yield return new TestCaseData("vectors/m.json", """{"vectors": [{"input": "0x1", "output": "0x1", "mask": "0x3"}, {"input": "0x3", "output": "0x3", "mask": "0x3"}, {"input": "0x2", "output": "0x2", "mask": "0x3"}]}""", "vectors/m.json has no vector for input 0").SetName("Run_BrokenFixture_NoZeroVector");
    yield return new TestCaseData("vectors/m.json", """{"vectors": [{"input": "0x0", "output": "0x0", "mask": "0x3"}, {"input": "0x3", "output": "0x3", "mask": "0x3"}, {"input": "0x1", "output": "0x1", "mask": "0x3"}]}""", "vectors/m.json has fewer than two single-bit inputs").SetName("Run_BrokenFixture_OneSingleBitVector");
  }

  [TestCaseSource(nameof(BrokenFixtures))]
  public void Run_BrokenFixture_ExitsOneWithoutWriting(string file, string? content, string message) {
    using var repository = new SpecRepository();
    var fixtures = CopyFixtures(repository);
    if (file.EndsWith("m.json", StringComparison.Ordinal)) {
      repository.Write("fixtures/mappings/m.json", """{"name": "m", "source": {"line": 1}, "width": 2, "mapping": [{"type": "src", "val": 0, "inv": false}, {"type": "src", "val": 1, "inv": false}]}""");
      repository.Write("fixtures/vectors/m.json", """{"vectors": [{"input": "0x0", "output": "0x0", "mask": "0x3"}, {"input": "0x3", "output": "0x3", "mask": "0x3"}, {"input": "0x1", "output": "0x1", "mask": "0x3"}, {"input": "0x2", "output": "0x2", "mask": "0x3"}]}""");
    }

    var path = Path.Combine(fixtures, file);
    if (content is null)
      System.IO.File.Delete(path);
    else
      System.IO.File.WriteAllText(path, content);

    var (exit, _, error) = Run(repository.Root, "import-bitperm", "--fixtures", fixtures, "--out", "out");
    Assert.That(exit, Is.EqualTo(1));
    Assert.That(error, Does.StartWith("permutations import: " + message));
    Assert.That(Directory.Exists(Path.Combine(repository.Root, "out")), Is.False);
  }

  [TestCase(new string[0], "permutations needs 'import-bitperm'")]
  [TestCase(new[] { "export" }, "unknown permutations command 'export'")]
  [TestCase(new[] { "import-bitperm", "--force" }, "unknown option '--force'")]
  [TestCase(new[] { "import-bitperm", "--out" }, "option '--out' requires a value")]
  [TestCase(new[] { "import-bitperm", "--out", "a", "--out", "b" }, "option '--out' is given more than once")]
  [TestCase(new[] { "import-bitperm", "--check", "--check" }, "option '--check' is given more than once")]
  public void Run_ABadCommandLine_IsAUsageError(string[] args, string message) {
    var (exit, _, error) = Run(_root, args);
    Assert.That(exit, Is.EqualTo(2));
    Assert.That(error, Does.Contain(message));
  }

  [Test]
  public void Run_NullArguments_Throw() {
    Assert.That(() => ImportBitPermCommand.Run(null!, TextWriter.Null, TextWriter.Null), Throws.ArgumentNullException);
    Assert.That(() => ImportBitPermCommand.Run([], null!, TextWriter.Null), Throws.ArgumentNullException);
    Assert.That(() => ImportBitPermCommand.Run([], TextWriter.Null, null!), Throws.ArgumentNullException);
    Assert.That(() => ImportBitPermCommand.Generate(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Program_DispatchesTheVerb_AndListsItInTheUsage() {
    var (output, error) = (new StringWriter(), new StringWriter());
    Assert.That(Program.Run(["permutations"], output, error), Is.EqualTo(Program.UsageError));
    Assert.That(error.ToString(), Does.Contain("permutations needs 'import-bitperm'"));
    var usage = new StringWriter();
    Program.Run([], usage, TextWriter.Null);
    Assert.That(usage.ToString(), Does.Contain("permutations import-bitperm [--fixtures <dir>] [--out <dir>] [--check]"));
  }

}
