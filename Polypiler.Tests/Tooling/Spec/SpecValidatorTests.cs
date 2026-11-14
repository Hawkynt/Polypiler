using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Spec.Families;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// <see cref="SpecValidator"/> against the test design of SPEC-META: the metadata schema and rules, the
/// cross-file checks, the order of the steps and the failures of a family's schema.
/// </summary>
[TestFixture]
public sealed class SpecValidatorTests {

  private static string[] Lines(params (string Key, string? Value)[] changes) {
    using var repository = new SpecRepository();
    repository.Document("demo/widget.yaml", SpecText.Document(changes));
    return repository.Lines();
  }

  private static void AssertValid(params (string Key, string? Value)[] changes) => Assert.That(Lines(changes), Is.Empty);

  private static void AssertOne(string expected, params (string Key, string? Value)[] changes) {
    var lines = Lines(changes);
    Assert.That(lines, Has.Length.EqualTo(1), string.Join("\n", lines));
    Assert.That(lines[0], Does.StartWith(expected));
  }

  /// <summary>Test design 1.</summary>
  [Test]
  public void NormativeReviewedTestedDocument_IsValid() {
    using var repository = new SpecRepository();
    repository.Document("demo/widget.yaml", SpecText.Document());
    Assert.That(repository.Validate(), Is.EqualTo(new SpecValidation(1, [])).Using<SpecValidation>((a, b) => a.Documents == b.Documents && a.Diagnostics.SequenceEqual(b.Diagnostics)));
  }

  /// <summary>Test design 2.</summary>
  [Test]
  public void DraftWithoutTestsAndNormativeText_IsValid() => AssertValid(("status", "draft"), ("tests", "[]"), ("normative", "\"\""), ("reviewedRevision", "0"));

  /// <summary>Test design 3.</summary>
  [TestCase("draft")]
  [TestCase("normative")]
  [TestCase("deprecated")]
  public void EachStatus_IsValid(string status)
    => AssertValid(("status", status), ("deprecatedIn", status == "deprecated" ? "P1" : null));

  /// <summary>Test design 4.</summary>
  [TestCase("none")]
  [TestCase("additive")]
  [TestCase("breaking")]
  public void EachCompatibilityAtVersion2_IsValid(string compatibility) => AssertValid(("compatibility", compatibility));

  /// <summary>Test design 5.</summary>
  [TestCase("SPEC-META")]
  [TestCase("\"#12\"")]
  [TestCase("C++")]
  public void IssueAsBacklogIdOrNumber_IsValid(string issue) => AssertValid(("issue", issue));

  [TestCase("spec meta")]
  [TestCase("\"#0\"")]
  [TestCase("\"#\"")]
  public void IssueOfAnotherShape_IsASchemaViolation(string issue) => AssertOne("demo/widget.yaml(6,8): error PLY0040: /issue: ", ("issue", issue));

  /// <summary>Test design 6, 12, 14: each metadata rule, its message and its place.</summary>
  [TestCase("demo/widget.yaml(1,5): error PLY0041: id 'TYPES.u32' does not start with family prefix 'DEMO'", "id", "TYPES.u32")]
  [TestCase("demo/widget.yaml(1,5): error PLY0041: id 'DEMOS.widget' does not start with family prefix 'DEMO'", "id", "DEMOS.widget")]
  [TestCase("demo/widget.yaml(7,8): error PLY0041: normative document needs at least one test", "tests", "[]")]
  [TestCase("demo/widget.yaml(10,19): error PLY0041: normative document must be reviewed at its current version (reviewedRevision 1, version 2)", "reviewedRevision", "1")]
  [TestCase("demo/widget.yaml(4,9): error PLY0041: deprecatedIn is required when status is deprecated", "status", "deprecated")]
  [TestCase("demo/widget.yaml(12,15): error PLY0041: deprecatedIn is only allowed when status is deprecated", "deprecatedIn", "P2")]
  [TestCase("demo/widget.yaml(9,16): error PLY0041: compatibility must be none at version 1", "version", "1", "reviewedRevision", "1")]
  public void MetadataRule_IsReportedAtItsValue(string expected, string key, string value, string? key2 = null, string? value2 = null)
    => Assert.That(Lines(key2 is null ? [(key, value)] : [(key, value), (key2, value2)]), Is.EqualTo(new[] { expected }));

