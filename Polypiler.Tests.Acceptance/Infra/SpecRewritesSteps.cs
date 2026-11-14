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
/// The steps of <c>tests/features/infra/spec-rewrites.feature</c>. The scenario's workspace stands in for
/// the repository: it holds the family's schema and this feature file, the test path a normative fixture
/// names. A fixture tree is copied under its own relative path; a single invalid fixture goes to
/// <c>spec/rewrites/</c> under the file name its ID gives, a fixture folder with its files as they are,
/// each with the valid documents its interactions name.
/// </summary>
[Binding, Scope(Feature = "Schema and validator for spec/rewrites/")]
public sealed class SpecRewritesSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Valid = "tests/fixtures/spec/rewrites/valid";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private string _fixture = "";

  /// <summary>Puts what the documents rely on where the tool looks for it from the workspace.</summary>
  [BeforeScenario]
  public void CopyRepositoryFiles() {
    foreach (var path in new[] { "spec/schemas/rewrites.schema.json", "tests/features/infra/spec-rewrites.feature" })
      workspace.Copy(Path.Combine(_root, path), path);
  }

  /// <summary>Copies a fixture tree under its own relative path.</summary>
  [Given("^the spec tree " + StepText.Quoted + "$")]
  public void FixtureTree(string tree) {
    var source = Path.Combine(_root, tree);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{tree}/{Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/')}");
  }

  /// <summary>Writes <c>spec/rewrites/&lt;file&gt;</c>: a draft rewrite with the given forms, floating-point flags and proof class.</summary>
  [Given("^a rewrite document " + StepText.Quoted + " turning " + StepText.Quoted + " into " + StepText.Quoted
    + @" with floatingPoint \{touchesFloat: (true|false), reassociates: (true|false), contracts: (true|false)\} and proof class " + StepText.Quoted + "$")]
  public void RewriteDocument(string file, string before, string after, string touchesFloat, string reassociates, string contracts, string proof) {
    var optimization = Path.GetFileNameWithoutExtension(file);
    workspace.Write($"spec/rewrites/{file}", $"""
      id: REWRITES.{optimization}
      title: Cancel a term added and subtracted again
      version: 1
      status: draft
      owner: Hawkynt
      issue: "#59"
      tests: []
      introducedIn: P1
      compatibility: none
      reviewedRevision: 0
      normative: ""
      spec:
        optimization: {optimization}
        kind: canonicalize
        before: "{before}"
        after: "{after}"
        predicates: []
        proof: {"{"}class: {proof}{"}"}
        floatingPoint: {"{"}touchesFloat: {touchesFloat}, reassociates: {reassociates}, contracts: {contracts}{"}"}
        negativeFixtures: [tests/fixtures/optimize/{optimization.ToLowerInvariant()}/cancellation.pir]
        switch: "--disable-opt {optimization}"
        optLevels: [balanced, speed, size]
        interactions: {"{"}after: [], enables: []{"}"}
        document: docs/optimizations/{optimization}-cancel-added-term.md

      """);
  }

  /// <summary>Remembers an invalid fixture, a file or a folder, after checking that it exists.</summary>
  [Given("^the spec fixture " + StepText.Quoted + "$")]
  public void Fixture(string fixture) {
    Assert.That(Path.Exists(Path.Combine(_root, fixture)), Is.True, fixture);
    this._fixture = fixture;
  }

  /// <summary>Writes the remembered fixture and the valid documents it refers to into <c>spec/rewrites/</c>, and runs the command line there.</summary>
  [When("^I run " + StepText.Quoted + " on a tree containing only that fixture and the valid documents it refers to$")]
  public void RunOnFixture(string line) {
    var source = Path.Combine(_root, this._fixture);
    var files = Directory.Exists(source)
      ? Directory.EnumerateFiles(source, "*.yaml", SearchOption.AllDirectories).Select(file => (Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/'), file)).ToList()
      : [($"{SpecDocument.Parse("rewrites/fixture.yaml", File.ReadAllText(source)).Id!["REWRITES.".Length..]}.yaml", source)];
    foreach (var (name, file) in files)
      workspace.Copy(file, $"spec/rewrites/{name}");

    foreach (var name in files.SelectMany(file => Referenced(file.Item2)).Distinct())
      if (!files.Any(file => file.Item1 == $"{name}.yaml") && File.Exists(Path.Combine(_root, Valid, "rewrites", $"{name}.yaml")))
        workspace.Copy(Path.Combine(_root, Valid, "rewrites", $"{name}.yaml"), $"spec/rewrites/{name}.yaml");

    log.Add(Toolchain.Tooling(workspace.Path, [.. CommandLine.Split(StepText.Unescape(line)).Skip(1)]));
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last output line is " + StepText.Quoted + "$")]
  public void LastOutputLine(string text)
    => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n')[^1].TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

  /// <summary>The optimizations a rewrite document names under <c>interactions</c>.</summary>
  private static IEnumerable<string> Referenced(string file)
    => SpecDocument.Parse("rewrites/fixture.yaml", File.ReadAllText(file)).Body?["interactions"] is JsonObject interactions
      ? new[] { "after", "enables" }.SelectMany(list => interactions[list] as JsonArray ?? []).Select(value => (string)value!)
      : [];

}
