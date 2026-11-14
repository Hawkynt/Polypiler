using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Tests.Verification.Manifests;

/// <summary>
/// <see cref="ManifestReader"/> and <see cref="ManifestValidator"/>: the schema first (<c>PLY0053</c>
/// with the JSON pointer of each error), then the state rule (<c>PLY0054</c>); an invalid manifest is
/// never loaded.
/// </summary>
[TestFixture]
public sealed class ManifestValidatorTests {

  private static string[] Pointers(Diagnostic[] diagnostics)
    => [.. diagnostics.Select(diagnostic => diagnostic.Message.Split(": ")[1])];

  private static (Manifest? Manifest, Diagnostic[] Diagnostics) Changed(Action<JsonObject> change, string fixture = "verified.json") {
    var tree = ManifestFixtures.Tree(fixture);
    change(tree);
    return ManifestFixtures.Parse(tree);
  }

  private static void AssertInvalidAt(Action<JsonObject> change, string pointer, string fixture = "verified.json") {
    var (manifest, diagnostics) = Changed(change, fixture);
    Assert.That(manifest, Is.Null);
    Assert.That(diagnostics.Select(diagnostic => diagnostic.Code).Distinct(), Is.EqualTo(new[] { "PLY0053" }));
    Assert.That(Pointers(diagnostics), Does.Contain(pointer));
  }

  [TestCase("generated.json", ManifestState.Generated)]
  [TestCase("built.json", ManifestState.Built)]
  [TestCase("executed.json", ManifestState.Executed)]
  [TestCase("verified.json", ManifestState.Verified)]
  public void AValidFixture_Validates_AndRoundTripsByteForByte(string fixture, ManifestState state) {
    var bag = new DiagnosticBag();
    var manifest = ManifestReader.Read(ManifestFixtures.PathOf(fixture), bag);
    Assert.That((bag.Items.Count, manifest!.State, ManifestValidator.Validate(ManifestFixtures.PathOf(fixture), bag)), Is.EqualTo((0, state, true)));
    Assert.That(ManifestWriter.Serialize(manifest), Is.EqualTo(ManifestFixtures.Text(fixture)));
  }

  [Test]
  public void VerifiedWithoutResults_ClaimsMoreThanItsEvidence() {
    var bag = new DiagnosticBag();
    var path = ManifestFixtures.PathOf("verified-without-results.json");
    Assert.That(ManifestValidator.Validate(path, bag), Is.False);
    var diagnostic = bag.Items.Single();
    Assert.That((diagnostic.Code, diagnostic.Message, diagnostic.File, diagnostic.ExitCode, bag.ExitCode), Is.EqualTo(("PLY0054",
      $"manifest '{path}' claims 'verified' but the evidence supports only 'executed': no verification results are recorded",
      path, ExitCode.EnvironmentError, ExitCode.EnvironmentError)));
    Assert.That((int)bag.ExitCode, Is.EqualTo(1));
  }

  [TestCase("verified-with-failure.json", "claims 'verified' but the evidence supports only 'executed': the verification passed 2 of 3 with 1 failed")]
  [TestCase("built-without-build.json", "claims 'built' but the evidence supports only 'generated': no build is recorded")]
  public void AStateTheEvidenceDoesNotSupport_IsPly0054(string fixture, string message) {
    var bag = new DiagnosticBag();
    Assert.That(ManifestValidator.Validate(ManifestFixtures.PathOf(fixture), bag), Is.False);
    Assert.That(bag.Items.Select(diagnostic => (diagnostic.Code, diagnostic.Message.EndsWith(message, StringComparison.Ordinal))), Is.EqualTo(new[] { ("PLY0054", true) }));
  }

  [Test]
  public void AVerificationByPolypiler_SupportsOnlyExecuted() {
    var (manifest, diagnostics) = Changed(tree => tree["verification"]!["producer"] = "polypiler");
    Assert.That((manifest, diagnostics.Single().Message), Is.EqualTo(((Manifest?)null,
      "manifest 'm.json' claims 'verified' but the evidence supports only 'executed': the expected values were produced by polypiler itself")));
    Assert.That(Changed(tree => {
      tree["verification"]!["producer"] = "polypiler";
      tree["state"] = "executed";
    }).Manifest!.State, Is.EqualTo(ManifestState.Executed));
  }

