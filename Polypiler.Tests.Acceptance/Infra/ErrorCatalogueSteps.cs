using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Diagnostics;
using Polypiler.Tooling.Report;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-docs-errors.feature</c>. The registry
/// <c>tests/fixtures/infra/docs-errors/registry.yaml</c> and its notes folders are copied into the
/// scenario's workspace as <c>registry.yaml</c>, <c>notes-valid/</c> and <c>notes/</c>; the command
/// lines of the feature run the real <c>polypiler-tooling</c> there, and "the catalogue is generated"
/// runs the same command in process.
/// </summary>
[Binding, Scope(Feature = "Error catalogue generated from the diagnostics registry")]
public sealed class ErrorCatalogueSteps(ScenarioWorkspace workspace) {

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "docs-errors");

  private Dictionary<string, (DateTime Written, string Text)> _snapshot = [];
  private string _sentence = "";

  /// <summary>Copies the registry fixture and the valid notes into the workspace and checks the codes it registers.</summary>
  [Given(@"^the registry fixture with the codes (PLY\d{4}(?:, PLY\d{4})*) and the retired (PLY\d{4})$")]
  public void RegistryFixture(string codes, string retired) {
    var registry = this.CopyFixture();
    Assert.That(registry.Codes.Select(entry => entry.Code), Is.EqualTo(codes.Split(", ").Append(retired)));
    Assert.That(registry.Codes.Where(entry => entry.Retired).Select(entry => entry.Code), Is.EqualTo(new[] { retired }));
  }

  /// <summary>Copies the registry fixture and the valid notes into the workspace.</summary>
  [Given("^the same registry fixture$")]
  public void SameRegistryFixture() => this.CopyFixture();

  /// <summary>Copies the fixture and checks that the note of a code has a sentence under a section.</summary>
  [Given("^a notes file " + StepText.Quoted + " with the section " + StepText.Quoted + " containing " + StepText.Quoted + "$")]
  public void NotesFile(string name, string section, string sentence) {
    this.CopyFixture();
    this._sentence = StepText.Unescape(sentence);
    Assert.That(SectionOf(File.ReadAllText(workspace.Resolve("notes-valid/" + name)), StepText.Unescape(section)), Does.Contain(this._sentence));
  }

  /// <summary>Copies the fixture, generates the catalogue into a directory and records its files.</summary>
  [Given("^a generated catalogue in " + StepText.Quoted + "$")]
  public void GeneratedCatalogue(string directory) {
    this.CopyFixture();
    this.Generate(directory);
    this._snapshot = Directory.GetFiles(workspace.Resolve(directory)).ToDictionary(path => path, path => (File.GetLastWriteTimeUtc(path), File.ReadAllText(path)));
    Assert.That(this._snapshot, Is.Not.Empty);
  }

  /// <summary>Changes the message of a code in the workspace's registry.</summary>
  [Given(@"^the registry message of (PLY\d{4}) is changed to " + StepText.Quoted + "$")]
  public void MessageChanged(string code, string message) {
    var path = workspace.Resolve("registry.yaml");
    var pattern = new Regex($"(code: {code}\\n(?:    .*\\n)*?    message: ).*\\n");
    var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
    Assert.That(pattern.IsMatch(text), Is.True, $"{code} has no message in the registry fixture");
    File.WriteAllText(path, pattern.Replace(text, match => match.Groups[1].Value + "\"" + StepText.Unescape(message) + "\"\n", 1));
    Assert.That(DiagnosticRegistryValidator.Validate(path).Find(code)?.Message, Is.EqualTo(StepText.Unescape(message)));
  }

  /// <summary>Copies the fixture with the notes folder that holds a note of an unregistered code.</summary>
  [Given("^the notes folder also contains " + StepText.Quoted + "$")]
  public void NotesFolderAlsoContains(string name) {
    this.CopyFixture();
    Assert.That(File.Exists(workspace.Resolve("notes/" + name)), Is.True, name);
  }

  /// <summary>Generates the catalogue into <c>out</c> in process.</summary>
  [When("^the catalogue is generated$")]
  public void CatalogueGenerated() => this.Generate("out");

  /// <summary>Checks the files of a directory.</summary>
  [Then("^the directory " + StepText.Quoted + " contains exactly ((?:\"[^\"]+\"(?:, | and )?)+)$")]
  public void DirectoryContainsExactly(string directory, string names)
    => Assert.That(Directory.GetFiles(workspace.Resolve(directory)).Select(Path.GetFileName),
      Is.EquivalentTo(Regex.Matches(names, "\"([^\"]+)\"").Select(match => match.Groups[1].Value)));

  /// <summary>Checks that a directory holds no file, or does not exist.</summary>
  [Then("^the directory " + StepText.Quoted + " contains no file$")]
  public void DirectoryContainsNoFile(string directory) {
    var path = workspace.Resolve(directory);
    Assert.That(Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories) : [], Is.Empty);
  }

  /// <summary>Checks that a file has a line equal to the row.</summary>
  [Then("^" + StepText.Quoted + " contains the row " + StepText.Quoted + "$")]
  public void ContainsRow(string file, string row)
    => Assert.That(this.Lines(file), Does.Contain(StepText.Unescape(row)));

  /// <summary>Checks that a file has a line that contains the fragment.</summary>
  [Then("^" + StepText.Quoted + " contains the row fragment " + StepText.Quoted + "$")]
  public void ContainsRowFragment(string file, string fragment)
    => Assert.That(this.Lines(file), Has.Some.Contains(StepText.Unescape(fragment)));

  /// <summary>Checks the exit code column of a code's row in the index.</summary>
  [Then(@"^the index row of (PLY\d{4}) shows the exit code " + StepText.Quoted + "$")]
  public void IndexRowExitCode(string code, string exitCode) {
    var row = this.Lines("out/" + ErrorCatalogueWriter.IndexName).Single(line => line.StartsWith($"| [{code}]", StringComparison.Ordinal));
    Assert.That(row.Split(" | ")[2], Is.EqualTo(StepText.Unescape(exitCode)));
  }

  /// <summary>Checks the status of a page.</summary>
  [Then("^the page " + StepText.Quoted + " has the status " + StepText.Quoted + "$")]
  public void PageStatus(string page, string status)
    => Assert.That(this.Lines("out/" + page), Does.Contain($"| Status | {StepText.Unescape(status)} |"));

  /// <summary>Checks that a page has the sentence of the notes file under a section.</summary>
  [Then("^" + StepText.Quoted + " contains that sentence under " + StepText.Quoted + "$")]
  public void ContainsThatSentence(string page, string section) => this.ContainsUnder(page, this._sentence, section);

  /// <summary>Checks that a page has a text under a section.</summary>
  [Then("^" + StepText.Quoted + " contains " + StepText.Quoted + " under " + StepText.Quoted + "$")]
  public void ContainsUnder(string page, string text, string section)
    => Assert.That(SectionOf(File.ReadAllText(workspace.Resolve("out/" + page)), StepText.Unescape(section)), Does.Contain(StepText.Unescape(text)));

  /// <summary>Checks that the files recorded by the generation are unchanged and that no file was added.</summary>
  [Then("^no file in " + StepText.Quoted + " was modified$")]
  public void NoFileModified(string directory)
    => Assert.That(Directory.GetFiles(workspace.Resolve(directory)).ToDictionary(path => path, path => (File.GetLastWriteTimeUtc(path), File.ReadAllText(path))),
      Is.EquivalentTo(this._snapshot));

  /// <summary>Copies the registry and both notes folders of the fixture into the workspace and reads the registry.</summary>
  private DiagnosticRegistry CopyFixture() {
    workspace.Copy(Path.Combine(_fixtures, "registry.yaml"), "registry.yaml");
    foreach (var folder in new[] { "notes-valid", "notes" })
      foreach (var file in Directory.GetFiles(Path.Combine(_fixtures, folder)))
        workspace.Copy(file, folder + "/" + Path.GetFileName(file));

    var registry = DiagnosticRegistryValidator.Validate(workspace.Resolve("registry.yaml"));
    Assert.That(registry.Problems, Is.Empty);
    return registry;
  }

  private void Generate(string directory) {
    var (output, error) = (new StringWriter(), new StringWriter());
    var exitCode = ErrorCatalogueCommand.Run(["errors", "--registry", "registry.yaml", "--notes", "notes-valid", "--out", directory], output, error, workspace.Path);
    Assert.That(exitCode, Is.Zero, error.ToString());
  }

  private string[] Lines(string file) => File.ReadAllText(workspace.Resolve(StepText.Unescape(file))).Split('\n');

  /// <summary>The text of a section of a Markdown text: the lines after its heading up to the next heading.</summary>
  private static string SectionOf(string markdown, string heading) {
    var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    var start = Array.IndexOf(lines, heading);
    Assert.That(start, Is.Not.Negative, $"no section '{heading}'");
    return string.Join("\n", lines.Skip(start + 1).TakeWhile(line => !line.StartsWith('#')));
  }

}
