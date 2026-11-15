// SPDX-License-Identifier: MIT
namespace Polypiler.Core.IR;

/// <summary>Linear interpolation, defined as three operations in a fixed order.</summary>
public static partial class FloatOperations {

  /// <summary><c>a + (b - a) * t</c>: the subtraction, the multiplication and the addition, each rounded.</summary>
  /// <param name="a">The value at 0.</param>
  /// <param name="b">The value at 1.</param>
  /// <param name="t">The position.</param>
  /// <returns>The interpolated value.</returns>
  public static double Lerp(double a, double b, double t) => a + (b - a) * t;

  /// <summary><c>a + (b - a) * t</c> of <c>float</c>.</summary>
  /// <param name="a">The value at 0.</param>
  /// <param name="b">The value at 1.</param>
  /// <param name="t">The position.</param>
  /// <returns>The interpolated value.</returns>
  public static float LerpF(float a, float b, float t) => a + (b - a) * t;

}
