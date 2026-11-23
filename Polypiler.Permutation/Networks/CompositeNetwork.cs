using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Polypiler.Core;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Networks;

/// <summary>
/// Assembles the terms of a routing into one network, as BitPerm's composite assembly does: the <c>Or</c>
/// of the terms, then <c>Xor</c> with the mapping's <see cref="BitMapping.InvertMask"/>, then <c>Or</c> with
/// its <see cref="BitMapping.OnesMask"/> (BitPerm's <c>logicXor</c> and <c>logicOr</c>, converted). A
/// non-square mapping is assembled at the larger of its widths: the input is zero-extended first and the
/// result truncated last; the terms work on bit positions and are unaffected.
/// </summary>
public static class CompositeNetwork {

  /// <summary>Assembles terms into a network that computes a mapping.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <param name="terms">The terms, each emitted at the assembly width over the (extended) input.</param>
  /// <param name="registry">The semantics of the operations the terms emit; <see cref="NetworkOpRegistry.Default"/> when <see langword="null"/>.</param>
  /// <returns>The network, from the mapping's input width to its output width.</returns>
  /// <exception cref="PermutationModelException">
  /// PLY0810: a term breaks the term contract - it sets bits outside its routed mask or gives a routed bit
  /// other than its source - two terms route the same bit, or a source bit of the mapping is routed by no term.
  /// </exception>
  public static BitOperationNetwork Assemble(BitMapping mapping, IReadOnlyList<PrimitiveCandidate> terms, NetworkOpRegistry? registry = null) {
    ArgumentNullException.ThrowIfNull(mapping);
    ArgumentNullException.ThrowIfNull(terms);
    registry ??= NetworkOpRegistry.Default;
    var width = Math.Max(mapping.InputWidth, mapping.OutputWidth);
    var builder = new NetworkBuilder(mapping.InputWidth);
    var input = builder.ZeroExtend(NetworkBuilder.Input, width);

    var routed = BigInteger.Zero;
    var values = new List<int>();
    foreach (var (term, number) in terms.Select((term, index) => (term, index + 1))) {
      if ((routed & term.Routed) is { IsZero: false } overlap)
        throw Refused($"term {Number(number)} routes bits {NetworkText.Hex(overlap)} another term routes");

      var value = term.Emit(builder);
      Check(mapping, term, number, builder.Build(value), registry);
      routed |= term.Routed;
      values.Add(value);
    }

    if ((Sourced(mapping) & ~routed) is { IsZero: false } missing)
      throw Refused($"source bits {NetworkText.Hex(missing)} are routed by no term");

    var result = values.Count == 0 ? builder.Const(width, 0) : values.Skip(1).Aggregate(values[0], builder.Or);
    result = builder.OrConstant(builder.XorConstant(result, mapping.InvertMask), mapping.OnesMask);
    return builder.Build(builder.Truncate(result, mapping.OutputWidth));
  }

  /// <summary>The output bits whose source is an input bit, inverted or not.</summary>
  private static BigInteger Sourced(BitMapping mapping) {
    var bits = BigInteger.Zero;
    for (var output = 0; output < mapping.OutputWidth; ++output)
      if (mapping.Outputs[output].ReadsInput)
        bits |= BigInteger.One << output;

    return bits;
  }

  /// <summary>
  /// The term contract on the term's own network: no bit outside its routed mask for an input of all ones,
  /// nothing for zero, and for each input bit alone exactly the routed outputs that read it. A routing term
  /// is linear in its input bits, so these inputs decide it.
  /// </summary>
  private static void Check(BitMapping mapping, PrimitiveCandidate term, int number, BitOperationNetwork network, NetworkOpRegistry registry) {
    var ones = Steps.Mask(mapping.InputWidth);
    if ((NetworkEvaluator.Evaluate(network, ones, registry) & ~term.Routed) is { IsZero: false } leak)
      throw Refused($"term {Number(number)} sets bits {NetworkText.Hex(leak)} outside its routed mask {NetworkText.Hex(term.Routed)}");

    for (var source = -1; source < mapping.InputWidth; ++source) {
      var input = source < 0 ? BigInteger.Zero : BigInteger.One << source;
      var expected = BigInteger.Zero;
      for (var output = 0; output < mapping.OutputWidth; ++output)
        if (!(term.Routed >> output).IsEven && mapping.Outputs[output].ReadsInput && mapping.Outputs[output].Index == source)
          expected |= BigInteger.One << output;

      if (NetworkEvaluator.Evaluate(network, input, registry) is var actual && actual != expected)
        throw Refused($"term {Number(number)} gives {NetworkText.Hex(actual)} for input {NetworkText.Hex(input)}, its routed bits want {NetworkText.Hex(expected)}");
    }
  }

  private static string Number(int number) => number.ToString(CultureInfo.InvariantCulture);

  private static PermutationModelException Refused(string problem) => new(Diagnostic.Create(NetworkDiagnostics.IllFormed, null, 0, "Assemble", problem));

}
