// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The traps of the integer operations of up to 32 bits, for a target whose numbers do not trap: a
/// check that throws the exception class of the IR's trap, followed by the operation on numbers that
/// are exact - the quotient and remainder of two doubles and the sum, difference and product, each
/// made an integer by ECMA-262's <c>ToInt32</c> or <c>ToUint32</c>, which change nothing a check let
/// through. Once a helper is inlined where an operand is a constant, folding removes the check it
/// proves, and what is left is the operation of the target.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The quotient of two <c>int</c>, rounded towards zero.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <param name="smallest">The smallest value of the type divided.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  /// <exception cref="OverflowException">The dividend is the smallest value and the divisor -1.</exception>
  public static int Divide32(int dividend, int divisor, int smallest) {
    CheckDivision32(dividend, divisor, smallest);
    return IntrinsicToInt32((double)dividend / divisor);
  }

  /// <summary>The remainder of two <c>int</c>, which has the sign of the dividend.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <param name="smallest">The smallest value of the type divided.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  /// <exception cref="OverflowException">The dividend is the smallest value and the divisor -1.</exception>
  public static int Remainder32(int dividend, int divisor, int smallest) {
    CheckDivision32(dividend, divisor, smallest);
    return IntrinsicToInt32((double)dividend % divisor);
  }

  /// <summary>The quotient of two <c>uint</c>.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  public static uint DivideUnsigned32(uint dividend, uint divisor) {
    if (divisor == 0u)
      throw new DivideByZeroException("divide by zero");

    return IntrinsicToUint32((double)dividend / divisor);
  }

  /// <summary>The remainder of two <c>uint</c>.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  public static uint RemainderUnsigned32(uint dividend, uint divisor) {
    if (divisor == 0u)
      throw new DivideByZeroException("divide by zero");

    return IntrinsicToUint32((double)dividend % divisor);
  }

  /// <summary>The sum of two values of a signed type of up to 32 bits, which traps outside the bounds of the type.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  /// <param name="low">The smallest value of the type.</param>
  /// <param name="high">The largest value of the type.</param>
  /// <exception cref="OverflowException">The sum lies outside the bounds.</exception>
  public static int AddChecked32(int left, int right, int low, int high) => Checked32((double)left + right, low, high);

  /// <summary>The difference of two values of a signed type of up to 32 bits, which traps outside the bounds of the type.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  /// <param name="low">The smallest value of the type.</param>
  /// <param name="high">The largest value of the type.</param>
  /// <exception cref="OverflowException">The difference lies outside the bounds.</exception>
  public static int SubtractChecked32(int left, int right, int low, int high) => Checked32((double)left - right, low, high);

  /// <summary>
  /// The product of two values of a signed type of up to 32 bits, which traps outside the bounds of the
  /// type. A product that a double does not hold exactly lies beyond 2^53, outside the bounds whichever
  /// way it is rounded.
  /// </summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <param name="low">The smallest value of the type.</param>
  /// <param name="high">The largest value of the type.</param>
  /// <exception cref="OverflowException">The product lies outside the bounds.</exception>
  public static int MultiplyChecked32(int left, int right, int low, int high) => Checked32((double)left * right, low, high);

  /// <summary>The sum of two values of an unsigned type of up to 32 bits, which traps above the largest value of the type.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  /// <param name="high">The largest value of the type.</param>
  /// <exception cref="OverflowException">The sum lies above the largest value.</exception>
  public static uint AddCheckedUnsigned32(uint left, uint right, uint high) => CheckedUnsigned32((double)left + right, high);

  /// <summary>The difference of two values of an unsigned type of up to 32 bits, which traps where it is negative.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  /// <param name="high">The largest value of the type.</param>
  /// <exception cref="OverflowException">The subtrahend is larger.</exception>
  public static uint SubtractCheckedUnsigned32(uint left, uint right, uint high) => CheckedUnsigned32((double)left - right, high);

  /// <summary>The product of two values of an unsigned type of up to 32 bits, which traps above the largest value of the type.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <param name="high">The largest value of the type.</param>
  /// <exception cref="OverflowException">The product lies above the largest value.</exception>
  public static uint MultiplyCheckedUnsigned32(uint left, uint right, uint high) => CheckedUnsigned32((double)left * right, high);

  /// <summary>A value of a signed type of up to 32 bits that a checked conversion takes to a type of the bounds given, unchanged, which traps outside them.</summary>
  /// <param name="value">The value.</param>
  /// <param name="low">The smallest value the target type holds.</param>
  /// <param name="high">The largest value the target type holds.</param>
  /// <exception cref="OverflowException">The value lies outside the bounds.</exception>
  public static int CheckRange32(int value, int low, int high) {
    if (value < low || value > high)
      throw new OverflowException("integer overflow");

    return value;
  }

  /// <summary>A <c>uint</c> that a checked conversion takes to a type of the bounds given, unchanged, which traps outside them.</summary>
  /// <param name="value">The value.</param>
  /// <param name="low">The smallest value the target type holds.</param>
  /// <param name="high">The largest value the target type holds.</param>
  /// <exception cref="OverflowException">The value lies outside the bounds.</exception>
  public static uint CheckRangeUnsigned32(uint value, uint low, uint high) {
    if (value < low || value > high)
      throw new OverflowException("integer overflow");

    return value;
  }

  /// <summary>A number as an integer of a signed type of up to 32 bits, its fraction cut off, which traps where the result lies outside the bounds or the number is a NaN.</summary>
  /// <param name="value">The number.</param>
  /// <param name="low">The smallest value of the type.</param>
  /// <param name="high">The largest value of the type.</param>
  /// <exception cref="OverflowException">The whole part lies outside the bounds, or the number is a NaN.</exception>
  public static int FromDoubleChecked32(double value, int low, int high) {
    var whole = Math.Truncate(value);
    if (!(whole >= low && whole <= high))
      throw new OverflowException("integer overflow");

    return IntrinsicToInt32(whole);
  }

  /// <summary>A number as an integer of an unsigned type of up to 32 bits, its fraction cut off, which traps where the result lies outside the type or the number is a NaN.</summary>
  /// <param name="value">The number.</param>
  /// <param name="high">The largest value of the type.</param>
  /// <exception cref="OverflowException">The whole part lies outside the type, or the number is a NaN.</exception>
  public static uint FromDoubleCheckedUnsigned32(double value, uint high) {
    var whole = Math.Truncate(value);
    if (!(whole >= 0.0 && whole <= high))
      throw new OverflowException("integer overflow");

    return IntrinsicToUint32(whole);
  }

  /// <summary>A whole number within the bounds as an <c>int</c>; outside them the overflow trap.</summary>
  private static int Checked32(double value, int low, int high) {
    if (value < low || value > high)
      throw new OverflowException("integer overflow");

    return IntrinsicToInt32(value);
  }

  /// <summary>A whole number from 0 to the largest value as a <c>uint</c>; outside that the overflow trap.</summary>
  private static uint CheckedUnsigned32(double value, uint high) {
    if (value < 0.0 || value > high)
      throw new OverflowException("integer overflow");

    return IntrinsicToUint32(value);
  }

}
