using System.Globalization;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Scev;

/// <summary>
/// How many times the body of a loop runs, for every execution that leaves the loop at all (#350): an
/// <see cref="Exact"/> number, a <see cref="Symbolic"/> one - a value of the function plus an offset - an
/// upper bound (<see cref="Max"/>), or <see cref="Unknown"/>. An execution that stops inside the loop,
/// by a trap, ends the whole run and is no counterexample. A count is never guessed: an
/// <see cref="Exact"/> or <see cref="Symbolic"/> one holds for every such execution, a <see cref="Max"/>
/// one is never exceeded.
/// </summary>
public abstract record TripCount {

  private protected TripCount() { }

  /// <summary>Nothing is known of the count.</summary>
  public static TripCount Unknown { get; } = new Opaque();

  /// <summary>The body runs exactly <paramref name="Count"/> times.</summary>
  /// <param name="Count">The count, 0 or more.</param>
  public sealed record Exact(BigInteger Count) : TripCount {

    /// <inheritdoc/>
    public override string ToString() => $"Exact({this.Count.ToString(CultureInfo.InvariantCulture)})";

  }

  /// <summary>The body runs <c>Value + Offset</c> times, the value as it is where the loop is entered.</summary>
  /// <param name="Value">An integer expression the loop does not change.</param>
  /// <param name="Offset">What is added to it.</param>
  public sealed record Symbolic(IrExpr Value, BigInteger Offset) : TripCount {

    /// <inheritdoc/>
    public override string ToString() => $"Symbolic({AnalysisDiagnostics.Describe(this.Value)}, {this.Offset.ToString(CultureInfo.InvariantCulture)})";

  }

  /// <summary>The body runs at most <paramref name="Bound"/> times.</summary>
  /// <param name="Bound">The bound, 0 or more.</param>
  public sealed record Max(BigInteger Bound) : TripCount {

    /// <inheritdoc/>
    public override string ToString() => $"Max({this.Bound.ToString(CultureInfo.InvariantCulture)})";

  }

  /// <summary>The case that claims nothing.</summary>
  private sealed record Opaque : TripCount {

    /// <inheritdoc/>
    public override string ToString() => "Unknown";

  }

}
