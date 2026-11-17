// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The conversions of IEEE 754 numbers in the five rounding modes (#117), for the targets whose
/// conversions only truncate or round to nearest even: an integer to <c>binary32</c> or
/// <c>binary64</c> and <c>binary64</c> to <c>binary32</c> through the round-and-pack step of
/// <c>IeeeRounding.cs</c>, and a number to an integer type under a range policy. The number is first
/// rounded to an integral <c>double</c> in the mode, once, from its exact value; the policy then applies
/// to that integer: in range it is the integer, outside it traps, saturates, wraps modulo 2^64 - which
/// the conversion to the type, modulo 2^N, completes - or gives the sentinel. A NaN gives what the
/// policy gives it, or raises its own trap. The mode is 0 <c>rne</c>, 1 <c>rtz</c>, 2 <c>rup</c>, 3
/// <c>rdn</c>, 4 <c>rna</c>; the policy 0 trap, 1 saturate, 2 wrap, 3 sentinel; a trap is the number
/// of the IR's trap: 0 divide by zero, 2 integer overflow, 3 invalid operation.
/// </summary>
public static partial class HelperOperations {

  /// <summary>An integer as the nearest <c>double</c> in a mode.</summary>
  /// <param name="value">The integer; a <c>ulong</c> as its bits.</param>
  /// <param name="isUnsigned">Whether the bits are those of a <c>ulong</c>.</param>
  /// <param name="mode">The rounding mode.</param>
  public static double IntToDoubleRounded(long value, bool isUnsigned, int mode) => BitConverter.Int64BitsToDouble(IntegerBits(value, isUnsigned, mode, 52, 11));

  /// <summary>An integer as the nearest <c>float</c> in a mode.</summary>
  /// <param name="value">The integer; a <c>ulong</c> as its bits.</param>
  /// <param name="isUnsigned">Whether the bits are those of a <c>ulong</c>.</param>
  /// <param name="mode">The rounding mode.</param>
  public static float IntToSingleRounded(long value, bool isUnsigned, int mode) => BitConverter.Int32BitsToSingle((int)IntegerBits(value, isUnsigned, mode, 23, 8));

  /// <summary>A <c>double</c> as the nearest <c>float</c> in a mode, overflow giving infinity or the largest <c>float</c> as the mode says.</summary>
  /// <param name="value">The number.</param>
  /// <param name="mode">The rounding mode.</param>
  public static float DoubleToSingleRounded(double value, int mode) {
    if (!IsOrdinary(value))
      return (float)value;

    return BitConverter.Int32BitsToSingle((int)RoundPack(IsSignBit(value), ExponentOf(value), SignificandOf(value), mode, 23, 8));
  }

  /// <summary>A number rounded to an integral number in a mode; a NaN and the infinities as they are.</summary>
  /// <param name="value">The number.</param>
  /// <param name="mode">The rounding mode.</param>
  public static double RoundToIntegral(double value, int mode) {
    if (double.IsNaN(value) || double.IsInfinity(value))
      return value;

    var floor = Math.Floor(value);
    // Exact: the number and its floor lie within one of each other.
    var fraction = value - floor;
    if (fraction == 0.0)
      return value;
    if (mode == 1)
      return value < 0.0 ? floor + 1.0 : floor;
    if (mode == 2)
      return floor + 1.0;
    if (mode == 3)
      return floor;
    if (fraction > 0.5 || (fraction == 0.5 && (mode == 4 ? value > 0.0 : floor % 2.0 != 0.0)))
      return floor + 1.0;

    return floor;
  }

  /// <summary>
  /// A number as the bits of an integer of up to 64 bits under a contract: rounded in the mode, then in
  /// range the integer, outside the policy's value; the caller converts the bits to the type.
  /// </summary>
  /// <param name="value">The number.</param>
  /// <param name="mode">The rounding mode.</param>
  /// <param name="policy">The range policy.</param>
  /// <param name="low">The smallest value of the type, a power of two or 0.</param>
  /// <param name="high">The largest value of the type plus one, a power of two.</param>
  /// <param name="smallest">The smallest value of the type, as bits.</param>
  /// <param name="largest">The largest value of the type, as bits.</param>
  /// <param name="sentinel">The value of the sentinel policy, as bits.</param>
  /// <param name="trap">The trap of the trap policy.</param>
  /// <param name="nanTrap">The trap a NaN raises, or -1 where it gives the policy's value.</param>
  public static long FloatToIntBits(double value, int mode, int policy, double low, double high, long smallest, long largest, long sentinel, int trap, int nanTrap) {
    if (double.IsNaN(value)) {
      if (nanTrap >= 0)
        RaiseTrap(nanTrap);
      if (policy == 0)
        RaiseTrap(trap);

      return policy == 3 ? sentinel : 0L;
    }

    var integral = RoundToIntegral(value, mode);
    if (integral >= low && integral < high)
      return WrapToBits(integral);
    if (policy == 0)
      RaiseTrap(trap);
    if (policy == 1)
      return integral < low ? smallest : largest;
    if (policy == 2)
      return double.IsInfinity(integral) ? 0L : WrapToBits(integral);

    return sentinel;
  }

  /// <summary>An integral number modulo 2^64, as the bits of a <c>long</c>.</summary>
  private static long WrapToBits(double integral) {
    // The remainder of two doubles is exact; it keeps the sign of the number and lies within 2^64.
    var rest = integral % 18446744073709551616.0;
    if (rest >= 9223372036854775808.0)
      return (long)(rest - 9223372036854775808.0) + long.MinValue;
    if (rest < -9223372036854775808.0)
      return (long)(rest + 9223372036854775808.0) + long.MinValue;

    return (long)rest;
  }

  /// <summary>Throws the exception class of a trap of the IR.</summary>
  private static void RaiseTrap(int trap) {
    if (trap == 0)
      throw new DivideByZeroException("divide by zero");
    if (trap == 2)
      throw new OverflowException("overflow");

    throw new ArithmeticException("invalid operation");
  }

  /// <summary>The bits of an integer in a format, rounded once.</summary>
  private static long IntegerBits(long value, bool isUnsigned, int mode, int fractionBits, int exponentBits) {
    if (value == 0L)
      return 0L;

    var negative = !isUnsigned && value < 0L;
    var magnitude = negative ? 0UL - (ulong)value : (ulong)value;
    var exponent = 0;
    if (magnitude >= 0x8000000000000000UL) {
      magnitude = (magnitude >> 1) | (magnitude & 1UL);
      exponent = 1;
    }

    while (magnitude < 0x4000000000000000UL) {
      magnitude <<= 1;
      --exponent;
    }

    return RoundPack(negative, exponent, magnitude, mode, fractionBits, exponentBits);
  }

}
