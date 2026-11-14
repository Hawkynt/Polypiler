using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Tests.Verification.Oracles.Support;

namespace Polypiler.Tests.Verification.Oracles;

/// <summary>The <c>*.vectors.json</c> format, schema 1, and its loader.</summary>
[TestFixture]
public sealed class VectorFileTests {

  private const string Head = """
    "schema": 1, "fixture": "f",
    "provenance": { "kind": "published", "source": "s", "detail": "d", "generatedBy": "hand" },
    """;

  private static VectorFile Parse(string exactness, string vectors = "[]")
    => VectorFile.Parse($$"""{ {{Head}} "exactness": {{exactness}}, "vectors": {{vectors}} }""", "t.vectors.json");

  private static string Problem(string json)
    => Assert.Throws<FormatException>(() => VectorFile.Parse(json, "t.vectors.json"))!.Message;

  [Test]
  public void TheAdlerVectors_Load() {
    var file = VectorFile.Load(OracleTesting.AdlerVectors);
    Assert.That((file.Fixture, file.Provenance.Kind, file.Provenance.Source, file.Provenance.GeneratedBy, file.Provenance.OracleKind), Is.EqualTo(("cipher.checksum.adler32", "published", "RFC 1950", "hand", OracleKind.PublishedVectors)));
    Assert.That(file.Provenance.Detail, Does.Contain("b3a8a8e92cbca83475986391c3cac56f0b185d29"));
    Assert.That(file.Exactness, Is.EqualTo(new ExactnessPolicy.Exact()));
    Assert.That(file.Path, Is.EqualTo(OracleTesting.AdlerVectors));
    Assert.That(file.Vectors, Has.Count.EqualTo(4));
    Assert.That(file.Vectors[0].Input, Is.Empty, "test design 3: the empty input");
    Assert.That(file.Vectors[3].Input, Has.Length.EqualTo(300).And.All.EqualTo(0xff));
    Assert.That(Hex.Format(file.Vectors[2].Expected!), Is.EqualTo("024d0127"));
  }

  /// <summary>Test design 4: an empty vector list loads; whether it verifies anything is the report's question.</summary>
  [Test]
  public void AnEmptyVectorList_Loads() => Assert.That(Parse("""{ "mode": "exact" }""").Vectors, Is.Empty);

  [Test]
  public void Hex_IsEitherCase_TextIsUtf8_AndExpectedIsOptional() {
    var file = Parse("""{ "mode": "exact" }""", """[ { "name": "x", "input": { "hex": "aBcD" }, "expected": { "hex": "FF" } }, { "name": "y", "input": { "text": "a\u00e9" } } ]""");
    Assert.That(file.Vectors[0].Input, Is.EqualTo(new byte[] { 0xab, 0xcd }));
    Assert.That(file.Vectors[0].Expected, Is.EqualTo(new byte[] { 0xff }));
    Assert.That(file.Vectors[1].Input, Is.EqualTo(new byte[] { 0x61, 0xc3, 0xa9 }));
    Assert.That(file.Vectors[1].Expected, Is.Null);
  }

  /// <summary>The file of the integrity gate, without an exactness block, compares exactly.</summary>
  [Test]
  public void AFileWithoutExactness_IsExact() {
    var file = VectorFile.Parse($$"""{ {{Head}} "vectors": [ { "name": "abc", "input": { "text": "abc" }, "expected": { "hex": "024D0127" } } ] }""", "x");
    Assert.That((file.Exactness, Hex.Format(file.Vectors[0].Expected!)), Is.EqualTo((new ExactnessPolicy.Exact(), "024d0127")));
  }

  [TestCase("published", OracleKind.PublishedVectors)]
  [TestCase("upstream-embedded", OracleKind.PublishedVectors)]
  [TestCase("reference-run", OracleKind.SourceToolchain)]
  public void TheProvenanceKind_NamesTheOracleKind(string kind, OracleKind expected) {
    var file = VectorFile.Parse($$"""{ "schema": 1, "fixture": "f", "provenance": { "kind": "{{kind}}", "source": "s", "detail": "d", "generatedBy": "g" }, "vectors": [] }""", "x");
    Assert.That(file.Provenance.OracleKind, Is.EqualTo(expected));
  }

