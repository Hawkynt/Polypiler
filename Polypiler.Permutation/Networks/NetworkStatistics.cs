using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Polypiler.Permutation.Networks;

/// <summary>
/// The size of a network: steps per operation, depth (the longest chain of steps from the input to the
/// result), the peak number of values live at once while the steps run in order (the input included,
/// a value being live from its step to its last use, the result to the end), and the constants - the
/// <c>Const</c> steps and the bit-pattern immediates - with the bits needed to write them.
/// </summary>
public sealed class NetworkStatistics {

  private readonly BitOperationNetwork _network;
  private readonly NetworkOpRegistry _registry;

  /// <summary>Measures a network whose operands refer to earlier values.</summary>
  /// <param name="network">The network.</param>
  /// <param name="registry">The semantics of its operations; <see cref="NetworkOpRegistry.Default"/> when <see langword="null"/>.</param>
  public NetworkStatistics(BitOperationNetwork network, NetworkOpRegistry? registry = null) {
    ArgumentNullException.ThrowIfNull(network);
    this._network = network;
    this._registry = registry ?? NetworkOpRegistry.Default;
    this.StepsPerOperation = network.Steps.GroupBy(step => step.Op).ToDictionary(group => group.Key, group => group.Count());

    var depths = new int[network.Steps.Count + 1];
    for (var value = 1; value <= network.Steps.Count; ++value)
      depths[value] = 1 + network.Steps[value - 1].Operands.Select(operand => depths[operand]).DefaultIfEmpty(0).Max();
    this.Depth = depths[network.Result];

    var lastUse = new int[network.Steps.Count + 1];
    for (var value = 1; value <= network.Steps.Count; ++value)
      foreach (var operand in network.Steps[value - 1].Operands)
        lastUse[operand] = value;
    lastUse[network.Result] = int.MaxValue;
    this.PeakLive = Enumerable.Range(0, network.Steps.Count + 1).Select(at => Enumerable.Range(0, at + 1).Count(value => value == at || lastUse[value] > at)).Max();

    var constants = network.Steps.Select(step => step.Immediate).OfType<NetworkImmediate.Bits>().Select(bits => bits.Value).ToList();
    this.Constants = constants.Count;
    this.ConstantBits = constants.Sum(value => value.IsZero ? 1L : (long)value.GetBitLength());
  }

  /// <summary>How many steps each operation has.</summary>
  public IReadOnlyDictionary<NetworkOp, int> StepsPerOperation { get; }

  /// <summary>The longest chain of steps from the input to the result; 0 for a network that is its input.</summary>
  public int Depth { get; }

  /// <summary>The most values live at once.</summary>
  public int PeakLive { get; }

  /// <summary>The constants: <c>Const</c> steps and bit-pattern immediates.</summary>
  public int Constants { get; }

  /// <summary>The bits needed to write the constants, one for a zero.</summary>
  public long ConstantBits { get; }

  /// <summary>
  /// The cost of the network under a cost table: the sum over <b>all</b> steps of the cycles of the primitive
  /// each step's operation costs; a <c>Const</c>, <c>ZeroExtend</c> or <c>Truncate</c> costs nothing.
  /// </summary>
  /// <param name="costs">The cost table.</param>
  /// <returns>The cost.</returns>
  /// <exception cref="Model.PermutationModelException">PLY0811: a primitive the table has no entry for.</exception>
  public long Cost(PrimitiveCostTable costs) {
    ArgumentNullException.ThrowIfNull(costs);
    return this._network.Steps.Sum(step => this._registry.TryGet(step.Op, out var semantics) && semantics.Primitive is { } primitive ? costs.Cycles(primitive) : 0L);
  }

}
