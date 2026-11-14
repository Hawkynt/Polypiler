using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Spec;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/spec-meta.feature</c>. A fixture tree of
/// <c>tests/fixtures/spec/meta/</c> is copied into the scenario's workspace under the same relative
/// path, where the real <c>polypiler-tooling</c> executable validates it; the workspace stands in for
/// the repository, so it also holds the demo family's schema and this feature file, the test path the
/// fixture documents name. A step that describes a document checks the fixture against the description.
/// </summary>
[Binding, Scope(Feature = "Spec metadata rules and shared validator")]
public sealed class SpecMetaSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Fixtures = "tests/fixtures/spec/meta";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private string _line = "";

  /// <summary>Puts what the documents rely on where the tool looks for it from the workspace.</summary>
  [BeforeScenario]
  public void CopyRepositoryFiles() {
    foreach (var path in new[] { "spec/schemas/demo.schema.json", "tests/features/infra/spec-meta.feature" })
      workspace.Copy(Path.Combine(_root, path), path);
  }

  /// <summary>Copies the fixture tree <c>valid</c> after checking its document against the description.</summary>
  [Given("^the spec document " + StepText.Quoted + " with id " + StepText.Quoted + @", version (\d+), status " + StepText.Quoted
    + @", reviewedRevision (\d+), introducedIn " + StepText.Quoted + ", compatibility " + StepText.Quoted + @" and tests \[" + StepText.Quoted + @"\]$")]
  public void CompleteDocument(string path, string id, int version, string status, int reviewedRevision, string introducedIn, string compatibility, string test) {
    var meta = this.Tree("valid").Documents.Single(document => document.Path == path).Meta!;
    Assert.That((meta.Id, meta.Version, meta.Status, meta.ReviewedRevision, meta.IntroducedIn, meta.Compatibility, meta.Tests.Single()),
      Is.EqualTo((id, (long)version, status, (long)reviewedRevision, introducedIn, compatibility, test)));
  }

  /// <summary>Copies a fixture tree.</summary>
  [Given("^the spec fixture tree " + StepText.Quoted + "$")]
  public void FixtureTree(string tree) => this.Tree(tree);

  /// <summary>Copies the fixture tree <c>bad-type</c> after checking the line that holds the value.</summary>
  [Given("^the spec document " + StepText.Quoted + " whose " + StepText.Quoted + @" on line (\d+) is the string " + StepText.Quoted + "$")]
  public void DocumentWithString(string path, string key, int line, string value) {
    this.Tree("bad-type");
    Assert.That(File.ReadAllLines(workspace.Resolve($"{Fixtures}/bad-type/{path}"))[line - 1], Is.EqualTo($"{key}: {value}"));
  }

  /// <summary>Copies the fixture tree <c>broken-yaml</c> after checking that the document fails on the line.</summary>
  [Given("^the spec document " + StepText.Quoted + @" that is not valid YAML on line (\d+)$")]
  public void BrokenDocument(string path, int line) {
    var tree = this.Tree("broken-yaml");
    Assert.That(tree.Unreadable.Single().Line, Is.EqualTo(line));
    Assert.That(tree.Unreadable.Single().File, Is.EqualTo(path));
  }

  /// <summary>Writes <c>spec/</c> with the valid demo document and a document of another family that is no YAML at all.</summary>
  [Given("^a spec tree with a valid family " + StepText.Quoted + " and an invalid family " + StepText.Quoted + "$")]
  public void TwoFamilies(string valid, string invalid) {
    workspace.Copy(Path.Combine(_root, Fixtures, "valid", valid, "widget.yaml"), $"spec/{valid}/widget.yaml");
    workspace.Write($"spec/{invalid}/thing.yaml", "id: [\n");
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last output line is " + StepText.Quoted + "$")]
  public void LastOutputLine(string text)
    => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n')[^1].TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

  /// <summary>Checks that exactly one line of standard error contains the text, and keeps it.</summary>
  [Then(@"^stderr contains exactly (\d+) line with " + StepText.Quoted + "$")]
  public void StderrLines(int count, string text) {
    var lines = log.Last.Stderr.Split('\n').Where(line => line.Contains(StepText.Unescape(text), StringComparison.Ordinal)).ToArray();
    Assert.That(lines, Has.Length.EqualTo(count), log.Last.Output);
    this._line = lines[0];
  }

  /// <summary>Checks the line the previous step kept.</summary>
  [Then("^that line contains " + StepText.Quoted + "$")]
  public void ThatLineContains(string text) => Assert.That(this._line, Does.Contain(StepText.Unescape(text)));

  /// <summary>Copies a fixture tree into the workspace and reads it there.</summary>
  private SpecTree Tree(string tree) {
    var source = Path.Combine(_root, Fixtures, tree);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{Fixtures}/{tree}/{Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/')}");
    return SpecTree.Load(workspace.Resolve($"{Fixtures}/{tree}"), workspace.Path);
  }

}
