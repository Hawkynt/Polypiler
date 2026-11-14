using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The inverse hyperbolic sine, correctly rounded, converted from CORE-MATH's <c>asinh.c</c> and
/// <c>asinhf.c</c> (MIT); see <c>THIRD-PARTY-NOTICE.CORE-MATH.InverseHyperbolic.txt</c>.
/// </summary>
public static partial class FloatOperations {

  // asinh(x) - x near zero: x^3 times the coefficients of x^0, x^2, ... on |x| < 2^-12, < 0x1.3p-6
  // and < 0x1.bp-4.
  private static readonly double[] _asinhSmall = [
    -0.16666666666666666, 0.07499999733907847
  ];

  private static readonly double[] _asinhMedium = [
    -0.16666666666666666, 0.0749999999997719, -0.0446428538299723, 0.030366545941310663
  ];

  private static readonly double[] _asinhLarge = [
    -0.16666666666666666, 0.07499999999999951, -0.0446428571423326, 0.0303819442002541,
    -0.02237210153194042, 0.017345577248855333, -0.013510645942218957
  ];

  // The accurate asinh(x)/x - 1 near zero: double-double coefficients of x^2 to x^24, then doubles of x^26 to x^34.
  private static readonly double[] _asinhZeroHigh = [
    -0.16666666666666666, -9.25185853854297e-18, 0.075, 2.7755575615550295e-18,
    -0.044642857142857144, 9.912705698174665e-19, 0.030381944444444444, 3.8548671327582004e-19,
    -0.022372159090909092, 9.485871075109131e-19, 0.017352764423076924, -1.2633993685676637e-18,
    -0.01396484374999994, -5.300814386530525e-19, 0.011551800896134386, 7.186841828991838e-19,
    -0.00976160952885175, -7.862905803702611e-19, 0.008390335793399237, -7.498099604023874e-19,
    -0.007312525300826245, -2.8835404659690026e-19, 0.006447195160858236, 2.7174708906665914e-19
  ];

  private static readonly double[] _asinhZeroLow = [
    -0.005739738542546391, 0.005148958294676521, -0.004614686668431739, 0.003915181914363946,
    -0.0024076353540777497
  ];

  // asinh(x)/x - 1 of float near zero: the coefficients of x^2 to x^16, negated.
  private static readonly double[] _asinhFloatSmall = [
    0.1666666666666666, -0.07499999999987002, 0.04464285709978007, -0.03038193899998537,
    0.022371820451468214, -0.017341279402218638, 0.013747204759994313, -0.009357447726757803
  ];

