using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Spec.Families;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// <see cref="RecognitionSpecFamily"/>, the family of <c>spec/recognition/</c>: its schema, its seven
/// rules, the fixtures of <c>tests/fixtures/spec/recognition/</c> and the seed documents of the
/// prototype's rules, run through the whole engine. The cases are variants of the valid
/// <c>O0100</c> document of the fixtures.
/// </summary>
[TestFixture]
public sealed class RecognitionSpecFamilyTests {

  private const string Fixtures = "tests/fixtures/spec/recognition";

  private const string RotatePath = "recognition/O0100.yaml";

  private const string RotateLeftPath = "intrinsics/shuffle/rotateleft.yaml";

  private const string Page = "docs/optimizations/O0100-rotate-recognition.md";

  private static readonly string _repository = PrototypeFixture.RepositoryRoot;

  private static readonly string[] _rules = ["layout", "id-in-block", "switch-matches", "counterexample-differs", "smt-needs-budget", "exhaustive-width", "document-exists"];

  private static string Valid => File.ReadAllText(Path.Combine(_repository, Fixtures, "valid", "recognition", "O0100.yaml"));

  private static string RotateLeft => File.ReadAllText(Path.Combine(_repository, Fixtures, "valid", "intrinsics", "shuffle", "rotateleft.yaml"));

  /// <summary>A text with each old text replaced by a new one; every old text must occur exactly once.</summary>
  private static string Change(string text, params (string Old, string New)[] changes) {
    foreach (var (old, replacement) in changes) {
      Assert.That(text.Split(old).Length, Is.EqualTo(2), $"'{old}' must occur once");
      text = text.Replace(old, replacement, StringComparison.Ordinal);
    }
    return text;
  }

  /// <summary>The valid document renumbered: its ID, optimization, switch and page.</summary>
  private static string Renumbered(string number) => Change(Valid,
    ("id: RECOGNITION.O0100", "id: RECOGNITION." + number),
    ("optimization: O0100", "optimization: " + number),
    ("\"--disable-opt O0100\"", $"\"--disable-opt {number}\""),
    ("O0100-rotate-recognition.md", number + "-rotate-recognition.md"));

  /// <summary>A repository with the recognition schema, the intrinsic the valid document introduces, and the documents.</summary>
  private static SpecRepository Repository(params (string Path, string Text)[] documents) {
    var repository = new SpecRepository();
    repository.Write("spec/schemas/recognition.schema.json", File.ReadAllText(Path.Combine(_repository, "spec", "schemas", "recognition.schema.json")));
    repository.Document(RotateLeftPath, RotateLeft);
    foreach (var (path, text) in documents)
      repository.Document(path, text);
    return repository;
  }

  private static string[] Lines(string path, string text) {
    using var repository = Repository((path, text));
    return repository.Lines("recognition");
  }

  private static string[] Lines(params (string Old, string New)[] changes) => Lines(RotatePath, Change(Valid, changes));

  private static string Rule(string reason, string rule) => $"error PLY0046: {reason} (rule: {rule})";

