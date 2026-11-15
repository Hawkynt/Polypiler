using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Gates;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/ver-gates.feature</c>. Records are written by
/// <see cref="GateRecorder"/> into an artifacts directory of the scenario's workspace, beside a copy of
/// the evidence of <c>tests/fixtures/verify/gates/evidence/</c>, and read back by <see cref="GateStore"/>.
/// </summary>
[Binding, Scope(Feature = "Gate-state tracking per language pair")]
public sealed class VerGatesSteps(ScenarioWorkspace workspace) {

  private const string Fixtures = "tests/fixtures/verify/gates";

  private static readonly PairKey _csharpToJavaScript = new("csharp", "javascript", "net10", "node-es2022");

  private GateState[] _evidence = [];
  private GateState _state;
  private PairKey _pair = _csharpToJavaScript;
  private string _gates = "";
  private GateStore? _store;
  private GateStore? _baseline;
  private readonly DiagnosticBag _diagnostics = new();
  private IReadOnlyList<GateRegression> _regressions = [];

  private static GateState State(string name)
    => GateStates.TryParse(name, out var state) ? state : throw new ArgumentException($"'{name}' is no gate state", nameof(name));

  private static string EvidenceOf(GateState state) => state switch {
    GateState.Parsed => "evidence/frontend.log",
    GateState.Lifted => "evidence/lifted.ir",
    GateState.Recognized => "evidence/opt-report.txt",
    GateState.Proved => "evidence/proof-report.txt",
    GateState.Legalized => "evidence/legal.ir",
    GateState.Emitted => "evidence/manifests/generated.json",
    GateState.Compiled => "evidence/manifests/built.json",
    GateState.Executed => "evidence/manifests/executed.json",
    GateState.Verified => "evidence/manifests/verified.json",
    _ => "evidence/bench.json"
  };

  /// <summary>A gate directory of its own in the workspace, with a copy of the fixture evidence beside it.</summary>
  private string Artifacts(string name) {
    var source = Path.Combine(PrototypeFixture.RepositoryRoot, Fixtures, "evidence");
    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, Path.Combine(name, "evidence", Path.GetRelativePath(source, file)));