  /// <summary>
  /// The inverse hyperbolic sine <c>log(x + sqrt(x^2 + 1))</c>, correctly rounded to nearest. Near
  /// zero an odd polynomial; elsewhere the logarithm of the double-double <c>|x| + sqrt(x^2 + 1)</c>
  /// from tables, and where its error bound does not decide the rounding, an accurate evaluation in
  /// triple-double.
  /// </summary>
  /// <param name="x">The argument.</param>
  /// <returns>The correctly rounded inverse hyperbolic sine; NaN for NaN and <c>x</c> for an infinity.</returns>
  public static double Asinh(double x) {
    var ax = Math.Abs(x);
    var u = (ulong)BitConverter.DoubleToInt64Bits(ax);
    if (u < 0x3fbb000000000000UL) {
      // |x| < 0x1.bp-4; below 0x1.7137449123ef7p-26 the result rounds to x.
      if (u < 0x3e57137449123ef7UL) {
        if (u == 0)
          return x;

        return Math.FusedMultiplyAdd(-8.673617379884035e-19, x, x); // -0x1p-60
      }

      var x2h = x * x;
      var x2l = Math.FusedMultiplyAdd(x, x, -x2h);
      var x3h = x2h * x;
      double sl;
      if (u < 0x3f93000000000000UL) {
        if (u < 0x3f30000000000000UL) {
          if (u < 0x3e5a000000000000UL)
            sl = x3h * -0.16666666666666666; // -0x1.5555555555555p-3
          else
            sl = x3h * (_asinhSmall[0] + x2h * _asinhSmall[1]);
        } else {
          sl = x3h * (_asinhMedium[0] + x2h * (_asinhMedium[1] + x2h * (_asinhMedium[2] + x2h * _asinhMedium[3])));
        }
      } else {
        var c1 = _asinhLarge[1] + x2h * _asinhLarge[2];
        var c3 = _asinhLarge[3] + x2h * _asinhLarge[4];
        var c5 = _asinhLarge[5] + x2h * _asinhLarge[6];
        var x4 = x2h * x2h;
        sl = x3h * (_asinhLarge[0] + x2h * (c1 + x4 * (c3 + x4 * c5)));
      }

      var eps = 1.6349768761081407e-16 * x3h; // 0x1.79p-53
      var lb = x + (sl - eps);
      var ub = x + (sl + eps);
      if (lb == ub)
        return lb;

      return AsinhZero(x, x2h, x2l);
    }

    // |x| >= 0x1.bp-4: (ah, al) approximates |x| + sqrt(x^2 + 1), or half of it from 2^52 on.
    var squareHigh = 0.0;
    var squareLow = 0.0;
    double ah;
    double al;
    var off = 0x3ff;
    var low = new double[1];
    if (u < 0x4190000000000000UL) {
      // |x| < 2^26
      squareHigh = x * x;
      squareLow = Math.FusedMultiplyAdd(x, x, -squareHigh);
      double th;
      if (u < 0x3ff0000000000000UL)
        th = InverseHyperbolicFastTwoSum(1, squareHigh, low);
      else
        th = InverseHyperbolicFastTwoSum(squareHigh, 1, low);

      var tl = low[0] + squareLow;
      ah = Math.Sqrt(th);
      var rs = 0.5 / th;
      al = (tl - Math.FusedMultiplyAdd(ah, ah, -th)) * (rs * ah);
      ah = InverseHyperbolicFastTwoSum(ah, ax, low);
      al += low[0];
    } else if (u < 0x4330000000000000UL) {
      // |x| < 2^52
      ah = 2 * ax;
      al = 0.5 / ax;
    } else {
      if (u >= 0x7ff0000000000000UL)
        return x + x;

      off = 0x3fe;
      ah = ax;
      al = 0;
    }

    // log(ah) = e*log(2) + log(1/(r1*r2)) + log(1 + dx) with dx = r1*r2*m - 1, r1 and r2 chosen
    // from the significand m by the piecewise linear log2.
    var t = (ulong)BitConverter.DoubleToInt64Bits(ah);
    var e = (int)(t >> 52) - off;
    t &= ~0UL >> 12;
    double ed = e;
    var i = (int)(t >> 47);
    var d = (long)(t & ~0UL >> 17);
    var j = unchecked(t + ((ulong)_inverseHyperbolicB0[i] << 33) + (ulong)(_inverseHyperbolicB1[i] * (d >> 16))) >> 42;
    t |= 0x3ff0000000000000UL;
    var i1 = (int)(j >> 5);
    var i2 = (int)(j & 0x1f);
    var r = _inverseHyperbolicR1[i1] * _inverseHyperbolicR2[i2];
    var dx = Math.FusedMultiplyAdd(r, BitConverter.Int64BitsToDouble((long)t), -1);
    var dx2 = dx * dx;
    var f = dx2 * ((_inverseHyperbolicLogPolynomial[0] + dx * _inverseHyperbolicLogPolynomial[1])
      + dx2 * ((_inverseHyperbolicLogPolynomial[2] + dx * _inverseHyperbolicLogPolynomial[3]) + dx2 * _inverseHyperbolicLogPolynomial[4]));
    const double Log2High = 0.6931471805598903; // 0x1.62e42fefa38p-1
    const double Log2Low = 5.497923018708371e-14; // 0x1.ef35793c7673p-45
    var lh = Log2High * ed + (_inverseHyperbolicLog1[2 * i1 + 1] + _inverseHyperbolicLog2[2 * i2 + 1]);
    var ll = Log2Low * ed + _inverseHyperbolicLog1[2 * i1] + _inverseHyperbolicLog2[2 * i2] + al / ah + f;
    ll += dx;
    var sign = CopySign(1, x);
    lh *= sign;
    ll *= sign;
    const double Bound = 1.63e-19;
    var lower = lh + (ll - Bound);
    var upper = lh + (ll + Bound);
    if (lower == upper)
      return lower;

    if (ax < 0.25)
      return AsinhZero(x, squareHigh, squareLow);

    return AsinhRefine(x, ah, al, 1.4426950408889634 * Math.Abs(lower)); // 0x1.71547652b82fep+0, about 1/log(2)
  }

