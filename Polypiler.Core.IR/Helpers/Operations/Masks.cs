// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// Masks and extensions: the mask of the lowest bits, the lowest set bit isolated, cleared or turned
/// into a mask of the bits up to it (Warren, <i>Hacker's Delight</i>, 2nd edition, §2-1), and the
/// sign and zero extension of the lowest bits. A count or a width outside the type traps as an
/// invalid operation.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The mask of the lowest <paramref name="count"/> bits of a type of up to 32 bits.</summary>
  /// <param name="count">The number of bits, 0 to the width.</param>
  /// <param name="bits">The width, 8, 16 or 32.</param>
  /// <exception cref="ArithmeticException">The count lies outside 0 to the width.</exception>
  public static uint LowMask32(int count, int bits) {
    if (count < 0 || count > bits)
      throw new ArithmeticException("invalid operation");

    return count == 32 ? 0xFFFFFFFFu : (1u << count) - 1u;
  }

  /// <summary>The mask of the lowest <paramref name="count"/> bits of a <c>ulong</c>.</summary>
  /// <param name="count">The number of bits, 0 to 64.</param>
  /// <exception cref="ArithmeticException">The count lies outside 0 to 64.</exception>
  public static ulong LowMask64(int count) {
    if (count < 0 || count > 64)
      throw new ArithmeticException("invalid operation");

    return count == 64 ? ulong.MaxValue : (1UL << count) - 1UL;
  }

  /// <summary>The lowest set bit of a pattern alone.</summary>
  /// <param name="value">The pattern.</param>
  public static uint IsolateLowestSet32(uint value) => value & (~value + 1u);

  /// <summary>A pattern without its lowest set bit.</summary>
  /// <param name="value">The pattern.</param>
  public static uint ClearLowestSet32(uint value) => value & (value - 1u);

  /// <summary>The mask of the bits of a pattern up to its lowest set bit; every bit for 0.</summary>
  /// <param name="value">The pattern.</param>
  public static uint MaskToLowestSet32(uint value) => value ^ (value - 1u);

  /// <summary>The lowest set bit of a <c>ulong</c> alone.</summary>
  /// <param name="value">The pattern.</param>
  public static ulong IsolateLowestSet64(ulong value) => value & (~value + 1UL);

  /// <summary>A <c>ulong</c> without its lowest set bit.</summary>
  /// <param name="value">The pattern.</param>
  public static ulong ClearLowestSet64(ulong value) => value & (value - 1UL);

  /// <summary>The mask of the bits of a <c>ulong</c> up to its lowest set bit; every bit for 0.</summary>
  /// <param name="value">The pattern.</param>
  public static ulong MaskToLowestSet64(ulong value) => value ^ (value - 1UL);

  /// <summary>The lowest <paramref name="bits"/> bits of a pattern of a type of up to 32 bits, sign-extended.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="bits">The number of bits, 1 to the width.</param>
  /// <param name="width">The width of the type, 8, 16 or 32.</param>
  /// <exception cref="ArithmeticException">The number of bits lies outside 1 to the width.</exception>
  public static int SignExtend32(uint value, int bits, int width) {
    if (bits < 1 || bits > width)
      throw new ArithmeticException("invalid operation");

    return (int)(value << (32 - bits)) >> (32 - bits);
  }

  /// <summary>The lowest <paramref name="bits"/> bits of a pattern of a type of up to 32 bits, zero-extended.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="bits">The number of bits, 1 to the width.</param>
  /// <param name="width">The width of the type, 8, 16 or 32.</param>
  /// <exception cref="ArithmeticException">The number of bits lies outside 1 to the width.</exception>
  public static uint ZeroExtend32(uint value, int bits, int width) {
    if (bits < 1 || bits > width)
      throw new ArithmeticException("invalid operation");

    return bits == 32 ? value : value & ((1u << bits) - 1u);
  }

  /// <summary>The lowest <paramref name="bits"/> bits of a <c>ulong</c>, sign-extended.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="bits">The number of bits, 1 to 64.</param>
  /// <exception cref="ArithmeticException">The number of bits lies outside 1 to 64.</exception>
  public static long SignExtend64(ulong value, int bits) {
    if (bits < 1 || bits > 64)
      throw new ArithmeticException("invalid operation");

    return (long)(value << (64 - bits)) >> (64 - bits);
  }

  /// <summary>The lowest <paramref name="bits"/> bits of a <c>ulong</c>, zero-extended.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="bits">The number of bits, 1 to 64.</param>
  /// <exception cref="ArithmeticException">The number of bits lies outside 1 to 64.</exception>
  public static ulong ZeroExtend64(ulong value, int bits) {
    if (bits < 1 || bits > 64)
      throw new ArithmeticException("invalid operation");

    return bits == 64 ? value : value & ((1UL << bits) - 1UL);
  }

}
