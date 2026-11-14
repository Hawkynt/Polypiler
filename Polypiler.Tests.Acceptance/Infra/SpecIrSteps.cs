using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Spec;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/spec-ir.feature</c>. The workspace stands in for the
/// repository: it holds the ir family's schema, and either a copy of a fixture tree of
/// <c>tests/fixtures/spec/ir/</c> or a <c>spec/</c> tree assembled from the valid documents and one
/// fixture, which the real <c>polypiler-tooling</c> executable then validates. A fixture names its place
/// in the tree on its first line, <c># at: ir/…</c>, and replaces the valid document with that path or
/// with its ID.
/// </summary>
[Binding, Scope(Feature = "Schema and validator for spec/ir/")]
public sealed class SpecIrSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Fixtures = "tests/fixtures/spec/ir";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private (string At, string Text)? _fixture;

  /// <summary>Puts the family schema where the tool looks for it from the workspace.</summary>
  [BeforeScenario]
  public void CopySchema() => workspace.Copy(Path.Combine(_root, "spec/schemas/ir.schema.json"), "spec/schemas/ir.schema.json");

  /// <summary>Copies a fixture tree into the workspace under the same relative path.</summary>
  [Given("^the spec tree " + StepText.Quoted + "$")]
  public void FixtureTree(string tree) {
    var source = Path.Combine(_root, tree);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{tree}/{Relative(source, file)}");
  }

  /// <summary>Keeps the fixture <c>variants/unreachable.yaml</c> after checking it against the description.</summary>
  [Given("^the spec document " + StepText.Quoted + " with kind " + StepText.Quoted + ", mnemonic " + StepText.Quoted + ", category " + StepText.Quoted
    + @", no operands, result null, flags \[\], traps \[\{when: ""always"", kind: unreachable\}\] and one example \{operands: \[\], trap: unreachable\}$")]
  public void UnreachableDocument(string path, string kind, string mnemonic, string category) {
    var fixture = Read($"{Fixtures}/variants/{Path.GetFileName(path)}");
    Assert.That(fixture.At, Is.EqualTo("ir/" + path));
    var body = SpecDocument.Parse(fixture.At, fixture.Text).Body!;
    Assert.That(((string?)body["kind"], (string?)body["mnemonic"], (string?)body["category"]), Is.EqualTo((kind, mnemonic, category)));
    Assert.That((body["operands"]!.AsArray().Count, body["result"], body["flags"]!.AsArray().Count), Is.EqualTo((0, (JsonNode?)null, 0)));
    Assert.That(body["traps"]!.ToJsonString(), Is.EqualTo("""[{"when":"always","kind":"unreachable"}]"""));
    Assert.That(body["examples"]!.ToJsonString(), Is.EqualTo("""[{"operands":[],"trap":"unreachable"}]"""));
    this._fixture = fixture;
  }

  /// <summary>Writes the kept document and the two valid stage documents under <c>spec/</c>, then runs the command line.</summary>
  [When("^I run " + StepText.Quoted + " on a tree with that document and the two stage documents$")]
  public void RunWithStages(string line) {
    var (at, text) = this._fixture!.Value;
    workspace.Write("spec/" + at, text);
    foreach (var stage in new[] { "ir/stages/source-semantic.yaml", "ir/stages/legalized-target.yaml" })
      workspace.Copy(Path.Combine(_root, Fixtures, "valid", stage), "spec/" + stage);
    this.Run(line);
  }

  /// <summary>Keeps an invalid fixture.</summary>
  [Given("^the spec fixture " + StepText.Quoted + "$")]
  public void InvalidFixture(string path) => this._fixture = Read(path);

  /// <summary>
  /// Writes under <c>spec/</c> the valid documents except the one the kept fixture replaces, by path or
  /// by ID, and the fixture at its place; then runs the command line.
  /// </summary>
  [When("^I run " + StepText.Quoted + " on a tree containing only that fixture and the valid documents it refers to$")]
  public void RunWithFixture(string line) {
    var (at, text) = this._fixture!.Value;
    var id = Id(text);
    var valid = Path.Combine(_root, Fixtures, "valid");
    foreach (var file in Directory.EnumerateFiles(valid, "*.yaml", SearchOption.AllDirectories)) {
      var path = Relative(valid, file);
      if (path != at && Id(File.ReadAllText(file)) != id)
        workspace.Copy(file, "spec/" + path);
    }
    workspace.Write("spec/" + at, text);
    this.Run(line);
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last output line is " + StepText.Quoted + "$")]
  public void LastOutputLine(string text)
    => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n')[^1].TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

  private void Run(string line) {
    var (target, arguments) = CommandSteps.Route([.. CommandLine.Split(StepText.Unescape(line))]);
    Assert.That(target, Is.EqualTo(CommandTarget.Tooling));
    log.Add(Toolchain.Tooling(workspace.Path, arguments));
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
