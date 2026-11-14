using System;
using System.Numerics;
using Polypiler.Core;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;

namespace Polypiler.Permutation.Evaluation;

/// <summary>
/// The canonical evaluation of a <see cref="BitMapping"/> (PRD §20.2, §20.6): the slow, obvious
/// reference every network, rewrite and generated routine is compared with. Output bit <c>i</c> is input
/// bit <c>j</c> for <see cref="BitSourceKind.Source"/>, its complement for
/// <see cref="BitSourceKind.InvertedSource"/>, 0 for <see cref="BitSourceKind.Zero"/>, 1 for
/// <see cref="BitSourceKind.One"/> and 0 for <see cref="BitSourceKind.DontCare"/> — the canonical
/// completion that keeps the function total; comparisons go through <see cref="AgreesOnRequired"/> and
/// never rely on it. Every overload is one loop over the output entries that reads one input bit per
/// entry: no group shifts, no tables, nothing shared with the networks it judges.
/// </summary>
public static class MappingEvaluator {

  /// <summary>Evaluates a mapping on an input of any width.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <param name="input">The input, 0 to <c>2^InputWidth - 1</c>.</param>
  /// <returns>The output, 0 to <c>2^OutputWidth - 1</c>.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="input"/> is negative or has a bit at or above the input width.</exception>
  public static BigInteger Evaluate(BitMapping mapping, BigInteger input) {
    ArgumentNullException.ThrowIfNull(mapping);
    RequireFits(input, mapping.InputWidth, "input", nameof(input));

    var inputBytes = input.ToByteArray(isUnsigned: true);
    var outputBytes = new byte[(mapping.OutputWidth + 7) / 8];
    for (var bit = 0; bit < mapping.OutputWidth; ++bit)
      if (OutputBit(mapping.Outputs[bit], index => index < inputBytes.Length * 8 && (inputBytes[index >> 3] >> (index & 7) & 1) != 0))
        outputBytes[bit >> 3] |= (byte)(1 << (bit & 7));

    return new(outputBytes, isUnsigned: true);
  }

  /// <summary>Evaluates a mapping whose input and output fit 64 bits.</summary>
  /// <param name="mapping">The mapping; both widths at most 64.</param>
  /// <param name="input">The input, 0 to <c>2^InputWidth - 1</c>.</param>
  /// <returns>The output.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException">A width of the mapping is above 64.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="input"/> has a bit at or above the input width.</exception>
  public static ulong Evaluate(BitMapping mapping, ulong input) {
    ArgumentNullException.ThrowIfNull(mapping);
    if (mapping.InputWidth > 64 || mapping.OutputWidth > 64)
      throw new ArgumentException($"the ulong overload needs widths of at most 64 bits; this mapping maps {mapping.InputWidth} input bits to {mapping.OutputWidth} output bits", nameof(mapping));

    RequireFits(input, mapping.InputWidth, "input", nameof(input));

    var output = 0UL;
    for (var bit = 0; bit < mapping.OutputWidth; ++bit)
      if (OutputBit(mapping.Outputs[bit], index => (input >> index & 1) != 0))
        output |= 1UL << bit;
    return output;
  }

  /// <summary>
  /// Evaluates a mapping on little-endian 64-bit limbs: limb 0 holds bits 0 to 63. Input and output
  /// hold <c>⌈width / 64⌉</c> limbs each; the unused high bits of the last output limb are written as 0.
  /// </summary>
  /// <param name="mapping">The mapping.</param>
  /// <param name="input">The input limbs; no bit at or above the input width may be set.</param>
  /// <param name="output">Receives the output limbs; it must not overlap <paramref name="input"/>.</param>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException">A limb count is wrong, the spans overlap, or the input has a bit at or above its width.</exception>
  public static void Evaluate(BitMapping mapping, ReadOnlySpan<ulong> input, Span<ulong> output) {
    ArgumentNullException.ThrowIfNull(mapping);
    RequireLimbs(mapping.InputWidth, input.Length, "input", nameof(input));
    RequireLimbs(mapping.OutputWidth, output.Length, "output", nameof(output));
    if (input.Overlaps(output))
      throw new ArgumentException("input and output overlap", nameof(output));

    var unused = mapping.InputWidth % 64;
    if (unused != 0 && input[^1] >> unused != 0)
      throw new ArgumentException($"input has bits set at or above its width of {mapping.InputWidth} bits", nameof(input));

    var source = input.ToArray();
    output.Clear();
    for (var bit = 0; bit < mapping.OutputWidth; ++bit)
      if (OutputBit(mapping.Outputs[bit], index => (source[index >> 6] >> (index & 63) & 1) != 0))
        output[bit >> 6] |= 1UL << (bit & 63);
  }

