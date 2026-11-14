// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The inverse hyperbolic cosine, correctly rounded, converted from CORE-MATH's <c>acosh.c</c> and
/// <c>acoshf.c</c> (MIT); see <c>THIRD-PARTY-NOTICE.CORE-MATH.InverseHyperbolic.txt</c>.
/// </summary>
public static partial class FloatOperations {

  // (acosh(1 + z)/sqrt(2z) - 1)/z near one: the coefficients of z^0 to z^8.
  private static readonly double[] _acoshNearOne = [
    -0.08333333333333333, 0.018749999999996787, -0.0055803571425102165, 0.0018988715087326544,
    -0.0006991293701336483, 0.00027112534949498546, -0.00010896132591969038, 4.410690395968596e-05,
    -1.4840272705632399e-05
  ];

  // log((1 + sqrt(1 - z))/2) as a polynomial of z = 1/x^2 on [111.75, 738), [738, 32896) and [32896, 0x1.ap+31).
  private static readonly double[] _acoshModerate = [
    1.8438554824029686e-20, -0.2500000000000051, -0.09374999970827397, -0.052088933292329824
  ];

  private static readonly double[] _acoshLarge = [
    -1.0150310783512001e-20, -0.24999999999990108, -0.09375014351472541
  ];

  private static readonly double[] _acoshHuge = [
    1.0007109452477697e-20, -0.25000000008663337
  ];

  // The accurate (acosh(1 + z)/sqrt(2z) - 1)/z: double-double coefficients of z^0 to z^9, then doubles of z^10 to z^15.
  private static readonly double[] _acoshOneHigh = [
    -0.08333333333333333, -4.625929269269436e-18, 0.01875, 6.938893887350383e-19,
    -0.005580357142857143, 1.239093492806872e-19, 0.0018988715277777778, 2.4004217442971665e-20,
    -0.0006991299715909091, 3.867929897043247e-20, 0.0002711369441105763, 2.4059605609238967e-20,
    -0.00010910034179684623, 6.538737541041623e-21, 4.5124222249574996e-05, -1.885407160762854e-21,
    -1.9065643587602643e-05, 2.144112338226363e-22, 8.193686867890108e-06, 1.6342510450995029e-24
  ];

  private static readonly double[] _acoshOneLow = [
    -3.570563089012387e-06, 1.5739619710158544e-06, -7.002006354495226e-07, 3.1186638489529637e-07,
    -1.3222134018555206e-07, 4.1117419046327436e-08
  ];

  // (acosh(1 + z)/sqrt(2z) - 1)/z of float: the coefficients of z^0 to z^7.
  private static readonly double[] _acoshFloatNearOne = [
    -0.08333333333328993, 0.018749999994215116, -0.0055803568456194285, 0.0018988638601201038,
    -0.0006990184157670764, 0.00027017646829586764, -0.00010420707647055027, 3.116411306139845e-05
  ];