  private static JsonNode SchemaNode(string pointer) {
    var node = JsonNode.Parse(File.ReadAllText(Path.Combine(_repository, "spec", "schemas", "recognition.schema.json")))!;
    foreach (var part in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
      node = node[part]!;
    return node;
  }

  private static string[] SchemaEnum(string pointer) => [.. SchemaNode(pointer).AsArray().Select(node => (string)node!)];

  [Test]
  public void Family_IsRegistered() {
    var family = SpecFamilies.All.OfType<RecognitionSpecFamily>().Single();
    Assert.That((family.Folder, family.SchemaPath), Is.EqualTo(("recognition", "spec/schemas/recognition.schema.json")));
  }

  [Test]
  public void Check_RefusesNullArguments() {
    var family = new RecognitionSpecFamily();
    using var repository = Repository((RotatePath, Valid));
    var tree = repository.Tree();
    Assert.That(() => family.Check(null!, tree).ToList(), Throws.ArgumentNullException);
    Assert.That(() => family.Check(tree.Documents[0], null!).ToList(), Throws.ArgumentNullException);
  }

  [Test]
  public void Schema_IsValidAgainstTheMetaSchema() {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repository, "spec", "schemas", "recognition.schema.json")));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  // The seed: one document per rule of the prototype

  [Test]
  public void RepositorySpecTree_RecognitionFamilyValidates() {
    var tree = SpecTree.Load(Path.Combine(_repository, "spec"), _repository);
    var validation = new SpecValidator(SpecFamilies.All).Run(tree, "recognition");
    Assert.That(validation.Diagnostics, Is.Empty);
    Assert.That(validation.Documents, Is.EqualTo(RuleTable.Default.Rules.Count));
  }

  /// <summary>Every rule of the prototype pipeline has its document, and every document a rule, whose optimization page says it is implemented.</summary>
  [Test]
  public void Seed_IsEveryRuleOfThePrototype() {
    var tree = SpecTree.Load(Path.Combine(_repository, "spec"), _repository);
    var seed = tree.Documents.Where(document => document.Family == "recognition").ToList();
    Assert.That(seed.Select(document => (string)document.Body!["optimization"]!), Is.EquivalentTo(RuleTable.Default.Ids));
    foreach (var document in seed) {
      var page = File.ReadAllText(Path.Combine(_repository, (string)document.Body!["document"]!));
      Assert.That(page, Does.Contain("<!-- status: implemented (prototype) -->"), document.Path);
      Assert.That(document.Meta!.Status, Is.EqualTo("draft"), document.Path + ": the proof obligations are not discharged yet");
    }
  }

  /// <summary>Each seed document introduces the intrinsics its rule creates, as the rule's source names them.</summary>
  [Test]
  public void Seed_IntroducesWhatTheRuleCreates() {
    var tree = SpecTree.Load(Path.Combine(_repository, "spec"), _repository);
    foreach (var rule in RuleTable.Default.Rules) {
      var document = tree.ById["RECOGNITION." + rule.Id];
      var introduced = document.Body!["replacement"]!["introduces"]!.AsArray().Select(node => (string)node!).Where(id => id.StartsWith("INTRINSICS.", StringComparison.Ordinal)).ToList();
      var source = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(_repository, "Polypiler.Core.Recognize", "Rules"), rule.Id + "*.cs").Single());
      foreach (var id in introduced)
        Assert.That(source, Does.Contain("IrIntrinsicOp." + id["INTRINSICS.".Length..]), $"{rule.Id} introduces {id}");
    }
  }

  // 1 — happy path

  [Test]
  public void ValidFixtureTree_HasOneDocumentAndNoError() {
    using var repository = Repository((RotatePath, Valid));
    var validation = repository.Validate("recognition");
    Assert.That((validation.Documents, validation.Diagnostics.Count), Is.EqualTo((1, 0)));
  }

  [Test]
  public void ValidFixtureIntrinsic_IsACopyOfTheSeed()
    => Assert.That(RotateLeft, Is.EqualTo(File.ReadAllText(Path.Combine(_repository, "spec", RotateLeftPath))));

  // 2 — every level, obligation class and precondition fact, and an unknown value of each

  [Test]
  public void EveryEnumeration_IsDeclaredByTheSchema() {
    Assert.That(SchemaEnum("/properties/level/enum"), Is.EquivalentTo(new[] { "source-ast", "typed-ssa" }));
    Assert.That(SchemaEnum("/properties/proofObligations/items/properties/class/enum"), Is.EquivalentTo(new[] { "exhaustive-8bit", "exhaustive-16bit", "smt-bitvector", "range-facts", "alias-facts" }));
    Assert.That(SchemaEnum("/properties/preconditions/items/properties/fact/enum"), Is.EquivalentTo(new[] { "none", "range", "known-bits", "alias", "effect", "type" }));
    Assert.That(SchemaEnum("/properties/optLevels/items/enum"), Is.EquivalentTo(new[] { "balanced", "speed", "size", "memory" }));
  }

  [TestCase("source-ast")]
  [TestCase("typed-ssa")]
  public void EveryLevel_IsValid(string level) => Assert.That(Lines(("level: typed-ssa", "level: " + level)), Is.Empty);

  [Test]
  public void EveryObligationClass_IsValid() {
    var obligations = SchemaEnum("/properties/proofObligations/items/properties/class/enum")
      .Select(name => $"    - {{id: {name}, class: {name}, widths: [8], budgetSeconds: 5}}\n");
    Assert.That(Lines(("    - {id: rotl-8, class: exhaustive-8bit, widths: [8]}\n", string.Concat(obligations))), Is.Empty);
  }

  [Test]
  public void EveryPreconditionFact_IsValid() {
    var preconditions = SchemaEnum("/properties/preconditions/items/properties/fact/enum")
      .Select(fact => $"    - {{id: {fact}, text: \"a {fact} fact\", fact: {fact}}}\n");
    Assert.That(Lines(("    - {id: same-value, text: \"both shifts take the same SSA value\", fact: none}\n", string.Concat(preconditions))), Is.Empty);
  }

  [TestCase("level: typed-ssa", "level: machine-code", "/spec/level:")]
  [TestCase("class: exhaustive-8bit", "class: exhaustive-4bit", "/spec/proofObligations/0/class:")]
  [TestCase("fact: range}", "fact: hunch}", "/spec/preconditions/1/fact:")]
  [TestCase("introduces: [INTRINSICS.RotateLeft]", "introduces: [TYPES.u32]", "/spec/replacement/introduces/0:")]
  public void UnknownValue_IsASchemaViolation(string old, string replacement, string pointer)
    => Assert.That(Lines((old, replacement)).Single(), Does.Contain("error PLY0040: " + pointer));

  // 3 — optLevels

  [TestCase("[balanced]")]
  [TestCase("[balanced, speed, size, memory]")]
  [TestCase("[size]")]
  public void OptLevels_ASubset_IsValid(string levels) => Assert.That(Lines(("optLevels: [balanced, speed, size, memory]", "optLevels: " + levels)), Is.Empty);

  [TestCase("[none]")]
  [TestCase("[]")]
  [TestCase("[speed, speed]")]
  public void OptLevels_NoneEmptyOrRepeated_IsASchemaViolation(string levels)
    => Assert.That(Lines(("optLevels: [balanced, speed, size, memory]", "optLevels: " + levels)).Single(), Does.Contain("error PLY0040: /spec/optLevels"));

  // 4 — draft and normative, with and without the optimization page

  private static string Normative => File.ReadAllText(Path.Combine(_repository, Fixtures, "invalid", "document-exists.yaml"));

  [Test]
  public void Draft_WithoutItsPage_IsValid() => Assert.That(Lines(), Is.Empty);

  [Test]
  public void Normative_WithItsPage_IsValid() {
    using var repository = Repository((RotatePath, Normative));
    repository.Write(Page, "# O0100\n");
    Assert.That(repository.Lines("recognition"), Is.Empty);
  }

  [Test]
  public void Normative_WithoutItsPage_IsRefusedAtTheDocument()
    => Assert.That(Lines(RotatePath, Normative), Is.EqualTo(new[] {
      "recognition/O0100.yaml(34,13): " + Rule($"document {Page} does not exist", "document-exists"),
    }));

  // 5 — the edges of the blocks

  [TestCase("O0100")]
  [TestCase("O0169")]
  [TestCase("O0200")]
  [TestCase("O0229")]
  public void NumberInsideABlock_IsValid(string number) => Assert.That(Lines($"recognition/{number}.yaml", Renumbered(number)), Is.Empty);

  [TestCase("O0000")]
  [TestCase("O0042")]
  [TestCase("O0099")]
  [TestCase("O0170")]
  [TestCase("O0199")]
  [TestCase("O0230")]
  [TestCase("O9999")]
  public void NumberOutsideTheBlocks_IsRefusedAtTheOptimization(string number)
    => Assert.That(Lines($"recognition/{number}.yaml", Renumbered(number)), Is.EqualTo(new[] {
      $"recognition/{number}.yaml(13,17): " + Rule($"{number} is outside the recognition blocks O0100-O0169 and O0200-O0229", "id-in-block"),
    }));

  // 6 — budgetSeconds

  [TestCase("0")]
  [TestCase("601")]
  [TestCase("\"5\"")]
  public void BudgetOutsideItsRange_IsASchemaViolation(string budget)
    => Assert.That(Lines(("budgetSeconds: 5", "budgetSeconds: " + budget)).Single(), Does.Contain("error PLY0040: /spec/proofObligations/1/budgetSeconds:"));

  [TestCase("1")]
  [TestCase("600")]
  public void BudgetAtItsEdges_IsValid(string budget) => Assert.That(Lines(("budgetSeconds: 5", "budgetSeconds: " + budget)), Is.Empty);

  // 7 — the lists at their lower edge

  private const string Counterexample = "    - description: \"arithmetic right shift is not a rotation\"\n      input: \"x = 0x80000000, i32, amounts 8 and 24\"\n      naive: \"0xFFFFFF80\"\n      correct: \"0x00000080\"\n";

  private const string Obligations = "    - {id: rotl-8, class: exhaustive-8bit, widths: [8]}\n    - {id: rotl-wide, class: smt-bitvector, widths: [16, 32, 64], budgetSeconds: 5}\n";

  private const string Preconditions = "    - {id: same-value, text: \"both shifts take the same SSA value\", fact: none}\n    - {id: complementary, text: \"the amounts sum to the width\", fact: range}\n";

  [TestCase("  counterexamples:\n" + Counterexample, "  counterexamples: []\n", "/spec/counterexamples:")]
  [TestCase("  proofObligations:\n" + Obligations, "  proofObligations: []\n", "/spec/proofObligations:")]
  [TestCase("  preconditions:\n" + Preconditions, "  preconditions: []\n", "/spec/preconditions:")]
  [TestCase("introduces: [INTRINSICS.RotateLeft]", "introduces: []", "/spec/replacement/introduces:")]
  public void EmptyList_IsASchemaViolation(string old, string replacement, string pointer)
    => Assert.That(Lines((old, replacement)).Single(), Does.Contain("error PLY0040: " + pointer));

  [Test]
  public void OneObligation_IsValid() => Assert.That(Lines((Obligations, "    - {id: rotl-8, class: exhaustive-8bit, widths: [8]}\n")), Is.Empty);

  [Test]
  public void OnePrecondition_IsValid() => Assert.That(Lines((Preconditions, "    - {id: same-value, text: \"both shifts take the same SSA value\", fact: none}\n")), Is.Empty);

  [Test]
  public void OneCounterexample_IsValid() => Assert.That(Lines(), Is.Empty);

  [Test]
  public void NoBinding_IsValid() => Assert.That(Lines(("bindings: [{name: x, role: rotated value}]", "bindings: []")), Is.Empty);

  [TestCase("    before: \"%a = Shl.u32[strict] %x, 8; %b = LShr.u32[strict] %x, 24; %r = Or.u32 %a, %b\"", "    before: \"\"", "/spec/pattern/before:")]
  [TestCase("    after: \"%r = RotateLeft.u32 %x, 8\"", "    after: \"\"", "/spec/replacement/after:")]
  [TestCase("{name: x, role: rotated value}", "{name: X, role: rotated value}", "/spec/pattern/bindings/0/name:")]
  [TestCase("naive: \"0xFFFFFF80\"", "naive: \"\"", "/spec/counterexamples/0/naive:")]
  [TestCase("widths: [8]}", "widths: []}", "/spec/proofObligations/0/widths:")]
  [TestCase("widths: [8]}", "widths: [0]}", "/spec/proofObligations/0/widths/0:")]
  [TestCase("widths: [16, 32, 64]", "widths: [16, 32, 65537]", "/spec/proofObligations/1/widths/2:")]
  [TestCase("{id: rotl-8,", "{id: Rotl-8,", "/spec/proofObligations/0/id:")]
  [TestCase("document: docs/optimizations/O0100-rotate-recognition.md", "document: docs/O0100-rotate-recognition.md", "/spec/document:")]
  [TestCase("\"--disable-opt O0100\"", "\"--disable O0100\"", "/spec/switch:")]
  public void MalformedValue_IsASchemaViolation(string old, string replacement, string pointer)
    => Assert.That(Lines((old, replacement)).Single(), Does.Contain("error PLY0040: " + pointer));

  // 8 — exhaustive classes and their widths

  [TestCase("[8]")]
  [TestCase("[16]")]
  [TestCase("[1, 8, 16]")]
  public void Exhaustive16_UpToSixteenBits_IsValid(string widths)
    => Assert.That(Lines(("class: exhaustive-8bit, widths: [8]}", $"class: exhaustive-16bit, widths: {widths}}}")), Is.Empty);

  [Test]
  public void Exhaustive16_OfSeventeenBits_IsRefusedAtTheWidth()
    => Assert.That(Lines(("class: exhaustive-8bit, widths: [8]}", "class: exhaustive-16bit, widths: [8, 17]}")), Is.EqualTo(new[] {
      "recognition/O0100.yaml(25,57): " + Rule("obligation 'rotl-8' of class exhaustive-16bit lists width 17", "exhaustive-width"),
    }));

  [TestCase("[4]", 4)]
  [TestCase("[16]", 16)]
  [TestCase("[32]", 32)]
  public void Exhaustive8_OfAnotherWidth_IsRefused(string widths, int width)
    => Assert.That(Lines(("class: exhaustive-8bit, widths: [8]}", $"class: exhaustive-8bit, widths: {widths}}}")).Single(),
      Does.EndWith(Rule($"obligation 'rotl-8' of class exhaustive-8bit lists width {width}", "exhaustive-width")));

  [Test]
  public void Exhaustive8_WithTwoWrongWidths_IsRefusedOncePerWidth()
    => Assert.That(Lines(("class: exhaustive-8bit, widths: [8]}", "class: exhaustive-8bit, widths: [16, 8, 32]}")), Is.EqualTo(new[] {
      "recognition/O0100.yaml(25,53): " + Rule("obligation 'rotl-8' of class exhaustive-8bit lists width 16", "exhaustive-width"),
      "recognition/O0100.yaml(25,60): " + Rule("obligation 'rotl-8' of class exhaustive-8bit lists width 32", "exhaustive-width"),
    }));

  [TestCase("range-facts")]
  [TestCase("alias-facts")]
  public void FactObligation_OfAnyWidth_IsValid(string @class)
    => Assert.That(Lines(("class: exhaustive-8bit, widths: [8]}", $"class: {@class}, widths: [8, 64, 128]}}")), Is.Empty);

  // smt-needs-budget

  [Test]
  public void SmtWithoutBudget_IsRefusedAtTheObligation()
    => Assert.That(Lines(("widths: [16, 32, 64], budgetSeconds: 5}", "widths: [16, 32, 64]}")), Is.EqualTo(new[] {
      "recognition/O0100.yaml(26,7): " + Rule("obligation 'rotl-wide' of class smt-bitvector has no budgetSeconds", "smt-needs-budget"),
    }));

  [Test]
  public void BudgetOnAnotherClass_IsValid()
    => Assert.That(Lines(("class: exhaustive-8bit, widths: [8]}", "class: exhaustive-8bit, widths: [8], budgetSeconds: 60}")), Is.Empty);

  // counterexample-differs

  [TestCase("\"0x00000080\"")]
  [TestCase("\" 0x00000080 \"")]
  [TestCase("\"0X00000080\"")]
  public void CounterexampleWithEqualResults_IsRefusedAtTheCorrectResult(string naive)
    => Assert.That(Lines(("naive: \"0xFFFFFF80\"", "naive: " + naive)), Is.EqualTo(new[] {
      "recognition/O0100.yaml(31,16): " + Rule("counterexamples[0] has equal naive and correct results (0x00000080)", "counterexample-differs"),
    }));

  [Test]
  public void SecondCounterexampleWithEqualResults_IsRefusedByItsIndex()
    => Assert.That(Lines((Counterexample, Counterexample + "    - {description: \"same\", input: \"x = 0\", naive: \"0\", correct: \"0\"}\n")).Single(),
      Does.EndWith(Rule("counterexamples[1] has equal naive and correct results (0)", "counterexample-differs")));

  // switch-matches

  [Test]
  public void SwitchOfAnotherOptimization_IsRefusedAtTheSwitch()
    => Assert.That(Lines(("\"--disable-opt O0100\"", "\"--disable-opt O0101\"")), Is.EqualTo(new[] {
      "recognition/O0100.yaml(32,11): " + Rule("switch '--disable-opt O0101' does not disable O0100", "switch-matches"),
    }));

  // references

  [Test]
  public void IntroducedDocumentThatDoesNotExist_IsABrokenReference()
    => Assert.That(Lines(("introduces: [INTRINSICS.RotateLeft]", "introduces: [INTRINSICS.RotateLeft, IR.rotl]")), Is.EqualTo(new[] {
      "recognition/O0100.yaml(20,41): error PLY0043: unknown spec id 'IR.rotl'",
    }));

  [Test]
  public void IntroducedIdListedTwice_IsASchemaViolation()
    => Assert.That(Lines(("introduces: [INTRINSICS.RotateLeft]", "introduces: [INTRINSICS.RotateLeft, INTRINSICS.RotateLeft]")).Single(), Does.Contain("error PLY0040: /spec/replacement/introduces:"));

  // layout

  [Test]
  public void IdNotMatchingTheFileName_IsRefusedAtTheId()
    => Assert.That(Lines(("id: RECOGNITION.O0100", "id: RECOGNITION.O0101")), Is.EqualTo(new[] {
      "recognition/O0100.yaml(1,5): " + Rule("id 'RECOGNITION.O0101' does not match file name 'O0100.yaml'", "layout"),
    }));

  [Test]
  public void OptimizationAndPageNotMatchingTheFileName_AreRefusedAndStopTheOtherRules()
    => Assert.That(Lines("recognition/O0101.yaml", Change(Valid, ("id: RECOGNITION.O0100", "id: RECOGNITION.O0101"))), Is.EqualTo(new[] {
      "recognition/O0101.yaml(13,17): " + Rule("optimization 'O0100' does not match file name 'O0101.yaml'", "layout"),
      "recognition/O0101.yaml(34,13): " + Rule($"document '{Page}' is not the page of O0101", "layout"),
    }));

  [TestCase("recognition/rotate/O0100.yaml")]
  [TestCase("recognition/rotate.yaml")]
  [TestCase("recognition/O100.yaml")]
  public void FileOutsideTheLayout_IsRefusedAtTheDocument(string path)
    => Assert.That(Lines(path, Valid), Is.EqualTo(new[] {
      $"{path}(1,1): " + Rule($"'{path}' is not recognition/O<4 digits>.yaml", "layout"),
    }));

  // 9 — the optimization's form

  [TestCase("O100")]
  [TestCase("O01000")]
  [TestCase("o0100")]
  public void MalformedOptimization_IsASchemaViolation(string optimization)
    => Assert.That(Lines(("optimization: O0100", "optimization: " + optimization)).Single(), Does.Contain("error PLY0040: /spec/optimization:"));

  // 10–18 — every fixture of tests/fixtures/spec/recognition/invalid

  private static IEnumerable<string> RequiredProperties() => SchemaNode("/required").AsArray().Select(node => (string)node!);

  private static IEnumerable<TestCaseData> InvalidFixtures() {
    yield return new TestCaseData("layout.yaml", Rule("id 'RECOGNITION.O0101' does not match file name 'O0100.yaml'", "layout"));
    yield return new TestCaseData("id-in-block.yaml", Rule("O0042 is outside the recognition blocks O0100-O0169 and O0200-O0229", "id-in-block"));
    yield return new TestCaseData("switch-matches.yaml", Rule("switch '--disable-opt O0101' does not disable O0100", "switch-matches"));
    yield return new TestCaseData("counterexample-differs.yaml", Rule("counterexamples[0] has equal naive and correct results (0x00000080)", "counterexample-differs"));
    yield return new TestCaseData("smt-needs-budget.yaml", Rule("obligation 'rotl-wide' of class smt-bitvector has no budgetSeconds", "smt-needs-budget"));
    yield return new TestCaseData("exhaustive-width.yaml", Rule("obligation 'rotl-8' of class exhaustive-8bit lists width 32", "exhaustive-width"));
    yield return new TestCaseData("document-exists.yaml", Rule($"document {Page} does not exist", "document-exists"));
    yield return new TestCaseData("no-proof.yaml", "error PLY0040: /spec/proofObligations:");
    yield return new TestCaseData("extra-property.yaml", "error PLY0040: /spec/author: property 'author' is not allowed");
    foreach (var key in RequiredProperties())
      yield return new TestCaseData($"missing-{key}.yaml", $"error PLY0040: /spec: Required properties [\"{key}\"] are not present");
    yield return new TestCaseData("missing-pattern-before.yaml", "error PLY0040: /spec/pattern: Required properties [\"before\"] are not present");
    yield return new TestCaseData("missing-pattern-bindings.yaml", "error PLY0040: /spec/pattern: Required properties [\"bindings\"] are not present");
    yield return new TestCaseData("missing-pattern-bindings-name.yaml", "error PLY0040: /spec/pattern/bindings/0: Required properties [\"name\"] are not present");
    yield return new TestCaseData("missing-pattern-bindings-role.yaml", "error PLY0040: /spec/pattern/bindings/0: Required properties [\"role\"] are not present");
    yield return new TestCaseData("missing-replacement-after.yaml", "error PLY0040: /spec/replacement: Required properties [\"after\"] are not present");
    yield return new TestCaseData("missing-replacement-introduces.yaml", "error PLY0040: /spec/replacement: Required properties [\"introduces\"] are not present");
    foreach (var key in new[] { "id", "text", "fact" })
      yield return new TestCaseData($"missing-preconditions-{key}.yaml", $"error PLY0040: /spec/preconditions/0: Required properties [\"{key}\"] are not present");
    foreach (var key in new[] { "id", "class", "widths" })
      yield return new TestCaseData($"missing-proofObligations-{key}.yaml", $"error PLY0040: /spec/proofObligations/0: Required properties [\"{key}\"] are not present");
    foreach (var key in new[] { "description", "input", "naive", "correct" })
      yield return new TestCaseData($"missing-counterexamples-{key}.yaml", $"error PLY0040: /spec/counterexamples/0: Required properties [\"{key}\"] are not present");
  }

  /// <summary>Writes a fixture where the layout puts it: named after its optimization, or after its ID where it has none.</summary>
  private static string[] FixtureLines(string fixture) {
    var text = File.ReadAllText(Path.Combine(_repository, Fixtures, "invalid", fixture));
    var document = SpecDocument.Parse("recognition/fixture.yaml", text);
    var name = (string?)document.Body?["optimization"] ?? document.Id![(document.Id!.IndexOf('.') + 1)..];
    return Lines($"recognition/{name}.yaml", text);
  }

  [TestCaseSource(nameof(InvalidFixtures))]
  public void InvalidFixture_IsRefusedWithOneDiagnostic(string fixture, string expected)
    => Assert.That(FixtureLines(fixture).Single(), Does.Contain(expected));

  /// <summary>Measurements: a negative fixture for every rule and every required property, and every fixture covered by a case above.</summary>
  [Test]
  public void EveryRuleAndEveryRequiredProperty_HasANegativeFixture() {
    var fixtures = Directory.EnumerateFiles(Path.Combine(_repository, Fixtures, "invalid")).Select(Path.GetFileName).ToHashSet();
    Assert.That(fixtures, Is.SupersetOf(_rules.Select(rule => rule + ".yaml").Concat(RequiredProperties().Select(key => $"missing-{key}.yaml"))));
    Assert.That(fixtures, Is.EquivalentTo(InvalidFixtures().Select(data => (string)data.Arguments[0]!)));
  }

  /// <summary>
  /// The oracle of the valid fixture, Hacker's Delight 2-15: for x = 0x80000000 the shift pair with an
  /// arithmetic right shift gives 0xFFFFFF80, the rotation 0x00000080.
  /// </summary>
  [Test]
  public void ValidCounterexample_AgreesWithTheShiftsWorkedOut() {
    var x = 0x80000000u;
    var naive = (x << 8) | unchecked((uint)((int)x >> 24));
    var correct = (x << 8) | (x >> 24);
    var counterexample = SpecDocument.Parse(RotatePath, Valid).Body!["counterexamples"]![0]!;
    Assert.That(((string?)counterexample["naive"], (string?)counterexample["correct"]), Is.EqualTo(($"0x{naive:X8}", $"0x{correct:X8}")));
  }

}
