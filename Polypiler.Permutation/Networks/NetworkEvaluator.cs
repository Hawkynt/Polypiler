using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Polypiler.Core;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Networks;

/// <summary>
/// Evaluates a network on one input in exact integers: the input is reduced to its width, and every step's
/// value modulo 2^width. It shares no code with <see cref="NetworkLowering"/>, so the interpreter of the
/// lowered IR is an independent second evaluation.
/// </summary>
public static class NetworkEvaluator {

  /// <summary>The value of a network on an input.</summary>
  /// <param name="network">The network; its operands must refer to earlier values.</param>
  /// <param name="input">The input.</param>
  /// <param name="registry">The semantics of the operations; <see cref="NetworkOpRegistry.Default"/> when <see langword="null"/>.</param>
  /// <returns>The result.</returns>
  /// <exception cref="PermutationModelException">PLY0810: an operation without semantics, or an operand defined after its use.</exception>
  public static BigInteger Evaluate(BitOperationNetwork network, BigInteger input, NetworkOpRegistry? registry = null) {
    ArgumentNullException.ThrowIfNull(network);
    return Values(network, input, registry)[network.Result];
  }

  /// <summary>The value of every value of a network on an input, the input first.</summary>
  /// <param name="network">The network.</param>
  /// <param name="input">The input.</param>
  /// <param name="registry">The semantics of the operations.</param>
  /// <returns>The values, indexed as the network numbers them.</returns>
  public static IReadOnlyList<BigInteger> Values(BitOperationNetwork network, BigInteger input, NetworkOpRegistry? registry = null) {
    ArgumentNullException.ThrowIfNull(network);
    registry ??= NetworkOpRegistry.Default;
    var values = new List<BigInteger> { input & Steps.Mask(network.InputWidth) };
    foreach (var (step, number) in network.Steps.Select((step, index) => (step, index + 1))) {
      if (!registry.TryGet(step.Op, out var semantics))
        throw Refused(number, step, "is not registered");
      if (step.Operands.FirstOrDefault(operand => operand < 0 || operand >= number, -1) is var undefined && undefined >= 0)
        throw Refused(number, step, $"operand v{undefined.ToString(CultureInfo.InvariantCulture)} is not defined before use");

      values.Add(semantics.Evaluate(step, [.. step.Operands.Select(operand => values[operand])]));
    }

    return values;
  }

  private static PermutationModelException Refused(int number, NetworkStep step, string problem)
    => new(Diagnostic.Create(NetworkDiagnostics.IllFormed, null, number, step.Op, problem));

}
