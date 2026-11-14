using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Spec.Families;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// <see cref="IrSpecFamily"/> and <c>spec/schemas/ir.schema.json</c>, run through the whole engine: the
/// seed documents of <c>spec/ir/</c>, every fixture of <c>tests/fixtures/spec/ir/</c> and the
/// equivalence classes and boundaries of the schema, written as variants of the seed documents.
/// </summary>
[TestFixture]
public sealed class IrSpecFamilyTests {

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "spec", "ir");

  private static readonly string _valid = Path.Combine(_fixtures, "valid");

  private static readonly string[] _rules = ["layout", "stage-range", "example-arity", "flag-declared", "trap-declared", "type-declared", "unique-mnemonic", "unique-stage-order"];

  /// <summary>A fixture file: where it goes in the tree (<c># at:</c>), what it must produce (<c># expect:</c>) and its ID.</summary>
  public sealed record Fixture(string Name, string At, string Expect, string Id, string Text) {

    /// <summary>Reads a fixture file.</summary>
    public static Fixture Read(string file) {
      var text = File.ReadAllText(file);
      string Header(string key) => text.Split('\n').Select(line => line.TrimEnd('\r')).FirstOrDefault(line => line.StartsWith($"# {key}: ", StringComparison.Ordinal))?[(key.Length + 4)..] ?? "";
      var id = text.Split('\n').Select(line => line.TrimEnd('\r')).First(line => line.StartsWith("id: ", StringComparison.Ordinal))[4..];
      return new(Path.GetFileName(file), Header("at"), Header("expect"), id, text);
    }

    /// <inheritdoc />
    public override string ToString() => this.Name;

  }

  private static IEnumerable<Fixture> InvalidFixtures()
    => Directory.EnumerateFiles(Path.Combine(_fixtures, "invalid"), "*.yaml").Order(StringComparer.Ordinal).Select(Fixture.Read);

  private static string ValidText(string path) => File.ReadAllText(Path.Combine(_valid, path));

  private static string Shl => ValidText("ir/operations/shl.yaml");

  private static string SourceSemantic => ValidText("ir/stages/source-semantic.yaml");

  private static string LegalizedTarget => ValidText("ir/stages/legalized-target.yaml");

  /// <summary>A text with each old line replaced by a new one; every old text must occur exactly once.</summary>
  private static string Change(string text, params (string Old, string New)[] changes) {
    foreach (var (old, replacement) in changes) {
      Assert.That(text.Split(old).Length, Is.EqualTo(2), $"'{old}' must occur once");
      text = text.Replace(old, replacement, StringComparison.Ordinal);
    }
    return text;
  }

  /// <summary>A repository with the ir schema, the documents of the valid tree except <paramref name="leaveOut"/>, then <paramref name="documents"/>.</summary>
  private static SpecRepository Repository(IEnumerable<string>? leaveOut, params (string Path, string Text)[] documents) {
    var repository = new SpecRepository();
    repository.Write("spec/schemas/ir.schema.json", File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "ir.schema.json")));
    var skipped = (leaveOut ?? []).ToHashSet(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(_valid, "*.yaml", SearchOption.AllDirectories)) {
      var path = Path.GetRelativePath(_valid, file).Replace(Path.DirectorySeparatorChar, '/');
      if (!skipped.Contains(path))
        repository.Document(path, File.ReadAllText(file));
    }
    foreach (var (path, text) in documents)
      repository.Document(path, text);
    return repository;
  }

  /// <summary>The diagnostics of the ir family over the valid tree with documents written over it.</summary>
  private static string[] Lines(params (string Path, string Text)[] documents) {
    using var repository = Repository(null, documents);
    return repository.Lines("ir");
  }

  /// <summary>The diagnostics of the ir family over the valid tree with <c>shl.yaml</c> changed.</summary>
  private static string[] ShlLines(params (string Old, string New)[] changes) => Lines(("ir/operations/shl.yaml", Change(Shl, changes)));

  private static string Problems(string[] lines) => string.Join("\n", lines);

  [Test]
  public void Family_IsRegistered() {
    var family = SpecFamilies.All.OfType<IrSpecFamily>().Single();
    Assert.That((family.Folder, family.SchemaPath), Is.EqualTo(("ir", "spec/schemas/ir.schema.json")));
  }

  [Test]
  public void Check_RefusesNullArguments() {
    var family = new IrSpecFamily();
    using var repository = Repository(null);
    var tree = repository.Tree();
    Assert.That(() => family.Check(null!, tree), Throws.ArgumentNullException);
    Assert.That(() => family.Check(tree.Documents[0], null!), Throws.ArgumentNullException);
  }

  [Test]
  public void RepositorySpecTree_IrFamilyValidates() {
    var tree = SpecTree.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec"), PrototypeFixture.RepositoryRoot);
    var validation = new SpecValidator(SpecFamilies.All).Run(tree, "ir");
    Assert.That(validation.Diagnostics, Is.Empty);
    Assert.That(validation.Documents, Is.GreaterThanOrEqualTo(3));
  }

  /// <summary>The valid tree is the seed documents of <c>spec/ir/</c>, copied.</summary>
  [TestCase("ir/operations/shl.yaml")]
  [TestCase("ir/stages/source-semantic.yaml")]
  [TestCase("ir/stages/legalized-target.yaml")]
  public void ValidTree_IsACopyOfTheSeedDocuments(string path)
    => Assert.That(ValidText(path), Is.EqualTo(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", path))));

  /// <summary>Test design 1 and 2: the operation with flags, a trap and three examples, and the two stages.</summary>
  [Test]
  public void ValidTree_HasThreeDocumentsAndNoError() {
    using var repository = Repository(null);
    var validation = repository.Validate("ir");
    Assert.That((validation.Documents, validation.Diagnostics.Count), Is.EqualTo((3, 0)));
  }

  /// <summary>The seed stages carry the ids and orders of the stage list of ADR 0001.</summary>
  [TestCase("ir/stages/source-semantic.yaml", "IR.stage.source-semantic", 10)]
  [TestCase("ir/stages/legalized-target.yaml", "IR.stage.legalized-target", 90)]
  public void SeedStage_FollowsTheAdr(string path, string id, int order) {
    var document = SpecDocument.Parse(path, ValidText(path));
    Assert.That((document.Id, (long)document.Body!["order"]!), Is.EqualTo((id, (long)order)));
    Assert.That(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "docs", "adr", "0001-ir-stage-invariants.md")), Does.Contain($"| {order} | ").And.Contain($"`{id}`"));
  }

  /// <summary>
  /// The masked example of <c>shl.yaml</c> agrees with the prototype's <c>ShiftLeft</c>, which takes the
  /// amount modulo the width: <c>0x80000001 &lt;&lt; (33 mod 32)</c> truncated to 32 bits.
  /// </summary>
  [Test]
  public void ShlMaskExample_AgreesWithTwosComplementArithmetic() {
    var example = SpecDocument.Parse("ir/operations/shl.yaml", Shl).Body!["examples"]![0]!;
    var value = Convert.ToUInt32((string)example["operands"]![0]!, 16);
    var amount = (int)(long)example["operands"]![1]!;
    Assert.That((string)example["flag"]!, Is.EqualTo("mask"));
    Assert.That($"0x{value << (amount & 31):X8}", Is.EqualTo((string)example["result"]!));
  }

  /// <summary>Test design 12–20: each fixture of <c>invalid/</c>, placed into the valid tree in place of the document with its path or ID, gives its expected diagnostic.</summary>
  [TestCaseSource(nameof(InvalidFixtures))]
  public void InvalidFixture_GivesItsDiagnostic(Fixture fixture) {
    Assert.That((fixture.At, fixture.Expect), Is.Not.EqualTo(("", "")), "a fixture states '# at:' and '# expect:'");
    var leaveOut = Directory.EnumerateFiles(_valid, "*.yaml", SearchOption.AllDirectories)
      .Select(file => Path.GetRelativePath(_valid, file).Replace(Path.DirectorySeparatorChar, '/'))
      .Where(path => path == fixture.At || Fixture.Read(Path.Combine(_valid, path)).Id == fixture.Id);
    using var repository = Repository(leaveOut, (fixture.At, fixture.Text));
    var lines = repository.Lines("ir");
    Assert.That(lines.Any(line => line.Contains("error " + fixture.Expect, StringComparison.Ordinal)), Is.True, Problems(lines));
    var expected = fixture.Name == "unique-mnemonic.yaml" ? 2 : 1;
    Assert.That(lines, Has.Length.EqualTo(expected), "one defect, one diagnostic; a duplicated mnemonic also breaks the layout: " + Problems(lines));
  }

  /// <summary>Measurement: every rule has a fixture.</summary>
  [Test]
  public void EveryRule_HasAFixture() {
    var covered = InvalidFixtures().Select(fixture => fixture.Expect).ToList();
    Assert.That(_rules.Where(rule => !covered.Any(expect => expect.EndsWith($"(rule: {rule})", StringComparison.Ordinal))), Is.Empty);
  }

  /// <summary>Measurement: every required property of the schema has a <c>missing-*</c> fixture.</summary>
  [Test]
  public void EveryRequiredProperty_HasAMissingFixture() {
    var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "ir.schema.json")))!;
    var definitions = schema["$defs"]!.AsObject();
    var expected = new SortedSet<string>(StringComparer.Ordinal) { "missing-kind.yaml" };
    void Walk(string kind, JsonNode node, string path) {
      while (node["$ref"] is { } reference)
        node = definitions[((string)reference!).Split('/')[^1]]!;
      foreach (var name in node["required"]?.AsArray().Select(item => (string)item!) ?? [])
        if (path.Length > 0 || name != "kind")
          expected.Add($"missing-{kind}-{path}{name}.yaml");
      foreach (var (name, child) in node["properties"]?.AsObject() ?? new JsonObject())
        if (child is not null)
          Walk(kind, child["items"] ?? child, $"{path}{name}-");
    }

    foreach (var kind in new[] { "operation", "stage", "version" })
      Walk(kind, definitions[kind]!, "");
    var present = Directory.EnumerateFiles(Path.Combine(_fixtures, "invalid"), "missing-*.yaml").Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
    Assert.That(expected.Where(name => !present.Contains(name)), Is.Empty);
    Assert.That(expected, Has.Count.EqualTo(27));
  }

  /// <summary>Test design 3: each kind validates.</summary>
  [Test]
  public void EachKind_Validates() {
    var version = File.ReadAllText(Path.Combine(_fixtures, "variants", "version.yaml"));
    Assert.That(Lines(("ir/version.yaml", version)), Is.Empty);
  }

  [Test]
  public void UnknownKind_IsASchemaViolation()
    => Assert.That(ShlLines(("kind: operation", "kind: magic")).Single(), Does.Contain("error PLY0040: /spec/kind:"));

  /// <summary>Test design 4: each category validates; another is a schema violation.</summary>
  [TestCase("arithmetic")]
  [TestCase("bitwise")]
  [TestCase("shift")]
  [TestCase("comparison")]
  [TestCase("conversion")]
  [TestCase("memory")]
  [TestCase("control")]
  [TestCase("call")]
  [TestCase("exception")]
  [TestCase("effect")]
  [TestCase("sequence")]
  [TestCase("vector")]
  public void EachCategory_Validates(string category) => Assert.That(ShlLines(("category: shift", $"category: {category}")), Is.Empty);

  [Test]
  public void CategoryMagic_IsASchemaViolation()
    => Assert.That(ShlLines(("category: shift", "category: magic")).Single(), Does.Contain("error PLY0040: /spec/category:"));

  /// <summary>Test design 4: each trap kind validates, declared and ended in by an example.</summary>
  [TestCase("overflow")]
  [TestCase("divide-by-zero")]
  [TestCase("bounds")]
  [TestCase("null")]
  [TestCase("invalid-shift")]
  [TestCase("unreachable")]
  public void EachTrapKind_Validates(string kind)
    => Assert.That(ShlLines(("kind: invalid-shift}", $"kind: \"{kind}\"}}"), ("trap: invalid-shift}", $"trap: \"{kind}\"}}")), Is.Empty);

  [Test]
  public void TrapKindMagic_IsASchemaViolation()
    => Assert.That(ShlLines(("kind: invalid-shift}", "kind: magic}")).Single(), Does.Contain("error PLY0040: /spec/traps/0/kind:"));

  /// <summary>The YAML 1.2 core schema reads a plain <c>null</c> as no value: the trap kind <c>null</c> must be quoted.</summary>
  [Test]
  public void TrapKindNullUnquoted_IsASchemaViolation()
    => Assert.That(ShlLines(("kind: invalid-shift}", "kind: null}"), ("trap: invalid-shift}", "trap: \"null\"}")).Single(), Does.Contain("error PLY0040: /spec/traps/0/kind:"));

  /// <summary>Test design 5: <c>pure</c>, one effect ID, both mixed.</summary>
  [Test]
  public void EffectId_ValidatesWhenTheDocumentExists() {
    var effect = SpecText.Document(("id", "EFFECTS.unknown-call"));
    Assert.That(Lines(("ir/operations/shl.yaml", Change(Shl, ("effects: [pure]", "effects: [EFFECTS.unknown-call]"))), ("effects/unknown-call.yaml", effect)), Is.Empty);
  }

  [Test]
  public void PureMixedWithAnEffect_IsASchemaViolation()
    => Assert.That(ShlLines(("effects: [pure]", "effects: [pure, EFFECTS.unknown-call]")).Single(), Does.Contain("error PLY0040: /spec/effects:"));

  [Test]
  public void EffectWithoutPrefix_IsASchemaViolation()
    => Assert.That(ShlLines(("effects: [pure]", "effects: [io]")).Single(), Does.Contain("error PLY0040: /spec/effects/0:"));

  [Test]
  public void NoEffect_IsASchemaViolation()
    => Assert.That(ShlLines(("effects: [pure]", "effects: []")).Single(), Does.Contain("error PLY0040: /spec/effects:"));

  /// <summary>Test design 6: a literal is a hexadecimal string or a decimal integer, negative ones included.</summary>
  [TestCase("\"0xFF\"")]
  [TestCase("255")]
  [TestCase("-1")]
  public void Literal_Validates(string literal)
    => Assert.That(ShlLines(("operands: [\"0x80000001\", 33]", $"operands: [{literal}, 33]")), Is.Empty);

  [TestCase("\"FF\"")]
  [TestCase("\"0x\"")]
  [TestCase("1.5")]
  [TestCase("true")]
  public void Literal_OtherwiseIsASchemaViolation(string literal)
    => Assert.That(ShlLines(("operands: [\"0x80000001\", 33]", $"operands: [{literal}, 33]")).Single(), Does.Contain("error PLY0040: /spec/examples/0/operands/0:"));

  /// <summary>Test design 7: 0 and 8 operands validate, 9 do not.</summary>
  [TestCase(0)]
  [TestCase(8)]
  public void OperandCount_Validates(int count) => Assert.That(ShlLines(WithOperands(count)), Is.Empty);

  [Test]
  public void NineOperands_IsASchemaViolation() => Assert.That(ShlLines(WithOperands(9)).Single(), Does.Contain("error PLY0040: /spec/operands:"));

  private static (string Old, string New)[] WithOperands(int count) {
    var operands = string.Join(", ", Enumerable.Range(0, count).Select(index => $"{{name: o{index}, type: T}}"));
    var literals = string.Join(", ", Enumerable.Repeat("1", count));
    return [
      ("operands: [{name: value, type: T}, {name: amount, type: T}]", $"operands: [{operands}]"),
      ("operands: [\"0x80000001\", 33]", $"operands: [{literals}]"),
      ("operands: [\"0x00000001\", 31]", $"operands: [{literals}]"),
      ("operands: [\"0x00000001\", 32]", $"operands: [{literals}]"),
    ];
  }

  /// <summary>Test design 8: one example is enough (no examples is the fixture <c>no-examples.yaml</c>).</summary>
  [Test]
  public void OneExample_Validates()
    => Assert.That(ShlLines(("    - {flag: strict, operands: [\"0x00000001\", 31], result: \"0x80000000\"}\n", ""), ("    - {flag: strict, operands: [\"0x00000001\", 32], trap: invalid-shift}\n", "")), Is.Empty);

  [Test]
  public void ExampleWithBothResultAndTrap_IsASchemaViolation()
    => Assert.That(ShlLines(("result: \"0x80000000\"}", "result: \"0x80000000\", trap: invalid-shift}")).Single(), Does.Contain("error PLY0040: /spec/examples/1/trap:"));

  /// <summary>Test design 9: stage order 0 / 1 / 999 / 1000, on a tree of the two stages alone.</summary>
  [TestCase(1, false)]
  [TestCase(999, false)]
  [TestCase(0, true)]
  [TestCase(1000, true)]
  public void StageOrder_Boundaries(int order, bool refused) {
    using var repository = Repository(["ir/operations/shl.yaml"], ("ir/stages/legalized-target.yaml", Change(LegalizedTarget, ("order: 90", $"order: {order}"))));
    var lines = repository.Lines("ir");
    if (refused)
      Assert.That(lines.Single(), Does.Contain("error PLY0040: /spec/order:"));
    else
      Assert.That(lines, Is.Empty);
  }

  /// <summary>Test design 10: a single-stage operation.</summary>
  [Test]
  public void StageRangeOfOneStage_Validates()
    => Assert.That(ShlLines(("to: IR.stage.legalized-target", "to: IR.stage.source-semantic")), Is.Empty);

  /// <summary>Test design 11: an operation without flags whose examples name none, and one that names one.</summary>
  [Test]
  public void OperationWithoutFlags_Validates() {
    var unreachable = Fixture.Read(Path.Combine(_fixtures, "variants", "unreachable.yaml"));
    Assert.That(Lines((unreachable.At, unreachable.Text)), Is.Empty);
  }

  [Test]
  public void OperationWithoutFlags_RefusesAnExampleThatNamesOne() {
    var unreachable = Fixture.Read(Path.Combine(_fixtures, "variants", "unreachable.yaml"));
    var text = Change(unreachable.Text, ("{operands: [], trap", "{flag: mask, operands: [], trap"));
    Assert.That(Lines((unreachable.At, text)), Is.EqualTo(new[] { "ir/operations/unreachable.yaml(25,14): error PLY0046: example 1 uses flag 'mask', the operation declares no flags (rule: flag-declared)" }));
  }

  [Test]
  public void OperationWithoutTraps_RefusesAnExampleThatTraps() {
    var lines = ShlLines(("traps: [{when: \"flag strict and amount >= width\", kind: invalid-shift}]", "traps: []"));
    Assert.That(lines.Single(), Does.EndWith("error PLY0046: example 3 traps with 'invalid-shift', the operation declares no traps (rule: trap-declared)"));
  }

  [Test]
  public void ResultTypeThatIsNoTypeParameter_IsRefused()
    => Assert.That(ShlLines(("result: {type: T}", "result: {type: U}")).Single(), Does.EndWith("error PLY0046: the result has type 'U', which is no type parameter of the operation (rule: type-declared)"));

  [Test]
  public void TypesId_ValidatesWhenTheDocumentExists() {
    var type = SpecText.Document(("id", "TYPES.u32"));
    Assert.That(Lines(("ir/operations/shl.yaml", Change(Shl, ("result: {type: T}", "result: {type: TYPES.u32}"))), ("types/u32.yaml", type)), Is.Empty);
  }

  [Test]
  public void StageReferenceToAnotherKind_IsRefused() {
    var misplaced = Change(Shl, ("id: IR.shl", "id: IR.stage.misplaced"), ("mnemonic: Shl", "mnemonic: Misplaced"));
    var lines = Lines(("ir/stages/misplaced.yaml", misplaced), ("ir/operations/shl.yaml", Change(Shl, ("from: IR.stage.source-semantic", "from: IR.stage.misplaced"))));
    Assert.That(lines, Has.Some.EndWith("error PLY0046: stages.from 'IR.stage.misplaced' is a document of kind 'operation', not a stage (rule: stage-range)"));
    Assert.That(lines, Has.Some.Contains("ir/stages/misplaced.yaml").And.Contains("expected 'stage' (rule: layout)"));
  }

  [Test]
  public void StageReferenceToABrokenStage_ReportsOnlyTheStage() {
    var broken = Change(SourceSemantic, ("  order: 10\n", ""));
    Assert.That(Lines(("ir/stages/source-semantic.yaml", broken)).Single(), Does.StartWith("ir/stages/source-semantic.yaml(").And.Contain("error PLY0040: /spec: Required properties [\"order\"]"));
  }

  [Test]
  public void StageReferenceToADocumentWithoutBody_ReportsOnlyThatDocument() {
    var empty = Change(SourceSemantic, ("id: IR.stage.source-semantic", "id: IR.stage.empty"));
    empty = empty[..empty.IndexOf("spec:", StringComparison.Ordinal)];
    var lines = Lines(("ir/stages/empty.yaml", empty), ("ir/operations/shl.yaml", Change(Shl, ("to: IR.stage.legalized-target", "to: IR.stage.empty"))));
    Assert.That(lines.Single(), Does.StartWith("ir/stages/empty.yaml(").And.Contain("error PLY0040: /: Required properties"));
  }

  [Test]
  public void BothStageReferencesBroken_AreBothReported() {
    var lines = ShlLines(("{from: IR.stage.source-semantic, to: IR.stage.legalized-target}", "{from: IR.stage.here, to: IR.stage.there}"));
    Assert.That(lines, Has.Length.EqualTo(2));
    Assert.That(lines[0], Does.EndWith("error PLY0043: unknown spec id 'IR.stage.here'"));
    Assert.That(lines[1], Does.EndWith("error PLY0043: unknown spec id 'IR.stage.there'"));
  }

  /// <summary>The uniqueness rules look only at earlier documents of the family, of the same kind.</summary>
  [Test]
  public void UniqueMnemonic_IgnoresOtherFamiliesAndBrokenDocuments() {
    var withoutBody = Change(Shl, ("id: IR.shl", "id: IR.aaa"));
    withoutBody = withoutBody[..withoutBody.IndexOf("spec:", StringComparison.Ordinal)];
    var withoutKind = Change(Shl, ("id: IR.shl", "id: IR.abc"), ("  kind: operation\n", ""));
    var withoutMnemonic = Change(Shl, ("id: IR.shl", "id: IR.abd"), ("  mnemonic: Shl\n", ""));
    var lines = Lines(
      ("demo/aaa.yaml", SpecText.Document(("id", "DEMO.aaa"))),
      ("ir/operations/aaa.yaml", withoutBody),
      ("ir/operations/abc.yaml", withoutKind),
      ("ir/operations/abd.yaml", withoutMnemonic));
    Assert.That(lines.Where(line => line.Contains("PLY0046", StringComparison.Ordinal)), Is.Empty, Problems(lines));
    Assert.That(lines, Has.Length.EqualTo(3), Problems(lines));
  }

}
