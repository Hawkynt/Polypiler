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
/// The steps of <c>tests/features/infra/spec-languages.feature</c>. The scenario's workspace stands in
/// for the repository: it holds the family's schema and <c>tooling/toolchains.lock</c>, which the
/// <c>toolchain-known</c> rule reads, and the real <c>polypiler-tooling</c> validates the tree there.
/// </summary>
[Binding, Scope(Feature = "Schema and validator for spec/languages/")]
public sealed class SpecLanguagesSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Valid = "tests/fixtures/spec/languages/valid";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private string _fixture = "";

  /// <summary>Puts the schema and the toolchain lock where the tool looks for them from the workspace.</summary>
  [BeforeScenario]
  public void CopyRepositoryFiles() {
    foreach (var path in new[] { "spec/schemas/languages.schema.json", "tooling/toolchains.lock" })
      workspace.Copy(Path.Combine(_root, path), path);
  }

  /// <summary>Copies a fixture tree into the workspace under the same relative path.</summary>
  [Given("^the spec tree " + StepText.Quoted + "$")]
  public void SpecTree(string tree) {
    var source = Path.Combine(_root, tree);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{tree}/{Relative(source, file)}");
  }

  /// <summary>Writes one language document per identifier into <c>spec/languages/</c>, its backlog code the identifier in upper case.</summary>
  [Given("^language documents for the identifiers " + StepText.Quoted + ", " + StepText.Quoted + ", " + StepText.Quoted + " and " + StepText.Quoted
    + @" with diagnostic blocks (\d+), (\d+), (\d+) and (\d+)$")]
  public void LanguageDocuments(string first, string second, string third, string fourth, int firstBlock, int secondBlock, int thirdBlock, int fourthBlock) {
    var languages = new[] { (first, firstBlock), (second, secondBlock), (third, thirdBlock), (fourth, fourthBlock) };
    foreach (var (identifier, block) in languages)
      workspace.Write($"spec/languages/{identifier}/language.yaml", $"""
        id: LANGUAGES.{identifier}
        title: {identifier}
        version: 1
        status: draft
        owner: Hawkynt
        issue: "#54"
        tests: []
        introducedIn: P0
        compatibility: none
        reviewedRevision: 0
        normative: ""
        spec:
          kind: language
          identifier: {identifier}
          displayName: {identifier}
          backlogCode: {identifier.ToUpperInvariant()}
          diagnosticBlock: {block}

        """);
  }

  /// <summary>Remembers a fixture of <c>invalid/</c>: a document, or a folder that is a whole spec tree.</summary>
  [Given("^the spec fixture " + StepText.Quoted + "$")]
  public void SpecFixture(string fixture) {
    Assert.That(Path.Exists(Path.Combine(_root, fixture)), fixture);
    this._fixture = fixture;
  }

  /// <summary>
  /// Builds <c>spec/</c> in the workspace from the fixture — a document at the place its ID gives it,
  /// with the valid language of the profile's folder beside it; a folder as it is — and runs the line.
  /// </summary>
  [When("^I run " + StepText.Quoted + " on a tree containing only that fixture and the valid documents it refers to$")]
  public void RunOnFixture(string line) {
    var source = Path.Combine(_root, this._fixture);
    if (File.Exists(source)) {
      var text = File.ReadAllText(source);
      var parts = Regex.Match(text, "^id: (.+)$", RegexOptions.Multiline).Groups[1].Value.Split('.', 3);
      if (parts.Length == 3)
        workspace.Copy(Path.Combine(_root, Valid, "languages", parts[1], "language.yaml"), $"spec/languages/{parts[1]}/language.yaml");
      workspace.Write(parts.Length == 2 ? $"spec/languages/{parts[1]}/language.yaml" : $"spec/languages/{parts[1]}/profiles/{parts[2]}.yaml", text);
    } else {
      foreach (var file in Directory.EnumerateFiles(source, "*.yaml", SearchOption.AllDirectories))
        workspace.Copy(file, $"spec/{Relative(source, file)}");
    }

    var (_, arguments) = CommandSteps.Route([.. CommandLine.Split(line)]);
    log.Add(Toolchain.Tooling(workspace.Path, arguments));
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last output line is " + StepText.Quoted + "$")]
  public void LastOutputLine(string text)
    => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n')[^1].TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

  private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

}
