// SPDX-License-Identifier: MIT
namespace Polypiler.Core.IR;

/// <summary>
/// Saturating arithmetic: the exact result, or the bound of the type it lies beyond. A sum or a
/// difference of 32 bits overflows where the signs say so (Warren, <i>Hacker's Delight</i>, 2nd
/// edition, §2-13); one of a narrower type, given its bounds, never overflows an <c>int</c> and is
/// clamped. A product is told by the quotient that undoes it, or computed in twice the width.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The sum of two values of a signed type of up to 32 bits, saturated to its bounds.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  /// <param name="low">The smallest value of the type.</param>
  /// <param name="high">The largest value of the type.</param>
  public static int AddSaturateSigned32(int left, int right, int low, int high) {
    var sum = left + right;
    if (((left ^ sum) & (right ^ sum)) < 0)
      return left < 0 ? low : high;

    return sum < low ? low : sum > high ? high : sum;
  }

  /// <summary>The difference of two values of a signed type of up to 32 bits, saturated to its bounds.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  /// <param name="low">The smallest value of the type.</param>
  /// <param name="high">The largest value of the type.</param>
  public static int SubSaturateSigned32(int left, int right, int low, int high) {
    var difference = left - right;
    if (((left ^ right) & (left ^ difference)) < 0)
      return left < 0 ? low : high;

    return difference < low ? low : difference > high ? high : difference;
  }

  /// <summary>The product of two values of a signed type of up to 32 bits, saturated to its bounds.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <param name="low">The smallest value of the type.</param>
  /// <param name="high">The largest value of the type.</param>
  public static int MulSaturateSigned32(int left, int right, int low, int high) {
    var product = (long)left * right;
    return product < low ? low : product > high ? high : (int)product;
  }

  /// <summary>The sum of two values of an unsigned type of up to 32 bits, saturated to its largest value.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  /// <param name="high">The largest value of the type.</param>
  public static uint AddSaturateUnsigned32(uint left, uint right, uint high) {
    var sum = left + right;
    return sum < left || sum > high ? high : sum;
  }

  /// <summary>The difference of two unsigned values, 0 where it would be negative.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  public static uint SubSaturateUnsigned32(uint left, uint right) => left < right ? 0u : left - right;

  /// <summary>The product of two values of an unsigned type of up to 32 bits, saturated to its largest value.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <param name="high">The largest value of the type.</param>
  public static uint MulSaturateUnsigned32(uint left, uint right, uint high) => left != 0u && right > high / left ? high : left * right;

  /// <summary>The sum of two <c>long</c>, saturated.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  public static long AddSaturateSigned64(long left, long right) {
    var sum = left + right;
    return ((left ^ sum) & (right ^ sum)) < 0L ? (left < 0L ? long.MinValue : long.MaxValue) : sum;
  }

  /// <summary>The difference of two <c>long</c>, saturated.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  public static long SubSaturateSigned64(long left, long right) {
    var difference = left - right;
    return ((left ^ right) & (left ^ difference)) < 0L ? (left < 0L ? long.MinValue : long.MaxValue) : difference;
  }

  /// <summary>The product of two <c>long</c>, saturated: it overflows where dividing it again does not give the first factor back.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  public static long MulSaturateSigned64(long left, long right) {
    if (left == 0L || right == 0L)
      return 0L;

    var product = left * right;
    if ((left == -1L && right == long.MinValue) || (right == -1L && left == long.MinValue) || product / right != left)
      return (left < 0L) == (right < 0L) ? long.MaxValue : long.MinValue;

    return product;
  }

  /// <summary>The sum of two <c>ulong</c>, saturated.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  public static ulong AddSaturateUnsigned64(ulong left, ulong right) {
    var sum = left + right;
    return sum < left ? ulong.MaxValue : sum;
  }

  /// <summary>The difference of two <c>ulong</c>, 0 where it would be negative.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  public static ulong SubSaturateUnsigned64(ulong left, ulong right) => left < right ? 0UL : left - right;

  /// <summary>The product of two <c>ulong</c>, saturated.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  public static ulong MulSaturateUnsigned64(ulong left, ulong right) => left != 0UL && right > ulong.MaxValue / left ? ulong.MaxValue : left * right;

}
