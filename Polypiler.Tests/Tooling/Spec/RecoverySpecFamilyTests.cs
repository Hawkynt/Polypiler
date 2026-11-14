using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Spec.Families;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// <see cref="RecoverySpecFamily"/>, the family of <c>spec/recovery/</c>: its schema, its seven rules
/// and the fixtures of <c>tests/fixtures/spec/recovery/</c>, run through the whole engine.
/// </summary>
[TestFixture]
public sealed class RecoverySpecFamilyTests {

  private const string Fixtures = "tests/fixtures/spec/recovery";

  private static readonly string _repository = PrototypeFixture.RepositoryRoot;

  /// <summary>The body of the valid document <c>O0170</c>, key by key, each value as YAML.</summary>
  private static readonly (string Key, string Value)[] _validBody = [
    ("optimization", "O0170"),
    ("technique", "de-unroll"),
    ("before", "\"Store.u8 %p+0, %v; Store.u8 %p+1, %v; Store.u8 %p+2, %v; Store.u8 %p+3, %v\""),
    ("after", "\"for %i in 0..3: Store.u8 %p+%i, %v\""),
    ("proofMode", "proven"),
    ("proofObligations", "[{id: same-writes, class: alias-facts, widths: [8]}]"),
    ("preserves", "[final-buffers, memory-effects, effect-order, traps]"),
    ("negativeCases", "[{description: \"the destination is volatile\", reason: \"access count is observable\"}]"),
    ("minRepetitions", "2"),
    ("switch", "\"--disable-opt O0170\""),
    ("document", "docs/optimizations/O0170-de-unroll-stores.md"),
  ];

  /// <summary>A pure expression slice: no store, no call, proven, preserving only the final buffers.</summary>
  private static readonly (string Key, string? Value)[] _pureSlice = [
    ("technique", "expression-slice"),
    ("before", "\"%t = Xor.u32 %a, %b; %r = Add.u32 %t, %c\""),
    ("after", "\"%r = Add.u32 Xor.u32(%a, %b), %c\""),
    ("preserves", "[final-buffers]"),
    ("minRepetitions", null),
  ];

  /// <summary>The valid recovery document with some body keys changed (<see langword="null"/> removes one; a new key is appended).</summary>
  private static string Document(string id, params (string Key, string? Value)[] changes) {
    var body = _validBody.Select(pair => (pair.Key, Value: (string?)pair.Value)).ToList();
    foreach (var (key, value) in changes) {
      var index = body.FindIndex(pair => pair.Key == key);
      if (index < 0)
        body.Add((key, value));
      else
        body[index] = (key, value);
    }

    var number = id[(id.IndexOf('.') + 1)..];
    var text = string.Concat(body.Where(pair => pair.Value is not null).Select(pair => $"\n  {pair.Key}: {pair.Value}"));
    return SpecText.Document(
      ("id", id),
      ("title", "Recovery rule " + number),
      ("version", "1"),
      ("status", "draft"),
      ("issue", "REC-ENGINE"),
      ("tests", "[]"),
      ("introducedIn", "P5"),
      ("compatibility", "none"),
      ("reviewedRevision", "0"),
      ("normative", "\"\""),
      ("spec", text));
  }

  /// <summary>A repository holding the recovery schema and the given documents under <c>spec/</c>.</summary>
  private static SpecRepository Repository(params (string Path, string Text)[] documents) {
    var repository = new SpecRepository();
    repository.Write("spec/schemas/recovery.schema.json", File.ReadAllText(Path.Combine(_repository, "spec", "schemas", "recovery.schema.json")));
    foreach (var (path, text) in documents)
      repository.Document(path, text);
    return repository;
  }

  private static string[] Lines(string path, string text) {
    using var repository = Repository((path, text));
    return repository.Lines("recovery");
  }

  private static string[] Lines(params (string Key, string? Value)[] changes) => Lines("recovery/O0170.yaml", Document("RECOVERY.O0170", changes));

  private static string Rule(string reason, string rule) => $"error PLY0046: {reason} (rule: {rule})";

