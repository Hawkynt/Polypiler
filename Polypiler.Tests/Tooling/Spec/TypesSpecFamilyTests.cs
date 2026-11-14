using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Spec.Families;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// <see cref="TypesSpecFamily"/>: the schema <c>spec/schemas/types.schema.json</c> and the rules of the
/// family, run through the whole engine on the seed documents of <c>spec/types/</c>, the fixtures of
/// <c>tests/fixtures/spec/types/</c> and documents written for one case each.
/// </summary>
[TestFixture]
public sealed class TypesSpecFamilyTests {

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "spec", "types");

  /// <summary>The body of a valid unsigned 32-bit integer, key by key, as YAML.</summary>
  private static readonly (string Key, string Value)[] _body = [
    ("family", "integer"),
    ("parameters", "[]"),
    ("bits", "32"),
    ("signedness", "unsigned"),
    ("overflow", "wrap"),
    ("layout", "{sizeBytes: 4, alignment: 4, byteOrder: target}"),
    ("distinctFrom", "[]"),
    ("values", "[{bits: \"0xFFFFFFFF\", value: \"4294967295\"}]"),
  ];

  /// <summary>A repository with the types schema.</summary>
  private static SpecRepository Repository()
    => new SpecRepository().Write("spec/schemas/types.schema.json", File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "types.schema.json")));

  /// <summary>A draft document with the ID and the body.</summary>
  private static string Document(string id, string body)
    => $"id: {id}\ntitle: A type\nversion: 1\nstatus: draft\nowner: Hawkynt\nissue: SPEC-TYPES\ntests: []\nintroducedIn: P1\n"
      + $"compatibility: none\nreviewedRevision: 0\nnormative: \"\"\nspec:\n{body}";

  /// <summary>The valid body with some keys given other values (<see langword="null"/> removes a key); a key the body lacks is appended.</summary>
  private static string Body(params (string Key, string? Value)[] changes) {
    var keys = _body.Select(pair => (pair.Key, Value: (string?)pair.Value)).ToList();
    foreach (var (key, value) in changes) {
      var index = keys.FindIndex(pair => pair.Key == key);
      if (index < 0)
        keys.Add((key, value));
      else
        keys[index] = (key, value);
    }

    return string.Concat(keys.Where(pair => pair.Value is not null).Select(pair => $"  {pair.Key}: {pair.Value}\n"));
  }

  /// <summary>The diagnostics of one document at <c>spec/&lt;path&gt;</c>, with further documents beside it.</summary>
  private static string[] Lines(string path, string body, params (string Path, string Text)[] others) {
    using var repository = Repository();
    repository.Document(path, Document("TYPES." + Path.GetFileNameWithoutExtension(path), body));
    foreach (var (other, text) in others)
      repository.Document(other, text);
    return repository.Lines("types");
  }

  /// <summary>The diagnostics of one document at <c>types/integer/t.yaml</c> with the changes to the valid body.</summary>
  private static string[] Lines(params (string Key, string? Value)[] changes) => Lines("types/integer/t.yaml", Body(changes));

  /// <summary>The diagnostics of a tree copied from a fixture folder, with the families the validator knows.</summary>
  private static SpecValidation ValidateTree(string root) {
    var tree = SpecTree.Load(root, PrototypeFixture.RepositoryRoot);
    return new SpecValidator(SpecFamilies.All).Run(tree, "types");
  }

  [Test]
  public void Family_IsRegistered() {
    var family = SpecFamilies.All.OfType<TypesSpecFamily>().Single();
    Assert.That((family.Folder, family.SchemaPath), Is.EqualTo(("types", "spec/schemas/types.schema.json")));
  }

  [Test]
  public void SeedDocuments_AreTheScalarTypesOfTheIr_AndValid() {
    var validation = ValidateTree(Path.Combine(PrototypeFixture.RepositoryRoot, "spec"));
    Assert.That(validation.Diagnostics, Is.Empty);
    var ids = SpecTree.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec"), PrototypeFixture.RepositoryRoot).Documents
      .Where(document => document.Family == "types").Select(document => document.Id).Order(StringComparer.Ordinal);
    Assert.That(ids, Is.EqualTo(new[] {
      "TYPES.bool", "TYPES.f32", "TYPES.f64", "TYPES.i16", "TYPES.i32", "TYPES.i64", "TYPES.i8", "TYPES.u16", "TYPES.u32", "TYPES.u64", "TYPES.u8"
    }));
  }

  [Test]
  public void SeedU32_IsTheDocumentOfTheIssue() {
    var text = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "types", "integer", "u32.yaml"));
    Assert.That(text, Is.EqualTo(File.ReadAllText(Path.Combine(_fixtures, "valid", "types", "integer", "u32.yaml"))));
    Assert.That(text, Does.Contain("{bits: \"0xFFFFFFFF\", value: \"4294967295\"}"));
  }

  [Test]
  public void ValidTree_HasThreeDocumentsAndNoErrors() {
    var validation = ValidateTree(Path.Combine(_fixtures, "valid"));
    Assert.That((validation.Documents, validation.Diagnostics.Count), Is.EqualTo((3, 0)));
  }

  [Test]
  public void FloatAndMbfOfEqualSize_ListEachOther() {
    var documents = SpecTree.Load(Path.Combine(_fixtures, "valid"), PrototypeFixture.RepositoryRoot).ById;
    var f32 = documents["TYPES.f32"].Body!;
    var mbf32 = documents["TYPES.mbf32"].Body!;
    Assert.That(((long)f32["bits"]!, (long)mbf32["bits"]!, (long)f32["layout"]!["sizeBytes"]!, (long)mbf32["layout"]!["sizeBytes"]!), Is.EqualTo((32L, 32L, 4L, 4L)));
    Assert.That(((string)f32["distinctFrom"]![0]!, (string)mbf32["distinctFrom"]![0]!), Is.EqualTo(("TYPES.mbf32", "TYPES.f32")));
  }

  [Test]
  public void DistinctListedOnOneSideOnly_IsReportedOnTheSideThatLists() {
    using var repository = Repository();
    foreach (var file in Directory.EnumerateFiles(Path.Combine(_fixtures, "valid", "types"), "*.yaml", SearchOption.AllDirectories))
      repository.Document(Path.GetRelativePath(Path.Combine(_fixtures, "valid"), file).Replace(Path.DirectorySeparatorChar, '/'),
        File.ReadAllText(file).Replace("distinctFrom: [TYPES.f32]", "distinctFrom: []", StringComparison.Ordinal));
    Assert.That(repository.Lines("types"), Is.EqualTo(new[] {
      "types/float/f32.yaml(21,18): error PLY0046: TYPES.f32 lists TYPES.mbf32 as distinct, but TYPES.mbf32 does not list TYPES.f32 (rule: distinct-symmetric)"
    }));
  }

  /// <summary>Each invalid fixture with the start of the one diagnostic it gives.</summary>
  private static readonly (string Fixture, string Expected)[] _invalid = [
    ("bits-fit-layout", "(15,9): error PLY0046: bits 32 do not fit layout.sizeBytes 2 (rule: bits-fit-layout)"),
    ("example-width", "(24,13): error PLY0046: values[0].bits 0x1FFFFFFFF needs 33 bits, the type has 32 (rule: example-width)"),
    ("distinct-symmetric", "(22,18): error PLY0046: TYPES.distinct-symmetric lists TYPES.u32 as distinct, but TYPES.u32 does not list TYPES.distinct-symmetric (rule: distinct-symmetric)"),
    ("alignment-power-of-two", "(20,16): error PLY0046: layout.alignment 3 is not a power of two (rule: alignment-power-of-two)"),
    ("family-requirements", "(13,11): error PLY0046: family 'float' requires 'rounding' (rule: family-requirements)"),
    ("layout", "(1,5): error PLY0046: id 'TYPES.misplaced' does not follow from the file name, expected 'TYPES.layout' (rule: layout)"),
    ("unknown-encoding-target", "(17,18): error PLY0043: unknown spec id 'TYPES.i31'"),
    ("extra-property", "(16,15): error PLY0040: /spec/endianness: property 'endianness' is not allowed"),
    ("missing-family", "(13,3): error PLY0040: /spec: Required properties [\"family\"] are not present"),
    ("missing-parameters", "(13,3): error PLY0040: /spec: Required properties [\"parameters\"] are not present"),
    ("missing-bits", "(13,3): error PLY0040: /spec: Required properties [\"bits\"] are not present"),
    ("missing-signedness", "(13,3): error PLY0040: /spec: Required properties [\"signedness\"] are not present"),
    ("missing-layout", "(13,3): error PLY0040: /spec: Required properties [\"layout\"] are not present"),
    ("missing-distinctFrom", "(13,3): error PLY0040: /spec: Required properties [\"distinctFrom\"] are not present"),
    ("missing-values", "(13,3): error PLY0040: /spec: Required properties [\"values\"] are not present"),
    ("missing-layout-sizeBytes", "(19,5): error PLY0040: /spec/layout: Required properties [\"sizeBytes\"] are not present"),
    ("missing-layout-alignment", "(19,5): error PLY0040: /spec/layout: Required properties [\"alignment\"] are not present"),
    ("missing-layout-byteOrder", "(19,5): error PLY0040: /spec/layout: Required properties [\"byteOrder\"] are not present"),
    ("missing-encoding-of", "(17,13): error PLY0040: /spec/encoding: Required properties [\"of\"] are not present"),
    ("missing-encoding-scheme", "(17,13): error PLY0040: /spec/encoding: Required properties [\"scheme\"] are not present"),
    ("missing-parameters-name", "(14,16): error PLY0040: /spec/parameters/0: Required properties [\"name\"] are not present"),
    ("missing-parameters-kind", "(14,16): error PLY0040: /spec/parameters/0: Required properties [\"kind\"] are not present"),
    ("missing-values-bits", "(24,7): error PLY0040: /spec/values/0: Required properties [\"bits\"] are not present"),
    ("missing-values-value", "(24,7): error PLY0040: /spec/values/0: Required properties [\"value\"] are not present"),
  ];

  private static IEnumerable<TestCaseData> InvalidFixtures() => _invalid.Select(pair => new TestCaseData(pair.Fixture, pair.Expected).SetName($"InvalidFixture_{pair.Fixture}"));

  /// <summary>
  /// Puts an invalid fixture where its family puts it, <c>spec/types/&lt;family&gt;/&lt;fixture&gt;.yaml</c>
  /// (<c>integer</c> when it names none), with the valid documents its body refers to.
  /// </summary>
  public static void PlaceInvalidFixture(string fixture, Action<string, string> write) {
    var text = File.ReadAllText(Path.Combine(_fixtures, "invalid", fixture + ".yaml"));
    var body = SpecDocument.Parse("types/x.yaml", text).Body!;
    var family = (string?)body["family"] ?? "integer";
    write($"types/{family}/{fixture}.yaml", text);
    foreach (var file in Directory.EnumerateFiles(Path.Combine(_fixtures, "valid", "types"), "*.yaml", SearchOption.AllDirectories)) {
      var id = "TYPES." + Path.GetFileNameWithoutExtension(file);
      if (body.ToJsonString().Contains('"' + id + '"', StringComparison.Ordinal))
        write(Path.GetRelativePath(Path.Combine(_fixtures, "valid"), file).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(file));
    }
  }

  [TestCaseSource(nameof(InvalidFixtures))]
  public void InvalidFixture_GivesExactlyItsDiagnostic(string fixture, string expected) {
    using var repository = Repository();
    PlaceInvalidFixture(fixture, (path, text) => repository.Document(path, text));
    var lines = repository.Lines("types");
    Assert.That(lines, Has.Length.EqualTo(1));
    Assert.That(lines[0], Does.EndWith(expected).And.Contain($"/{fixture}.yaml("));
  }

  [Test]
  public void EveryInvalidFixture_HasAnExpectation()
    => Assert.That(Directory.EnumerateFiles(Path.Combine(_fixtures, "invalid"), "*.yaml").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal),
      Is.EqualTo(_invalid.Select(pair => pair.Fixture).Order(StringComparer.Ordinal)));

  [Test]
  public void EveryRequiredPropertyOfTheSchema_HasAMissingFixture() {
    var schema = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "types.schema.json")))!;
    var required = new List<string>();
    void Collect(System.Text.Json.Nodes.JsonNode node, string prefix) {
      if (node["required"] is System.Text.Json.Nodes.JsonArray names)
        required.AddRange(names.Select(name => prefix + (string)name!));
      if (node["properties"] is System.Text.Json.Nodes.JsonObject properties)
        foreach (var (name, child) in properties)
          Collect(child!["items"] ?? child, prefix + name + "-");
    }

    Collect(schema, "");
    var fixtures = _invalid.Select(pair => pair.Fixture).ToHashSet(StringComparer.Ordinal);
    Assert.That(required.Where(name => !fixtures.Contains("missing-" + name)), Is.Empty);
  }

  // Equivalence classes: one valid document per family.

  private static IEnumerable<TestCaseData> Families() {
    yield return new TestCaseData("integer", Body()).SetName("Family_integer_IsValid");
    yield return new TestCaseData("float", Body(("family", "float"), ("signedness", "signed"), ("overflow", null), ("rounding", "[nearest-even, toward-zero]"), ("nan", "quiet-and-signaling"), ("subnormals", "true"))).SetName("Family_float_IsValid");
    yield return new TestCaseData("fixed", Body(("family", "fixed"), ("signedness", "signed"), ("overflow", "saturate"), ("rounding", "[toward-negative]"))).SetName("Family_fixed_IsValid");
    yield return new TestCaseData("decimal", Body(("family", "decimal"), ("signedness", "signed"), ("overflow", null), ("rounding", "[nearest-away, toward-positive]"))).SetName("Family_decimal_IsValid");
    yield return new TestCaseData("posit", Body(("family", "posit"), ("signedness", "signed"), ("overflow", null), ("rounding", "[nearest-even]"), ("nan", "quiet-only"))).SetName("Family_posit_IsValid");
    yield return new TestCaseData("mbf", Body(("family", "mbf"), ("signedness", "signed"), ("overflow", null), ("rounding", "[nearest-away]"), ("nan", "none"), ("subnormals", "false"))).SetName("Family_mbf_IsValid");
    yield return new TestCaseData("encoded-integer", Body(("family", "encoded-integer"), ("overflow", "trap"), ("encoding", "{of: TYPES.u32, scheme: gray}"))).SetName("Family_encoded-integer_IsValid");
    yield return new TestCaseData("text", Body(("family", "text"), ("signedness", "none"), ("overflow", null))).SetName("Family_text_IsValid");
    yield return new TestCaseData("boolean", Body(("family", "boolean"), ("bits", "1"), ("signedness", "none"), ("overflow", null), ("values", "[{bits: \"0x1\", value: \"1\"}]"))).SetName("Family_boolean_IsValid");
    yield return new TestCaseData("vector", Body(("family", "vector"), ("bits", "128"), ("overflow", "source-defined"), ("lanes", "4"), ("layout", "{sizeBytes: 16, alignment: 16, byteOrder: little}"))).SetName("Family_vector_IsValid");
    yield return new TestCaseData("custom", Body(("family", "custom"), ("signedness", "none"), ("layout", "{sizeBytes: 4, alignment: 4, byteOrder: big}"))).SetName("Family_custom_IsValid");
  }

  [TestCaseSource(nameof(Families))]
  public void Family_IsValid(string family, string body)
    => Assert.That(Lines($"types/{family}/t.yaml", body, ("types/integer/u32.yaml", Document("TYPES.u32", Body()))), Is.Empty);

  [Test]
  public void UnknownFamily_IsASchemaViolation() => Assert.That(Lines(("family", "rational")).Single(), Does.Contain("error PLY0040: /spec/family:"));

  [TestCase("signedness", "both")]
  [TestCase("overflow", "clamp")]
  [TestCase("layout", "{sizeBytes: 4, alignment: 4, byteOrder: middle}")]
  [TestCase("rounding", "[stochastic]")]
  [TestCase("encoding", "{of: TYPES.u32, scheme: base64}")]
  public void UnknownEnumerationValue_IsASchemaViolation(string key, string value)
    => Assert.That(Lines(("family", key == "encoding" ? "encoded-integer" : "integer"), (key, value)).Single(), Does.Contain("error PLY0040: /spec/" + key));

  [Test]
  public void UnknownNanValue_IsASchemaViolation()
    => Assert.That(Lines(("family", "float"), ("signedness", "signed"), ("rounding", "[nearest-even]"), ("nan", "loud"), ("subnormals", "true")).Single(), Does.Contain("error PLY0040: /spec/nan"));

  [TestCase("zigzag")]
  [TestCase("gray")]
  [TestCase("bcd")]
  [TestCase("leb128")]
  [TestCase("ones-complement")]
  [TestCase("sign-magnitude")]
  public void EncodingScheme_IsValid(string scheme)
    => Assert.That(Lines("types/encoded-integer/t.yaml", Body(("family", "encoded-integer"), ("encoding", $"{{of: TYPES.u32, scheme: {scheme}}}")), ("types/integer/u32.yaml", Document("TYPES.u32", Body()))), Is.Empty);

  [TestCase("wrap")]
  [TestCase("trap")]
  [TestCase("saturate")]
  [TestCase("source-defined")]
  public void Overflow_IsValid(string overflow) => Assert.That(Lines(("overflow", overflow)), Is.Empty);

  [TestCase("signed")]
  [TestCase("unsigned")]
  public void Signedness_IsValid(string signedness) => Assert.That(Lines(("signedness", signedness)), Is.Empty);

  [TestCase("little")]
  [TestCase("big")]
  [TestCase("target")]
  public void ByteOrder_IsValid(string byteOrder) => Assert.That(Lines(("layout", $"{{sizeBytes: 4, alignment: 4, byteOrder: {byteOrder}}}")), Is.Empty);

  [Test]
  public void ParameterizedType_IsValid()
    => Assert.That(Lines(("parameters", "[{name: N, kind: integer, min: 1, max: 4096}, {name: Mode, kind: enum}, {name: T, kind: type}]")), Is.Empty);

  // Boundaries.

  [TestCase("0", "/spec/bits")]
  [TestCase("65537", "/spec/bits")]
  public void BitsOutOfRange_IsASchemaViolation(string bits, string pointer) => Assert.That(Lines(("bits", bits)).Single(), Does.Contain("error PLY0040: " + pointer));

  [TestCase("1")]
  [TestCase("65536")]
  public void BitsAtTheLimits_AreValid(string bits)
    => Assert.That(Lines(("bits", bits), ("layout", "{sizeBytes: 8192, alignment: 1, byteOrder: target}"), ("values", "[{bits: \"0x1\", value: \"1\"}]")), Is.Empty);

  [Test]
  public void BitsWrittenAsAWholeReal_AreValid() => Assert.That(Lines(("bits", "32.0")), Is.Empty);

  [TestCase("31")]
  [TestCase("32")]
  public void BitsWithinTheLayout_AreValid(string bits) => Assert.That(Lines(("bits", bits), ("values", "[{bits: \"0x1\", value: \"1\"}]")), Is.Empty);

  [Test]
  public void BitsOneAboveTheLayout_BreakBitsFitLayout()
    => Assert.That(Lines(("bits", "33")), Is.EqualTo(new[] { "types/integer/t.yaml(15,9): error PLY0046: bits 33 do not fit layout.sizeBytes 4 (rule: bits-fit-layout)" }));

  [TestCase("0xFFFFFFFF")]
  [TestCase("0x00000000FFFFFFFF")]
  [TestCase("0x0")]
  public void ExampleWithinTheWidth_IsValid(string literal) => Assert.That(Lines(("values", $"[{{bits: \"{literal}\", value: \"1\"}}]")), Is.Empty);

  [Test]
  public void ExampleOneBitTooWide_BreaksExampleWidth()
    => Assert.That(Lines(("values", "[{bits: \"0x0\", value: \"0\"}, {bits: \"0x100000000\", value: \"1\"}]")).Single(),
      Does.EndWith("error PLY0046: values[1].bits 0x100000000 needs 33 bits, the type has 32 (rule: example-width)"));

  [TestCase("1", false)]
  [TestCase("2", true)]
  public void LanesOnAVector(string lanes, bool valid) {
    var lines = Lines("types/vector/t.yaml", Body(("family", "vector"), ("lanes", lanes)));
    if (valid)
      Assert.That(lines, Is.Empty);
    else
      Assert.That(lines.Single(), Does.Contain("error PLY0040: /spec/lanes"));
  }

  [Test]
  public void VectorWithoutLanes_BreaksFamilyRequirements()
    => Assert.That(Lines("types/vector/t.yaml", Body(("family", "vector"))).Single(), Does.EndWith("error PLY0046: family 'vector' requires 'lanes' (rule: family-requirements)"));

  [Test]
  public void LanesOnANonVector_BreakFamilyRequirements()
    => Assert.That(Lines(("lanes", "2")), Is.EqualTo(new[] { "types/integer/t.yaml(21,10): error PLY0046: family 'integer' excludes 'lanes' (rule: family-requirements)" }));

  [Test]
  public void EncodingOnAnInteger_BreaksFamilyRequirements()
    => Assert.That(Lines("types/integer/t.yaml", Body(("encoding", "{of: TYPES.t, scheme: bcd}"))).Single(), Does.EndWith("error PLY0046: family 'integer' excludes 'encoding' (rule: family-requirements)"));

  [Test]
  public void EncodedIntegerWithoutEncoding_BreaksFamilyRequirements()
    => Assert.That(Lines("types/encoded-integer/t.yaml", Body(("family", "encoded-integer"))).Single(), Does.EndWith("error PLY0046: family 'encoded-integer' requires 'encoding' (rule: family-requirements)"));

  [Test]
  public void IntegerWithoutOverflow_BreaksFamilyRequirements()
    => Assert.That(Lines(("overflow", null)).Single(), Does.EndWith("error PLY0046: family 'integer' requires 'overflow' (rule: family-requirements)"));

  [Test]
  public void FloatWithoutNanAndSubnormals_ReportsBoth()
    => Assert.That(Lines("types/float/t.yaml", Body(("family", "float"), ("signedness", "signed"), ("rounding", "[nearest-even]"))).Select(line => line[line.IndexOf("PLY", StringComparison.Ordinal)..]), Is.EqualTo(new[] {
      "PLY0046: family 'float' requires 'nan' (rule: family-requirements)",
      "PLY0046: family 'float' requires 'subnormals' (rule: family-requirements)",
    }));

  [Test]
  public void SignednessNoneOnAnInteger_BreaksFamilyRequirements()
    => Assert.That(Lines(("signedness", "none")), Is.EqualTo(new[] { "types/integer/t.yaml(16,15): error PLY0046: family 'integer' does not allow signedness 'none' (rule: family-requirements)" }));

  [Test]
  public void EmptyRoundingList_IsASchemaViolation() => Assert.That(Lines(("rounding", "[]")).Single(), Does.Contain("error PLY0040: /spec/rounding"));

  [Test]
  public void EmptyValues_IsASchemaViolation() => Assert.That(Lines(("values", "[]")).Single(), Does.Contain("error PLY0040: /spec/values"));

  [Test]
  public void OneValue_IsValid() => Assert.That(Lines(), Is.Empty);

  [TestCase("NaN")]
  [TestCase("+Infinity")]
  [TestCase("-Infinity")]
  [TestCase("-0")]
  [TestCase("1.5e-3")]
  public void SpecialValue_IsValid(string value) => Assert.That(Lines(("values", $"[{{bits: \"0x1\", value: \"{value}\"}}]")), Is.Empty);

  [TestCase("bits", "FF")]
  [TestCase("value", "Infinity")]
  public void MalformedExample_IsASchemaViolation(string key, string text) {
    var example = key == "bits" ? $"{{bits: \"{text}\", value: \"1\"}}" : $"{{bits: \"0x1\", value: \"{text}\"}}";
    Assert.That(Lines(("values", $"[{example}]")).Single(), Does.Contain($"error PLY0040: /spec/values/0/{key}"));
  }

  [TestCase("0")]
  [TestCase("65")]
  public void AlignmentOutOfRange_IsASchemaViolation(string alignment)
    => Assert.That(Lines(("layout", $"{{sizeBytes: 4, alignment: {alignment}, byteOrder: target}}")).Single(), Does.Contain("error PLY0040: /spec/layout/alignment"));

  [TestCase("1")]
  [TestCase("4")]
  [TestCase("64")]
  public void AlignmentPowerOfTwo_IsValid(string alignment) => Assert.That(Lines(("layout", $"{{sizeBytes: 4, alignment: {alignment}, byteOrder: target}}")), Is.Empty);

  [TestCase("3")]
  [TestCase("48")]
  public void AlignmentNotAPowerOfTwo_BreaksTheRule(string alignment)
    => Assert.That(Lines(("layout", $"{{sizeBytes: 4, alignment: {alignment}, byteOrder: target}}")).Single(),
      Does.EndWith($"error PLY0046: layout.alignment {alignment} is not a power of two (rule: alignment-power-of-two)"));

  // Layout of the files.

  [Test]
  public void DocumentDirectlyUnderTypes_BreaksLayout()
    => Assert.That(Lines("types/u32.yaml", Body()), Is.EqualTo(new[] { "types/u32.yaml(13,11): error PLY0046: spec/types/u32.yaml declares family 'integer', expected folder spec/types/integer (rule: layout)" }));

  [Test]
  public void DocumentInTheFolderOfAnotherFamily_BreaksLayout()
    => Assert.That(Lines("types/float/u32.yaml", Body()), Is.EqualTo(new[] { "types/float/u32.yaml(13,11): error PLY0046: spec/types/float/u32.yaml declares family 'integer', expected folder spec/types/integer (rule: layout)" }));

  [Test]
  public void DocumentOneFolderTooDeep_BreaksLayout()
    => Assert.That(Lines("types/integer/wide/u32.yaml", Body()).Single(), Does.EndWith("error PLY0046: spec/types/integer/wide/u32.yaml declares family 'integer', expected folder spec/types/integer (rule: layout)"));

  [Test]
  public void IdThatDoesNotFollowFromTheFileName_BreaksLayout() {
    using var repository = Repository();
    repository.Document("types/integer/u32.yaml", Document("TYPES.uint", Body()));
    Assert.That(repository.Lines("types"), Is.EqualTo(new[] { "types/integer/u32.yaml(1,5): error PLY0046: id 'TYPES.uint' does not follow from the file name, expected 'TYPES.u32' (rule: layout)" }));
  }

  // References.

  [Test]
  public void DistinctFromAnUnknownType_IsPLY0043()
    => Assert.That(Lines(("distinctFrom", "[TYPES.nothing]")), Is.EqualTo(new[] { "types/integer/t.yaml(19,18): error PLY0043: unknown spec id 'TYPES.nothing'" }));

  [Test]
  public void DistinctFromAnotherFamilyId_IsASchemaViolation() => Assert.That(Lines(("distinctFrom", "[DEMO.widget]")).Single(), Does.Contain("error PLY0040: /spec/distinctFrom/0"));

  [Test]
  public void DistinctFromEachOther_IsValid()
    => Assert.That(Lines("types/integer/t.yaml", Body(("distinctFrom", "[TYPES.s]")), ("types/integer/s.yaml", Document("TYPES.s", Body(("distinctFrom", "[TYPES.t]"))))), Is.Empty);

  [Test]
  public void DistinctFromADocumentWithABrokenBody_LeavesTheReportToThatDocument() {
    var lines = Lines("types/integer/t.yaml", Body(("distinctFrom", "[TYPES.s]")), ("types/integer/s.yaml", Document("TYPES.s", Body(("distinctFrom", null)))));
    Assert.That(lines.Single(), Does.StartWith("types/integer/s.yaml(13,3): error PLY0040: /spec: Required properties [\"distinctFrom\"]"));
  }

  [Test]
  public void DistinctFromADocumentWithoutBody_LeavesTheReportToThatDocument() {
    using var repository = Repository();
    repository.Document("types/integer/t.yaml", Document("TYPES.t", Body(("distinctFrom", "[TYPES.s]"))));
    repository.Document("types/integer/s.yaml", Document("TYPES.s", "").Replace("spec:\n", "", StringComparison.Ordinal));
    Assert.That(repository.Lines("types").Single(), Does.StartWith("types/integer/s.yaml(1,1): error PLY0040"));
  }

  [Test]
  public void DistinctListOfOtherWithForeignEntries_DoesNotCountThem()
    => Assert.That(Lines("types/integer/t.yaml", Body(("distinctFrom", "[TYPES.s]")), ("types/integer/s.yaml", Document("TYPES.s", Body(("distinctFrom", "[1, TYPES.x]"))))).Count(line => line.Contains("(rule: distinct-symmetric)", StringComparison.Ordinal)), Is.EqualTo(1));

  [Test]
  public void EncodingOfAnUnknownType_IsPLY0043()
    => Assert.That(Lines("types/encoded-integer/t.yaml", Body(("family", "encoded-integer"), ("encoding", "{of: TYPES.i31, scheme: zigzag}"))).Single(), Does.EndWith("error PLY0043: unknown spec id 'TYPES.i31'"));

  [Test]
  public void UnknownBodyProperty_IsASchemaViolation() => Assert.That(Lines(("endianness", "little")).Single(), Does.Contain("error PLY0040: /spec/endianness"));

}