    var gates = workspace.Resolve(Path.Combine(name, "gates"));
    Directory.CreateDirectory(gates);
    return gates;
  }

  /// <summary>Records the mandatory states up to one, each with its fixture evidence.</summary>
  private static void RecordUpTo(string gates, PairKey pair, string fixture, GateState last) {
    var recorder = new GateRecorder(gates, "scenario");
    foreach (var state in GateStates.Evidenced.Where(state => state <= last && !GateStates.IsOptional(state)))
      recorder.Record(pair, fixture, recorder.Evidence(state, Path.Combine(recorder.Root, EvidenceOf(state))));
  }

  private static GateStore Load(string gates, IDiagnosticSink sink) => GateStore.Load(gates, sink);

  [Given("^a fixture with valid evidence for the states (.+)$")]
  public void FixtureWithEvidence(string list) => this._evidence = [.. list.Split(',', StringSplitOptions.TrimEntries).Select(State)];

  [When("^its gate state is computed$")]
  public void Computed() {
    var gates = this.Artifacts("artifacts");
    var recorder = new GateRecorder(gates, "scenario");
    foreach (var state in this._evidence)
      recorder.Record(this._pair, "fixture", recorder.Evidence(state, Path.Combine(recorder.Root, EvidenceOf(state))));

    this._state = Load(gates, this._diagnostics).FixturesOf(this._pair).Single().State;
    Assert.That(this._diagnostics.Items, Is.Empty);
  }

  [Then("^the state is " + StepText.Quoted + "$")]
  public void StateIs(string state) => Assert.That(GateStates.Name(this._state), Is.EqualTo(state));

  [Given("^the pair (\\w+) to (\\w+) with profiles " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Pair(string from, string to, string source, string target) {
    this._pair = new(from, to, source, target);
    this._gates = this.Artifacts("artifacts");
  }

  [Given("^fixture " + StepText.Quoted + " recorded up to " + StepText.Quoted + " and fixture " + StepText.Quoted + " recorded up to " + StepText.Quoted + "$")]
  public void TwoFixtures(string first, string firstState, string second, string secondState) {
    RecordUpTo(this._gates, this._pair, first, State(firstState));
    RecordUpTo(this._gates, this._pair, second, State(secondState));
  }

  [Given("^the record " + StepText.Quoted + "$")]
  public void Record(string path) {
    this._gates = this.Artifacts("artifacts");
    var source = Path.Combine(PrototypeFixture.RepositoryRoot, path);
    var pair = JsonNode.Parse(File.ReadAllText(source))!["pair"]!;
    this._pair = new(pair["from"]!.GetValue<string>(), pair["to"]!.GetValue<string>(), pair["sourceProfile"]!.GetValue<string>(), pair["targetProfile"]!.GetValue<string>());
    File.Copy(source, Path.Combine(this._gates, this._pair.FileName));
  }

  [Given("^a record whose " + StepText.Quoted + " evidence is a manifest with state " + StepText.Quoted + "$")]
  public void ManifestBelowState(string state, string manifest) {
    Assert.That((state, manifest), Is.EqualTo(("verified", "built")), "the fixture is a built manifest given for verified");
    this.Record(Fixtures + "/invalid/manifest-below-state.json");
  }

  [When("^the gate store is loaded$")]
  public void Loaded() => this._store = Load(this._gates, this._diagnostics);

  [Then("^the pair state is " + StepText.Quoted + "$")]
  public void PairStateIs(string state) => Assert.That(GateStates.Name(this._store!.StateOf(this._pair)), Is.EqualTo(state));

  [Then("^the pair (\\w+) to (\\w+) with profiles " + StepText.Quoted + " and " + StepText.Quoted + " has state " + StepText.Quoted + "$")]
  public void OtherPairStateIs(string from, string to, string source, string target, string state)
    => Assert.That(GateStates.Name(this._store!.StateOf(new(from, to, source, target))), Is.EqualTo(state));

  [Then("^diagnostic (PLY\\d{4}) is reported with reason " + StepText.Quoted + "$")]
  public void DiagnosticWithReason(string code, string reason) {
    var diagnostic = this._diagnostics.Items.Single();
    Assert.That(diagnostic.Code, Is.EqualTo(code));
    Assert.That(diagnostic.Message, Does.EndWith(": " + reason));
    Assert.That(diagnostic.Message, Does.StartWith($"gate record '{Path.Combine(this._gates, this._pair.FileName)}': fixture '"));
  }

  [Given("^a baseline in which (\\w+) to (\\w+) is " + StepText.Quoted + "$")]
  public void Baseline(string from, string to, string state) {
    this._pair = new(from, to, _csharpToJavaScript.SourceProfile, _csharpToJavaScript.TargetProfile);
    var gates = this.Artifacts("baseline");
    RecordUpTo(gates, this._pair, "add32", State(state));
    this._baseline = Load(gates, this._diagnostics);
  }

  [Given("^a current store in which (\\w+) to (\\w+) is " + StepText.Quoted + "$")]
  public void Current(string from, string to, string state) {
    Assert.That((from, to), Is.EqualTo((this._pair.From, this._pair.To)));
    var gates = this.Artifacts("current");
    RecordUpTo(gates, this._pair, "add32", State(state));
    this._store = Load(gates, this._diagnostics);
  }

  [When("^the current store is compared with the baseline$")]
  public void Compared() {
    Assert.That(this._diagnostics.Items, Is.Empty);
    this._regressions = this._store!.CompareWith(this._baseline!);
  }

  [Then("^exactly one regression is returned, from " + StepText.Quoted + " to " + StepText.Quoted + "$")]
  public void OneRegression(string before, string after)
    => Assert.That(this._regressions, Is.EqualTo(new[] { new GateRegression(this._pair, State(before), State(after)) }));

}
