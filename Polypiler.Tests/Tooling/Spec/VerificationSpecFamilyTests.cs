using System.Collections.Generic;
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
/// <see cref="VerificationSpecFamily"/> and <c>spec/schemas/verification.schema.json</c>, run through the
/// whole engine: the fixtures of <c>tests/fixtures/spec/verification/</c>, the seed document of
/// <c>spec/verification/</c> and one document per row of the issue's test design. Every document is
/// validated beside the type and intrinsic documents of the <c>kinds</c> fixture tree it may name.
/// </summary>
[TestFixture]
public sealed class VerificationSpecFamilyTests {

  private const string Fixtures = "tests/fixtures/spec/verification";

  private const string Schema = "spec/schemas/verification.schema.json";

  private const string Family = "verification";

  private static string RepositoryFile(string path) => Path.Combine(PrototypeFixture.RepositoryRoot, path);

  /// <summary>A repository with the family's schema and every document of the <c>kinds</c> tree outside <c>verification/</c>.</summary>
  private static SpecRepository Repository() {
    var repository = new SpecRepository();
    repository.Write(Schema, File.ReadAllText(RepositoryFile(Schema)));
    var kinds = RepositoryFile($"{Fixtures}/kinds");
    foreach (var file in Directory.EnumerateFiles(kinds, "*.yaml", SearchOption.AllDirectories)) {
      var path = Path.GetRelativePath(kinds, file).Replace(Path.DirectorySeparatorChar, '/');
      if (!path.StartsWith(Family + "/", System.StringComparison.Ordinal))
        repository.Document(path, File.ReadAllText(file));
    }

    return repository;
  }

  /// <summary>The place a fixture's ID <c>VERIFICATION.&lt;kind&gt;.&lt;name&gt;</c> gives it: <c>verification/&lt;kind&gt;s/&lt;name&gt;.yaml</c>.</summary>
  public static string PlaceOf(string fixtureText) {
    var parts = SpecDocument.Parse("verification/fixture.yaml", fixtureText).Id!.Split('.', 3);
    return $"verification/{parts[1]}s/{parts[2]}.yaml";
  }

  private static string Header(string id) => $"id: {id}\ntitle: Test\nversion: 1\nstatus: draft\nowner: Hawkynt\nissue: VER-ORACLE\ntests: []\n"
    + "introducedIn: P0\ncompatibility: none\nreviewedRevision: 0\nnormative: \"\"\nspec:\n";

  /// <summary>The diagnostics of one document with the given body lines, at a path that follows the layout unless one is given.</summary>
  private static string[] Lines(string body, string path = "verification/generators/test.yaml", string? id = null) {
    using var repository = Repository();
    var parts = path.Split('/');
    id ??= $"VERIFICATION.{parts[^2].TrimEnd('s')}.{Path.GetFileNameWithoutExtension(path)}";
    repository.Document(path, Header(id) + body);
    return repository.Lines(Family);
  }

  private static string[] Generator(string type, string strategy, string extra = "")
    => Lines($"  kind: generator\n  type: {type}\n  strategy: {strategy}\n{extra}");