  /// <summary>
  /// The accurate asinh(x) near zero in double-double, <c>x + x * x^2 * p(x^2)</c>, where
  /// <c>(x2h, x2l)</c> is the square of <c>x</c>.
  /// </summary>
  private static double AsinhZero(double x, double x2h, double x2l) {
    var low = new double[1];
    var y2 = x2h * (_asinhZeroLow[0] + x2h * (_asinhZeroLow[1] + x2h * (_asinhZeroLow[2] + x2h * (_asinhZeroLow[3] + x2h * _asinhZeroLow[4]))));

    // Horner's rule in double-double, y2 a correction to the highest coefficient.
    var k = _asinhZeroHigh.Length / 2 - 1;
    var y1 = InverseHyperbolicFastTwoSum(_asinhZeroHigh[2 * k], y2, low);
    y2 = low[0] + _asinhZeroHigh[2 * k + 1];
    while (--k >= 0) {
      y1 = InverseHyperbolicMulDDAccurate(x2h, x2l, y1, y2, low);
      y2 = low[0];
      y1 = InverseHyperbolicFastTwoSum(_asinhZeroHigh[2 * k], y1, low);
      y2 += low[0] + _asinhZeroHigh[2 * k + 1];
    }

    y1 = InverseHyperbolicMulDDAccurate(y1, y2, x2h, x2l, low);
    y1 = InverseHyperbolicMulDDD(y1, low[0], x, low);
    y2 = low[0];
    var y0 = InverseHyperbolicFastTwoSum(x, y1, low);
    y1 = InverseHyperbolicFastTwoSum(low[0], y2, low);
    y1 = InverseHyperbolicOffPowerOfTwo(y1, low[0]);
    return y0 + y1;
  }

