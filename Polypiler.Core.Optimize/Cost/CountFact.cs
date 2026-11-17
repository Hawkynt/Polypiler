using System;

namespace Polypiler.Core.Optimize.Cost;

/// <summary>
/// What is known of a count the formulas take — an element count, a trip count, a number of work
/// units: a constant, a proven range, or nothing.
/// </summary>
public readonly record struct CountFact {

  private CountFact(long low, long high) {
    this.Low = low;
    this.High = high;
    this.IsKnown = true;
  }

  /// <summary>The smallest value the count can have; meaningful when <see cref="IsKnown"/>.</summary>
  public long Low { get; }

  /// <summary>The largest value the count can have; meaningful when <see cref="IsKnown"/>.</summary>
  public long High { get; }

  /// <summary>Whether a constant or a range is known.</summary>
  public bool IsKnown { get; }

  /// <summary>Nothing is known: the profile's <c>unknownTripCount</c> stands in.</summary>
  public static CountFact Unknown => default;

  /// <summary>A count known exactly.</summary>
  /// <param name="value">The count, not negative.</param>
  /// <returns>The fact.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
  public static CountFact Constant(long value) => Range(value, value);

  /// <summary>A count proven to lie in a range, both bounds included.</summary>
  /// <param name="low">The lower bound, not negative.</param>
  /// <param name="high">The upper bound, not below <paramref name="low"/>.</param>
  /// <returns>The fact.</returns>
  /// <exception cref="ArgumentOutOfRangeException">A bound is negative, or the range is empty.</exception>
  public static CountFact Range(long low, long high) {
    ArgumentOutOfRangeException.ThrowIfNegative(low);
    ArgumentOutOfRangeException.ThrowIfLessThan(high, low);
    return new(low, high);
  }

  /// <summary>The count the formulas use: the midpoint of the range, rounded down, or <paramref name="unknown"/> without a fact.</summary>
  /// <param name="unknown">The profile's <c>unknownTripCount</c>.</param>
  /// <returns>The count.</returns>
  public long Resolve(long unknown) => this.IsKnown ? this.Low + (this.High - this.Low) / 2 : unknown;

}