  /// <summary>Test design 7.</summary>
  [TestCase("0", false)]
  [TestCase("1", true)]
  [TestCase("2", true)]
  [TestCase("2147483648", false)]
  public void VersionBoundary(string version, bool valid) {
    var changes = new (string, string?)[] { ("version", version), ("status", "draft"), ("compatibility", "none"), ("reviewedRevision", "0") };
    if (valid)
      AssertValid(changes);
    else
      AssertOne("demo/widget.yaml(3,10): error PLY0040: /version: ", changes);
  }

  /// <summary>Test design 8.</summary>
  [TestCase("-1", "draft", "demo/widget.yaml(10,19): error PLY0040: /reviewedRevision: ")]
  [TestCase("0", "draft", null)]
  [TestCase("2", "normative", null)]
  [TestCase("3", "draft", "demo/widget.yaml(10,19): error PLY0044: reviewedRevision 3 exceeds version 2")]
  [TestCase("3", "normative", "demo/widget.yaml(10,19): error PLY0044: reviewedRevision 3 exceeds version 2")]
  public void ReviewedRevisionBoundary(string revision, string status, string? expected) {
    if (expected is null)
      AssertValid(("reviewedRevision", revision), ("status", status));
    else
      AssertOne(expected, ("reviewedRevision", revision), ("status", status));
  }

  /// <summary>Test design 9.</summary>
  [TestCase(0, false)]
  [TestCase(1, true)]
  [TestCase(120, true)]
  [TestCase(121, false)]
  public void TitleLengthBoundary(int length, bool valid) {
    var title = ("title", (string?)$"\"{new string('t', length)}\"");
    if (valid)
      AssertValid(title);
    else
      AssertOne("demo/widget.yaml(2,8): error PLY0040: /title: ", title);
  }

  /// <summary>Test design 10.</summary>
  [TestCase("P0", true)]
  [TestCase("P6", true)]
  [TestCase("P7", false)]
  public void IntroducedInBoundary(string phase, bool valid) {
    if (valid)
      AssertValid(("introducedIn", phase));
    else
      AssertOne("demo/widget.yaml(8,15): error PLY0040: /introducedIn: ", ("introducedIn", phase));
  }

  /// <summary>Test design 11.</summary>
  [TestCase("P2", null)]
  [TestCase("P3", null)]
  [TestCase("P1", "demo/widget.yaml(12,15): error PLY0044: deprecatedIn P1 is earlier than introducedIn P2")]
  public void DeprecatedInAgainstIntroducedIn(string deprecatedIn, string? expected) {
    var changes = new (string, string?)[] { ("status", "deprecated"), ("introducedIn", "P2"), ("deprecatedIn", deprecatedIn) };
    if (expected is null)
      AssertValid(changes);
    else
      Assert.That(Lines(changes), Is.EqualTo(new[] { expected }));
  }

  /// <summary>Test design 12: an empty list is fine for a draft.</summary>
  [Test]
  public void EmptyTestsForADraft_IsValid() => AssertValid(("status", "draft"), ("tests", "[]"));

  /// <summary>Test design 13.</summary>
  [Test]
  public void EmptyTree_HasNoDocumentsAndNoErrors() {
    using var repository = new SpecRepository();
    Directory.CreateDirectory(repository.SpecRoot);
    Assert.That((repository.Validate().Documents, repository.Validate().Diagnostics.Count), Is.EqualTo((0, 0)));
  }

  /// <summary>Test design 15: each unreadable file is one PLY0047 with line and column, and goes no further.</summary>
  [TestCase("a: [1\n", "demo/widget.yaml(2,1): error PLY0047: not valid YAML: ")]
  [TestCase("a: 1\n\tb: 2\n", "demo/widget.yaml(1,4): error PLY0047: not valid YAML: ")]
  [TestCase("id: DEMO.widget\nid: DEMO.widget\n", "demo/widget.yaml(2,1): error PLY0047: duplicate key 'id'")]
  [TestCase("id: &a DEMO.widget\ntitle: *a\n", "demo/widget.yaml(1,5): error PLY0047: anchors are not allowed")]
  public void UnreadableFile_IsOneDiagnostic(string text, string expected) {
    using var repository = new SpecRepository();
    repository.Document("demo/widget.yaml", text);
    var result = repository.Validate();
    Assert.That(result.Documents, Is.EqualTo(1));
    Assert.That(result.Diagnostics.Select(DiagnosticRenderer.ToText).Single(), Does.StartWith(expected));
  }

