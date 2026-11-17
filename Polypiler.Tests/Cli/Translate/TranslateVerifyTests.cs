using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Cli.Translate;

/// <summary>
/// <c>translate --verify</c> (#180, test design 1, 5 to 8, 11, 15 to 17): build, execute and compare
/// through the runner and the oracle framework, each step earning its state in the manifest and none
/// ahead of it, never <c>verified</c> after a failure. The runner here starts nothing; the acceptance
/// tests run the same through <c>node</c>.
/// </summary>
[TestFixture]
public sealed class TranslateVerifyTests {

  private const string Sum32 = TranslateWorkspace.Fixtures + "/sum32.cs";
  private const string Vectors = TranslateWorkspace.Fixtures + "/sum32.vectors.json";
  private const string Wrong = TranslateWorkspace.Fixtures + "/sum32.wrong.vectors.json";

  private TranslateWorkspace _workspace = null!;

  [SetUp]
  public void CreateWorkspace() => this._workspace = new();

  [TearDown]
  public void DeleteWorkspace() => this._workspace.Dispose();

  private (int ExitCode, string Output, string Error) Verify(FakeRunner runner, string? vectors, params string[] options) {
    string[] given = vectors is null ? [] : ["--vectors", vectors];
    return this._workspace.Run(runner, ["translate", Sum32, "--from", "csharp", "--to", "javascript", "--verify", .. given, .. options, "--output", "out"]);
  }

  private static string LastLine(string output) => output.TrimEnd('\n').Split('\n')[^1];

  [Test]
  public void AllVectorsPassing_IsVerified() {
    var runner = new FakeRunner(_ => FakeRunner.Completed("ok 3\nok 0\nok 0\n"));
    var (exitCode, output, error) = this.Verify(runner, Vectors, "--export", "Add");
    Assert.That((exitCode, error, LastLine(output)), Is.EqualTo((0, "", "out: verified (3 of 3 vectors)")));
    var manifest = this._workspace.Manifest("out");
    Assert.That((manifest.State, manifest.Build, manifest.Execution), Is.EqualTo((ManifestState.Verified, new ManifestBuild("node", "node --check Sum32.mjs", 0), new ManifestExecution("node polypiler.verify.mjs", 0))));
    Assert.That((manifest.Verification!.Oracle, manifest.Verification.Producer, manifest.Verification.Total, manifest.Verification.Passed, manifest.Verification.Failed),
      Is.EqualTo(("sum32.vectors.json", "hand", 3L, 3L, 0L)));
    Assert.That((manifest.Verification.Exactness.Mode, manifest.Verification.Exactness.Observed.Identical), Is.EqualTo(("exact", 3L)));
    Assert.That(File.ReadAllText(this._workspace.Resolve("out/polypiler.manifest.json")), Does.Contain("\"result\": \"passed\""));
  }

  [Test]
  public void ThePlan_BuildsTheModuleAndRunsTheDriverOnTheVectors() {
    var runner = new FakeRunner(_ => FakeRunner.Completed("ok 3\nok 0\nok 0\n"));
    this.Verify(runner, Vectors);
    var plan = runner.Plans.Single();
    Assert.That((plan.Build.Single().ToString(), plan.Execute.ToString(), plan.Trust), Is.EqualTo(("node --check Sum32.mjs", "node polypiler.verify.mjs", TrustLevel.Trusted)));
    Assert.That(plan.Files.Keys.Order(StringComparer.Ordinal), Is.EqualTo(new[] { "Sum32.mjs", "polypiler.verify.mjs" }));
    Assert.That(Encoding.UTF8.GetString(plan.Files["polypiler.verify.mjs"].Span), Does.Contain("const calls = [[1, 2], [4294967295, 1], [2147483648, 2147483648]];"));
    Assert.That(File.Exists(this._workspace.Resolve("out/polypiler.verify.mjs")), Is.False, "the driver is no output");
  }

