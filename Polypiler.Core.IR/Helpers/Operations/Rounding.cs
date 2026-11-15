// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The roundings, the sign and the ordering of floating-point numbers that a target lacks: the
/// rounding to an integer with ties towards positive infinity of ECMA-262's <c>Math.round</c>, the
/// ones with ties to even and away from zero built on it, the truncation, the sign that traps on a
/// NaN, ECMA-262's conversions to 32-bit integers, and the smaller and the larger of two numbers as
/// IEEE 754-2019 orders them. The clamp and the rounding to digits are the portable implementations
/// of <see cref="FloatOperations"/>.
/// </summary>
public static partial class HelperOperations {

  /// <summary>
  /// ECMA-262's <c>Math.round</c>: a tie goes towards positive infinity, and a zero keeps the sign of
  /// the number - told by the sign of its reciprocal, since <c>Math.CopySign</c> came late.
  /// </summary>
  /// <param name="value">The number.</param>
  public static double RoundHalfUp(double value) {
    var floor = Math.Floor(value);
    var rounded = value - floor >= 0.5 ? floor + 1.0 : floor;
    return rounded != 0.0 ? rounded : value < 0.0 || 1.0 / value < 0.0 ? -0.0 : 0.0;
  }

  /// <summary>The nearest integral number, a tie going to the even one, from the one of <see cref="IntrinsicRoundHalfUp"/>.</summary>
  /// <param name="value">The number.</param>
  public static double RoundEven(double value) {
    var rounded = IntrinsicRoundHalfUp(value);
    return rounded - value == 0.5 && rounded % 2.0 != 0.0 ? rounded - 1.0 : rounded;
  }

  /// <summary>The nearest integral number, a tie going away from zero.</summary>
  /// <param name="value">The number.</param>
  public static double RoundAway(double value) => value < 0.0 ? -IntrinsicRoundHalfUp(-value) : IntrinsicRoundHalfUp(value);

  /// <summary>The number without its fraction.</summary>
  /// <param name="value">The number.</param>
  public static double Truncate(double value) => value < 0.0 ? Math.Ceiling(value) : Math.Floor(value);

  /// <summary>The sign of a floating-point number: -1, 0 for either zero, or 1.</summary>
  /// <param name="value">The number.</param>
  /// <exception cref="ArithmeticException">The number is a NaN.</exception>
  public static int SignOf(double value) {
    // A NaN is the one number that is neither below 0 nor at or above it.
    if (!(value < 0.0) && !(value >= 0.0))
      throw new ArithmeticException("invalid operation");

    return value < 0.0 ? -1 : value > 0.0 ? 1 : 0;
  }

  /// <summary>
  /// ECMA-262's <c>ToInt32</c>: the number without its fraction modulo 2^32, a NaN and the infinities
  /// 0. The remainder of two doubles is exact and lies within 2^32, which the conversion to
  /// <c>long</c> holds; the last conversion keeps the low 32 bits.
  /// </summary>
  /// <param name="value">The number.</param>
  public static int ToInt32(double value)
    => double.IsNaN(value) || double.IsInfinity(value) ? 0 : unchecked((int)checked((long)(Math.Truncate(value) % 4294967296.0)));

  /// <summary>ECMA-262's <c>ToUint32</c>: like <see cref="ToInt32"/>, read as an unsigned integer.</summary>
  /// <param name="value">The number.</param>
  public static uint ToUint32(double value)
    => double.IsNaN(value) || double.IsInfinity(value) ? 0u : unchecked((uint)checked((long)(Math.Truncate(value) % 4294967296.0)));

  /// <summary>The smaller of two <c>double</c> as IEEE 754-2019 orders them: -0 before +0, and a NaN when either is one.</summary>
  /// <param name="left">The first number.</param>
  /// <param name="right">The second number.</param>
  public static double MinimumDouble(double left, double right) {
    if (left != right)
      return double.IsNaN(left) || left < right ? left : right;

    return left == 0.0 && 1.0 / left < 0.0 ? left : right;
  }

  /// <summary>The larger of two <c>double</c> as IEEE 754-2019 orders them: +0 after -0, and a NaN when either is one.</summary>
  /// <param name="left">The first number.</param>
  /// <param name="right">The second number.</param>
  public static double MaximumDouble(double left, double right) {
    if (left != right)
      return double.IsNaN(left) || left > right ? left : right;

    return right == 0.0 && 1.0 / right < 0.0 ? left : right;
  }

  /// <summary>The smaller of two <c>float</c> as IEEE 754-2019 orders them.</summary>
  /// <param name="left">The first number.</param>
  /// <param name="right">The second number.</param>
  public static float MinimumSingle(float left, float right) {
    if (left != right)
      return float.IsNaN(left) || left < right ? left : right;

    return left == 0.0f && 1.0f / left < 0.0f ? left : right;
  }

  /// <summary>The larger of two <c>float</c> as IEEE 754-2019 orders them.</summary>
  /// <param name="left">The first number.</param>
  /// <param name="right">The second number.</param>
  public static float MaximumSingle(float left, float right) {
    if (left != right)
      return float.IsNaN(left) || left > right ? left : right;

    return right == 0.0f && 1.0f / right < 0.0f ? left : right;
  }

}
