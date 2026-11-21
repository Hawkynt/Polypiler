using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Optimize.Cost;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Optimize;

/// <summary>
/// The steps of <c>tests/features/optimize/opt-cost.feature</c> (#155): the test profiles of
/// <c>tests/fixtures/optimize/cost/</c> read by the cost model, its decisions, and the diagnostics of a
/// missing or malformed profile.
/// </summary>
[Binding, Scope(Feature = "Per-target cost model")]
public sealed class CostModelSteps {

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "optimize", "cost");
  private static readonly OperationKey _rotate = OperationKey.Parse("RotateLeft.u32");
  private static readonly OperationKey _byteXor = OperationKey.Parse("Xor.u8");

  private readonly Dictionary<string, CostModel> _models = [];
  private CostModel? _model;
  private Decision _decision;
  private string? _profileText;
  private Diagnostic? _error;

  private CostModel Model(string name) {
    if (!this._models.TryGetValue(name, out var model))
      this._models[name] = model = new(TargetCostProfile.Load(Path.Combine(_fixtures, name + ".yaml")));
    return this._model = model;
  }

  private void Choose(CostModel model, IReadOnlyList<Alternative> alternatives, string objective)
    => this._decision = model.Choose(alternatives, Enum.Parse<Objective>(objective));

  [Given("^the profile \"wide\" with RotateLeft\\.u32 native latency (\\d+) size (\\d+), simd width (\\d+) setup (\\d+) perBlock (\\d+), swar registerBits (\\d+) setup (\\d+) perBlock (\\d+), threads count (\\d+) spawn (\\d+)$")]
  public void WideProfile(long latency, long size, long width, long simdSetup, long simdBlock, long bits, long swarSetup, long swarBlock, long count, long spawn) {
    var profile = this.Model("wide").Profile;
    Assert.That(profile.Operations[_rotate].Native, Is.EqualTo(new Cost(latency, size)));
    Assert.That(profile.Simd, Is.EqualTo(new[] { new SimdForm(width, simdSetup, simdBlock) }));
    Assert.That(profile.Swar, Is.EqualTo(new SwarForm(bits, swarSetup, swarBlock)));
    Assert.That(profile.Threads, Is.EqualTo(new ThreadsForm(count, spawn)));
  }

  [Given("^the profile \"narrow\" with RotateLeft\\.u32 sequence latency (\\d+) size (\\d+) and helper call latency (\\d+) size (\\d+) body size (\\d+), swar registerBits (\\d+) setup (\\d+) perBlock (\\d+), no simd and no threads$")]
  public void NarrowProfile(long latency, long size, long callLatency, long callSize, long bodySize, long bits, long swarSetup, long swarBlock) {
    var profile = this.Model("narrow").Profile;
    var forms = profile.Operations[_rotate];
    Assert.That(forms.Native, Is.Null);
    Assert.That(forms.Sequence, Is.EqualTo(new Cost(latency, size)));
    Assert.That((forms.Helper!.Call, forms.Helper.Body.Size), Is.EqualTo((new Cost(callLatency, callSize), bodySize)));
    Assert.That(profile.Swar, Is.EqualTo(new SwarForm(bits, swarSetup, swarBlock)));
    Assert.That(profile.Simd, Is.Empty);
    Assert.That(profile.Threads, Is.Null);
  }

  [Given("^both profiles have scalar byte Xor latency (\\d+) and loop overhead (\\d+)$")]
  public void BothProfiles(long latency, long overhead) {
    foreach (var name in new[] { "wide", "narrow" }) {
      var model = this.Model(name);
      Assert.That((model.Of(_byteXor).Latency, model.Profile.LoopOverhead), Is.EqualTo((latency, overhead)), name);
    }
  }

  [When("^the cost model chooses the form of RotateLeft\\.u32 under profile \"(\\w+)\" with objective (\\w+)$")]
  public void ChoosesForm(string profile, string objective) => this.ChoosesFormAt(1, profile, objective);

  [When("^the cost model chooses the form of RotateLeft\\.u32 at (\\d+) call sites under profile \"(\\w+)\" with objective (\\w+)$")]
  public void ChoosesFormAt(long sites, string profile, string objective) {
    var model = this.Model(profile);
    this.Choose(model, model.OperationForms(_rotate, sites), objective);
  }

  [When("^the cost model chooses the data path of a byte Xor over (\\d+) elements under profile \"(\\w+)\" with objective (\\w+)$")]
  public void ChoosesDataPath(long n, string profile, string objective) {
    var model = this.Model(profile);
    this.Choose(model, model.DataPaths(_byteXor, CountFact.Constant(n)), objective);
  }

  [When("^the cost model chooses the placement of (\\d+) work units under profile \"(\\w+)\" with objective (\\w+)$")]
  public void ChoosesPlacement(long work, string profile, string objective) {
    var model = this.Model(profile);
    this.Choose(model, model.Placements(CountFact.Constant(work)), objective);
  }

  [Then("^the decision is \"([^\"]+)\" with score (\\d+)$")]
  public void DecisionIs(string form, long score) {
    Assert.That((this._decision.Form, this._decision.Score), Is.EqualTo((form, score)));
    Assert.That(this._decision.Detail, Does.StartWith($"cost: {form} {score}"));
  }

  [Then("^IsAvailable\\(\"(\\w+)\"\\) is (true|false) and IsAvailable\\(\"(\\w+)\"\\) is (true|false)$")]
  public void Availability(string first, bool firstAvailable, string second, bool secondAvailable)
    => Assert.That((this._model!.IsAvailable(first), this._model.IsAvailable(second)), Is.EqualTo((firstAvailable, secondAvailable)));

  [When("^the cost model is created for target profile \"([^\"]+)\" and no cost\\.yaml exists for it$")]
  public void CreatedForTarget(string target) {
    var specRoot = Path.Combine(PrototypeFixture.RepositoryRoot, "spec");
    Assert.That(File.Exists(TargetCostProfile.PathOf(specRoot, target)), Is.False);
    this._error = Assert.Throws<CostProfileException>(() => CostModel.ForTarget(specRoot, target))!.Diagnostic;
  }

  [Then("^error (PLY\\d{4}) \"([^\"]+)\" is reported$")]
  public void ErrorReported(string code, string message)
    => Assert.That((this._error!.Code, this._error.Message), Is.EqualTo((code, message)));

  [Given("^a profile file whose RotateLeft\\.u32 native latency is (-?\\d+)$")]
  public void ProfileWithLatency(long latency)
    => this._profileText = $"profile: p\nunknownTripCount: 16\ndefault: {{ latency: 2, size: 2 }}\noperations:\n  RotateLeft.u32:\n    native: {{ latency: {latency}, size: 1 }}\nloop: {{ overhead: 2 }}\n";

  [When("^the profile is loaded$")]
  public void ProfileLoaded()
    => this._error = Assert.Throws<CostProfileException>(() => TargetCostProfile.Parse(this._profileText!, "cost.yaml"))!.Diagnostic;

  [Then("^error (PLY\\d{4}) names the key \"([^\"]+)\"$")]
  public void ErrorNamesKey(string code, string key) {
    Assert.That(this._error!.Code, Is.EqualTo(code));
    Assert.That(this._error.Message, Does.StartWith($"cost profile 'cost.yaml': {key}: "));
  }

}