  [Test]
  public void OneVector_IsVerifiedAsOneOfOne() {
    var one = this._workspace.Write("one.vectors.json", File.ReadAllText(this._workspace.Resolve(Vectors)).Replace("""
    { "name": "Add(0xFFFFFFFF, 1)", "input": { "hex": "ffffffff01000000" }, "expected": { "hex": "00000000" } },
    { "name": "Add(0x80000000, 0x80000000)", "input": { "hex": "0000008000000080" }, "expected": { "hex": "00000000" } }
""", "").Replace("""{ "hex": "03000000" } },""", """{ "hex": "03000000" } }"""));
    var (exitCode, output, error) = this.Verify(new FakeRunner(_ => FakeRunner.Completed("ok 3\n")), one);
    Assert.That((exitCode, error, LastLine(output)), Is.EqualTo((0, "", "out: verified (1 of 1 vectors)")));
  }

  [Test]
  public void AMismatch_IsPLY0051_AndLeavesTheStateExecuted() {
    var (exitCode, output, error) = this.Verify(new FakeRunner(_ => FakeRunner.Completed("ok 3\n")), Wrong);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.SemanticMismatch));
    Assert.That(error, Does.StartWith("polypiler: error PLY0051: 'cli/translate/sum32-wrong' vector 'Add(1, 2)': expected 04000000, actual 03000000"));
    Assert.That(LastLine(output), Is.EqualTo("out: executed"));
    var manifest = this._workspace.Manifest("out");
    Assert.That((manifest.State, manifest.Verification!.Failed), Is.EqualTo((ManifestState.Executed, 1L)));
    Assert.That(File.ReadAllText(this._workspace.Resolve("out/polypiler.manifest.json")), Does.Contain("\"result\": \"failed\""));
  }

  [TestCase("trap TypeError\nok 0\nok 0\n", TestName = "A trap fails its vector")]
  [TestCase("ok 4294967296\nok 0\nok 0\n", TestName = "A value outside the result type fails its vector")]
  [TestCase("ok 3.5\nok 0\nok 0\n", TestName = "A value that is no integer fails its vector")]
  [TestCase("ok 3\n", TestName = "A missing line fails its vector")]
  public void AnOutputThatIsNotTheValue_IsAMismatch(string stdout) {
    var (exitCode, output, _) = this.Verify(new FakeRunner(_ => FakeRunner.Completed(stdout)), Vectors);
    Assert.That((exitCode, LastLine(output), this._workspace.Manifest("out").State), Is.EqualTo(((int)ExitCode.SemanticMismatch, "out: executed", ManifestState.Executed)));
  }

  [Test]
  public void ADriverThatFailed_LeavesTheStateBuilt_AndEveryVectorFails() {
    var (exitCode, output, error) = this.Verify(new FakeRunner(_ => FakeRunner.Completed("ok 3\nok 0\nok 0\n", 1)), Vectors);
    Assert.That((exitCode, LastLine(output)), Is.EqualTo(((int)ExitCode.SemanticMismatch, "out: built")));
    Assert.That(error.Split('\n').Count(line => line.Contains("error PLY0051", StringComparison.Ordinal)), Is.EqualTo(3));
    Assert.That(this._workspace.Manifest("out").Execution!.ExitCode, Is.EqualTo(1));
  }

  [Test]
  public void ABuildThatFailed_IsPLY0030_AndLeavesTheStateGenerated() {
    var failed = Diagnostic.Create(RunnerDiagnostics.BuildFailed, null, "node --check Sum32.mjs", 1);
    var (exitCode, output, error) = this.Verify(new FakeRunner(_ => FakeRunner.Ended(RunOutcome.BuildFailed, failed)), Vectors);
    Assert.That((exitCode, LastLine(output)), Is.EqualTo(((int)ExitCode.TargetBuildFailure, "out: generated")));
    Assert.That(error, Does.StartWith("polypiler: error PLY0030: target build failed: 'node --check Sum32.mjs' exited with 1"));
    var manifest = this._workspace.Manifest("out");
    Assert.That((manifest.State, manifest.Build!.ExitCode, manifest.Execution), Is.EqualTo((ManifestState.Generated, -1, (ManifestExecution?)null)));
  }

  [Test]
  public void ARunnerLimit_IsItsDiagnostic_AndClaimsNoStep() {
    var limit = Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 30000L);
    var (exitCode, output, error) = this.Verify(new FakeRunner(_ => FakeRunner.Ended(RunOutcome.TimedOut, limit)), Vectors);
    Assert.That((exitCode, LastLine(output)), Is.EqualTo(((int)ExitCode.BudgetExceeded, "out: generated")));
    Assert.That(error, Does.StartWith("polypiler: error PLY0031: time limit of 30000 ms exceeded"));
    Assert.That(this._workspace.Manifest("out").Build, Is.Null);
  }

  [Test]
  public void ZeroVectors_IsPLY0063_AndLeavesTheStateExecuted() {
    var empty = this._workspace.Write("empty.vectors.json", """
      { "schema": 1, "fixture": "empty", "provenance": { "kind": "published", "source": "s", "detail": "d", "generatedBy": "hand" },
        "export": "Add", "signature": { "parameters": [ { "name": "a", "type": "u32" }, { "name": "b", "type": "u32" } ], "result": "u32" },
        "vectors": [] }
      """);
    var (exitCode, output, error) = this.Verify(new FakeRunner(_ => FakeRunner.Completed("")), empty);
    Assert.That((exitCode, LastLine(output), error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.OracleFailure, "out: executed", "polypiler: error PLY0063: no independent oracle is registered for 'empty'")));
    Assert.That(this._workspace.Manifest("out").Verification, Is.Null);
  }

  [Test]
  public void VerifyWithAnExportButWithoutVectors_IsPLY0063AfterRunningNothing() {
    var runner = new FakeRunner(_ => FakeRunner.Completed(""));
    var (exitCode, _, error) = this.Verify(runner, null, "--export", "Add");
    Assert.That((exitCode, error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.OracleFailure, $"polypiler: error PLY0063: no independent oracle is registered for '{Sum32}'")));
    Assert.That(System.Text.Encoding.UTF8.GetString(runner.Plans.Single().Files["polypiler.verify.mjs"].Span), Does.Contain("const calls = [];").And.Contain("m[\"Add\"]"));
  }

  [Test]
  public void TheFloatingPointModeOfTheManifest_IsTheProfileOfTheComparison() {
    var verifier = new TranslationVerifier(new FakeRunner(_ => FakeRunner.Completed("ok 3\n")));
    var vectors = VectorFile.Load(this._workspace.Resolve(Wrong));
    var manifest = this.Generated() with { FloatingPoint = new("strict", []) };
    using var output = new StringWriter();
    var cli = new CliContext(output, output, "", DiagnosticsFormat.Text, false, this._workspace.Path);
    Directory.CreateDirectory(this._workspace.Resolve("direct"));
    verifier.Run(new("f", vectors, "Add"), manifest, [("Sum32.mjs", [])], this._workspace.Resolve("direct"), "direct", cli);
    Assert.That(this._workspace.Manifest("direct").Verification!.Exactness.Profile, Is.EqualTo("strict"));
  }

  private Manifest Generated() => new() {
    Compiler = new("p", "1", 1), Command = ["t"], Inputs = [], Frontend = new("c", null), Target = new("j", "p", "r", "any"), Outputs = [], Api = [], Intrinsics = [],
    Helpers = [], NativeDependencies = [], Licensing = new("MIT", [], []), Caveats = [], Optimization = new("default", [], []), Warnings = [], Toolchains = [], Commands = new("b", null)
  };

  [Test]
  public void VerifyWithoutVectors_IsPLY0063() {
    var (exitCode, output, error) = this.Verify(new FakeRunner(_ => FakeRunner.Completed("")), null);
    Assert.That((exitCode, LastLine(output), error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.OracleFailure, "out: executed", $"polypiler: error PLY0063: no independent oracle is registered for '{Sum32}'")));
  }

  [Test]
  public void VectorsPolypilerProduced_AreNoOracle() {
    var own = this._workspace.Write("own.vectors.json", File.ReadAllText(this._workspace.Resolve(Vectors)).Replace("\"generatedBy\": \"hand\"", "\"generatedBy\": \"polypiler\""));
    var (exitCode, output, error) = this.Verify(new FakeRunner(_ => FakeRunner.Completed("ok 3\nok 0\nok 0\n")), own);
    Assert.That((exitCode, LastLine(output), error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.OracleFailure, "out: executed", "polypiler: error PLY0063: no independent oracle is registered for 'cli/translate/sum32'")));
  }

  [Test]
  public void VectorsWithoutAnExpectedValue_FailTheOracle_AndRecordNoComparison() {
    var recording = this._workspace.Write("rec.vectors.json", File.ReadAllText(this._workspace.Resolve(Wrong)).Replace(""", "expected": { "hex": "04000000" }""", ""));
    var (exitCode, _, error) = this.Verify(new FakeRunner(_ => FakeRunner.Completed("ok 3\n")), recording);
    Assert.That((exitCode, error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.OracleFailure, "polypiler: error PLY0050: reference oracle 'rec.vectors.json' failed for 'cli/translate/sum32-wrong': vector 'Add(1, 2)' has no published value")));
    Assert.That(this._workspace.Manifest("out").Verification, Is.Null);
  }

  [Test]
  public void VectorsWithoutVerify_ArePLY0003() {
    var (exitCode, _, error) = this._workspace.Run("translate", Sum32, "--from", "csharp", "--to", "javascript", "--vectors", Vectors, "--output", "out");
    Assert.That((exitCode, error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.UsageError, "polypiler: error PLY0003: option '--vectors' requires --verify")));
    Assert.That(Directory.Exists(this._workspace.Resolve("out")), Is.False);
  }

  [TestCase(new[] { "--to", "csharp" }, "option '--verify' is not available for the target language 'csharp'; the command builds and runs JavaScript")]
  [TestCase(new[] { "--to", "javascript", "--module", "iife", "--global", "Lib" }, "option '--verify' takes a module: the module format 'iife' declares a global object, which the driver does not import")]
  [TestCase(new[] { "--to", "javascript", "--module", "amd" }, "unknown module format 'amd'; the module formats are 'esm', 'cjs', 'iife'")]
  public void VerifyOfAnOutputTheCommandCannotRun_IsPLY0003(string[] target, string message) {
    var runner = new FakeRunner(_ => throw new AssertionException("nothing runs"));
    var (exitCode, _, error) = this._workspace.Run(runner, ["translate", Sum32, "--from", "csharp", .. target, "--verify", "--vectors", Vectors, "--output", "out"]);
    Assert.That((exitCode, error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.UsageError, "polypiler: error PLY0003: " + message)));
    Assert.That(Directory.Exists(this._workspace.Resolve("out")), Is.False);
  }

  [Test]
  public void AMissingVectorFile_IsPLY0004() {
    var (exitCode, _, error) = this.Verify(new FakeRunner(_ => throw new AssertionException("nothing runs")), "missing.vectors.json");
    Assert.That((exitCode, error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.EnvironmentError, "polypiler: error PLY0004: input 'missing.vectors.json' does not exist or is not readable")));
  }

  [TestCase("{", "PLY0003", TestName = "A vector file that is no JSON is PLY0003")]
  [TestCase("""{ "schema": 1, "fixture": "f", "provenance": { "kind": "published", "source": "s", "detail": "d", "generatedBy": "hand" }, "export": "Add", "signature": { "parameters": [ { "name": "a", "type": "int" } ], "result": "u32" }, "vectors": [] }""", "PLY0064", TestName = "A vector file with an unknown image type is PLY0064")]
  [TestCase("""{ "schema": 1, "fixture": "f", "provenance": { "kind": "published", "source": "s", "detail": "d", "generatedBy": "hand" }, "vectors": [] }""", "PLY0003: the vector file 'bad.vectors.json' names no export and signature", TestName = "A byte-stream vector file is PLY0003")]
  [TestCase("""{ "schema": 1, "fixture": "f", "provenance": { "kind": "published", "source": "s", "detail": "d", "generatedBy": "hand" }, "export": "Add", "signature": { "parameters": [ { "name": "a", "type": "u64" } ], "result": "u32" }, "vectors": [] }""", "PLY0003: the vector file 'bad.vectors.json': the image type 'u64' is not one the JavaScript driver passes", TestName = "A signature the driver cannot pass is PLY0003")]
  [TestCase("""{ "schema": 1, "fixture": "f", "provenance": { "kind": "published", "source": "s", "detail": "d", "generatedBy": "hand" }, "export": "Add", "signature": { "parameters": [ { "name": "a", "type": "u32" } ], "result": "u32" }, "exactness": { "mode": "ulp", "bound": 1, "format": "f64", "reason": "r" }, "vectors": [] }""", "PLY0003: the vector file 'bad.vectors.json' compares within a tolerance", TestName = "A tolerance is PLY0003")]
  public void AVectorFileTheDriverCannotUse_IsRefusedBeforeAnythingIsWritten(string text, string diagnostic) {
    var bad = this._workspace.Write("bad.vectors.json", text);
    var (exitCode, _, error) = this.Verify(new FakeRunner(_ => throw new AssertionException("nothing runs")), bad);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UsageError), error);
    Assert.That(error, Does.Contain("error " + diagnostic));
    Assert.That(Directory.Exists(this._workspace.Resolve("out")), Is.False);
  }

  [Test]
  public void TheExportOfTheVectors_MustBeInTheModule() {
    var other = this._workspace.Write("mul.vectors.json", File.ReadAllText(this._workspace.Resolve(Vectors)).Replace("\"export\": \"Add\"", "\"export\": \"Mul\""));
    var (exitCode, _, error) = this.Verify(new FakeRunner(_ => throw new AssertionException("nothing runs")), other);
    Assert.That((exitCode, error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.InvalidSource, $"polypiler: error PLY0013: export 'Mul' not found in '{Sum32}'")));
    Assert.That(Directory.Exists(this._workspace.Resolve("out")), Is.False);
  }

  [Test]
  public void TheManifestCommand_RecordsTheVectorsRelativeToTheOutput() {
    this.Verify(new FakeRunner(_ => FakeRunner.Completed("ok 3\nok 0\nok 0\n")), Vectors);
    var command = this._workspace.Manifest("out").Command;
    Assert.That(command.SkipWhile(argument => argument != "--verify"), Is.SupersetOf(new[] { "--verify", "--vectors", "../" + Vectors }));
  }

  [Test, Category("EndToEnd")]
  public void TheVerifierOfTheHost_IsMadeFromTheSearchPath()
    => Assert.That(TranslationVerifier.ForHost(), Is.Not.Null);

  [Test]
  public void ACommandWithoutAVerifier_IsRefused()
    => Assert.That(() => new TranslateCommand(null!), Throws.ArgumentNullException);

  [Test]
  public void NullArguments_AreRefused() {
    var verifier = new TranslationVerifier(new FakeRunner(_ => FakeRunner.Completed("")));
    var manifest = this.Generated();
    using var context = new StringWriter();
    var cli = new CliContext(context, context, "", DiagnosticsFormat.Text, false, this._workspace.Path);
    var request = new VerificationRequest("f", null, "Add");
    Assert.That(() => verifier.Run(null!, manifest, [], "d", "d", cli), Throws.ArgumentNullException);
    Assert.That(() => verifier.Run(request, null!, [], "d", "d", cli), Throws.ArgumentNullException);
    Assert.That(() => verifier.Run(request, manifest, null!, "d", "d", cli), Throws.ArgumentNullException);
    Assert.That(() => verifier.Run(request, manifest, [], "d", "d", null!), Throws.ArgumentNullException);
  }

}
