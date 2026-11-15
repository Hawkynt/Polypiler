// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The operations of the 64-bit integers on their two 32-bit limbs, for a profile that has no 64-bit
/// integer of its own (optimization O0075): legalization replaces every such operation by the call of
/// its helper here. A helper reads the limbs with <see cref="IntrinsicHighWord"/> and the conversion
/// to <c>uint</c>, joins them with <see cref="IntrinsicJoinWords"/>, and computes on <c>uint</c> and
/// <c>int</c> alone - or calls another helper of the family, never itself. Carries and borrows are
/// unsigned comparisons of the low limbs; the sign lies in the high limb. Division shifts and subtracts
/// bit by bit; a product is formed from the four partial products of the limbs, the upper half of the
/// low one by <see cref="MulHighUnsigned32"/>. The traps are those of the operations: a zero divisor,
/// a quotient or a checked result outside the type, a number outside the type converted with a check.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The sum of two 64-bit integers, wrapped around.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  public static long Add64(long left, long right) {
    var low = (uint)left + (uint)right;
    var carry = low < (uint)left ? 1u : 0u;
    return IntrinsicJoinWords(IntrinsicHighWord(left) + IntrinsicHighWord(right) + carry, low);
  }

  /// <summary>The difference of two 64-bit integers, wrapped around.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  public static long Subtract64(long left, long right) {
    var borrow = (uint)left < (uint)right ? 1u : 0u;
    return IntrinsicJoinWords(IntrinsicHighWord(left) - IntrinsicHighWord(right) - borrow, (uint)left - (uint)right);
  }

  /// <summary>The negation of a 64-bit integer, wrapped around: the smallest value is its own.</summary>
  /// <param name="value">The value.</param>
  public static long Negate64(long value) {
    var low = 0u - (uint)value;
    return IntrinsicJoinWords(~IntrinsicHighWord(value) + (low == 0u ? 1u : 0u), low);
  }

  /// <summary>The complement of a 64-bit integer.</summary>
  /// <param name="value">The value.</param>
  public static long Not64(long value) => IntrinsicJoinWords(~IntrinsicHighWord(value), ~(uint)value);

  /// <summary>The bitwise and of two 64-bit integers.</summary>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  public static long And64(long left, long right) => IntrinsicJoinWords(IntrinsicHighWord(left) & IntrinsicHighWord(right), (uint)left & (uint)right);

  /// <summary>The bitwise or of two 64-bit integers.</summary>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  public static long Or64(long left, long right) => IntrinsicJoinWords(IntrinsicHighWord(left) | IntrinsicHighWord(right), (uint)left | (uint)right);

  /// <summary>The bitwise exclusive or of two 64-bit integers.</summary>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  public static long Xor64(long left, long right) => IntrinsicJoinWords(IntrinsicHighWord(left) ^ IntrinsicHighWord(right), (uint)left ^ (uint)right);

  /// <summary>A 64-bit integer shifted to the left; the count is taken modulo 64, as C# takes it.</summary>
  /// <param name="value">The value.</param>
  /// <param name="count">The number of bits.</param>
  public static long ShiftLeft64(long value, int count) {
    count &= 63;
    if (count == 0)
      return value;

    var low = (uint)value;
    if (count >= 32)
      return IntrinsicJoinWords(low << (count - 32), 0u);

    return IntrinsicJoinWords((IntrinsicHighWord(value) << count) | (low >> (32 - count)), low << count);
  }

  /// <summary>A 64-bit integer shifted to the right, the sign copied into the bits that come in; the count is taken modulo 64.</summary>
  /// <param name="value">The value.</param>
  /// <param name="count">The number of bits.</param>
  public static long ShiftRightArithmetic64(long value, int count) {
    count &= 63;
    if (count == 0)
      return value;

    var high = (int)IntrinsicHighWord(value);
    if (count >= 32)
      return IntrinsicJoinWords((uint)(high >> 31), (uint)(high >> (count - 32)));

    return IntrinsicJoinWords((uint)(high >> count), ((uint)value >> count) | ((uint)high << (32 - count)));
  }

  /// <summary>A 64-bit integer shifted to the right, zeros coming in; the count is taken modulo 64.</summary>
  /// <param name="value">The value.</param>
  /// <param name="count">The number of bits.</param>
  public static long ShiftRightLogical64(long value, int count) {
    count &= 63;
    if (count == 0)
      return value;

    var high = IntrinsicHighWord(value);
    if (count >= 32)
      return IntrinsicJoinWords(0u, high >> (count - 32));

    return IntrinsicJoinWords(high >> count, ((uint)value >> count) | (high << (32 - count)));
  }

  /// <summary>Whether two 64-bit integers are equal.</summary>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  public static bool Equal64(long left, long right) => (uint)left == (uint)right && IntrinsicHighWord(left) == IntrinsicHighWord(right);

  /// <summary>The order of two <c>long</c>: -1, 0 or 1.</summary>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  public static int CompareSigned64(long left, long right) {
    var leftHigh = (int)IntrinsicHighWord(left);
    var rightHigh = (int)IntrinsicHighWord(right);
    if (leftHigh != rightHigh)
      return leftHigh < rightHigh ? -1 : 1;

    return CompareLow((uint)left, (uint)right);
  }

  /// <summary>The order of two <c>ulong</c>: -1, 0 or 1.</summary>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  public static int CompareUnsigned64(ulong left, ulong right) {
    var leftHigh = IntrinsicHighWord((long)left);
    var rightHigh = IntrinsicHighWord((long)right);
    if (leftHigh != rightHigh)
      return leftHigh < rightHigh ? -1 : 1;

    return CompareLow((uint)left, (uint)right);
  }

  /// <summary>The product of two 64-bit integers, wrapped around.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  public static long Multiply64(long left, long right) {
    var leftLow = (uint)left;
    var rightLow = (uint)right;
    var high = MulHighUnsigned32(leftLow, rightLow) + IntrinsicHighWord(left) * rightLow + leftLow * IntrinsicHighWord(right);
    return IntrinsicJoinWords(high, leftLow * rightLow);
  }

  /// <summary>The quotient of two <c>long</c>, rounded towards zero.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  /// <exception cref="OverflowException">The dividend is the smallest <c>long</c> and the divisor -1.</exception>
  public static long DivideSigned64(long dividend, long divisor) {
    var quotient = (long)DivideWords(Magnitude64(dividend), Magnitude64(divisor), false);
    CheckQuotient64(dividend, divisor);
    return IsNegative64(dividend) != IsNegative64(divisor) ? -quotient : quotient;
  }

  /// <summary>The remainder of two <c>long</c>, which has the sign of the dividend.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  /// <exception cref="OverflowException">The dividend is the smallest <c>long</c> and the divisor -1.</exception>
  public static long RemainderSigned64(long dividend, long divisor) {
    var rest = (long)DivideWords(Magnitude64(dividend), Magnitude64(divisor), true);
    CheckQuotient64(dividend, divisor);
    return IsNegative64(dividend) ? -rest : rest;
  }

  /// <summary>The quotient of two <c>ulong</c>.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  public static ulong DivideUnsigned64(ulong dividend, ulong divisor) => DivideWords(dividend, divisor, false);

  /// <summary>The remainder of two <c>ulong</c>.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  public static ulong RemainderUnsigned64(ulong dividend, ulong divisor) => DivideWords(dividend, divisor, true);

  /// <summary>The sum of two <c>long</c>, which traps where it leaves the type.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  /// <exception cref="OverflowException">The sum lies outside the type.</exception>
  public static long AddCheckedSigned64(long left, long right) {
    var sum = left + right;
    if ((int)((IntrinsicHighWord(left) ^ IntrinsicHighWord(sum)) & (IntrinsicHighWord(right) ^ IntrinsicHighWord(sum))) < 0)
      throw new OverflowException("integer overflow");

    return sum;
  }

  /// <summary>The sum of two <c>ulong</c>, which traps where it leaves the type.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  /// <exception cref="OverflowException">The sum lies above the largest <c>ulong</c>.</exception>
  public static ulong AddCheckedUnsigned64(ulong left, ulong right) {
    var sum = left + right;
    if (sum < left)
      throw new OverflowException("integer overflow");

    return sum;
  }

  /// <summary>The difference of two <c>long</c>, which traps where it leaves the type.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  /// <exception cref="OverflowException">The difference lies outside the type.</exception>
  public static long SubtractCheckedSigned64(long left, long right) {
    var difference = left - right;
    if ((int)((IntrinsicHighWord(left) ^ IntrinsicHighWord(right)) & (IntrinsicHighWord(left) ^ IntrinsicHighWord(difference))) < 0)
      throw new OverflowException("integer overflow");

    return difference;
  }

  /// <summary>The difference of two <c>ulong</c>, which traps where it is negative.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  /// <exception cref="OverflowException">The subtrahend is larger.</exception>
  public static ulong SubtractCheckedUnsigned64(ulong left, ulong right) {
    if (left < right)
      throw new OverflowException("integer overflow");

    return left - right;
  }

  /// <summary>The product of two <c>long</c>, which traps where it leaves the type.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <exception cref="OverflowException">The product lies outside the type.</exception>
  public static long MultiplyCheckedSigned64(long left, long right) {
    var negative = IsNegative64(left) != IsNegative64(right);
    var leftMagnitude = Magnitude64(left);
    var rightMagnitude = Magnitude64(right);
    var product = leftMagnitude * rightMagnitude;
    var limit = negative ? 0x8000000000000000UL : 0x7FFFFFFFFFFFFFFFUL;
    if (MulHighUnsigned64(leftMagnitude, rightMagnitude) != 0UL || product > limit)
      throw new OverflowException("integer overflow");

    return negative ? -(long)product : (long)product;
  }

  /// <summary>The product of two <c>ulong</c>, which traps where it lies above the largest <c>ulong</c>.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <exception cref="OverflowException">The product does not fit the type.</exception>
  public static ulong MultiplyCheckedUnsigned64(ulong left, ulong right) {
    if (MulHighUnsigned64(left, right) != 0UL)
      throw new OverflowException("integer overflow");

    return left * right;
  }

  /// <summary>A <c>long</c> that a checked conversion takes to a type of the bounds given, unchanged, which traps outside them.</summary>
  /// <param name="value">The value.</param>
  /// <param name="low">The smallest value the target type holds.</param>
  /// <param name="high">The largest value the target type holds.</param>
  /// <exception cref="OverflowException">The value lies outside the bounds.</exception>
  public static long CheckRangeSigned64(long value, long low, long high) {
    if (value < low || value > high)
      throw new OverflowException("integer overflow");

    return value;
  }

  /// <summary>A <c>ulong</c> that a checked conversion takes to a type of the bounds given, unchanged, which traps outside them.</summary>
  /// <param name="value">The value.</param>
  /// <param name="low">The smallest value the target type holds.</param>
  /// <param name="high">The largest value the target type holds.</param>
  /// <exception cref="OverflowException">The value lies outside the bounds.</exception>
  public static ulong CheckRangeUnsigned64(ulong value, ulong low, ulong high) {
    if (value < low || value > high)
      throw new OverflowException("integer overflow");

    return value;
  }

  /// <summary>An <c>int</c> as a <c>long</c>, its sign extended into the high limb.</summary>
  /// <param name="value">The value.</param>
  public static long Widen64(int value) => IntrinsicJoinWords(value < 0 ? 0xFFFFFFFFu : 0u, (uint)value);

  /// <summary>A <c>ulong</c> as the nearest <c>double</c>: the high limb scaled, which is exact, and the low limb added, which rounds once.</summary>
  /// <param name="value">The value.</param>
  public static double ToDoubleUnsigned64(ulong value) => IntrinsicHighWord((long)value) * 4294967296.0 + (uint)value;

  /// <summary>A <c>long</c> as the nearest <c>double</c>.</summary>
  /// <param name="value">The value.</param>
  public static double ToDouble64(long value) => IsNegative64(value) ? -ToDoubleUnsigned64(Magnitude64(value)) : ToDoubleUnsigned64((ulong)value);

  /// <summary>
  /// A <c>ulong</c> as the nearest <c>float</c>, rounded once: below 2^53 the number is exact as a
  /// <c>double</c>; above, the eleven lowest bits are shifted out, and the lowest bit that stays
  /// remembers whether any of them was set, which is all the rounding to binary32 needs of them.
  /// </summary>
  /// <param name="value">The value.</param>
  public static float ToSingleUnsigned64(ulong value) {
    var high = IntrinsicHighWord((long)value);
    var low = (uint)value;
    if (high < 0x200000u)
      return (float)(high * 4294967296.0 + low);

    var kept = (low >> 11) | (high << 21) | ((low & 0x7FFu) != 0u ? 1u : 0u);
    return (float)((high >> 11) * 4294967296.0 + kept) * 2048f;
  }

  /// <summary>A <c>long</c> as the nearest <c>float</c>, rounded once.</summary>
  /// <param name="value">The value.</param>
  public static float ToSingle64(long value) => IsNegative64(value) ? -ToSingleUnsigned64(Magnitude64(value)) : ToSingleUnsigned64((ulong)value);

  /// <summary>A number as the nearest <c>long</c>, its fraction cut off; 0 for a NaN.</summary>
  /// <param name="value">The number.</param>
  public static long FromDouble64(double value) {
    if (value >= 9223372036854775808.0)
      return long.MaxValue;

    if (value > -9223372036854775808.0)
      return (long)WholeToWords(Math.Truncate(value));

    // Below the range, or a NaN, which compares false either way.
    return value <= -9223372036854775808.0 ? long.MinValue : 0L;
  }

  /// <summary>A number as the nearest <c>ulong</c>, its fraction cut off; 0 for a NaN.</summary>
  /// <param name="value">The number.</param>
  public static ulong FromDoubleUnsigned64(double value) {
    // At or below 0, or a NaN, which compares false.
    if (!(value > 0.0))
      return 0UL;

    if (value >= 18446744073709551616.0)
      return ulong.MaxValue;

    return WholeToWords(Math.Truncate(value));
  }

  /// <summary>A number as a <c>long</c>, its fraction cut off, which traps where the result lies outside the type or the number is a NaN.</summary>
  /// <param name="value">The number.</param>
  /// <exception cref="OverflowException">The whole part lies outside the type, or the number is a NaN.</exception>
  public static long FromDoubleChecked64(double value) {
    var whole = Math.Truncate(value);
    if (!(whole >= -9223372036854775808.0 && whole < 9223372036854775808.0))
      throw new OverflowException("integer overflow");

    return (long)WholeToWords(whole);
  }

  /// <summary>A number as a <c>ulong</c>, its fraction cut off, which traps where the result lies outside the type or the number is a NaN.</summary>
  /// <param name="value">The number.</param>
  /// <exception cref="OverflowException">The whole part lies outside the type, or the number is a NaN.</exception>
  public static ulong FromDoubleCheckedUnsigned64(double value) {
    var whole = Math.Truncate(value);
    if (!(whole >= 0.0 && whole < 18446744073709551616.0))
      throw new OverflowException("integer overflow");

    return WholeToWords(whole);
  }

  /// <summary>Steps an element of an array of <c>long</c> by one and gives the value before or after.</summary>
  /// <param name="array">The array.</param>
  /// <param name="index">The index of the element.</param>
  /// <param name="increment">Whether one is added; otherwise it is subtracted.</param>
  /// <param name="postfix">Whether the value before the step is given; otherwise the one after.</param>
  public static long StepElement64(long[] array, int index, bool increment, bool postfix) {
    var before = array[index];
    var after = increment ? before + 1L : before - 1L;
    array[index] = after;
    return postfix ? before : after;
  }

  /// <summary>Steps an element of an array of <c>ulong</c> by one and gives the value before or after.</summary>
  /// <param name="array">The array.</param>
  /// <param name="index">The index of the element.</param>
  /// <param name="increment">Whether one is added; otherwise it is subtracted.</param>
  /// <param name="postfix">Whether the value before the step is given; otherwise the one after.</param>
  public static ulong StepElementUnsigned64(ulong[] array, int index, bool increment, bool postfix) {
    var before = array[index];
    var after = increment ? before + 1UL : before - 1UL;
    array[index] = after;
    return postfix ? before : after;
  }

  /// <summary>Steps an element of an array of <c>long</c> by one, trapping before the store where the result leaves the type.</summary>
  /// <param name="array">The array.</param>
  /// <param name="index">The index of the element.</param>
  /// <param name="increment">Whether one is added; otherwise it is subtracted.</param>
  /// <param name="postfix">Whether the value before the step is given; otherwise the one after.</param>
  /// <exception cref="OverflowException">The result lies outside the type.</exception>
  public static long StepElementChecked64(long[] array, int index, bool increment, bool postfix) {
    var before = array[index];
    var after = increment ? checked(before + 1L) : checked(before - 1L);
    array[index] = after;
    return postfix ? before : after;
  }

  /// <summary>Steps an element of an array of <c>ulong</c> by one, trapping before the store where the result leaves the type.</summary>
  /// <param name="array">The array.</param>
  /// <param name="index">The index of the element.</param>
  /// <param name="increment">Whether one is added; otherwise it is subtracted.</param>
  /// <param name="postfix">Whether the value before the step is given; otherwise the one after.</param>
  /// <exception cref="OverflowException">The result lies outside the type.</exception>
  public static ulong StepElementCheckedUnsigned64(ulong[] array, int index, bool increment, bool postfix) {
    var before = array[index];
    var after = increment ? checked(before + 1UL) : checked(before - 1UL);
    array[index] = after;
    return postfix ? before : after;
  }

  /// <summary>Whether every bit of a flag is set in a 64-bit value: <c>HasFlag</c> of an enum of 64 bits.</summary>
  /// <param name="value">The value.</param>
  /// <param name="flag">The flag.</param>
  public static bool HasFlag64(long value, long flag) => (value & flag) == flag;

  /// <summary>The order of two low limbs: -1, 0 or 1.</summary>
  private static int CompareLow(uint left, uint right) => left == right ? 0 : left < right ? -1 : 1;

  /// <summary>Whether a <c>long</c> is negative: whether the sign bit of its high limb is set.</summary>
  private static bool IsNegative64(long value) => (int)IntrinsicHighWord(value) < 0;

  /// <summary>The magnitude of a <c>long</c> as a <c>ulong</c>, which the smallest <c>long</c> has as well.</summary>
  private static ulong Magnitude64(long value) => IsNegative64(value) ? (ulong)-value : (ulong)value;

  /// <summary>Traps where a quotient of two <c>long</c> lies outside the type: the smallest value divided by -1.</summary>
  private static void CheckQuotient64(long dividend, long divisor) {
    if (IntrinsicHighWord(divisor) == 0xFFFFFFFFu && (uint)divisor == 0xFFFFFFFFu && IntrinsicHighWord(dividend) == 0x80000000u && (uint)dividend == 0u)
      throw new OverflowException("integer overflow");
  }

  /// <summary>
  /// The quotient or the remainder of two <c>ulong</c>, bit by bit: the remainder takes in the next
  /// bit of the dividend and gives up the divisor where it holds it, a bit of the quotient. Before a
  /// bit comes in the remainder holds no more bits of the dividend than lie above that bit, so it
  /// never leaves 64 bits.
  /// </summary>
  /// <exception cref="DivideByZeroException">The divisor is 0.</exception>
  private static ulong DivideWords(ulong dividend, ulong divisor, bool remainder) {
    var divisorHigh = IntrinsicHighWord((long)divisor);
    var divisorLow = (uint)divisor;
    if (divisorHigh == 0u && divisorLow == 0u)
      throw new DivideByZeroException("divide by zero");

    var high = IntrinsicHighWord((long)dividend);
    var low = (uint)dividend;
    if (divisorHigh == 0u && high == 0u)
      return remainder ? low % divisorLow : low / divisorLow;

    var quotientHigh = 0u;
    var quotientLow = 0u;
    var restHigh = 0u;
    var restLow = 0u;
    for (var bit = 63; bit >= 0; --bit) {
      restHigh = (restHigh << 1) | (restLow >> 31);
      restLow = (restLow << 1) | ((bit >= 32 ? high >> (bit - 32) : low >> bit) & 1u);
      if (restHigh > divisorHigh || (restHigh == divisorHigh && restLow >= divisorLow)) {
        restHigh = restHigh - divisorHigh - (restLow < divisorLow ? 1u : 0u);
        restLow -= divisorLow;
        if (bit >= 32)
          quotientHigh |= 1u << (bit - 32);
        else
          quotientLow |= 1u << bit;
      }
    }

    return remainder ? (ulong)IntrinsicJoinWords(restHigh, restLow) : (ulong)IntrinsicJoinWords(quotientHigh, quotientLow);
  }

  /// <summary>A whole number whose magnitude lies below 2^64 as the 64 bits of its two's complement, from its limbs.</summary>
  private static ulong WholeToWords(double whole) {
    var negative = whole < 0.0;
    var magnitude = negative ? -whole : whole;
    var high = Math.Floor(magnitude / 4294967296.0);
    var bits = IntrinsicJoinWords((uint)high, (uint)(magnitude - high * 4294967296.0));
    return (ulong)(negative ? -bits : bits);
  }

}
