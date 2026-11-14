// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// Rounding to a number of decimal digits as .NET's <c>Math.Round(double, int, MidpointRounding)</c>
/// and <c>MathF.Round(float, int, MidpointRounding)</c> define it (MIT): below a limit the number is
/// multiplied by the power of ten, rounded to an integer in the mode and divided again; from the limit
/// on it is returned unchanged. The powers of ten are those of .NET's tables.
/// </summary>
public static partial class FloatOperations {

  /// <summary>The modes of rounding to digits, with the values of .NET's <c>MidpointRounding</c>.</summary>
  public const int RoundToEven = 0;

  /// <summary>A tie goes away from zero.</summary>
  public const int RoundAwayFromZero = 1;

  /// <summary>Towards zero.</summary>
  public const int RoundToZero = 2;

  /// <summary>Towards negative infinity.</summary>
  public const int RoundToNegativeInfinity = 3;

  /// <summary>Towards positive infinity.</summary>
  public const int RoundToPositiveInfinity = 4;

  private static readonly double[] _roundPower10Double = [
    1E0, 1E1, 1E2, 1E3, 1E4, 1E5, 1E6, 1E7, 1E8, 1E9, 1E10, 1E11, 1E12, 1E13, 1E14, 1E15
  ];

  private static readonly float[] _roundPower10Single = [1E0f, 1E1f, 1E2f, 1E3f, 1E4f, 1E5f, 1E6f];

  /// <summary>
  /// The number rounded to a number of decimal digits in a mode, for 0 to 15 digits; a number of
  /// magnitude 10^16 or more, and a NaN, is returned unchanged.
  /// </summary>
  /// <param name="value">The number.</param>
  /// <param name="digits">The number of digits after the point, 0 to 15.</param>
  /// <param name="mode">One of <see cref="RoundToEven"/> to <see cref="RoundToPositiveInfinity"/>.</param>
  /// <returns>The rounded number.</returns>
  public static double RoundDigits(double value, int digits, int mode) {
    if (Math.Abs(value) < 1e16) {
      var power10 = _roundPower10Double[digits];
      value = RoundIntegral(value * power10, mode) / power10;
    }

    return value;
  }

  /// <summary>The <c>float</c> rounded to 0 to 6 decimal digits; from 10^8 on it is returned unchanged.</summary>
  /// <param name="value">The number.</param>
  /// <param name="digits">The number of digits after the point, 0 to 6.</param>
  /// <param name="mode">One of <see cref="RoundToEven"/> to <see cref="RoundToPositiveInfinity"/>.</param>
  /// <returns>The rounded number.</returns>
  public static float RoundDigitsF(float value, int digits, int mode) {
    if (Math.Abs(value) < 1e8f) {
      var power10 = _roundPower10Single[digits];
      value = RoundIntegralF(value * power10, mode) / power10;
    }

    return value;
  }

  /// <summary>The number rounded to an integer in a mode.</summary>
  private static double RoundIntegral(double value, int mode) {
    if (mode == RoundAwayFromZero)
      return Math.Round(value, MidpointRounding.AwayFromZero);

    if (mode == RoundToZero)
      return Math.Truncate(value);

    if (mode == RoundToNegativeInfinity)
      return Math.Floor(value);

    return mode == RoundToPositiveInfinity ? Math.Ceiling(value) : Math.Round(value);
  }

  /// <summary>The <c>float</c> rounded to an integer in a mode.</summary>
  private static float RoundIntegralF(float value, int mode) {
    if (mode == RoundAwayFromZero)
      return MathF.Round(value, MidpointRounding.AwayFromZero);

    if (mode == RoundToZero)
      return MathF.Truncate(value);

    if (mode == RoundToNegativeInfinity)
      return MathF.Floor(value);

    return mode == RoundToPositiveInfinity ? MathF.Ceiling(value) : MathF.Round(value);
  }

}
