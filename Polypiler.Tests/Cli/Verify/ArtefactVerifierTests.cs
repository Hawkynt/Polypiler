using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Cli.Verify;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Core.Verification.Running;
using Polypiler.Tests.Cli.Verify.Support;

namespace Polypiler.Tests.Cli.Verify;

/// <summary>
/// <see cref="ArtefactVerifier"/> on copies of <c>tests/fixtures/cli/verify</c>, with a scripted node:
/// every row of the test design of #181 and the paths around it.
/// </summary>
[TestFixture]
public sealed class ArtefactVerifierTests {

  private VerifyWorkspace _workspace = null!;
  private ScriptedNode _node = null!;
  private StringWriter _output = null!;
  private DiagnosticBag _diagnostics = null!;

  [SetUp]
  public void CopyTheFixtures() {
    this._workspace = new VerifyWorkspace();
    this._node = new ScriptedNode();
    this._output = new StringWriter();
    this._diagnostics = new DiagnosticBag();
  }

  [TearDown]
  public void DeleteTheCopy() {
    this._workspace.Dispose();
    this._output.Dispose();
  }

  private VerificationOutcome Verify(string? vectors = VerifyWorkspace.Vectors, bool update = true, string? evidence = null, string directory = "copy")
    => new ArtefactVerifier(this._output, this._workspace.Root, this._node, RunLimits.Default).Run(new VerifyRequest(directory, vectors, update, evidence), this._diagnostics);