  /// <summary>Test design 16, and every key the PRD requires: a missing one is a schema violation at the document.</summary>
  [Test]
  public void UnknownTopLevelKey_IsASchemaViolation()
    => Assert.That(Lines(("notes", "x")), Is.EqualTo(new[] { "demo/widget.yaml(12,8): error PLY0040: /notes: property 'notes' is not allowed" }));

  [TestCase("id")]
  [TestCase("title")]
  [TestCase("version")]
  [TestCase("status")]
  [TestCase("owner")]
  [TestCase("issue")]
  [TestCase("tests")]
  [TestCase("introducedIn")]
  [TestCase("compatibility")]
  [TestCase("reviewedRevision")]
  [TestCase("normative")]
  [TestCase("spec")]
  public void MissingKey_IsASchemaViolation(string key)
    => Assert.That(Lines((key, null)), Is.EqualTo(new[] { $"demo/widget.yaml(1,1): error PLY0040: /: Required properties [\"{key}\"] are not present" }));

  [TestCase("owner", "\" \"", "demo/widget.yaml(5,8): error PLY0040: /owner: ")]
  [TestCase("normative", "\"\"", "demo/widget.yaml(11,12): error PLY0040: /normative: ")]
  [TestCase("tests", $"[{SpecRepository.TestPath}, {SpecRepository.TestPath}]", "demo/widget.yaml(7,8): error PLY0040: /tests: ")]
  [TestCase("version", "\"2\"", "demo/widget.yaml(3,10): error PLY0040: /version: ")]
  [TestCase("status", "final", "demo/widget.yaml(4,9): error PLY0040: /status: ")]
  public void WrongValue_IsASchemaViolation(string key, string value, string expected) => AssertOne(expected, (key, value));

  [Test]
  public void DocumentThatIsNoMapping_IsASchemaViolation() {
    using var repository = new SpecRepository();
    repository.Document("demo/widget.yaml", "- a\n");
    Assert.That(repository.Lines().Single(), Does.StartWith("demo/widget.yaml(1,1): error PLY0040: /: "));
  }

  /// <summary>Test design 17.</summary>
  [Test]
  public void DuplicateId_IsReportedOnTheLaterPath() {
    using var repository = new SpecRepository();
    repository.Document("demo/b.yaml", SpecText.Document()).Document("demo/a.yaml", SpecText.Document());
    Assert.That(repository.Lines(), Is.EqualTo(new[] { "demo/b.yaml(1,5): error PLY0042: duplicate spec id 'DEMO.widget', first defined in demo/a.yaml" }));
  }

  [Test]
  public void DuplicateIdInTwoFamilies_IsReportedOnTheLaterPath() {
    using var repository = new SpecRepository();
    var families = new ISpecFamily[] { new DemoSpecFamily(), new TestFamily("other", "spec/schemas/demo.schema.json") };
    repository.Document("demo/a.yaml", SpecText.Document()).Document("other/a.yaml", SpecText.Document());
    var lines = new SpecValidator(families).Run(repository.Tree()).Diagnostics.Select(DiagnosticRenderer.ToText);
    Assert.That(lines, Is.EqualTo(new[] {
      "other/a.yaml(1,5): error PLY0041: id 'DEMO.widget' does not start with family prefix 'OTHER'",
      "other/a.yaml(1,5): error PLY0042: duplicate spec id 'DEMO.widget', first defined in demo/a.yaml"
    }));
  }

