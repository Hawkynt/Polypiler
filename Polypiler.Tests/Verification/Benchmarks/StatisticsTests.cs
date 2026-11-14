using System;
using NUnit.Framework;
using Polypiler.Core.Verification.Benchmarks;

namespace Polypiler.Tests.Verification.Benchmarks;

/// <summary>The statistics against the values worked out by hand in the issue; no sample is ever removed.</summary>
[TestFixture]
public sealed class StatisticsTests {

  /// <summary>Gherkin outline and test design 3: sample counts one, two, odd and even.</summary>
  [TestCase(new long[] { 10, 12, 11, 50, 13 }, 12L, 1L)]
  [TestCase(new long[] { 10, 12, 14, 20 }, 13L, 2L)]
  [TestCase(new long[] { 7 }, 7L, 0L)]
  [TestCase(new long[] { 10, 20 }, 15L, 5L)]
  public void MedianAndMad_AreTheWorkedValues(long[] samples, long median, long mad)
    => Assert.That((Statistics.Median(samples), Statistics.Mad(samples)), Is.EqualTo((median, mad)));

  /// <summary>Test design 4: a two-value median that ends in a half is rounded to the even neighbour.</summary>
  [TestCase(1L, 2L, 2L)]
  [TestCase(3L, 4L, 4L)]
  [TestCase(-3L, -2L, -2L)]
  [TestCase(-2L, -1L, -2L)]
  [TestCase(long.MaxValue, long.MaxValue, long.MaxValue)]
  public void ATwoValueMedian_RoundsHalfToEven(long a, long b, long median)
    => Assert.That(Statistics.Median([a, b]), Is.EqualTo(median));

  [Test]
  public void TheOrderOfTheSamples_DoesNotMatter()
    => Assert.That(Statistics.Median([50, 13, 10, 12, 11]), Is.EqualTo(12));

  [Test]
  public void AnOutlier_IsKept_InMinAndMax()
    => Assert.That(MetricStatistics.Of([10, 12, 11, 50, 13]), Is.EqualTo(new MetricStatistics(12, 1, 10, 50)));

  [Test]
  public void NoSamples_HaveNoMedian() {
    Assert.That(() => Statistics.Median([]), Throws.ArgumentException);
    Assert.That(() => Statistics.Mad([]), Throws.ArgumentException);
    Assert.That(() => Statistics.Median(null!), Throws.ArgumentNullException);
  }

}