  private string[] Lines => this._output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);

  private string[] Codes => [.. this._diagnostics.Items.Select(diagnostic => diagnostic.Code)];

  private string Messages => string.Join("\n", this._diagnostics.Items.Select(diagnostic => diagnostic.Message));

  private void AssertNothingRan() => Assert.That(this._node.Plans, Is.Empty);

  // Row 1: happy path.
  [Test]
  public void AGeneratedDirectory_IsVerified_AgainstFourVectors() {
    var outcome = this.Verify();

    Assert.That(outcome, Is.EqualTo(new VerificationOutcome(ManifestState.Verified, 4, 4)));
    Assert.That(this._diagnostics.Items, Is.Empty);
    Assert.That(this.Lines, Is.EqualTo(new[] { "  empty: pass", "  a: pass", "  abc: pass", "  Wikipedia: pass", "copy: verified (4 of 4 vectors)" }));
    var manifest = this._workspace.Manifest;
    Assert.That(manifest.State, Is.EqualTo(ManifestState.Verified));
    Assert.That(manifest.Build, Is.EqualTo(new ManifestBuild("node", "node --check adler32.js", 0)));
    Assert.That(manifest.Execution, Is.EqualTo(new ManifestExecution($"node {JavaScriptDriver.FileName} adler32.js adler32", 0)));
    Assert.That(manifest.Toolchains, Is.EqualTo(new[] { new ManifestToolchain("node", "22.1.0") }));
    Assert.That((manifest.Verification!.Oracle, manifest.Verification.Producer, manifest.Verification.Total, manifest.Verification.Passed, manifest.Verification.Failed),
      Is.EqualTo(("../vectors/adler32.vectors.json", "hand", 4L, 4L, 0L)));
    Assert.That((manifest.Verification.Exactness.Profile, manifest.Verification.Exactness.Mode, manifest.Verification.Exactness.Observed.Identical),
      Is.EqualTo(("strict", "exact", 4L)));
  }

  [Test]
  public void TheDriver_IsCopiedBesideTheOutputs_AndGetsOneArgumentListPerVector() {
    this.Verify();

    var run = this._node.Of(JavaScriptDriver.FileName).Single();
    Assert.That(run.Files.Keys, Is.EquivalentTo(new[] { "adler32.js", JavaScriptDriver.FileName }));
    Assert.That(JsonNode.Parse(run.Execute.Stdin.ToArray())!.AsArray(), Has.Count.EqualTo(4));
    Assert.That(run.Trust, Is.EqualTo(TrustLevel.Trusted));
  }

  // Row 2: every initial state ends verified.
  [TestCase(ManifestState.Generated)]
  [TestCase(ManifestState.Built)]
  [TestCase(ManifestState.Executed)]
  [TestCase(ManifestState.Verified)]
  public void EveryInitialState_EndsVerified(ManifestState initial) {
    this._workspace.Manifest = Recorded(this._workspace.Manifest, initial);

    Assert.That(this.Verify().Reached, Is.EqualTo(ManifestState.Verified));
    Assert.That(this._workspace.Manifest.State, Is.EqualTo(ManifestState.Verified));
  }

  /// <summary>A manifest whose evidence supports a state, with a verification of its own vectors for <see cref="ManifestState.Verified"/>.</summary>
  private static Manifest Recorded(Manifest manifest, ManifestState state) => manifest with {
    Build = state >= ManifestState.Built ? new("node", "node --check adler32.js", 0) : null,
    Execution = state >= ManifestState.Executed ? new("node run.mjs", 0) : null,
    Verification = state >= ManifestState.Verified ? new("../vectors/adler32.vectors.json", "hand", new("strict", "exact", "signed-zero", [], [], new(4, 0, 0, null)), 4, 4, 0) : null
  };

  // Row 3: the vectors of --against and those the manifest records give the same result.
  [Test]
  public void TheVectorsTheManifestRecords_GiveTheSameResult_AsAgainst() {
    var against = this.Verify();
    this._output.GetStringBuilder().Clear();

    var recorded = this.Verify(vectors: null);

    Assert.That(recorded, Is.EqualTo(against));
    Assert.That(this.Lines[^1], Is.EqualTo("copy: verified (4 of 4 vectors)"));
  }

  [Test]
  public void ARecordedVectorFileThatIsGone_IsUnreadable() {
    this.Verify();
    File.Delete(Path.Combine(this._workspace.Root, VerifyWorkspace.Vectors));
    this._diagnostics = new DiagnosticBag();

    this.Verify(vectors: null);

    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0004" }));
    Assert.That(this.Messages, Is.EqualTo("input 'copy/../vectors/adler32.vectors.json' does not exist or is not readable"));
  }

  // Row 4: --no-update leaves the directory as it was.
  [Test]
  public void NoUpdate_LeavesEveryFileOfTheDirectory() {
    var before = VerifyWorkspace.Hashes(this._workspace.Copy);

    Assert.That(this.Verify(update: false).Reached, Is.EqualTo(ManifestState.Verified));
    Assert.That(VerifyWorkspace.Hashes(this._workspace.Copy), Is.EqualTo(before));
  }

  // Rows 5 and 6: the empty input, one vector.
  [Test]
  public void OneVector_OfTheEmptyInput_Passes() {
    this.WriteVectors("one.vectors.json", """{ "name": "empty", "input": { "hex": "00000000" }, "expected": { "hex": "01000000" } }""");

    Assert.That(this.Verify("vectors/one.vectors.json"), Is.EqualTo(new VerificationOutcome(ManifestState.Verified, 1, 1)));
    Assert.That(this.Lines, Is.EqualTo(new[] { "  empty: pass", "copy: verified (1 of 1 vectors)" }));
  }

  // Row 6: zero vectors.
  [Test]
  public void ZeroVectors_HaveNoOracle() {
    this.WriteVectors("none.vectors.json", "");

    this.Verify("vectors/none.vectors.json");

    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0063" }));
    Assert.That(this._diagnostics.ExitCode, Is.EqualTo(ExitCode.OracleFailure));
    this.AssertNothingRan();
  }

  private void WriteVectors(string name, string vectors, string signature = """{ "parameters": [ { "name": "bytes", "type": "bytes", "mode": "in" } ], "result": "u32" }""", string generatedBy = "hand", string exactness = """{ "mode": "exact" }""")
    => this._workspace.Write("vectors/" + name, $$"""
      { "schema": 1, "fixture": "test.{{name}}",
        "provenance": { "kind": "published", "source": "RFC 1950", "detail": "section 8.2", "generatedBy": "{{generatedBy}}" },
        "exactness": {{exactness}}, "export": "adler32", "signature": {{signature}}, "vectors": [ {{vectors}} ] }
      """);

  // Rows 7 and 16: a value off by one fails and downgrades a verified manifest.
  [Test]
  public void AValueOffByOne_Fails_AndDowngradesAVerifiedManifest() {
    this._workspace.Manifest = Recorded(this._workspace.Manifest, ManifestState.Verified);

    var outcome = this.Verify(VerifyWorkspace.WrongVectors);

    Assert.That(outcome, Is.EqualTo(new VerificationOutcome(ManifestState.Executed, 0, 1)));
    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0051" }));
    Assert.That(this._diagnostics.ExitCode, Is.EqualTo(ExitCode.SemanticMismatch));
    Assert.That(this.Lines, Is.EqualTo(new[] { "  abc: FAIL expected 0x024D0128 actual 0x024D0127", "copy: executed (0 of 1 vectors)" }));
    var verification = this._workspace.Manifest.Verification!;
    Assert.That((this._workspace.Manifest.State, verification.Passed, verification.Failed), Is.EqualTo((ManifestState.Executed, 0L, 1L)));
  }

  // Row 8: a manifest without outputs.
  [Test]
  public void AManifestWithoutOutputs_IsInvalid() {
    this._workspace.Manifest = this._workspace.Manifest with { Outputs = [] };

    this.Verify();

    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0053" }));
    Assert.That(this.Messages, Is.EqualTo("manifest 'copy/polypiler.manifest.json' is invalid: /outputs: lists no output file"));
    this.AssertNothingRan();
  }

  [Test]
  public void AnOutputOutsideTheDirectory_IsInvalid() {
    var manifest = this._workspace.Manifest;
    this._workspace.Manifest = manifest with { Outputs = [manifest.Outputs[0] with { Path = "../vectors/adler32.vectors.json" }] };

    this.Verify();

    Assert.That(this.Messages, Is.EqualTo("manifest 'copy/polypiler.manifest.json' is invalid: /outputs/0/path: '../vectors/adler32.vectors.json' leaves the output directory"));
  }

  // Row 9: no manifest.
  [Test]
  public void ADirectoryWithoutAManifest_IsRefused() {
    var outcome = this.Verify(directory: "no-manifest/");

    Assert.That(outcome, Is.EqualTo(new VerificationOutcome(ManifestState.Generated, 0, 0)));
    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0016" }));
    Assert.That(this.Messages, Is.EqualTo("no manifest in 'no-manifest/'"));
    Assert.That(this._diagnostics.ExitCode, Is.EqualTo(ExitCode.EnvironmentError));
    Assert.That(this._output.ToString(), Is.Empty);
  }

  [Test]
  public void ADirectoryThatDoesNotExist_IsUnreadable() {
    this.Verify(directory: "absent");

    Assert.That(this.Messages, Is.EqualTo("input 'absent' does not exist or is not readable"));
  }

  // Row 10: a manifest that is not valid.
  [Test]
  public void ATruncatedManifest_IsInvalid() {
    File.WriteAllText(Path.Combine(this._workspace.Copy, Manifest.FileName), "{ \"schemaVersion\": 1,");

    this.Verify();

    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0053" }));
    Assert.That(this._diagnostics.ExitCode, Is.EqualTo(ExitCode.EnvironmentError));
    this.AssertNothingRan();
  }

  // Row 11: an edited or missing output.
  [Test]
  public void AnEditedOutput_IsNotVerified_AndNothingRuns() {
    var outcome = this.Verify(directory: "adler32-edited");

    Assert.That(outcome.Reached, Is.EqualTo(ManifestState.Generated));
    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0054" }));
    Assert.That(this.Messages, Does.StartWith("manifest 'adler32-edited/polypiler.manifest.json' claims 'generated' but the evidence supports only 'none': the output 'adler32.js' has the SHA-256 "));
    this.AssertNothingRan();
  }

  [Test]
  public void AMissingOutput_IsNotVerified() {
    var before = File.ReadAllText(Path.Combine(this._workspace.Copy, Manifest.FileName));
    File.Delete(Path.Combine(this._workspace.Copy, "adler32.js"));

    this.Verify();

    Assert.That(this.Messages, Does.EndWith("the output 'adler32.js' is missing"));
    Assert.That(File.ReadAllText(Path.Combine(this._workspace.Copy, Manifest.FileName)), Is.EqualTo(before));
    this.AssertNothingRan();
  }

  [Test]
  public void AFileTheManifestDoesNotList_IsIgnored() {
    File.WriteAllText(Path.Combine(this._workspace.Copy, "notes.txt"), "not generated");

    Assert.That(this.Verify().Reached, Is.EqualTo(ManifestState.Verified));
  }

  // Row 12: a vector file that is missing or malformed.
  [Test]
  public void AMissingVectorFile_IsUnreadable() {
    this.Verify("vectors/absent.vectors.json");

    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0004" }));
    Assert.That(this.Messages, Is.EqualTo("input 'vectors/absent.vectors.json' does not exist or is not readable"));
  }

  [Test]
  public void AMalformedVectorFile_IsAUsageError_AndTheManifestStaysGenerated() {
    this.Verify("vectors/broken.json");

    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0017" }));
    Assert.That(this.Messages, Does.StartWith("'vectors/broken.json' holds no test vectors: not valid JSON: "));
    Assert.That(this._diagnostics.ExitCode, Is.EqualTo(ExitCode.UsageError));
    Assert.That(this._workspace.Manifest.State, Is.EqualTo(ManifestState.Generated));
    this.AssertNothingRan();
  }

  [Test]
  public void AVectorWhoseInputIsNoImageOfTheSignature_IsNotUnderstood() {
    this.WriteVectors("leftover.vectors.json", """{ "name": "long", "input": { "hex": "0000000000" }, "expected": { "hex": "01000000" } }""");

    this.Verify("vectors/leftover.vectors.json");

    Assert.That(this.Messages, Is.EqualTo("'vectors/leftover.vectors.json' holds no test vectors: vector 'long': input image has 1 byte(s) left over after 'bytes'"));
  }

  [TestCase("""{ "parameters": [ { "name": "sum", "type": "u32", "mode": "out" } ], "result": "void" }""", "the parameter 'sum' is 'out'; only 'in' parameters can be passed to a JavaScript function")]
  [TestCase("""{ "parameters": [ { "name": "words", "type": "seq<u32>", "mode": "in" } ], "result": "u32" }""", "the type 'seq<u32>' cannot be passed to or returned from a JavaScript function; integers, bool, f32, f64, bytes, utf8 and utf16 can")]
  public void ASignatureTheDriverCannotCall_IsNotUnderstood(string signature, string reason) {
    this.WriteVectors("typed.vectors.json", "", signature);

    this.Verify("vectors/typed.vectors.json");

    Assert.That(this.Messages, Is.EqualTo("'vectors/typed.vectors.json' holds no test vectors: " + reason));
  }

  // Row 13: no vectors anywhere.
  [Test]
  public void NoVectorsAnywhere_HaveNoOracle() {
    this.Verify(vectors: null);

    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0063" }));
    Assert.That(this.Messages, Is.EqualTo("no independent oracle is registered for 'copy'"));
    Assert.That(this._diagnostics.ExitCode, Is.EqualTo(ExitCode.OracleFailure));
  }

  [Test]
  public void VectorsPolypilerProduced_HaveNoOracle() {
    this.WriteVectors("self.vectors.json", """{ "name": "empty", "input": { "hex": "00000000" }, "expected": { "hex": "01000000" } }""", generatedBy: "polypiler");

    this.Verify("vectors/self.vectors.json");

    Assert.That(this.Messages, Is.EqualTo("no independent oracle is registered for 'test.self.vectors.json'"));
    this.AssertNothingRan();
  }

  // Row 14: the build fails.
  [Test]
  public void AFailedBuild_IsATargetBuildFailure_AndStaysGenerated() {
    this._node.Answer = plan => plan.Execute.Arguments[0] == "--check" ? ScriptedNode.Completed(1, "", "adler32.js:3 SyntaxError: Unexpected token") : null;

    var outcome = this.Verify();

    Assert.That(outcome, Is.EqualTo(new VerificationOutcome(ManifestState.Generated, 0, 4)));
    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0030" }));
    Assert.That(this._diagnostics.Items[0].Cause, Is.EqualTo("adler32.js:3 SyntaxError: Unexpected token"));
    Assert.That(this._diagnostics.ExitCode, Is.EqualTo(ExitCode.TargetBuildFailure));
    Assert.That(this._workspace.Manifest.Build, Is.EqualTo(new ManifestBuild("node", "node --check adler32.js", 1)));
    Assert.That(this.Lines, Is.EqualTo(new[] { "copy: generated (0 of 4 vectors)" }));
    Assert.That(this._node.Of(JavaScriptDriver.FileName), Is.Empty);
  }

  [TestCase("", "the step printed nothing")]
  [TestCase("x", "x")]
  public void TheCauseOfAFailedBuild_IsWhatItPrinted_CutAt500Characters(string printed, string cause) {
    var text = printed.Length == 0 ? "" : new string('x', 600);
    this._node.Answer = plan => plan.Execute.Arguments[0] == "--check" ? ScriptedNode.Completed(1, text) : null;

    this.Verify();

    Assert.That(this._diagnostics.Items.Single().Cause, Is.EqualTo(printed.Length == 0 ? cause : new string('x', 500) + "..."));
  }

  [Test]
  public void ABuildOverItsTimeLimit_StaysGenerated_WithoutABuild() {
    this._node.Answer = plan => plan.Execute.Arguments[0] == "--check" ? ScriptedNode.Ended(RunOutcome.TimedOut, Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 60000L)) : null;

    Assert.That(this.Verify().Reached, Is.EqualTo(ManifestState.Generated));
    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0031" }));
    Assert.That(this._workspace.Manifest.Build, Is.Null);
  }

  // Row 15: the execution exceeds its time limit.
  [Test]
  public void AnExecutionOverItsTimeLimit_IsABudgetExceeded_AndStaysBuilt() {
    this._node.Answer = plan => plan.Execute.Arguments[0] == JavaScriptDriver.FileName ? ScriptedNode.Ended(RunOutcome.TimedOut, Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 60000L)) : null;

    Assert.That(this.Verify().Reached, Is.EqualTo(ManifestState.Built));
    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0031" }));
    Assert.That(this._diagnostics.ExitCode, Is.EqualTo(ExitCode.BudgetExceeded));
    Assert.That(this._workspace.Manifest.State, Is.EqualTo(ManifestState.Built));
  }

  [Test]
  public void AnExecutionThatExitsNonZero_StaysBuilt() {
    this._node.Answer = plan => plan.Execute.Arguments[0] == JavaScriptDriver.FileName ? ScriptedNode.Completed(3, "", "'adler32.js' exports no function 'adler32'") : null;

    Assert.That(this.Verify().Reached, Is.EqualTo(ManifestState.Built));
    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0030" }));
    Assert.That(this._workspace.Manifest.Execution!.ExitCode, Is.EqualTo(3));
  }

  [Test]
  public void ADriverThatPrintsNoLinePerVector_IsReported_AndStaysExecuted() {
    this._node.Answer = plan => plan.Execute.Arguments[0] == JavaScriptDriver.FileName ? ScriptedNode.Completed(0, "") : null;

    Assert.That(this.Verify().Reached, Is.EqualTo(ManifestState.Executed));
    Assert.That(this._diagnostics.Items.Single().Cause, Is.EqualTo("the driver printed no result line per vector: "));
  }

  [Test]
  public void ACallThatThrows_FailsWithItsTrap() {
    this._node.Answer = plan => plan.Execute.Arguments[0] == JavaScriptDriver.FileName ? ScriptedNode.Completed(0, """{"t":"x","v":"RangeError"}""" + "\n") : null;

    this.Verify(VerifyWorkspace.WrongVectors);

    Assert.That(this.Lines[0], Is.EqualTo("  abc: FAIL expected 0x024D0128 actual trap other:RangeError"));
  }

  [Test]
  public void AVectorThatExpectsATrap_IsAnOracleFailure() {
    this.WriteVectors("trap.vectors.json", """{ "name": "empty", "input": { "hex": "00000000" }, "expectedTrap": "bounds" }""");
    this._node.Answer = plan => plan.Execute.Arguments[0] == JavaScriptDriver.FileName ? ScriptedNode.Completed(0, """{"t":"x","v":"RangeError"}""" + "\n") : null;

    var outcome = this.Verify("vectors/trap.vectors.json");

    Assert.That(outcome.Reached, Is.EqualTo(ManifestState.Executed));
    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0050" }));
    Assert.That(this._workspace.Manifest.Verification, Is.Null);
  }

  // Row 17: the toolchain is missing.
  [Test]
  public void AMissingToolchain_IsAnEnvironmentError_AndLeavesTheManifest() {
    var before = VerifyWorkspace.Hashes(this._workspace.Copy);
    var missing = Diagnostic.Create(VerifyDiagnostics.ToolchainMissing, null, "toolchain 'node' is not installed (probe: node --version): 'node' was not found on PATH");
    this._node.Answer = _ => ScriptedNode.Ended(RunOutcome.Refused, missing);

    var outcome = this.Verify();

    Assert.That(outcome, Is.EqualTo(new VerificationOutcome(ManifestState.Generated, 0, 4)));
    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0021" }));
    Assert.That(this._diagnostics.ExitCode, Is.EqualTo(ExitCode.EnvironmentError));
    Assert.That(VerifyWorkspace.Hashes(this._workspace.Copy), Is.EqualTo(before));
    Assert.That(this._output.ToString(), Is.Empty);
  }

  [Test]
  public void AVersionProbeThatFails_IsReported() {
    this._node.Answer = plan => plan.Execute.Arguments[0] == "--version" ? ScriptedNode.Completed(9, "") : null;

    this.Verify();

    Assert.That(this.Messages, Is.EqualTo("target build failed: 'node --version' exited with 9"));
  }

  [Test]
  public void AVersionProbeWithoutOutput_RecordsAnUnknownVersion_InPlaceOfTheOldOne() {
    this._workspace.Manifest = this._workspace.Manifest with { Toolchains = [new("node", "18.0.0"), new("python", "3.12")] };
    this._node.Answer = plan => plan.Execute.Arguments[0] == "--version" ? ScriptedNode.Completed(0, "\n") : null;

    this.Verify();

    Assert.That(this._workspace.Manifest.Toolchains, Is.EquivalentTo(new[] { new ManifestToolchain("python", "3.12"), new ManifestToolchain("node", "unknown") }));
  }

  // Row 18: --evidence given and absent.
  [Test]
  public void Evidence_IsABundleWithTheManifestBeforeAndAfter() {
    this.Verify(evidence: "evidence");

    var bundle = Path.Combine(this._workspace.Root, "evidence", "polypiler-verify", "copy");
    var tree = JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "evidence.json")))!;
    Assert.That(tree["outcome"]!.GetValue<string>(), Is.EqualTo("passed"));
    Assert.That(tree["toolVersions"]!["node"]!.GetValue<string>(), Is.EqualTo("22.1.0"));
    Assert.That(tree["processes"]!.AsArray().Select(process => process!["label"]!.GetValue<string>()), Is.EqualTo(new[] { "version", "build-1", "execute" }));
    Assert.That(tree["target"]!.AsObject(), Has.Count.EqualTo(4));
    Assert.That(File.ReadAllText(Path.Combine(bundle, Manifest.FileName)), Does.Contain("\"state\": \"verified\""));
    Assert.That(File.ReadAllText(Path.Combine(bundle, "files", "manifest-before", Path.GetFileName(Directory.GetFiles(Path.Combine(bundle, "files", "manifest-before")).Single()))), Does.Contain("\"state\": \"generated\""));
  }

  [Test]
  public void Evidence_OfAFailedRun_SaysSo() {
    this.Verify(VerifyWorkspace.WrongVectors, evidence: "evidence");

    var tree = JsonNode.Parse(File.ReadAllText(Path.Combine(this._workspace.Root, "evidence", "polypiler-verify", "copy", "evidence.json")))!;
    Assert.That(tree["outcome"]!.GetValue<string>(), Is.EqualTo("failed"));
    Assert.That(tree["diff"], Is.Not.Null);
  }

  [Test]
  public void NoEvidence_WritesNoBundle() {
    this.Verify();

    Assert.That(Directory.Exists(Path.Combine(this._workspace.Root, "evidence")), Is.False);
  }

  [Test]
  public void OutputOfAnotherLanguage_IsNotRun() {
    var manifest = this._workspace.Manifest;
    this._workspace.Manifest = manifest with { Target = manifest.Target with { Language = "rust" } };

    this.Verify();

    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0003" }));
    Assert.That(this.Messages, Is.EqualTo("verify runs javascript output; 'copy' holds rust output"));
    this.AssertNothingRan();
  }

  [Test]
  public void OutputsWithoutAJavaScriptSource_AreInvalid() {
    var manifest = this._workspace.Manifest;
    this._workspace.Manifest = manifest with { Outputs = [manifest.Outputs[0] with { Role = ManifestOutputRole.Test }] };

    this.Verify();

    Assert.That(this.Messages, Is.EqualTo("manifest 'copy/polypiler.manifest.json' is invalid: /outputs: lists no JavaScript source file"));
  }

  [Test]
  public void AVectorFileWithoutAnExport_RunsTheModuleAsAProgram() {
    this._workspace.Write("vectors/stream.vectors.json", """
      { "schema": 1, "fixture": "test.stream",
        "provenance": { "kind": "published", "source": "RFC 1950", "detail": "section 8.2", "generatedBy": "hand" },
        "vectors": [ { "name": "abc", "input": { "text": "abc" }, "expected": { "hex": "024d0127" } },
                     { "name": "a", "input": { "text": "a" }, "expected": { "hex": "00620063" } } ] }
      """);

    var outcome = this.Verify("vectors/stream.vectors.json");

    Assert.That(outcome, Is.EqualTo(new VerificationOutcome(ManifestState.Executed, 1, 2)));
    Assert.That(this.Lines, Is.EqualTo(new[] { "  abc: pass", "  a: FAIL expected 0x00620063 actual 0x00620062", "copy: executed (1 of 2 vectors)" }));
    Assert.That(this._node.Plans.Select(plan => plan.Execute.ToString()), Is.EqualTo(new[] { "node --version", "node --check adler32.js", "node adler32.js", "node adler32.js" }));
  }

  [Test]
  public void AProgramThatPrintsNoHexadecimal_ComparesAsItsText_AndAProgramThatFails_StopsTheRun() {
    this._workspace.Write("vectors/stream.vectors.json", """
      { "schema": 1, "fixture": "test.stream",
        "provenance": { "kind": "published", "source": "RFC 1950", "detail": "section 8.2", "generatedBy": "hand" },
        "vectors": [ { "name": "abc", "input": { "text": "abc" }, "expected": { "hex": "" } } ] }
      """);
    this._node.Answer = plan => plan.Execute.Arguments is ["adler32.js"] ? ScriptedNode.Completed(0, "ok\n") : null;

    this.Verify("vectors/stream.vectors.json");
    Assert.That(this.Lines[0], Is.EqualTo("  abc: FAIL expected (nothing) actual 0x6F6B"));

    this._node.Answer = plan => plan.Execute.Arguments is ["adler32.js"] ? ScriptedNode.Ended(RunOutcome.OutputExceeded, Diagnostic.Create(RunnerDiagnostics.OutputLimitExceeded, null, 1L)) : null;
    this._diagnostics = new DiagnosticBag();
    this.Verify("vectors/stream.vectors.json");
    Assert.That(this.Codes, Is.EqualTo(new[] { "PLY0033" }));
  }

  [Test]
  public void AToleranceTheFixtureDeclares_IsRecorded_WithTheWorstVector() {
    this.WriteVectors("float.vectors.json",
      """{ "name": "one", "input": { "hex": "0000000000000000" }, "expected": { "hex": "000000000000f03f" } }, { "name": "two", "input": { "hex": "0000000000000000" }, "expected": { "hex": "0000000000000040" } }""",
      """{ "parameters": [ { "name": "x", "type": "f64", "mode": "in" } ], "result": "f64" }""",
      exactness: """{ "mode": "ulp", "bound": 2, "format": "f64", "reason": "the test says so" }""");
    this._node.Answer = plan => plan.Execute.Arguments[0] == JavaScriptDriver.FileName
      ? ScriptedNode.Completed(0, ScriptedNode.Number(1.0) + "\n" + ScriptedNode.Number(Math.BitIncrement(2.0)) + "\n")
      : null;

    Assert.That(this.Verify("vectors/float.vectors.json").Reached, Is.EqualTo(ManifestState.Verified));
    var exactness = this._workspace.Manifest.Verification!.Exactness;
    Assert.That(exactness.Mode, Is.EqualTo("tolerance"));
    Assert.That(exactness.Declared, Is.EqualTo(new[] { new ManifestDeclaredBound("result", "ulp", 2, "the test says so") }));
    Assert.That(exactness.Observed, Is.EqualTo(new ManifestObserved(1, 1, 1, "two")));
  }

  [Test]
  public void TheFloatingPointModeOfTheManifest_IsTheProfileOfTheVerification() {
    this._workspace.Manifest = this._workspace.Manifest with { FloatingPoint = new("accurate", []) };

    this.Verify();

    Assert.That(this._workspace.Manifest.Verification!.Exactness.Profile, Is.EqualTo("accurate"));
  }

  [Test]
  public void TheArguments_AreChecked() {
    var verifier = new ArtefactVerifier(this._output, this._workspace.Root, this._node, RunLimits.Default);
    Assert.Multiple(() => {
      Assert.That(() => verifier.Run(null!, this._diagnostics), Throws.ArgumentNullException);
      Assert.That(() => verifier.Run(new VerifyRequest("copy", null, true, null), null!), Throws.ArgumentNullException);
      Assert.That(() => new ArtefactVerifier(null!, "", this._node, RunLimits.Default), Throws.ArgumentNullException);
      Assert.That(() => new ArtefactVerifier(this._output, null!, this._node, RunLimits.Default), Throws.ArgumentNullException);
      Assert.That(() => new ArtefactVerifier(this._output, "", null!, RunLimits.Default), Throws.ArgumentNullException);
      Assert.That(() => new ArtefactVerifier(this._output, "", this._node, null!), Throws.ArgumentNullException);
      Assert.That(new ArtefactVerifier(this._output, this._workspace.Root), Is.Not.Null);
    });
  }

}
