using System;
using System.Globalization;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Range;

/// <summary>
/// The fact of the range domain: every value lies in <c>[Min, Max]</c>, as a mathematical integer (the
/// bits of a <c>u64</c> read as unsigned, <see cref="IrOperators.Number"/>; truth values as 0 and 1).
/// The whole of <see cref="Int128"/> is Unknown; a range with <c>Min &gt; Max</c> is Unreachable.
/// </summary>
/// <param name="Min">The smallest value.</param>
/// <param name="Max">The largest value.</param>
public readonly record struct ValueRange(Int128 Min, Int128 Max) : IFact<ValueRange> {

  /// <summary>Every value.</summary>
  public static ValueRange Unknown { get; } = new(Int128.MinValue, Int128.MaxValue);

  /// <summary>No value: no execution reaches the point.</summary>
  public static ValueRange Unreachable { get; } = new(1, 0);

  /// <inheritdoc/>
  public static IFactDomain<ValueRange> Domain => RangeDomain.Instance;

  /// <summary>The range of one value.</summary>
  /// <param name="value">The value.</param>
  /// <returns>The range <c>[value, value]</c>.</returns>
  public static ValueRange Of(Int128 value) => new(value, value);

  /// <summary>Every value of an integer type, or Unknown for another type.</summary>
  /// <param name="type">The type.</param>
  /// <returns>The range of the type.</returns>
  public static ValueRange Of(IrType type) {
    ArgumentNullException.ThrowIfNull(type);
    return type.IsInteger ? new(IrOperators.Minimum(type), IrOperators.Maximum(type)) : type == IrType.Bool ? new(0, 1) : Unknown;
  }

  /// <summary>Whether the range holds no value.</summary>
  public bool IsEmpty => this.Min > this.Max;

  /// <summary>Whether the range claims nothing.</summary>
  public bool IsUnknown => this == Unknown;

  /// <summary>Whether a value lies in the range.</summary>
  /// <param name="value">The value.</param>
  /// <returns><see langword="true"/> when <c>Min ≤ value ≤ Max</c>.</returns>
  public bool Contains(Int128 value) => this.Min <= value && value <= this.Max;

  /// <summary>Whether every value of another range lies in this one.</summary>
  /// <param name="other">The other range.</param>
  /// <returns><see langword="true"/> when it does; an empty range lies in every range.</returns>
  public bool Covers(ValueRange other) => other.IsEmpty || (this.Min <= other.Min && other.Max <= this.Max);

  /// <inheritdoc/>
  public override string ToString() => RangeDomain.Instance.Describe(this);

}

/// <summary>The lattice of intervals: join is the hull, meet the intersection, widening moves a growing bound to the end of <see cref="Int128"/>.</summary>
public sealed class RangeDomain : IFactDomain<ValueRange> {

  private RangeDomain() { }

  /// <summary>The domain.</summary>
  public static RangeDomain Instance { get; } = new();

  /// <inheritdoc/>
  public string Name => "range";

  /// <inheritdoc/>
  public ValueRange Unknown => ValueRange.Unknown;

  /// <inheritdoc/>
  public ValueRange Unreachable => ValueRange.Unreachable;

  /// <inheritdoc/>
  public ValueRange Join(ValueRange left, ValueRange right)
    => left.IsEmpty ? right : right.IsEmpty ? left : new(Int128.Min(left.Min, right.Min), Int128.Max(left.Max, right.Max));

  /// <inheritdoc/>
  public ValueRange Meet(ValueRange left, ValueRange right) {
    var meet = new ValueRange(Int128.Max(left.Min, right.Min), Int128.Min(left.Max, right.Max));
    return meet.IsEmpty ? ValueRange.Unreachable : meet;
  }

  /// <inheritdoc/>
  public ValueRange Widen(ValueRange previous, ValueRange next) {
    if (previous.IsEmpty || next.IsEmpty)
      return this.Join(previous, next);

    return new(next.Min < previous.Min ? Int128.MinValue : previous.Min, next.Max > previous.Max ? Int128.MaxValue : previous.Max);
  }

  /// <inheritdoc/>
  public bool Implies(ValueRange stronger, ValueRange weaker) => weaker.Covers(stronger);

  /// <inheritdoc/>
  public bool Contains(ValueRange fact, object value) => value is IrConst constant
    ? fact.Contains(constant.Type == IrType.Bool ? constant.Value : IrOperators.Number(constant.Type, constant.Value))
    : fact.IsUnknown;

  /// <inheritdoc/>
  public string Describe(ValueRange fact) => fact.IsEmpty ? "unreachable" : fact.IsUnknown ? "unknown"
    : string.Create(CultureInfo.InvariantCulture, $"[{fact.Min}, {fact.Max}]");

}
