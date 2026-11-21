using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Optimize.Cost;
using Polypiler.Prototype.Testing;
using CostValue = Polypiler.Core.Optimize.Cost.Cost;

namespace Polypiler.Tests.Optimize.Cost;

/// <summary>The test profiles of <c>tests/fixtures/optimize/cost/</c>.</summary>
internal static class CostFixtures {

  public static string Directory { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "optimize", "cost");

  public static string PathOf(string name) => Path.Combine(Directory, name + ".yaml");

  public static CostModel Model(string name) => new(TargetCostProfile.Load(PathOf(name)));

  public static readonly OperationKey Rotate = OperationKey.Parse("RotateLeft.u32");

  public static readonly OperationKey ByteXor = OperationKey.Parse("Xor.u8");

  /// <summary>A profile whose text is the minimal valid one with <paramref name="extra"/> appended.</summary>
  public static CostModel Inline(string extra) => new(TargetCostProfile.Parse(Minimal + extra, "inline.yaml"));

  public const string Minimal = "profile: inline\nunknownTripCount: 16\ndefault: { latency: 2, size: 2 }\nloop: { overhead: 2 }\n";

}

/// <summary>
/// The decisions of the cost model, one test per row of the test design of #155; every expected score
/// is the weighted sum written out by hand in the issue.
/// </summary>
[TestFixture]
public sealed class CostModelTests {

  private static readonly CostModel _wide = CostFixtures.Model("wide");
  private static readonly CostModel _narrow = CostFixtures.Model("narrow");

  private static Decision Forms(CostModel model, long sites, Objective objective) => model.Choose(model.OperationForms(CostFixtures.Rotate, sites), objective);

  // Row 1.
  [Test]
  public void NativeForm_BeatsSequenceAndHelper() {
    var decision = Forms(_wide, 1, Objective.Speed);
    Assert.That((decision.Form, decision.Score, decision.Index), Is.EqualTo(("native", 1L, 0)));
    Assert.That(decision.Detail, Is.EqualTo("cost: native 1 vs sequence 3, helper 12"));
    Assert.That(decision.Reason, Is.EqualTo("lowest Speed score"));
  }

  // Rows 2, 3, 4.
  [TestCase(10, Objective.Speed, "sequence", 30, "cost: sequence 30 vs helper 120")]
  [TestCase(10, Objective.Size, "sequence", 30, "cost: sequence 30 vs helper 40")]
  [TestCase(30, Objective.Size, "helper", 80, "cost: helper 80 vs sequence 90")]
  public void WithoutNativeForm_ObjectiveAndCallSitesDecide(int sites, Objective objective, string form, long score, string detail) {
    var decision = Forms(_narrow, sites, objective);
    Assert.That((decision.Form, decision.Score, decision.Detail), Is.EqualTo((form, score, detail)));
  }

  // Row 5: sequence 20 × 3 = 60, helper 20 × 2 + 20 = 60; Balanced: 4 × 60 + 60 = 300 against 4 × 240 + 60 = 1020.
  [Test]
  public void HelperBreakEven_IsBrokenByTheBalancedWeights() {
    var decision = Forms(_narrow, 20, Objective.Size);
    Assert.That((decision.Form, decision.Score), Is.EqualTo(("sequence", 60L)));
    Assert.That(decision.Reason, Is.EqualTo("tied under Size, lower Balanced score 300"));
  }

  // Rows 6, 7, 8: body 4, overhead 2, trip 8.
  [TestCase(Objective.Speed, "unrolled", 32)]
  [TestCase(Objective.Size, "loop", 6)]
  [TestCase(Objective.Balanced, "unrolled", 160)]
  public void LoopOrUnrolled_FollowsTheObjective(Objective objective, string form, long score) {
    var alternatives = _wide.LoopForms(new(4, 4), CountFact.Constant(8));
    Assert.That(alternatives.Select(alternative => alternative.Cost), Is.EqualTo(new[] { new CostValue(48, 6), new CostValue(32, 32) }));
    var decision = _wide.Choose(alternatives, objective);
    Assert.That((decision.Form, decision.Score), Is.EqualTo((form, score)));
  }

