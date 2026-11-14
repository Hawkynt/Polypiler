using System;
using System.Numerics;
using Polypiler.Core;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Semantics;

/// <summary>
/// The domain and the obligations of a transform. The input domain is every <c>x</c> with
/// <c>x &amp; InputKnownZero == 0</c> and <c>x &amp; InputKnownOne == InputKnownOne</c>; the output bits
/// whose value matters are <see cref="RequiredOutputMask"/>, every other output bit is a don't-care. A
/// don't-care (PRD §20.7) is unconstrained in the result; it is not an unknown input, and there is no
/// notion of an unknown input here.
/// </summary>
/// <param name="InputKnownZero">The input bits known to be 0.</param>
/// <param name="InputKnownOne">The input bits known to be 1.</param>
/// <param name="RequiredOutputMask">The output bits whose value matters.</param>
public sealed record BitCondition(BigInteger InputKnownZero, BigInteger InputKnownOne, BigInteger RequiredOutputMask) {

  /// <summary>Nothing is known about the input and every output bit matters.</summary>
  /// <param name="inputWidth">The input width, 1 to 4096.</param>
  /// <param name="outputWidth">The output width, 1 to 4096.</param>
  /// <exception cref="PermutationModelException">A width is out of range (<c>PLY0800</c>).</exception>
  public static BitCondition Unconstrained(int inputWidth, int outputWidth) {
    foreach (var width in (ReadOnlySpan<int>)[inputWidth, outputWidth])
      if (!BitMapping.IsWidth(width))
        throw new PermutationModelException(Diagnostic.Create(PermutationDiagnostics.WidthOutOfRange, null, width));

    return new(BigInteger.Zero, BigInteger.Zero, (BigInteger.One << outputWidth) - 1);
  }

  /// <summary>The condition a mapping implies: nothing known about the input, every output but the don't-cares required.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  public static BitCondition ForMapping(BitMapping mapping) {
    ArgumentNullException.ThrowIfNull(mapping);
    return new(BigInteger.Zero, BigInteger.Zero, mapping.RequiredOutputMask);
  }

  /// <summary>Whether an input lies in the domain.</summary>
  /// <param name="input">The input, not negative.</param>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="input"/> is negative.</exception>
  public bool Contains(BigInteger input) {
    ArgumentOutOfRangeException.ThrowIfNegative(input);
    return (input & this.InputKnownZero).IsZero && (input & this.InputKnownOne) == this.InputKnownOne;
  }

  /// <summary>Whether an input lies in the domain; an evaluator calls this and stops on <see langword="false"/>.</summary>
  /// <param name="input">The input, not negative.</param>
  /// <param name="diagnostics">Receives <c>PLY0806</c> when the input lies outside the domain.</param>
  /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="input"/> is negative.</exception>
  public bool Admits(BigInteger input, DiagnosticBag diagnostics) {
    ArgumentNullException.ThrowIfNull(diagnostics);
    if (this.Contains(input))
      return true;

    diagnostics.Report(Diagnostic.Create(BitSemanticsDiagnostics.OutsideDomain, null, Hex.Format(input)));
    return false;
  }

  /// <summary>Whether output bit <paramref name="bit"/> matters.</summary>
  /// <param name="bit">The output bit, not negative.</param>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="bit"/> is negative.</exception>
  public bool Requires(int bit) {
    ArgumentOutOfRangeException.ThrowIfNegative(bit);
    return Hex.Has(this.RequiredOutputMask, bit);
  }

}