  /// <summary>
  /// The accurate asinh(x) as the logarithm of the double-double <c>(zh, zl)</c>, which approximates
  /// <c>|x| + sqrt(x^2 + 1)</c> (its half where <c>zl</c> is zero), and where <c>a</c> approximates the
  /// result divided by <c>log(2)</c>: the argument is reduced by four table factors, the logarithm of
  /// the rest evaluated in double-double and the sum carried in triple-double.
  /// </summary>
  private static double AsinhRefine(double x, double zh, double zl, double a) {
    var t = (ulong)BitConverter.DoubleToInt64Bits(zh);
    var e = (int)(t >> 52) - 0x3ff + (zl == 0.0 ? 1 : 0);
    t &= ~0UL >> 12;
    t |= 0x3ff0000000000000UL;
    var tf = BitConverter.Int64BitsToDouble((long)t);
    double ed = e;
    var v = (ulong)BitConverter.DoubleToInt64Bits(a - ed + 1.0000076293945312); // 0x1.00008p+0
    var i = (v - 0x3ff0000000000000UL) >> 36;
    var i1 = (int)((i >> 12) & 0x1f);
    var i2 = (int)((i >> 8) & 0xf);
    var i3 = (int)((i >> 4) & 0xf);
    var i4 = (int)(i & 0xf);
    const double L20 = 0.34657359027994517; // 0x1.62e42fefa38p-2
    const double L21 = 2.7489615093542512e-14; // 0x1.ef35793c768p-46
    const double L22 = -6.562349208892628e-28; // -0x1.9ff0342542fc3p-91
    var el2 = L22 * ed;
    var el1 = L21 * ed;
    var el0 = L20 * ed;
    var k1 = 3 * i1;
    var k2 = 3 * (17 + i2);
    var k3 = 3 * (34 + i3);
    var k4 = 3 * (51 + i4);
    var l0 = _inverseHyperbolicRefineLog[k1] + _inverseHyperbolicRefineLog[k2] + (_inverseHyperbolicRefineLog[k3] + _inverseHyperbolicRefineLog[k4]);
    var l1 = _inverseHyperbolicRefineLog[k1 + 1] + _inverseHyperbolicRefineLog[k2 + 1] + (_inverseHyperbolicRefineLog[k3 + 1] + _inverseHyperbolicRefineLog[k4 + 1]);
    var l2 = _inverseHyperbolicRefineLog[k1 + 2] + _inverseHyperbolicRefineLog[k2 + 2] + (_inverseHyperbolicRefineLog[k3 + 2] + _inverseHyperbolicRefineLog[k4 + 2]);
    l0 += el0;
    var t12 = _inverseHyperbolicRefineT1[i1] * _inverseHyperbolicRefineT2[i2];
    var t34 = _inverseHyperbolicRefineT3[i3] * _inverseHyperbolicRefineT4[i4];
    var th = t12 * t34;
    var tl = Math.FusedMultiplyAdd(t12, t34, -th);
    var dh = th * tf;
    var dl = Math.FusedMultiplyAdd(th, tf, -dh);
    var sh = tl * tf;
    var sl = Math.FusedMultiplyAdd(tl, tf, -sh);
    var low = new double[1];
    var xh = InverseHyperbolicFastTwoSum(dh - 1, dl, low);
    var xl = low[0];
    if (zl != 0.0) {
      // zl / 2^e
      var w = (ulong)BitConverter.DoubleToInt64Bits(zl);
      w = unchecked(w - (ulong)((long)e << 52));
      xl += th * BitConverter.Int64BitsToDouble((long)w);
    }

    xh = InverseHyperbolicAddDD(xh, xl, sh, sl, low);
    xl = low[0];
    sh = InverseHyperbolicRefineLog1P(xh, xl, low);
    sh = InverseHyperbolicAddDD(sh, low[0], el1, el2, low);
    sh = InverseHyperbolicAddDD(sh, low[0], l1, l2, low);
    sl = low[0];
    var v0 = InverseHyperbolicFastTwoSum(l0, sh, low);
    var v1 = InverseHyperbolicFastTwoSum(low[0], sl, low);
    var v2 = low[0];
    v0 = InverseHyperbolicFastTwoSum(v0, v1, low);
    v1 = InverseHyperbolicFastTwoSum(low[0], v2, low);
    v2 = low[0];
    var scale = CopySign(2, x);
    v0 *= scale;
    v1 *= scale;
    v2 *= scale;
    v1 = InverseHyperbolicOffPowerOfTwo(v1, v2);
    var b1 = (ulong)BitConverter.DoubleToInt64Bits(v1);
    var b0 = (ulong)BitConverter.DoubleToInt64Bits(v0);
    var er = (b1 + 41) & ~0UL >> 12;
    var de = ((b0 >> 52) & 0x7ff) - ((b1 >> 52) & 0x7ff);
    var res = v0 + v1;
    if (de > 99 || er < 80)
      return InverseHyperbolicDatabase(AsinhDatabaseFirst, AsinhDatabaseRows, x, res);

    return res;
  }

