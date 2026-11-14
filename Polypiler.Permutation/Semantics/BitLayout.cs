using System;
using System.Numerics;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Semantics;

/// <summary>
/// How the bits of a transform are grouped: into lanes of <paramref name="LaneBits"/> bits each, lane
/// <c>k</c> holding bits <c>k·LaneBits</c> to <c>(k+1)·LaneBits - 1</c>, and in which byte order the
/// lanes lie when the transform is bound to memory bytes. Rule H5 of <see cref="BitSemanticsVerifier"/>
/// requires the lane size to divide both widths.
/// </summary>
/// <param name="LaneBits">The bits per lane, at least 1.</param>
/// <param name="ByteOrder">The byte order; it matters only where a mapping is bound to memory bytes.</param>
public sealed record BitLayout(int LaneBits, ByteOrder ByteOrder) {

  /// <summary>The bits per lane, at least 1.</summary>
  public int LaneBits { get; } = LaneBits >= 1 ? LaneBits : throw new ArgumentOutOfRangeException(nameof(LaneBits), LaneBits, "a lane holds at least one bit");

  /// <summary>The byte order of the lanes in memory.</summary>
  public ByteOrder ByteOrder { get; } = Enum.IsDefined(ByteOrder) ? ByteOrder : throw new ArgumentOutOfRangeException(nameof(ByteOrder), ByteOrder, "not a byte order");

  /// <summary>
  /// The layout of a transform that declares none: one lane of the whole width when input and output
  /// agree, otherwise the largest lane size that divides both, and no byte order.
  /// </summary>
  /// <param name="inputWidth">The input width, 1 to 4096.</param>
  /// <param name="outputWidth">The output width, 1 to 4096.</param>
  /// <exception cref="ArgumentOutOfRangeException">A width is outside 1..4096.</exception>
  public static BitLayout Default(int inputWidth, int outputWidth) {
    if (!BitMapping.IsWidth(inputWidth))
      throw new ArgumentOutOfRangeException(nameof(inputWidth), inputWidth, "a width lies in 1..4096");
    if (!BitMapping.IsWidth(outputWidth))
      throw new ArgumentOutOfRangeException(nameof(outputWidth), outputWidth, "a width lies in 1..4096");

    return new((int)BigInteger.GreatestCommonDivisor(inputWidth, outputWidth), ByteOrder.NotApplicable);
  }

  /// <summary>Whether the lane size divides a width.</summary>
  /// <param name="width">The width.</param>
  public bool Divides(int width) => width % this.LaneBits == 0;

  /// <summary>
  /// Whether every output bit that reads an input bit, plain or inverted, reads one of its own lane:
  /// output bit <c>i</c> and input bit <c>j</c> satisfy <c>i / LaneBits == j / LaneBits</c>. Constants and
  /// don't-cares read nothing and respect every lane.
  /// </summary>
  /// <param name="mapping">The mapping.</param>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  public bool RespectsLanes(BitMapping mapping) {
    ArgumentNullException.ThrowIfNull(mapping);
    for (var bit = 0; bit < mapping.OutputWidth; ++bit) {
      var entry = mapping.Outputs[bit];
      if (entry.ReadsInput && entry.Index / this.LaneBits != bit / this.LaneBits)
        return false;
    }
    return true;
  }

}