  [Test]
  public void LoopUnderBalanced_ScoresAsTheIssueComputes()
    => Assert.That(_wide.LoopForms(new(4, 4), CountFact.Constant(8))[0].Cost.Score(Objective.Balanced), Is.EqualTo(198));

  // Rows 9, 10, 11, 12, 13.
  [TestCase("wide", 64, "simd-128", 8, "cost: simd-128 8 vs scalar 64, swar-64 12")]
  [TestCase("narrow", 64, "swar-64", 12, "cost: swar-64 12 vs scalar 64")]
  [TestCase("wide", 3, "scalar", 3, "cost: scalar 3 vs swar-64 7, simd-128 7")]
  [TestCase("narrow", 8, "swar-64", 5, "cost: swar-64 5 vs scalar 8")]
  [TestCase("narrow", 0, "scalar", 0, "cost: scalar 0 vs swar-64 4")]
  public void DataPath_FollowsTheElementCountAndTheHardware(string profile, long n, string form, long score, string detail) {
    var model = profile == "wide" ? _wide : _narrow;
    var decision = model.Choose(model.DataPaths(CostFixtures.ByteXor, CountFact.Constant(n)), Objective.Speed);
    Assert.That((decision.Form, decision.Score, decision.Detail), Is.EqualTo((form, score, detail)));
  }

  // Rows 14, 15: 1024 elements, unpacked memory 4096 latency 1024, packed memory 512 latency 3072.
  [TestCase(Objective.Memory, "packed", 512)]
  [TestCase(Objective.Speed, "unpacked", 1024)]
  public void Storage_FollowsTheObjective(Objective objective, string form, long score) {
    Alternative[] alternatives = [new("unpacked", new(1024, 0, 4096)), new("packed", new(3072, 0, 512))];
    var decision = _wide.Choose(alternatives, objective);
    Assert.That((decision.Form, decision.Score), Is.EqualTo((form, score)));
  }

  [Test]
  public void Evaluation_CountsAllocationsUnderMemory() {
    Alternative[] alternatives = [new("eager", new(10, 0, 100, 0)), new("lazy", new(12, 0, 40, 1))];
    Assert.That(_wide.Choose(alternatives, Objective.Memory).Form, Is.EqualTo("lazy"));
    Assert.That(_wide.Choose(alternatives, Objective.Speed).Form, Is.EqualTo("eager"));
  }

  // Rows 16, 17 and the narrow profile without threads.
  [TestCase("wide", 1_000_000, "threads-4", 260_000)]
  [TestCase("wide", 10_000, "cpu", 10_000)]
  [TestCase("narrow", 1_000_000, "cpu", 1_000_000)]
  public void Placement_ChoosesThreadsOnlyWhenTheWorkOutweighsTheSpawn(string profile, long work, string form, long score) {
    var model = profile == "wide" ? _wide : _narrow;
    var decision = model.Choose(model.Placements(CountFact.Constant(work)), Objective.Speed);
    Assert.That((decision.Form, decision.Score), Is.EqualTo((form, score)));
  }

  [Test]
  public void Placement_OffersTheAcceleratorsAProfileLists() {
    var model = CostFixtures.Inline("gpu: { transfer: 1000, perElement: 1 }\nnpu: { transfer: 500, perElement: 2 }\n");
    var alternatives = model.Placements(CountFact.Constant(100_000));
    Assert.That(alternatives.Select(alternative => (alternative.Form, alternative.Cost.Latency)), Is.EqualTo(new[] { ("cpu", 100_000L), ("gpu", 101_000L), ("npu", 200_500L) }));
    Assert.That(model.Choose(model.Placements(CountFact.Constant(10)), Objective.Speed).Form, Is.EqualTo("cpu"));
  }

