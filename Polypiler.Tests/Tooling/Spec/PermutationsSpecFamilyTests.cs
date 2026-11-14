using System.IO;
using System.Linq;
using System.Text.Json;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Spec.Families;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// <see cref="PermutationsSpecFamily"/> and <c>spec/schemas/permutations.schema.json</c>, run through the
/// whole engine: the fixtures of <c>tests/fixtures/spec/permutations/</c>, the seed document of
/// <c>spec/permutations/</c> and one document per row of the issue's test design.
/// </summary>
[TestFixture]
public sealed class PermutationsSpecFamilyTests {

  private const string Fixtures = "tests/fixtures/spec/permutations";

  private const string Schema = "spec/schemas/permutations.schema.json";

  private static string RepositoryFile(string path) => Path.Combine(PrototypeFixture.RepositoryRoot, path);

  private static SpecRepository Repository() {
    var repository = new SpecRepository();
    repository.Write(Schema, File.ReadAllText(RepositoryFile(Schema)));
    return repository;
  }

  /// <summary>The folder of a document that a fixture's ID <c>PERMUTATIONS.&lt;kind&gt;.&lt;name&gt;</c> places it in: <c>permutations/&lt;kind&gt;s/&lt;name&gt;.yaml</c>.</summary>
  public static string PlaceOf(string fixtureText) {
    var id = SpecDocument.Parse("permutations/fixture.yaml", fixtureText).Id!;
    var parts = id.Split('.', 3);
    return $"permutations/{parts[1]}s/{parts[2]}.yaml";
  }

  private static string Header(string id) => $"id: {id}\ntitle: Test\nversion: 1\nstatus: draft\nowner: Hawkynt\nissue: PERM-MODEL\ntests: []\n"
    + "introducedIn: P2\ncompatibility: none\nreviewedRevision: 0\nnormative: \"\"\nspec:\n";

  /// <summary>The diagnostics of one document with the given body lines, at a path that follows the layout unless one is given.</summary>
  private static string[] Lines(string body, string path = "permutations/mappings/test.yaml", string? id = null) {
    using var repository = Repository();
    var parts = path.Split('/');
    id ??= $"PERMUTATIONS.{parts[1].TrimEnd('s')}.{Path.GetFileNameWithoutExtension(path)}";
    repository.Document(path, Header(id) + body);
    return repository.Lines("permutations");
  }

  private static string Mapping(int width, string outputs, bool bijective, params string[] vectors)
    => $"  kind: mapping\n  inputWidth: {width}\n  outputs: {outputs}\n  bijective: {(bijective ? "true" : "false")}\n  vectors:\n"
      + string.Concat(vectors.Select(vector => $"    - {{{vector}}}\n"));

  private static string Grammar(string extra) => $"  kind: grammar\n  primitives: [shift]\n{extra}";

  private static string Cost(string costs, string evidence) => $"  kind: cost\n  cpu: z80\n  costs: {costs}\n  evidence: {evidence}\n";

  private const string Documented = "{kind: documented, reference: \"Zilog, Z80 CPU User Manual UM0080\"}";

  [Test]
  public void Family_IsRegistered() {
    var family = SpecFamilies.All.OfType<PermutationsSpecFamily>().Single();
    Assert.That((family.Folder, family.SchemaPath), Is.EqualTo(("permutations", Schema)));
  }

