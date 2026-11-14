using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The representation of a floating-point number: scaling by a power of two, the exponent, the
/// neighbours and the split into fraction and exponent. <c>ScaleB</c> is converted from musl's
/// <c>scalbn.c</c> and <c>scalbnf.c</c> as .NET carries it; the split from musl's <c>frexp.c</c> and
/// <c>frexpf.c</c>; the exponent and the neighbours follow .NET's <c>Math.ILogB</c>,
/// <c>Math.BitIncrement</c> and <c>Math.BitDecrement</c> (MIT). See <c>THIRD-PARTY-NOTICE.musl.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>x * 2^n</c>, rounded once. The factor is built from its exponent; a scale beyond the
  /// exponent range is applied in steps, and one into the subnormal range is first taken 53 binades
  /// further down, so that only the last multiplication rounds.
  /// </summary>
  /// <param name="x">The number.</param>
  /// <param name="n">The power of two.</param>
  /// <returns>The scaled number.</returns>
  public static double ScaleB(double x, int n) {
    var y = x;
    if (n > 1023) {
      y *= 8.98846567431158E+307;
      n -= 1023;
      if (n > 1023) {
        y *= 8.98846567431158E+307;
        n -= 1023;
        if (n > 1023)
          n = 1023;
      }
    } else if (n < -1022) {
      y *= 2.2250738585072014E-308 * 9007199254740992.0;
      n += 1022 - 53;
      if (n < -1022) {
        y *= 2.2250738585072014E-308 * 9007199254740992.0;
        n += 1022 - 53;
        if (n < -1022)
          n = -1022;
      }
    }

    return y * BitConverter.Int64BitsToDouble((long)(0x3ff + n) << 52);
  }

  /// <summary><c>x * 2^n</c> of <c>float</c>, rounded once; see <see cref="ScaleB"/>.</summary>
  /// <param name="x">The number.</param>
  /// <param name="n">The power of two.</param>
  /// <returns>The scaled number.</returns>
  public static float ScaleBF(float x, int n) {
    var y = x;
    if (n > 127) {
      y *= 1.70141183E+38f;
      n -= 127;
      if (n > 127) {
        y *= 1.70141183E+38f;
        n -= 127;
        if (n > 127)
          n = 127;
      }
    } else if (n < -126) {
      y *= 1.17549435E-38f * 16777216.0f;
      n += 126 - 24;
      if (n < -126) {
        y *= 1.17549435E-38f * 16777216.0f;
        n += 126 - 24;
        if (n < -126)
          n = -126;
      }
    }

    return y * BitConverter.Int32BitsToSingle((0x7f + n) << 23);
  }

  /// <summary>
  /// The binary exponent of a number, <c>logB</c> of IEEE 754-2019 §5.3.3 as an integer: of a
  /// subnormal number the exponent of its highest set bit; <see cref="int.MinValue"/> for a zero and
  /// <see cref="int.MaxValue"/> for an infinity or a NaN, as .NET returns them.
  /// </summary>
  /// <param name="x">The number.</param>
  /// <returns>The exponent.</returns>
  public static int ILogB(double x) {
    var bits = BitConverter.DoubleToInt64Bits(x) & long.MaxValue;
    var biased = (int)(bits >> 52);
    if (biased == 0x7ff)
      return int.MaxValue;

    if (biased != 0)
      return biased - 1023;

    if (bits == 0)
      return int.MinValue;

    return -1022 - (System.Numerics.BitOperations.LeadingZeroCount((ulong)bits) - 11);
  }

  /// <summary>The binary exponent of a <c>float</c>; see <see cref="ILogB"/>.</summary>
  /// <param name="x">The number.</param>
  /// <returns>The exponent.</returns>
  public static int ILogBF(float x) {
    var bits = BitConverter.SingleToInt32Bits(x) & int.MaxValue;
    var biased = bits >> 23;
    if (biased == 0xff)
      return int.MaxValue;

    if (biased != 0)
      return biased - 127;

    if (bits == 0)
      return int.MinValue;

    return -126 - (System.Numerics.BitOperations.LeadingZeroCount((uint)bits) - 8);
  }

  /// <summary>
  /// The next number towards positive infinity, as <c>Math.BitIncrement</c>: the smallest subnormal
  /// after -0 and +0, the most negative finite number after negative infinity; positive infinity and a
  /// NaN are returned unchanged.
  /// </summary>
  /// <param name="x">The number.</param>
  /// <returns>Its successor.</returns>
  public static double BitIncrement(double x) {
    var bits = BitConverter.DoubleToInt64Bits(x);
    if ((bits & 0x7ff0000000000000L) == 0x7ff0000000000000L)
      return bits == unchecked((long)0xfff0000000000000UL) ? -1.7976931348623157E+308 : x;

    if (bits == long.MinValue)
      return 4.94065645841247E-324;

    return BitConverter.Int64BitsToDouble(bits < 0 ? bits - 1 : bits + 1);
  }

  /// <summary>The next <c>float</c> towards positive infinity; see <see cref="BitIncrement"/>.</summary>
  /// <param name="x">The number.</param>
  /// <returns>Its successor.</returns>
  public static float BitIncrementF(float x) {
    var bits = BitConverter.SingleToInt32Bits(x);
    if ((bits & 0x7f800000) == 0x7f800000)
      return bits == unchecked((int)0xff800000U) ? -3.40282347E+38f : x;

    if (bits == int.MinValue)
      return 1.401298E-45f;

    return BitConverter.Int32BitsToSingle(bits < 0 ? bits - 1 : bits + 1);
  }

  /// <summary>
  /// The next number towards negative infinity, as <c>Math.BitDecrement</c>: the negative smallest
  /// subnormal before +0 (but -0 steps on from itself like any negative number), the largest finite
  /// number before positive infinity; negative infinity and a NaN are returned unchanged.
  /// </summary>
  /// <param name="x">The number.</param>
  /// <returns>Its predecessor.</returns>
  public static double BitDecrement(double x) {
    var bits = BitConverter.DoubleToInt64Bits(x);
    if ((bits & 0x7ff0000000000000L) == 0x7ff0000000000000L)
      return bits == 0x7ff0000000000000L ? 1.7976931348623157E+308 : x;

    if (bits == 0)
      return -4.94065645841247E-324;

    return BitConverter.Int64BitsToDouble(bits < 0 ? bits + 1 : bits - 1);
  }

  /// <summary>The next <c>float</c> towards negative infinity; see <see cref="BitDecrement"/>.</summary>
  /// <param name="x">The number.</param>
  /// <returns>Its predecessor.</returns>
  public static float BitDecrementF(float x) {
    var bits = BitConverter.SingleToInt32Bits(x);
    if ((bits & 0x7f800000) == 0x7f800000)
      return bits == 0x7f800000 ? 3.40282347E+38f : x;

    if (bits == 0)
      return -1.401298E-45f;

    return BitConverter.Int32BitsToSingle(bits < 0 ? bits + 1 : bits - 1);
  }

  /// <summary>
  /// The fraction of C's <c>frexp</c>: the number with its exponent replaced so that its magnitude is
  /// in [0.5, 1); a zero, an infinity and a NaN are returned unchanged.
  /// </summary>
  /// <param name="x">The number.</param>
  /// <returns>The fraction, with the sign of the number.</returns>
  public static double Fraction(double x) {
    var bits = BitConverter.DoubleToInt64Bits(x);
    var biased = (int)(bits >> 52 & 0x7ff);
    if (biased == 0) {
      if (x == 0)
        return x;

      bits = BitConverter.DoubleToInt64Bits(x * 1.8446744073709552E+19);
    } else if (biased == 0x7ff) {
      return x;
    }

    return BitConverter.Int64BitsToDouble(bits & unchecked((long)0x800fffffffffffffUL) | 0x3fe0000000000000L);
  }

  /// <summary>The fraction of C's <c>frexpf</c>; see <see cref="Fraction"/>.</summary>
  /// <param name="x">The number.</param>
  /// <returns>The fraction, with the sign of the number.</returns>
  public static float FractionF(float x) {
    var bits = BitConverter.SingleToInt32Bits(x);
    var biased = bits >> 23 & 0xff;
    if (biased == 0) {
      if (x == 0)
        return x;

      bits = BitConverter.SingleToInt32Bits(x * 1.8446744E+19f);
    } else if (biased == 0xff) {
      return x;
    }

    return BitConverter.Int32BitsToSingle(bits & unchecked((int)0x807fffffU) | 0x3f000000);
  }

  /// <summary>
  /// The exponent of C's <c>frexp</c>: the power of two that the fraction is multiplied by to give the
  /// number; 0 for a zero, an infinity and a NaN.
  /// </summary>
  /// <param name="x">The number.</param>
  /// <returns>The exponent.</returns>
  public static int Exponent(double x) {
    var biased = (int)(BitConverter.DoubleToInt64Bits(x) >> 52 & 0x7ff);
    if (biased == 0)
      return x == 0 ? 0 : (int)(BitConverter.DoubleToInt64Bits(x * 1.8446744073709552E+19) >> 52 & 0x7ff) - 0x3fe - 64;

    return biased == 0x7ff ? 0 : biased - 0x3fe;
  }

  /// <summary>The exponent of C's <c>frexpf</c>; see <see cref="Exponent"/>.</summary>
  /// <param name="x">The number.</param>
  /// <returns>The exponent.</returns>
  public static int ExponentF(float x) {
    var biased = BitConverter.SingleToInt32Bits(x) >> 23 & 0xff;
    if (biased == 0)
      return x == 0 ? 0 : (BitConverter.SingleToInt32Bits(x * 1.8446744E+19f) >> 23 & 0xff) - 0x7e - 64;

    return biased == 0xff ? 0 : biased - 0x7e;
  }

}
