// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The IEEE 754 arithmetic of <c>binary32</c> and <c>binary64</c> in the five rounding modes, the
/// fourteen comparisons and the classification, for the targets whose arithmetic only rounds to
/// nearest even. The operands are taken apart into sign, exponent and integral significand; the exact
/// result is formed on 64-bit integers, its significand normalised so that its leading bit is bit 62,
/// with every bit shifted out folded into bit 0 - a sticky bit, which keeps the exact value strictly
/// between the same two neighbours and never on a tie - and then rounded once into the format, in the
/// mode (IEEE 754-2019 §4.3), overflow giving infinity or the largest finite number as the mode says
/// (§7.4). A product is formed from the partial products of 32-bit limbs, a quotient by long division
/// one bit at a time. The structure follows the round-and-pack step of John R. Hauser's Berkeley
/// SoftFloat 3 (BSD-3-Clause), written anew for any width of exponent and fraction: the format is the
/// two last parameters. A NaN, an infinity or a zero operand gives the result of the target's own
/// operation, which is exact there in every mode; only an exact zero sum depends on the mode.
/// The mode is 0 <c>rne</c>, 1 <c>rtz</c>, 2 <c>rup</c>, 3 <c>rdn</c>, 4 <c>rna</c>.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The sum of two <c>double</c>, rounded once in a mode.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  /// <param name="mode">The rounding mode.</param>
  public static double AddRounded64(double left, double right, int mode) => BitConverter.Int64BitsToDouble(AddBits(left, right, mode, 52, 11));

  /// <summary>The difference of two <c>double</c>, rounded once in a mode.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  /// <param name="mode">The rounding mode.</param>
  public static double SubtractRounded64(double left, double right, int mode) => BitConverter.Int64BitsToDouble(AddBits(left, -right, mode, 52, 11));

  /// <summary>The product of two <c>double</c>, rounded once in a mode.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <param name="mode">The rounding mode.</param>
  public static double MultiplyRounded64(double left, double right, int mode) => BitConverter.Int64BitsToDouble(MultiplyBits(left, right, mode, 52, 11));

  /// <summary>The quotient of two <c>double</c>, rounded once in a mode.</summary>
  /// <param name="left">The dividend.</param>
  /// <param name="right">The divisor.</param>
  /// <param name="mode">The rounding mode.</param>
  public static double DivideRounded64(double left, double right, int mode) => BitConverter.Int64BitsToDouble(DivideBits(left, right, mode, 52, 11));

  /// <summary>The sum of two <c>float</c>, rounded once in a mode: the operands are exact as <c>double</c>, the sum is rounded to <c>float</c> alone.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  /// <param name="mode">The rounding mode.</param>
  public static float AddRounded32(float left, float right, int mode) => BitConverter.Int32BitsToSingle((int)AddBits(left, right, mode, 23, 8));

  /// <summary>The difference of two <c>float</c>, rounded once in a mode.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  /// <param name="mode">The rounding mode.</param>
  public static float SubtractRounded32(float left, float right, int mode) => BitConverter.Int32BitsToSingle((int)AddBits(left, -(double)right, mode, 23, 8));

  /// <summary>The product of two <c>float</c>, rounded once in a mode.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <param name="mode">The rounding mode.</param>
  public static float MultiplyRounded32(float left, float right, int mode) => BitConverter.Int32BitsToSingle((int)MultiplyBits(left, right, mode, 23, 8));

  /// <summary>The quotient of two <c>float</c>, rounded once in a mode.</summary>
  /// <param name="left">The dividend.</param>
  /// <param name="right">The divisor.</param>
  /// <param name="mode">The rounding mode.</param>
  public static float DivideRounded32(float left, float right, int mode) => BitConverter.Int32BitsToSingle((int)DivideBits(left, right, mode, 23, 8));

  /// <summary>
  /// One of the fourteen comparisons of IEEE 754-2019 §5.11, numbered as the IR numbers them: 0
  /// <c>oeq</c>, 1 <c>one</c>, 2 <c>olt</c>, 3 <c>ole</c>, 4 <c>ogt</c>, 5 <c>oge</c>, 6 <c>ord</c>, 7
  /// <c>ueq</c>, 8 <c>une</c>, 9 <c>ult</c>, 10 <c>ule</c>, 11 <c>ugt</c>, 12 <c>uge</c>, 13 <c>uno</c>.
  /// A <c>float</c> is compared as the <c>double</c> it converts to exactly.
  /// </summary>
  /// <param name="left">The first operand.</param>
  /// <param name="right">The second operand.</param>
  /// <param name="predicate">The comparison.</param>
  public static bool CompareFloat(double left, double right, int predicate) {
    if (double.IsNaN(left) || double.IsNaN(right))
      return predicate >= 7;

    var less = left < right;
    var equal = left == right;
    var ordered = predicate >= 7 ? predicate - 7 : predicate;
    if (ordered == 0)
      return equal;
    if (ordered == 1)
      return !equal;
    if (ordered == 2)
      return less;
    if (ordered == 3)
      return less || equal;
    if (ordered == 4)
      return !less && !equal;
    if (ordered == 5)
      return !less;

    // ord, and uno for an ordered pair.
    return predicate == 6;
  }

  /// <summary>The class of a <c>double</c> as the bits of the IR's <c>FloatClass</c>: 1 zero, 2 subnormal, 4 normal, 8 infinite, 16 quiet NaN, 32 signalling NaN, and 64 for the sign.</summary>
  /// <param name="value">The number.</param>
  public static int ClassifyFloat64(double value) {
    var bits = BitConverter.DoubleToInt64Bits(value);
    return ClassifyBits((int)((bits >> 52) & 0x7FFL), (bits & 0xFFFFFFFFFFFFFL) != 0L, (bits & 0x8000000000000L) != 0L, bits < 0L);
  }

  /// <summary>The class of a <c>float</c>, told by its own bits: a signalling NaN would be quieted by the conversion to <c>double</c>.</summary>
  /// <param name="value">The number.</param>
  public static int ClassifyFloat32(float value) {
    var bits = BitConverter.SingleToInt32Bits(value);
    var field = (bits >> 23) & 0xFF;
    return ClassifyBits(field == 0xFF ? 0x7FF : field, (bits & 0x7FFFFF) != 0, (bits & 0x400000) != 0, bits < 0);
  }

  /// <summary>The class bits of a number from its exponent field (all ones as 0x7FF), whether its fraction and its top fraction bit are set, and its sign.</summary>
  private static int ClassifyBits(int field, bool fraction, bool quiet, bool negative) {
    var kind = field == 0x7FF ? (!fraction ? 8 : quiet ? 16 : 32) : field != 0 ? 4 : fraction ? 2 : 1;
    return negative ? kind | 64 : kind;
  }

  /// <summary>The bits of the sum of two numbers in a format, rounded once; both operands are values of the format.</summary>
  private static long AddBits(double left, double right, int mode, int fractionBits, int exponentBits) {
    if (!IsOrdinary(left) || !IsOrdinary(right)) {
      if (left == 0.0 && right == 0.0) {
        // An exact zero sum: -0 when both are -0, and in rdn when either is.
        var negative = IsSignBit(left) ? IsSignBit(right) || mode == 3 : IsSignBit(right) && mode == 3;
        return negative ? SignBit(fractionBits, exponentBits) : 0L;
      }

      return Encode(left + right, fractionBits);
    }

    var leftExponent = ExponentOf(left);
    var rightExponent = ExponentOf(right);
    // One bit of headroom for the carry of a sum; the significands end in zeros, so the shift is exact.
    var leftSignificand = SignificandOf(left) >> 1;
    var rightSignificand = SignificandOf(right) >> 1;
    var leftNegative = IsSignBit(left);
    var rightNegative = IsSignBit(right);
    if (leftExponent < rightExponent) {
      var exponent = leftExponent;
      leftExponent = rightExponent;
      rightExponent = exponent;
      var significand = leftSignificand;
      leftSignificand = rightSignificand;
      rightSignificand = significand;
      var negative = leftNegative;
      leftNegative = rightNegative;
      rightNegative = negative;
    }

    rightSignificand = ShiftRightJam(rightSignificand, leftExponent - rightExponent);
    var sign = leftNegative != rightNegative && leftSignificand < rightSignificand ? rightNegative : leftNegative;
    var sum = leftNegative == rightNegative ? leftSignificand + rightSignificand
      : leftSignificand >= rightSignificand ? leftSignificand - rightSignificand
      : rightSignificand - leftSignificand;

    if (sum == 0UL)
      return mode == 3 ? SignBit(fractionBits, exponentBits) : 0L;

    var shift = 0;
    while (sum < 0x4000000000000000UL) {
      sum <<= 1;
      ++shift;
    }

    // The significands were shifted right by one: the exponent of bit 0 is one higher, less the normalising shift.
    return RoundPack(sign, leftExponent + 1 - shift, sum, mode, fractionBits, exponentBits);
  }

  /// <summary>The bits of the product of two numbers in a format, rounded once.</summary>
  private static long MultiplyBits(double left, double right, int mode, int fractionBits, int exponentBits) {
    if (!IsOrdinary(left) || !IsOrdinary(right))
      return Encode(left * right, fractionBits);

    // 53-bit significands with the leading bit at bit 52; their product has 105 or 106 bits.
    var leftSignificand = SignificandOf(left) >> 10;
    var rightSignificand = SignificandOf(right) >> 10;
    var leftLow = leftSignificand & 0xFFFFFFFFUL;
    var leftHigh = leftSignificand >> 32;
    var rightLow = rightSignificand & 0xFFFFFFFFUL;
    var rightHigh = rightSignificand >> 32;
    var lowest = leftLow * rightLow;
    var middle = leftLow * rightHigh + leftHigh * rightLow;
    var low = lowest + (middle << 32);
    var high = leftHigh * rightHigh + (middle >> 32) + (low < lowest ? 1UL : 0UL);
    // The 128-bit product shifted right so that its leading bit is bit 62, the bits shifted out kept as a sticky bit.
    var shift = (high >> 41) != 0UL ? 43 : 42;
    var significand = (high << (64 - shift)) | (low >> shift) | ((low & ((1UL << shift) - 1UL)) != 0UL ? 1UL : 0UL);
    return RoundPack(IsSignBit(left) != IsSignBit(right), ExponentOf(left) + ExponentOf(right) + 20 + shift, significand, mode, fractionBits, exponentBits);
  }

  /// <summary>The bits of the quotient of two numbers in a format, rounded once.</summary>
  private static long DivideBits(double left, double right, int mode, int fractionBits, int exponentBits) {
    if (!IsOrdinary(left) || !IsOrdinary(right))
      return Encode(left / right, fractionBits);

    var dividend = SignificandOf(left) >> 10;
    var divisor = SignificandOf(right) >> 10;
    // floor(dividend × 2^bits / divisor) lies in [2^62, 2^63): 62 further bits when the dividend is the larger, 63 otherwise.
    var bits = dividend >= divisor ? 62 : 63;
    var quotient = 0UL;
    var remainder = dividend;
    for (var index = 0; index <= bits; ++index) {
      quotient <<= 1;
      if (remainder >= divisor) {
        quotient |= 1UL;
        remainder -= divisor;
      }

      remainder <<= 1;
    }

    if (remainder != 0UL)
      quotient |= 1UL;

    return RoundPack(IsSignBit(left) != IsSignBit(right), ExponentOf(left) - ExponentOf(right) - bits, quotient, mode, fractionBits, exponentBits);
  }

  /// <summary>
  /// The bits of <c>±significand × 2^exponent</c> in a format, rounded once in a mode: the significand
  /// has its leading bit at bit 62, and bit 0 set where anything was cut off below it.
  /// </summary>
  private static long RoundPack(bool negative, int exponent, ulong significand, int mode, int fractionBits, int exponentBits) {
    var bias = (1 << (exponentBits - 1)) - 1;
    var leading = exponent + 62;
    var drop = 62 - fractionBits + (leading < 1 - bias ? 1 - bias - leading : 0);
    // Past 63 places all of it lies below half of the smallest subnormal number.
    var integral = drop >= 64 ? 0UL : significand >> drop;
    var rest = drop >= 64 ? significand : significand & ((1UL << drop) - 1UL);
    var midpoint = drop >= 64 ? 0xFFFFFFFFFFFFFFFFUL : 1UL << (drop - 1);
    var above = rest > midpoint;
    var half = rest == midpoint;
    var inexact = rest != 0UL;

    var up = mode == 0 ? above || (half && (integral & 1UL) != 0UL)
      : mode == 4 ? above || half
      : mode == 2 ? inexact && !negative
      : mode == 3 && inexact && negative;
    if (up)
      ++integral;

    // The exponent of bit 0 of the integral significand; it matters only for a normal result, where the drop is below 64.
    var quantum = exponent + drop;
    if ((integral >> (fractionBits + 1)) != 0UL) {
      integral >>= 1;
      ++quantum;
    }

    var sign = negative ? SignBit(fractionBits, exponentBits) : 0L;
    if ((integral >> fractionBits) == 0UL)
      return sign | (long)integral;

    var field = quantum + fractionBits + bias;
    var infinite = (long)(((1UL << exponentBits) - 1UL) << fractionBits);
    if (field >= (1 << exponentBits) - 1) {
      // Infinity, or the largest finite number where the mode rounds towards zero for the sign.
      var largest = mode == 1 || (mode == 2 && negative) || (mode == 3 && !negative);
      return sign | (largest ? infinite - 1L : infinite);
    }

    return sign | ((long)field << fractionBits) | (long)(integral & ((1UL << fractionBits) - 1UL));
  }

  /// <summary>A value shifted right, the bits shifted out kept as bit 0.</summary>
  private static ulong ShiftRightJam(ulong value, int count) {
    if (count == 0)
      return value;
    if (count >= 63)
      return value != 0UL ? 1UL : 0UL;

    return (value >> count) | ((value & ((1UL << count) - 1UL)) != 0UL ? 1UL : 0UL);
  }

  /// <summary>Whether a number is finite and not zero.</summary>
  private static bool IsOrdinary(double value) => value != 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);

  /// <summary>Whether the sign bit of a number is set.</summary>
  private static bool IsSignBit(double value) => BitConverter.DoubleToInt64Bits(value) < 0L;

  /// <summary>The sign bit of a format.</summary>
  private static long SignBit(int fractionBits, int exponentBits) => (long)(1UL << (fractionBits + exponentBits));

  /// <summary>The bits of a number that is a value of a format, in the format.</summary>
  private static long Encode(double value, int fractionBits)
    => fractionBits == 52 ? BitConverter.DoubleToInt64Bits(value) : BitConverter.SingleToInt32Bits((float)value) & 0xFFFFFFFFL;

  /// <summary>The significand of a finite non-zero <c>double</c>, shifted so that its leading bit is bit 62.</summary>
  private static ulong SignificandOf(double value) {
    var bits = BitConverter.DoubleToInt64Bits(value);
    var field = (bits >> 52) & 0x7FFL;
    var significand = (ulong)(bits & 0xFFFFFFFFFFFFFL);
    if (field != 0L)
      significand |= 0x10000000000000UL;

    while (significand < 0x4000000000000000UL)
      significand <<= 1;

    return significand;
  }

  /// <summary>The exponent of bit 0 of <see cref="SignificandOf"/>: the number is that significand times 2 to it.</summary>
  private static int ExponentOf(double value) {
    var bits = BitConverter.DoubleToInt64Bits(value);
    var field = (int)((bits >> 52) & 0x7FFL);
    var significand = (ulong)(bits & 0xFFFFFFFFFFFFFL);
    if (field != 0)
      return field - 1075 - 10;

    // A subnormal number: its significand is shifted further, by as many places as it lacks of 53 bits.
    var exponent = -1074 - 10;
    var top = 0x10000000000000UL;
    while (significand < top) {
      significand <<= 1;
      --exponent;
    }

    return exponent;
  }

}
