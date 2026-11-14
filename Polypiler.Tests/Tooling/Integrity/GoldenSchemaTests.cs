using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Integrity;

namespace Polypiler.Tests.Tooling.Integrity;

/// <summary>
/// <c>spec/schemas/vectors.schema.json</c>, <c>golden.lock.schema.json</c> and
/// <c>golden.provenance.schema.json</c> checked by JsonSchema.Net, which shares no code with the
/// loaders: the schemas against the meta-schema, the repository's golden files against them, and the
/// malformed files the loader refuses refused by the schema too.
/// </summary>
[TestFixture]
public sealed class GoldenSchemaTests {

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private static readonly Dictionary<string, JsonSchema> _schemas = new[] { "vectors.schema.json", "golden.lock.schema.json", "golden.provenance.schema.json" }
    .ToDictionary(name => name, name => JsonSchema.FromText(File.ReadAllText(Path.Combine(_root, "spec", "schemas", name))));

  private static JsonSchema Schema(string name) => _schemas[name];

  private static bool Valid(string schema, string text) {
    using var document = JsonDocument.Parse(text);
    return Schema(schema).Evaluate(document.RootElement).IsValid;
  }

  [TestCase("vectors.schema.json")]
  [TestCase("golden.lock.schema.json")]
  [TestCase("golden.provenance.schema.json")]
  public void Schema_IsValidAgainstTheMetaSchema(string name) {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "spec", "schemas", name)));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  [Test]
  public void RepositoryGoldenFiles_ConformToTheSchemas() {
    var coverage = GoldenCoverage.Of(_root);
    Assert.That(Valid("golden.lock.schema.json", File.ReadAllText(Path.Combine(_root, GoldenLock.RepositoryPath))), Is.True);
    var vectors = coverage.Files.Where(IntegrityChecker.IsVectorFile).ToList();
    Assert.That(vectors, Is.Not.Empty);
    foreach (var file in vectors)
      Assert.That(Valid("vectors.schema.json", File.ReadAllText(Path.Combine(_root, file))), Is.True, file);
    Assert.That(coverage.Markers, Has.Count.EqualTo(2));
    foreach (var marker in coverage.Markers)
      Assert.That(Valid("golden.provenance.schema.json", File.ReadAllText(Path.Combine(_root, marker.Path))), Is.True, marker.Path);
  }

  [TestCase("published.json")]
  [TestCase("upstream-embedded.json")]
  [TestCase("kinds.json")]
  public void Fixtures_ConformToTheVectorSchema(string fixture) => Assert.That(Valid("vectors.schema.json", GoldenTree.Fixture(fixture)), Is.True);

  [TestCase("\"024d0127\"", "\"024\"")]
  [TestCase("\"kind\": \"reference-run\"", "\"kind\": \"guess\"")]
  [TestCase("\"schema\": 1", "\"schema\": 2")]
  [TestCase("{ \"text\": \"abc\" }, \"expected\"", "{ \"value\": 1 }, \"expected\"")]
  [TestCase("{ \"value\": 38600999 }", "{ \"args\": [] }")]
  public void MalformedVectors_AreRefusedByTheSchemaAndTheLoader(string original, string replacement) {
    var text = GoldenTree.Fixture("kinds.json").Replace(original, replacement, StringComparison.Ordinal);
    Assert.That(Valid("vectors.schema.json", text), Is.False);
    Assert.That(() => VectorFile.Parse(text, "v"), Throws.InstanceOf<IntegrityException>());
  }

  [TestCase("polypiler")]
  [TestCase("Polypiler-Tooling")]
  public void SelfGeneratedVectors_AreRefusedByTheSchemaAndTheGate(string generator) {
    var text = GoldenTree.Fixture("kinds.json").Replace("\"generatedBy\": \"python\"", $"\"generatedBy\": \"{generator}\"", StringComparison.Ordinal);
    Assert.That(Valid("vectors.schema.json", text), Is.False);
    Assert.That(new IntegrityChecker(_root).Independence(VectorFile.Parse(text, "v").Provenance), Is.Not.Empty);
  }

  /// <summary>A language runner's file with a signature and a trap vector conforms; one with both expected values, or with half of the call, does not.</summary>
  [TestCase("u32-identity.json", true)]
  [TestCase("invalid/hex-and-trap.json", false)]
  [TestCase("invalid/missing-signature.json", false)]
  public void RunnerFiles_AreCheckedByTheSchema(string file, bool valid) {
    var text = File.ReadAllText(Path.Combine(_root, "tests", "fixtures", "verify", "images", file));
    Assert.That(Valid("vectors.schema.json", text), Is.EqualTo(valid));
    if (valid)
      Assert.That(VectorFile.Parse(text, file).Find("max widened by a width-less number")!.Expected.Kind, Is.EqualTo(VectorValueKind.Trap));
  }

  [Test]
  public void EmptyVectorList_IsRefusedByTheSchema()
    => Assert.That(Valid("vectors.schema.json", "{\"schema\":1,\"fixture\":\"f\",\"provenance\":{\"kind\":\"published\",\"source\":\"s\",\"detail\":\"d\",\"generatedBy\":\"hand\"},\"vectors\":[]}"), Is.False);

}
