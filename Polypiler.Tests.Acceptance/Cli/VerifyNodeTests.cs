using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Cli.Verify;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Cli;

/// <summary>
/// The rows of the test design of <c>polypiler verify</c> that need node itself (#181): a module that
/// does not parse, one that never ends, the evidence bundle and the vectors the manifest records. They
/// run <see cref="ArtefactVerifier"/> in process on node from <c>PATH</c>, in the sandbox of this host.
/// </summary>
[TestFixture, Category("ExternalInterop")]
[Layer("core")]
public sealed class VerifyNodeTests {

  private string _root = "";
  private string _copy = "";
  private StringWriter _output = null!;
  private DiagnosticBag _diagnostics = null!;

  private static string Fixture(string name) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "cli", "verify", name);

  [SetUp]
  public void CopyTheFixture() {
    this._root = Path.Combine(Path.GetTempPath(), "polypiler-verify-node-" + Guid.NewGuid().ToString("N"));
    this._copy = Path.Combine(this._root, "copy");
    Directory.CreateDirectory(this._copy);
    foreach (var file in Directory.EnumerateFiles(Fixture("adler32-js")))
      File.WriteAllText(Path.Combine(this._copy, Path.GetFileName(file)), File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal));
    foreach (var name in new[] { "adler32.vectors.json", "adler32.wrong.vectors.json" })
      File.Copy(Fixture(name), Path.Combine(this._root, name));

    this._output = new StringWriter();
    this._diagnostics = new DiagnosticBag();
  }

  [TearDown]
  public void DeleteTheCopy() {
    this._output.Dispose();
    Directory.Delete(this._root, true);
  }

  private VerificationOutcome Verify(string? vectors = "adler32.vectors.json", RunLimits? limits = null, string? evidence = null) {
    var toolchains = PathToolchainResolver.FromEnvironment();
    var runner = new CrossLanguageRunner(SandboxFactory.ForHost(toolchains), toolchains);
    return new ArtefactVerifier(this._output, this._root, runner, limits ?? RunLimits.Default).Run(new VerifyRequest("copy", vectors, true, evidence), this._diagnostics);
  }

  private Manifest Manifest {
    get {
      var bag = new DiagnosticBag();
      return ManifestReader.Read(Path.Combine(this._copy, Manifest.FileName), bag)!;
    }
  }

  /// <summary>Replaces the module and records its hash, as if the compiler had written it.</summary>
  private void Generated(string module) {
    File.WriteAllText(Path.Combine(this._copy, "adler32.js"), module);
    var manifest = this.Manifest;
    ManifestWriter.Write(manifest with { Outputs = [manifest.Outputs[0] with { Sha256 = ManifestWriter.Sha256(File.ReadAllBytes(Path.Combine(this._copy, "adler32.js"))) }] }, this._copy);
  }

  // Row 1 with node, and row 18: the bundle holds the version node reported.
  [Test, RequiresToolchain("node")]
  public void TheFixture_IsVerifiedByNode_WithEvidence() {
    var outcome = this.Verify(evidence: "evidence");

    Assert.That(outcome, Is.EqualTo(new VerificationOutcome(ManifestState.Verified, 4, 4)), string.Join("\n", this._diagnostics.Items));
    var tree = JsonNode.Parse(File.ReadAllText(Path.Combine(this._root, "evidence", "polypiler-verify", "copy", "evidence.json")))!;
    var version = tree["toolVersions"]!["node"]!.GetValue<string>();
    Assert.That(version, Does.Match(@"^\d+\.\d+\.\d+"));
    Assert.That(this.Manifest.Toolchains, Is.EqualTo(new[] { new ManifestToolchain("node", version) }));
  }

  // Row 3 with node: the second run takes the vectors the first one recorded.
  [Test, RequiresToolchain("node")]
  public void TheRecordedVectors_AreUsedWithoutAgainst() {
    this.Verify("adler32.wrong.vectors.json");
    this._output.GetStringBuilder().Clear();

    var outcome = this.Verify(vectors: null);

    Assert.That(outcome, Is.EqualTo(new VerificationOutcome(ManifestState.Executed, 0, 1)));
    Assert.That(this._output.ToString(), Does.Contain("  abc: FAIL expected 0x024D0128 actual 0x024D0127"));
  }

  // Row 14: a module with a syntax error and a matching hash.
  [Test, RequiresToolchain("node")]
  public void AModuleThatDoesNotParse_IsATargetBuildFailure() {
    this.Generated("function adler32(bytes) {\n  return (;\n}\nmodule.exports = { adler32 };\n");

    var outcome = this.Verify();

    Assert.That(outcome.Reached, Is.EqualTo(ManifestState.Generated));
    Assert.That(this._diagnostics.Items.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0030" }));
    Assert.That(this._diagnostics.Items[0].Cause, Does.Contain("SyntaxError"));
    Assert.That(this._diagnostics.ExitCode, Is.EqualTo(ExitCode.TargetBuildFailure));
    Assert.That(this.Manifest.Build!.ExitCode, Is.Not.Zero);
  }

  // Row 15: an endless loop under a time limit.
  [Test, RequiresToolchain("node")]
  public void AModuleThatNeverReturns_ExceedsTheTimeLimit_AndStaysBuilt() {
    this.Generated("function adler32(bytes) {\n  for (;;) {}\n}\nmodule.exports = { adler32 };\n");

    var outcome = this.Verify(limits: new RunLimits(TimeSpan.FromSeconds(2), RunLimits.Default.MemoryBytes, RunLimits.Default.OutputBytes, RunLimits.Default.Processes));

    Assert.That(outcome.Reached, Is.EqualTo(ManifestState.Built));
    Assert.That(this._diagnostics.Items.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0031" }));
    Assert.That(this._diagnostics.ExitCode, Is.EqualTo(ExitCode.BudgetExceeded));
    Assert.That(this.Manifest.State, Is.EqualTo(ManifestState.Built));
  }

  // A module that does not export the function the vectors name.
  [Test, RequiresToolchain("node")]
  public void AModuleWithoutTheExport_StaysBuilt() {
    this.Generated("module.exports = { other() { return 1; } };\n");

    Assert.That(this.Verify().Reached, Is.EqualTo(ManifestState.Built));
    Assert.That(this._diagnostics.Items.Single().Cause, Does.Contain("exports no function 'adler32'"));
  }

  // An ES module, imported the same way.
  [Test, RequiresToolchain("node")]
  public void AnEsModule_IsVerifiedToo() {
    var manifest = this.Manifest;
    File.Move(Path.Combine(this._copy, "adler32.js"), Path.Combine(this._copy, "adler32.mjs"));
    File.WriteAllText(Path.Combine(this._copy, "adler32.mjs"), File.ReadAllText(Path.Combine(this._copy, "adler32.mjs")).Replace("'use strict';\n", "").Replace("module.exports = { adler32 };", "export { adler32 };"));
    ManifestWriter.Write(manifest with { Outputs = [new("adler32.mjs", ManifestWriter.Sha256(File.ReadAllBytes(Path.Combine(this._copy, "adler32.mjs"))), ManifestOutputRole.Source)] }, this._copy);

    Assert.That(this.Verify(), Is.EqualTo(new VerificationOutcome(ManifestState.Verified, 4, 4)), string.Join("\n", this._diagnostics.Items));
  }

}
