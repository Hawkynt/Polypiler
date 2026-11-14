using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The classification of IEEE 754-2019 §5.7.2 as .NET's <c>double</c> and <c>float</c> define it, read
/// from the bits: a zero is neither normal nor subnormal, and the sign bit of a NaN counts as a sign.
/// </summary>
public static partial class FloatOperations {

  /// <summary>Whether the number is a NaN.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> for a NaN.</returns>
  public static bool IsNaN(double x) => (BitConverter.DoubleToInt64Bits(x) & long.MaxValue) > 0x7ff0000000000000L;

  /// <summary>Whether the <c>float</c> is a NaN.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> for a NaN.</returns>
  public static bool IsNaNF(float x) => (BitConverter.SingleToInt32Bits(x) & int.MaxValue) > 0x7f800000;

  /// <summary>Whether the number is an infinity of either sign.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> for an infinity.</returns>
  public static bool IsInfinity(double x) => (BitConverter.DoubleToInt64Bits(x) & long.MaxValue) == 0x7ff0000000000000L;

  /// <summary>Whether the <c>float</c> is an infinity of either sign.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> for an infinity.</returns>
  public static bool IsInfinityF(float x) => (BitConverter.SingleToInt32Bits(x) & int.MaxValue) == 0x7f800000;

  /// <summary>Whether the number is neither an infinity nor a NaN.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> for a finite number.</returns>
  public static bool IsFinite(double x) => (BitConverter.DoubleToInt64Bits(x) & long.MaxValue) < 0x7ff0000000000000L;

  /// <summary>Whether the <c>float</c> is neither an infinity nor a NaN.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> for a finite number.</returns>
  public static bool IsFiniteF(float x) => (BitConverter.SingleToInt32Bits(x) & int.MaxValue) < 0x7f800000;

  /// <summary>Whether the sign bit is set: of -0, of a negative number and of a negative NaN.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> when the sign bit is set.</returns>
  public static bool IsNegative(double x) => BitConverter.DoubleToInt64Bits(x) < 0;

  /// <summary>Whether the sign bit of the <c>float</c> is set.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> when the sign bit is set.</returns>
  public static bool IsNegativeF(float x) => BitConverter.SingleToInt32Bits(x) < 0;

  /// <summary>Whether the number is normal: finite, not zero and not subnormal.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> for a normal number.</returns>
  public static bool IsNormal(double x) {
    var biased = BitConverter.DoubleToInt64Bits(x) >> 52 & 0x7ff;
    return biased != 0 && biased != 0x7ff;
  }

  /// <summary>Whether the <c>float</c> is normal.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> for a normal number.</returns>
  public static bool IsNormalF(float x) {
    var biased = BitConverter.SingleToInt32Bits(x) >> 23 & 0xff;
    return biased != 0 && biased != 0xff;
  }

  /// <summary>Whether the number is subnormal: below the smallest normal number and not zero.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> for a subnormal number.</returns>
  public static bool IsSubnormal(double x) {
    var magnitude = BitConverter.DoubleToInt64Bits(x) & long.MaxValue;
    return magnitude != 0 && magnitude < 0x0010000000000000L;
  }

  /// <summary>Whether the <c>float</c> is subnormal.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> for a subnormal number.</returns>
  public static bool IsSubnormalF(float x) {
    var magnitude = BitConverter.SingleToInt32Bits(x) & int.MaxValue;
    return magnitude != 0 && magnitude < 0x00800000;
  }

  /// <summary>Whether the number is an integer: finite and without a fraction, -0 included.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> for an integral number.</returns>
  public static bool IsInteger(double x) => IsFinite(x) && x == Math.Truncate(x);

  /// <summary>Whether the <c>float</c> is an integer.</summary>
  /// <param name="x">The number.</param>
  /// <returns><see langword="true"/> for an integral number.</returns>
  public static bool IsIntegerF(float x) => IsFiniteF(x) && x == MathF.Truncate(x);

}
