using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Cli;

/// <summary>
/// The steps of <c>tests/features/infra/cli-verify.feature</c>. Before each scenario the fixtures of
/// <c>tests/fixtures/cli/verify</c> are copied into the workspace under the same path; a scenario's
/// copy of a generated directory is <c>copy</c>.
/// Line endings are made line feeds on the way, so that the recorded hashes hold on every checkout.
/// </summary>
[Binding, Scope(Feature = "polypiler verify")]
public sealed class VerifySteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Fixtures = "tests/fixtures/cli/verify";
  private const string Copy = "copy";

  private Dictionary<string, string> _before = [];

  private string CopyPath => workspace.Resolve(Copy);

  private string ManifestPath => Path.Combine(this.CopyPath, Manifest.FileName);

  /// <summary>Copies the fixtures into the workspace.</summary>
  [BeforeScenario(Order = 100)]
  public void CopyTheFixtures() {
    foreach (var file in Directory.EnumerateFiles(Path.Combine(PrototypeFixture.RepositoryRoot, Fixtures), "*", SearchOption.AllDirectories))
      workspace.Write(Path.GetRelativePath(PrototypeFixture.RepositoryRoot, file).Replace('\\', '/'), Text(file));
  }

  private static string Text(string file) => File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);

  private Manifest Manifest {
    get {
      var bag = new DiagnosticBag();
      return ManifestReader.Read(this.ManifestPath, bag) ?? throw new AssertionException(string.Join("; ", bag.Items));
    }
  }

  [Given("^a copy of " + StepText.Quoted + "$")]
  public void ACopyOf(string directory) {
    var source = workspace.Resolve(StepText.Unescape(directory));
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Write(Path.Combine(Copy, Path.GetRelativePath(source, file)), File.ReadAllText(file));

    this._before = Hashes(this.CopyPath);
  }

  [Given("^a copy of " + StepText.Quoted + " whose manifest state is " + StepText.Quoted + "$")]
  public void ACopyInState(string directory, string state) {
    this.ACopyOf(directory);
    Assert.That(ManifestStateRule.Name(this.Manifest.State), Is.EqualTo(state));
  }

  /// <summary>Records a passing verification of the published vectors, which only <c>verified</c> supports.</summary>
  [Given("^a copy of " + StepText.Quoted + " whose manifest state has been set to \"verified\"$")]
  public void ACopySetToVerified(string directory) {
    this.ACopyOf(directory);
    ManifestWriter.Write(this.Manifest with {
      Build = new("node", "node --check adler32.js", 0),
      Execution = new("node polypiler-verify-driver.mjs adler32.js adler32", 0),
      Verification = new("../tests/fixtures/cli/verify/adler32.vectors.json", "hand", new("strict", "exact", "signed-zero", [], [], new(4, 0, 0, null)), 4, 4, 0)
    }, this.CopyPath);
    Assert.That(this.Manifest.State, Is.EqualTo(ManifestState.Verified));
    this._before = Hashes(this.CopyPath);
  }

  [Then("^the last line of stdout is " + StepText.Quoted + "$")]
  public void LastLineIs(string text)
    => Assert.That(log.Last.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

  [Then("^the manifest state is (?:still )?" + StepText.Quoted + "$")]
  public void StateIs(string state) => Assert.That(ManifestStateRule.Name(this.Manifest.State), Is.EqualTo(state), log.Last.Output);

  [Then("^the manifest state is " + StepText.Quoted + " with verification result \"failed\"$")]
  public void StateIsWithAFailure(string state) {
    this.StateIs(state);
    Assert.That(this.Manifest.Verification!.Failed, Is.GreaterThan(0));
  }

  [Then("^every file in the copy has the SHA-256 it had before the run$")]
  public void Unchanged() => Assert.That(Hashes(this.CopyPath), Is.EqualTo(this._before));

  /// <summary>The run stopped before the build: the copy is as it was, and its manifest records neither a build nor an execution.</summary>
  [Then("^nothing was built or executed$")]
  public void NothingRan() {
    this.Unchanged();
    var text = File.ReadAllText(this.ManifestPath);
    Assert.That(text, Does.Not.Contain("\"build\": {").And.Not.Contain("\"execution\""));
  }

  private static Dictionary<string, string> Hashes(string directory)
    => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(file => Path.GetRelativePath(directory, file), file => ManifestWriter.Sha256(File.ReadAllBytes(file)));

}