  [Test]
  public void Family_IsRegistered() {
    var family = SpecFamilies.All.OfType<RecoverySpecFamily>().Single();
    Assert.That((family.Folder, family.SchemaPath), Is.EqualTo(("recovery", "spec/schemas/recovery.schema.json")));
  }

  [Test]
  public void Schema_IsValidAgainstTheMetaSchema() {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repository, "spec", "schemas", "recovery.schema.json")));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  // 1 — happy path

  [Test]
  public void ValidFixture_HasNoErrors() {
    using var repository = Repository(("recovery/O0170.yaml", File.ReadAllText(Path.Combine(_repository, Fixtures, "valid", "recovery", "O0170.yaml"))));
    var validation = repository.Validate("recovery");
    Assert.That((validation.Documents, validation.Diagnostics.Count), Is.EqualTo((1, 0)));
  }

  [Test]
  public void GeneratedValidDocument_HasNoErrors() => Assert.That(Lines(), Is.Empty);

  // 2 — every technique and every preserved effect, and an unknown value of each

  [TestCase("expression-slice")]
  [TestCase("compiler-idiom")]
  [TestCase("loop-reconstruct")]
  [TestCase("structure-reconstruct")]
  public void TechniqueOtherThanDeUnroll_WithoutMinRepetitions_IsValid(string technique)
    => Assert.That(Lines(("technique", technique), ("minRepetitions", null)), Is.Empty);

  [Test]
  public void EveryTechnique_IsDeclaredByTheSchema() {
    var declared = SchemaEnum("/properties/technique/enum");
    Assert.That(declared, Is.EquivalentTo(new[] { "de-unroll", "expression-slice", "compiler-idiom", "loop-reconstruct", "structure-reconstruct" }));
  }

  [Test]
  public void EveryPreservedEffect_IsValid() {
    var declared = SchemaEnum("/properties/preserves/items/enum");
    Assert.That(declared, Is.EquivalentTo(new[] { "final-buffers", "memory-effects", "effect-order", "traps", "flags", "timing" }));
    Assert.That(Lines(("preserves", $"[{string.Join(", ", declared)}]")), Is.Empty);
  }

  [Test]
  public void EveryObligationClass_IsValid() {
    var obligations = SchemaEnum("/properties/proofObligations/items/properties/class/enum").Select(name => $"{{id: {name}, class: {name}}}");
    Assert.That(Lines(("proofObligations", $"[{string.Join(", ", obligations)}]")), Is.Empty);
  }

  [TestCase("technique", "de-roll", "/spec/technique:")]
  [TestCase("preserves", "[final-buffers, memory-effects, effect-order, cache]", "/spec/preserves/3:")]
  [TestCase("proofMode", "guessed", "/spec/proofMode:")]
  [TestCase("proofObligations", "[{id: same-writes, class: hunch}]", "/spec/proofObligations/0/class:")]
  public void UnknownEnumerationValue_IsASchemaViolation(string key, string value, string pointer)
    => Assert.That(Lines((key, value)).Single(), Does.Contain("error PLY0040: " + pointer));

  // 3 — both proof modes

  [Test]
  public void Proven_WithObligations_IsValid() => Assert.That(Lines(("proofMode", "proven")), Is.Empty);

  [Test]
  public void SpeculativeGuarded_WithSpeculation_IsValid()
    => Assert.That(Lines(("proofMode", "speculative-guarded"), ("proofObligations", null), ("speculation", "{guard: \"%p+0 .. %p+3 is not volatile\", fallback: \"the four stores\"}")), Is.Empty);

  [Test]
  public void SpeculativeGuarded_WithSpeculationAndObligations_IsValid()
    => Assert.That(Lines(("proofMode", "speculative-guarded"), ("speculation", "{guard: \"%p is plain memory\", fallback: \"the four stores\"}")), Is.Empty);

  // 4 — mode and speculation disagree

  [Test]
  public void Proven_CarryingSpeculation_IsRefused()
    => Assert.That(Lines(("speculation", "{guard: \"%p is plain memory\", fallback: \"the four stores\"}")).Single(),
      Does.EndWith(Rule("proofMode 'proven' must not carry 'speculation'", "speculation-matches-mode")));

  [Test]
  public void SpeculativeGuarded_WithoutSpeculation_IsRefusedAtTheMode()
    => Assert.That(Lines(("proofMode", "speculative-guarded")), Is.EqualTo(new[] {
      "recovery/O0170.yaml(17,14): " + Rule("proofMode 'speculative-guarded' requires 'speculation' with guard and fallback", "speculation-matches-mode"),
    }));

  [TestCase("{guard: \"\", fallback: \"the four stores\"}", "/spec/speculation/guard:")]
  [TestCase("{guard: \"%p is plain memory\", fallback: \"\"}", "/spec/speculation/fallback:")]
  [TestCase("{guard: \"%p is plain memory\"}", "/spec/speculation:")]
  [TestCase("{fallback: \"the four stores\"}", "/spec/speculation:")]
  public void Speculation_WithEmptyOrMissingPart_IsASchemaViolation(string speculation, string pointer)
    => Assert.That(Lines(("proofMode", "speculative-guarded"), ("speculation", speculation)).Single(), Does.Contain("error PLY0040: " + pointer));

  // 5 — a pure rule

  [Test]
  public void PureSlice_PreservingOnlyFinalBuffers_IsValid() => Assert.That(Lines(_pureSlice), Is.Empty);

  // 6 — the edges of the block, and where a number of another family's recovery block belongs

  [TestCase("O0170")]
  [TestCase("O0185")]
  [TestCase("O0199")]
  public void NumberInsideTheBlock_IsValid(string number)
    => Assert.That(Lines($"recovery/{number}.yaml", Document("RECOVERY." + number, ("optimization", number), ("switch", $"\"--disable-opt {number}\""), ("document", $"docs/optimizations/{number}-de-unroll-stores.md"))), Is.Empty);

  [TestCase("O0000", "")]
  [TestCase("O0100", "")]
  [TestCase("O0169", "")]
  [TestCase("O0200", "; O0200 belongs to spec/recognition")]
  [TestCase("O0229", "; O0229 belongs to spec/recognition")]
  [TestCase("O0230", "; O0230 belongs to spec/rewrites")]
  [TestCase("O0255", "; O0255 belongs to spec/rewrites")]
  [TestCase("O0259", "; O0259 belongs to spec/rewrites")]
  [TestCase("O0260", "")]
  [TestCase("O9999", "")]
  public void NumberOutsideTheBlock_IsRefused(string number, string hint)
    => Assert.That(Lines($"recovery/{number}.yaml", Document("RECOVERY." + number, ("optimization", number), ("switch", $"\"--disable-opt {number}\""), ("document", $"docs/optimizations/{number}-de-unroll-stores.md"))),
      Is.EqualTo(new[] { $"recovery/{number}.yaml(13,17): " + Rule($"{number} is outside the recovery block O0170-O0199{hint}", "id-in-block") }));

  // 7 — minRepetitions

  [TestCase("1")]
  [TestCase("65537")]
  [TestCase("\"2\"")]
  public void MinRepetitionsOutsideItsRange_IsASchemaViolation(string value)
    => Assert.That(Lines(("minRepetitions", value)).Single(), Does.Contain("error PLY0040: /spec/minRepetitions:"));

  [TestCase("2")]
  [TestCase("65536")]
  public void MinRepetitionsAtItsEdges_IsValid(string value) => Assert.That(Lines(("minRepetitions", value)), Is.Empty);

  // 8 — the lists at their lower edge

  [Test]
  public void NoNegativeCase_IsASchemaViolation() => Assert.That(Lines(("negativeCases", "[]")).Single(), Does.Contain("error PLY0040: /spec/negativeCases:"));

  [Test]
  public void OneNegativeCase_IsValid() => Assert.That(Lines(("negativeCases", "[{description: \"overlapping writes\", reason: \"later values change\"}]")), Is.Empty);

  [TestCase("{description: \"overlapping writes\"}")]
  [TestCase("{description: \"\", reason: \"later values change\"}")]
  [TestCase("{description: \"overlapping writes\", reason: \"later values change\", source: forum}")]
  public void MalformedNegativeCase_IsASchemaViolation(string negativeCase)
    => Assert.That(Lines(("negativeCases", $"[{negativeCase}]")).Single(), Does.Contain("error PLY0040: /spec/negativeCases/0"));

  [Test]
  public void NothingPreserved_IsASchemaViolation() => Assert.That(Lines([.. _pureSlice, ("preserves", "[]")]).Single(), Does.Contain("error PLY0040: /spec/preserves:"));

  [Test]
  public void OnePreservedEffect_IsValid() => Assert.That(Lines(_pureSlice), Is.Empty);

  [Test]
  public void PreservedEffectListedTwice_IsASchemaViolation()
    => Assert.That(Lines([.. _pureSlice, ("preserves", "[final-buffers, final-buffers]")]).Single(), Does.Contain("error PLY0040: /spec/preserves:"));

  [TestCase("[{id: rotl, class: smt-bitvector, budgetSeconds: 0}]", "/spec/proofObligations/0/budgetSeconds:")]
  [TestCase("[{id: rotl, class: smt-bitvector, budgetSeconds: 601}]", "/spec/proofObligations/0/budgetSeconds:")]
  [TestCase("[{id: rotl, class: smt-bitvector, widths: []}]", "/spec/proofObligations/0/widths:")]
  [TestCase("[{id: Rotl, class: smt-bitvector}]", "/spec/proofObligations/0/id:")]
  [TestCase("[{class: smt-bitvector}]", "/spec/proofObligations/0:")]
  public void MalformedObligation_IsASchemaViolation(string obligations, string pointer)
    => Assert.That(Lines(("proofObligations", obligations)).Single(), Does.Contain("error PLY0040: " + pointer));

  [TestCase("[{id: rotl, class: smt-bitvector, budgetSeconds: 1}]")]
  [TestCase("[{id: rotl, class: smt-bitvector, widths: [8, 16], budgetSeconds: 600}]")]
  public void ObligationAtTheEdges_IsValid(string obligations) => Assert.That(Lines(("proofObligations", obligations)), Is.Empty);

  // 9 — minRepetitions belongs to de-unroll only

  [Test]
  public void MinRepetitionsOnAnotherTechnique_IsRefusedAtTheValue()
    => Assert.That(Lines(("technique", "compiler-idiom")), Is.EqualTo(new[] {
      "recovery/O0170.yaml(21,19): " + Rule("technique 'compiler-idiom' does not take 'minRepetitions'", "deunroll-needs-repetitions"),
    }));

  [Test]
  public void DeUnrollWithoutMinRepetitions_IsRefusedAtTheTechnique()
    => Assert.That(Lines(("minRepetitions", null)), Is.EqualTo(new[] {
      "recovery/O0170.yaml(14,14): " + Rule("technique 'de-unroll' requires 'minRepetitions'", "deunroll-needs-repetitions"),
    }));

  // proven-needs-obligation

  [Test]
  public void Proven_WithEmptyObligations_IsRefusedAtTheList()
    => Assert.That(Lines(("proofObligations", "[]")), Is.EqualTo(new[] {
      "recovery/O0170.yaml(18,21): " + Rule("proofMode 'proven' requires at least one proof obligation", "proven-needs-obligation"),
    }));

  [Test]
  public void Proven_WithoutObligations_IsRefusedAtTheMode()
    => Assert.That(Lines(("proofObligations", null)), Is.EqualTo(new[] {
      "recovery/O0170.yaml(17,14): " + Rule("proofMode 'proven' requires at least one proof obligation", "proven-needs-obligation"),
    }));

  // effects-preserved

  [TestCase("[final-buffers, memory-effects, traps]", "stores", "effect-order")]
  [TestCase("[final-buffers, effect-order, traps]", "stores", "memory-effects")]
  public void Stores_WithoutAnEffectPreserved_AreRefused(string preserves, string what, string missing)
    => Assert.That(Lines(("preserves", preserves)), Is.EqualTo(new[] {
      "recovery/O0170.yaml(19,14): " + Rule($"the rule rewrites {what} but does not preserve '{missing}'", "effects-preserved"),
    }));

  [Test]
  public void Stores_WithNeitherEffectPreserved_AreRefusedOncePerEffect()
    => Assert.That(Lines(("preserves", "[final-buffers]")), Is.EqualTo(new[] {
      "recovery/O0170.yaml(19,14): " + Rule("the rule rewrites stores but does not preserve 'effect-order'", "effects-preserved"),
      "recovery/O0170.yaml(19,14): " + Rule("the rule rewrites stores but does not preserve 'memory-effects'", "effects-preserved"),
    }));

  [TestCase("\"%r = Call @f, %a\"", "\"%r = Call @g, %a\"")]
  [TestCase("\"%r = Add.u32 %a, %b\"", "\"%r = Call @add, %a, %b\"")]
  public void Calls_WithoutEffectOrder_AreRefused(string before, string after)
    => Assert.That(Lines([.. _pureSlice, ("before", before), ("after", after), ("preserves", "[final-buffers, memory-effects]")]).Single(),
      Does.EndWith(Rule("the rule rewrites calls but does not preserve 'effect-order'", "effects-preserved")));

  [Test]
  public void StoreOnlyInTheRecoveredForm_IsRefused()
    => Assert.That(Lines([.. _pureSlice, ("after", "\"Store.u32 %p, Add.u32(%a, %b)\""), ("preserves", "[final-buffers, memory-effects]")]).Single(),
      Does.EndWith(Rule("the rule rewrites stores but does not preserve 'effect-order'", "effects-preserved")));

  // switch-matches

  [Test]
  public void SwitchOfAnotherOptimization_IsRefusedAtTheSwitch()
    => Assert.That(Lines(("switch", "\"--disable-opt O0171\"")), Is.EqualTo(new[] {
      "recovery/O0170.yaml(22,11): " + Rule("switch '--disable-opt O0171' does not disable O0170", "switch-matches"),
    }));

  [TestCase("--disable-opt O170")]
  [TestCase("--disable-opt  O0170")]
  [TestCase("--disable O0170")]
  public void MalformedSwitch_IsASchemaViolation(string value)
    => Assert.That(Lines(("switch", $"\"{value}\"")).Single(), Does.Contain("error PLY0040: /spec/switch:"));

  // layout

  [Test]
  public void IdNotMatchingTheFileName_IsRefusedAtTheId()
    => Assert.That(Lines("recovery/O0170.yaml", Document("RECOVERY.O0171")), Is.EqualTo(new[] {
      "recovery/O0170.yaml(1,5): " + Rule("id 'RECOVERY.O0171' does not match file name 'O0170.yaml'", "layout"),
    }));

  [Test]
  public void OptimizationNotMatchingTheFileName_IsRefusedAtTheOptimization()
    => Assert.That(Lines("recovery/O0171.yaml", Document("RECOVERY.O0171")), Is.EqualTo(new[] {
      "recovery/O0171.yaml(13,17): " + Rule("optimization 'O0170' does not match file name 'O0171.yaml'", "layout"),
    }));

  [Test]
  public void DocumentPageOfAnotherOptimization_IsRefusedAtTheDocument()
    => Assert.That(Lines(("document", "docs/optimizations/O0171-de-unroll-stores.md")), Is.EqualTo(new[] {
      "recovery/O0170.yaml(23,13): " + Rule("document 'docs/optimizations/O0171-de-unroll-stores.md' is not the page of O0170", "layout"),
    }));

  [TestCase("recovery/loops/O0170.yaml")]
  [TestCase("recovery/de-unroll.yaml")]
  [TestCase("recovery/O170.yaml")]
  public void FileOutsideTheLayout_IsRefused(string path)
    => Assert.That(Lines(path, Document("RECOVERY.O0170")), Is.EqualTo(new[] {
      $"{path}(1,1): " + Rule($"'{path}' is not recovery/O<4 digits>.yaml", "layout"),
    }));

  [TestCase("document", "docs/optimizations/O0170.md")]
  [TestCase("document", "docs/O0170-de-unroll-stores.md")]
  [TestCase("optimization", "O170")]
  [TestCase("before", "\"\"")]
  [TestCase("after", "\"\"")]
  public void MalformedValue_IsASchemaViolation(string key, string value)
    => Assert.That(Lines((key, value)), Has.Some.Contains($"error PLY0040: /spec/{key}:"));

  // 10–17 — every fixture of tests/fixtures/spec/recovery/invalid

  private static IEnumerable<TestCaseData> InvalidFixtures() {
    yield return new TestCaseData("layout.yaml", "O0170", Rule("id 'RECOVERY.O0171' does not match file name 'O0170.yaml'", "layout"));
    yield return new TestCaseData("id-in-block.yaml", "O0100", Rule("O0100 is outside the recovery block O0170-O0199", "id-in-block"));
    yield return new TestCaseData("speculation-matches-mode.yaml", "O0170", Rule("proofMode 'speculative-guarded' requires 'speculation' with guard and fallback", "speculation-matches-mode"));
    yield return new TestCaseData("proven-needs-obligation.yaml", "O0170", Rule("proofMode 'proven' requires at least one proof obligation", "proven-needs-obligation"));
    yield return new TestCaseData("deunroll-needs-repetitions.yaml", "O0170", Rule("technique 'de-unroll' requires 'minRepetitions'", "deunroll-needs-repetitions"));
    yield return new TestCaseData("effects-preserved.yaml", "O0170", Rule("the rule rewrites stores but does not preserve 'effect-order'", "effects-preserved"));
    yield return new TestCaseData("switch-matches.yaml", "O0170", Rule("switch '--disable-opt O0171' does not disable O0170", "switch-matches"));
    yield return new TestCaseData("no-negative-case.yaml", "O0170", "error PLY0040: /spec/negativeCases:");
    yield return new TestCaseData("extra-property.yaml", "O0170", "error PLY0040: /spec/author: property 'author' is not allowed");
    foreach (var key in RequiredProperties())
      yield return new TestCaseData($"missing-{key}.yaml", "O0170", $"error PLY0040: /spec: Required properties [\"{key}\"] are not present");
  }

  [TestCaseSource(nameof(InvalidFixtures))]
  public void InvalidFixture_IsRefusedWithOneDiagnostic(string fixture, string fileName, string expected) {
    var text = File.ReadAllText(Path.Combine(_repository, Fixtures, "invalid", fixture));
    Assert.That(Lines($"recovery/{fileName}.yaml", text).Single(), Does.Contain(expected));
  }

  [Test]
  public void EveryRuleAndEveryRequiredProperty_HasANegativeFixture() {
    var fixtures = Directory.EnumerateFiles(Path.Combine(_repository, Fixtures, "invalid")).Select(Path.GetFileName).ToHashSet();
    string[] rules = ["layout", "id-in-block", "speculation-matches-mode", "proven-needs-obligation", "deunroll-needs-repetitions", "effects-preserved", "switch-matches"];
    Assert.That(fixtures, Is.SupersetOf(rules.Select(rule => rule + ".yaml").Concat(RequiredProperties().Select(key => $"missing-{key}.yaml"))));
  }

  private static IEnumerable<string> RequiredProperties()
    => SchemaNode("/required").AsArray().Select(node => (string)node!);

  private static string[] SchemaEnum(string pointer) => [.. SchemaNode(pointer).AsArray().Select(node => (string)node!)];

  private static JsonNode SchemaNode(string pointer) {
    var node = JsonNode.Parse(File.ReadAllText(Path.Combine(_repository, "spec", "schemas", "recovery.schema.json")))!;
    foreach (var part in pointer.Split('/', System.StringSplitOptions.RemoveEmptyEntries))
      node = node is JsonArray array ? array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)]! : node[part]!;
    return node;
  }

}
