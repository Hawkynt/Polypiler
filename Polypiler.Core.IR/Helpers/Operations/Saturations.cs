// SPDX-License-Identifier: MIT
namespace Polypiler.Core.IR;

/// <summary>
/// The conversions of a floating-point number to an integer that saturate: the nearest value of the
/// integer type where the number lies outside it, 0 for a NaN, the number without its fraction
/// otherwise. The comparisons come first, so the conversion itself only ever sees a number within the
/// range, where every conversion of every target gives the same.
/// </summary>
public static partial class HelperOperations {

  /// <summary>A number as an integer of a type of up to 32 bits, saturated to its bounds.</summary>
  /// <param name="value">The number.</param>
  /// <param name="low">The smallest value of the type.</param>
  /// <param name="high">The largest value of the type.</param>
  public static int SaturateToInt32(double value, int low, int high)
    => value >= high ? high : value > low ? checked((int)value) : value <= low ? low : 0;

  /// <summary>A number as the nearest <c>uint</c>, 0 for a NaN.</summary>
  /// <param name="value">The number.</param>
  public static uint SaturateToUInt32(double value)
    => value >= 4294967295.0 ? 4294967295u : value > 0.0 ? checked((uint)value) : 0u;

  /// <summary>A number as the nearest <c>long</c>, 0 for a NaN; the upper bound is 2^63, which no <c>long</c> reaches.</summary>
  /// <param name="value">The number.</param>
  public static long SaturateToInt64(double value)
    => value >= 9223372036854775808.0 ? long.MaxValue
      : value > -9223372036854775808.0 ? checked((long)value)
      : value <= -9223372036854775808.0 ? long.MinValue : 0L;

  /// <summary>A number as the nearest <c>ulong</c>, 0 for a NaN; the upper bound is 2^64.</summary>
  /// <param name="value">The number.</param>
  public static ulong SaturateToUInt64(double value)
    => value >= 18446744073709551616.0 ? ulong.MaxValue : value > 0.0 ? checked((ulong)value) : 0UL;

}