  /// <summary>
  /// The inverse hyperbolic cosine <c>log(x + sqrt(x^2 - 1))</c>, correctly rounded to nearest. Near
  /// one <c>sqrt(2z)</c> times a polynomial of <c>z = x - 1</c>; elsewhere the logarithm of
  /// <c>x + sqrt(x^2 - 1)</c> from tables, with a correction polynomial of <c>1/x^2</c> for large
  /// <c>x</c>; and where the error bound does not decide the rounding, an accurate evaluation in
  /// triple-double.
  /// </summary>
  /// <param name="x">The argument.</param>
  /// <returns>The correctly rounded inverse hyperbolic cosine; 0 for 1, NaN below 1 and for NaN, and +infinity for +infinity.</returns>
  public static double Acosh(double x) {
    var ix = (ulong)BitConverter.DoubleToInt64Bits(x);
    if (ix >= 0x7ff0000000000000UL) {
      // x < 0, an infinity or a NaN
      var aix = ix << 1;
      if (ix == 0x7ff0000000000000UL || aix > 0x7ffUL << 53)
        return x + x;

      return double.NaN;
    }

    if ((long)ix <= 0x3ff0000000000000L) {
      // 0 <= x <= 1
      if (ix == 0x3ff0000000000000UL)
        return 0;

      return double.NaN;
    }

    double g;
    double eps;
    var off = 0x3fe;
    var t = ix;
    var low = new double[1];
    if (ix < 0x3ff1e83e425aee63UL) {
      // 1 < x < 0x1.1e83e425aee63p+0: sqrt(2z) in double-double times a polynomial of z
      var z = x - 1;
      var iz = -0.25 / z;
      var zt = 2 * z;
      var sh = Math.Sqrt(zt);
      var sl = Math.FusedMultiplyAdd(sh, sh, -zt) * (sh * iz);
      var z2 = z * z;
      var z4 = z2 * z2;
      var p = _acoshNearOne[0] + z * (((_acoshNearOne[1] + z * _acoshNearOne[2]) + z2 * (_acoshNearOne[3] + z * _acoshNearOne[4]))
        + z4 * ((_acoshNearOne[5] + z * _acoshNearOne[6]) + z2 * (_acoshNearOne[7] + z * _acoshNearOne[8])));
      var ds = Math.FusedMultiplyAdd(sh * z, p, sl);
      eps = ds * 8.881784197001252e-16 - 4.930380657631324e-32 * sh; // 0x1.00p-50, 0x1p-104
      var lb = sh + (ds - eps);
      var ub = sh + (ds + eps);
      if (lb == ub)
        return lb;

      return AcoshOne(z, sh, sl);
    } else if (ix < 0x405bf00000000000UL) {
      // x < 111.75: the logarithm of the double-double x + sqrt(x^2 - 1)
      off = 0x3ff;
      var x2h = x * x;
      var wh = x2h - 1;
      var wl = Math.FusedMultiplyAdd(x, x, -x2h);
      var sh = Math.Sqrt(wh);
      var ish = 0.5 / wh;
      var sl = (wl - Math.FusedMultiplyAdd(sh, sh, -wh)) * (sh * ish);
      var th = InverseHyperbolicFastTwoSum(x, sh, low);
      var tl = low[0] + sl;
      t = (ulong)BitConverter.DoubleToInt64Bits(th);
      g = tl / th;
      eps = 1.6305384234645282e-19; // 0x1.81p-63
    } else if (ix < 0x4087100000000000UL) {
      // x < 738: the logarithm of 2x, corrected
      var z = 1 / (x * x);
      g = _acoshModerate[0] + z * (_acoshModerate[1] + z * (_acoshModerate[2] + z * _acoshModerate[3]));
      eps = 1.9100592960584473e-19; // 0x1.c3p-63
    } else if (ix < 0x40e0100000000000UL) {
      // x < 32896
      var z = 1 / (x * x);
      g = _acoshLarge[0] + z * (_acoshLarge[1] + z * _acoshLarge[2]);
      eps = 1.7364175418713157e-19; // 0x1.9ap-63
    } else if (ix < 0x41ea000000000000UL) {
      // x < 0x1.ap+31
      var z = 1 / (x * x);
      g = _acoshHuge[0] + z * _acoshHuge[1];
      eps = 1.7321823771350442e-19; // 0x1.99p-63
    } else {
      g = 0;
      eps = 1.8380614955418317e-19; // 0x1.b2p-63
    }

    // log(t) = e*log(2) + log(1/(r1*r2)) + log(1 + dx), as for asinh.
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
    var lh = (_inverseHyperbolicLog1[2 * i1 + 1] + _inverseHyperbolicLog2[2 * i2 + 1]) + Log2High * ed;
    var t1 = (Log2Low * ed) + (_inverseHyperbolicLog1[2 * i1] + _inverseHyperbolicLog2[2 * i2]);
    var t2 = f + t1;
    var t3 = g + t2;
    var ll = dx + t3;
    var lower = lh + (ll - eps);
    var upper = lh + (ll + eps);
    if (lower == upper)
      return lower;

    return AcoshRefine(x, 1.4426950408889634 * lower); // 0x1.71547652b82fep+0, about 1/log(2)
  }

  /// <summary>
  /// The accurate acosh(1 + z) near one in double-double, <c>(sh, sl) * (1 + z * p(z))</c>, where
  /// <c>(sh, sl)</c> is <c>sqrt(2z)</c>.
  /// </summary>
  private static double AcoshOne(double z, double sh, double sl) {
    var low = new double[1];
    var y2 = z * (_acoshOneLow[0] + z * (_acoshOneLow[1] + z * (_acoshOneLow[2] + z * (_acoshOneLow[3] + z * (_acoshOneLow[4] + z * _acoshOneLow[5])))));

    // Horner's rule in double-double at (z, 0), y2 a correction to the highest coefficient.
    var k = _acoshOneHigh.Length / 2 - 1;
    var y1 = InverseHyperbolicFastTwoSum(_acoshOneHigh[2 * k], y2, low);
    y2 = low[0] + _acoshOneHigh[2 * k + 1];
    while (--k >= 0) {
      y1 = InverseHyperbolicMulDDAccurate(z, 0, y1, y2, low);
      y2 = low[0];
      y1 = InverseHyperbolicFastTwoSum(_acoshOneHigh[2 * k], y1, low);
      y2 += low[0] + _acoshOneHigh[2 * k + 1];
    }

    y1 = InverseHyperbolicMulDDD(y1, y2, z, low);
    y2 = low[0];
    var y0 = InverseHyperbolicFastTwoSum(1, y1, low);
    y1 = low[0] + y2;
    y0 = InverseHyperbolicMulDDAccurate(y0, y1, sh, sl, low);
    return y0 + low[0];
  }

