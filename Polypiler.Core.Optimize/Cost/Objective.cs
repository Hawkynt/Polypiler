using System;

namespace Polypiler.Core.Optimize.Cost;

/// <summary>What the cost model minimises: the four optimizing levels <c>--opt balanced|speed|size|memory</c>.</summary>
public enum Objective {

  /// <summary>Time first, the rest counted: weights latency 4, size 1, memory 1, allocations 1.</summary>
  Balanced,

  /// <summary>Time alone: weights latency 1, size 0, memory 0, allocations 0.</summary>
  Speed,

  /// <summary>Code size alone: weights latency 0, size 1, memory 0, allocations 0.</summary>
  Size,

  /// <summary>Memory and allocations: weights latency 0, size 0, memory 1, allocations 1.</summary>
  Memory

}

/// <summary>The weight table of the objectives.</summary>
public static class Objectives {

  /// <summary>The weights an objective gives latency, size, memory and allocations.</summary>
  /// <param name="objective">The objective.</param>
  /// <returns>The four weights.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The objective is not a member of <see cref="Objective"/>.</exception>
  public static (long Latency, long Size, long Memory, long Allocations) Weights(Objective objective) => objective switch {
    Objective.Balanced => (4, 1, 1, 1),
    Objective.Speed => (1, 0, 0, 0),
    Objective.Size => (0, 1, 0, 0),
    Objective.Memory => (0, 0, 1, 1),
    _ => throw new ArgumentOutOfRangeException(nameof(objective), objective, "no such objective")
  };

}
