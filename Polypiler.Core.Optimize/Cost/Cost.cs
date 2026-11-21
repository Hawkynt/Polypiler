using System;

namespace Polypiler.Core.Optimize.Cost;

/// <summary>
/// What one form of a computation costs on a target, in abstract integer units of the target's cost
/// profile: time on the critical path, code size, memory held and the number of allocations. Costs add
/// component-wise; a result beyond the range of <see cref="long"/> is <see cref="long.MaxValue"/>.
/// </summary>
/// <param name="Latency">The time the form takes.</param>
/// <param name="Size">The code the form adds.</param>
/// <param name="Memory">The memory the form holds.</param>
/// <param name="Allocations">The allocations the form makes.</param>
public readonly record struct Cost(long Latency, long Size, long Memory = 0, long Allocations = 0) {

  /// <summary>Nothing at all.</summary>
  public static Cost Zero => default;

  /// <summary>The component-wise sum, each component saturating at <see cref="long.MaxValue"/>.</summary>
  /// <param name="left">The first cost.</param>
  /// <param name="right">The second cost.</param>
  /// <returns>The sum.</returns>
  public static Cost operator +(Cost left, Cost right) => new(
    CostFormulas.Add(left.Latency, right.Latency),
    CostFormulas.Add(left.Size, right.Size),
    CostFormulas.Add(left.Memory, right.Memory),
    CostFormulas.Add(left.Allocations, right.Allocations));

  /// <summary>The cost repeated: every component multiplied, saturating at <see cref="long.MaxValue"/>.</summary>
  /// <param name="cost">The cost.</param>
  /// <param name="times">How often.</param>
  /// <returns>The product.</returns>
  public static Cost operator *(Cost cost, long times) => new(
    CostFormulas.Multiply(cost.Latency, times),
    CostFormulas.Multiply(cost.Size, times),
    CostFormulas.Multiply(cost.Memory, times),
    CostFormulas.Multiply(cost.Allocations, times));

  /// <summary>The component-wise sum, as a method.</summary>
  /// <param name="other">The cost to add.</param>
  /// <returns>The sum.</returns>
  public Cost Add(Cost other) => this + other;

  /// <summary>The cost repeated, as a method.</summary>
  /// <param name="times">How often.</param>
  /// <returns>The product.</returns>
  public Cost Multiply(long times) => this * times;

  /// <summary>
  /// The score under an objective: the sum of the components weighted by <see cref="Objectives.Weights"/>,
  /// computed exactly and <see cref="long.MaxValue"/> where it leaves the range of <see cref="long"/>.
  /// </summary>
  /// <param name="objective">What to minimise.</param>
  /// <returns>The score; lower is better.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The objective is not a member of <see cref="Objective"/>.</exception>
  public long Score(Objective objective) {
    var (latency, size, memory, allocations) = Objectives.Weights(objective);
    var sum = (Int128)this.Latency * latency + (Int128)this.Size * size + (Int128)this.Memory * memory + (Int128)this.Allocations * allocations;
    return CostFormulas.Clamp(sum);
  }

}