  /// <summary>Test design 18.</summary>
  [TestCase("tests/nowhere.feature", "demo/widget.yaml(7,9): error PLY0043: test path 'tests/nowhere.feature' does not exist")]
  [TestCase("../x", "demo/widget.yaml(7,9): error PLY0043: test path '../x' is outside the repository")]
  [TestCase("..", "demo/widget.yaml(7,9): error PLY0043: test path '..' is outside the repository")]
  [TestCase("tests/../../x", "demo/widget.yaml(7,9): error PLY0043: test path 'tests/../../x' is outside the repository")]
  public void BrokenTestPath_IsPLY0043(string path, string expected) => Assert.That(Lines(("tests", $"[{path}]")), Is.EqualTo(new[] { expected }));

  [Test]
  public void TestPathToAFolder_Exists() => AssertValid(("tests", "[tests]"));

  [Test]
  public void TestPaths_AreResolvedAgainstTheRepositoryRootNotTheSpecRoot() {
    using var repository = new SpecRepository();
    repository.Document("demo/widget.yaml", SpecText.Document()).Document(SpecRepository.TestPath, "x");
    File.Delete(Path.Combine(repository.Root, SpecRepository.TestPath));
    Assert.That(repository.Lines().Single(), Does.Contain("PLY0043: test path 'tests/widget.feature' does not exist"));
  }

  /// <summary>Test design 19: an unknown family named on the command line.</summary>
  [Test]
  public void UnknownFamilyOption_IsPLY0047WithoutDocuments() {
    using var repository = new SpecRepository();
    repository.Document("demo/widget.yaml", SpecText.Document());
    var result = repository.Validate("gadgets");
    Assert.That(result.Documents, Is.Zero);
    Assert.That(result.Diagnostics.Select(DiagnosticRenderer.ToText), Is.EqualTo(new[] { "polypiler: error PLY0047: unknown spec family 'gadgets'" }));
  }

  [Test]
  public void RootThatDoesNotExist_IsPLY0047() {
    using var repository = new SpecRepository();
    var root = Path.Combine(repository.Root, "nowhere");
    Assert.That(new SpecValidator(SpecFamilies.All).Validate(root).Select(DiagnosticRenderer.ToText), Is.EqualTo(new[] { $"polypiler: error PLY0047: spec root '{root}' does not exist" }));
  }

  [Test]
  public void Validate_ReadsTheTreeAndFindsTheRepository() {
    using var repository = new SpecRepository();
    repository.Document("demo/widget.yaml", SpecText.Document());
    Assert.That(new SpecValidator(SpecFamilies.All).Validate(repository.SpecRoot), Is.Empty);
  }

  [Test]
  public void DocumentOfAnUnknownFolder_IsPLY0047AndGoesNoFurther() {
    using var repository = new SpecRepository();
    repository.Document("gadgets/thing.yaml", "- not even metadata\n");
    var result = repository.Validate();
    Assert.That(result.Documents, Is.EqualTo(1));
    Assert.That(result.Diagnostics.Select(DiagnosticRenderer.ToText), Is.EqualTo(new[] { "gadgets/thing.yaml(1,1): error PLY0047: unknown spec family 'gadgets'" }));
  }

  /// <summary>Test design 20: a family schema that is missing or not a schema.</summary>
  [TestCase(null, "cannot read schema 'spec/schemas/demo.schema.json' of family 'demo': ")]
  [TestCase("{\"type\": ", "cannot read schema 'spec/schemas/demo.schema.json' of family 'demo': ")]
  [TestCase("{\"type\": 3}", "cannot read schema 'spec/schemas/demo.schema.json' of family 'demo': ")]
  public void BrokenFamilySchema_IsNamedOnceAndNoDocumentPasses(string? schema, string expected) {
    using var repository = new SpecRepository();
    var path = Path.Combine(repository.Root, "spec", "schemas", "demo.schema.json");
    if (schema is null)
      File.Delete(path);
    else
      File.WriteAllText(path, schema);
    repository.Document("demo/a.yaml", SpecText.Document(("id", "DEMO.a"))).Document("demo/b.yaml", SpecText.Document(("id", "DEMO.b")));
    var result = repository.Validate();
    Assert.That(result.Documents, Is.EqualTo(2));
    Assert.That(result.Diagnostics.Single().Message, Does.StartWith(expected));
    Assert.That(result.Diagnostics.Single().Code, Is.EqualTo("PLY0047"));
  }

