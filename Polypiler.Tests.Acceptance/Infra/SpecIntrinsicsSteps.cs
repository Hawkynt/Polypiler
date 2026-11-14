using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Spec;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/spec-intrinsics.feature</c>. The workspace stands in for the
/// repository: it holds the schemas of the families the valid tree of <c>tests/fixtures/spec/intrinsics/</c>
/// uses, and either a copy of that tree or a <c>spec/</c> tree assembled from its documents and one
/// fixture, which the real <c>polypiler-tooling</c> executable then validates. A fixture names its place
/// in the tree on its first line, <c># at: intrinsics/…</c>, and replaces the valid document with that
/// path or with its ID.
/// </summary>
[Binding, Scope(Feature = "Schema and validator for spec/intrinsics/")]
public sealed class SpecIntrinsicsSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Fixtures = "tests/fixtures/spec/intrinsics";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private (string At, string Text)? _fixture;

  /// <summary>Puts the schemas of the families of the valid tree where the tool looks for them from the workspace.</summary>
  [BeforeScenario]
  public void CopySchemas() {
    foreach (var family in new[] { "intrinsics", "types", "ir", "effects" })
      workspace.Copy(Path.Combine(_root, $"spec/schemas/{family}.schema.json"), $"spec/schemas/{family}.schema.json");
  }

  /// <summary>Copies a fixture tree into the workspace under the same relative path.</summary>
  [Given("^the spec tree " + StepText.Quoted + "$")]
  public void FixtureTree(string tree) {
    var source = Path.Combine(_root, tree);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{tree}/{Relative(source, file)}");
  }

  /// <summary>
  /// Writes the fixture <c>variants/sin.yaml</c> under <c>spec/</c>, after checking it against the
  /// description, with the documents of the valid tree.
  /// </summary>
  [Given("^the spec document " + StepText.Quoted + " with group " + StepText.Quoted + @" and exactness \{mode: tolerance\} without " + StepText.Quoted + "$")]
  public void InexactDocument(string path, string group, string missing) {
    var (at, text) = Read($"{Fixtures}/variants/{Path.GetFileName(path)}");
    Assert.That(at, Is.EqualTo("intrinsics/" + path));
    var body = SpecDocument.Parse(at, text).Body!;
    Assert.That(((string?)body["group"], body["exactness"]!.ToJsonString()), Is.EqualTo(((string?)group, """{"mode":"tolerance"}""")));
    Assert.That(body["exactness"]![missing], Is.Null);
    this.WriteTree(at, text);
  }

  /// <summary>Keeps an invalid fixture.</summary>
  [Given("^the spec fixture " + StepText.Quoted + "$")]
  public void InvalidFixture(string path) => this._fixture = Read(path);

  /// <summary>Writes the kept fixture and the valid documents it does not replace under <c>spec/</c>, then runs the command line.</summary>
  [When("^I run " + StepText.Quoted + " on a tree containing only that fixture and the valid documents it refers to$")]
  public void RunWithFixture(string line) {
    var (at, text) = this._fixture!.Value;
    this.WriteTree(at, text);
    var (target, arguments) = CommandSteps.Route([.. CommandLine.Split(StepText.Unescape(line))]);
    Assert.That(target, Is.EqualTo(CommandTarget.Tooling));
    log.Add(Toolchain.Tooling(workspace.Path, arguments));
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last output line is " + StepText.Quoted + "$")]
  public void LastOutputLine(string text)
    => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n')[^1].TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

  /// <summary>Writes under <c>spec/</c> the valid documents except the one a document replaces by path or by ID, and the document at its place.</summary>
  private void WriteTree(string at, string text) {
    var id = Id(text);
    var valid = Path.Combine(_root, Fixtures, "valid");
    foreach (var file in Directory.EnumerateFiles(valid, "*.yaml", SearchOption.AllDirectories)) {
      var path = Relative(valid, file);
      if (path != at && Id(File.ReadAllText(file)) != id)
        workspace.Copy(file, "spec/" + path);
    }
    workspace.Write("spec/" + at, text);
  }

  /// <summary>A fixture file: its place in the tree from the <c># at:</c> line, and its text.</summary>
  private static (string At, string Text) Read(string path) {
    var text = File.ReadAllText(Path.Combine(_root, path));
    var at = Lines(text).First(line => line.StartsWith("# at: ", StringComparison.Ordinal))["# at: ".Length..];
    return (at, text);
  }

  private static string Id(string text) => Lines(text).First(line => line.StartsWith("id: ", StringComparison.Ordinal))["id: ".Length..];

  private static IEnumerable<string> Lines(string text) => text.Split('\n').Select(line => line.TrimEnd('\r'));

  private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

}