  // Row 18: the range [0, 100] has the midpoint 50; loop 50 × (4 + 2) = 300, unrolled 50 × 4 = 200.
  [Test]
  public void UnknownTripCount_WithARange_UsesTheMidpoint() {
    var alternatives = _wide.LoopForms(new(4, 4), CountFact.Range(0, 100));
    Assert.That(alternatives.Select(alternative => alternative.Cost.Latency), Is.EqualTo(new[] { 300L, 200L }));
  }

  // Row 19: unknownTripCount 16; loop 16 × 6 = 96, unrolled 16 × 4 = 64; the data path and the placement likewise.
  [Test]
  public void UnknownTripCount_WithoutAFact_UsesTheProfile() {
    Assert.That(_wide.LoopForms(new(4, 4), CountFact.Unknown).Select(alternative => alternative.Cost.Latency), Is.EqualTo(new[] { 96L, 64L }));
    Assert.That(_wide.DataPaths(CostFixtures.ByteXor, CountFact.Unknown)[0].Cost.Latency, Is.EqualTo(16));
    Assert.That(_wide.Placements(CountFact.Unknown)[0].Cost.Latency, Is.EqualTo(16));
  }

  // Row 20.
  [Test]
  public void OperationNotListed_CostsTheDefault() {
    var popCount = OperationKey.Parse("PopCount.u32");
    Assert.That(_narrow.Of(popCount), Is.EqualTo(new CostValue(2, 2)));
    var decision = _narrow.Choose(_narrow.OperationForms(popCount, 3), Objective.Speed);
    Assert.That((decision.Form, decision.Score), Is.EqualTo(("default", 6L)));
    Assert.That(decision.Reason, Does.Contain("default cost"));
    Assert.That(decision.Detail, Is.EqualTo("cost: default 6"));
    var path = _narrow.Choose(_narrow.DataPaths(popCount, CountFact.Constant(1)), Objective.Speed);
    Assert.That((path.Form, path.Reason), Is.EqualTo(("scalar", "lowest Speed score; default cost")));
  }

  // Row 25: latency 2^62 under Balanced is 2^64, beyond long; the score is long.MaxValue and the alternative loses.
  [Test]
  public void ScoreOverflow_SaturatesAndLoses() {
    Alternative[] alternatives = [new("huge", new(1L << 62, 0)), new("small", new(1, 0))];
    Assert.That(alternatives[0].Cost.Score(Objective.Balanced), Is.EqualTo(long.MaxValue));
    var decision = _wide.Choose(alternatives, Objective.Balanced);
    Assert.That((decision.Form, decision.Score), Is.EqualTo(("small", 4L)));
    Assert.That(decision.Detail, Is.EqualTo($"cost: small 4 vs huge {long.MaxValue}"));
  }

  // Row 26.
  [Test]
  public void IdenticalAlternatives_TheFirstListedWins() {
    Alternative[] alternatives = [new("a", new(5, 5)), new("b", new(5, 5))];
    var decision = _wide.Choose(alternatives, Objective.Speed);
    Assert.That((decision.Index, decision.Form), Is.EqualTo((0, "a")));
    Assert.That(decision.Reason, Is.EqualTo("tied under Speed and Balanced, listed first"));
    Assert.That(_wide.Choose([.. alternatives.Reverse()], Objective.Speed).Form, Is.EqualTo("b"));
  }

  [Test]
  public void LaterAlternative_WinsOnTheBalancedTieBreak() {
    Alternative[] alternatives = [new("a", new(5, 9)), new("b", new(5, 1))];
    Assert.That(_wide.Choose(alternatives, Objective.Speed).Form, Is.EqualTo("b"));
  }

  [Test]
  public void Choose_RefusesNullAndEmptyLists() {
    Assert.Throws<ArgumentNullException>(() => _wide.Choose(null!, Objective.Speed));
    Assert.Throws<ArgumentException>(() => _wide.Choose([], Objective.Speed));
  }

