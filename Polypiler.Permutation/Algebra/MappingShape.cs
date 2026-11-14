using System;
using System.Numerics;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;

namespace Polypiler.Permutation.Algebra;

/// <summary>
/// The normal form under which a mapping is named: two spellings of the same mapping get the same
/// shape. <see cref="Describe"/> tries the shapes in a fixed order and the first that applies wins, so
/// a 16-bit byte swap is <c>Rotation(8)</c> everywhere and never <c>ReverseBytes</c>. Here <c>out[i]</c>
/// is the input bit output bit <c>i</c> reads and <c>w</c> the width.
/// </summary>
public abstract record MappingShape {

  private protected MappingShape() { }

  /// <summary>The shape in the notation of reports: <c>Identity</c>, <c>Rotation(4)</c>, <c>Compress(0xAA)</c> and so on.</summary>
  public abstract override string ToString();

  /// <summary>
  /// The first shape that applies, in this order: <see cref="Identity"/>, <see cref="Rotation"/>,
  /// <see cref="ReverseBytes"/>, <see cref="ReverseBits"/>, <see cref="ReverseBitsInBytes"/>,
  /// <see cref="Compress"/>, <see cref="Expand"/>, <see cref="Permutation"/>, <see cref="Routing"/>.
  /// </summary>
  /// <param name="mapping">The mapping.</param>
  /// <returns>Its shape.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  public static MappingShape Describe(BitMapping mapping) {
    ArgumentNullException.ThrowIfNull(mapping);
    var width = mapping.InputWidth;
    if (!mapping.IsSquare)
      return new Routing();
    if (All(mapping, bit => bit))
      return new Identity();

    if (mapping.IsBijective) {
      var amount = RotationAmount(mapping);
      if (amount > 0)
        return new Rotation(amount);
      if (width % 8 == 0 && width >= 16 && All(mapping, bit => 8 * (width / 8 - 1 - bit / 8) + bit % 8))
        return new ReverseBytes();
      if (All(mapping, bit => width - 1 - bit))
        return new ReverseBits();
      if (width % 8 == 0 && width >= 16 && All(mapping, bit => 8 * (bit / 8) + 7 - bit % 8))
        return new ReverseBitsInBytes();
      return new Permutation();
    }

