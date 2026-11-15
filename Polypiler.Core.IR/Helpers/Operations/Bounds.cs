// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// Bounds of integers: the clamp, which traps on bounds out of order, the magnitude that traps on the
/// smallest value and the one that wraps it, the sign, and the smaller and the larger of two 64-bit
/// integers. A type narrower than <c>int</c> takes the helper of <c>int</c> or <c>uint</c>, which
/// holds every value of it.
/// </summary>
public static partial class HelperOperations {

  /// <summary>An <c>int</c> within two bounds.</summary>
  /// <param name="value">The value.</param>
  /// <param name="low">The lower bound.</param>
  /// <param name="high">The upper bound.</param>
  /// <exception cref="ArithmeticException">The lower bound is above the upper one.</exception>
  public static int ClampSigned32(int value, int low, int high) {
    if (low > high)
      throw new ArithmeticException("invalid operation");

    return value < low ? low : value > high ? high : value;
  }

  /// <summary>A <c>uint</c> within two bounds.</summary>
  /// <param name="value">The value.</param>
  /// <param name="low">The lower bound.</param>
  /// <param name="high">The upper bound.</param>
  /// <exception cref="ArithmeticException">The lower bound is above the upper one.</exception>
  public static uint ClampUnsigned32(uint value, uint low, uint high) {
    if (low > high)
      throw new ArithmeticException("invalid operation");

    return value < low ? low : value > high ? high : value;
  }

  /// <summary>A <c>long</c> within two bounds.</summary>
  /// <param name="value">The value.</param>
  /// <param name="low">The lower bound.</param>
  /// <param name="high">The upper bound.</param>
  /// <exception cref="ArithmeticException">The lower bound is above the upper one.</exception>
  public static long ClampSigned64(long value, long low, long high) {
    if (low > high)
      throw new ArithmeticException("invalid operation");

    return value < low ? low : value > high ? high : value;
  }

  /// <summary>A <c>ulong</c> within two bounds.</summary>
  /// <param name="value">The value.</param>
  /// <param name="low">The lower bound.</param>
  /// <param name="high">The upper bound.</param>
  /// <exception cref="ArithmeticException">The lower bound is above the upper one.</exception>
  public static ulong ClampUnsigned64(ulong value, ulong low, ulong high) {
    if (low > high)
      throw new ArithmeticException("invalid operation");

    return value < low ? low : value > high ? high : value;
  }

  /// <summary>The magnitude of an <c>int</c> of a type whose smallest value has none.</summary>
  /// <param name="value">The value.</param>
  /// <param name="smallest">The smallest value of the type.</param>
  /// <exception cref="OverflowException">The value is the smallest one.</exception>
  public static int AbsSigned32(int value, int smallest) {
    if (value == smallest)
      throw new OverflowException("integer overflow");

    return value < 0 ? -value : value;
  }

  /// <summary>The magnitude of a <c>long</c>.</summary>
  /// <param name="value">The value.</param>
  /// <exception cref="OverflowException">The value is the smallest <c>long</c>.</exception>
  public static long AbsSigned64(long value) {
    if (value == long.MinValue)
      throw new OverflowException("integer overflow");

    return value < 0L ? -value : value;
  }

  /// <summary>The magnitude of an <c>int</c>, which the smallest value wraps to itself.</summary>
  /// <param name="value">The value.</param>
  public static int AbsWrap32(int value) => value < 0 ? -value : value;

  /// <summary>The magnitude of a <c>long</c>, which the smallest value wraps to itself.</summary>
  /// <param name="value">The value.</param>
  public static long AbsWrap64(long value) => value < 0L ? -value : value;

  /// <summary>The sign of an <c>int</c>: -1, 0 or 1.</summary>
  /// <param name="value">The value.</param>
  public static int Sign32(int value) => value < 0 ? -1 : value > 0 ? 1 : 0;

  /// <summary>The sign of a <c>long</c>: -1, 0 or 1.</summary>
  /// <param name="value">The value.</param>
  public static int Sign64(long value) => value < 0L ? -1 : value > 0L ? 1 : 0;

  /// <summary>The sign of a <c>ulong</c>: 0 or 1.</summary>
  /// <param name="value">The value.</param>
  public static int SignUnsigned64(ulong value) => value != 0UL ? 1 : 0;

  /// <summary>The smaller of two <c>long</c>.</summary>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  public static long MinSigned64(long left, long right) => left < right ? left : right;

  /// <summary>The larger of two <c>long</c>.</summary>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  public static long MaxSigned64(long left, long right) => left > right ? left : right;

  /// <summary>The smaller of two <c>ulong</c>.</summary>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  public static ulong MinUnsigned64(ulong left, ulong right) => left < right ? left : right;

  /// <summary>The larger of two <c>ulong</c>.</summary>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  public static ulong MaxUnsigned64(ulong left, ulong right) => left > right ? left : right;

}
