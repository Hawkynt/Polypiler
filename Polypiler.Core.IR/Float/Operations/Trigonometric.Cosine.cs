using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The correctly rounded cosine: <c>cr_cos</c> of CORE-MATH's <c>binary64/cos/cos.c</c> and
/// <c>cr_cosf</c> of its <c>binary32/cos/cosf.c</c> (MIT). See <c>THIRD-PARTY-NOTICE.CORE-MATH.Trigonometric.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>cos(pi*i/16)</c> for <c>0 &lt;= i &lt; 32</c>; the sine of the same angle is the entry eight
  /// further on, modulo 32.
  /// </summary>
  private static readonly double[] _cosFTable = [
    1.0, 0.9807852804032304, 0.9238795325112867, 0.8314696123025452,
    0.7071067811865476, 0.5555702330196022, 0.3826834323650898, 0.19509032201612828,
    0.0, -0.19509032201612828, -0.3826834323650898, -0.5555702330196022,
    -0.7071067811865476, -0.8314696123025452, -0.9238795325112867, -0.9807852804032304,
    -1.0, -0.9807852804032304, -0.9238795325112867, -0.8314696123025452,
    -0.7071067811865476, -0.5555702330196022, -0.3826834323650898, -0.19509032201612828,
    0.0, 0.19509032201612828, 0.3826834323650898, 0.5555702330196022,
    0.7071067811865476, 0.8314696123025452, 0.9238795325112867, 0.9807852804032304
  ];

  /// <summary>The bits of <c>|x|</c> of the large binary32 arguments whose cosine the evaluation misses.</summary>
  private static readonly uint[] _cosFExceptionArguments = [0x5922aa80u, 0x5aa4542cu, 0x5f18b878u, 0x6115cb11u];

  /// <summary>The high parts of the cosines of <see cref="_cosFExceptionArguments"/>.</summary>
  private static readonly float[] _cosFExceptionHigh = [0.5339164137840271f, 0.4887744188308716f, 0.996410071849823f, 0.9690579771995544f];

  /// <summary>The low parts of the cosines of <see cref="_cosFExceptionArguments"/>.</summary>
  private static readonly float[] _cosFExceptionLow = [-1.4901161193847656E-08f, 7.450580596923828E-09f, 1.4901161193847656E-08f, -1.4901161193847656E-08f];

  /// <summary>
  /// The cosine of <paramref name="x"/> in radians, correctly rounded to nearest. Below <c>2^-27</c>
  /// it rounds as <c>1 - x^2</c>; up to <c>2^31</c> the argument is reduced by multiples of
  /// <c>pi/2^13</c>, above by Payne-Hanek with 128 bits of <c>1/(2*pi)</c>; a table lookup and a short
  /// polynomial give a result with an error bound, and when the bound straddles a rounding boundary
  /// an accurate path in 128-bit fixed point decides.
  /// </summary>
  /// <param name="x">The angle in radians.</param>
  /// <returns>The correctly rounded cosine; NaN for an infinite or NaN argument.</returns>
  public static double Cos(double x) {
    var z = Math.Abs(x);
    var e = (int)(BitConverter.DoubleToInt64Bits(z) >> 52);
    if (e < 0x3ff - 27)
      return Math.FusedMultiplyAdd(x, -x, 1.0);

    if (e < 0x3ff + 31)
      return CosModerate(z);

    if (e == 0x7ff)
      return x - x;

    return CosLarge(z);
  }

  /// <summary>
  /// The cosine of <paramref name="x"/> of <c>float</c>, correctly rounded: evaluated in double after a
  /// reduction by multiples of <c>pi/16</c>; the five arguments the evaluation would round wrongly are
  /// exceptions with their results written out.
  /// </summary>
  /// <param name="x">The angle in radians.</param>
  /// <returns>The correctly rounded cosine; NaN for an infinite or NaN argument.</returns>
  public static float CosF(float x) {
    var ax = (uint)BitConverter.SingleToInt32Bits(x) << 1;
    if (ax > 0x99000000u || ax < 0x74000000u) {
      // |x| < 2^-11: cos(x) rounds as 1 - x^2/2 does, computed without an underflow of x/2.
      if (ax < 0x74000000u)
        return 0.5f * MathF.FusedMultiplyAdd(x, -x, 2.0f);

      // |x| > 2^26.
      return CosFLarge(x);
    }

    var quadrant = new int[1];
    double z;
    if (ax < 0x82a41896u) {
      if (ax == 0x812d97c8u)
        return 1.1924880638503055E-08f + -2.220446049250313E-16f;

      z = TrigFReduceNear(x, quadrant);
    } else {
      z = TrigFReduce(x, quadrant);
    }

    var ia = quadrant[0];
    var z2 = z * z;
    var z4 = z2 * z2;
    var aa = (_trigFSinCoefficients[0] + z2 * _trigFSinCoefficients[1]) + z4 * (_trigFSinCoefficients[2] + z2 * _trigFSinCoefficients[3]);
    var bb = (_trigFCosCoefficients[0] + z2 * _trigFCosCoefficients[1]) + z4 * (_trigFCosCoefficients[2] + z2 * _trigFCosCoefficients[3]);
    var c0 = _cosFTable[ia & 31];
    var s0 = _cosFTable[(ia + 8) & 31];
    var r = c0 + aa * (z * s0) - bb * (z2 * c0);
    return (float)r;
  }

  /// <summary>
  /// The fast path for <c>2^-27 &lt;= x &lt; 2^31</c>, <c>x</c> positive: <c>x = k*pi/2^13 + r</c>, the
  /// sine and cosine of <c>k*pi/2^13</c> from two tables, those of <c>r</c> from polynomials.
  /// </summary>
  private static double CosModerate(double x) {
    // |5215.189175235227/2^14 - 1/pi| < 2^-55.496 (0x1.45f306dc9c883p+12).
    var k = Math.Round(5215.189175235227 * x);
    // rh is exact.
    var rh = Math.FusedMultiplyAdd(k, TrigPiHigh, x);
    var rl = k * TrigPiLow;
    var r = rh + rl;
    var r2 = r * r;
    var j = (long)k;
    // A reduction by an odd multiple of pi changes the sign.
    var sbit = (int)(j >> 14 & 1);
    var i1 = (int)(j >> 7 & 0x7f) * 4;
    var i2 = (int)(j & 0x7f) * 4;
    // cos(t1 + t2) = cos(t1) * cos(t2) - sin(t1) * sin(t2) in double-double, sin(t1 + t2) in double.
    var c1h = _trigSinCosCoarse[i1 + 2] * _trigSinCosFine[i2 + 2];
    var c1l = TrigMulLow(_trigSinCosCoarse[i1 + 2], _trigSinCosCoarse[i1 + 3], _trigSinCosFine[i2 + 2], _trigSinCosFine[i2 + 3], c1h);
    var c2h = _trigSinCosFine[i2] * _trigSinCosCoarse[i1];
    var c2l = TrigMulLow(_trigSinCosFine[i2], _trigSinCosFine[i2 + 1], _trigSinCosCoarse[i1], _trigSinCosCoarse[i1 + 1], c2h);
    var ch = c1h + -c2h;
    var cl = (c1l + -c2l) + TrigTwoSumLow(c1h, -c2h, ch);
    var sh = _trigSinCosCoarse[i1] * _trigSinCosFine[i2 + 2] + _trigSinCosCoarse[i1 + 2] * _trigSinCosFine[i2];
    // sin(r) - r and cos(r) - 1 for |r| <= 2^-13.339 (0x1.55555553068fp-3, 0x1.55555553bfd3p-5).
    var sr = r * (1.0 - 0.16666666659950335 * r2);
    var cr = r2 * (-0.5 + 0.041666666655141404 * r2);
    var sign = sbit != 0 ? -1.0 : 1.0;
    var fh = sign * ch;
    var fl = sign * (cl + ch * cr - sh * sr);
    // The error bound 0x1.dep-64.
    var lb = fh + (fl - 1.0122043719688889E-19);
    var ub = fh + (fl + 1.0122043719688889E-19);
    if (ub == lb)
      return lb;

    return CosAccurate(x);
  }

  /// <summary>The fast path for <c>x &gt;= 2^31</c>, after the Payne-Hanek reduction modulo <c>2*pi</c>.</summary>
  private static double CosLarge(double x) {
    var reduced = new double[1];
    var j = TrigReduceLarge(x, reduced);
    // x/(2*pi) = k + j/2^15 + r with 0 <= r < 2^-15.
    var r = reduced[0];
    var r2 = r * r;
    var sbit = j >> 14;
    var i1 = (j >> 7 & 0x7f) * 4;
    var i2 = (j & 0x7f) * 4;
    var c1h = _trigSinCosCoarse[i1 + 2] * _trigSinCosFine[i2 + 2];
    var c1l = TrigMulLow(_trigSinCosCoarse[i1 + 2], _trigSinCosCoarse[i1 + 3], _trigSinCosFine[i2 + 2], _trigSinCosFine[i2 + 3], c1h);
    var c2h = _trigSinCosFine[i2] * _trigSinCosCoarse[i1];
    var c2l = TrigMulLow(_trigSinCosFine[i2], _trigSinCosFine[i2 + 1], _trigSinCosCoarse[i1], _trigSinCosCoarse[i1 + 1], c2h);
    var ch = c1h + -c2h;
    var cl = (c1l + -c2l) + TrigTwoSumLow(c1h, -c2h, ch);
    var sh = _trigSinCosCoarse[i1] * _trigSinCosFine[i2 + 2] + _trigSinCosCoarse[i1 + 2] * _trigSinCosFine[i2];
    // sin(2*pi*r) and cos(2*pi*r) - 1 (0x1.921fb54442d18p2, 0x1.4abbcdb6b26d1p5, 0x1.3bd3cc9be45dep4, 0x1.03c1eee483083p6).
    var sr = r * (6.283185307179586 - 41.341700961418276 * r2);
    var cr = r2 * (-19.739208802178716 + 64.93938786554104 * r2);
    var sign = sbit != 0 ? -1.0 : 1.0;
    var fh = sign * ch;
    var fl = sign * (cl + ch * cr - sh * sr);
    // The error bound 0x1.01p-63.
    var lb = fh + (fl - 1.088437337221776E-19);
    var ub = fh + (fl + 1.088437337221776E-19);
    if (lb == ub)
      return lb;

    return CosAccurate(x);
  }

  /// <summary>
  /// The accurate path for <c>x &gt;= 2^-27</c>, <c>x</c> positive, in 128-bit fixed point:
  /// <c>x/(2*pi) = k/2^13 + r</c>, <c>sin(pi*k/2^12)</c> and <c>cos(pi*k/2^12)</c> from tables of
  /// <c>pi*i1/2^6</c> and <c>pi*i2/2^12</c>, those of <c>2*pi*r</c> from polynomials.
  /// </summary>
  private static double CosAccurate(double x) {
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
    // k = 2^12*s + 2^6*i1 + i2: cos(pi*k/2^12) = (-1)^s * cos(t1 + t2) with t1 = pi*i1/2^6, t2 = pi*i2/2^12.
    var sbit = k >> 12 & 1;
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

    // cos(x) = cos(z) * cos(2*pi*r) - sin(z) * sin(2*pi*r).
    TrigCosPolynomial(u2h, u2l, u4h, u4l, w);
    TrigMulHigh128(czh, czl, w[0], w[1], w);
    czh = w[0];
    czl = w[1];
    TrigSinPolynomial(uh, ul, u2h, u2l, u4h, u4l, w);
    TrigMulHigh128(szh, szl, w[0], w[1], w);
    szh = w[0];
    szl = w[1];
    // cos(z) is positive for i1 < 32 and not for i1 >= 32; sin(z) * sin(2*pi*r) has the sign of r.
    if ((i1 >= 32) != negative) {
      czl = unchecked(czl + szl);
      czh = unchecked(czh + szh + TrigCarry(czl, szl));
    } else if (!TrigLess(szh, szl, czh, czl)) {
      var borrow = TrigBorrow(szl, czl);
      czl = unchecked(szl - czl);
      czh = unchecked(szh - czh - borrow);
      sbit ^= 1;
    } else {
      var borrow = TrigBorrow(czl, szl);
      czl = unchecked(czl - szl);
      czh = unchecked(czh - szh - borrow);
    }

    // For i1 >= 32 cos(z) is not positive, which flips the sign. (CORE-MATH looks up four arguments
    // near 2^-22 here, which rounding to nearest never brings to this point.)
    return TrigToDouble(czh, czl, sbit ^ (i1 >= 32 ? 1 : 0));
  }

  /// <summary>
  /// The binary32 cosine of the large arguments: an infinity or NaN, or <c>|x| &gt; 2^26</c>; a result
  /// within 6 units of the last 28 bits of a rounding boundary is looked up among the exceptions.
  /// </summary>
  private static float CosFLarge(float x) {
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
    var s0 = _cosFTable[(ia + 8) & 31];
    var c0 = _cosFTable[ia & 31];
    var r = c0 + z * (aa * s0 - bb * (z * c0));
    var tail = (ulong)(BitConverter.DoubleToInt64Bits(r) + 6) & 0xfffffffUL;
    if (tail <= 12) {
      var ax = bits & 0x7fffffffu;
      for (var i = 0; i < _cosFExceptionArguments.Length; i++)
        if (_cosFExceptionArguments[i] == ax)
          return _cosFExceptionHigh[i] + _cosFExceptionLow[i];
    }

    return (float)r;
  }

}