  [Test]
  public void BodyAgainstTheFamilySchema_ReportsThePointerUnderSpec() {
    using var repository = new SpecRepository();
    repository.Document("demo/widget.yaml", SpecText.Document(("spec", "\n  parts:\n    - name: Gear\n  extra: 1")));
    Assert.That(repository.Lines(), Is.EqualTo(new[] {
      "demo/widget.yaml(14,13): error PLY0040: /spec/parts/0/name: The string value is not a match for the indicated regular expression",
      "demo/widget.yaml(15,10): error PLY0040: /spec/extra: property 'extra' is not allowed"
    }));
  }

  [Test]
  public void NullBody_IsASchemaViolationAtSpec() {
    using var repository = new SpecRepository();
    repository.Document("demo/widget.yaml", SpecText.Document(("spec", "~")));
    Assert.That(repository.Lines().Single(), Does.StartWith("demo/widget.yaml(12,7): error PLY0040: /spec: "));
  }

  /// <summary>The order of the steps: a document that fails the metadata schema reaches neither the metadata rules nor the family.</summary>
  [Test]
  public void SchemaViolation_StopsTheLaterSteps()
    => Assert.That(Lines(("version", "two"), ("id", "TYPES.u32"), ("tests", "[nowhere]"), ("spec", "{parts: [{name: a}, {name: a}]}")),
      Is.EqualTo(new[] { "demo/widget.yaml(3,10): error PLY0040: /version: Value is \"string\" but should be \"integer\"" }));

  [Test]
  public void MetadataRule_StopsTheFamilySteps()
    => Assert.That(Lines(("reviewedRevision", "1"), ("spec", "{parts: [{name: a}, {name: a}]}")).Single(), Does.Contain("PLY0041"));

  [Test]
  public void OnlyTheSelectedFamily_IsValidatedAndCounted() {
    using var repository = new SpecRepository();
    var families = new ISpecFamily[] { new DemoSpecFamily(), new TestFamily("other", "spec/schemas/demo.schema.json") };
    repository.Document("demo/widget.yaml", SpecText.Document(("spec", "{parts: [{name: a, uses: OTHER.thing}]}")))
      .Document("other/thing.yaml", SpecText.Document(("id", "OTHER.thing"), ("version", "two")))
      .Document("other/broken.yaml", "a: [");
    var validator = new SpecValidator(families);
    var demo = validator.Run(repository.Tree(), "demo");
    Assert.That((demo.Documents, demo.Diagnostics.Count), Is.EqualTo((1, 0)));
    var all = validator.Run(repository.Tree());
    Assert.That((all.Documents, all.Diagnostics.Select(diagnostic => diagnostic.File)), Is.EqualTo((3, new[] { "other/broken.yaml", "other/thing.yaml" })).Using<(int, IEnumerable<string?>)>((a, b) => a.Item1 == b.Item1 && a.Item2.SequenceEqual(b.Item2)));
  }

  [Test]
  public void Diagnostics_AreSortedByPathLineColumnAndCode() {
    using var repository = new SpecRepository();
    repository.Document("demo/b.yaml", SpecText.Document(("id", "DEMO.b"), ("tests", "[x, y]"), ("reviewedRevision", "1")))
      .Document("demo/a.yaml", SpecText.Document(("id", "TYPES.a")));
    Assert.That(repository.Validate().Diagnostics.Select(diagnostic => (diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.Code)), Is.EqualTo(new[] {
      ("demo/a.yaml", 1, 5, "PLY0041"),
      ("demo/b.yaml", 7, 9, "PLY0043"),
      ("demo/b.yaml", 7, 12, "PLY0043"),
      ("demo/b.yaml", 10, 19, "PLY0041")
    }));
  }

  [Test]
  public void TwoFamiliesOnOneFolder_AreRefused()
    => Assert.Throws<ArgumentException>(() => new SpecValidator([new DemoSpecFamily(), new DemoSpecFamily()]));

