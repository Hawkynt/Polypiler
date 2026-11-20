// SPDX-License-Identifier: MIT
namespace Polypiler.Core.IR;

public static partial class HelperOperations {

  /// <summary>
  /// The integer square root of a <c>uint</c>, digit by digit in base 4 (the binary restoring method):
  /// sixteen steps, each deciding one bit of the root from the remainder. Every value of 8 and 16 bits
  /// is a <c>uint</c> as well.
  /// </summary>
  /// <param name="value">The value.</param>
  /// <returns>The largest root whose square is at most the value.</returns>
  public static uint ISqrt32(uint value) {
    uint root = 0;
    uint bit = 1u << 30;
    while (bit > value)
      bit >>= 2;
    while (bit != 0) {
      if (value >= root + bit) {
        value -= root + bit;
        root = (root >> 1) + bit;
      } else {
        root >>= 1;
      }
      bit >>= 2;
    }
    return root;
  }

  /// <summary>The integer square root of a <c>ulong</c>, digit by digit as <see cref="ISqrt32"/>: thirty-two steps.</summary>
  /// <param name="value">The value.</param>
  /// <returns>The largest root whose square is at most the value.</returns>
  public static ulong ISqrt64(ulong value) {
    ulong root = 0;
    ulong bit = 1UL << 62;
    while (bit > value)
      bit >>= 2;
    while (bit != 0) {
      if (value >= root + bit) {
        value -= root + bit;
        root = (root >> 1) + bit;
      } else {
        root >>= 1;
      }
      bit >>= 2;
    }
    return root;
  }

}
