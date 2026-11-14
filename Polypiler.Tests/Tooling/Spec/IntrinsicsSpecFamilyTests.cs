using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Spec.Families;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// <see cref="IntrinsicsSpecFamily"/> and <c>spec/schemas/intrinsics.schema.json</c>, run through the
/// whole engine: the seed documents of <c>spec/intrinsics/</c>, every fixture of
/// <c>tests/fixtures/spec/intrinsics/</c> and the equivalence classes and boundaries of the issue's test
/// design, written as variants of the seed <c>PopCount</c>.
/// </summary>
[TestFixture]
public sealed class IntrinsicsSpecFamilyTests {

  private const string PopCountPath = "intrinsics/bitcount/popcount.yaml";

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "spec", "intrinsics");

  private static readonly string _valid = Path.Combine(_fixtures, "valid");

  private static readonly string[] _rules = ["layout", "vector-arity", "vector-width", "unique-name", "integer-is-exact", "type-bound"];

  private static readonly string[] _groups = ["IntMath", "FloatMath", "Boolean", "Bitfield", "BitCount", "Shuffle", "Saturation", "Carry", "Endian", "Trig", "Sequence", "Matrix", "Tensor", "CopyFill"];

  /// <summary>A fixture file: where it goes in the tree (<c># at:</c>), what it must produce (<c># expect:</c>) and its ID.</summary>
  public sealed record Fixture(string Name, string At, string Expect, string Id, string Text) {

    /// <summary>Reads a fixture file.</summary>
    public static Fixture Read(string file) {
      var text = File.ReadAllText(file);
      var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
      string Header(string key) => lines.FirstOrDefault(line => line.StartsWith($"# {key}: ", StringComparison.Ordinal))?[(key.Length + 4)..] ?? "";
      return new(Path.GetFileName(file), Header("at"), Header("expect"), lines.First(line => line.StartsWith("id: ", StringComparison.Ordinal))[4..], text);
    }

    /// <inheritdoc />
    public override string ToString() => this.Name;

  }

  private static IEnumerable<Fixture> InvalidFixtures()
    => Directory.EnumerateFiles(Path.Combine(_fixtures, "invalid"), "*.yaml").Order(StringComparer.Ordinal).Select(Fixture.Read);

  private static string ValidText(string path) => File.ReadAllText(Path.Combine(_valid, path));

  private static string PopCount => ValidText(PopCountPath);

  private static string Sin => File.ReadAllText(Path.Combine(_fixtures, "variants", "sin.yaml"));

  private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

  /// <summary>A text with each old text replaced by a new one; every old text must occur exactly once.</summary>
  private static string Change(string text, params (string Old, string New)[] changes) {
    foreach (var (old, replacement) in changes) {
      Assert.That(text.Split(old).Length, Is.EqualTo(2), $"'{old}' must occur once");
      text = text.Replace(old, replacement, StringComparison.Ordinal);
    }
    return text;
  }

  /// <summary>A repository with the intrinsics schema, the documents of the valid tree except <paramref name="leaveOut"/>, then <paramref name="documents"/>.</summary>
  private static SpecRepository Repository(IEnumerable<string>? leaveOut, params (string Path, string Text)[] documents) {
    var repository = new SpecRepository();
    repository.Write("spec/schemas/intrinsics.schema.json", File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "intrinsics.schema.json")));
    var skipped = (leaveOut ?? []).ToHashSet(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(_valid, "*.yaml", SearchOption.AllDirectories))
      if (Relative(_valid, file) is var path && !skipped.Contains(path))
        repository.Document(path, File.ReadAllText(file));
    foreach (var (path, text) in documents)
      repository.Document(path, text);
    return repository;
  }

  /// <summary>The diagnostics of the intrinsics family over the valid tree with documents written over it.</summary>
  private static string[] Lines(params (string Path, string Text)[] documents) {
    using var repository = Repository(null, documents);
    return repository.Lines("intrinsics");
  }

  /// <summary>The diagnostics over the valid tree with <c>popcount.yaml</c> changed.</summary>
  private static string[] PopCountLines(params (string Old, string New)[] changes) => Lines((PopCountPath, Change(PopCount, changes)));

  /// <summary>The diagnostics of <c>popcount.yaml</c> with its body changed, moved to the folder of another group.</summary>
  private static string[] GroupLines(string group, params (string Old, string New)[] changes) {
    using var repository = Repository([PopCountPath], ($"intrinsics/{group.ToLowerInvariant()}/popcount.yaml", Change(PopCount, [("group: BitCount", "group: " + group), .. changes])));
    return repository.Lines("intrinsics");
  }

  private static string Problems(string[] lines) => string.Join("\n", lines);

  /// <summary>A vector line of <c>popcount.yaml</c>, by its index.</summary>
  private static string Vector(int index) => index switch {
    0 => "{types: {T: TYPES.u32}, inputs: [\"0x00000000\"], outputs: [0]}",
    1 => "{types: {T: TYPES.u32}, inputs: [\"0xFFFFFFFF\"], outputs: [32]}",
    2 => "{types: {T: TYPES.u32}, inputs: [\"0x80000001\"], outputs: [2]}",
    _ => "{types: {T: TYPES.u32}, inputs: [\"0x12345678\"], outputs: [13]}"
  };

  [Test]
  public void Family_IsRegistered() {
    var family = SpecFamilies.All.OfType<IntrinsicsSpecFamily>().Single();
    Assert.That((family.Folder, family.SchemaPath), Is.EqualTo(("intrinsics", "spec/schemas/intrinsics.schema.json")));
  }

  [Test]
  public void Check_RefusesNullArguments() {
    var family = new IntrinsicsSpecFamily();
    using var repository = Repository(null);
    var tree = repository.Tree();
    Assert.That(() => family.Check(null!, tree), Throws.ArgumentNullException);
    Assert.That(() => family.Check(tree.Documents[0], null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Schema_IsValidAgainstTheMetaSchema() {
    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "intrinsics.schema.json")));
    Assert.That(Json.Schema.MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  /// <summary>The seed documents of <c>spec/intrinsics/</c> validate with the rest of <c>spec/</c>.</summary>
  [Test]
  public void RepositorySpecTree_IntrinsicsFamilyValidates() {
    var tree = SpecTree.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec"), PrototypeFixture.RepositoryRoot);
    var validation = new SpecValidator(SpecFamilies.All).Run(tree, "intrinsics");
    Assert.That(validation.Diagnostics, Is.Empty);
    Assert.That(validation.Documents, Is.EqualTo(85));
  }

  /// <summary>
  /// The seed is every exact intrinsic the IR implements: each document names an operation of
  /// <see cref="IrIntrinsicOp"/>, and the operations without one are the four array operations and the
  /// floating-point operations whose contract is <c>ulp(n)</c>, which wait for their tolerance documents.
  /// </summary>
  [Test]
  public void Seed_IsEveryExactIntrinsicOfTheIr() {
    var tree = SpecTree.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec"), PrototypeFixture.RepositoryRoot);
    var names = tree.Documents.Where(document => document.Family == "intrinsics").Select(document => (string)document.Body!["name"]!).ToHashSet(StringComparer.Ordinal);
    Assert.That(names.Where(name => !Enum.TryParse<IrIntrinsicOp>(name, out _)), Is.Empty);
    var missing = Enum.GetValues<IrIntrinsicOp>().Where(op => !names.Contains(op.ToString())).ToList();
    bool Inexact(IrIntrinsicOp op) => FloatOperationTable.Of(op) is { Contract.Kind: FloatContractKind.Ulp } && !IntegerOperations.Operations.Contains(op);
    Assert.That(missing.Where(op => !Inexact(op)), Is.EquivalentTo(new[] { IrIntrinsicOp.ArrayOf, IrIntrinsicOp.ArrayCopy, IrIntrinsicOp.ArrayFill, IrIntrinsicOp.ArrayClear }));
    Assert.That(Enum.GetValues<IrIntrinsicOp>().Where(Inexact).Where(op => names.Contains(op.ToString())), Is.Empty);
  }

  /// <summary>
  /// Each seed document of a floating-point operation states the contract <see cref="FloatOperationTable"/>
  /// gives it, exact; an operation shared with the integers whose floating-point half is inexact
  /// (<c>Log2</c>) is described for the integers alone.
  /// </summary>
  [Test]
  public void SeedFloatOperations_AreExactAsTheIrStatesThem() {
    var tree = SpecTree.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec"), PrototypeFixture.RepositoryRoot);
    foreach (var document in tree.Documents.Where(document => document.Family == "intrinsics")) {
      var op = Enum.Parse<IrIntrinsicOp>((string)document.Body!["name"]!);
      if (FloatOperationTable.Of(op) is not { } operation)
        continue;
      if (operation.Contract.Kind == FloatContractKind.Ulp && IntegerOperations.Operations.Contains(op))
        Assert.That((string?)document.Body["typeParameters"]![0]!["constraint"], Is.EqualTo("integer"), op + ": the document holds the integer half; the floating-point half waits for its tolerance document");
      else
        Assert.That((operation.Contract.Kind, (string?)document.Body["exactness"]!["mode"]), Is.EqualTo((FloatContractKind.Exact, (string?)"exact")), op.ToString());
    }
  }

  /// <summary>The valid tree holds copies of the seed and of the documents it refers to.</summary>
  [TestCase(PopCountPath)]
  [TestCase("types/integer/u32.yaml")]
  [TestCase("types/integer/i32.yaml")]
  [TestCase("types/integer/u8.yaml")]
  [TestCase("types/float/f64.yaml")]
  [TestCase("ir/stages/legalized-target.yaml")]
  [TestCase("effects/unknown-call.yaml")]
  public void ValidTree_IsACopyOfTheSeedDocuments(string path)
    => Assert.That(ValidText(path), Is.EqualTo(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", path))));

  /// <summary>The valid tree's <c>Sin</c> and its tolerance are the documents of the verification family's valid tree.</summary>
  [TestCase("intrinsics/trig/sin.yaml")]
  [TestCase("verification/tolerances/sin.yaml")]
  public void ValidTree_SharesSinWithTheVerificationFixtures(string path)
    => Assert.That(ValidText(path), Is.EqualTo(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "spec", "verification", "valid", path))));

  /// <summary>
  /// Test design 1: the exact integer intrinsic with four vectors, and <c>Sin</c>, an inexact one whose
  /// policy is the tolerance document of the verification family.
  /// </summary>
  [Test]
  public void ValidTree_HasTwoDocumentsAndNoError() {
    using var repository = Repository(null);
    var validation = repository.Validate("intrinsics");
    Assert.That((validation.Documents, validation.Diagnostics.Count), Is.EqualTo((2, 0)));
  }

  /// <summary>
  /// Test design 9 and the oracle: each vector of <c>PopCount</c> agrees with a count of the set bits by
  /// nibbles, 0x12345678 giving 1+1+2+1+2+2+3+1 = 13, and 0xFFFFFFFF the maximum 32.
  /// </summary>
  [Test]
  public void PopCountVectors_AgreeWithACountByNibbles() {
    var vectors = SpecDocument.Parse(PopCountPath, PopCount).Body!["vectors"]!.AsArray();
    int[] nibbles = [0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4];
    foreach (var vector in vectors) {
      var value = Convert.ToUInt32((string)vector!["inputs"]![0]!, 16);
      var count = Enumerable.Range(0, 8).Sum(nibble => nibbles[(value >> (4 * nibble)) & 0xF]);
      Assert.That((long)vector["outputs"]![0]!, Is.EqualTo(count));
    }
    Assert.That(vectors.Select(vector => (long)vector!["outputs"]![0]!), Is.EqualTo(new long[] { 0, 32, 2, 13 }));
  }

  /// <summary>Test design 13–20: each fixture of <c>invalid/</c>, placed into the valid tree in place of the document with its path or ID, gives exactly its diagnostic.</summary>
  [TestCaseSource(nameof(InvalidFixtures))]
  public void InvalidFixture_GivesItsDiagnostic(Fixture fixture) {
    Assert.That((fixture.At, fixture.Expect), Is.Not.EqualTo(("", "")), "a fixture states '# at:' and '# expect:'");
    var leaveOut = Directory.EnumerateFiles(_valid, "*.yaml", SearchOption.AllDirectories)
      .Select(file => Relative(_valid, file))
      .Where(path => path == fixture.At || (fixture.Name != "unique-name.yaml" && Fixture.Read(Path.Combine(_valid, path)).Id == fixture.Id));
    using var repository = Repository(leaveOut, (fixture.At, fixture.Text));
    var lines = repository.Lines("intrinsics");
    Assert.That(lines, Has.Length.EqualTo(1), Problems(lines));
    Assert.That(lines[0], Does.Contain("error " + fixture.Expect).And.StartWith($"{fixture.At}("));
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
    var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "intrinsics.schema.json")))!;
    var definitions = schema["$defs"]!.AsObject();
    var expected = new SortedSet<string>(StringComparer.Ordinal);
    void Walk(JsonNode node, string path) {
      while (node["$ref"] is { } reference)
        node = definitions[((string)reference!).Split('/')[^1]]!;
      foreach (var name in node["required"]?.AsArray().Select(item => (string)item!) ?? [])
        expected.Add($"missing-{path}{name}.yaml");
      foreach (var (name, child) in node["properties"]?.AsObject() ?? new JsonObject())
        if (child is not null)
          Walk(child["items"] ?? child, $"{path}{name}-");
    }

    Walk(schema, "");
    var present = Directory.EnumerateFiles(Path.Combine(_fixtures, "invalid"), "missing-*.yaml").Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
    Assert.That(expected.Where(name => !present.Contains(name)), Is.Empty);
    Assert.That(expected, Has.Count.EqualTo(30));
  }

  /// <summary>Measurement: every value of every enumeration of the schema is used by a valid document of the seed or by a case of these tests.</summary>
  [Test]
  public void EveryEnumerationValue_IsDeclaredByTheSchemaAsTheIssueLists() {
    var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "intrinsics.schema.json")))!;
    string[] Values(JsonNode node) => [.. node["enum"]!.AsArray().Select(item => (string)item!)];
    Assert.That(Values(schema["properties"]!["group"]!), Is.EqualTo(_groups));
    Assert.That(Values(schema["properties"]!["typeParameters"]!["items"]!["properties"]!["constraint"]!), Is.EqualTo(new[] { "integer", "unsigned", "signed", "float", "any" }));
    Assert.That(Values(schema["properties"]!["legality"]!["items"]!["properties"]!["fact"]!), Is.EqualTo(new[] { "none", "range", "known-bits", "alias", "effect", "shape" }));
    Assert.That(Values(schema["properties"]!["exactness"]!["properties"]!["mode"]!), Is.EqualTo(new[] { "exact", "tolerance" }));
    Assert.That(Values(schema["properties"]!["algebra"]!["items"]!), Is.EqualTo(new[] { "commutative", "associative", "idempotent", "involution", "identity-element", "absorbing-element" }));
  }

  // Test design 2: each group; an unknown one is the fixture unknown-group.yaml.

  private static IEnumerable<TestCaseData> Groups() => _groups.Select(group => new TestCaseData(group).SetName($"Group_{group}_IsValid"));

  [TestCaseSource(nameof(Groups))]
  public void Group_IsValid(string group) => Assert.That(GroupLines(group), Is.Empty);

  // Test design 3: exact and tolerance.

  [Test]
  public void ToleranceWithPolicy_IsValid()
    => Assert.That(Lines(("intrinsics/trig/sin.yaml", Change(Sin, ("exactness: {mode: tolerance}", "exactness: {mode: tolerance, policy: VERIFICATION.tolerance.sin}")))), Is.Empty);

  [Test]
  public void ToleranceWithoutPolicy_IsASchemaViolation() {
    var lines = Lines(("intrinsics/trig/sin.yaml", Sin));
    Assert.That(lines.Single(), Is.EqualTo("intrinsics/trig/sin.yaml(22,14): error PLY0040: /spec/exactness: Required properties [\"policy\"] are not present"));
  }

  [Test]
  public void PolicyOfAnExactIntrinsic_IsASchemaViolation()
    => Assert.That(PopCountLines(("exactness: {mode: exact}", "exactness: {mode: exact, policy: VERIFICATION.tolerance.sin}")).Single(), Does.Contain("error PLY0040: /spec/exactness/policy:"));

  [Test]
  public void PolicyNamingNoDocument_IsABrokenReference()
    => Assert.That(Lines(("intrinsics/trig/sin.yaml", Change(Sin, ("exactness: {mode: tolerance}", "exactness: {mode: tolerance, policy: VERIFICATION.tolerance.cos}")))).Single(),
      Does.EndWith("error PLY0043: unknown spec id 'VERIFICATION.tolerance.cos'"));

  /// <summary>Test design 4 and the boundaries of the returns: one, two and four return values; an outputs count that does not match.</summary>
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(4)]
  public void ReturnValues_AreValid(int count) {
    var returns = string.Join(", ", Enumerable.Range(0, count).Select(index => $"{{name: r{index}, type: TYPES.i32}}"));
    var outputs = string.Join(", ", Enumerable.Repeat("0", count));
    var vectors = Enumerable.Range(0, 3).Select(index => (Vector(index), Vector(index).Replace(index switch { 0 => "[0]", 1 => "[32]", _ => "[2]" }, $"[{outputs}]", StringComparison.Ordinal)));
    Assert.That(PopCountLines([("returns: [{name: count, type: TYPES.i32}]", $"returns: [{returns}]"), .. vectors, (Vector(3), Vector(3).Replace("[13]", $"[{outputs}]", StringComparison.Ordinal))]), Is.Empty);
  }

  [Test]
  public void OutputsCountOfAnIntrinsicWithTwoReturnValues_BreaksVectorArity()
    => Assert.That(PopCountLines(("returns: [{name: count, type: TYPES.i32}]", "returns: [{name: quotient, type: T}, {name: remainder, type: T}]")),
      Has.Length.EqualTo(4).And.All.Contains("has 1 outputs, the intrinsic declares 2 return values (rule: vector-arity)"));

  [TestCase(0)]
  [TestCase(5)]
  public void ReturnValues_OutsideOneToFour_AreASchemaViolation(int count) {
    var returns = string.Join(", ", Enumerable.Range(0, count).Select(index => $"{{name: r{index}, type: TYPES.i32}}"));
    Assert.That(PopCountLines(("returns: [{name: count, type: TYPES.i32}]", $"returns: [{returns}]")).Single(), Does.Contain("error PLY0040: /spec/returns:"));
  }

  /// <summary>Test design 7: zero and eight parameters are valid, nine are not.</summary>
  [TestCase(0)]
  [TestCase(8)]
  public void Parameters_UpToEight_AreValid(int count) {
    var parameters = string.Join(", ", Enumerable.Range(0, count).Select(index => $"{{name: p{index}, type: T}}"));
    var inputs = string.Join(", ", Enumerable.Repeat("\"0x00000000\"", count));
    var changes = new List<(string, string)> { ("parameters: [{name: value, type: T}]", $"parameters: [{parameters}]") };
    foreach (var (input, index) in new[] { "\"0x00000000\"", "\"0xFFFFFFFF\"", "\"0x80000001\"", "\"0x12345678\"" }.Select((input, index) => (input, index)))
      changes.Add((Vector(index), Vector(index).Replace($"[{input}]", $"[{inputs}]", StringComparison.Ordinal)));
    Assert.That(PopCountLines([.. changes]), Is.Empty);
  }

  [Test]
  public void NineParameters_AreASchemaViolation() {
    var parameters = string.Join(", ", Enumerable.Range(0, 9).Select(index => $"{{name: p{index}, type: T}}"));
    Assert.That(PopCountLines(("parameters: [{name: value, type: T}]", $"parameters: [{parameters}]")).Single(), Does.Contain("error PLY0040: /spec/parameters:"));
  }

  [Test]
  public void InputsCountWithNoParameter_SaysParameters()
    => Assert.That(PopCountLines(("parameters: [{name: value, type: T}]", "parameters: []"))[0], Does.EndWith("vectors[0] has 1 inputs, the intrinsic declares 0 parameters (rule: vector-arity)"));

  /// <summary>Test design 5: each legality fact.</summary>
  [TestCase("none")]
  [TestCase("range")]
  [TestCase("known-bits")]
  [TestCase("alias")]
  [TestCase("effect")]
  [TestCase("shape")]
  public void LegalityFact_IsValid(string fact)
    => Assert.That(PopCountLines(("legality: []", $"legality: [{{id: condition, text: \"a condition\", fact: {fact}}}]")), Is.Empty);

  [Test]
  public void LegalityFactMagic_IsASchemaViolation()
    => Assert.That(PopCountLines(("legality: []", "legality: [{id: condition, text: \"a condition\", fact: magic}]")).Single(), Does.Contain("error PLY0040: /spec/legality/0/fact:"));

  /// <summary>Test design 6: three vectors are the minimum.</summary>
  [Test]
  public void ThreeVectors_AreValid() => Assert.That(PopCountLines(("    - " + Vector(3) + "\n", "")), Is.Empty);

  /// <summary>Test design 8: decimal literals at the ends of a signed and an unsigned range, and one beyond.</summary>
  [TestCase("TYPES.i32", "-2147483648", true)]
  [TestCase("TYPES.i32", "2147483647", true)]
  [TestCase("TYPES.i32", "-2147483649", false)]
  [TestCase("TYPES.i32", "2147483648", false)]
  [TestCase("TYPES.u32", "4294967295", true)]
  [TestCase("TYPES.u32", "4294967296", false)]
  [TestCase("TYPES.u32", "-1", false)]
  [TestCase("TYPES.i32", "\"0xFFFFFFFF\"", true)]
  [TestCase("TYPES.i32", "\"0x100000000\"", false)]
  public void DecimalAndHexLiterals_FitTheRangeOfTheirType(string type, string literal, bool fits) {
    var lines = PopCountLines((Vector(0), $"{{types: {{T: {type}}}, inputs: [{literal}], outputs: [0]}}"));
    if (fits)
      Assert.That(lines, Is.Empty);
    else
      Assert.That(lines.Single(), Does.EndWith($"error PLY0046: vectors[0].inputs[0] {literal.Trim('"')} does not fit {type} (32 bits) (rule: vector-width)"));
  }

  [Test]
  public void OutputOfTheWrongWidth_BreaksVectorWidth()
    => Assert.That(PopCountLines((Vector(3), Vector(3).Replace("[13]", "[\"0x1FFFFFFFF\"]", StringComparison.Ordinal))).Single(),
      Is.EqualTo($"{PopCountPath}(29,65): error PLY0046: vectors[3].outputs[0] 0x1FFFFFFFF does not fit TYPES.i32 (32 bits) (rule: vector-width)"));

  /// <summary>Test design 10: each optional property present is valid.</summary>
  [TestCase("attributes: [{name: n, type: TYPES.u32, low: 0, high: 0}]")]
  [TestCase("algebra: [commutative, associative, idempotent, involution, identity-element, absorbing-element]")]
  [TestCase("lowersTo: [INTRINSICS.PopCount, IR.stage.legalized-target]")]
  [TestCase("facts: {resultRange: [0, 32], knownZeroBits: \"0xFFFFFFC0\", knownOneBits: \"0x0\"}")]
  [TestCase("laneLiftable: true")]
  [TestCase("requiresRuntime: false")]
  [TestCase("spelling: Bits.PopCount")]
  public void OptionalProperty_IsValid(string property) => Assert.That(PopCountLines(("  vectors:\n", $"  {property}\n  vectors:\n")), Is.Empty);

  [Test]
  public void Accuracy_IsValidWithTolerance()
    => Assert.That(Lines(("intrinsics/trig/sin.yaml", Change(Sin, ("exactness: {mode: tolerance}", "exactness: {mode: tolerance, policy: VERIFICATION.tolerance.sin}"), ("accuracy: ulp(1)", "accuracy: library(crlibm)")))), Is.Empty);

  [TestCase("algebra: [distributive]", "/spec/algebra/0")]
  [TestCase("facts: {nonZero: true}", "/spec/facts/nonZero")]
  [TestCase("facts: {resultRange: [0]}", "/spec/facts/resultRange")]
  [TestCase("laneLiftable: \"yes\"", "/spec/laneLiftable")]
  [TestCase("requiresRuntime: 1", "/spec/requiresRuntime")]
  [TestCase("spelling: memory.copy", "/spec/spelling")]
  [TestCase("lowersTo: [TYPES.u32]", "/spec/lowersTo/0")]
  [TestCase("attributes: [{name: n, type: TYPES.u32, low: 0, high: \"0\"}]", "/spec/attributes/0/high")]
  public void OptionalPropertyOfTheWrongShape_IsASchemaViolation(string property, string pointer)
    => Assert.That(PopCountLines(("  vectors:\n", $"  {property}\n  vectors:\n")).Single(), Does.Contain($"error PLY0040: {pointer}:"));

  [TestCase("ulp(0)")]
  [TestCase("ulp")]
  [TestCase("library()")]
  public void AccuracyOfTheWrongShape_IsASchemaViolation(string accuracy)
    => Assert.That(Lines(("intrinsics/trig/sin.yaml", Change(Sin, ("exactness: {mode: tolerance}", "exactness: {mode: tolerance, policy: VERIFICATION.tolerance.sin}"), ("accuracy: ulp(1)", $"accuracy: \"{accuracy}\"")))).Single(),
      Does.Contain("error PLY0040: /spec/accuracy:"));

  [Test]
  public void AccuracyOfAnExactIntrinsic_IsASchemaViolation()
    => Assert.That(PopCountLines(("  vectors:\n", "  accuracy: cr\n  vectors:\n")).Single(), Does.Contain("error PLY0040: /spec/exactness/mode:"));

  [Test]
  public void AttributeTypeNamingNoDocument_IsABrokenReference()
    => Assert.That(PopCountLines(("  vectors:\n", "  attributes: [{name: n, type: TYPES.u7, low: 0, high: 1}]\n  vectors:\n")).Single(), Does.EndWith("error PLY0043: unknown spec id 'TYPES.u7'"));

  /// <summary>Test design 12: two intrinsics with the same spelling.</summary>
  [Test]
  public void SameSpellingTwice_BreaksUniqueName() {
    var copy = Change(PopCount, ("id: INTRINSICS.PopCount", "id: INTRINSICS.CopyBits"), ("name: PopCount", "name: CopyBits"), ("  vectors:\n", "  spelling: Memory.Copy\n  vectors:\n"));
    var lines = Lines((PopCountPath, Change(PopCount, ("  vectors:\n", "  spelling: Memory.Copy\n  vectors:\n"))), ("intrinsics/bitcount/copybits.yaml", copy));
    Assert.That(lines.Single(), Does.StartWith("intrinsics/bitcount/popcount.yaml(").And.EndWith("error PLY0046: spelling 'Memory.Copy' is already used by INTRINSICS.CopyBits (rule: unique-name)"));
  }

  /// <summary>Names differing only in case are one name: two files of one folder could not hold them on every file system.</summary>
  [Test]
  public void NameDifferingOnlyInCase_InAnotherGroup_BreaksUniqueName() {
    var other = Change(PopCount, ("id: INTRINSICS.PopCount", "id: INTRINSICS.POPCOUNT"), ("name: PopCount", "name: POPCOUNT"), ("group: BitCount", "group: Bitfield"));
    Assert.That(Lines(("intrinsics/bitfield/popcount.yaml", other)).Single(), Does.EndWith("error PLY0046: intrinsic 'POPCOUNT' is already defined by INTRINSICS.PopCount (rule: unique-name)"));
  }

  /// <summary>The rule <c>integer-is-exact</c> leaves the four inexact groups alone.</summary>
  [TestCase("FloatMath")]
  [TestCase("Trig")]
  [TestCase("Matrix")]
  [TestCase("Tensor")]
  public void InexactGroup_MayUseATolerance(string group)
    => Assert.That(GroupLines(group, ("exactness: {mode: exact}", "exactness: {mode: tolerance, policy: VERIFICATION.tolerance.sin}")), Is.Empty);

  /// <summary>Each constraint admits the types it names and refuses the others, under the rule <c>type-bound</c>.</summary>
  [TestCase("integer", "TYPES.u32", true)]
  [TestCase("unsigned", "TYPES.u32", true)]
  [TestCase("any", "TYPES.u32", true)]
  [TestCase("signed", "TYPES.i32", true)]
  [TestCase("signed", "TYPES.u32", false)]
  [TestCase("float", "TYPES.f64", true)]
  [TestCase("float", "TYPES.u32", false)]
  [TestCase("integer", "TYPES.f64", false)]
  [TestCase("unsigned", "TYPES.i32", false)]
  [TestCase("unsigned", "TYPES.f64", false)]
  [TestCase("signed", "TYPES.f64", false)]
  public void Constraint_AdmitsItsTypes(string constraint, string type, bool admitted) {
    var hex = type == "TYPES.f64" ? "\"0x0000000000000000\"" : "\"0x00000000\"";
    var changes = new List<(string, string)> { ("{name: T, constraint: integer}", $"{{name: T, constraint: {constraint}}}") };
    changes.AddRange(Enumerable.Range(0, 4).Select(index => (Vector(index), $"{{types: {{T: {type}}}, inputs: [{hex}], outputs: [0]}}")));
    var lines = PopCountLines([.. changes]);
    if (admitted)
      Assert.That(lines, Is.Empty, Problems(lines));
    else
      Assert.That(lines, Has.Length.EqualTo(4).And.All.Contains($"binds 'T' to {type} of family").And.All.EndWith($"which the constraint '{constraint}' excludes (rule: type-bound)"));
  }

  [Test]
  public void ParameterOfAnUndeclaredType_BreaksTypeBound()
    => Assert.That(PopCountLines(("parameters: [{name: value, type: T}]", "parameters: [{name: value, type: U}]")).Single(),
      Is.EqualTo($"{PopCountPath}(16,36): error PLY0046: parameters[0] has type 'U', which is no type parameter of the intrinsic (rule: type-bound)"));

  [Test]
  public void ReturnTypeNamingNoDocument_IsABrokenReference()
    => Assert.That(PopCountLines(("returns: [{name: count, type: TYPES.i32}]", "returns: [{name: count, type: TYPES.i31}]")).Single(), Does.EndWith("error PLY0043: unknown spec id 'TYPES.i31'"));

  [Test]
  public void VectorBindingAnUndeclaredParameter_BreaksTypeBound()
    => Assert.That(PopCountLines((Vector(0), "{types: {T: TYPES.u32, U: TYPES.u32}, inputs: [\"0x00000000\"], outputs: [0]}")).Single(),
      Does.EndWith("error PLY0046: vectors[0] binds 'U', which is no type parameter of the intrinsic (rule: type-bound)"));

  [Test]
  public void EffectId_ValidatesWhenTheDocumentExists()
    => Assert.That(PopCountLines(("effects: [pure]", "effects: [EFFECTS.unknown-call]")), Is.Empty);

  [Test]
  public void PureMixedWithAnEffect_IsASchemaViolation()
    => Assert.That(PopCountLines(("effects: [pure]", "effects: [pure, EFFECTS.unknown-call]")).Single(), Does.Contain("error PLY0040: /spec/effects:"));

  // Array types: a list of literals, each element checked.

  private static readonly (string Old, string New) _arrayParameter = ("parameters: [{name: value, type: T}]", "parameters: [{name: value, type: \"TYPES.u8[]\"}]");

  private static (string Old, string New)[] ArrayVectors(string first) => [
    (Vector(0), $"{{types: {{T: TYPES.u32}}, inputs: [{first}], outputs: [0]}}"),
    (Vector(1), "{types: {T: TYPES.u32}, inputs: [[]], outputs: [0]}"),
    (Vector(2), "{types: {T: TYPES.u32}, inputs: [[\"0x01\", 255]], outputs: [9]}"),
  ];

  [Test]
  public void ArrayParameter_TakesAListOfLiterals() => Assert.That(PopCountLines([_arrayParameter, .. ArrayVectors("[\"0xFF\"]"), (Vector(3), "{types: {T: TYPES.u32}, inputs: [[0]], outputs: [0]}")]), Is.Empty);

  [Test]
  public void ArrayElementTooWide_BreaksVectorWidth()
    => Assert.That(PopCountLines([_arrayParameter, .. ArrayVectors("[\"0x01\", \"0x100\"]"), (Vector(3), "{types: {T: TYPES.u32}, inputs: [[0]], outputs: [0]}")]).Single(),
      Is.EqualTo($"{PopCountPath}(26,49): error PLY0046: vectors[0].inputs[0][1] 0x100 does not fit TYPES.u8 (8 bits) (rule: vector-width)"));

  [Test]
  public void ScalarForAnArray_BreaksVectorWidth()
    => Assert.That(PopCountLines([_arrayParameter, .. ArrayVectors("[0]"), (Vector(3), "{types: {T: TYPES.u32}, inputs: [5], outputs: [0]}")]).Single(),
      Does.EndWith("error PLY0046: vectors[3].inputs[0] 5 is not a list, the value is an array of TYPES.u8 (rule: vector-width)"));

  [Test]
  public void ListForAScalar_BreaksVectorWidth()
    => Assert.That(PopCountLines((Vector(0), "{types: {T: TYPES.u32}, inputs: [[\"0x00\"]], outputs: [0]}")).Single(),
      Does.EndWith("error PLY0046: vectors[0].inputs[0] is a list, the value is a single TYPES.u32 (rule: vector-width)"));

  [Test]
  public void ArrayTypeNamingNoDocument_IsABrokenReference()
    => Assert.That(PopCountLines(("parameters: [{name: value, type: T}]", "parameters: [{name: value, type: \"TYPES.u7[]\"}]")).Single(), Does.EndWith("error PLY0043: unknown spec id 'TYPES.u7'"));

  /// <summary>
  /// A type document whose own body is broken — no <c>bits</c>, no <c>family</c> — reports itself; the
  /// vectors that bind it are not measured against it, and only the constraint it cannot show is reported.
  /// </summary>
  [Test]
  public void TypeDocumentWithoutBitsOrFamily_IsNotMeasured() {
    var broken = Change(ValidText("types/integer/u32.yaml"), ("  family: integer\n", ""), ("  bits: 32\n", ""), ("  signedness: unsigned\n", ""));
    var lines = Lines(("types/integer/u32.yaml", broken));
    Assert.That(lines, Has.Length.EqualTo(4).And.All.EndWith("binds 'T' to TYPES.u32 of family '' and signedness '', which the constraint 'integer' excludes (rule: type-bound)"));
  }

  [Test]
  public void TypeDocumentWithoutSignedness_IsNotMeasured() {
    var broken = Change(ValidText("types/integer/u32.yaml"), ("  signedness: unsigned\n", ""));
    Assert.That(Lines(("types/integer/u32.yaml", broken), (PopCountPath, Change(PopCount, (Vector(0), "{types: {T: TYPES.u32}, inputs: [\"0x1FFFFFFFF\"], outputs: [0]}")))), Is.Empty);
  }

  [Test]
  public void TypeDocumentWithBitsAsText_IsNotMeasured() {
    var broken = Change(ValidText("types/integer/u32.yaml"), ("  bits: 32\n", "  bits: \"32\"\n"));
    Assert.That(Lines(("types/integer/u32.yaml", broken), (PopCountPath, Change(PopCount, (Vector(0), "{types: {T: TYPES.u32}, inputs: [\"0x1FFFFFFFF\"], outputs: [0]}")))), Is.Empty);
  }

  /// <summary>A literal of a type of 64 bits and more is measured exactly.</summary>
  [Test]
  public void WideLiterals_AreMeasuredExactly() {
    var u64 = Change(ValidText("types/integer/u32.yaml"), ("id: TYPES.u32", "id: TYPES.u64"), ("bits: 32", "bits: 64"), ("sizeBytes: 4, alignment: 4", "sizeBytes: 8, alignment: 8"),
      ("    - {bits: \"0x00000000\", value: \"0\"}\n    - {bits: \"0x80000000\", value: \"2147483648\"}\n    - {bits: \"0xFFFFFFFF\", value: \"4294967295\"}\n", "    - {bits: \"0x0\", value: \"0\"}\n"));
    string Line(string literal) => $"{{types: {{T: TYPES.u64}}, inputs: [{literal}], outputs: [0]}}";
    var lines = Lines(("types/integer/u64.yaml", u64), (PopCountPath, Change(PopCount, (Vector(0), Line("18446744073709551615")), (Vector(1), Line("18446744073709551616")), (Vector(2), Line("\"0xFFFFFFFFFFFFFFFF\"")))));
    Assert.That(lines.Single(), Does.EndWith("vectors[1].inputs[0] 18446744073709551616 does not fit TYPES.u64 (64 bits) (rule: vector-width)"));
    Assert.That(BigInteger.Parse("18446744073709551616"), Is.EqualTo(BigInteger.One << 64));
  }

  /// <summary>A layout failure stops the other rules: one defect, one diagnostic.</summary>
  [Test]
  public void LayoutFailure_StopsTheOtherRules()
    => Assert.That(Lines((PopCountPath, Change(PopCount, ("id: INTRINSICS.PopCount", "id: INTRINSICS.Count"), (Vector(0), "{types: {T: TYPES.u32}, inputs: [], outputs: [0]}")))).Single(),
      Does.EndWith("(rule: layout)"));

}
