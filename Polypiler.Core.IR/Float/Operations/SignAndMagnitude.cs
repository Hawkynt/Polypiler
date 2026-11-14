// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// Sign and magnitude: the sign copied from one number to another, and the minimum and maximum of
/// IEEE 754-2019 §9.6 by magnitude and with NaN ignored, as .NET defines them (MIT): a NaN operand of
/// the magnitude functions is returned, and the zeros are ordered, -0 before +0.
/// </summary>
public static partial class FloatOperations {

  /// <summary>The magnitude of <paramref name="x"/> with the sign bit of <paramref name="y"/>, NaN included.</summary>
  /// <param name="x">The number that gives the magnitude.</param>
  /// <param name="y">The number that gives the sign.</param>
  /// <returns>The magnitude with the sign.</returns>
  public static double CopySign(double x, double y)
    => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(x) & long.MaxValue | BitConverter.DoubleToInt64Bits(y) & long.MinValue);

  /// <summary>The magnitude of <paramref name="x"/> with the sign bit of <paramref name="y"/>, of <c>float</c>.</summary>
  /// <param name="x">The number that gives the magnitude.</param>
  /// <param name="y">The number that gives the sign.</param>
  /// <returns>The magnitude with the sign.</returns>
  public static float CopySignF(float x, float y)
    => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) & int.MaxValue | BitConverter.SingleToInt32Bits(y) & int.MinValue);

  /// <summary>
  /// The operand of the smaller magnitude, <c>minimumMagnitude</c> of IEEE 754-2019: a NaN when either
  /// is one, and of two of equal magnitude the negative one.
  /// </summary>
  /// <param name="x">The first operand.</param>
  /// <param name="y">The second operand.</param>
  /// <returns>The operand of the smaller magnitude.</returns>
  public static double MinMagnitude(double x, double y) {
    var ax = Math.Abs(x);
    var ay = Math.Abs(y);
    if (ax < ay || IsNaN(ax))
      return x;

    if (ax == ay)
      return BitConverter.DoubleToInt64Bits(x) < 0 ? x : y;

    return y;
  }

  /// <summary>The operand of the smaller magnitude of two <c>float</c>; see <see cref="MinMagnitude"/>.</summary>
  /// <param name="x">The first operand.</param>
  /// <param name="y">The second operand.</param>
  /// <returns>The operand of the smaller magnitude.</returns>
  public static float MinMagnitudeF(float x, float y) {
    var ax = Math.Abs(x);
    var ay = Math.Abs(y);
    if (ax < ay || IsNaNF(ax))
      return x;

    if (ax == ay)
      return BitConverter.SingleToInt32Bits(x) < 0 ? x : y;

    return y;
  }

  /// <summary>
  /// The operand of the larger magnitude, <c>maximumMagnitude</c> of IEEE 754-2019: a NaN when either
  /// is one, and of two of equal magnitude the positive one.
  /// </summary>
  /// <param name="x">The first operand.</param>
  /// <param name="y">The second operand.</param>
  /// <returns>The operand of the larger magnitude.</returns>
  public static double MaxMagnitude(double x, double y) {
    var ax = Math.Abs(x);
    var ay = Math.Abs(y);
    if (ax > ay || IsNaN(ax))
      return x;

    if (ax == ay)
      return BitConverter.DoubleToInt64Bits(x) < 0 ? y : x;

    return y;
  }

  /// <summary>The operand of the larger magnitude of two <c>float</c>; see <see cref="MaxMagnitude"/>.</summary>
  /// <param name="x">The first operand.</param>
  /// <param name="y">The second operand.</param>
  /// <returns>The operand of the larger magnitude.</returns>
  public static float MaxMagnitudeF(float x, float y) {
    var ax = Math.Abs(x);
    var ay = Math.Abs(y);
    if (ax > ay || IsNaNF(ax))
      return x;

    if (ax == ay)
      return BitConverter.SingleToInt32Bits(x) < 0 ? y : x;

    return y;
  }

  /// <summary>
  /// The smaller operand, <c>minimumNumber</c> of IEEE 754-2019: a NaN operand is ignored, so the
  /// result is a NaN only when both are; -0 is smaller than +0.
  /// </summary>
  /// <param name="x">The first operand.</param>
  /// <param name="y">The second operand.</param>
  /// <returns>The smaller operand.</returns>
  public static double MinNumber(double x, double y) {
    if (x != y)
      return IsNaN(y) ? x : x < y ? x : y;

    return BitConverter.DoubleToInt64Bits(x) < 0 ? x : y;
  }

  /// <summary>The smaller of two <c>float</c>, a NaN ignored; see <see cref="MinNumber"/>.</summary>
  /// <param name="x">The first operand.</param>
  /// <param name="y">The second operand.</param>
  /// <returns>The smaller operand.</returns>
  public static float MinNumberF(float x, float y) {
    if (x != y)
      return IsNaNF(y) ? x : x < y ? x : y;

    return BitConverter.SingleToInt32Bits(x) < 0 ? x : y;
  }

  /// <summary>
  /// The larger operand, <c>maximumNumber</c> of IEEE 754-2019: a NaN operand is ignored, so the
  /// result is a NaN only when both are; +0 is larger than -0.
  /// </summary>
  /// <param name="x">The first operand.</param>
  /// <param name="y">The second operand.</param>
  /// <returns>The larger operand.</returns>
  public static double MaxNumber(double x, double y) {
    if (x != y)
      return IsNaN(y) ? x : y < x ? x : y;

    return BitConverter.DoubleToInt64Bits(y) < 0 ? x : y;
  }

  /// <summary>The larger of two <c>float</c>, a NaN ignored; see <see cref="MaxNumber"/>.</summary>
  /// <param name="x">The first operand.</param>
  /// <param name="y">The second operand.</param>
  /// <returns>The larger operand.</returns>
  public static float MaxNumberF(float x, float y) {
    if (x != y)
      return IsNaNF(y) ? x : y < x ? x : y;

    return BitConverter.SingleToInt32Bits(y) < 0 ? x : y;
  }

  /// <summary>
  /// The value within two bounds as <c>Math.Clamp</c> computes it: the lower bound when the value is
  /// below it, the upper bound when above, otherwise the value itself - also a NaN value, and the
  /// value when a bound is a NaN. A lower bound above the upper one is an error the operation traps
  /// on before it calls this method; here it gives what the comparisons give.
  /// </summary>
  /// <param name="value">The value.</param>
  /// <param name="low">The lower bound.</param>
  /// <param name="high">The upper bound.</param>
  /// <returns>The value within the bounds.</returns>
  public static double Clamp(double value, double low, double high)
    => value < low ? low : value > high ? high : value;

  /// <summary>The value within two bounds of <c>float</c>; see <see cref="Clamp"/>.</summary>
  /// <param name="value">The value.</param>
  /// <param name="low">The lower bound.</param>
  /// <param name="high">The upper bound.</param>
  /// <returns>The value within the bounds.</returns>
  public static float ClampF(float value, float low, float high)
    => value < low ? low : value > high ? high : value;

}
