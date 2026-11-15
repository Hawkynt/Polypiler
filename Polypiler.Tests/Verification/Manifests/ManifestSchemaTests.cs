using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Verification.Manifests;

/// <summary>
/// <c>spec/schemas/manifest.schema.json</c> on its own, evaluated by JsonSchema.Net without the C#
/// model, and <see cref="ManifestSchema"/>, which adds the sub-schemas of the sections.
/// </summary>
[TestFixture]
public sealed class ManifestSchemaTests {

  private static readonly string _schemaFile = Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", ManifestSchema.MainFile);

  private static readonly Lazy<JsonSchema> _raw = new(() => JsonSchema.FromText(File.ReadAllText(_schemaFile), new BuildOptions { SchemaRegistry = new SchemaRegistry() }));

  private string _directory = "";

  [SetUp]
  public void CreateDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-manifest-schema-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteDirectory() => Directory.Delete(this._directory, true);

  private static bool RawValid(string text) => _raw.Value.Evaluate(JsonDocument.Parse(text).RootElement).IsValid;

  [TestCase("generated.json", true)]
  [TestCase("built.json", true)]
  [TestCase("executed.json", true)]
  [TestCase("verified.json", true)]
  [TestCase("unknown-state.json", false)]
  [TestCase("schema-version-2.json", false)]
  // The state rule is no business of the schema: these fail the validator, not the schema.
  [TestCase("verified-without-results.json", true)]
  [TestCase("verified-with-failure.json", true)]
  [TestCase("built-without-build.json", true)]
  public void TheSchemaAlone_JudgesTheFixtures(string fixture, bool valid)
    => Assert.That(RawValid(ManifestFixtures.Text(fixture)), Is.EqualTo(valid));

  /// <summary>PRD §10.2, item by item, against the properties of the schema.</summary>
  [TestCase("compiler and IR versions", "compiler")]
  [TestCase("requested CLI/config", "command")]
  [TestCase("input hashes and provenance", "inputs")]
  [TestCase("frontend profile", "frontend")]
  [TestCase("outputs", "outputs")]
  [TestCase("API", "api")]
  [TestCase("target/runtime/platform profile", "target")]
  [TestCase("intrinsics", "intrinsics")]
  [TestCase("helpers", "helpers")]
  [TestCase("native dependencies", "nativeDependencies")]
  [TestCase("licensing and model/weight restrictions", "licensing")]
  [TestCase("source semantic caveats", "caveats")]
  [TestCase("optimization passes/proofs", "optimization")]
  [TestCase("warnings", "warnings")]
  [TestCase("toolchain versions", "toolchains")]
  [TestCase("exact verification results", "verification")]
  [TestCase("benchmarks when measured", "benchmarks")]
  [TestCase("build/run commands", "commands")]
  [TestCase("generated/built/executed/verified states", "state")]
  public void EveryItemOfPrd10_2_IsAProperty(string item, string property) {
    var schema = JsonNode.Parse(File.ReadAllText(_schemaFile))!;
    Assert.That(schema["properties"]!.AsObject().ContainsKey(property), Is.True, item);
  }

  [Test]
  public void TheDetails_OfPrd10_2_AreRequired() {
    var schema = JsonNode.Parse(File.ReadAllText(_schemaFile))!["properties"]!;
    Assert.That(new[] {
      schema["compiler"]!["required"]!.AsArray().Select(item => item!.GetValue<string>()).Contains("irVersion"),
      schema["inputs"]!["items"]!["required"]!.AsArray().Select(item => item!.GetValue<string>()).SequenceEqual(["path", "sha256", "provenance"]),
      schema["licensing"]!["required"]!.AsArray().Select(item => item!.GetValue<string>()).Contains("modelRestrictions"),
      schema["state"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>()).SequenceEqual(["generated", "built", "executed", "verified"])
    }, Is.All.True);
  }

  [Test]
  public void TheDefaultSchema_IsTheRepositorysOne()
    => Assert.That(ManifestSchema.Default.Evaluate(JsonDocument.Parse(ManifestFixtures.Text("verified.json")).RootElement), Is.Empty);

  [Test]
  public void WithoutSubSchemas_EverySectionIsCheckedForItsTypeOnly() {
    File.Copy(_schemaFile, Path.Combine(this._directory, ManifestSchema.MainFile));
    var schema = ManifestSchema.Load(this._directory);
    var tree = ManifestFixtures.Tree("generated.json");
    tree["assumptions"] = new JsonArray(new JsonObject { ["anything"] = 1 });
    Assert.That((schema.SectionsWithSchema, schema.Evaluate(JsonSerializer.SerializeToElement(tree))), Is.EqualTo((Array.Empty<string>(), Array.Empty<(string, string)>())));
  }

  [Test]
  public void ASubSchema_IsApplied_OnceItsFileExists() {
    File.Copy(_schemaFile, Path.Combine(this._directory, ManifestSchema.MainFile));
    File.WriteAllText(Path.Combine(this._directory, ManifestSchema.SectionFile("assumptions")),
      """{ "type": "array", "items": { "type": "object", "required": ["target"] } }""");
    var schema = ManifestSchema.Load(this._directory);
    var tree = ManifestFixtures.Tree("generated.json");
    tree["assumptions"] = new JsonArray(new JsonObject { ["target"] = "f" });
    var valid = schema.Evaluate(JsonSerializer.SerializeToElement(tree));
    tree["assumptions"] = new JsonArray(new JsonObject { ["anything"] = 1 });
    var invalid = schema.Evaluate(JsonSerializer.SerializeToElement(tree));
    Assert.That((schema.SectionsWithSchema.Single(), valid.Count, invalid.Select(error => error.Pointer)), Is.EqualTo(("assumptions", 0, new[] { "/assumptions/0" })));
  }

  [Test]
  public void ASubSchema_DoesNotLiftTheTypeCheck() {
    File.Copy(_schemaFile, Path.Combine(this._directory, ManifestSchema.MainFile));
    File.WriteAllText(Path.Combine(this._directory, ManifestSchema.SectionFile("budget")), "true");
    var tree = ManifestFixtures.Tree("generated.json");
    tree["budget"] = new JsonObject();
    Assert.That(ManifestSchema.Load(this._directory).Evaluate(JsonSerializer.SerializeToElement(tree)).Select(error => error.Pointer), Is.EqualTo(new[] { "/budget" }));
  }

  [TestCase("benchmarks")]
  [TestCase("assumptions")]
  [TestCase("resources")]
  [TestCase("objectModel")]
  [TestCase("profile")]
  [TestCase("budget")]
  public void EverySection_HasItsFileName(string section)
    => Assert.That((ManifestSchema.Sections.Contains(section), ManifestSchema.SectionFile(section)), Is.EqualTo((true, $"manifest.{section}.schema.json")));

  [Test]
  public void AMissingMainSchema_IsAnIOException()
    => Assert.That(() => ManifestSchema.Load(this._directory), Throws.InstanceOf<IOException>());

}
