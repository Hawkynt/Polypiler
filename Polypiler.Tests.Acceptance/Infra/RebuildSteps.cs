using System.IO;
using NUnit.Framework;
using Polypiler.Core.Verification.Rebuild;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Bench;
using Polypiler.Tooling.Toolchains;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-rebuild.feature</c>. The scenario's workspace is a
/// checkout as far as the check needs one: the toolchain lock, the ignore list when a scenario writes
/// one, and the generator under <c>generator/</c>, which <c>--input generator</c> copies into every run's
/// fresh working directory. The real <c>polypiler-tooling</c> runs there, in this host's sandbox.
/// </summary>
[Binding, Scope(Feature = "Deterministic rebuild check")]
public sealed class RebuildSteps(ScenarioWorkspace workspace, CommandSteps commands, CommandLog log) {

  private static readonly string[] _generatorFiles = ["polypiler-generator.dll", "polypiler-generator.runtimeconfig.json"];

  private void Checkout() {
    workspace.Copy(Path.Combine(PrototypeFixture.RepositoryRoot, ToolchainLock.RepositoryPath), ToolchainLock.RepositoryPath);
    foreach (var file in _generatorFiles)
      workspace.Copy(Path.Combine(System.AppContext.BaseDirectory, file), $"generator/{file}");
  }

  [Given("^the generator verb " + StepText.Quoted + " writing " + StepText.Quoted + " with content " + StepText.Quoted + " and an empty " + StepText.Quoted + "$")]
  public void GeneratorWriting(string verb, string file, string content, string empty) {
    Assert.That((verb, file, StepText.Unescape(content), empty), Is.EqualTo(("stable", "a.txt", "hello\n", "b/c.txt")));
    this.Checkout();
  }

  [Given("^the generator verb " + StepText.Quoted + "$")]
  public void Generator(string verb) {
    Assert.That(verb, Is.AnyOf("timestamp", "unordered", "extra", "empty", "stable", "sleep"));
    this.Checkout();
  }

  [Given("^rebuild-ignore.yaml lists the manifest pointer " + StepText.Quoted + " with reason " + StepText.Quoted + "$")]
  public void IgnoredPointer(string pointer, string reason) {
    this.Checkout();
    workspace.Write(RebuildIgnore.RepositoryPath, $"schema: 1\nfiles: []\nmanifestPointers:\n  - pointer: {pointer}\n    reason: {reason}\n");
  }

  [Given("^rebuild-ignore.yaml lists the file glob " + StepText.Quoted + " without a reason$")]
  public void IgnoredFileWithoutReason(string glob) {
    this.Checkout();
    workspace.Write(RebuildIgnore.RepositoryPath, $"schema: 1\nfiles:\n  - glob: \"{glob}\"\nmanifestPointers: []\n");
  }

  /// <summary>
  /// The build output of <c>polypiler-tooling</c>, in the configuration these tests were built in,
  /// copied into the workspace under a name: <c>--input</c> carries it into each run.
  /// </summary>
  [Given("^the polypiler-tooling build output as " + StepText.Quoted + "$")]
  public void ToolingBuildOutput(string name) {
    workspace.Copy(Path.Combine(PrototypeFixture.RepositoryRoot, ToolchainLock.RepositoryPath), ToolchainLock.RepositoryPath);
    var configuration = BenchCommand.Configuration(typeof(RebuildSteps).Assembly);
    var build = Path.Combine(PrototypeFixture.RepositoryRoot, "Polypiler.Tooling", "bin", configuration, "net10.0");
    Assert.That(File.Exists(Path.Combine(build, "polypiler-tooling.dll")), Is.True, $"polypiler-tooling was not built: {build}");
    foreach (var file in Directory.EnumerateFiles(build, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{name}/{Path.GetRelativePath(build, file).Replace('\\', '/')}");
  }

  [When("^the rebuild check runs the generator verb " + StepText.Quoted + "$")]
  public void CheckGenerator(string verb)
    => commands.Run($"rebuild check --output out --input generator -- dotnet-sdk generator/polypiler-generator.dll {verb} {{output}}");

  [Then("^stderr does not mention " + StepText.Quoted + "$")]
  public void StderrDoesNotMention(string text) => Assert.That(log.Last.Stderr, Does.Not.Contain(text), log.Last.Output);

}
