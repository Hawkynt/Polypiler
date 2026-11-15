// SPDX-License-Identifier: MIT
namespace Polypiler.Core.IR;

/// <summary>Powers of two: whether a value is one, and the smallest one not below a bit pattern.</summary>
public static partial class HelperOperations {

  /// <summary>Whether an <c>int</c> is a power of two; never 0 and never a negative value.</summary>
  /// <param name="value">The value.</param>
  public static bool IsPow2Signed32(int value) => value > 0 && (value & (value - 1)) == 0;

  /// <summary>Whether a <c>uint</c> is a power of two; never 0.</summary>
  /// <param name="value">The value.</param>
  public static bool IsPow2Unsigned32(uint value) => value != 0u && (value & (value - 1u)) == 0u;

  /// <summary>Whether a <c>long</c> is a power of two; never 0 and never a negative value.</summary>
  /// <param name="value">The value.</param>
  public static bool IsPow2Signed64(long value) => value > 0L && (value & (value - 1L)) == 0L;

  /// <summary>Whether a <c>ulong</c> is a power of two; never 0.</summary>
  /// <param name="value">The value.</param>
  public static bool IsPow2Unsigned64(ulong value) => value != 0UL && (value & (value - 1UL)) == 0UL;

  /// <summary>
  /// The smallest power of two not below the pattern in the lowest <paramref name="bits"/> bits,
  /// 2^width where it is above 2^(width - 1), which the type of the width wraps to 0; 0 for 0.
  /// </summary>
  /// <param name="value">The pattern, perhaps with bits above the width.</param>
  /// <param name="bits">The width, 8, 16 or 32.</param>
  public static uint RoundUpPow2Of32(uint value, int bits) {
    var pattern = bits == 32 ? value : value & ((1u << bits) - 1u);
    if (pattern == 0u)
      return 0u;

    var power = 1u;
    while (power != 0u && power < pattern)
      power <<= 1;

    return power;
  }

  /// <summary>The smallest power of two not below a <c>ulong</c>, 0 where it is above 2^63; 0 for 0.</summary>
  /// <param name="value">The pattern.</param>
  public static ulong RoundUpPow2Of64(ulong value) {
    if (value == 0UL)
      return 0UL;

    var power = 1UL;
    while (power != 0UL && power < value)
      power <<= 1;

    return power;
  }

}