  [TestCase(0, 0, ManifestState.Executed)]
  [TestCase(1, 1, ManifestState.Verified)]
  [TestCase(2, 3, ManifestState.Executed)]
  public void TheTotalsBoundary_DecidesVerified(int passed, int total, ManifestState state) {
    var (manifest, diagnostics) = Changed(tree => {
      tree["verification"]!["passed"] = passed;
      tree["verification"]!["total"] = total;
      tree["state"] = ManifestStateRule.Name(state);
    });
    Assert.That((manifest?.State, diagnostics.Length), Is.EqualTo(((ManifestState?)state, 0)));
  }

  [Test]
  public void AStateBelowTheEvidence_IsInvalidAtState() {
    var (manifest, diagnostics) = Changed(tree => tree["state"] = "built");
    Assert.That((manifest, diagnostics.Single().Message), Is.EqualTo(((Manifest?)null,
      "manifest 'm.json' is invalid: /state: 'built' is lower than the 'verified' the evidence supports")));
  }

  [Test]
  public void AnUnknownState_FailsTheSchemaAtState() {
    var bag = new DiagnosticBag();
    Assert.That(ManifestValidator.Validate(ManifestFixtures.PathOf("unknown-state.json"), bag), Is.False);
    Assert.That((bag.Items.Single().Code, Pointers([.. bag.Items])), Is.EqualTo(("PLY0053", new[] { "/state" })));
  }

  [Test]
  public void SchemaVersion2_IsUnsupported() {
    var bag = new DiagnosticBag();
    Assert.That(ManifestValidator.Validate(ManifestFixtures.PathOf("schema-version-2.json"), bag), Is.False);
    Assert.That((bag.Items.Single().Code, Pointers([.. bag.Items])), Is.EqualTo(("PLY0053", new[] { "/schemaVersion" })));
  }

  [Test]
  public void ATruncatedFile_IsNotJson_AndNothingIsLoaded() {
    var bag = new DiagnosticBag();
    var path = ManifestFixtures.PathOf("truncated.json");
    Assert.That(ManifestReader.Read(path, bag), Is.Null);
    var diagnostic = bag.Items.Single();
    Assert.That((diagnostic.Code, diagnostic.Message.StartsWith($"manifest '{path}' is invalid: /: not JSON: ", StringComparison.Ordinal), diagnostic.Line > 1, diagnostic.Column > 0),
      Is.EqualTo(("PLY0053", true, true, true)));
  }

  [TestCase("{\"a\": 1, \"a\": 2}")]
  [TestCase("{\"a\": 1,}")]
  [TestCase("// comment\n{}")]
  public void JsonThatIsNotStrict_IsNotJson(string text)
    => Assert.That(ManifestFixtures.Parse(text).Diagnostics.Single().Message, Does.StartWith("manifest 'm.json' is invalid: /: not JSON: "));

  [TestCase("[]")]
  [TestCase("\"manifest\"")]
  [TestCase("null")]
  public void AManifestThatIsNoObject_IsInvalidAtTheRoot(string text) {
    var (manifest, diagnostics) = ManifestFixtures.Parse(text);
    Assert.That((manifest, diagnostics.Select(diagnostic => diagnostic.Message).Distinct()), Is.EqualTo(((Manifest?)null, new[] { "manifest 'm.json' is invalid: /: Value is \"" + (text == "[]" ? "array" : text == "null" ? "null" : "string") + "\" but should be \"object\"" })));
  }

  [Test]
  public void AMissingRequiredProperty_IsReportedAtTheObject()
    => AssertInvalidAt(tree => tree.Remove("frontend"), "/");

