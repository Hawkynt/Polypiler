using System;
using NUnit.Framework;
using Polypiler.Core.Optimize.Cost;
using CostValue = Polypiler.Core.Optimize.Cost.Cost;

namespace Polypiler.Tests.Optimize.Cost;

/// <summary>The formulas, the weights and the saturating arithmetic of the cost model (#155).</summary>
[TestFixture]
public sealed class CostFormulasTests {

  [Test]
  public void Helper_PaysTheCallPerSiteAndTheBodyOnce()
    => Assert.That(CostFormulas.Helper(new(12, 2), new(0, 20), 30), Is.EqualTo(new CostValue(360, 80)));

  [Test]
  public void Loop_AndUnrolled_FollowTheFormulas() {
    Assert.That(CostFormulas.Loop(new(4, 4, 7, 1), 2, 8), Is.EqualTo(new CostValue(48, 6, 7, 1)));
    Assert.That(CostFormulas.Unrolled(new(4, 4, 7, 1), 8), Is.EqualTo(new CostValue(32, 32, 7, 1)));
  }

  [TestCase(64, 16, 8)]
  [TestCase(64, 8, 12)]
  [TestCase(3, 16, 7)]
  [TestCase(17, 16, 6)]
  [TestCase(0, 8, 4)]
  public void Blocked_DividesIntoBlocksAndATail(long n, long lanes, long latency)
    => Assert.That(CostFormulas.Blocked(4, 1, new(1, 1), n, lanes), Is.EqualTo(new CostValue(latency, 6)));

  [Test]
  public void Blocked_RefusesNoLanesAndANegativeCount() {
    Assert.Throws<ArgumentOutOfRangeException>(() => CostFormulas.Blocked(4, 1, new(1, 1), 8, 0));
    Assert.Throws<ArgumentOutOfRangeException>(() => CostFormulas.Blocked(4, 1, new(1, 1), -1, 8));
  }

  [TestCase(1_000_000, 4, 260_000)]
  [TestCase(10_000, 4, 12_500)]
  [TestCase(10_001, 4, 12_501)]
  [TestCase(0, 4, 10_000)]
  [TestCase(5, 1, 10_005)]
  public void Threaded_RoundsTheShareUp(long work, long threads, long latency)
    => Assert.That(CostFormulas.Threaded(10_000, work, threads), Is.EqualTo(new CostValue(latency, 0)));

  [Test]
  public void Threaded_RefusesNoThreadsAndNegativeWork() {
    Assert.Throws<ArgumentOutOfRangeException>(() => CostFormulas.Threaded(1, 1, 0));
    Assert.Throws<ArgumentOutOfRangeException>(() => CostFormulas.Threaded(1, -1, 1));
  }

  [Test]
  public void Offloaded_PaysTheTransferAndEachElement()
    => Assert.That(CostFormulas.Offloaded(1000, 3, 10), Is.EqualTo(new CostValue(1030, 0)));

  [TestCase(1, 2, 3)]
  [TestCase(long.MaxValue, 0, long.MaxValue)]
  [TestCase(long.MaxValue, 1, long.MaxValue)]
  [TestCase(long.MinValue, -1, long.MaxValue)]
  [TestCase(-5, 3, -2)]
  public void Add_SaturatesAtMaxValue(long left, long right, long sum) => Assert.That(CostFormulas.Add(left, right), Is.EqualTo(sum));

  [TestCase(3, 4, 12)]
  [TestCase(1L << 31, 1L << 31, 1L << 62)]
  [TestCase(1L << 32, 1L << 31, long.MaxValue)]
  [TestCase(long.MinValue, 2, long.MaxValue)]
  public void Multiply_SaturatesAtMaxValue(long left, long right, long product) => Assert.That(CostFormulas.Multiply(left, right), Is.EqualTo(product));

  [Test]
  public void Costs_AddAndRepeatComponentWise() {
    var cost = new CostValue(1, 2, 3, 4);
    Assert.That(cost + new CostValue(10, 20, 30, 40), Is.EqualTo(new CostValue(11, 22, 33, 44)));
    Assert.That(cost.Add(CostValue.Zero), Is.EqualTo(cost));
    Assert.That(cost * 3, Is.EqualTo(new CostValue(3, 6, 9, 12)));
    Assert.That(cost.Multiply(0), Is.EqualTo(CostValue.Zero));
    Assert.That(new CostValue(long.MaxValue, 0) + new CostValue(1, 0), Is.EqualTo(new CostValue(long.MaxValue, 0)));
  }

  // The weights of the issue: Speed (1, 0, 0, 0), Size (0, 1, 0, 0), Memory (0, 0, 1, 1), Balanced (4, 1, 1, 1).
  [TestCase(Objective.Speed, 1)]
  [TestCase(Objective.Size, 2)]
  [TestCase(Objective.Memory, 7)]
  [TestCase(Objective.Balanced, 4 + 2 + 3 + 4)]
  public void Score_IsTheWeightedSum(Objective objective, long score)
    => Assert.That(new CostValue(1, 2, 3, 4).Score(objective), Is.EqualTo(score));

  [Test]
  public void Weights_RefuseAnObjectiveThatIsNoMember() {
    Assert.Throws<ArgumentOutOfRangeException>(() => Objectives.Weights((Objective)4));
    Assert.That(Objectives.Weights(Objective.Balanced), Is.EqualTo((4L, 1L, 1L, 1L)));
  }

  [Test]
  public void Score_SaturatesBeyondTheRangeOfLong() {
    Assert.That(new CostValue(1L << 62, 0).Score(Objective.Balanced), Is.EqualTo(long.MaxValue));
    Assert.That(new CostValue(long.MinValue, 0).Score(Objective.Balanced), Is.EqualTo(long.MaxValue));
    Assert.That(new CostValue(long.MaxValue, 0).Score(Objective.Speed), Is.EqualTo(long.MaxValue));
  }

  [Test]
  public void CountFact_ResolvesToTheConstantTheMidpointOrTheProfilesCount() {
    Assert.That(CountFact.Constant(8).Resolve(16), Is.EqualTo(8));
    Assert.That(CountFact.Range(0, 100).Resolve(16), Is.EqualTo(50));
    Assert.That(CountFact.Range(1, 4).Resolve(16), Is.EqualTo(2));
    Assert.That(CountFact.Range(0, long.MaxValue).Resolve(16), Is.EqualTo(long.MaxValue / 2));
    Assert.That(CountFact.Unknown.Resolve(16), Is.EqualTo(16));
    Assert.That((CountFact.Range(3, 9).Low, CountFact.Range(3, 9).High, CountFact.Range(3, 9).IsKnown, CountFact.Unknown.IsKnown), Is.EqualTo((3L, 9L, true, false)));
  }

  [Test]
  public void CountFact_RefusesNegativeAndEmptyRanges() {
    Assert.Throws<ArgumentOutOfRangeException>(() => CountFact.Constant(-1));
    Assert.Throws<ArgumentOutOfRangeException>(() => CountFact.Range(5, 4));
  }

}
