using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Verification.Capabilities;
using Polypiler.Core.Verification.Gates;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Commands;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/spec-capabilities.feature</c>. The scenario's workspace is a
/// repository: <c>spec/</c> is a copy of a tree of <c>tests/fixtures/verify/capabilities/</c>, and
/// <c>artifacts/</c> holds a copy of the evidence of <c>tests/fixtures/verify/gates/evidence/</c> with
/// <c>artifacts/gates/</c> beside it, where <see cref="GateRecorder"/> writes the records. The command
/// lines of the feature run the real <c>polypiler-tooling</c> there.
/// </summary>
[Binding, Scope(Feature = "Capability report from executed evidence")]
public sealed partial class SpecCapabilitiesSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Fixtures = "tests/fixtures/verify/capabilities";

  private const string Evidence = "tests/fixtures/verify/gates/evidence";

  private const string Json = "docs/capabilities.json";

  private string? _tree;
  private string _committed = "";
  private string[] _outputs = [];

  private static GateState State(string name)
    => GateStates.TryParse(name, out var state) ? state : throw new ArgumentException($"'{name}' is no gate state", nameof(name));

  /// <summary>The pair of a text <c>&lt;from&gt;/&lt;source profile&gt; -&gt; &lt;to&gt;/&lt;target profile&gt;</c>.</summary>
  private static PairKey Pair(string text) {
    var match = PairText().Match(text);
    Assert.That(match.Success, Is.True, $"'{text}' is no pair");
    return new(match.Groups[1].Value, match.Groups[3].Value, match.Groups[2].Value, match.Groups[4].Value);
  }

  private static string EvidenceOf(GateState state) => state switch {
    GateState.Parsed => "evidence/frontend.log",
    GateState.Lifted => "evidence/lifted.ir",
    GateState.Legalized => "evidence/legal.ir",
    GateState.Emitted => "evidence/manifests/generated.json",
    GateState.Compiled => "evidence/manifests/built.json",
    GateState.Executed => "evidence/manifests/executed.json",
    GateState.Verified => "evidence/manifests/verified.json",
    _ => throw new ArgumentOutOfRangeException(nameof(state), state, "the feature records no such state")
  };

  /// <summary>Copies a spec tree and the gate evidence into the workspace, once per scenario.</summary>
  private void Tree(string name) {
    Assert.That(this._tree, Is.Null.Or.EqualTo(name), "a scenario uses one tree");
    if (this._tree is not null)
      return;

    this._tree = name;
    foreach (var (source, target) in new[] { (Path.Combine(Fixtures, name, "spec"), "spec"), (Evidence, "artifacts/evidence") }) {
      var root = Path.Combine(PrototypeFixture.RepositoryRoot, source);
      foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        workspace.Copy(file, Path.Combine(target, Path.GetRelativePath(root, file)));
    }

    Directory.CreateDirectory(workspace.Resolve("artifacts/gates"));
  }

  /// <summary>Records a fixture of a pair: every mandatory state up to one, each with its fixture evidence.</summary>
  private void Record(PairKey pair, string fixture, GateState last) {
    this.Tree("one-verified");
    var recorder = new GateRecorder(workspace.Resolve("artifacts/gates"), "scenario");
    foreach (var state in GateStates.Evidenced.Where(state => state <= last && !GateStates.IsOptional(state)))
      recorder.Record(pair, fixture, recorder.Evidence(state, Path.Combine(recorder.Root, EvidenceOf(state))));
  }

  private CapabilityReport Report(string relative = Json) => CapabilityReport.Load(File.OpenRead(workspace.Resolve(relative)));

  [Given("^a spec tree with source profiles " + StepText.Quoted + " and " + StepText.Quoted + " and target profiles " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void SpecTree(string firstSource, string secondSource, string firstTarget, string secondTarget) {
    this.Tree("one-verified");
    foreach (var profile in new[] { firstSource, secondSource }) {
      var (language, name) = (profile[..profile.IndexOf('/')], profile[(profile.IndexOf('/') + 1)..]);
      Assert.That(File.Exists(workspace.Resolve($"spec/languages/{language}/profiles/{name}.yaml")), Is.True, profile);
    }

    foreach (var profile in new[] { firstTarget, secondTarget })
      Assert.That(File.Exists(workspace.Resolve($"spec/targets/{profile}/target.yaml")), Is.True, profile);
  }

  [Given("^the same spec tree$")]
  public void SameSpecTree() => this.Tree("one-verified");

  [Given("^the tree " + StepText.Quoted + "$")]
  public void NamedTree(string name) => this.Tree(name);

  [Given("^an empty gate directory$")]
  public void EmptyGateDirectory() => Assert.That(Directory.GetFileSystemEntries(workspace.Resolve("artifacts/gates")), Is.Empty);

  [Given("^a valid gate record for " + StepText.Quoted + " whose single fixture has evidence up to " + StepText.Quoted + "$")]
  public void SingleFixture(string pair, string state) => this.Record(Pair(pair), "add32", State(state));

  [Given("^a valid gate record for " + StepText.Quoted + " with fixture " + StepText.Quoted + " at " + StepText.Quoted + " and fixture " + StepText.Quoted + " at " + StepText.Quoted + "$")]
  public void TwoFixtures(string pair, string first, string firstState, string second, string secondState) {
    this.Record(Pair(pair), first, State(firstState));
    this.Record(Pair(pair), second, State(secondState));
  }

  [Given("^a gate record for " + StepText.Quoted + " claiming " + StepText.Quoted + " whose manifest file was altered after recording$")]
  public void AlteredManifest(string pair, string state) {
    this.Record(Pair(pair), "add32", State(state));
    File.AppendAllText(workspace.Resolve(Path.Combine("artifacts", EvidenceOf(State(state)))), "\n");
  }

  /// <summary>Writes the report of the evidence, then raises one cell by hand.</summary>
  [Given("^a committed " + StepText.Quoted + " in which " + StepText.Quoted + " is " + StepText.Quoted + "$")]
  public void HandRaised(string file, string pair, string state) {
    Assert.That(file, Is.EqualTo(Json));
    this.Tree("one-verified");
    Assert.That(ReportCapabilitiesCommand.Run([], TextWriter.Null, TextWriter.Null, workspace.Path, null), Is.Zero);
    var key = Pair(pair);
    var pattern = new Regex($"(\"from\": \"{key.From}\",\\s*\"sourceProfile\": \"{key.SourceProfile}\",\\s*\"to\": \"{key.To}\",\\s*\"targetProfile\": \"{key.TargetProfile}\",\\s*)\"state\": \"planned\",(\\s*)\"fixtures\": 0,(\\s*)\"evidence\": null");
    var path = workspace.Resolve(file);
    var text = File.ReadAllText(path);
    Assert.That(pattern.IsMatch(text), Is.True, pair);
    File.WriteAllText(path, pattern.Replace(text, $"$1\"state\": \"{state}\",$2\"fixtures\": 1,$3\"evidence\": \"artifacts/gates/{key.FileName}\""));
    this._committed = File.ReadAllText(path);
  }

  [Given("^no gate record for that pair$")]
  public void NoGateRecord() => Assert.That(Directory.GetFiles(workspace.Resolve("artifacts/gates")), Is.Empty);

  [When("^I run " + StepText.Quoted + " twice into two output directories$")]
  public void RunTwice(string line) {
    var words = line.Split(' ');
    Assert.That(words[0], Is.EqualTo("polypiler-tooling"));
    this._outputs = ["first", "second"];
    foreach (var output in this._outputs)
      Assert.That(log.Add(Toolchain.Tooling(workspace.Path, [.. words[1..], "--out", output])).ExitCode, Is.Zero, log.Last.Output);
  }

  [Then("^" + StepText.Quoted + " lists (\\d+) pairs$")]
  public void Lists(string file, int count) => Assert.That(this.Report(file).Pairs, Has.Count.EqualTo(count));

  [Then("^every pair has state " + StepText.Quoted + ", fixtures (\\d+) and evidence null$")]
  public void EveryPair(string state, int fixtures)
    => Assert.That(this.Report().Pairs.Select(pair => (GateStates.Name(pair.State), pair.Fixtures, pair.Evidence)), Is.All.EqualTo((state, fixtures, (string?)null)));

  [Then("^the pair " + StepText.Quoted + " has state " + StepText.Quoted + " and fixtures (\\d+)$")]
  public void PairStateAndFixtures(string pair, string state, int fixtures) {
    var key = Pair(pair);
    var found = this.Report().Find(key.From, key.SourceProfile, key.To, key.TargetProfile);
    Assert.That(found, Is.Not.Null, pair);
    Assert.That((GateStates.Name(found!.State), found.Fixtures), Is.EqualTo((state, fixtures)));
  }

  [Then("^the pair " + StepText.Quoted + " has state " + StepText.Quoted + "$")]
  public void PairState(string pair, string state) {
    var key = Pair(pair);
    Assert.That(GateStates.Name(this.Report().StateOf(key.From, key.SourceProfile, key.To, key.TargetProfile)), Is.EqualTo(state));
  }

  [Then("^" + StepText.Quoted + " contains the row " + StepText.Quoted + " under the heading " + StepText.Quoted + "$")]
  public void ContainsRow(string file, string row, string heading) {
    var text = File.ReadAllText(workspace.Resolve(file));
    var start = text.IndexOf($"\n## {heading}\n", StringComparison.Ordinal);
    Assert.That(start, Is.Not.Negative, heading);
    var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
    Assert.That(text[start..end].Split('\n'), Does.Contain(StepText.Unescape(row)));
  }

  [Then("^" + StepText.Quoted + " is unchanged$")]
  public void Unchanged(string file) => Assert.That(File.ReadAllText(workspace.Resolve(file)), Is.EqualTo(this._committed));

  [Then("^the two " + StepText.Quoted + " files have the same SHA-256$")]
  public void SameHash(string name) {
    var hashes = this._outputs.Select(output => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(workspace.Resolve(Path.Combine(output, name)))))).ToList();
    TestContext.Out.WriteLine($"SHA-256 of {name}: {string.Join(", ", hashes)}");
    Assert.That(hashes.Distinct().Count(), Is.EqualTo(1));
  }

  [GeneratedRegex(@"^([a-z0-9.+_-]+)/([a-z0-9.+_-]+) -> ([a-z0-9.+_-]+)/([a-z0-9.+_-]+)$")]
  private static partial Regex PairText();

}
