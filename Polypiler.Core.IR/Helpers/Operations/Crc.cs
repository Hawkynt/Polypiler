// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The update of the register of a cyclic redundancy check over a span of bytes, one bit at a time:
/// no table, so the helper works for every polynomial. The span has to lie within the array, which is
/// checked before any byte is read; the polynomial is in its normal form without its top bit and not
/// 0, for a reflected CRC too. A register of up to 32 bits is worked on as a <c>uint</c>, told its
/// width, one of 64 bits as a <c>ulong</c>.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The register of a CRC of 8, 16 or 32 bits after the bytes of a span.</summary>
  /// <param name="crc">The register before the span, perhaps with bits above the width.</param>
  /// <param name="poly">The polynomial, normal form, without its top bit.</param>
  /// <param name="reflected">Whether bytes enter at the bottom of the register, lowest bit first.</param>
  /// <param name="data">The bytes.</param>
  /// <param name="index">The first byte of the span.</param>
  /// <param name="count">The number of bytes of the span.</param>
  /// <param name="bits">The width, 8, 16 or 32.</param>
  /// <exception cref="IndexOutOfRangeException">The span does not lie within the array.</exception>
  /// <exception cref="ArithmeticException">The polynomial is 0.</exception>
  public static uint CrcUpdate32(uint crc, uint poly, bool reflected, byte[] data, int index, int count, int bits) {
    CheckSpan(data, index, count);
    var mask = bits == 32 ? 0xFFFFFFFFu : (1u << bits) - 1u;
    var generator = poly & mask;
    if (generator == 0u)
      throw new ArithmeticException("invalid operation");

    var register = crc & mask;
    if (reflected) {
      var reversed = BitReverse32(generator) >> (32 - bits);
      for (var at = index; at < index + count; ++at) {
        register = register ^ data[at];
        for (var step = 0; step < 8; ++step)
          register = (register & 1u) != 0u ? (register >> 1) ^ reversed : register >> 1;
      }

      return register;
    }

    var top = 1u << (bits - 1);
    for (var at = index; at < index + count; ++at) {
      register = register ^ ((uint)data[at] << (bits - 8));
      for (var step = 0; step < 8; ++step)
        register = ((register & top) != 0u ? (register << 1) ^ generator : register << 1) & mask;
    }

    return register;
  }

  /// <summary>The register of a CRC of 64 bits after the bytes of a span.</summary>
  /// <param name="crc">The register before the span.</param>
  /// <param name="poly">The polynomial, normal form, without its top bit.</param>
  /// <param name="reflected">Whether bytes enter at the bottom of the register, lowest bit first.</param>
  /// <param name="data">The bytes.</param>
  /// <param name="index">The first byte of the span.</param>
  /// <param name="count">The number of bytes of the span.</param>
  /// <exception cref="IndexOutOfRangeException">The span does not lie within the array.</exception>
  /// <exception cref="ArithmeticException">The polynomial is 0.</exception>
  public static ulong CrcUpdate64(ulong crc, ulong poly, bool reflected, byte[] data, int index, int count) {
    CheckSpan(data, index, count);
    if (poly == 0UL)
      throw new ArithmeticException("invalid operation");

    var register = crc;
    if (reflected) {
      var reversed = BitReverse64(poly);
      for (var at = index; at < index + count; ++at) {
        register = register ^ data[at];
        for (var step = 0; step < 8; ++step)
          register = (register & 1UL) != 0UL ? (register >> 1) ^ reversed : register >> 1;
      }

      return register;
    }

    for (var at = index; at < index + count; ++at) {
      register = register ^ ((ulong)data[at] << 56);
      for (var step = 0; step < 8; ++step)
        register = (register & 0x8000000000000000UL) != 0UL ? (register << 1) ^ poly : register << 1;
    }

    return register;
  }

  private static void CheckSpan(byte[] data, int index, int count) {
    if (index < 0 || count < 0 || index > data.Length - count)
      throw new IndexOutOfRangeException("index out of range");
  }

}