  [Test]
  public void Choose_RefusesAnObjectiveThatIsNoMember()
    => Assert.Throws<ArgumentOutOfRangeException>(() => _wide.Choose([new("a", new(1, 1))], (Objective)42));

  [TestCase("native", true, true)]
  [TestCase("sequence", true, true)]
  [TestCase("helper", true, true)]
  [TestCase("simd", true, false)]
  [TestCase("simd-128", true, false)]
  [TestCase("simd-256", false, false)]
  [TestCase("swar-64", true, true)]
  [TestCase("threads", true, false)]
  [TestCase("threads-4", true, false)]
  [TestCase("gpu", false, false)]
  [TestCase("npu", false, false)]
  [TestCase("scalar", true, true)]
  [TestCase("packed", true, true)]
  [TestCase("teleport", false, false)]
  [TestCase(null, false, false)]
  public void IsAvailable_FollowsTheProfile(string? form, bool wide, bool narrow) {
    Assert.That(_wide.IsAvailable(form!), Is.EqualTo(wide));
    Assert.That(_narrow.IsAvailable(form!), Is.EqualTo(narrow));
  }

  [Test]
  public void IsAvailable_NamesTheNativeFormOnlyWhereAnOperationHasOne() {
    Assert.That(_narrow.IsAvailable("native"), Is.True, "Xor.u8 is native in the narrow profile");
    var model = CostFixtures.Inline("operations:\n  RotateLeft.u32:\n    sequence: { latency: 3, size: 3 }\ngpu: { transfer: 1, perElement: 1 }\nnpu: { transfer: 1, perElement: 1 }\n");
    Assert.That((model.IsAvailable("native"), model.IsAvailable("helper"), model.IsAvailable("gpu"), model.IsAvailable("npu")), Is.EqualTo((false, false, true, true)));
  }

  [Test]
  public void Of_TakesTheFirstFormListed() {
    Assert.That(_wide.Of(CostFixtures.Rotate), Is.EqualTo(new CostValue(1, 1)));
    Assert.That(_narrow.Of(CostFixtures.Rotate), Is.EqualTo(new CostValue(3, 3)));
    var helperOnly = CostFixtures.Inline("operations:\n  RotateLeft.u32:\n    helper: { call: { latency: 12, size: 2 }, body: { latency: 0, size: 20 } }\n");
    Assert.That(helperOnly.Of(CostFixtures.Rotate), Is.EqualTo(new CostValue(12, 22)));
  }

  [Test]
  public void OperationForms_RefusesANegativeNumberOfCallSites()
    => Assert.Throws<ArgumentOutOfRangeException>(() => _wide.OperationForms(CostFixtures.Rotate, -1));

  [Test]
  public void DataPaths_LeaveOutTheRegistersNarrowerThanAnElement() {
    var model = CostFixtures.Inline("operations:\n  Xor.u64:\n    native: { latency: 1, size: 1 }\nswar: { registerBits: 32, setup: 1, perBlock: 1 }\nsimd:\n  - { width: 32, setup: 1, perBlock: 1 }\n  - { width: 128, setup: 1, perBlock: 1 }\n");
    Assert.That(model.DataPaths(OperationKey.Parse("Xor.u64"), CountFact.Constant(4)).Select(alternative => alternative.Form), Is.EqualTo(new[] { "scalar", "simd-128" }));
    Assert.That(model.DataPaths(new OperationKey("Xor", "str"), CountFact.Constant(4)).Select(alternative => alternative.Form), Is.EqualTo(new[] { "scalar" }));
  }

  [Test]
  public void Constructor_RefusesNull() => Assert.Throws<ArgumentNullException>(() => new CostModel(null!));

