// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The correctly rounded sine: <c>cr_sin</c> of CORE-MATH's <c>binary64/sin/sin.c</c> and
/// <c>cr_sinf</c> of its <c>binary32/sin/sinf.c</c> (MIT). See <c>THIRD-PARTY-NOTICE.CORE-MATH.Trigonometric.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>sin(pi*i/16)</c> for <c>0 &lt;= i &lt; 32</c>; the cosine of the same angle is the entry
  /// eight further on, modulo 32.
  /// </summary>
  private static readonly double[] _sinFTable = [
    0.0, 0.19509032201612828, 0.3826834323650898, 0.5555702330196022,
    0.7071067811865476, 0.8314696123025452, 0.9238795325112867, 0.9807852804032304,
    1.0, 0.9807852804032304, 0.9238795325112867, 0.8314696123025452,
    0.7071067811865476, 0.5555702330196022, 0.3826834323650898, 0.19509032201612828,
    0.0, -0.19509032201612828, -0.3826834323650898, -0.5555702330196022,
    -0.7071067811865476, -0.8314696123025452, -0.9238795325112867, -0.9807852804032304,
    -1.0, -0.9807852804032304, -0.9238795325112867, -0.8314696123025452,
    -0.7071067811865476, -0.5555702330196022, -0.3826834323650898, -0.19509032201612828
  ];

  /// <summary>
  /// The sine of <paramref name="x"/> in radians, correctly rounded to nearest. A tiny argument is
  /// its own sine up to one term; below <c>2^31</c> the argument is reduced by multiples of
  /// <c>pi/2^13</c> in double-double arithmetic, above by Payne-Hanek with 128 bits of <c>1/(2*pi)</c>;
  /// a table lookup and a short polynomial then give a result with an error bound, and when the two
  /// ends of the bound round differently an accurate path in 128-bit fixed point decides.
  /// </summary>
  /// <param name="x">The angle in radians.</param>
  /// <returns>The correctly rounded sine; NaN for an infinite or NaN argument.</returns>
  public static double Sin(double x) {
    var bits = BitConverter.DoubleToInt64Bits(x);
    var e = (int)(bits >> 52 & 0x7ff);
    if (e < 0x3ff - 26) {
      // |x| < 2^-26: sin(x) = x - x^3/6 rounds as x - x * 2^-54 does.
      if (bits << 1 == 0)
        return x;

      return Math.FusedMultiplyAdd(x, -5.551115123125783E-17, x);
    }

    if (e < 0x3ff + 31)
      return SinModerate(x, (int)((ulong)bits >> 63));

    if (e == 0x7ff)
      return x - x;

    return SinLarge(x);
  }

  /// <summary>
  /// The sine of <paramref name="x"/> of <c>float</c>, correctly rounded: evaluated in double after a
  /// reduction by multiples of <c>pi/16</c>; the three arguments the evaluation would round wrongly
  /// are exceptions with their results written out.
  /// </summary>
  /// <param name="x">The angle in radians.</param>
  /// <returns>The correctly rounded sine; NaN for an infinite or NaN argument.</returns>
  public static float SinF(float x) {
    var ax = (uint)BitConverter.SingleToInt32Bits(x) << 1;
    if (ax > 0x99000000u || ax < 0x73000000u) {
      // |x| > 2^26 or |x| < 2^-12.
      if (ax < 0x73000000u) {
        if (ax < 0x66000000u) {
          // |x| < 2^-25.
          if (ax == 0u)
            return x;

          return MathF.FusedMultiplyAdd(-x, MathF.Abs(x), x);
        }

        return -0.1666666716337204f * x * (x * x) + x;
      }

      return SinFLarge(x);
    }

    var quadrant = new int[1];
    double z;
    if (ax < 0x822d97c8u) {
      // |x| < 0x1.2d97c8p+3.
      if (ax == 0x7e75b8a2u)
        return SinFSigned(x, 0.6668131947517395f, -1.4901161193847656E-08f);

      if (ax == 0x7f4f0654u)
        return SinFSigned(x, 0.9658464193344116f, -1.4901161193847656E-08f);

      z = TrigFReduceNear(x, quadrant);
    } else {
      if (ax == 0x8c333330u)
        return SinFSigned(x, -0.34761324524879456f, -7.450580596923828E-09f);

      z = TrigFReduce(x, quadrant);
    }

    var ia = quadrant[0];
    var z2 = z * z;
    var z4 = z2 * z2;
    var aa = (_trigFSinCoefficients[0] + z2 * _trigFSinCoefficients[1]) + z4 * (_trigFSinCoefficients[2] + z2 * _trigFSinCoefficients[3]);
    var bb = (_trigFCosCoefficients[0] + z2 * _trigFCosCoefficients[1]) + z4 * (_trigFCosCoefficients[2] + z2 * _trigFCosCoefficients[3]);
    var s0 = _sinFTable[ia & 31];
    var c0 = _sinFTable[(ia + 8) & 31];
    var r = s0 + aa * (z * c0) - bb * (z2 * s0);
    return (float)r;
  }

  /// <summary>
  /// The fast path for <c>2^-26 &lt;= |x| &lt; 2^31</c>: <c>|x| = k*pi/2^13 + r</c>, the sine and
  /// cosine of <c>k*pi/2^13</c> from two tables, those of <c>r</c> from polynomials; the accurate
  /// paths decide when the error bound straddles a rounding boundary.
  /// </summary>
  private static double SinModerate(double x, int sbit) {
    var ax = Math.Abs(x);
    // |5215.189175235227/2^14 - 1/pi| < 2^-55.496 (0x1.45f306dc9c883p+12).
    var k = Math.Round(5215.189175235227 * ax);
    // rh is exact.
    var rh = Math.FusedMultiplyAdd(k, TrigPiHigh, ax);
    var rl = k * TrigPiLow;
    var r = rh + rl;
    var r2 = r * r;
    var j = (long)k;
    // A reduction by an odd multiple of pi changes the sign.
    sbit ^= (int)(j >> 14 & 1);
    var i1 = (int)(j >> 7 & 0x7f) * 4;
    var i2 = (int)(j & 0x7f) * 4;
    // sin(t1 + t2) = sin(t1) * cos(t2) + cos(t1) * sin(t2) in double-double, cos(t1 + t2) in double.
    var s1h = _trigSinCosCoarse[i1] * _trigSinCosFine[i2 + 2];
    var s1l = TrigMulLow(_trigSinCosCoarse[i1], _trigSinCosCoarse[i1 + 1], _trigSinCosFine[i2 + 2], _trigSinCosFine[i2 + 3], s1h);
    var s2h = _trigSinCosFine[i2] * _trigSinCosCoarse[i1 + 2];
    var s2l = TrigMulLow(_trigSinCosFine[i2], _trigSinCosFine[i2 + 1], _trigSinCosCoarse[i1 + 2], _trigSinCosCoarse[i1 + 3], s2h);
    var sh = s1h + s2h;
    var sl = (s1l + s2l) + TrigTwoSumLow(s1h, s2h, sh);
    var ch = _trigSinCosCoarse[i1 + 2] * _trigSinCosFine[i2 + 2] - _trigSinCosCoarse[i1] * _trigSinCosFine[i2];
    // sin(r) - r and cos(r) - 1 for |r| <= 2^-13.339 (0x1.55555553068fp-3, 0x1.55555553bfd3p-5).
    var sr = r * (1.0 - 0.16666666659950335 * r2);
    var cr = r2 * (-0.5 + 0.041666666655141404 * r2);
    var sign = sbit != 0 ? -1.0 : 1.0;
    var fh = sign * sh;
    var fl = sign * (sl + sh * cr + ch * sr);
    // The error bound 0x1.dep-64.
    var lb = fh + (fl - 1.0122043719688889E-19);
    var ub = fh + (fl + 1.0122043719688889E-19);
    if (ub == lb)
      return lb;

    if (Math.Abs(x) < 1.52587890625E-05)
      return SinSmallAccurate(x);

    return SinAccurate(x);
  }

  /// <summary>The fast path for <c>|x| &gt;= 2^31</c>, after the Payne-Hanek reduction modulo <c>2*pi</c>.</summary>
  private static double SinLarge(double x) {
    var reduced = new double[1];
    var j = TrigReduceLarge(Math.Abs(x), reduced);
    // x/(2*pi) = k + j/2^15 + r with 0 <= r < 2^-15.
    var r = reduced[0];
    var sbit = x > 0 ? 0 : 1;
    var r2 = r * r;
    sbit ^= j >> 14;
    var i1 = (j >> 7 & 0x7f) * 4;
    var i2 = (j & 0x7f) * 4;
    var s1h = _trigSinCosCoarse[i1] * _trigSinCosFine[i2 + 2];
    var s1l = TrigMulLow(_trigSinCosCoarse[i1], _trigSinCosCoarse[i1 + 1], _trigSinCosFine[i2 + 2], _trigSinCosFine[i2 + 3], s1h);
    var s2h = _trigSinCosFine[i2] * _trigSinCosCoarse[i1 + 2];
    var s2l = TrigMulLow(_trigSinCosFine[i2], _trigSinCosFine[i2 + 1], _trigSinCosCoarse[i1 + 2], _trigSinCosCoarse[i1 + 3], s2h);
    var sh = s1h + s2h;
    var sl = (s1l + s2l) + TrigTwoSumLow(s1h, s2h, sh);
    var ch = _trigSinCosCoarse[i1 + 2] * _trigSinCosFine[i2 + 2] - _trigSinCosCoarse[i1] * _trigSinCosFine[i2];
    // sin(2*pi*r) and cos(2*pi*r) - 1 (0x1.921fb54442d18p2, 0x1.4abbcdb6b26d1p5, 0x1.3bd3cc9be45dep4, 0x1.03c1eee483083p6).
    var sr = r * (6.283185307179586 - 41.341700961418276 * r2);
    var cr = r2 * (-19.739208802178716 + 64.93938786554104 * r2);
    var sign = sbit != 0 ? -1.0 : 1.0;
    var fh = sign * sh;
    var fl = sign * (sl + sh * cr + ch * sr);
    // The error bound 0x1.01p-63.
    var lb = fh + (fl - 1.088437337221776E-19);
    var ub = fh + (fl + 1.088437337221776E-19);
    if (lb == ub)
      return lb;

    return SinAccurate(x);
  }

  /// <summary>
  /// The accurate path for <c>|x| &lt; 2^-16</c>: <c>x + (c3h + c3l)*x^3 + c5*x^5</c> in double-double,
  /// with a relative error below <c>2^-112.743</c>.
  /// </summary>
  private static double SinSmallAccurate(double x) {
    // c3h = -0x1.5555555555555p-3, c3l = -0x1.55554b00de7e8p-57, c5 = 0x1.111111110848p-7.
    var x2h = x * x;
    var x2l = Math.FusedMultiplyAdd(x, x, -x2h);
    var h = 0.00833333333327091 * x2h;
    h += -9.251854266180608E-18;
    var sum = -0.16666666666666666 + h;
    var l = TrigTwoSumLow(-0.16666666666666666, h, sum);
    h = sum;
    var product = h * x2h;
    l = TrigMulLow(h, l, x2h, x2l, product);
    h = product;
    product = h * x;
    l = TrigMulLow(h, l, x, 0.0, product);
    h = product;
    sum = x + h;
    l += TrigTwoSumLow(x, h, sum);
    return sum + l;
  }

  /// <summary>
  /// The accurate path for <c>|x| &gt;= 2^-16</c>, in 128-bit fixed point: <c>x/(2*pi) = k/2^13 + r</c>,
  /// <c>sin(pi*k/2^12)</c> and <c>cos(pi*k/2^12)</c> from tables of <c>pi*i1/2^6</c> and <c>pi*i2/2^12</c>,
  /// those of <c>2*pi*r</c> from polynomials.
  /// </summary>
  private static double SinAccurate(double x) {
    var reduced = new ulong[3];
    var k = TrigReduceAccurate(x, reduced);
    var negative = reduced[2] != 0;
    // u = 8*r, u2 = u^2, u4 = u^4.
    var uh = reduced[0] << 3 | reduced[1] >> 61;
    var ul = reduced[1] << 3;
    var w = new ulong[2];
    TrigMulHigh128(uh, ul, uh, ul, w);
    var u2h = w[0];
    var u2l = w[1];
    TrigMulHigh128(u2h, u2l, u2h, u2l, w);
    var u4h = w[0];
    var u4l = w[1];
    // k = 2^12*s + 2^6*i1 + i2: sin(pi*k/2^12) = (-1)^s * sin(t1 + t2) with t1 = pi*i1/2^6, t2 = pi*i2/2^12.
    var sbit = (x > 0 ? 0 : 1) ^ (k >> 12);
    var i1 = k >> 6 & 0x3f;
    var i2 = (k & 0x3f) * 2;
    // |sin(t1)| and |cos(t1)| from the table of sines, by sin(pi - t) = sin(t) and cos(t) = sin(t + pi/2).
    var s1 = (i1 <= 32 ? i1 : 64 - i1) * 2;
    var c1 = (i1 <= 32 ? 32 - i1 : i1 - 32) * 2;
    // sin(t1 + t2) = sin(t1) * cos(t2) +/- |cos(t1)| * sin(t2), which is not negative.
    TrigMulHigh128(_trigSinCoarse[s1], _trigSinCoarse[s1 + 1], _trigCosFine[i2], _trigCosFine[i2 + 1], w);
    var szh = w[0];
    var szl = w[1];
    TrigMulHigh128(_trigSinCoarse[c1], _trigSinCoarse[c1 + 1], _trigSinFine[i2], _trigSinFine[i2 + 1], w);
    if (i1 < 32) {
      szl = unchecked(szl + w[1]);
      szh = unchecked(szh + w[0] + TrigCarry(szl, w[1]));
    } else {
      var borrow = TrigBorrow(szl, w[1]);
      szl = unchecked(szl - w[1]);
      szh = unchecked(szh - w[0] - borrow);
    }

    // |cos(t1 + t2)| = |cos(t1)| * cos(t2) -/+ sin(t1) * sin(t2).
    TrigMulHigh128(_trigSinCoarse[c1], _trigSinCoarse[c1 + 1], _trigCosFine[i2], _trigCosFine[i2 + 1], w);
    var czh = w[0];
    var czl = w[1];
    TrigMulHigh128(_trigSinCoarse[s1], _trigSinCoarse[s1 + 1], _trigSinFine[i2], _trigSinFine[i2 + 1], w);
    if (i1 < 32) {
      var borrow = TrigBorrow(czl, w[1]);
      czl = unchecked(czl - w[1]);
      czh = unchecked(czh - w[0] - borrow);
    } else {
      czl = unchecked(czl + w[1]);
      czh = unchecked(czh + w[0] + TrigCarry(czl, w[1]));
    }

    // sin(x) = sin(z) * cos(2*pi*r) + cos(z) * sin(2*pi*r).
    TrigCosPolynomial(u2h, u2l, u4h, u4l, w);
    TrigMulHigh128(szh, szl, w[0], w[1], w);
    szh = w[0];
    szl = w[1];
    TrigSinPolynomial(uh, ul, u2h, u2l, u4h, u4l, w);
    TrigMulHigh128(czh, czl, w[0], w[1], w);
    czh = w[0];
    czl = w[1];
    // The first term is not negative; the second has the sign of cos(z) * r, positive for i1 < 32 and r >= 0.
    if ((i1 < 32) != negative) {
      szl = unchecked(szl + czl);
      szh = unchecked(szh + czh + TrigCarry(szl, czl));
    } else if (TrigLess(szh, szl, czh, czl)) {
      var borrow = TrigBorrow(czl, szl);
      szl = unchecked(czl - szl);
      szh = unchecked(czh - szh - borrow);
      sbit ^= 1;
    } else {
      var borrow = TrigBorrow(szl, czl);
      szl = unchecked(szl - czl);
      szh = unchecked(szh - czh - borrow);
    }

    return TrigToDouble(szh, szl, sbit);
  }

  /// <summary>The binary32 sine of the large arguments: an infinity or NaN, or <c>|x| &gt; 2^26</c>.</summary>
  private static float SinFLarge(float x) {
    var bits = (uint)BitConverter.SingleToInt32Bits(x);
    if (bits << 1 >= 0xffu << 24)
      return x - x;

    var quadrant = new int[1];
    var z = TrigFReduceLarge(bits, quadrant);
    var ia = quadrant[0];
    var z2 = z * z;
    var z4 = z2 * z2;
    var aa = (_trigFSinCoefficients[0] + z2 * _trigFSinCoefficients[1]) + z4 * (_trigFSinCoefficients[2] + z2 * _trigFSinCoefficients[3]);
    var bb = (_trigFCosCoefficients[0] + z2 * _trigFCosCoefficients[1]) + z4 * (_trigFCosCoefficients[2] + z2 * _trigFCosCoefficients[3]);
    var s0 = _sinFTable[ia & 31];
    var c0 = _sinFTable[(ia + 8) & 31];
    var r = s0 + z * (aa * c0 - bb * (z * s0));
    return (float)r;
  }

  /// <summary>An exception of the binary32 sine: <c>rh + rl</c> with the sign of <paramref name="x"/>.</summary>
  private static float SinFSigned(float x, float rh, float rl) {
    var sign = CopySignF(1.0f, x);
    return sign * rh + sign * rl;
  }

}