  [TestCase("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
  [TestCase("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015a")]
  public void AMalformedHash_FailsAtTheProperty(string hash)
    => AssertInvalidAt(tree => tree["outputs"]![1]!["sha256"] = hash, "/outputs/1/sha256");

  [TestCase("/tmp/out/a.cs")]
  [TestCase("C:/out/a.cs")]
  [TestCase("out\\a.cs")]
  [TestCase("~/a.cs")]
  public void AnAbsoluteOrNonPortablePath_FailsAtTheProperty(string path)
    => AssertInvalidAt(tree => tree["outputs"]![0]!["path"] = path, "/outputs/0/path");

  [Test]
  public void AnUnknownTopLevelProperty_IsNamed() {
    var (_, diagnostics) = Changed(tree => tree["extras"] = new JsonObject());
    Assert.That(diagnostics.Single().Message, Is.EqualTo("manifest 'm.json' is invalid: /extras: property 'extras' is not allowed"));
  }

  [Test]
  public void AnInputWithoutProvenance_HasNull_AndOneWithItTheObject() {
    var pinned = ManifestFixtures.Model("verified.json").Inputs.Single();
    var local = Changed(tree => tree["inputs"]![0]!["provenance"] = null).Manifest!.Inputs.Single();
    Assert.That((pinned.Provenance, local.Provenance), Is.EqualTo((new ManifestProvenance("Hawkynt/Hawkynt.github.io", "b3a8a8e92cbca83475986391c3cac56f0b185d29", "src/Waves.cs"), (ManifestProvenance?)null)));
  }

  [Test]
  public void AShortCommit_FailsAtTheProperty()
    => AssertInvalidAt(tree => tree["inputs"]![0]!["provenance"]!["commit"] = "b3a8a8e", "/inputs/0/provenance/commit");

  [Test]
  public void ExactAndToleranceExactness_BothValidate() {
    var tolerance = ManifestFixtures.Model("verified.json").Verification!.Exactness;
    var exact = Changed(tree => tree["verification"]!["exactness"] = JsonNode.Parse("""
      { "profile": "strict", "mode": "exact", "specialValues": "bitwise", "operations": [], "declared": [],
        "observed": { "identical": 4096, "withinTolerance": 0, "maxDistance": 0 } }
      """)).Manifest!.Verification!.Exactness;
    Assert.That((exact.Operations.Count, exact.Observed.WorstVector, tolerance.Operations.Single(), tolerance.Observed), Is.EqualTo((0, (string?)null,
      new ManifestToleranceOperation("INTRINSICS.Sin", "ulp(1)", "VERIFICATION.tolerance.sin", "ulp", 3), new ManifestObserved(4090, 6, 1, "sin-0173"))));
  }

  [Test]
  public void ADeclaredBound_IsReadAndWrittenBack() {
    var manifest = Changed(tree => tree["verification"]!["exactness"]!["declared"] = JsonNode.Parse("""
      [{ "output": "sum", "mode": "relative", "bound": 1e-12, "reason": "sum of 64 Sin results" }]
      """)).Manifest!;
    Assert.That(manifest.Verification!.Exactness.Declared.Single(), Is.EqualTo(new ManifestDeclaredBound("sum", "relative", 1e-12, "sum of 64 Sin results")));
    Assert.That(ManifestWriter.Serialize(manifest), Does.Contain("\"bound\": 1e-12,\n          \"reason\": \"sum of 64 Sin results\""));
  }

  [Test]
  public void TheWarnings_OfARun_AreItsWarningDiagnostics() {
    Diagnostic[] diagnostics = [
      new("PLY0001", DiagnosticSeverity.Error, "e"), new("PLY1101", DiagnosticSeverity.Warning, "w"), new("PLY0002", DiagnosticSeverity.Info, "i")
    ];
    Assert.That(ManifestMessage.WarningsOf(diagnostics), Is.EqualTo(new[] { new ManifestMessage("PLY1101", "w") }));
  }

  [Test]
  public void MaxDistance0_WithoutWorstVector_IsValid()
    => Assert.That(Changed(tree => {
      var observed = tree["verification"]!["exactness"]!["observed"]!.AsObject();
      observed["maxDistance"] = 0;
      observed.Remove("worstVector");
    }).Diagnostics, Is.Empty);

  [Test]
  public void MaxDistance0_WithWorstVector_IsInvalid()
    => AssertInvalidAt(tree => tree["verification"]!["exactness"]!["observed"]!["maxDistance"] = 0, "/verification/exactness/observed/worstVector");

  [Test]
  public void TheReasons_SayWhatIsWrong() {
    string Reason(Action<JsonObject> change) => Changed(change).Diagnostics.Single().Message.Split(": ", 3)[2];
    Assert.That(new[] {
      Reason(tree => tree["verification"]!["exactness"]!["observed"]!["maxDistance"] = 0),
      Reason(tree => tree["verification"]!["exactness"]!["observed"]!["worstVector"] = ""),
      Reason(tree => tree["state"] = "certified")
    }, Is.EqualTo(new[] { "property 'worstVector' is not allowed", "Value should be at least 1 characters", "Value should match one of the values specified by the enum" }));
  }

  [Test]
  public void MaxDistance1_WithoutWorstVector_IsInvalid()
    => AssertInvalidAt(tree => tree["verification"]!["exactness"]!["observed"]!.AsObject().Remove("worstVector"), "/verification/exactness/observed");

  [Test]
  public void ExactnessAsAString_IsInvalid()
    => AssertInvalidAt(tree => tree["verification"]!["exactness"] = "bit-exact", "/verification/exactness");

  [Test]
  public void ExactModeWithAnOperation_IsInvalid()
    => AssertInvalidAt(tree => {
      var exactness = tree["verification"]!["exactness"]!;
      exactness["mode"] = "exact";
      exactness["observed"]!["withinTolerance"] = 0;
    }, "/verification/exactness/operations");

  [Test]
  public void ExactModeWithADeclaredBound_IsInvalid()
    => AssertInvalidAt(tree => {
      var exactness = tree["verification"]!["exactness"]!;
      exactness["mode"] = "exact";
      exactness["operations"] = new JsonArray();
      exactness["observed"]!["withinTolerance"] = 0;
      exactness["declared"] = JsonNode.Parse("""[{ "output": "x", "mode": "ulp", "bound": 2, "reason": "r" }]""");
    }, "/verification/exactness/declared");

  [Test]
  public void ExactModeWithElementsWithinTolerance_IsInvalid()
    => AssertInvalidAt(tree => {
      var exactness = tree["verification"]!["exactness"]!;
      exactness["mode"] = "exact";
      exactness["operations"] = new JsonArray();
    }, "/verification/exactness/observed/withinTolerance");

  [Test]
  public void ADeclaredBoundWithoutReason_IsInvalid()
    => AssertInvalidAt(tree => tree["verification"]!["exactness"]!["declared"] = JsonNode.Parse("""[{ "output": "x", "mode": "ulp", "bound": 2 }]"""),
      "/verification/exactness/declared/0");

  [Test]
  public void AnUnknownProfile_IsInvalid()
    => AssertInvalidAt(tree => tree["verification"]!["exactness"]!["profile"] = "sloppy", "/verification/exactness/profile");

  [TestCase("benchmarks", "[{ \"name\": \"b\", \"ns\": 12 }]")]
  [TestCase("assumptions", "[{ \"target\": \"f\" }]")]
  [TestCase("resources", "{ \"ram\": 120 }")]
  [TestCase("objectModel", "{ \"features\": [] }")]
  [TestCase("profile", "{ \"name\": \"p\" }")]
  [TestCase("budget", "{ \"code\": 4096 }")]
  public void AnExtensionSection_IsValidPresentAndAbsent_ButNeverNullOrEmpty(string section, string value) {
    var present = Changed(tree => tree[section] = JsonNode.Parse(value));
    Assert.That((present.Diagnostics.Length, ManifestWriter.Serialize(present.Manifest!).Contains($"\"{section}\": "), ManifestFixtures.Model("verified.json").State),
      Is.EqualTo((0, true, ManifestState.Verified)));
    AssertInvalidAt(tree => tree[section] = null, "/" + section);
    AssertInvalidAt(tree => tree[section] = JsonNode.Parse(value.StartsWith('[') ? "[]" : "{}"), "/" + section);
  }

  [TestCase("shape", "{ \"kind\": \"class\" }")]
  [TestCase("adapterOf", "\"Wave\"")]
  [TestCase("representation", "\"double\"")]
  public void AnOptionalApiMember_IsValidPresent_ButNeverNull(string member, string value) {
    var present = Changed(tree => tree["api"]![0]![member] = JsonNode.Parse(value));
    Assert.That((present.Diagnostics.Length, ManifestWriter.Serialize(present.Manifest!).Contains($"\"{member}\": "), ManifestWriter.Serialize(ManifestFixtures.Model("verified.json")).Contains($"\"{member}\"")),
      Is.EqualTo((0, true, false)));
    AssertInvalidAt(tree => tree["api"]![0]![member] = null, $"/api/0/{member}");
  }

  [Test]
  public void AHelper_IsReadWithEveryMember() {
    var (manifest, diagnostics) = Changed(tree => tree["helpers"] = JsonNode.Parse("""
      [{ "symbol": "$imul", "license": "LGPL-3.0-or-later", "origin": "polypiler", "callSites": 2, "size": 140, "dependsOn": ["$u32"] }]
      """));
    Assert.That((diagnostics.Length, manifest!.Helpers.Single().Symbol, manifest.Helpers.Single().DependsOn.Single()), Is.EqualTo((0, "$imul", "$u32")));
  }

  [Test]
  public void EveryMemberOfTheModel_IsRead() {
    var manifest = ManifestFixtures.Model("verified.json");
    Assert.That(new object?[] {
      manifest.Compiler, manifest.Command.Count, manifest.Frontend, manifest.Target, manifest.Outputs.Select(output => output.Role),
      manifest.Api.Single().Signature, manifest.Intrinsics.Single(), manifest.FloatingPoint!.Natives.Single(), manifest.Licensing.Obligations.Single(),
      manifest.Toolchains.Single(), manifest.Build, manifest.Execution, manifest.Commands
    }, Is.EqualTo(new object?[] {
      new ManifestCompiler("polypiler", "0.1.0", 1), 8, new ManifestFrontend("csharp", null), new ManifestTarget("javascript", "es2022, esm", "node", "any"),
      new[] { ManifestOutputRole.Notice, ManifestOutputRole.Source, ManifestOutputRole.Test },
      "f64 Wave(f64 x)", "INTRINSICS.Sin", new ManifestNativeFunction("INTRINSICS.Sin", "f64", "ulp(1)", "node", 1, 3), "keep the copyright notice",
      new ManifestToolchain("node", "24.11.1"), new ManifestBuild("node", "node --check Waves.mjs", 0), new ManifestExecution("node Waves.test.mjs", 0),
      new ManifestCommands("polypiler translate ../src/Waves.cs --from csharp --to javascript --output .", "node Waves.test.mjs")
    }));
  }

  [TestCase("helper", ManifestOutputRole.Helper)]
  [TestCase("build", ManifestOutputRole.Build)]
  public void EveryRole_IsRead(string name, ManifestOutputRole role)
    => Assert.That(Changed(tree => tree["outputs"]![0]!["role"] = name).Manifest!.Outputs[0].Role, Is.EqualTo(role));

  [Test]
  public void TheCaveatsAndWarnings_AreRead() {
    var manifest = Changed(tree => {
      tree["caveats"] = JsonNode.Parse("""[{ "code": "JS-INT32", "message": "m" }]""");
      tree["warnings"] = JsonNode.Parse("""[{ "code": "PLY1101", "message": "w" }]""");
      tree["optimization"] = JsonNode.Parse("""{ "level": "default", "disabled": ["O0005"], "passes": [{ "id": "O0001", "proofClass": "unproven" }] }""");
      tree["nativeDependencies"] = JsonNode.Parse("""[{ "name": "ncnn", "version": "1.0", "licence": "BSD-3-Clause" }]""");
    }).Manifest!;
    Assert.That((manifest.Caveats.Single(), manifest.Warnings.Single(), manifest.Optimization.Disabled.Single(), manifest.Optimization.Passes.Single(), manifest.NativeDependencies.Single()),
      Is.EqualTo((new ManifestMessage("JS-INT32", "m"), new ManifestMessage("PLY1101", "w"), "O0005", new ManifestPass("O0001", "unproven"), new ManifestNativeDependency("ncnn", "1.0", "BSD-3-Clause"))));
  }

  [Test]
  public void AMissingFile_IsAnIOException()
    => Assert.That(() => ManifestValidator.Validate(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"), new DiagnosticBag()), Throws.InstanceOf<IOException>());

  [Test]
  public void TheRepositorySchema_AgreesWithTheEmbeddedOne() {
    var bag = new DiagnosticBag();
    Assert.That(ManifestFixtures.Valid.All(fixture => ManifestValidator.Validate(ManifestFixtures.PathOf(fixture), bag, ManifestFixtures.RepositorySchema)), Is.True);
  }

}