    if (CompressMask(mapping) is { } compress)
      return new Compress(compress);
    if (ExpandMask(mapping) is { } expand)
      return new Expand(expand);
    return new Routing();
  }

  /// <summary>Whether every output bit <c>i</c> is the plain source <c>expected(i)</c>.</summary>
  private static bool All(BitMapping mapping, Func<int, int> expected) {
    for (var bit = 0; bit < mapping.OutputWidth; ++bit)
      if (mapping.Outputs[bit] != BitSource.From(expected(bit)))
        return false;
    return true;
  }

  /// <summary>The <c>k</c> of a bijection with <c>out[(i + k) mod w] = i</c>, or 0 when it is no rotation; <c>k</c> is where input bit 0 lands.</summary>
  private static int RotationAmount(BitMapping mapping) {
    var width = mapping.OutputWidth;
    var amount = 0;
    while (mapping.Outputs[amount].Index != 0)
      ++amount;
    return All(mapping, bit => (bit - amount + width) % width) ? amount : 0;
  }

  /// <summary>The mask of a compress: the plain sources at outputs <c>0 … k−1</c> read ascending inputs, every other output is <c>=0</c>, and the mask is not <c>2^k − 1</c>.</summary>
  private static BigInteger? CompressMask(BitMapping mapping) {
    var mask = BigInteger.Zero;
    var (count, previous) = (0, -1);
    for (var bit = 0; bit < mapping.OutputWidth; ++bit) {
      var entry = mapping.Outputs[bit];
      if (entry.Kind == BitSourceKind.Source && count == bit && entry.Index > previous) {
        mask |= BigInteger.One << entry.Index;
        (count, previous) = (count + 1, entry.Index);
      } else if (entry.Kind != BitSourceKind.Zero) {
        return null;
      }
    }
    return Packed(mask, count);
  }

  /// <summary>The mask of an expand: the plain sources read inputs <c>0 … k−1</c> in order at ascending outputs, every other output is <c>=0</c>, and the mask is not <c>2^k − 1</c>.</summary>
  private static BigInteger? ExpandMask(BitMapping mapping) {
    var mask = BigInteger.Zero;
    var count = 0;
    for (var bit = 0; bit < mapping.OutputWidth; ++bit) {
      var entry = mapping.Outputs[bit];
      if (entry.Kind == BitSourceKind.Source && entry.Index == count) {
        mask |= BigInteger.One << bit;
        ++count;
      } else if (entry.Kind != BitSourceKind.Zero) {
        return null;
      }
    }
    return Packed(mask, count);
  }

  /// <summary>The mask, unless it is the contiguous low mask of its <paramref name="count"/> bits, which is a plain <c>And</c>.</summary>
  private static BigInteger? Packed(BigInteger mask, int count) => mask == (BigInteger.One << count) - 1 ? null : mask;

  /// <summary>1 — square, <c>out[i] = i</c>.</summary>
  public sealed record Identity : MappingShape {

    /// <inheritdoc/>
    public override string ToString() => "Identity";

  }

  /// <summary>2 — a bijection with <c>out[(i + k) mod w] = i</c>, <c>0 &lt; k &lt; w</c>: a rotation towards the most significant bit; swapping the halves is <c>Rotation(w/2)</c>.</summary>
  /// <param name="Amount">The amount <c>k</c>.</param>
  public sealed record Rotation(int Amount) : MappingShape {

    /// <inheritdoc/>
    public override string ToString() => $"Rotation({this.Amount})";

  }

  /// <summary>3 — a bijection of <c>w ∈ 8ℕ</c>, <c>w ≥ 16</c>, with <c>out[i] = 8·(w/8 − 1 − ⌊i/8⌋) + i mod 8</c>.</summary>
  public sealed record ReverseBytes : MappingShape {

    /// <inheritdoc/>
    public override string ToString() => "ReverseBytes";

  }

  /// <summary>4 — a bijection of <c>w ≥ 2</c> with <c>out[i] = w − 1 − i</c>; at width 1 that list is the identity, which comes first.</summary>
  public sealed record ReverseBits : MappingShape {

    /// <inheritdoc/>
    public override string ToString() => "ReverseBits";

  }

  /// <summary>5 — a bijection of <c>w ∈ 8ℕ</c>, <c>w ≥ 16</c>, with <c>out[i] = 8·⌊i/8⌋ + 7 − i mod 8</c>.</summary>
  public sealed record ReverseBitsInBytes : MappingShape {

    /// <inheritdoc/>
    public override string ToString() => "ReverseBitsInBytes";

  }

  /// <summary>6 — square; the inputs in the mask, ascending, feed outputs <c>0 … k−1</c>; every other output is <c>=0</c>; the mask is not <c>2^k − 1</c>.</summary>
  /// <param name="Mask">The inputs that are kept.</param>
  public sealed record Compress(BigInteger Mask) : MappingShape {

    /// <inheritdoc/>
    public override string ToString() => $"Compress({Hex.Format(this.Mask)})";

  }

  /// <summary>7 — square; inputs <c>0 … k−1</c> feed the outputs in the mask, ascending; every other output is <c>=0</c>; the mask is not <c>2^k − 1</c>.</summary>
  /// <param name="Mask">The outputs that are fed.</param>
  public sealed record Expand(BigInteger Mask) : MappingShape {

    /// <inheritdoc/>
    public override string ToString() => $"Expand({Hex.Format(this.Mask)})";

  }

  /// <summary>8 — a bijection that is none of the above.</summary>
  public sealed record Permutation : MappingShape {

    /// <inheritdoc/>
    public override string ToString() => "Permutation";

  }

  /// <summary>9 — every other mapping.</summary>
  public sealed record Routing : MappingShape {

    /// <inheritdoc/>
    public override string ToString() => "Routing";

  }

}
