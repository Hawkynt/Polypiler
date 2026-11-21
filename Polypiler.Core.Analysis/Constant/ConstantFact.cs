using System.Globalization;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Constant;

/// <summary>What a constant fact says of a value.</summary>
public enum ConstantKind {

  /// <summary>Any value is possible.</summary>
  Unknown,

  /// <summary>The value is always <see cref="ConstantFact.Value"/>.</summary>
  Constant,

  /// <summary>No execution reaches the point.</summary>
  Unreachable

}

/// <summary>
/// The fact of the constant domain: a value is one integer or truth value on every execution reaching the
/// point, carried as an <see cref="IrConst"/> carries it - truth values as 0 and 1, a <c>u64</c> as its
/// 64 bits.
/// </summary>
/// <param name="Kind">What the fact says.</param>
/// <param name="Value">The value of a <see cref="ConstantKind.Constant"/> fact; 0 otherwise.</param>
public readonly record struct ConstantFact(ConstantKind Kind, long Value) : IFact<ConstantFact> {

  /// <summary>The fact that claims nothing.</summary>
  public static ConstantFact Unknown => default;

  /// <summary>The fact of a point no execution reaches.</summary>
  public static ConstantFact Unreachable { get; } = new(ConstantKind.Unreachable, 0);

  /// <summary>The fact that a value is always the given one.</summary>
  /// <param name="value">The value, as a constant carries it.</param>
  /// <returns>The fact.</returns>
  public static ConstantFact Of(long value) => new(ConstantKind.Constant, value);

  /// <summary>Whether the fact names one value.</summary>
  public bool IsConstant => this.Kind == ConstantKind.Constant;

  /// <inheritdoc/>
  public static IFactDomain<ConstantFact> Domain => ConstantDomain.Instance;

  /// <inheritdoc/>
  public override string ToString() => ConstantDomain.Instance.Describe(this);

}

/// <summary>The lattice of <see cref="ConstantFact"/>: Unreachable below every constant, every constant below Unknown, two constants apart.</summary>
public sealed class ConstantDomain : IFactDomain<ConstantFact> {

  private ConstantDomain() { }

  /// <summary>The domain.</summary>
  public static ConstantDomain Instance { get; } = new();

  /// <inheritdoc/>
  public string Name => "constant";

  /// <inheritdoc/>
  public ConstantFact Unknown => ConstantFact.Unknown;

  /// <inheritdoc/>
  public ConstantFact Unreachable => ConstantFact.Unreachable;

  /// <inheritdoc/>
  public ConstantFact Join(ConstantFact left, ConstantFact right) => left.Kind == ConstantKind.Unreachable ? right
    : right.Kind == ConstantKind.Unreachable || left == right ? left
    : ConstantFact.Unknown;

  /// <inheritdoc/>
  public ConstantFact Meet(ConstantFact left, ConstantFact right) => left.Kind == ConstantKind.Unknown ? right
    : right.Kind == ConstantKind.Unknown || left == right ? left
    : ConstantFact.Unreachable;

  /// <inheritdoc/>
  public ConstantFact Widen(ConstantFact previous, ConstantFact next) => this.Join(previous, next);

  /// <inheritdoc/>
  public bool Implies(ConstantFact stronger, ConstantFact weaker) => this.Join(stronger, weaker) == weaker;

  /// <inheritdoc/>
  public bool Contains(ConstantFact fact, object value) => fact.Kind switch {
    ConstantKind.Unknown => true,
    ConstantKind.Constant => value is IrConst constant && constant.Value == fact.Value,
    _ => false
  };

  /// <inheritdoc/>
  public string Describe(ConstantFact fact) => fact.Kind switch {
    ConstantKind.Constant => "constant " + fact.Value.ToString(CultureInfo.InvariantCulture),
    ConstantKind.Unknown => "unknown",
    _ => "unreachable"
  };

}