  /// <summary>
  /// Evaluates a routing on an input its condition admits; the condition is checked by
  /// <see cref="BitCondition.Admits"/> before anything is evaluated.
  /// </summary>
  /// <param name="routing">The routing.</param>
  /// <param name="input">The input, 0 to <c>2^InputWidth - 1</c>.</param>
  /// <returns>The output.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="routing"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="input"/> is negative or has a bit at or above the input width.</exception>
  /// <exception cref="PermutationModelException">The input lies outside the declared input domain (<c>PLY0806</c>).</exception>
  public static BigInteger EvaluateChecked(BitRouting routing, BigInteger input) {
    ArgumentNullException.ThrowIfNull(routing);
    RequireFits(input, routing.InputWidth, "input", nameof(input));
    var diagnostics = new DiagnosticBag();
    return routing.Condition.Admits(input, diagnostics)
      ? Evaluate(routing.Mapping, input)
      : throw new PermutationModelException(diagnostics.Items[0]);
  }

  /// <summary>Whether a candidate output equals the evaluation on every output bit that is not a don't-care.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <param name="input">The input, 0 to <c>2^InputWidth - 1</c>.</param>
  /// <param name="candidateOutput">The output to judge, 0 to <c>2^OutputWidth - 1</c>.</param>
  /// <returns>Whether the two agree on <see cref="BitMapping.RequiredOutputMask"/>.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The input or the candidate is negative or wider than its width.</exception>
  public static bool AgreesOnRequired(BitMapping mapping, BigInteger input, BigInteger candidateOutput) {
    ArgumentNullException.ThrowIfNull(mapping);
    RequireFits(candidateOutput, mapping.OutputWidth, "candidate output", nameof(candidateOutput));
    return ((Evaluate(mapping, input) ^ candidateOutput) & mapping.RequiredOutputMask).IsZero;
  }

  /// <summary>The output for every input from 0 to <c>2^InputWidth - 1</c>, indexed by the input.</summary>
  /// <param name="mapping">The mapping; at most 16 input bits.</param>
  /// <returns>The <c>2^InputWidth</c> outputs.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  /// <exception cref="PermutationModelException">The mapping has more than 16 input bits (<c>PLY0807</c>).</exception>
  public static BigInteger[] TruthTable(BitMapping mapping) {
    ArgumentNullException.ThrowIfNull(mapping);
    if (mapping.InputWidth > BitSemanticsDiagnostics.TruthTableLimit)
      throw new PermutationModelException(Diagnostic.Create(BitSemanticsDiagnostics.TruthTableTooLarge, null, mapping.InputWidth, BitSemanticsDiagnostics.TruthTableLimit));

    var table = new BigInteger[1 << mapping.InputWidth];
    for (var input = 0; input < table.Length; ++input)
      table[input] = Evaluate(mapping, new BigInteger(input));
    return table;
  }

  /// <summary>For each input bit <c>j</c>, <c>Evaluate(2^j) XOR Evaluate(0)</c>: the output bits that input bit changes.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <returns>One image per input bit, indexed by the input bit.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  public static BigInteger[] BasisImage(BitMapping mapping) {
    ArgumentNullException.ThrowIfNull(mapping);
    var zero = Evaluate(mapping, BigInteger.Zero);
    var images = new BigInteger[mapping.InputWidth];
    for (var bit = 0; bit < images.Length; ++bit)
      images[bit] = Evaluate(mapping, BigInteger.One << bit) ^ zero;
    return images;
  }

  /// <summary>The value of one output entry, reading at most one input bit through <paramref name="inputBit"/>.</summary>
  private static bool OutputBit(BitSource entry, Func<int, bool> inputBit) => entry.Kind switch {
    BitSourceKind.Source => inputBit(entry.Index),
    BitSourceKind.InvertedSource => !inputBit(entry.Index),
    BitSourceKind.One => true,
    _ => false
  };

  private static void RequireFits(BigInteger value, int width, string what, string parameter) {
    if (Hex.Exceeds(value, width))
      throw new ArgumentOutOfRangeException(parameter, $"{what} {Hex.Format(value)} does not fit {width} bits");
  }

  private static void RequireLimbs(int width, int count, string what, string parameter) {
    var expected = (width + 63) / 64;
    if (count != expected)
      throw new ArgumentException($"a width of {width} bits needs {expected} {what} limbs, got {count}", parameter);
  }

}