  /// <summary>Test design 22: a declared and a derived bound both load, with their special-value rule.</summary>
  [Test]
  public void Tolerances_Load() {
    Assert.That(Parse("""{ "mode": "ulp", "bound": 3, "format": "f64", "policy": "VERIFICATION.tolerance.sin" }""").Exactness,
      Is.EqualTo(new ExactnessPolicy.Ulp(3, FloatFormat.F64, policy: "VERIFICATION.tolerance.sin")));
    Assert.That(Parse("""{ "mode": "relative", "bound": 1e-12, "format": "f64", "specialValues": "class", "reason": "sum" }""").Exactness,
      Is.EqualTo(new ExactnessPolicy.Relative(1e-12, FloatFormat.F64, reason: "sum") { SpecialValues = SpecialValues.Class }));
    Assert.That(Parse("""{ "mode": "absolute", "bound": 0.25, "format": "f32", "reason": "r" }""").Exactness,
      Is.EqualTo(new ExactnessPolicy.Absolute(0.25, FloatFormat.F32, reason: "r")));
    Assert.That(Parse("""{ "mode": "exact", "specialValues": "bitwise" }""").Exactness.SpecialValues, Is.EqualTo(SpecialValues.Bitwise));
    Assert.That(Parse("""{ "mode": "exact", "specialValues": "signed-zero" }""").Exactness.SpecialValues, Is.EqualTo(SpecialValues.SignedZero));
  }

  /// <summary>Test design 21: an exactness not in the declared shape names the property.</summary>
  [TestCase("\"bit-exact\"", "'exactness' must be an object")]
  [TestCase("""{ "mode": "ulp", "bound": 1, "format": "f64" }""", "'exactness.policy'")]
  [TestCase("""{ "mode": "ulp", "bound": 1, "format": "f64", "policy": "p", "reason": "r" }""", "'exactness.reason'")]
  [TestCase("""{ "mode": "ulp", "bound": 0, "format": "f64", "reason": "r" }""", "'exactness.bound' must be a positive number")]
  [TestCase("""{ "mode": "ulp", "bound": 1.5, "format": "f64", "reason": "r" }""", "'exactness.bound' must be a positive integer")]
  [TestCase("""{ "mode": "ulp", "bound": 3000000000, "format": "f64", "reason": "r" }""", "'exactness.bound' must be a positive integer")]
  [TestCase("""{ "mode": "relative", "bound": "1", "format": "f64", "reason": "r" }""", "'exactness.bound' must be a positive number")]
  [TestCase("""{ "mode": "absolute", "bound": -1, "format": "f64", "reason": "r" }""", "'exactness.bound' must be a positive number")]
  [TestCase("""{ "mode": "absolute", "format": "f64", "reason": "r" }""", "'exactness.bound' is missing")]
  [TestCase("""{ "mode": "exact", "bound": 1 }""", "'exactness.bound' is not a property")]
  [TestCase("""{ "mode": "ulp", "bound": 1, "format": "f16", "reason": "r" }""", "'exactness.format'")]
  [TestCase("""{ "mode": "fuzzy" }""", "'exactness.mode'")]
  [TestCase("""{ "mode": "exact", "specialValues": "none" }""", "'exactness.specialValues'")]
  [TestCase("""{ "mode": "exact", "specialValues": 1 }""", "'exactness.specialValues'")]
  [TestCase("""{ }""", "'exactness.mode' is missing")]
  [TestCase("""{ "mode": "ulp", "bound": 1, "format": "f64", "reason": "r", "slack": 2 }""", "'exactness.slack' is not a property")]
  public void AMalformedExactness_NamesTheProperty(string exactness, string message)
    => Assert.That(Assert.Throws<FormatException>(() => Parse(exactness))!.Message, Does.StartWith("t.vectors.json: ").And.Contain(message));