  /// <summary>
  /// The accurate acosh(x) as the logarithm of <c>x + sqrt(x^2 - 1)</c> in double-double, where
  /// <c>a</c> approximates the result divided by <c>log(2)</c>: the argument is reduced by four table
  /// factors, the logarithm of the rest evaluated in double-double and the sum carried in triple-double.
  /// </summary>
  private static double AcoshRefine(double x, double a) {
    var low = new double[1];
    var ix = (ulong)BitConverter.DoubleToInt64Bits(x);
    double zh;
    double zl;
    var huge = 0;
    if (ix < 0x4190000000000000UL) {
      // x < 2^26
      var x2h = x * x;
      var x2l = Math.FusedMultiplyAdd(x, x, -x2h);
      var wh = InverseHyperbolicFastTwoSum(x2h - 1, x2l, low);
      var wl = low[0];
      var sh = Math.Sqrt(wh);
      var sl = (wl - Math.FusedMultiplyAdd(sh, sh, -wh)) / (2.0 * sh);
      zh = InverseHyperbolicFastTwoSum(x, sh, low);
      zl = low[0] + sl;
      zh = InverseHyperbolicFastTwoSum(zh, zl, low);
      zl = low[0];
    } else if (ix < 0x4330000000000000UL) {
      // x < 2^52
      zh = 2 * x;
      zl = -0.5 / x;
    } else {
      // (x + sqrt(x^2 - 1))/2, with 1 added to the exponent below: 2x could overflow.
      zh = x;
      zl = 0;
      huge = 1;
    }

    var t = (ulong)BitConverter.DoubleToInt64Bits(zh);
    var e = (int)(t >> 52) - 0x3ff + huge;
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
    var rh = tl * tf;
    var rl = Math.FusedMultiplyAdd(tl, tf, -rh);
    var xh = InverseHyperbolicFastTwoSum(dh - 1, dl, low);
    var xl = low[0];
    if (zl != 0.0) {
      // zl / 2^e
      var w = (ulong)BitConverter.DoubleToInt64Bits(zl);
      w = unchecked(w - (ulong)((long)e << 52));
      xl += th * BitConverter.Int64BitsToDouble((long)w);
    }

    xh = InverseHyperbolicAddDD(xh, xl, rh, rl, low);
    xl = low[0];
    rh = InverseHyperbolicRefineLog1P(xh, xl, low);
    rh = InverseHyperbolicAddDD(rh, low[0], el1, el2, low);
    rh = InverseHyperbolicAddDD(rh, low[0], l1, l2, low);
    rl = low[0];
    var v0 = InverseHyperbolicFastTwoSum(l0, rh, low);
    var v1 = InverseHyperbolicFastTwoSum(low[0], rl, low);
    var v2 = low[0];
    v0 *= 2;
    v1 *= 2;
    v2 *= 2;
    v1 = InverseHyperbolicOffPowerOfTwo(v1, v2);
    var b1 = (ulong)BitConverter.DoubleToInt64Bits(v1);
    var b0 = (ulong)BitConverter.DoubleToInt64Bits(v0);
    var er = (b1 + 7) & ~0UL >> 12;
    var de = ((b0 >> 52) & 0x7ff) - ((b1 >> 52) & 0x7ff);
    var res = v0 + v1;
    if (de > 102 || er < 15)
      return InverseHyperbolicDatabase(AcoshDatabaseFirst, AcoshDatabaseRows, x, res);

    return res;
  }

  /// <summary>
  /// The inverse hyperbolic cosine of <c>float</c>, correctly rounded to nearest: near one
  /// <c>sqrt(2z)</c> times a polynomial of <c>z = x - 1</c>, elsewhere the logarithm of
  /// <c>x + sqrt(x^2 - 1)</c> in double, as for <see cref="AsinhF"/>.
  /// </summary>
  /// <param name="x">The argument.</param>
  /// <returns>The correctly rounded inverse hyperbolic cosine; 0 for 1, NaN below 1 and for NaN, and +infinity for +infinity.</returns>
  public static float AcoshF(float x) {
    var t = (uint)BitConverter.SingleToInt32Bits(x);
    if (t <= 0x3f800000u)
      return AcoshSpecialF(x);

    if (t <= 0x3f99db23u) {
      // x <= 0x1.33b646p+0
      var zf = x - 1.0f;
      double z = zf;
      var a = Math.Sqrt(2.0 * z);
      var z2 = z * z;
      var z4 = z2 * z2;
      var f = ((_acoshFloatNearOne[0] + z * _acoshFloatNearOne[1]) + z2 * (_acoshFloatNearOne[2] + z * _acoshFloatNearOne[3]))
        + z4 * ((_acoshFloatNearOne[4] + z * _acoshFloatNearOne[5]) + z2 * (_acoshFloatNearOne[6] + z * _acoshFloatNearOne[7]));
      return (float)(a + (a * z) * f);
    }

    if (t < 0x7f800000u) {
      double xd = x;
      return (float)InverseHyperbolicFloatLogOf(xd + Math.Sqrt(xd * xd - 1));
    }

    return AcoshSpecialF(x);
  }

  /// <summary>acosh of a <c>float</c> outside (1, +infinity): 0 for 1, the argument for +infinity and NaN, NaN for the rest.</summary>
  private static float AcoshSpecialF(float x) {
    var t = (uint)BitConverter.SingleToInt32Bits(x);
    if (t == 0x3f800000u)
      return 0.0f;

    if (t << 1 > 0xff000000u)
      return x + x;

    if (t == 0x7f800000u)
      return x;

    return float.NaN;
  }

}