  private static string[] Boundary(string type, params string[] values)
    => Generator(type, "boundary", $"  values: [{string.Join(", ", values.Select(value => $"\"{value}\""))}]\n");

  private static string[] Tolerance(string mode, string extra = "")
    => Lines($"  kind: tolerance\n  operation: INTRINSICS.Sin\n  mode: {mode}\n{extra}", "verification/tolerances/test.yaml");

  private static string[] Oracle(string implementation, string observes = "[return-value]")
    => Lines($"  kind: oracle\n  implementation: {implementation}\n  observes: {observes}\n", "verification/oracles/test.yaml");

  private static string[] Enum(string property) {
    using var schema = JsonDocument.Parse(File.ReadAllText(RepositoryFile(Schema)));
    var node = schema.RootElement.GetProperty("properties").GetProperty(property);
    if (node.TryGetProperty("items", out var items))
      node = items;
    return [.. node.GetProperty("enum").EnumerateArray().Select(value => value.GetString()!)];
  }

  [Test]
  public void Family_IsRegistered() {
    var family = SpecFamilies.All.OfType<VerificationSpecFamily>().Single();
    Assert.That((family.Folder, family.SchemaPath), Is.EqualTo((Family, Schema)));
  }

  [Test]
  public void Schema_IsValidAgainstTheMetaSchema() {
    using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFile(Schema)));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  // Rows 1 and 2: the seed, an oracle observing return value and buffers, the ulp tolerance of ADR 0005 for Sin.
  [Test]
  public void ValidFixtureTree_HasNoErrors() {
    var result = new SpecValidator(SpecFamilies.All).Run(SpecTree.Load(RepositoryFile($"{Fixtures}/valid"), PrototypeFixture.RepositoryRoot), Family);
    Assert.That((result.Documents, result.Diagnostics.Count), Is.EqualTo((3, 0)));
  }

  [Test]
  public void SeedDocument_IsTheValidFixtureAndValidates() {
    Assert.That(File.ReadAllText(RepositoryFile("spec/verification/generators/u32-boundary.yaml")),
      Is.EqualTo(File.ReadAllText(RepositoryFile($"{Fixtures}/valid/verification/generators/u32-boundary.yaml"))));
    Assert.That(File.ReadAllText(RepositoryFile($"{Fixtures}/valid/types/integer/u32.yaml")), Is.EqualTo(File.ReadAllText(RepositoryFile("spec/types/integer/u32.yaml"))));
    var result = new SpecValidator(SpecFamilies.All).Run(SpecTree.Load(RepositoryFile("spec"), PrototypeFixture.RepositoryRoot), Family);
    Assert.That((result.Documents, result.Diagnostics.Count), Is.EqualTo((1, 0)));
  }

  [Test]
  public void SeedDocument_HoldsTheBoundaryValuesOfU32() {
    var body = SpecDocument.Parse("verification/generators/u32-boundary.yaml", File.ReadAllText(RepositoryFile("spec/verification/generators/u32-boundary.yaml"))).Body!;
    var values = body["values"]!.AsArray().Select(value => System.Convert.ToUInt32((string)value!, 16)).ToArray();
    Assert.That(values, Is.EqualTo(new[] { uint.MinValue, 1u, (uint)int.MaxValue, unchecked((uint)int.MinValue), uint.MaxValue - 1, uint.MaxValue }));
  }

  // Row 3: every kind, strategy, mode, special-value rule and observed behaviour.
  [Test]
  public void KindsFixtureTree_HasNoErrors() {
    var result = new SpecValidator(SpecFamilies.All).Run(SpecTree.Load(RepositoryFile($"{Fixtures}/kinds"), PrototypeFixture.RepositoryRoot), Family);
    Assert.That((result.Documents, result.Diagnostics.Count), Is.EqualTo((11, 0)));
  }

  [TestCase("kind", 3)]
  [TestCase("strategy", 4)]
  [TestCase("mode", 4)]
  [TestCase("specialValues", 3)]
  [TestCase("observes", 9)]
  public void KindsFixtures_UseEveryValueOfTheSchema(string property, int count) {
    var tree = SpecTree.Load(RepositoryFile($"{Fixtures}/kinds"), PrototypeFixture.RepositoryRoot);
    var used = new HashSet<string>();
    foreach (var document in tree.Documents.Where(document => document.Family == Family))
      switch (document.Body![property]) {
        case System.Text.Json.Nodes.JsonArray list:
          used.UnionWith(list.Select(value => (string)value!));
          break;
        case { } value:
          used.Add((string)value!);
          break;
      }

    Assert.That(used, Is.EquivalentTo(Enum(property)).And.Count.EqualTo(count));
  }

  [Test]
  public void SpecialValues_MayBeAbsent()
    => Assert.That(Tolerance("ulp", "  bound: 3\n"), Is.Empty);

  [TestCase("kind", "  kind: checker\n")]
  [TestCase("strategy", "  kind: generator\n  type: TYPES.u32\n  strategy: gaussian\n")]
  [TestCase("mode", "  kind: tolerance\n  operation: INTRINSICS.Sin\n  mode: ulps\n  bound: 1\n")]
  [TestCase("specialValues", "  kind: tolerance\n  operation: INTRINSICS.Sin\n  mode: exact\n  specialValues: payload\n")]
  [TestCase("observes/0", "  kind: oracle\n  implementation: node\n  observes: [stdout]\n")]
  public void UnknownEnumerationValue_IsASchemaViolation(string pointer, string body)
    => Assert.That(Lines(body).Single(), Does.Contain($"error PLY0040: /spec/{pointer}:"));

  [TestCase("  kind: generator\n  type: TYPES.u32\n  strategy: corpus\n  mode: exact\n", "mode")]
  [TestCase("  kind: tolerance\n  operation: INTRINSICS.Sin\n  mode: exact\n  type: TYPES.u32\n", "type")]
  [TestCase("  kind: oracle\n  implementation: node\n  observes: [io]\n  seed: 1\n", "seed")]
  public void PropertyOfAnotherKind_IsASchemaViolation(string body, string property)
    => Assert.That(Lines(body).Single(), Does.Contain($"error PLY0040: /spec/{property}:"));

  // Row 4: each strategy with and without the properties it requires.
  [TestCase("uniform", "  seed: 1\n  count: 1\n", null)]
  [TestCase("uniform", "  count: 1\n", "(15,13): error PLY0046: strategy 'uniform' requires 'seed'")]
  [TestCase("bit-pattern", "  seed: 1\n", "(15,13): error PLY0046: strategy 'bit-pattern' requires 'count'")]
  [TestCase("uniform", "  seed: 1\n  count: 1\n  values: [\"0x0\"]\n", "(18,11): error PLY0046: strategy 'uniform' does not take 'values'")]
  [TestCase("boundary", "  values: [\"0x00000000\", \"0xFFFFFFFF\"]\n", null)]
  [TestCase("boundary", "", "(15,13): error PLY0046: strategy 'boundary' requires 'values'")]
  [TestCase("boundary", "  values: [\"0x00000000\", \"0xFFFFFFFF\"]\n  seed: 7\n", "(17,9): error PLY0046: strategy 'boundary' does not take 'seed'")]
  [TestCase("corpus", "", null)]
  [TestCase("corpus", "  count: 5\n", "(16,10): error PLY0046: strategy 'corpus' does not take 'count'")]
  public void Strategy_RequiresExactlyItsProperties(string strategy, string extra, string? problem) {
    var lines = Generator("TYPES.u32", strategy, extra);
    Assert.That(lines, problem is null ? Is.Empty : Is.EqualTo(new[] { $"verification/generators/test.yaml{problem} (rule: strategy-requirements)" }));
  }

  [Test]
  public void Strategy_ReportsEveryMissingProperty()
    => Assert.That(Generator("TYPES.u32", "uniform"), Is.EqualTo(new[] {
      "verification/generators/test.yaml(15,13): error PLY0046: strategy 'uniform' requires 'count' (rule: strategy-requirements)",
      "verification/generators/test.yaml(15,13): error PLY0046: strategy 'uniform' requires 'seed' (rule: strategy-requirements)",
    }));

  // Row 5: a bound for every mode but exact.
  [TestCase("exact", "", null)]
  [TestCase("exact", "  bound: 1\n", "(16,10): error PLY0046: mode 'exact' takes no 'bound'")]
  [TestCase("ulp", "", "(15,9): error PLY0046: mode 'ulp' requires 'bound'")]
  [TestCase("relative", "", "(15,9): error PLY0046: mode 'relative' requires 'bound'")]
  [TestCase("absolute", "", "(15,9): error PLY0046: mode 'absolute' requires 'bound'")]
  [TestCase("absolute", "  bound: 0.5\n", null)]
  public void Bound_IsRequiredExceptForExact(string mode, string extra, string? problem) {
    var lines = Tolerance(mode, extra);
    Assert.That(lines, problem is null ? Is.Empty : Is.EqualTo(new[] { $"verification/tolerances/test.yaml{problem} (rule: bound-for-mode)" }));
  }

  // Row 6: the range of the seed.
  [TestCase("-1", false)]
  [TestCase("0", true)]
  [TestCase("4294967295", true)]
  [TestCase("4294967296", false)]
  public void Seed_Boundaries(string seed, bool valid) {
    var lines = Generator("TYPES.u32", "uniform", $"  seed: {seed}\n  count: 1\n");
    Assert.That(lines, valid ? Is.Empty : Has.Length.EqualTo(1).And.Some.Contains("error PLY0040: /spec/seed:"));
  }

  // Row 7: the range of the count.
  [TestCase("0", false)]
  [TestCase("1", true)]
  [TestCase("1000000", true)]
  [TestCase("1000001", false)]
  public void Count_Boundaries(string count, bool valid) {
    var lines = Generator("TYPES.u32", "bit-pattern", $"  seed: 1\n  count: {count}\n");
    Assert.That(lines, valid ? Is.Empty : Has.Length.EqualTo(1).And.Some.Contains("error PLY0040: /spec/count:"));
  }

  // Row 8: the bound is positive; in ulp mode a whole number of steps (ADR 0005).
  [TestCase("relative", "0", false)]
  [TestCase("relative", "1e-300", true)]
  [TestCase("absolute", "-1", false)]
  [TestCase("ulp", "1", true)]
  [TestCase("ulp", "1.5", false)]
  public void Bound_Boundaries(string mode, string bound, bool valid) {
    var lines = Tolerance(mode, $"  bound: {bound}\n");
    Assert.That(lines, valid ? Is.Empty : Has.Length.EqualTo(1).And.Some.Contains("error PLY0040: /spec/bound:"));
  }

  // Row 9: the type's maximum and one above it, as a bit pattern and as a decimal.
  [TestCase("0xFFFFFFFF", null)]
  [TestCase("4294967295", null)]
  [TestCase("0x100000000", "values[1] 0x100000000 does not fit TYPES.u32 (32 bits)")]
  [TestCase("4294967296", "values[1] 4294967296 does not fit TYPES.u32 (32 bits)")]
  [TestCase("-1", "values[1] -1 does not fit TYPES.u32 (32 bits)")]
  public void Value_MustFitTheType(string maximum, string? problem) {
    var lines = Boundary("TYPES.u32", "0x00000000", maximum, "0x7FFFFFFF");
    Assert.That(lines, problem is null ? Is.Empty : Has.Length.EqualTo(1).And.Some.EndsWith($"error PLY0046: {problem} (rule: value-fits-type)"));
  }

  [TestCase("-128", "127", null)]
  [TestCase("0x80", "0x7F", null)]
  [TestCase("-129", "127", "values[0] -129 does not fit TYPES.i8 (8 bits) (rule: value-fits-type)")]
  [TestCase("-128", "128", "values[1] 128 does not fit TYPES.i8 (8 bits) (rule: value-fits-type)")]
  [TestCase("0x80", "0x100", "values[1] 0x100 does not fit TYPES.i8 (8 bits) (rule: value-fits-type)")]
  [TestCase("0xFF", "0x7F", "boundary generator for TYPES.i8 lacks the minimum 0x80 (rule: boundary-covers-extremes)")]
  public void SignedType_ReadsBitPatternsInTwosComplement(string first, string second, string? problem) {
    var lines = Boundary("TYPES.i8", first, second);
    Assert.That(lines, problem is null ? Is.Empty : Has.Length.EqualTo(1).And.Some.EndsWith(problem));
  }

  // Row 10: the extremes of the type.
  [TestCase(new[] { "0x00000000", "0xFFFFFFFF" }, new string[0])]
  [TestCase(new[] { "0x00000001", "0xFFFFFFFF" }, new[] { "minimum 0x00000000" })]
  [TestCase(new[] { "0x00000000", "0xFFFFFFFE" }, new[] { "maximum 0xFFFFFFFF" })]
  [TestCase(new[] { "0x00000001" }, new[] { "maximum 0xFFFFFFFF", "minimum 0x00000000" })]
  [TestCase(new[] { "0", "4294967295" }, new string[0])]
  public void BoundaryGenerator_CoversTheExtremes(string[] values, string[] lacks)
    => Assert.That(Boundary("TYPES.u32", values), Is.EqualTo(lacks.Select(lack => $"verification/generators/test.yaml(16,11): error PLY0046: boundary generator for TYPES.u32 lacks the {lack} (rule: boundary-covers-extremes)")));

  [Test]
  public void FloatType_ChecksTheWidthOfBitPatternsOnly() {
    Assert.That(Boundary("TYPES.f64", "0x7FF8000000000000", "1"), Is.Empty);
    Assert.That(Boundary("TYPES.f64", "0x10000000000000000").Single(),
      Does.EndWith("error PLY0046: values[0] 0x10000000000000000 does not fit TYPES.f64 (64 bits) (rule: value-fits-type)"));
  }

  [TestCase("  {}\n")]
  [TestCase("  bits: 0\n")]
  [TestCase("  bits: \"32\"\n")]
  [TestCase("  - 32\n")]
  public void TypeWithoutAWidth_IsNotCheckedHere(string body) {
    using var repository = Repository();
    repository.Document("types/integer/u7.yaml", Header("TYPES.u7") + body);
    repository.Document("verification/generators/test.yaml", Header("VERIFICATION.generator.test") + "  kind: generator\n  type: TYPES.u7\n  strategy: boundary\n  values: [\"0x1FF\"]\n");
    Assert.That(repository.Lines(Family), Is.Empty);
  }

  [Test]
  public void TypeWithoutSignednessOrFamily_IsAnUnsignedPattern() {
    using var repository = Repository();
    repository.Document("types/integer/u7.yaml", Header("TYPES.u7") + "  bits: 7\n");
    repository.Document("verification/generators/test.yaml", Header("VERIFICATION.generator.test") + "  kind: generator\n  type: TYPES.u7\n  strategy: boundary\n  values: [\"0x80\", \"-5\"]\n");
    Assert.That(repository.Lines(Family), Is.EqualTo(new[] {
      "verification/generators/test.yaml(16,12): error PLY0046: values[0] 0x80 does not fit TYPES.u7 (7 bits) (rule: value-fits-type)",
    }));
  }

  [Test]
  public void UnknownType_IsABrokenReferenceAndNothingElse()
    => Assert.That(Boundary("TYPES.u33", "0x1FFFFFFFF"), Is.EqualTo(new[] { "verification/generators/test.yaml(14,9): error PLY0043: unknown spec id 'TYPES.u33'" }));

  [Test]
  public void UnknownOperation_IsABrokenReference()
    => Assert.That(Lines("  kind: tolerance\n  operation: IR.fsin\n  mode: exact\n", "verification/tolerances/test.yaml"),
      Is.EqualTo(new[] { "verification/tolerances/test.yaml(14,14): error PLY0043: unknown spec id 'IR.fsin'" }));

  // Row 11: what an oracle observes.
  [TestCase("[]", false)]
  [TestCase("[traps]", true)]
  [TestCase("[io, io]", false)]
  public void Observes_Boundaries(string observes, bool valid) {
    var lines = Oracle("node", observes);
    Assert.That(lines, valid ? Is.Empty : Has.Length.EqualTo(1).And.Some.Contains("error PLY0040: /spec/observes:"));
  }

  [TestCase("polypiler", false)]
  [TestCase("Polypiler", false)]
  [TestCase("polypiler-reference", true)]
  public void Oracle_IsIndependentOfPolypiler(string implementation, bool valid) {
    var lines = Oracle(implementation);
    Assert.That(lines, valid ? Is.Empty : Is.EqualTo(new[] {
      $"verification/oracles/test.yaml(14,19): error PLY0046: an oracle must be independent of Polypiler, found implementation '{implementation}' (rule: oracle-not-self)",
    }));
  }

  [Test]
  public void SecondToleranceOfAnOperation_IsReportedOnTheLaterPath() {
    using var repository = Repository();
    foreach (var name in new[] { "b", "a", "c" })
      repository.Document($"verification/tolerances/{name}.yaml", Header($"VERIFICATION.tolerance.{name}") + "  kind: tolerance\n  operation: INTRINSICS.Sin\n  mode: exact\n");
    repository.Document("verification/tolerances/d.yaml", Header("VERIFICATION.tolerance.d") + "  kind: tolerance\n  operation: INTRINSICS.Cos\n  mode: exact\n");
    Assert.That(repository.Lines(Family), Is.EqualTo(new[] {
      "verification/tolerances/b.yaml(14,14): error PLY0046: operation INTRINSICS.Sin already has tolerance VERIFICATION.tolerance.a (rule: one-tolerance-per-operation)",
      "verification/tolerances/c.yaml(14,14): error PLY0046: operation INTRINSICS.Sin already has tolerance VERIFICATION.tolerance.a (rule: one-tolerance-per-operation)",
    }));
  }

  [Test]
  public void OtherDocumentsNamingTheOperation_AreNoTolerances() {
    using var repository = Repository();
    repository.Document("verification/tolerances/b.yaml", Header("VERIFICATION.tolerance.b") + "  kind: tolerance\n  operation: INTRINSICS.Sin\n  mode: exact\n");
    repository.Document("demo/a.yaml", Header("DEMO.a") + "  kind: tolerance\n  operation: INTRINSICS.Sin\n");
    repository.Document("verification/a/empty.yaml", "");
    repository.Document("verification/a/list.yaml", Header("VERIFICATION.a.list") + "  - 1\n");
    repository.Document("verification/a/kind.yaml", Header("VERIFICATION.a.kind") + "  kind: [tolerance]\n  operation: INTRINSICS.Sin\n");
    repository.Document("verification/a/number.yaml", Header("VERIFICATION.a.number") + "  kind: 5\n  operation: INTRINSICS.Sin\n");
    repository.Document("verification/a/oracle.yaml", Header("VERIFICATION.a.oracle") + "  kind: oracle\n  operation: INTRINSICS.Sin\n");
    repository.Document("verification/a/operation.yaml", Header("VERIFICATION.a.operation") + "  kind: tolerance\n  operation: [INTRINSICS.Sin]\n");
    repository.Document("verification/a/count.yaml", Header("VERIFICATION.a.count") + "  kind: tolerance\n  operation: 5\n");
    var tolerance = repository.Lines(Family).Where(line => line.Contains("verification/tolerances/b.yaml", System.StringComparison.Ordinal));
    Assert.That(tolerance, Is.Empty);
  }

  // Rows 12 to 17, 19 and the layout: one diagnostic per fixture.
  [TestCase("strategy-requirements", "verification/generators/u32-uniform.yaml(15,13): error PLY0046: strategy 'uniform' requires 'seed' (rule: strategy-requirements)")]
  [TestCase("boundary-covers-extremes", "verification/generators/u32-no-maximum.yaml(16,11): error PLY0046: boundary generator for TYPES.u32 lacks the maximum 0xFFFFFFFF (rule: boundary-covers-extremes)")]
  [TestCase("value-fits-type", "verification/generators/u32-too-wide.yaml(16,82): error PLY0046: values[5] 0x100000000 does not fit TYPES.u32 (32 bits) (rule: value-fits-type)")]
  [TestCase("bound-for-mode", "verification/tolerances/cos.yaml(15,9): error PLY0046: mode 'ulp' requires 'bound' (rule: bound-for-mode)")]
  [TestCase("oracle-not-self", "verification/oracles/self.yaml(14,19): error PLY0046: an oracle must be independent of Polypiler, found implementation 'polypiler' (rule: oracle-not-self)")]
  [TestCase("unknown-type", "verification/generators/u33-boundary.yaml(14,9): error PLY0043: unknown spec id 'TYPES.u33'")]
  [TestCase("layout", "verification/generators/misplaced.yaml(13,9): error PLY0046: kind 'tolerance' must live in spec/verification/tolerances, found spec/verification/generators (rule: layout)")]
  [TestCase("extra-property", "verification/generators/extra.yaml(18,17): error PLY0040: /spec/distribution: property 'distribution' is not allowed")]
  public void InvalidFixture_IsRefusedWithOneDiagnostic(string fixture, string expected) {
    var text = File.ReadAllText(RepositoryFile($"{Fixtures}/invalid/{fixture}.yaml"));
    using var repository = Repository();
    repository.Document(PlaceOf(text), text);
    Assert.That(repository.Lines(Family), Is.EqualTo(new[] { expected }));
  }

  // Row 16: the fixture beside the valid tolerance of the same operation.
  [Test]
  public void OneTolerancePerOperationFixture_IsRefusedBesideTheValidTolerance() {
    var text = File.ReadAllText(RepositoryFile($"{Fixtures}/invalid/one-tolerance-per-operation.yaml"));
    using var repository = Repository();
    repository.Document("verification/tolerances/sin.yaml", File.ReadAllText(RepositoryFile($"{Fixtures}/valid/verification/tolerances/sin.yaml")));
    repository.Document(PlaceOf(text), text);
    Assert.That(repository.Lines(Family), Is.EqualTo(new[] {
      "verification/tolerances/sine.yaml(14,14): error PLY0046: operation INTRINSICS.Sin already has tolerance VERIFICATION.tolerance.sin (rule: one-tolerance-per-operation)",
    }));
  }

  // Row 18: one fixture per required property.
  [TestCase("kind", "/spec")]
  [TestCase("type", "/spec")]
  [TestCase("strategy", "/spec")]
  [TestCase("operation", "/spec")]
  [TestCase("mode", "/spec")]
  [TestCase("implementation", "/spec")]
  [TestCase("observes", "/spec")]
  public void MissingRequiredProperty_IsASchemaViolationAtItsParent(string property, string pointer) {
    var text = File.ReadAllText(RepositoryFile($"{Fixtures}/invalid/missing-{property}.yaml"));
    using var repository = Repository();
    repository.Document(PlaceOf(text), text);
    Assert.That(repository.Lines(Family).Single(), Does.Contain($"error PLY0040: {pointer}: Required properties").And.Contain($"\"{property}\""));
  }

  [Test]
  public void EveryRequiredPropertyOfTheSchema_HasAMissingFixture() {
    using var schema = JsonDocument.Parse(File.ReadAllText(RepositoryFile(Schema)));
    var required = schema.RootElement.GetProperty("required").EnumerateArray()
      .Concat(schema.RootElement.GetProperty("allOf").EnumerateArray().SelectMany(branch => branch.GetProperty("then").GetProperty("required").EnumerateArray()))
      .Select(value => value.GetString()!).ToArray();
    var fixtures = Directory.GetFiles(RepositoryFile($"{Fixtures}/invalid"), "missing-*.yaml").Select(file => Path.GetFileNameWithoutExtension(file)["missing-".Length..]).ToArray();
    Assert.That(fixtures, Is.EquivalentTo(required).And.Length.EqualTo(7));
  }

  // Row 20: a file outside the documented folders, and an ID that does not follow from the path.
  [Test]
  public void DocumentOutsideTheLayout_IsRefused() {
    using var repository = Repository();
    repository.Document("verification/u32-boundary.yaml", File.ReadAllText(RepositoryFile("spec/verification/generators/u32-boundary.yaml")));
    Assert.That(repository.Lines(Family), Is.EqualTo(new[] {
      "verification/u32-boundary.yaml(13,9): error PLY0046: kind 'generator' must live in spec/verification/generators, found spec/verification (rule: layout)",
    }));
  }

  [Test]
  public void IdThatDoesNotFollowFromThePath_IsRefused()
    => Assert.That(Lines("  kind: oracle\n  implementation: node\n  observes: [io]\n", "verification/oracles/test.yaml", "VERIFICATION.oracle.other"), Is.EqualTo(new[] {
      "verification/oracles/test.yaml(1,5): error PLY0046: id 'VERIFICATION.oracle.other' must be 'VERIFICATION.oracle.test' in spec/verification/oracles/test.yaml (rule: layout)",
    }));

  [Test]
  public void Check_RefusesNull() {
    var family = new VerificationSpecFamily();
    using var repository = Repository();
    var document = repository.Tree().Documents[0];
    Assert.That(() => family.Check(null!, repository.Tree()), Throws.ArgumentNullException);
    Assert.That(() => family.Check(document, null!), Throws.ArgumentNullException);
  }

}
