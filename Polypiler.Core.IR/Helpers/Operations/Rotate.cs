// SPDX-License-Identifier: MIT
namespace Polypiler.Core.IR;

/// <summary>
/// The helpers of Polypiler: what replaces an operation of the IR where a target has no native form
/// of it, written once in the subset of C# the C# frontend imports. Each file holds one family; a
/// helper computes what <see cref="IntegerOperations"/> and the interpreter compute, with no more
/// than the operators every target has, so that the backends write it through their ordinary path.
/// </summary>
public static partial class HelperOperations {

  /// <summary>
  /// A <c>uint</c> rotated left by the count modulo 32. The count of the second shift is the
  /// complement modulo 32, so a count that is a multiple of 32 shifts by nothing twice.
  /// </summary>
  /// <param name="value">The pattern.</param>
  /// <param name="count">The count, any <c>int</c>.</param>
  public static uint RotateLeft32(uint value, int count) => (value << count) | (value >> (-count & 31));

  /// <summary>A <c>uint</c> rotated right by the count modulo 32.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="count">The count, any <c>int</c>.</param>
  public static uint RotateRight32(uint value, int count) => (value >> count) | (value << (-count & 31));

  /// <summary>A <c>ulong</c> rotated left by the count modulo 64.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="count">The count, any <c>int</c>.</param>
  public static ulong RotateLeft64(ulong value, int count) => (value << count) | (value >> (-count & 63));

  /// <summary>A <c>ulong</c> rotated right by the count modulo 64.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="count">The count, any <c>int</c>.</param>
  public static ulong RotateRight64(ulong value, int count) => (value >> count) | (value << (-count & 63));

  /// <summary>
  /// The pattern in the lowest <paramref name="bits"/> bits - 8 or 16 - rotated left by the count
  /// modulo the width; the bits above are dropped first and the result has none.
  /// </summary>
  /// <param name="value">The pattern, perhaps with bits above the width.</param>
  /// <param name="count">The count, any <c>int</c>.</param>
  /// <param name="bits">The width, 8 or 16.</param>
  public static uint RotateLeftNarrow(uint value, int count, int bits) {
    uint mask = (1u << bits) - 1u;
    value &= mask;
    count &= bits - 1;
    return ((value << count) | (value >> (bits - count))) & mask;
  }

  /// <summary>The pattern in the lowest <paramref name="bits"/> bits - 8 or 16 - rotated right by the count modulo the width.</summary>
  /// <param name="value">The pattern, perhaps with bits above the width.</param>
  /// <param name="count">The count, any <c>int</c>.</param>
  /// <param name="bits">The width, 8 or 16.</param>
  public static uint RotateRightNarrow(uint value, int count, int bits) {
    uint mask = (1u << bits) - 1u;
    value &= mask;
    count &= bits - 1;
    return ((value >> count) | (value << (bits - count))) & mask;
  }

}
