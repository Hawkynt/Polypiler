// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The quotient rounded towards negative infinity and its remainder, and the Euclidean ones, whose
/// remainder is never negative, of signed integers; those of unsigned integers are the truncating
/// ones. Each traps where the truncating division does: on a zero divisor, and on the smallest value
/// of the type divided by -1, whose quotient the type cannot hold. A type narrower than <c>int</c>
/// is divided as an <c>int</c>, its smallest value given.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The quotient of two <c>int</c> rounded towards negative infinity.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <param name="smallest">The smallest value of the type divided.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  /// <exception cref="OverflowException">The dividend is the smallest value and the divisor -1.</exception>
  public static int FloorDivide32(int dividend, int divisor, int smallest) {
    CheckDivision32(dividend, divisor, smallest);
    var quotient = dividend / divisor;
    return dividend % divisor != 0 && (dividend < 0) != (divisor < 0) ? quotient - 1 : quotient;
  }

  /// <summary>The remainder of the quotient of two <c>int</c> rounded towards negative infinity, which has the sign of the divisor.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <param name="smallest">The smallest value of the type divided.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  /// <exception cref="OverflowException">The dividend is the smallest value and the divisor -1.</exception>
  public static int FloorModulo32(int dividend, int divisor, int smallest) {
    CheckDivision32(dividend, divisor, smallest);
    var remainder = dividend % divisor;
    return remainder != 0 && (remainder < 0) != (divisor < 0) ? remainder + divisor : remainder;
  }

  /// <summary>The Euclidean quotient of two <c>int</c>, whose remainder is never negative.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <param name="smallest">The smallest value of the type divided.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  /// <exception cref="OverflowException">The dividend is the smallest value and the divisor -1.</exception>
  public static int EuclidDivide32(int dividend, int divisor, int smallest) {
    CheckDivision32(dividend, divisor, smallest);
    var quotient = dividend / divisor;
    if (dividend % divisor >= 0)
      return quotient;

    return divisor > 0 ? quotient - 1 : quotient + 1;
  }

  /// <summary>The Euclidean remainder of two <c>int</c>, never negative.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <param name="smallest">The smallest value of the type divided.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  /// <exception cref="OverflowException">The dividend is the smallest value and the divisor -1.</exception>
  public static int EuclidModulo32(int dividend, int divisor, int smallest) {
    CheckDivision32(dividend, divisor, smallest);
    var remainder = dividend % divisor;
    if (remainder >= 0)
      return remainder;

    return divisor < 0 ? remainder - divisor : remainder + divisor;
  }

  /// <summary>The quotient of two <c>long</c> rounded towards negative infinity.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  /// <exception cref="OverflowException">The dividend is the smallest <c>long</c> and the divisor -1.</exception>
  public static long FloorDivide64(long dividend, long divisor) {
    CheckDivision64(dividend, divisor);
    var quotient = dividend / divisor;
    return dividend % divisor != 0L && (dividend < 0L) != (divisor < 0L) ? quotient - 1L : quotient;
  }

  /// <summary>The remainder of the quotient of two <c>long</c> rounded towards negative infinity.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  /// <exception cref="OverflowException">The dividend is the smallest <c>long</c> and the divisor -1.</exception>
  public static long FloorModulo64(long dividend, long divisor) {
    CheckDivision64(dividend, divisor);
    var remainder = dividend % divisor;
    return remainder != 0L && (remainder < 0L) != (divisor < 0L) ? remainder + divisor : remainder;
  }

  /// <summary>The Euclidean quotient of two <c>long</c>.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  /// <exception cref="OverflowException">The dividend is the smallest <c>long</c> and the divisor -1.</exception>
  public static long EuclidDivide64(long dividend, long divisor) {
    CheckDivision64(dividend, divisor);
    var quotient = dividend / divisor;
    if (dividend % divisor >= 0L)
      return quotient;

    return divisor > 0L ? quotient - 1L : quotient + 1L;
  }

  /// <summary>The Euclidean remainder of two <c>long</c>.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  /// <exception cref="OverflowException">The dividend is the smallest <c>long</c> and the divisor -1.</exception>
  public static long EuclidModulo64(long dividend, long divisor) {
    CheckDivision64(dividend, divisor);
    var remainder = dividend % divisor;
    if (remainder >= 0L)
      return remainder;

    return divisor < 0L ? remainder - divisor : remainder + divisor;
  }

  private static void CheckDivision32(int dividend, int divisor, int smallest) {
    if (divisor == 0)
      throw new DivideByZeroException("divide by zero");

    if (divisor == -1 && dividend == smallest)
      throw new OverflowException("integer overflow");
  }

  private static void CheckDivision64(long dividend, long divisor) {
    if (divisor == 0L)
      throw new DivideByZeroException("divide by zero");

    if (divisor == -1L && dividend == long.MinValue)
      throw new OverflowException("integer overflow");
  }

}
