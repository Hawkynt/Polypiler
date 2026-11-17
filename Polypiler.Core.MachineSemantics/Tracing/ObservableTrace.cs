using System;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.MachineSemantics.Tracing;

/// <summary>How a traced run ended.</summary>
public enum TraceOutcome {

  /// <summary>The routine returned to its caller.</summary>
  Returned,

  /// <summary>The routine ended in a trap.</summary>
  Trapped,

  /// <summary>The run was abandoned for a reason outside the routine.</summary>
  Aborted,

  /// <summary>The vector's <c>limits</c> ran out before the routine returned.</summary>
  BudgetExceeded

}

/// <summary>
/// What a run of a routine showed to the outside (PRD §23.1): the final state, the ordered events, how
/// it ended and, where the producer counts them, the cycles it took.
/// </summary>
/// <param name="Final">The state at the end.</param>
/// <param name="Events">The events, in the order they happened.</param>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Cycles">The cycle count, or <see langword="null"/> when the producer does not count cycles.</param>
public sealed record ObservableTrace(MachineSnapshot Final, ImmutableArray<TraceEvent> Events, TraceOutcome Outcome, long? Cycles = null) {

  /// <inheritdoc/>
  public bool Equals(ObservableTrace? other)
    => other is not null && Equals(this.Final, other.Final) && this.Events.SequenceEqual(other.Events) && this.Outcome == other.Outcome && this.Cycles == other.Cycles;

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Final, this.Events.Length, this.Outcome, this.Cycles);

}
