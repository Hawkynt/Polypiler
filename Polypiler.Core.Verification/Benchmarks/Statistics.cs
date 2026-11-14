using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Verification.Benchmarks;

/// <summary>The summary of one metric over the samples of a result.</summary>
/// <param name="Median">The median (<see cref="Statistics.Median"/>).</param>
/// <param name="Mad">The median absolute deviation (<see cref="Statistics.Mad"/>).</param>
/// <param name="Min">The smallest sample.</param>
/// <param name="Max">The largest sample.</param>
public sealed record MetricStatistics(long Median, long Mad, long Min, long Max) {

  /// <summary>The summary of a non-empty list of samples.</summary>
  /// <param name="values">The samples.</param>
  /// <exception cref="ArgumentException">The list is empty.</exception>
  public static MetricStatistics Of(IReadOnlyList<long> values)
    => new(Statistics.Median(values), Statistics.Mad(values), values.Min(), values.Max());

}

/// <summary>
/// The robust statistics of the benchmark harness on integer nanoseconds and bytes. No sample is ever
/// removed as an outlier: the median absolute deviation (Hampel, "The Influence Curve and Its Role in
/// Robust Estimation", JASA 69, 1974) states the dispersion without hiding the slow run.
/// </summary>
public static class Statistics {

  /// <summary>
  /// The median: the middle value of the sorted samples; for an even count the mean of the two middle
  /// values, rounded half to even.
  /// </summary>
  /// <param name="values">The samples, in any order.</param>
  /// <exception cref="ArgumentNullException">The list is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException">The list is empty.</exception>
  public static long Median(IReadOnlyList<long> values) {
    ArgumentNullException.ThrowIfNull(values);
    if (values.Count == 0)
      throw new ArgumentException("a median needs at least one value", nameof(values));

    var sorted = values.Order().ToArray();
    var middle = sorted.Length / 2;
    return sorted.Length % 2 == 1 ? sorted[middle] : MeanHalfToEven(sorted[middle - 1], sorted[middle]);
  }

  /// <summary>The median absolute deviation: the median of the absolute deviations from the median.</summary>
  /// <param name="values">The samples, in any order.</param>
  /// <exception cref="ArgumentNullException">The list is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException">The list is empty.</exception>
  public static long Mad(IReadOnlyList<long> values) {
    var median = Median(values);
    return Median([.. values.Select(value => (long)Int128.Abs((Int128)value - median))]);
  }

  /// <summary>The mean of two integers, a half rounded to the even neighbour; exact for every pair of <see cref="long"/> values.</summary>
  public static long MeanHalfToEven(long a, long b) {
    var sum = (Int128)a + b;
    var floor = sum >> 1;
    return (long)((sum & 1) == 1 && (floor & 1) == 1 ? floor + 1 : floor);
  }

}
