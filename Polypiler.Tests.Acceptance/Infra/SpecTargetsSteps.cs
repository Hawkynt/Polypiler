using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/spec-targets.feature</c>. The scenario's workspace stands in
/// for the repository: it holds the family's schema and <c>tooling/toolchains.lock</c>, which the
/// <c>toolchain-known</c> rule reads, and the real <c>polypiler-tooling</c> validates the tree there.
/// </summary>
[Binding, Scope(Feature = "Schema and validator for spec/targets/")]
public sealed class SpecTargetsSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Valid = "tests/fixtures/spec/targets/valid";

  private const string Seed = "spec/targets/javascript/es2022/target.yaml";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private string _fixture = "";

  /// <summary>Puts the schema and the toolchain lock where the tool looks for them from the workspace.</summary>
  [BeforeScenario]
  public void CopyRepositoryFiles() {
    foreach (var path in new[] { "spec/schemas/targets.schema.json", "tooling/toolchains.lock" })
      workspace.Copy(Path.Combine(_root, path), path);
  }

  /// <summary>Copies a fixture tree into the workspace under the same relative path.</summary>
  [Given("^the spec tree " + StepText.Quoted + "$")]
  public void SpecTree(string tree) {
    var source = Path.Combine(_root, tree);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{tree}/{Relative(source, file)}");
  }

  /// <summary>Copies the valid tree to <c>spec/</c> after checking that its target has the stated pointer model.</summary>
  [Given("^the seed target with pointers " + StepText.Quoted + @" and abi.pointerBits (\d+)$")]
  public void SeedTarget(string pointers, int bits) {
    this.CopyValid(withTarget: true);
    var text = File.ReadAllText(workspace.Resolve(Seed));
    Assert.That(text, Does.Contain($"  pointers: {pointers}\n").And.Contain($"pointerBits: {bits}}}"));
  }

  /// <summary>Changes the pointer width of the seed target and runs the line.</summary>
  [When(@"^I change abi.pointerBits to (\d+) and run " + StepText.Quoted + "$")]
  public void ChangePointerBits(int bits, string line) {
    var file = workspace.Resolve(Seed);
    var text = File.ReadAllText(file);
    var edited = Regex.Replace(text, @"pointerBits: \d+\}", $"pointerBits: {bits}}}");
    Assert.That(edited, Is.Not.EqualTo(text));
    File.WriteAllText(file, edited);
    this.Run(line);
  }

  /// <summary>Remembers a fixture of <c>invalid/</c>: a document, or a folder of target documents.</summary>
  [Given("^the spec fixture " + StepText.Quoted + "$")]
  public void SpecFixture(string fixture) {
    Assert.That(Path.Exists(Path.Combine(_root, fixture)), fixture);
    this._fixture = fixture;
  }

  /// <summary>
  /// Builds <c>spec/</c> in the workspace from the language and type documents of the valid tree and
  /// the fixture — a document at the place its ID gives it, a folder as it is — and runs the line.
  /// </summary>
  [When("^I run " + StepText.Quoted + " on a tree containing only that fixture and the valid documents it refers to$")]
  public void RunOnFixture(string line) {
    this.CopyValid(withTarget: false);
    var source = Path.Combine(_root, this._fixture);
    if (File.Exists(source)) {
      var text = File.ReadAllText(source);
      var parts = Regex.Match(text, "^id: (.+)$", RegexOptions.Multiline).Groups[1].Value.Split('.', 3);
      workspace.Write($"spec/targets/{parts[1]}/{parts[2]}/target.yaml", text);
    } else {
      foreach (var file in Directory.EnumerateFiles(source, "*.yaml", SearchOption.AllDirectories))
        workspace.Copy(file, $"spec/{Relative(source, file)}");
    }

    this.Run(line);
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last output line is " + StepText.Quoted + "$")]
  public void LastOutputLine(string text)
    => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n')[^1].TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

  /// <summary>Copies the documents of the valid tree to <c>spec/</c>, its target only when asked.</summary>
  private void CopyValid(bool withTarget) {
    var source = Path.Combine(_root, Valid);
    foreach (var file in Directory.EnumerateFiles(source, "*.yaml", SearchOption.AllDirectories)) {
      var path = Relative(source, file);
      if (withTarget || !path.StartsWith("targets/", StringComparison.Ordinal))
        workspace.Copy(file, $"spec/{path}");
    }
  }

  private void Run(string line) {
    var (_, arguments) = CommandSteps.Route([.. CommandLine.Split(line)]);
    log.Add(Toolchain.Tooling(workspace.Path, arguments));
  }

  private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

}