  /// <summary>
  /// The inverse hyperbolic sine of <c>float</c>, correctly rounded to nearest: an odd polynomial near
  /// zero, elsewhere the logarithm of <c>|x| + sqrt(x^2 + 1)</c> in double from a table of 128
  /// entries, evaluated again more accurately where the double lies too close to a rounding boundary.
  /// </summary>
  /// <param name="x">The argument.</param>
  /// <returns>The correctly rounded inverse hyperbolic sine; NaN for NaN and <c>x</c> for an infinity.</returns>
  public static float AsinhF(float x) {
    var t = (uint)BitConverter.SingleToInt32Bits(x) & 0x7fffffffu;
    double xs = x;
    if (t <= 0x3e815667u) {
      // |x| <= 0x1.02accep-2
      if (t <= 0x39ddb3d7u) {
        // |x| <= 0x1.bb67aep-12
        if (t == 0)
          return x;

        return MathF.FusedMultiplyAdd(x, -2.9802322387695312e-08f, x); // -0x1p-25f
      }

      var x2 = xs * xs;
      var x4 = x2 * x2;
      var x8 = x4 * x4;
      var f = x2 * (((_asinhFloatSmall[0] + x2 * _asinhFloatSmall[1]) + x4 * (_asinhFloatSmall[2] + x2 * _asinhFloatSmall[3]))
        + x8 * ((_asinhFloatSmall[4] + x2 * _asinhFloatSmall[5]) + x4 * (_asinhFloatSmall[6] + x2 * _asinhFloatSmall[7])));
      return (float)(xs - xs * f);
    }

    if (t >= 0x7f800000u)
      return x + x;

    var xd = Math.Abs(xs);
    return (float)CopySign(InverseHyperbolicFloatLogOf(xd + Math.Sqrt(xd * xd + 1)), xs);
  }

  /// <summary>
  /// <c>log(y)</c> of a double <c>y &gt; 1</c>, accurate enough to round to <c>float</c> correctly:
  /// <c>y = 2^e * (1 + m)</c> is reduced by the table entry nearest to <c>1 + m</c> and the rest
  /// evaluated by a polynomial; where the result lies near a rounding boundary of float, by a longer
  /// one with log(2) split in two.
  /// </summary>
  private static double InverseHyperbolicFloatLogOf(double y) {
    var tp = (ulong)BitConverter.DoubleToInt64Bits(y);
    var m = tp & ~0UL >> 12;
    var j = (int)((m + (1UL << 44)) >> 45);
    var e = (int)(tp >> 52) - 0x3ff;
    var w = BitConverter.Int64BitsToDouble((long)(m | 0x3ff0000000000000UL));
    var z = w * _inverseHyperbolicFloatInverse[j] - 1.0;
    var z2 = z * z;
    var r = ((_inverseHyperbolicFloatLog[128] * e + _inverseHyperbolicFloatLog[j]) + z * _inverseHyperbolicFloatLogPolynomial[0])
      + z2 * (_inverseHyperbolicFloatLogPolynomial[1] + z * _inverseHyperbolicFloatLogPolynomial[2]);
    var bits = (ulong)BitConverter.DoubleToInt64Bits(r);
    if (((bits + 259000) & 0xfffffffUL) >= 260000)
      return r;

    z2 = z * z;
    var c0 = _inverseHyperbolicFloatLogAccurate[0] + z * _inverseHyperbolicFloatLogAccurate[1];
    var c2 = _inverseHyperbolicFloatLogAccurate[2] + z * _inverseHyperbolicFloatLogAccurate[3];
    var c4 = _inverseHyperbolicFloatLogAccurate[4] + z * _inverseHyperbolicFloatLogAccurate[5];
    c0 += z2 * (c2 + z2 * c4);
    const double Ln2Low = 1.4286068203094173e-06; // 0x1.7f7d1cf79abcap-20
    const double Ln2High = 0.693145751953125; // 0x1.62e4p-1
    var lh = Ln2High * e;
    var ll = Ln2Low * e;
    r = (z * c0 + (ll + _inverseHyperbolicFloatLog[j])) + lh;
    if ((BitConverter.DoubleToInt64Bits(r) & 0xfffffffL) == 0) {
      // r lies exactly on a boundary: move it by what its rounding lost.
      var h = (z * c0 + (ll + _inverseHyperbolicFloatLog[j])) + (lh - r);
      r += 64 * h;
    }

    return r;
  }

}
