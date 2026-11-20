using System.Collections.Generic;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>The reorderings of the bits of a value of every integer type.</summary>
  private static IEnumerable<IntegerOperation> Reordering => [
    Same(IrIntrinsicOp.ByteSwap, _integers, type => [type], (_, types, arguments) => ByteSwap(types[0], Long(arguments, 0))),
    Same(IrIntrinsicOp.BitReverse, _integers, type => [type], (_, types, arguments) => BitReverse(types[0], Long(arguments, 0))),
    Same(IrIntrinsicOp.ReverseBitsInBytes, _integers, type => [type], (_, types, arguments) => ReverseBitsInBytes(types[0], Long(arguments, 0)))
  ];

  /// <summary>The bytes of a value in the opposite order; a value of 8 bits is itself.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>The value with its bytes reversed.</returns>
  public static long ByteSwap(IrType type, long value) {
    var pattern = Pattern(type, value);
    var swapped = 0UL;
    for (var shift = 0; shift < Width(type); shift += 8)
      swapped = (swapped << 8) | ((pattern >> shift) & 0xFF);

    return Value(type, swapped);
  }

  /// <summary>
  /// The bits of a value in the opposite order: the lowest bit becomes the highest. The 64 bits are
  /// reversed by swapping ever larger groups (Warren, <i>Hacker's Delight</i>, 2nd ed., §7-1), and
  /// the reversed value of a narrower type is their top end.
  /// </summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>The value with its bits reversed.</returns>
  public static long BitReverse(IrType type, long value) {
    var bits = Pattern(type, value);
    bits = ((bits & 0x5555555555555555UL) << 1) | ((bits >> 1) & 0x5555555555555555UL);
    bits = ((bits & 0x3333333333333333UL) << 2) | ((bits >> 2) & 0x3333333333333333UL);
    bits = ((bits & 0x0F0F0F0F0F0F0F0FUL) << 4) | ((bits >> 4) & 0x0F0F0F0F0F0F0F0FUL);
    bits = ((bits & 0x00FF00FF00FF00FFUL) << 8) | ((bits >> 8) & 0x00FF00FF00FF00FFUL);
    bits = ((bits & 0x0000FFFF0000FFFFUL) << 16) | ((bits >> 16) & 0x0000FFFF0000FFFFUL);
    bits = (bits << 32) | (bits >> 32);
    return Value(type, bits >> (64 - Width(type)));
  }

  /// <summary>
  /// The bits of each byte of a value in the opposite order, the bytes in their place: bit <c>i</c>
  /// moves to <c>8·(i / 8) + 7 - i % 8</c>, one bit after the other, so that it holds for every width
  /// that is a whole number of bytes.
  /// </summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>The value with the bits of every byte reversed.</returns>
  public static long ReverseBitsInBytes(IrType type, long value) {
    var pattern = Pattern(type, value);
    var reversed = 0UL;
    for (var bit = 0; bit < Width(type); ++bit)
      reversed |= ((pattern >> bit) & 1) << (8 * (bit / 8) + 7 - (bit % 8));

    return Value(type, reversed);
  }

}