  [Test]
  public void Schema_IsValidAgainstTheMetaSchema() {
    using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFile(Schema)));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  [Test]
  public void ValidFixtureTree_HasNoErrors() {
    var result = new SpecValidator(SpecFamilies.All).Run(SpecTree.Load(RepositoryFile($"{Fixtures}/valid"), PrototypeFixture.RepositoryRoot), "permutations");
    Assert.That((result.Documents, result.Diagnostics.Count), Is.EqualTo((1, 0)));
  }

  [Test]
  public void SeedDocument_IsTheValidFixtureAndValidates() {
    Assert.That(File.ReadAllText(RepositoryFile("spec/permutations/mappings/reverse8.yaml")), Is.EqualTo(File.ReadAllText(RepositoryFile($"{Fixtures}/valid/permutations/mappings/reverse8.yaml"))));
    var result = new SpecValidator(SpecFamilies.All).Run(SpecTree.Load(RepositoryFile("spec"), PrototypeFixture.RepositoryRoot), "permutations");
    Assert.That(result.Diagnostics, Is.Empty);
  }

  [Test]
  public void EveryKindPrimitiveAndEvidence_Validates() {
    using var repository = Repository();
    var source = RepositoryFile($"{Fixtures}/kinds");
    foreach (var file in Directory.EnumerateFiles(source, "*.yaml", SearchOption.AllDirectories))
      repository.Document(Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(file));
    repository.Write("docs/benchmarks/mos6502-shift.json", "{}");
    var result = repository.Validate("permutations");
    Assert.That((result.Documents, result.Diagnostics.Count), Is.EqualTo((5, 0)), string.Join("\n", repository.Lines("permutations")));
  }

  [Test]
  public void ValidFixtures_UseEveryPrimitiveOfTheSchema() {
    using var schema = JsonDocument.Parse(File.ReadAllText(RepositoryFile(Schema)));
    var declared = schema.RootElement.GetProperty("$defs").GetProperty("primitive").GetProperty("enum").EnumerateArray().Select(value => value.GetString()!).ToArray();
    var used = SpecDocument.Parse("permutations/x.yaml", File.ReadAllText(RepositoryFile($"{Fixtures}/kinds/permutations/grammars/every-primitive.yaml"))).Body!["primitives"]!.AsArray().Select(value => (string)value!).ToArray();
    Assert.That(used, Is.EquivalentTo(declared).And.Length.EqualTo(17));
  }

  [TestCase("bijective-consistent", "permutations/mappings/spread.yaml(15,22): error PLY0046: mapping is declared bijective but input bit 3 is used twice (rule: bijective-consistent)")]
  [TestCase("index-in-range", "permutations/mappings/past-the-input.yaml(15,22): error PLY0046: outputs[3] reads input bit 8, inputWidth is 8 (rule: index-in-range)")]
  [TestCase("vector-consistent", "permutations/mappings/wrong-vector.yaml(19,31): error PLY0046: vectors[1]: the mapping gives 0x2D for input 0xB4, the document says 0x2C (rule: vector-consistent)")]
  [TestCase("vector-width", "permutations/mappings/wide-vector.yaml(18,15): error PLY0046: vectors[0].input 0x100 does not fit inputWidth 8 (rule: vector-width)")]
  [TestCase("cost-evidence", "permutations/costs/mos6502.yaml(17,41): error PLY0046: measured cost evidence docs/benchmarks/6502-shift.json does not exist (rule: cost-evidence)")]
  [TestCase("unique-primitive", "permutations/costs/twice.yaml(17,19): error PLY0046: costs[1]: primitive 'shift' is listed twice (rule: unique-primitive)")]
  [TestCase("layout", "permutations/costs/layout.yaml(13,9): error PLY0046: kind 'mapping' must live in spec/permutations/mappings, found spec/permutations/costs (rule: layout)")]
  [TestCase("one-vector", "permutations/mappings/one-vector.yaml(18,5): error PLY0040: /spec/vectors: Value should have at least 2 items")]
  [TestCase("extra-property", "permutations/mappings/extra-property.yaml(17,12): error PLY0040: /spec/inverse: property 'inverse' is not allowed")]
  public void InvalidFixture_IsRefusedWithOneDiagnostic(string fixture, string expected) {
    var text = File.ReadAllText(RepositoryFile($"{Fixtures}/invalid/{fixture}.yaml"));
    using var repository = Repository();
    repository.Document(PlaceOf(text), text);
    Assert.That(repository.Lines("permutations"), Is.EqualTo(new[] { expected }));
  }

  [TestCase("kind", "/spec", "kind")]
  [TestCase("input-width", "/spec", "inputWidth")]
  [TestCase("outputs", "/spec", "outputs")]
  [TestCase("bijective", "/spec", "bijective")]
  [TestCase("vectors", "/spec", "vectors")]
  [TestCase("vector-input", "/spec/vectors/0", "input")]
  [TestCase("vector-output", "/spec/vectors/0", "output")]
  [TestCase("mask", "/spec/vectors/0", "mask")]
  [TestCase("primitives", "/spec", "primitives")]
  [TestCase("max-depth", "/spec", "maxDepth")]
  [TestCase("cpu", "/spec", "cpu")]
  [TestCase("costs", "/spec", "costs")]
  [TestCase("cost-primitive", "/spec/costs/0", "primitive")]
  [TestCase("cost-cycles", "/spec/costs/0", "cycles")]
  [TestCase("evidence", "/spec", "evidence")]
  [TestCase("evidence-kind", "/spec/evidence", "kind")]
  [TestCase("evidence-reference", "/spec/evidence", "reference")]
  public void MissingRequiredProperty_IsASchemaViolationAtItsParent(string fixture, string pointer, string property) {
    var text = File.ReadAllText(RepositoryFile($"{Fixtures}/invalid/missing-{fixture}.yaml"));
    using var repository = Repository();
    repository.Document(PlaceOf(text), text);
    var line = repository.Lines("permutations").Single();
    Assert.That(line, Does.Contain($"error PLY0040: {pointer}: Required properties").And.Contain($"\"{property}\""));
  }

  [Test]
  public void EveryRequiredPropertyOfTheSchema_HasAMissingFixture() {
    var fixtures = Directory.GetFiles(RepositoryFile($"{Fixtures}/invalid"), "missing-*.yaml").Length;
    Assert.That(fixtures, Is.EqualTo(17));
  }

  // Row 2: every form of an output entry.
  [TestCase("[{not: 0}]", "input: \"0x0\", output: \"0x1\"", "input: \"0x1\", output: \"0x0\"")]
  [TestCase("[{const: 0}]", "input: \"0x0\", output: \"0x0\"", "input: \"0x1\", output: \"0x0\"")]
  [TestCase("[{const: 1}]", "input: \"0x0\", output: \"0x1\"", "input: \"0x1\", output: \"0x1\"")]
  [TestCase("[{dontCare: true}]", "input: \"0x0\", output: \"0x0\", mask: \"0x0\"", "input: \"0x1\", output: \"0x1\", mask: \"0x0\"")]
  public void NonPlainEntry_IsValidUnlessDeclaredBijective(string outputs, string first, string second) {
    Assert.That(Lines(Mapping(1, outputs, false, first, second)), Is.Empty);
    Assert.That(Lines(Mapping(1, outputs, true, first, second)), Is.EqualTo(new[] {
      "permutations/mappings/test.yaml(15,13): error PLY0046: mapping is declared bijective but outputs[0] is not a plain input bit (rule: bijective-consistent)",
    }));
  }

  // Row 3: routings that are no bijection.
  [TestCase(1, "[0, 0]", "input: \"0x0\", output: \"0x0\"", "input: \"0x1\", output: \"0x3\"", "(15,16): error PLY0046: mapping is declared bijective but input bit 0 is used twice")]
  [TestCase(2, "[0]", "input: \"0x0\", output: \"0x0\"", "input: \"0x3\", output: \"0x1\"", "(15,12): error PLY0046: mapping is declared bijective but it has 1 outputs for inputWidth 2")]
  [TestCase(2, "[0, 1, 0]", "input: \"0x1\", output: \"0x5\"", "input: \"0x2\", output: \"0x2\"", "(15,19): error PLY0046: mapping is declared bijective but input bit 0 is used twice")]
  [TestCase(2, "[1, 0, 1]", "input: \"0x1\", output: \"0x2\"", "input: \"0x2\", output: \"0x5\"", "(15,19): error PLY0046: mapping is declared bijective but input bit 1 is used twice")]
  public void Routing_IsValidOnlyWhenNotDeclaredBijective(int width, string outputs, string first, string second, string declaredBijective) {
    Assert.That(Lines(Mapping(width, outputs, false, first, second)), Is.Empty);
    Assert.That(Lines(Mapping(width, outputs, true, first, second)), Is.EqualTo(new[] { $"permutations/mappings/test.yaml{declaredBijective} (rule: bijective-consistent)" }));
  }

  [Test]
  public void Permutation_DeclaredNotBijective_IsRefused()
    => Assert.That(Lines(Mapping(2, "[1, 0]", false, "input: \"0x1\", output: \"0x2\"", "input: \"0x3\", output: \"0x3\"")), Is.EqualTo(new[] {
      "permutations/mappings/test.yaml(16,14): error PLY0046: mapping is a bijection of its input bits but is declared not bijective (rule: bijective-consistent)",
    }));

  // Row 4: an inverted bit is evaluated.
  [Test]
  public void InvertedBit_IsEvaluated() {
    Assert.That(Lines(Mapping(1, "[{not: 0}]", false, "input: \"0x00\", output: \"0x01\"", "input: \"0x01\", output: \"0x00\"")), Is.Empty);
    Assert.That(Lines(Mapping(1, "[{not: 0}]", false, "input: \"0x00\", output: \"0x00\"", "input: \"0x01\", output: \"0x01\"")), Is.EqualTo(new[] {
      "permutations/mappings/test.yaml(18,31): error PLY0046: vectors[0]: the mapping gives 0x1 for input 0x00, the document says 0x00 (rule: vector-consistent)",
      "permutations/mappings/test.yaml(19,31): error PLY0046: vectors[1]: the mapping gives 0x0 for input 0x01, the document says 0x01 (rule: vector-consistent)",
    }));
  }

  // Row 5: a don't-care bit is excluded by the mask, which a don't-care makes required.
  [Test]
  public void DontCareBit_IsExcludedByTheMask() {
    Assert.That(Lines(Mapping(1, "[0, {dontCare: true}]", false, "input: \"0x1\", output: \"0x3\", mask: \"0x1\"", "input: \"0x0\", output: \"0x0\", mask: \"0x1\"")), Is.Empty);
    Assert.That(Lines(Mapping(1, "[0, {dontCare: true}]", false, "input: \"0x1\", output: \"0x3\"", "input: \"0x0\", output: \"0x0\", mask: \"0x1\"")).Single(),
      Does.Contain("error PLY0040: /spec/vectors/0: Required properties [\"mask\"]"));
  }

  [Test]
  public void DontCareBit_IsExcludedEvenWhereTheMaskCoversIt()
    => Assert.That(Lines(Mapping(1, "[0, {dontCare: true}]", false, "input: \"0x1\", output: \"0x1\", mask: \"0x3\"", "input: \"0x0\", output: \"0x2\", mask: \"0x3\"")), Is.Empty);

  [Test]
  public void MaskedMismatch_ReportsTheMaskedValue()
    => Assert.That(Lines(Mapping(1, "[0, {dontCare: true}, {const: 1}]", false, "input: \"0x1\", output: \"0x0\", mask: \"0x5\"", "input: \"0x0\", output: \"0x4\", mask: \"0x5\"")), Is.EqualTo(new[] {
      "permutations/mappings/test.yaml(18,30): error PLY0046: vectors[0]: the mapping gives 0x5 for input 0x1, the document says 0x0 (rule: vector-consistent)",
    }));

  // Row 7: the limits of inputWidth.
  [TestCase(0, false)]
  [TestCase(1, true)]
  [TestCase(4096, true)]
  [TestCase(4097, false)]
  public void InputWidth_Boundaries(int width, bool valid) {
    var lines = Lines(Mapping(width, "[0, 0]", false, "input: \"0x0\", output: \"0x0\"", "input: \"0x1\", output: \"0x3\""));
    if (valid)
      Assert.That(lines, Is.Empty);
    else
      Assert.That(lines.Single(), Does.Contain("error PLY0040: /spec/inputWidth:"));
  }

  [Test]
  public void WidestMapping_IsEvaluated() {
    var outputs = "[" + string.Join(", ", Enumerable.Range(0, 4096).Select(index => 4095 - index)) + "]";
    var top = "0x8" + new string('0', 1023);
    Assert.That(Lines(Mapping(4096, outputs, true, $"input: \"0x1\", output: \"{top}\"", $"input: \"{top}\", output: \"0x1\"")), Is.Empty);
  }

  [Test]
  public void MoreThan4096Outputs_IsASchemaViolation()
    => Assert.That(Lines(Mapping(1, "[" + string.Join(", ", Enumerable.Repeat(0, 4097)) + "]", false, "input: \"0x0\", output: \"0x0\"", "input: \"0x0\", output: \"0x0\"")).Single(),
      Does.Contain("error PLY0040: /spec/outputs:"));

  // Row 8: the last input bit and the one past it, plain and inverted.
  [TestCase("[0, 1, 2, 3, 4, 5, 6, 7]", null)]
  [TestCase("[0, 1, 2, 3, 4, 5, 6, 8]", "(15,34): error PLY0046: outputs[7] reads input bit 8, inputWidth is 8 (rule: index-in-range)")]
  [TestCase("[0, 1, 2, 3, 4, 5, 6, {not: 9}]", "(15,40): error PLY0046: outputs[7] reads input bit 9, inputWidth is 8 (rule: index-in-range)")]
  public void SourceIndex_Boundaries(string outputs, string? problem) {
    var lines = Lines(Mapping(8, outputs, true, "input: \"0x00\", output: \"0x00\"", "input: \"0x01\", output: \"0x01\""));
    Assert.That(lines, problem is null ? Is.Empty : Is.EqualTo(new[] { "permutations/mappings/test.yaml" + problem }));
  }

  // Row 9: the identity of width 1.
  [Test]
  public void IdentityOfWidthOne_IsBijective()
    => Assert.That(Lines(Mapping(1, "[0]", true, "input: \"0x00\", output: \"0x00\"", "input: \"0x01\", output: \"0x01\"")), Is.Empty);

  // Row 10: the number of vectors.
  [TestCase(1, false)]
  [TestCase(2, true)]
  public void VectorCount_Boundaries(int count, bool valid) {
    var lines = Lines(Mapping(1, "[0]", true, [.. Enumerable.Repeat("input: \"0x1\", output: \"0x1\"", count)]));
    Assert.That(lines.Length, Is.EqualTo(valid ? 0 : 1));
  }

  [TestCase("input: \"0x1\", output: \"0x4\"", "(18,30): error PLY0046: vectors[0].output 0x4 does not fit 2 outputs (rule: vector-width)")]
  [TestCase("input: \"0x1\", output: \"0x1\", mask: \"0x7\"", "(18,43): error PLY0046: vectors[0].mask 0x7 does not fit 2 outputs (rule: vector-width)")]
  [TestCase("input: \"0x2\", output: \"0x4\"", "(18,15): error PLY0046: vectors[0].input 0x2 does not fit inputWidth 1 (rule: vector-width)")]
  public void VectorTooWide_IsRefusedPerField(string vector, string problem) {
    var lines = Lines(Mapping(1, "[0, 0]", false, vector, "input: \"0x1\", output: \"0x3\""));
    Assert.That(lines[0], Is.EqualTo("permutations/mappings/test.yaml" + problem));
  }

  [TestCase("\"0x\"")]
  [TestCase("\"12\"")]
  [TestCase("\"0xG1\"")]
  [TestCase("12")]
  public void VectorThatIsNoHexString_IsASchemaViolation(string value)
    => Assert.That(Lines(Mapping(1, "[0]", true, $"input: {value}, output: \"0x1\"", "input: \"0x0\", output: \"0x0\"")).Single(), Does.Contain("error PLY0040: /spec/vectors/0/input:"));

  [TestCase("[-1]")]
  [TestCase("[4096]")]
  [TestCase("[{not: 0, const: 1}]")]
  [TestCase("[{const: 2}]")]
  [TestCase("[{dontCare: false}]")]
  [TestCase("[\"0\"]")]
  public void OutputThatIsNoEntry_IsASchemaViolation(string outputs)
    => Assert.That(Lines(Mapping(1, outputs, false, "input: \"0x0\", output: \"0x0\", mask: \"0x0\"", "input: \"0x1\", output: \"0x1\", mask: \"0x0\"")), Has.All.Contains("error PLY0040: /spec/outputs/0"));

  // Row 6: each kind, a grammar with and without target, both kinds of evidence.
  [Test]
  public void Grammar_WithAnExistingTarget_IsValid() {
    using var repository = Repository();
    repository.Document("permutations/grammars/test.yaml", Header("PERMUTATIONS.grammar.test") + Grammar("  maxDepth: 4\n  target: TARGETS.z80\n"));
    repository.Document("targets/z80.yaml", Header("TARGETS.z80") + "  {}\n");
    Assert.That(repository.Lines("permutations"), Is.Empty);
  }

  [Test]
  public void Grammar_WithAnUnknownTarget_IsPLY0043()
    => Assert.That(Lines(Grammar("  maxDepth: 4\n  target: TARGETS.z80\n"), "permutations/grammars/test.yaml"), Is.EqualTo(new[] {
      "permutations/grammars/test.yaml(16,11): error PLY0043: unknown spec id 'TARGETS.z80'",
    }));

  [Test]
  public void Grammar_WithATargetOfAnotherFamily_IsASchemaViolation()
    => Assert.That(Lines(Grammar("  maxDepth: 4\n  target: PERMUTATIONS.grammar.test\n"), "permutations/grammars/test.yaml").Single(), Does.Contain("error PLY0040: /spec/target:"));

  [Test]
  public void Grammar_WithAPrimitiveTwice_IsASchemaViolation()
    => Assert.That(Lines("  kind: grammar\n  primitives: [shift, shift]\n  maxDepth: 4\n", "permutations/grammars/test.yaml").Single(), Does.Contain("error PLY0040: /spec/primitives:"));

  [Test]
  public void Grammar_WithAnUnknownPrimitive_IsASchemaViolation()
    => Assert.That(Lines("  kind: grammar\n  primitives: [teleport]\n  maxDepth: 4\n", "permutations/grammars/test.yaml").Single(), Does.Contain("error PLY0040: /spec/primitives/0:"));

  // Row 11: maxDepth, cycles, bytes.
  [TestCase(0, false)]
  [TestCase(1, true)]
  [TestCase(64, true)]
  [TestCase(65, false)]
  public void MaxDepth_Boundaries(int depth, bool valid) {
    var lines = Lines(Grammar($"  maxDepth: {depth}\n"), "permutations/grammars/test.yaml");
    Assert.That(lines, valid ? Is.Empty : Has.Length.EqualTo(1).And.All.Contains("error PLY0040: /spec/maxDepth:"));
  }

  [TestCase("{primitive: shift, cycles: -1}", "/spec/costs/0/cycles")]
  [TestCase("{primitive: shift, cycles: 0}", null)]
  [TestCase("{primitive: shift, cycles: 4, bytes: 0}", "/spec/costs/0/bytes")]
  [TestCase("{primitive: shift, cycles: 4, bytes: 1}", null)]
  [TestCase("{primitive: shift, cycles: 4}", null)]
  public void CyclesAndBytes_Boundaries(string cost, string? pointer) {
    var lines = Lines(Cost($"[{cost}]", Documented), "permutations/costs/test.yaml");
    Assert.That(lines, pointer is null ? Is.Empty : Has.Length.EqualTo(1).And.All.Contains($"error PLY0040: {pointer}:"));
  }

  [TestCase("docs/benchmarks/run.json", true)]
  [TestCase("docs/benchmarks/nested/run.json", true)]
  [TestCase("docs/benchmarks/missing.json", false)]
  public void MeasuredEvidence_NeedsAnExistingBenchmarkFile(string reference, bool valid) {
    using var repository = Repository();
    repository.Write("docs/benchmarks/run.json", "{}").Write("docs/benchmarks/nested/run.json", "{}");
    repository.Document("permutations/costs/test.yaml", Header("PERMUTATIONS.cost.test") + Cost("[{primitive: shift, cycles: 4}]", $"{{kind: measured, reference: {reference}}}"));
    Assert.That(repository.Lines("permutations"), valid ? Is.Empty : Is.EqualTo(new[] {
      $"permutations/costs/test.yaml(16,41): error PLY0046: measured cost evidence {reference} does not exist (rule: cost-evidence)",
    }));
  }

  [TestCase("docs/run.json")]
  [TestCase("docs/benchmarks")]
  [TestCase("docs/benchmarks/../run.json")]
  [TestCase("\"\"")]
  public void MeasuredEvidence_OutsideTheBenchmarks_IsRefused(string reference) {
    using var repository = Repository();
    repository.Write("docs/run.json", "{}").Write("run.json", "{}");
    repository.Document("permutations/costs/test.yaml", Header("PERMUTATIONS.cost.test") + Cost("[{primitive: shift, cycles: 4}]", $"{{kind: measured, reference: {reference}}}"));
    Assert.That(repository.Lines("permutations").Single(), Does.Contain("is not a file under docs/benchmarks/ (rule: cost-evidence)"));
  }

  [TestCase("\"\"", false)]
  [TestCase("\"  \"", false)]
  [TestCase("\"Zilog UM0080\"", true)]
  public void DocumentedEvidence_NeedsACitation(string reference, bool valid)
    => Assert.That(Lines(Cost("[{primitive: shift, cycles: 4}]", $"{{kind: documented, reference: {reference}}}"), "permutations/costs/test.yaml"), valid ? Is.Empty : Is.EqualTo(new[] {
      "permutations/costs/test.yaml(16,43): error PLY0046: documented cost evidence needs a citation (rule: cost-evidence)",
    }));

  [Test]
  public void EvidenceOfAnUnknownKind_IsASchemaViolation()
    => Assert.That(Lines(Cost("[{primitive: shift, cycles: 4}]", "{kind: guessed, reference: \"a hunch\"}"), "permutations/costs/test.yaml").Single(), Does.Contain("error PLY0040: /spec/evidence/kind:"));

  [TestCase("  kind: grammar\n  primitives: [shift]\n  maxDepth: 4\n  inputWidth: 8\n", "grammars", "/spec/inputWidth")]
  [TestCase("  kind: grammar\n  primitives: [shift]\n  maxDepth: 4\n  cpu: z80\n", "grammars", "/spec/cpu")]
  [TestCase("  kind: cost\n  cpu: z80\n  costs: [{primitive: shift, cycles: 4}]\n  evidence: " + Documented + "\n  maxDepth: 4\n", "costs", "/spec/maxDepth")]
  [TestCase("  kind: mapping\n  inputWidth: 1\n  outputs: [0]\n  bijective: true\n  vectors: [{input: \"0x0\", output: \"0x0\"}, {input: \"0x1\", output: \"0x1\"}]\n  primitives: [shift]\n", "mappings", "/spec/primitives")]
  [TestCase("  kind: shuffle\n", "shuffles", "/spec/kind")]
  public void PropertyOfAnotherKind_IsASchemaViolation(string body, string folder, string pointer)
    => Assert.That(Lines(body, $"permutations/{folder}/test.yaml").Single(), Does.Contain($"error PLY0040: {pointer}:"));

  // Row 19 and the ID half of the layout rule.
  [Test]
  public void DocumentOutsideTheKindFolders_IsRefused()
    => Assert.That(Lines(Mapping(1, "[0]", true, "input: \"0x0\", output: \"0x0\"", "input: \"0x1\", output: \"0x1\""), "permutations/reverse8.yaml", "PERMUTATIONS.mapping.reverse8"), Is.EqualTo(new[] {
      "permutations/reverse8.yaml(13,9): error PLY0046: kind 'mapping' must live in spec/permutations/mappings, found spec/permutations (rule: layout)",
    }));

  [Test]
  public void DocumentInANestedFolder_IsRefused()
    => Assert.That(Lines(Grammar("  maxDepth: 4\n"), "permutations/grammars/x86/test.yaml", "PERMUTATIONS.grammar.test").Single(),
      Does.EndWith("kind 'grammar' must live in spec/permutations/grammars, found spec/permutations/grammars/x86 (rule: layout)"));

  [TestCase("PERMUTATIONS.grammar.other")]
  [TestCase("PERMUTATIONS.mapping.test")]
  [TestCase("PERMUTATIONS.test")]
  public void IdThatDoesNotFollowThePath_IsRefused(string id)
    => Assert.That(Lines(Grammar("  maxDepth: 4\n"), "permutations/grammars/test.yaml", id), Is.EqualTo(new[] {
      $"permutations/grammars/test.yaml(1,5): error PLY0046: id '{id}' must be 'PERMUTATIONS.grammar.test' in spec/permutations/grammars/test.yaml (rule: layout)",
    }));

  [Test]
  public void IntegerWrittenAsFloat_IsEvaluated()
    => Assert.That(Lines(Mapping(1, "[0.0]", true, "input: \"0x0\", output: \"0x0\"", "input: \"0x1\", output: \"0x1\"").Replace("inputWidth: 1", "inputWidth: 1.0")), Is.Empty);

}
