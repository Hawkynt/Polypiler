// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// Bit fields and single bits. A field outside the width, or a bit position outside it, traps as an
/// invalid operation. A pattern of up to 32 bits is worked on as a <c>uint</c>, told its width, one of
/// 64 bits as a <c>ulong</c>.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The field of <paramref name="length"/> bits from bit <paramref name="start"/> on of the lowest <paramref name="bits"/> bits.</summary>
  /// <param name="value">The pattern, perhaps with bits above the width.</param>
  /// <param name="start">The lowest bit of the field.</param>
  /// <param name="length">The number of bits of the field.</param>
  /// <param name="bits">The width, 8, 16 or 32.</param>
  /// <exception cref="ArithmeticException">The field does not lie within the width.</exception>
  public static uint ExtractBits32(uint value, int start, int length, int bits) {
    CheckField(start, length, bits);
    if (length == 0)
      return 0u;

    var pattern = bits == 32 ? value : value & ((1u << bits) - 1u);
    return length == 32 ? pattern : (pattern >> start) & ((1u << length) - 1u);
  }

  /// <summary>
  /// The field of <paramref name="length"/> bits from bit <paramref name="start"/> on of the lowest
  /// <paramref name="bits"/> bits, sign-extended from its top bit: the bit of the sign flipped and
  /// subtracted (Warren, <i>Hacker's Delight</i>, 2nd ed., §2-6).
  /// </summary>
  /// <param name="value">The pattern, perhaps with bits above the width.</param>
  /// <param name="start">The lowest bit of the field.</param>
  /// <param name="length">The number of bits of the field.</param>
  /// <param name="bits">The width, 8, 16 or 32.</param>
  /// <exception cref="ArithmeticException">The field does not lie within the width.</exception>
  public static uint ExtractBitsSigned32(uint value, int start, int length, int bits) {
    var field = ExtractBits32(value, start, length, bits);
    if (length == 0)
      return 0u;

    var sign = 1u << (length - 1);
    return (field ^ sign) - sign;
  }

  /// <summary>A pattern with a field of <paramref name="length"/> bits from bit <paramref name="start"/> on replaced by the low bits of another.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="field">The new field, in its low bits.</param>
  /// <param name="start">The lowest bit of the field.</param>
  /// <param name="length">The number of bits of the field.</param>
  /// <param name="bits">The width, 8, 16 or 32.</param>
  /// <exception cref="ArithmeticException">The field does not lie within the width.</exception>
  public static uint InsertBits32(uint value, uint field, int start, int length, int bits) {
    CheckField(start, length, bits);
    if (length == 0)
      return value;

    var mask = (length == 32 ? 0xFFFFFFFFu : (1u << length) - 1u) << start;
    return (value & ~mask) | ((field << start) & mask);
  }

  /// <summary>Whether the bit at a position of a pattern of up to 32 bits is set.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="position">The position, from 0 for the lowest bit.</param>
  /// <param name="bits">The width, 8, 16 or 32.</param>
  /// <exception cref="ArithmeticException">The position lies outside the width.</exception>
  public static bool TestBit32(uint value, int position, int bits) {
    CheckPosition(position, bits);
    return ((value >> position) & 1u) != 0u;
  }

  /// <summary>A pattern of up to 32 bits with the bit at a position set.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="position">The position, from 0 for the lowest bit.</param>
  /// <param name="bits">The width, 8, 16 or 32.</param>
  /// <exception cref="ArithmeticException">The position lies outside the width.</exception>
  public static uint SetBit32(uint value, int position, int bits) {
    CheckPosition(position, bits);
    return value | (1u << position);
  }

  /// <summary>A pattern of up to 32 bits with the bit at a position cleared.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="position">The position, from 0 for the lowest bit.</param>
  /// <param name="bits">The width, 8, 16 or 32.</param>
  /// <exception cref="ArithmeticException">The position lies outside the width.</exception>
  public static uint ClearBit32(uint value, int position, int bits) {
    CheckPosition(position, bits);
    return value & ~(1u << position);
  }

  /// <summary>A pattern of up to 32 bits with the bit at a position flipped.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="position">The position, from 0 for the lowest bit.</param>
  /// <param name="bits">The width, 8, 16 or 32.</param>
  /// <exception cref="ArithmeticException">The position lies outside the width.</exception>
  public static uint ToggleBit32(uint value, int position, int bits) {
    CheckPosition(position, bits);
    return value ^ (1u << position);
  }

  /// <summary>The field of <paramref name="length"/> bits from bit <paramref name="start"/> on of a <c>ulong</c>.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="start">The lowest bit of the field.</param>
  /// <param name="length">The number of bits of the field.</param>
  /// <exception cref="ArithmeticException">The field does not lie within 64 bits.</exception>
  public static ulong ExtractBits64(ulong value, int start, int length) {
    CheckField(start, length, 64);
    if (length == 0)
      return 0UL;

    return length == 64 ? value : (value >> start) & ((1UL << length) - 1UL);
  }

  /// <summary>The field of <paramref name="length"/> bits from bit <paramref name="start"/> on of a <c>ulong</c>, sign-extended from its top bit.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="start">The lowest bit of the field.</param>
  /// <param name="length">The number of bits of the field.</param>
  /// <exception cref="ArithmeticException">The field does not lie within 64 bits.</exception>
  public static ulong ExtractBitsSigned64(ulong value, int start, int length) {
    var field = ExtractBits64(value, start, length);
    if (length == 0)
      return 0UL;

    var sign = 1UL << (length - 1);
    return (field ^ sign) - sign;
  }

  /// <summary>A <c>ulong</c> with a field replaced by the low bits of another.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="field">The new field, in its low bits.</param>
  /// <param name="start">The lowest bit of the field.</param>
  /// <param name="length">The number of bits of the field.</param>
  /// <exception cref="ArithmeticException">The field does not lie within 64 bits.</exception>
  public static ulong InsertBits64(ulong value, ulong field, int start, int length) {
    CheckField(start, length, 64);
    if (length == 0)
      return value;

    var mask = (length == 64 ? ulong.MaxValue : (1UL << length) - 1UL) << start;
    return (value & ~mask) | ((field << start) & mask);
  }

  /// <summary>Whether the bit at a position of a <c>ulong</c> is set.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="position">The position, from 0 for the lowest bit.</param>
  /// <exception cref="ArithmeticException">The position lies outside 64 bits.</exception>
  public static bool TestBit64(ulong value, int position) {
    CheckPosition(position, 64);
    return ((value >> position) & 1UL) != 0UL;
  }

  /// <summary>A <c>ulong</c> with the bit at a position set.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="position">The position, from 0 for the lowest bit.</param>
  /// <exception cref="ArithmeticException">The position lies outside 64 bits.</exception>
  public static ulong SetBit64(ulong value, int position) {
    CheckPosition(position, 64);
    return value | (1UL << position);
  }

  /// <summary>A <c>ulong</c> with the bit at a position cleared.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="position">The position, from 0 for the lowest bit.</param>
  /// <exception cref="ArithmeticException">The position lies outside 64 bits.</exception>
  public static ulong ClearBit64(ulong value, int position) {
    CheckPosition(position, 64);
    return value & ~(1UL << position);
  }

  /// <summary>A <c>ulong</c> with the bit at a position flipped.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="position">The position, from 0 for the lowest bit.</param>
  /// <exception cref="ArithmeticException">The position lies outside 64 bits.</exception>
  public static ulong ToggleBit64(ulong value, int position) {
    CheckPosition(position, 64);
    return value ^ (1UL << position);
  }

  private static void CheckField(int start, int length, int bits) {
    if (start < 0 || length < 0 || start > bits - length)
      throw new ArithmeticException("invalid operation");
  }

  private static void CheckPosition(int position, int bits) {
    if (position < 0 || position >= bits)
      throw new ArithmeticException("invalid operation");
  }

}
