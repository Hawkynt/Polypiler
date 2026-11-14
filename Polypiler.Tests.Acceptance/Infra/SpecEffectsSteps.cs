using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/spec-effects.feature</c>. The scenario's workspace stands in
/// for the repository: it holds the family's schema, and the documents of a scenario are written to its
/// <c>spec/effects/</c> or copied under their repository path, where the real <c>polypiler-tooling</c>
/// validates them.
/// </summary>
[Binding, Scope(Feature = "Schema and validator for spec/effects/")]
public sealed class SpecEffectsSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Valid = "tests/fixtures/spec/effects/valid/effects";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private string _fixture = "";

  /// <summary>Puts the family's schema where the tool looks for it from the workspace.</summary>
  [BeforeScenario]
  public void CopySchema() => workspace.Copy(Path.Combine(_root, "spec/schemas/effects.schema.json"), "spec/schemas/effects.schema.json");

  /// <summary>Copies a fixture tree of the repository into the workspace under the same path.</summary>
  [Given("^the spec tree " + StepText.Quoted + "$")]
  public void Tree(string tree) {
    var source = Path.Combine(_root, tree);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{tree}/{Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/')}");
  }

  /// <summary>Writes <c>spec/effects/</c>: the valid default effect, and the valid fixture document with its three permissions replaced.</summary>
  [Given("^the spec document " + StepText.Quoted + " with class " + StepText.Quoted + ", ordering " + StepText.Quoted
    + ", duplication " + StepText.Quoted + " and elimination " + StepText.Quoted + "$")]
  public void Document(string file, string effectClass, string ordering, string duplication, string elimination) {
    var lines = File.ReadAllLines(Path.Combine(_root, Valid, file));
    Assert.That(lines, Has.One.EqualTo($"  class: {effectClass}"));
    string Replace(string line) => line.TrimStart() switch {
      var text when text.StartsWith("ordering:", StringComparison.Ordinal) => $"  ordering: {ordering}",
      var text when text.StartsWith("duplication:", StringComparison.Ordinal) => $"  duplication: {duplication}",
      var text when text.StartsWith("elimination:", StringComparison.Ordinal) => $"  elimination: {elimination}",
      _ => line
    };
    workspace.Write($"spec/effects/{file}", string.Concat(lines.Select(line => Replace(line) + "\n")));
    workspace.Copy(Path.Combine(_root, Valid, "unknown-call.yaml"), "spec/effects/unknown-call.yaml");
  }

  /// <summary>Keeps the fixture the next step validates.</summary>
  [Given("^the spec fixture " + StepText.Quoted + "$")]
  public void Fixture(string fixture) {
    Assert.That(Path.Exists(Path.Combine(_root, fixture)), Is.True, fixture);
    this._fixture = fixture;
  }

  /// <summary>
  /// Writes <c>spec/effects/</c> from the kept fixture and validates it: a folder is the whole family; a
  /// file goes in next to the valid default effect, unless it declares itself the default.
  /// </summary>
  [When("^I run " + StepText.Quoted + " on a tree containing only that fixture and the valid documents it refers to$")]
  public void RunOnFixture(string line) {
    var source = Path.Combine(_root, this._fixture);
    if (Directory.Exists(source)) {
      foreach (var file in Directory.EnumerateFiles(source, "*.yaml", SearchOption.AllDirectories))
        workspace.Copy(file, "spec/effects/" + Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/'));
    } else {
      workspace.Copy(source, "spec/effects/" + Path.GetFileName(source));
      if (!File.ReadAllText(source).Contains("defaultForUnknown: true", StringComparison.Ordinal))
        workspace.Copy(Path.Combine(_root, Valid, "unknown-call.yaml"), "spec/effects/unknown-call.yaml");
    }

    var (target, arguments) = CommandSteps.Route([.. CommandLine.Split(line)]);
    Assert.That(target, Is.EqualTo(CommandTarget.Tooling), line);
    log.Add(Toolchain.Tooling(workspace.Path, arguments));
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last output line is " + StepText.Quoted + "$")]
  public void LastOutputLine(string text)
    => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n')[^1].TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

}