  [Test]
  public void ForTarget_ReadsTheCostYamlOfTheTargetProfile() {
    var root = Path.Combine(Path.GetTempPath(), "polypiler-cost-" + Guid.NewGuid().ToString("N"));
    try {
      var path = TargetCostProfile.PathOf(root, "demo/p1");
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.Copy(CostFixtures.PathOf("wide"), path);
      Assert.That(CostModel.ForTarget(root, "demo/p1").Profile.Name, Is.EqualTo("wide"));
    } finally {
      Directory.Delete(root, true);
    }
  }

  [Test]
  public void FormatDetail_PricesUnderTheObjectiveGiven() {
    var alternatives = _narrow.OperationForms(CostFixtures.Rotate, 10);
    var decision = _narrow.Choose(alternatives, Objective.Speed);
    Assert.That(CostModel.FormatDetail(alternatives, decision, Objective.Speed), Is.EqualTo(decision.Detail));
    Assert.That(CostModel.FormatDetail(alternatives, decision, Objective.Size), Is.EqualTo("cost: sequence 30 vs helper 40"));
  }

  [Test]
  public void FormatDetail_RefusesADecisionOfAnotherList() {
    Alternative[] alternatives = [new("a", new(1, 1))];
    Assert.Throws<ArgumentNullException>(() => CostModel.FormatDetail(null!, default, Objective.Speed));
    Assert.Throws<ArgumentOutOfRangeException>(() => CostModel.FormatDetail(alternatives, new Decision(-1, "a", 0, "", ""), Objective.Speed));
    Assert.Throws<ArgumentOutOfRangeException>(() => CostModel.FormatDetail(alternatives, new Decision(1, "a", 0, "", ""), Objective.Speed));
  }

  /// <summary>
  /// Decision determinism (measurement of #155): every query of the tests answered 1000 times, each time
  /// from a profile whose mapping keys were shuffled, gives one distinct result per query. The full
  /// measurement reads 2000 profiles and is tagged <c>Slow</c>; 20 rounds stay in the fast tier.
  /// </summary>
  [Test]
  public void Decisions_AreTheSameWhateverTheOrderOfTheProfileKeys() => AssertDeterministic(20);

  [Test, Category("Slow")]
  public void Decisions_AreTheSameOverAThousandShuffledProfiles() => AssertDeterministic(1000);

  private static void AssertDeterministic(int rounds) {
    var random = new Random(155);
    var texts = new[] { "wide", "narrow" }.ToDictionary(name => name, name => File.ReadAllText(CostFixtures.PathOf(name)));
    var results = new Dictionary<string, HashSet<Decision>>();
    for (var round = 0; round < rounds; ++round)
      foreach (var (name, text) in texts) {
        var model = new CostModel(TargetCostProfile.Parse(ProfileShuffle.Shuffle(text, random), name));
        foreach (var (query, decision) in Queries(model))
          (results.TryGetValue(name + query, out var set) ? set : results[name + query] = []).Add(decision);
      }
    Assert.That(results, Has.Count.EqualTo(2 * Queries(_wide).Count()));
    Assert.That(results.Values.Select(set => set.Count), Is.All.EqualTo(1));
  }

  private static IEnumerable<(string Query, Decision Decision)> Queries(CostModel model) {
    foreach (var objective in Enum.GetValues<Objective>()) {
      foreach (var sites in new[] { 1, 10, 20, 30 })
        yield return ($"form {sites} {objective}", model.Choose(model.OperationForms(CostFixtures.Rotate, sites), objective));
      foreach (var n in new[] { 0, 3, 8, 64 })
        yield return ($"path {n} {objective}", model.Choose(model.DataPaths(CostFixtures.ByteXor, CountFact.Constant(n)), objective));
      foreach (var work in new[] { 10_000, 1_000_000 })
        yield return ($"place {work} {objective}", model.Choose(model.Placements(CountFact.Constant(work)), objective));
      yield return ($"loop {objective}", model.Choose(model.LoopForms(new(4, 4), CountFact.Constant(8)), objective));
    }
  }

}
