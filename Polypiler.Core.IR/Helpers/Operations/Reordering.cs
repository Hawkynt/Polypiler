// SPDX-License-Identifier: MIT
namespace Polypiler.Core.IR;

/// <summary>
/// The byte swap, the bit reversal and the reversal of the bits in each byte: the bytes and the bits
/// of 32 bits by masks and shifts, those of 64 bits as the two halves exchanged, each reordered, and
/// those of fewer bits as the low part of the reordered 32 bits.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The two lowest bytes of a pattern in the opposite order.</summary>
  /// <param name="value">The pattern, perhaps with bits above 16.</param>
  public static uint ByteSwap16(uint value) => ((value & 0xFFu) << 8) | ((value >> 8) & 0xFFu);

  /// <summary>A <c>uint</c> with its bytes in the opposite order.</summary>
  /// <param name="value">The pattern.</param>
  public static uint ByteSwap32(uint value) => (value << 24) | ((value & 0xFF00u) << 8) | ((value >> 8) & 0xFF00u) | (value >> 24);

  /// <summary>A <c>ulong</c> with its bytes in the opposite order.</summary>
  /// <param name="value">The pattern.</param>
  public static ulong ByteSwap64(ulong value) => ((ulong)ByteSwap32((uint)value) << 32) | ByteSwap32((uint)(value >> 32));

  /// <summary>A <c>uint</c> with its bits in the opposite order.</summary>
  /// <param name="value">The pattern.</param>
  public static uint BitReverse32(uint value) {
    value = ((value & 0x55555555u) << 1) | ((value >> 1) & 0x55555555u);
    value = ((value & 0x33333333u) << 2) | ((value >> 2) & 0x33333333u);
    value = ((value & 0x0F0F0F0Fu) << 4) | ((value >> 4) & 0x0F0F0F0Fu);
    value = ((value & 0x00FF00FFu) << 8) | ((value >> 8) & 0x00FF00FFu);
    return (value << 16) | (value >> 16);
  }

  /// <summary>The lowest <paramref name="bits"/> bits of a pattern in the opposite order.</summary>
  /// <param name="value">The pattern, perhaps with bits above the width.</param>
  /// <param name="bits">The width, 8 or 16.</param>
  public static uint BitReverseNarrow(uint value, int bits) => BitReverse32(value) >> (32 - bits);

  /// <summary>A <c>ulong</c> with its bits in the opposite order.</summary>
  /// <param name="value">The pattern.</param>
  public static ulong BitReverse64(ulong value) => ((ulong)BitReverse32((uint)value) << 32) | BitReverse32((uint)(value >> 32));

  /// <summary>
  /// A <c>uint</c> with the bits of each byte in the opposite order: the first three swaps of the bit
  /// reversal, of single bits, pairs and nibbles, which never cross a byte. A narrower pattern is its
  /// low bytes.
  /// </summary>
  /// <param name="value">The pattern.</param>
  public static uint ReverseBitsInBytes32(uint value) {
    value = ((value & 0x55555555u) << 1) | ((value >> 1) & 0x55555555u);
    value = ((value & 0x33333333u) << 2) | ((value >> 2) & 0x33333333u);
    return ((value & 0x0F0F0F0Fu) << 4) | ((value >> 4) & 0x0F0F0F0Fu);
  }

  /// <summary>A <c>ulong</c> with the bits of each byte in the opposite order.</summary>
  /// <param name="value">The pattern.</param>
  public static ulong ReverseBitsInBytes64(ulong value) => ((ulong)ReverseBitsInBytes32((uint)(value >> 32)) << 32) | ReverseBitsInBytes32((uint)value);

}
