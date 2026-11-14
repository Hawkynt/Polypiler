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
/// <see cref="RewritesSpecFamily"/> through the whole engine: the schema <c>spec/schemas/rewrites.schema.json</c>,
/// every rule of the family, the fixtures of <c>tests/fixtures/spec/rewrites/</c> and the rows of the
/// issue's test design.
/// </summary>
[TestFixture]
public sealed class RewritesSpecFamilyTests {

  private const string Fixtures = "tests/fixtures/spec/rewrites";

  private const string Feature = "tests/features/infra/spec-rewrites.feature";

  private static readonly string _schema = Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "rewrites.schema.json");

  /// <summary>The body of a valid draft rewrite, key by key, as YAML values.</summary>
  private static Dictionary<string, string?> Body(string optimization) => new() {
    ["optimization"] = optimization,
    ["kind"] = "strength-reduce",
    ["before"] = "\"%r = Mul.u32[wrap] %x, 8\"",
    ["after"] = "\"%r = Shl.u32[strict] %x, 3\"",
    ["predicates"] = "[]",
    ["proof"] = "{class: smt-bitvector, widths: [8, 16, 32, 64], budgetSeconds: 5}",
    ["floatingPoint"] = "{touchesFloat: false, reassociates: false, contracts: false}",
    ["negativeFixtures"] = $"[tests/fixtures/optimize/{optimization.ToLowerInvariant()}/checked-multiply.pir]",
    ["switch"] = $"\"--disable-opt {optimization}\"",
    ["optLevels"] = "[balanced, speed, size]",
    ["interactions"] = "{after: [], enables: []}",
    ["document"] = $"docs/optimizations/{optimization}-multiply-to-shift.md",
  };

  /// <summary>A rewrite document: the valid draft with some body keys given other values (<see langword="null"/> removes one).</summary>
  private static string Rewrite(string optimization, params (string Key, string? Value)[] changes) => Rewrite(optimization, "draft", changes);

  private static string Rewrite(string optimization, string status, params (string Key, string? Value)[] changes) {
    var body = Body(optimization);
    foreach (var (key, value) in changes)
      body[key] = value;

    var normative = status == "normative";
    return $"id: REWRITES.{optimization}\ntitle: Rewrite {optimization}\nversion: 1\nstatus: {status}\nowner: Hawkynt\nissue: \"#59\"\n"
      + $"tests: [{(normative ? Feature : "")}]\nintroducedIn: P1\ncompatibility: none\nreviewedRevision: {(normative ? 1 : 0)}\n"
      + $"normative: {(normative ? "A rewrite MUST keep the meaning." : "\"\"")}\nspec:\n"
      + string.Concat(body.Where(pair => pair.Value is not null).Select(pair => $"  {pair.Key}: {pair.Value}\n"));
  }

  /// <summary>A throw-away repository with the family's schema and the feature its normative documents name.</summary>
  private static SpecRepository Repository() {
    var repository = new SpecRepository();
    repository.Write("spec/schemas/rewrites.schema.json", File.ReadAllText(_schema));
    repository.Write(Feature, "Feature: rewrites\n");
    return repository;
  }

  /// <summary>The diagnostics of the family for documents given by file name below <c>spec/rewrites/</c>.</summary>
  private static string[] Lines(params (string File, string Text)[] documents) {
    using var repository = Repository();
    foreach (var (file, text) in documents)
      repository.Document($"rewrites/{file}", text);
    return repository.Lines("rewrites");
  }

  private static string[] Lines(string optimization, params (string Key, string? Value)[] changes) => Lines(($"{optimization}.yaml", Rewrite(optimization, changes)));

  /// <summary>
  /// Puts a fixture of <c>invalid/</c> into a repository the way the acceptance step does: a single file
  /// under the name its ID gives, a folder with its files as they are.
  /// </summary>
  private static string[] FixtureLines(string fixture) {
    using var repository = Repository();
    var source = Path.Combine(PrototypeFixture.RepositoryRoot, Fixtures, "invalid", fixture);
    if (Directory.Exists(source))
      foreach (var file in Directory.EnumerateFiles(source, "*.yaml", SearchOption.AllDirectories))
        repository.Document($"rewrites/{Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/')}", File.ReadAllText(file));
    else {
      var text = File.ReadAllText(source);
      var id = text.Split('\n')[0]["id: REWRITES.".Length..];
      repository.Document($"rewrites/{id}.yaml", text);
    }

    return repository.Lines("rewrites");
  }

  private static string Rule(string message, string rule) => $"error PLY0046: {message} (rule: {rule})";

  [Test]
  public void Family_IsRegistered() {
    var family = SpecFamilies.All.OfType<RewritesSpecFamily>().Single();
    Assert.That((family.Folder, family.SchemaPath), Is.EqualTo(("rewrites", "spec/schemas/rewrites.schema.json")));
  }

  [Test]
  public void Schema_IsValidAgainstTheMetaSchema() {
    using var schema = JsonDocument.Parse(File.ReadAllText(_schema));
    Assert.That(MetaSchemas.Draft202012.Evaluate(schema.RootElement).IsValid, Is.True);
  }

  [Test]
  public void ValidFixture_HasNoErrors() {
    var validation = new SpecValidator(SpecFamilies.All).Run(SpecTree.Load(Path.Combine(PrototypeFixture.RepositoryRoot, Fixtures, "valid"), PrototypeFixture.RepositoryRoot), "rewrites");
    Assert.That((validation.Documents, validation.Diagnostics.Count), Is.EqualTo((1, 0)));
  }

  [Test]
  public void GeneratedDocument_IsValid() => Assert.That(Lines("O0001"), Is.Empty);

  [TestCase("id-in-block.yaml", "rewrites/O0150.yaml(13,17): error PLY0046: O0150 is outside the rewrite blocks O0001-O0099 and O0230-O0299 (rule: id-in-block)")]
  [TestCase("no-unproven-reassociation.yaml", "rewrites/O0002.yaml(19,12): error PLY0046: floating-point reassociation requires proof class float-exactness, found smt-bitvector (rule: no-unproven-reassociation)")]
  [TestCase("float-flags-consistent.yaml", "rewrites/O0002.yaml(25,19): error PLY0046: floatingPoint.reassociates is true but touchesFloat is false (rule: float-flags-consistent)")]
  [TestCase("interaction-acyclic", "rewrites/O0001.yaml(30,13): error PLY0046: interaction cycle O0001 -> O0002 -> O0001 (rule: interaction-acyclic)")]
  [TestCase("switch-matches.yaml", "rewrites/O0001.yaml(27,11): error PLY0046: switch '--disable-opt O0002' does not disable O0001 (rule: switch-matches)")]
  [TestCase("negative-fixture-exists.yaml", "rewrites/O0001.yaml(26,22): error PLY0046: negative fixture tests/fixtures/optimize/o0001/overflow.pir does not exist (rule: negative-fixture-exists)")]
  [TestCase("layout", "rewrites/O0001.yaml(1,5): error PLY0046: id 'REWRITES.O0002' does not match file name 'O0001.yaml' (rule: layout)")]
  [TestCase("layout-folder", "rewrites/folding/O0001.yaml(1,5): error PLY0046: file 'rewrites/folding/O0001.yaml' does not follow the layout rewrites/O<4 digits>.yaml (rule: layout)")]
  [TestCase("interaction-unknown.yaml", "rewrites/O0001.yaml(30,13): error PLY0043: unknown spec id 'REWRITES.O0050'")]
  public void InvalidFixture_IsRefusedWithItsRule(string fixture, string expected) => Assert.That(FixtureLines(fixture), Is.EqualTo(new[] { expected }));

  [TestCase("no-negative-fixture.yaml", "/spec/negativeFixtures:")]
  [TestCase("extra-property.yaml", "/spec/enabled: property 'enabled' is not allowed")]
  public void InvalidFixture_IsASchemaViolation(string fixture, string expected) {
    var line = FixtureLines(fixture).Single();
    Assert.That(line, Does.Contain($"error PLY0040: {expected}"));
  }

  /// <summary>Every required property of the schema, the nested ones as <c>parent.child</c>; <c>predicates.*</c> for the items of <c>predicates</c>.</summary>
  private static IEnumerable<string> RequiredProperties() {
    var schema = JsonNode.Parse(File.ReadAllText(_schema))!;
    foreach (var name in schema["required"]!.AsArray().Select(node => (string)node!)) {
      yield return name;
      var property = schema["properties"]![name]!;
      var nested = property["items"] ?? property;
      foreach (var child in nested["required"]?.AsArray().Select(node => (string)node!) ?? [])
        yield return $"{name}.{child}";
    }
  }

  [TestCaseSource(nameof(RequiredProperties))]
  public void MissingRequiredProperty_HasAFixtureAndIsASchemaViolation(string property) {
    var fixture = $"missing-{property}.yaml";
    Assert.That(File.Exists(Path.Combine(PrototypeFixture.RepositoryRoot, Fixtures, "invalid", fixture)), Is.True, fixture);
    var parent = property.Contains('.') ? "/spec/" + property[..property.IndexOf('.')] + (property.StartsWith("predicates.") ? "/0" : "") : "/spec";
    var child = property[(property.LastIndexOf('.') + 1)..];
    var line = FixtureLines(fixture).Single();
    Assert.That(line, Does.Contain($"error PLY0040: {parent}: Required properties").And.Contain($"\"{child}\""));
  }

  [Test]
  public void RequiredProperties_AreAllListed() => Assert.That(RequiredProperties().Count(), Is.EqualTo(21));

  /// <summary>The values of an enumeration of the schema, at a JSON pointer.</summary>
  private static IEnumerable<string> Enum(string pointer) {
    JsonNode node = JsonNode.Parse(File.ReadAllText(_schema))!;
    foreach (var part in pointer.Split('/', System.StringSplitOptions.RemoveEmptyEntries))
      node = node[part]!;
    return node["enum"]!.AsArray().Select(value => (string)value!);
  }

  private static IEnumerable<string> Kinds() => Enum("properties/kind");

  private static IEnumerable<string> ProofClasses() => Enum("properties/proof/properties/class");

  private static IEnumerable<string> Facts() => Enum("properties/predicates/items/properties/fact");

  private static IEnumerable<string> OptLevels() => Enum("properties/optLevels/items");

  [Test]
  public void Enumerations_HaveTheDeclaredSizes()
    => Assert.That((Kinds().Count(), ProofClasses().Count(), Facts().Count(), OptLevels().Count()), Is.EqualTo((10, 6, 6, 4)));

  [TestCaseSource(nameof(Kinds))]
  public void EachKind_IsValid(string kind) => Assert.That(Lines("O0001", ("kind", kind)), Is.Empty);

  [TestCaseSource(nameof(ProofClasses))]
  public void EachProofClass_IsValid(string proof) => Assert.That(Lines("O0001", ("proof", $"{{class: {proof}}}")), Is.Empty);

  [TestCaseSource(nameof(Facts))]
  public void EachPredicateFact_IsValid(string fact) => Assert.That(Lines("O0001", ("predicates", $"[{{id: holds, text: the condition holds, fact: {fact}}}]")), Is.Empty);

  [TestCaseSource(nameof(OptLevels))]
  public void EachOptLevel_IsValid(string level) => Assert.That(Lines("O0001", ("optLevels", $"[{level}]")), Is.Empty);

  [TestCase("kind", "inline")]
  [TestCase("proof", "{class: testing}")]
  [TestCase("predicates", "[{id: holds, text: the condition holds, fact: hope}]")]
  [TestCase("optLevels", "[none]")]
  [TestCase("optLevels", "[]")]
  [TestCase("proof", "{class: smt-bitvector, budgetSeconds: 601}")]
  [TestCase("switch", "\"--disable-opt O0001 now\"")]
  [TestCase("negativeFixtures", "[docs/elsewhere.pir]")]
  [TestCase("document", "docs/O0001-multiply-to-shift.md")]
  [TestCase("interactions", "{after: [O1], enables: []}")]
  public void UnknownOrMalformedValue_IsASchemaViolation(string key, string value)
    => Assert.That(Lines("O0001", (key, value)).Single(), Does.Contain("error PLY0040: /spec/" + key));

  [Test]
  public void FloatRewriteThatNeitherReassociatesNorContracts_IsValid()
    => Assert.That(Lines("O0001", ("floatingPoint", "{touchesFloat: true, reassociates: false, contracts: false}")), Is.Empty);

  private const string Exact = "[{id: exact-sum, text: a + b is exact in binary64, fact: float-exactness}]";

  [Test]
  public void ReassociationWithExactnessProofAndPredicate_IsValid()
    => Assert.That(Lines("O0002", ("floatingPoint", "{touchesFloat: true, reassociates: true, contracts: false}"), ("proof", "{class: float-exactness}"), ("predicates", Exact)), Is.Empty);

  [Test]
  public void ReassociationWithExactnessProofWithoutPredicate_IsRefused()
    => Assert.That(Lines("O0002", ("floatingPoint", "{touchesFloat: true, reassociates: true, contracts: false}"), ("proof", "{class: float-exactness}"),
        ("predicates", "[{id: other, text: something else, fact: range}]")).Single(),
      Does.EndWith(Rule("floating-point reassociation requires a predicate with fact float-exactness", "no-unproven-reassociation")));

  [Test]
  public void ContractionWithoutExactnessProof_IsRefused()
    => Assert.That(Lines("O0022", ("floatingPoint", "{touchesFloat: true, reassociates: false, contracts: true}")).Single(),
      Does.EndWith(Rule("floating-point contraction requires proof class float-exactness, found smt-bitvector", "no-unproven-reassociation")));

  [Test]
  public void ContractionWithoutFloat_IsInconsistent()
    => Assert.That(Lines("O0022", ("floatingPoint", "{touchesFloat: false, reassociates: false, contracts: true}"), ("proof", "{class: float-exactness}"), ("predicates", Exact)).Single(),
      Does.EndWith(Rule("floatingPoint.contracts is true but touchesFloat is false", "float-flags-consistent")));

  [TestCase("O0000", false)]
  [TestCase("O0001", true)]
  [TestCase("O0099", true)]
  [TestCase("O0100", false)]
  [TestCase("O0229", false)]
  [TestCase("O0230", true)]
  [TestCase("O0299", true)]
  [TestCase("O0300", false)]
  public void IdAtBlockEdge(string optimization, bool valid) {
    var expected = valid ? [] : new[] { $"rewrites/{optimization}.yaml(13,17): " + Rule($"{optimization} is outside the rewrite blocks O0001-O0099 and O0230-O0299", "id-in-block") };
    Assert.That((RewritesSpecFamily.InBlock(optimization), Lines(optimization)), Is.EqualTo((valid, expected)));
  }

  [TestCase("before")]
  [TestCase("after")]
  public void EmptyForm_IsASchemaViolation(string key) => Assert.That(Lines("O0001", (key, "\"\"")).Single(), Does.Contain($"error PLY0040: /spec/{key}:"));

  [TestCase("before")]
  [TestCase("after")]
  public void OneCharacterForm_IsValid(string key) => Assert.That(Lines("O0001", (key, "x")), Is.Empty);

  [Test]
  public void OneNegativeFixture_IsValid() => Assert.That(Lines("O0001", ("negativeFixtures", "[tests/fixtures/optimize/o0001/a.pir]")), Is.Empty);

  private static (string File, string Text) After(string optimization, params string[] after)
    => ($"{optimization}.yaml", Rewrite(optimization, ("interactions", $"{{after: [{string.Join(", ", after)}], enables: []}}")));

  [Test]
  public void SelfReference_IsACycle()
    => Assert.That(Lines(After("O0001", "O0001")), Is.EqualTo(new[] { "rewrites/O0001.yaml(23,26): " + Rule("interaction cycle O0001 -> O0001", "interaction-acyclic") }));

  [Test]
  public void CycleOfTwo_IsReportedOnceAtItsSmallestOptimization()
    => Assert.That(Lines(After("O0002", "O0001"), After("O0001", "O0002")), Is.EqualTo(new[] { "rewrites/O0001.yaml(23,26): " + Rule("interaction cycle O0001 -> O0002 -> O0001", "interaction-acyclic") }));

  [Test]
  public void CycleOfThree_IsReportedOnceAtItsSmallestOptimization()
    => Assert.That(Lines(After("O0001", "O0003"), After("O0003", "O0002"), After("O0002", "O0001")),
      Is.EqualTo(new[] { "rewrites/O0001.yaml(23,26): " + Rule("interaction cycle O0001 -> O0003 -> O0002 -> O0001", "interaction-acyclic") }));

  [Test]
  public void CycleIsFoundAtTheEntryThatStartsIt_AndTheShortestOneIsNamed()
    => Assert.That(Lines(After("O0001", "O0004", "O0002"), After("O0002", "O0003", "O0001"), After("O0003", "O0001"), After("O0004")),
      Is.EqualTo(new[] { "rewrites/O0001.yaml(23,33): " + Rule("interaction cycle O0001 -> O0002 -> O0001", "interaction-acyclic") }));

  [Test]
  public void CycleThroughASmallerOptimization_IsLeftToIt()
    => Assert.That(Lines(After("O0001", "O0003"), After("O0002", "O0003"), After("O0003", "O0001")),
      Is.EqualTo(new[] { "rewrites/O0001.yaml(23,26): " + Rule("interaction cycle O0001 -> O0003 -> O0001", "interaction-acyclic") }));

  [Test]
  public void ChainOfThreeWithoutCycle_IsValid()
    => Assert.That(Lines(After("O0001"), After("O0002", "O0001"), After("O0003", "O0002", "O0001")), Is.Empty);

  [Test]
  public void DiamondWithoutCycle_IsValid()
    => Assert.That(Lines(After("O0001"), After("O0002", "O0001"), After("O0003", "O0001"), After("O0004", "O0002", "O0003")), Is.Empty);

  [Test]
  public void Interactions_IgnoreDocumentsWithoutUsableLists() {
    var lines = Lines(After("O0001", "O0002"), After("O0002", "O0003"),
      ("O0003.yaml", "id: REWRITES.O0003\n"),
      ("O0004.yaml", Rewrite("O0004", ("interactions", null))),
      ("O0005.yaml", Rewrite("O0005", ("interactions", "{after: [1, O0001], enables: []}"))),
      ("folding/O0006.yaml", Rewrite("O0006", ("interactions", "{after: [O0001], enables: []}"))));
    Assert.That(lines.Where(line => line.Contains("rule: interaction-acyclic")), Is.Empty);
  }

  [Test]
  public void Interactions_UseOnlyStringEntriesOfOtherDocuments() {
    var lines = Lines(After("O0001", "O0005"), ("O0005.yaml", Rewrite("O0005", ("interactions", "{after: [1, O0001], enables: []}"))));
    Assert.That(lines, Does.Contain("rewrites/O0001.yaml(23,26): " + Rule("interaction cycle O0001 -> O0005 -> O0001", "interaction-acyclic")));
  }

  [Test]
  public void AfterNamingAMissingDocument_IsPLY0043()
    => Assert.That(Lines(After("O0001", "O0050")), Is.EqualTo(new[] { "rewrites/O0001.yaml(23,26): error PLY0043: unknown spec id 'REWRITES.O0050'" }));

  [Test]
  public void EnablesNamingAMissingDocument_IsPLY0043()
    => Assert.That(Lines("O0001", ("interactions", "{after: [], enables: [O0050]}")), Is.EqualTo(new[] { "rewrites/O0001.yaml(23,39): error PLY0043: unknown spec id 'REWRITES.O0050'" }));

  [Test]
  public void InteractionsNamingExistingDocuments_AreValid()
    => Assert.That(Lines(After("O0002", "O0001"), ("O0001.yaml", Rewrite("O0001", ("interactions", "{after: [], enables: [O0002]}")))), Is.Empty);

  [Test]
  public void NormativeWithExistingNegativeFixture_IsValid() {
    using var repository = Repository();
    repository.Write("tests/fixtures/optimize/o0001/checked-multiply.pir", "checked\n");
    repository.Document("rewrites/O0001.yaml", Rewrite("O0001", "normative"));
    Assert.That(repository.Lines("rewrites"), Is.Empty);
  }

  [Test]
  public void NormativeWithNegativeFixtureOutsideTheRepository_IsRefused() {
    using var repository = Repository();
    repository.Document("rewrites/O0001.yaml", Rewrite("O0001", "normative", ("negativeFixtures", "[tests/fixtures/../../../outside.pir]")));
    Assert.That(repository.Lines("rewrites"), Is.EqualTo(new[] { "rewrites/O0001.yaml(20,22): " + Rule("negative fixture tests/fixtures/../../../outside.pir does not exist", "negative-fixture-exists") }));
  }

  [Test]
  public void NormativeWithNegativeFixtureThatIsAFolder_IsRefused() {
    using var repository = Repository();
    repository.Write("tests/fixtures/optimize/o0001/checked-multiply.pir/keep", "");
    repository.Document("rewrites/O0001.yaml", Rewrite("O0001", "normative"));
    Assert.That(repository.Lines("rewrites").Single(), Does.EndWith("(rule: negative-fixture-exists)"));
  }

  [Test]
  public void OptimizationOtherThanTheFileName_BreaksTheLayout()
    => Assert.That(Lines(("O0001.yaml", Rewrite("O0001", ("optimization", "O0002"))))[0],
      Is.EqualTo("rewrites/O0001.yaml(13,17): " + Rule("optimization 'O0002' does not match file name 'O0001.yaml'", "layout")));

  [Test]
  public void DocumentPageOfAnotherOptimization_BreaksTheLayout()
    => Assert.That(Lines("O0001", ("document", "docs/optimizations/O0002-multiply-to-shift.md")),
      Is.EqualTo(new[] { "rewrites/O0001.yaml(24,13): " + Rule("document 'docs/optimizations/O0002-multiply-to-shift.md' does not belong to O0001", "layout") }));

  [TestCase("rewrites.yaml")]
  [TestCase("O001.yaml")]
  [TestCase("folding/O0001.yaml")]
  public void FileOutsideTheLayout_IsRefusedAndNothingElseIsChecked(string file)
    => Assert.That(Lines((file, Rewrite("O0150", ("switch", "\"--disable-opt O0151\"")))),
      Is.EqualTo(new[] { $"rewrites/{file}(1,5): " + Rule($"file 'rewrites/{file}' does not follow the layout rewrites/O<4 digits>.yaml", "layout") }));

  [Test]
  public void BrokenLayout_StopsTheOtherRules()
    => Assert.That(Lines(("O0001.yaml", Rewrite("O0150", ("switch", "\"--disable-opt O0151\"")))), Has.Length.EqualTo(3).And.All.EndWith("(rule: layout)"));

  [Test]
  public void Check_RejectsNullArguments() {
    var family = new RewritesSpecFamily();
    using var repository = Repository();
    var document = SpecDocument.Parse("rewrites/O0001.yaml", Rewrite("O0001"));
    Assert.Multiple(() => {
      Assert.That(() => family.Check(null!, repository.Tree()), Throws.ArgumentNullException);
      Assert.That(() => family.Check(document, null!), Throws.ArgumentNullException);
      Assert.That(() => RewritesSpecFamily.InBlock(null!), Throws.ArgumentNullException);
    });
  }

}
