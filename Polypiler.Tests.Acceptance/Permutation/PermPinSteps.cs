using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Corpus;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Permutation;

/// <summary>
/// The steps of <c>tests/features/permutation/perm-pin.feature</c>. The pinned BitPerm page is
/// imported into the scenario's workspace by the real <c>polypiler-tooling corpus import</c>, from the
/// checkout of <c>Hawkynt/Hawkynt.github.io</c> the corpus feature finds or fetches. The exporter runs
/// in a real <c>node</c> with the workspace as its working directory; in a command line,
/// <c>tooling/bitperm/export.mjs</c> and <c>tests/fixtures/bitperm</c> stand for the repository's files
/// and <c>&lt;page&gt;</c> for the imported page.
/// </summary>
[Binding, Scope(Feature = "BitPerm fixtures exported from the pinned page")]
public sealed class PermPinSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Repository = "Hawkynt/Hawkynt.github.io";
  private const string Pin = "b3a8a8e92cbca83475986391c3cac56f0b185d29";
  private const string Exporter = "tooling/bitperm/export.mjs";
  private const string Fixtures = "tests/fixtures/bitperm";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private string? _page;
  private string _out = "out";
  private JsonNode? _fixture;
  private (string Output, string Mask) _evaluated;

  private static string RepositoryFile(string path) => Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));

  private static JsonNode Load(string path) => JsonNode.Parse(File.ReadAllText(RepositoryFile(path)))!;

  /// <summary>Imports the page into the workspace with the repository's manifest and lock, once per scenario.</summary>
  private string Page() {
    if (this._page is not null)
      return this._page;

    workspace.Copy(RepositoryFile(CorpusManifest.RepositoryPath), CorpusManifest.RepositoryPath);
    workspace.Copy(RepositoryFile(CorpusLock.RepositoryPath), CorpusLock.RepositoryPath);
    var import = Toolchain.Tooling(workspace.Path, "corpus", "import", "--fixture", "bitperm.page", "--source", CorpusSteps.PinnedCheckout);
    Assert.That(import.ExitCode, Is.Zero, import.Output);
    this._page = CorpusImporter.FilePath(workspace.Path, Repository, Pin, "BitPerm/index.html");
    Assert.That(File.Exists(this._page), Is.True, this._page);
    return this._page;
  }

  /// <summary>Runs <c>node</c> in the workspace and logs what it did.</summary>
  private CommandResult Node(params string[] arguments) {
    var start = new ProcessStartInfo("node") {
      WorkingDirectory = workspace.Path,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    try {
      using var process = Process.Start(start)!;
      var error = process.StandardError.ReadToEndAsync();
      var output = process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      return log.Add(new CommandResult(process.ExitCode, output, error.Result));
    } catch (Win32Exception exception) {
      Assert.Fail($"node is required to run the BitPerm exporter but could not be started: {exception.Message}");
      throw;
    }
  }

  /// <summary>Makes sure the page is imported.</summary>
  [Given("^the pinned BitPerm page from the corpus$")]
  public void PinnedPage() => this.Page();

  /// <summary>Runs a <c>node</c> command line, with the exporter, the fixtures and <c>&lt;page&gt;</c> resolved.</summary>
  [When(@"^I run the exporter as ""node (.*)""$")]
  public void RunNode(string line) {
    var arguments = CommandLine.Split(line).Select(word => word switch {
      Exporter or Fixtures => RepositoryFile(word),
      "<page>" => this.Page(),
      _ => word
    });
    this.Node([.. arguments]);
  }

  /// <summary>Runs the exporter on the page of the workspace.</summary>
  [When("^I run the exporter on it$")]
  public void RunOnIt() => this.Node(RepositoryFile(Exporter), "--page", this._page!, "--out", this._out);

  /// <summary>Runs the exporter with <c>--check</c> against the copy of the fixtures in the workspace.</summary>
  [When("^I run the exporter with --check against that copy$")]
  public void RunCheckAgainstCopy() => this.Node(RepositoryFile(Exporter), "--page", this.Page(), "--out", this._out, "--check");

  /// <summary>Counts the blocks and the presets of the checked-in fixtures.</summary>
  [Then("^" + StepText.Quoted + @" lists (\d+) blocks and ""presets.json"" lists (\d+) presets$")]
  public void BlocksAndPresets(string blocks, int blockCount, int presetCount) {
    Assert.That(Load(blocks).AsArray(), Has.Count.EqualTo(blockCount));
    Assert.That(Load(Path.Combine(Path.GetDirectoryName(blocks)!, "presets.json")).AsArray(), Has.Count.EqualTo(presetCount));
  }

  /// <summary>Counts the tests and their sections.</summary>
  [Then("^" + StepText.Quoted + @" lists (\d+) tests in (\d+) sections$")]
  public void TestsInSections(string path, int tests, int sections) {
    var all = Load(path).AsArray();
    Assert.That(all, Has.Count.EqualTo(tests));
    Assert.That(all.Select(test => (string?)test!["section"]).Distinct().Count(), Is.EqualTo(sections));
  }

  /// <summary>Reads a checked-in fixture.</summary>
  [Given("^the exported fixture " + StepText.Quoted + "$")]
  public void TheFixture(string path) => this._fixture = Load(path);

  /// <summary>Checks one cost of a preset.</summary>
  [Then("^preset " + StepText.Quoted + " has cost " + StepText.Quoted + @" equal to (\d+)$")]
  public void PresetCost(string id, string key, int value) {
    var preset = this._fixture!.AsArray().Single(entry => (string?)entry!["id"] == id)!;
    Assert.That((int)preset["costs"]![key]!, Is.EqualTo(value));
  }

  /// <summary>Counts the vectors of a vector file.</summary>
  [Then(@"^it holds (\d+) vectors$")]
  public void HoldsVectors(int count) => Assert.That(this._fixture!["vectors"]!.AsArray(), Has.Count.EqualTo(count));

  /// <summary>Checks the output and the mask of one input.</summary>
  [Then("^input " + StepText.Quoted + " has output " + StepText.Quoted + " with mask " + StepText.Quoted + "$")]
  public void InputHasOutput(string input, string output, string mask) {
    var vector = this._fixture!["vectors"]!.AsArray().Single(entry => (string?)entry!["input"] == input)!;
    Assert.That(((string?)vector["output"], (string?)vector["mask"]), Is.EqualTo((output, mask)));
  }

  /// <summary>Copies the page into the workspace without the given line.</summary>
  [Given("^a copy of the page in which the line " + StepText.Quoted + " is deleted$")]
  public void PageWithoutLine(string line) {
    var lines = File.ReadAllLines(this.Page());
    Assert.That(lines.Count(text => text.Trim() == line), Is.EqualTo(1));
    this._page = workspace.Write("changed/index.html", string.Join('\n', lines.Where(text => text.Trim() != line)));
  }

  /// <summary>Copies the page into the workspace with a line added after another.</summary>
  [Given("^a copy of the page with the line " + StepText.Quoted + " after the line " + StepText.Quoted + "$")]
  public void PageWithLine(string added, string after) {
    var lines = File.ReadAllLines(this.Page()).ToList();
    var index = lines.FindIndex(text => text.Trim() == after);
    Assert.That(index, Is.Not.Negative, after);
    lines.Insert(index + 1, added);
    this._page = workspace.Write("changed/index.html", string.Join('\n', lines));
  }

  /// <summary>Copies the checked-in fixtures into the workspace, where the exporter is pointed next.</summary>
  private void CopyFixtures() {
    var source = RepositoryFile(Fixtures);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, Path.Combine(this._out, Path.GetRelativePath(source, file)));
  }

  /// <summary>Copies the fixtures and changes one cost of one preset in the copy.</summary>
  [Given("^a copy of " + StepText.Quoted + " in which the cost " + StepText.Quoted + " of " + StepText.Quoted + @" is (\d+)$")]
  public void ChangedCost(string file, string key, string id, int value) {
    this.CopyFixtures();
    var path = workspace.Resolve(Path.Combine(this._out, file));
    var text = File.ReadAllText(path);
    var line = text.Split('\n').Single(entry => entry.Contains($"\"id\": \"{id}\"", StringComparison.Ordinal));
    var at = text.IndexOf($"\"{key}\": ", text.IndexOf(line, StringComparison.Ordinal), StringComparison.Ordinal);
    var end = text.IndexOf(',', at);
    File.WriteAllText(path, text[..at] + $"\"{key}\": {value.ToString(CultureInfo.InvariantCulture)}" + text[end..]);
    Assert.That((int)JsonNode.Parse(File.ReadAllText(path))!.AsArray().Single(entry => (string?)entry!["id"] == id)!["costs"]![key]!, Is.EqualTo(value));
  }

  /// <summary>Copies the fixtures without one file.</summary>
  [Given("^a copy of the fixtures without " + StepText.Quoted + "$")]
  public void FixturesWithout(string file) {
    this.CopyFixtures();
    File.Delete(workspace.Resolve(Path.Combine(this._out, file)));
  }

  /// <summary>Evaluates a mapping written as words with the exporter's own <c>evaluate</c> and <c>maskOf</c>.</summary>
  [When("^the exporter evaluates the " + @"(\d+)" + "-bit mapping " + StepText.Quoted + " on input " + StepText.Quoted + "$")]
  public void Evaluate(int width, string entries, string input) {
    var mapping = entries.Split(", ").Select(entry => entry.Split(' ') switch {
      ["src", var bit] => $$"""{"type":"src","val":{{bit}},"inv":false}""",
      ["src", var bit, "inverted"] => $$"""{"type":"src","val":{{bit}},"inv":true}""",
      ["const", var bit] => $$"""{"type":"const","val":{{bit}}}""",
      ["empty"] => """{"type":"empty"}""",
      _ => throw new FormatException($"unknown mapping entry '{entry}'")
    }).ToArray();
    Assert.That(mapping, Has.Length.EqualTo(width));
    var module = new Uri(RepositoryFile(Exporter)).AbsoluteUri;
    var script = $$"""
      const { evaluate, maskOf } = await import({{JsonSerializer.Serialize(module)}});
      const mapping = [{{string.Join(",", mapping)}}];
      process.stdout.write('0b' + evaluate(mapping, {{input}}n).toString(2).padStart({{width}}, '0') + ' 0b' + maskOf(mapping).toString(2).padStart({{width}}, '0'));
      """;
    var result = this.Node("--input-type=module", "-e", script);
    Assert.That(result.ExitCode, Is.Zero, result.Output);
    var words = result.Stdout.Split(' ');
    this._evaluated = (words[0], words[1]);
  }

  /// <summary>Checks the output and the mask of the evaluation.</summary>
  [Then("^the output is " + StepText.Quoted + " and the mask is " + StepText.Quoted + "$")]
  public void OutputAndMask(string output, string mask) => Assert.That(this._evaluated, Is.EqualTo((output, mask)));

}