  [Test]
  public void NullArguments_AreRefused() {
    Assert.Throws<ArgumentNullException>(() => new SpecValidator(null!));
    Assert.Throws<ArgumentNullException>(() => new SpecValidator([]).Validate(null!));
    Assert.Throws<ArgumentNullException>(() => new SpecValidator([]).Run(null!));
  }

  [Test]
  public void FamilyRules_RunLastWithTheTree() {
    using var repository = new SpecRepository();
    var family = new TestFamily("demo", "spec/schemas/demo.schema.json");
    repository.Document("demo/widget.yaml", SpecText.Document());
    var tree = repository.Tree();
    Assert.That(new SpecValidator([family]).Run(tree).Diagnostics.Select(DiagnosticRenderer.ToText), Is.EqualTo(new[] { "demo/widget.yaml(13,3): error PLY0046: checked (rule: test)" }));
    Assert.That(family.Seen, Is.EqualTo(new[] { (tree.Documents[0], tree) }));
  }

  /// <summary>A family schema with <c>oneOf</c> and <c>anyOf</c> for the tests of <see cref="Combinator_ReportsOnlyTheBestAlternative"/>.</summary>
  private const string CombinatorSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "type": "object",
      "properties": {
        "size": { "oneOf": [
          { "type": "integer", "minimum": 0 },
          { "type": "object", "required": ["width", "height"], "properties": { "width": { "type": "integer" } }, "additionalProperties": false },
          { "type": "string", "pattern": "^[a-z]+$" }
        ] },
        "flag": { "anyOf": [{ "type": "integer" }, { "type": "boolean" }] },
        "kind": { "maxLength": 3, "anyOf": [{ "const": "a" }, { "type": "string" }] },
        "count": { "multipleOf": 2, "oneOf": [{ "minimum": 0 }, { "maximum": 10 }] }
      }
    }
    """;

  /// <summary>
  /// A value that fails a <c>oneOf</c> or <c>anyOf</c> gets the errors of the best-matching alternative
  /// only — the first whose <c>type</c> holds, then the one with the fewest errors; a satisfied
  /// combinator adds nothing to the errors of its siblings, and a <c>oneOf</c> matched twice gets one summary.
  /// </summary>
  [TestCase("{size: {width: x, depth: 1}}",
    "/spec/size: Required properties [\"height\"] are not present",
    "/spec/size/width: Value is \"string\" but should be \"integer\"",
    "/spec/size/depth: property 'depth' is not allowed")]
  [TestCase("{size: -1}", "/spec/size: -1 should be at least 0")]
  [TestCase("{size: Big}", "/spec/size: The string value is not a match for the indicated regular expression")]
  [TestCase("{flag: s}", "/spec/flag: Value is \"string\" but should be \"integer\"")]
  [TestCase("{kind: abcd}", "/spec/kind: Value should be at most 3 characters")]
  [TestCase("{count: 15}", "/spec/count: 15 should be a multiple of 2")]
  [TestCase("{count: 4}", "/spec/count: the value matches 2 alternatives of oneOf, but must match exactly one")]
  [TestCase("{size: 3, flag: true, kind: a, count: 12}")]
  public void Combinator_ReportsOnlyTheBestAlternative(string body, params string[] expected) {
    using var repository = new SpecRepository();
    repository.Write("spec/schemas/demo.schema.json", CombinatorSchema).Document("demo/widget.yaml", SpecText.Document(("spec", body)));
    var family = new TestFamily("demo", "spec/schemas/demo.schema.json");
    var problems = new SpecValidator([family]).Run(repository.Tree()).Diagnostics;
    Assert.That(problems.Where(problem => problem.Code == "PLY0040").Select(problem => problem.Message), Is.EquivalentTo(expected));
    Assert.That(family.Seen, expected.Length == 0 ? Is.Not.Empty : Is.Empty);
  }

  /// <summary>A family that records what it is given and reports one rule per document.</summary>
  private sealed class TestFamily(string folder, string schemaPath) : ISpecFamily {

    public List<(SpecDocument, SpecTree)> Seen { get; } = [];

    public string Folder => folder;

    public string SchemaPath => schemaPath;

    public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
      this.Seen.Add((document, tree));
      return [SpecDiagnostics.Rule(document, "/spec", "checked", "test")];
    }

  }

}