  /// <summary>Test design 19: malformed vectors and a missing provenance name the field.</summary>
  [TestCase("""[ { "name": "x", "input": { "hex": "abc" } } ]""", "'vectors[0].input.hex'")]
  [TestCase("""[ { "name": "x", "input": { "hex": "zz" } } ]""", "'vectors[0].input.hex'")]
  [TestCase("""[ { "name": "x", "input": { "hex": "ab cd" } } ]""", "'vectors[0].input.hex'")]
  [TestCase("""[ { "name": "x", "input": { "hex": 12 } } ]""", "'vectors[0].input.hex'")]
  [TestCase("""[ { "name": "x", "input": { "text": 12 } } ]""", "'vectors[0].input.text' must be a string")]
  [TestCase("""[ { "name": "x", "input": { "hex": "" }, "expected": { "hex": "0" } } ]""", "'vectors[0].expected.hex'")]
  [TestCase("""[ { "name": "x", "input": "" } ]""", "'vectors[0].input' must be an object")]
  [TestCase("""[ { "name": "x", "input": { } } ]""", "'vectors[0].input' must hold exactly one of 'hex' and 'text'")]
  [TestCase("""[ { "name": "x", "input": { "hex": "", "text": "" } } ]""", "'vectors[0].input' must hold exactly one of 'hex' and 'text'")]
  [TestCase("""[ { "name": "x", "input": { "args": [1] } } ]""", "'vectors[0].input.args' is a typed value")]
  [TestCase("""[ { "name": "x", "input": { "hex": "" }, "expected": { "value": 1 } } ]""", "'vectors[0].expected.value' is a typed value")]
  [TestCase("""[ { "name": "x", "input": { "bytes": "" } } ]""", "'vectors[0].input.bytes' is not a property")]
  [TestCase("""[ { "name": "x" } ]""", "'vectors[0].input' is missing")]
  [TestCase("""[ { "input": { "hex": "" } } ]""", "'vectors[0].name' is missing")]
  [TestCase("""[ { "name": "", "input": { "hex": "" } } ]""", "'vectors[0].name' must be a non-empty string")]
  [TestCase("""[ { "name": "x", "input": { "hex": "" } }, { "name": "x", "input": { "hex": "" } } ]""", "'vectors[1].name' 'x' is not unique")]
  [TestCase("""[ { "name": "x", "input": { "hex": "" }, "output": { "hex": "" } } ]""", "'vectors[0].output' is not a property")]
  [TestCase("""[ 1 ]""", "'vectors[0]' must be an object")]
  [TestCase("""{ }""", "'vectors' must be an array")]
  public void AMalformedVector_NamesTheField(string vectors, string message)
    => Assert.That(Assert.Throws<FormatException>(() => Parse("""{ "mode": "exact" }""", vectors))!.Message, Does.Contain(message));

  [TestCase("""{ "schema": 1, "fixture": "f", "exactness": { "mode": "exact" }, "vectors": [] }""", "'provenance' is missing")]
  [TestCase("""{ "schema": 1, "fixture": "f", "provenance": { "kind": "guess", "source": "s", "detail": "d", "generatedBy": "g" }, "exactness": { "mode": "exact" }, "vectors": [] }""", "'provenance.kind' 'guess' is not one of")]
  [TestCase("""{ "schema": 1, "fixture": "f", "provenance": { "kind": "published", "source": "s", "detail": "d" }, "exactness": { "mode": "exact" }, "vectors": [] }""", "'provenance.generatedBy' is missing")]
  [TestCase("""{ "schema": 1, "fixture": "f", "provenance": { "kind": "published", "source": "s", "detail": "d", "generatedBy": "g", "date": "x" }, "exactness": { "mode": "exact" }, "vectors": [] }""", "'provenance.date' is not a property")]
  [TestCase("""{ "schema": 1, "fixture": "f", "provenance": [], "exactness": { "mode": "exact" }, "vectors": [] }""", "'provenance' must be an object")]
  [TestCase("""{ "schema": 2, "fixture": "f" }""", "'schema' must be 1")]
  [TestCase("""{ "schema": "1", "fixture": "f" }""", "'schema' must be 1")]
  [TestCase("""{ "schema": 1.5, "fixture": "f" }""", "'schema' must be 1")]
  [TestCase("""{ "fixture": "f" }""", "'schema' is missing")]
  [TestCase("""{ "schema": 1, "fixture": 3 }""", "'fixture' must be a non-empty string")]
  [TestCase("""{ "schema": 1, "fixture": "f", "comment": "" }""", "'comment' is not a property")]
  [TestCase("""[]""", "'$' must be an object")]
  [TestCase("""{ "schema": 1, """, "not valid JSON")]
  public void AMalformedFile_NamesTheProperty(string json, string message) => Assert.That(Problem(json), Does.Contain(message));

  [Test]
  public void Load_AMissingFile_Throws()
    => Assert.That(() => VectorFile.Load(Path.Combine(OracleTesting.FixtureFolder, "absent.vectors.json")), Throws.InstanceOf<IOException>());

  [Test]
  public void Hex_TryParse_RefusesOddLengthAndSeparators() {
    Assert.That(Hex.TryParse(""), Is.Empty);
    Assert.That(Hex.TryParse("0"), Is.Null);
    Assert.That(Hex.TryParse("0g"), Is.Null);
    Assert.That(Hex.Format([0xAB, 0x01]), Is.EqualTo("ab01"));
  }

}
