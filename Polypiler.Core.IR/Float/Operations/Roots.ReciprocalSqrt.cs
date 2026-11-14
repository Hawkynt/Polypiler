// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The correctly rounded reciprocal square root, converted from CORE-MATH's <c>rsqrt.c</c> and
/// <c>rsqrtf.c</c> (MIT). See <c>THIRD-PARTY-NOTICE.CORE-MATH.Roots.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>1 / sqrt(x)</c>, correctly rounded to nearest with ties to even (IEEE 754-2019 §9.2
  /// rSqrt). Either zero gives +infinity, +infinity gives +0, and a negative
  /// <paramref name="x"/> or a NaN gives a NaN.
  /// </summary>
  /// <param name="x">The operand.</param>
  /// <returns>The correctly rounded reciprocal square root.</returns>
  public static double ReciprocalSqrt(double x) {
    var ix = (ulong)BitConverter.DoubleToInt64Bits(x);
    var e = (int)(ix >> 52);
    double r;
    if (e == 0) {
      if (ix == 0)
        return 1.0 / 0.0;

      r = Math.Sqrt(x) / x;
    } else if (e < 0x7fd) {
      // The common case 2^-1022 <= x < 2^1022.
      r = 1.0 / x * Math.Sqrt(x);
    } else if (e < 0x7ff) {
      r = 4.0 / x * (0.25 * Math.Sqrt(x));
    } else {
      var ax = ix << 1;
      // -0 gives +infinity like +0 (IEEE 754-2019 §9.2.1).
      if (ax == 0)
        return 1.0 / 0.0;

      // A NaN, a negative x, or +infinity.
      if (ax > 0x7ffUL << 53 || e >> 11 != 0)
        return (x - x) / (x - x);

      return 0.0;
    }

    // One Newton step r' = r - r/2 (r^2 x - 1), with r x to about 106 bits.
    var rx = r * x;
    var drx = Math.FusedMultiplyAdd(r, x, -rx);
    var h = Math.FusedMultiplyAdd(r, rx, -1.0) + r * drx;
    var dr = r * 0.5 * h;
    var rf = r - dr;

    // The rounding is hard when the correction is too close to a half ulp, or tiny.
    var idr = (ulong)BitConverter.DoubleToInt64Bits(dr);
    var ir = (ulong)BitConverter.DoubleToInt64Bits(r);
    var de = (int)(ir >> 52) - (int)(idr >> 52 & 0x7ff);
    var d = unchecked(idr + 18) & ulong.MaxValue >> 13;
    if (d <= 23 || de > 100)
      rf = RsqrtRefine(rf, x);

    return rf;
  }

  /// <summary>
  /// <c>1 / sqrt(x)</c> of <c>float</c>, correctly rounded to nearest with ties to even. Either zero gives
  /// +infinity, +infinity gives +0, and a negative <paramref name="x"/> or a NaN gives
  /// a NaN.
  /// </summary>
  /// <param name="x">The operand.</param>
  /// <returns>The correctly rounded reciprocal square root.</returns>
  public static float ReciprocalSqrtF(float x) {
    double xd = x;
    var ix = (uint)BitConverter.SingleToInt32Bits(x);
    if (ix >= 0xffu << 23 || ix == 0) {
      // Either zero gives +infinity (IEEE 754-2019 §9.2.1).
      if (ix << 1 == 0)
        return 1.0f / 0.0f;

      // A NaN or a negative x.
      if (ix >> 31 != 0 || ix << 9 != 0)
        return (x - x) / (x - x);

      return 0.0f;
    }

    // The double expression rounds correctly but for these significands, which a factor just
    // below 1 (0x1.ffffffffffffep-1) moves to the right side.
    var c = 1.0;
    var sq = Math.Sqrt(xd);
    var m = ix << 8;
    if (ix == 0x2f7e2au || m == 0xbdf8a800u || m == 0x55b7bd00u)
      c = 0.9999999999999998;

    return (float)(sq * (c / xd));
  }

  /// <summary>
  /// The correctly rounded <c>1 / sqrt(a)</c> from an approximation <paramref name="rf"/> within an
  /// ulp: the residual <c>r^2 a - 1</c> is computed exactly in 128 bits of integers while
  /// <paramref name="rf"/> is stepped across the root, and the midpoint then decides.
  /// </summary>
  private static double RsqrtRefine(double rf, double a) {
    var ir = (ulong)BitConverter.DoubleToInt64Bits(rf);
    var ia = (ulong)BitConverter.DoubleToInt64Bits(a);
    if (ia < 1UL << 52) {
      // A subnormal: normalize; only the parity of the exponent matters.
      var nz = System.Numerics.BitOperations.LeadingZeroCount(ia);
      ia <<= nz - 11;
      ia &= ulong.MaxValue >> 12;
      ia |= (ulong)(nz - 12) << 52;
    }

    // An even power of two has an exact root.
    if (ia << 11 == 1UL << 63)
      return rf;

    var e = (int)(ia >> 52 & 1);
    var rm = (ir << 11 | 1UL << 63) >> 11;
    var am = ((ia & ulong.MaxValue >> 12) | 1UL << 52) << (5 - e);

    // rt = rm am, and rrt = rm^2 am modulo 2^128, as (high, low).
    var rtLow = unchecked(rm * am);
    var rtHigh = MultiplyHigh(rm, am);
    var rrtLow = unchecked(rtLow * rm);
    var rrtHigh = unchecked(MultiplyHigh(rtLow, rm) + rtHigh * rm);
    var s = rrtHigh >> 63;
    var dd = 1 - 2 * (long)s;

    // rts = 2 rt, negated when the residual is negative.
    var negate = unchecked(0 - s);
    var rtsLow = unchecked(((rtLow << 1) ^ negate) + s);
    var rtsHigh = unchecked(((rtHigh << 1 | rtLow >> 63) ^ negate) + RsqrtCarry(rtsLow, s));

    // Step rm by one ulp at a time until the residual changes its sign.
    var am2 = am << 1;
    var am20 = unchecked(0 - am);
    ulong previousLow;
    ulong previousHigh;
    do {
      ir = unchecked(ir - (ulong)dd);
      previousLow = rrtLow;
      previousHigh = rrtHigh;
      am20 = unchecked(am20 + am2);
      var ttLow = unchecked(rtsLow - am20);
      var ttHigh = unchecked(rtsHigh - RsqrtCarry(rtsLow, am20));
      rrtLow = unchecked(previousLow - ttLow);
      rrtHigh = unchecked(previousHigh - ttHigh - RsqrtCarry(previousLow, ttLow));
    } while (((previousHigh ^ rrtHigh) >> 63) == 0);

    if (rrtHigh >> 63 == 0) {
      ir = unchecked(ir + (ulong)dd);
      rrtLow = previousLow;
      rrtHigh = previousHigh;
    }

    // Round to nearest: the sign of the residual at the midpoint above.
    rm = (ir << 11 | 1UL << 63) >> 11;
    rtLow = unchecked(rm * am);
    rtHigh = MultiplyHigh(rm, am);
    var quarter = am >> 2;
    rrtLow = unchecked(rrtLow + quarter);
    rrtHigh = unchecked(rrtHigh + RsqrtCarry(rrtLow, quarter));
    rrtLow = unchecked(rrtLow + rtLow);
    rrtHigh = unchecked(rrtHigh + rtHigh + RsqrtCarry(rrtLow, rtLow));
    ir += rrtHigh >> 63;
    return BitConverter.Int64BitsToDouble((long)ir);
  }

  /// <summary>
  /// 1 when <paramref name="low"/> is below <paramref name="other"/>, else 0: the carry out of the
  /// low half of a sum <c>low = a + other</c>, or the borrow of a difference <c>low - other</c>.
  /// </summary>
  private static ulong RsqrtCarry(ulong low, ulong other) => low < other ? 1UL : 0UL;

}
