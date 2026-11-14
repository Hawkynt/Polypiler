// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

public static partial class FloatOperations {

  // 2 * (e^x - 1) ~ 2x + x^2 + x^3/3 + x^4/12 for the reduced argument.
  private static readonly double[] _tanhExpFast = [2.0, 2.0, 1.3333333339299427, 0.6666666664683292];

  // tanh(x) = x + x^3 * P(x^2) for 0.25 > |x| > 0x1.d12ed0af1a27fp-27.
  private static readonly double[] _tanhSmall = [
    -0.3333333333333333, 0.13333333333332006, -0.053968253965448285, 0.021869488267393555,
    -0.008863221769815069, 0.003591725367929998, -0.001449063961717272, 0.0005287354884097332
  ];

  // The {high, low} coefficients of the double-double polynomials of TanhPolyDD, which starts at
  // one of the offsets below (in pairs): the accurate e^x - 1 of degree 0 to 2, and the degree 0 to 8
  // of the P of TanhZero.
  private const int TanhExpPolynomial = 0;
  private const int TanhZeroPolynomial = 3;

  private static readonly double[] _tanhDoubleDouble = [
    1.0, 7.180385960686095e-29, 0.5, -1.523885870582321e-28,
    0.16666666666666666, 9.213309146126262e-18,
    -0.3333333333333333, -1.8503717077079184e-17, 0.13333333333333333, 1.8503716978873156e-18,
    -0.05396825396825397, 2.555280216203743e-18, 0.021869488536155203, -1.7506565827004166e-19,
    -0.008863235529902196, -7.74871507811202e-19, 0.0035921280365724616, -3.90923225015485e-20,
    -0.0014558343870500075, -4.528021102697416e-20, 0.0005900274408832999, -5.0907881059533396e-20,
    -0.00023912911212249058, -1.1938491442160294e-20
  ];

  // Its coefficients of degree 9 to 13 in doubles.
  private static readonly double[] _tanhZeroLow = [
    9.691532755754035e-05, -3.927741240271311e-05, 1.590775186439605e-05,
    -6.360840639260085e-06, 2.1673633623487625e-06
  ];

  // The arguments whose double-double result still rounds the wrong way: {x, high, low} by x.
  private static readonly double[] _tanhExceptions = [
    0.2090839974007307, 0.2060895682174111, 6.938893903907228e-18,
    0.25045124584266104, 0.2453427932931634, -4.930380657631324e-32,
    0.2732616759729058, 0.2666572131426731, -2.465190328815662e-32,
    0.29014730604948313, 0.28227038751434835, 1.3877787807814457e-17,
    0.30366226016540027, 0.29466049539433053, -1.232595164407831e-32,
    0.31632581559201983, 0.3061809362935471, -2.7755575615628914e-17,
    0.461477120447514, 0.43128734008648434, 1.3877787807814457e-17,
    0.47467676321215935, 0.44197029676395017, -2.7755575615628914e-17,
    0.503783454716579, 0.4650874392328906, -4.930380657631324e-32,
    0.5312951816943817, 0.4863705116640336, -2.7755575615628914e-17,
    0.6013177353473791, 0.5379865745157587, 2.7755575615628914e-17,
    0.6444266668744435, 0.5679060815628252, -4.930380657631324e-32
  ];

  /// <summary>
  /// The hyperbolic tangent of a <c>double</c>, correctly rounded. Below <c>0.25</c> an odd
  /// polynomial, above it <c>1 - 2 * e^(-2|x|) / (1 + e^(-2|x|))</c> with
  /// <c>e^x = 2^(k/64) * 2^(j/4096) * e^r</c>, in double-double arithmetic below about 3.683 and in
  /// doubles above; from <c>0x1.30fc1931f09cap+4</c> the result rounds to <c>±1</c>. When the fast
  /// result cannot be rounded safely, the exponential is evaluated again in double-double arithmetic,
  /// and the few arguments that remain are looked up.
  /// </summary>
  /// <param name="x">The argument.</param>
  /// <returns><c>tanh(x)</c>.</returns>
  public static double Tanh(double x) {
    var lo = new double[1];
    var ax = Math.Abs(x);
    var aix = (ulong)BitConverter.DoubleToInt64Bits(ax);
    if (aix >= 0x40330fc1931f09caUL) {
      // |x| >= 0x1.30fc1931f09cap+4: NaN, or a result that rounds to ±1.
      if (aix > 0x7ff0000000000000UL)
        return x + x;

      var f = CopySign(1.0, x);
      if (aix == 0x7ff0000000000000UL)
        return f;

      return f - CopySign(2.7755575615628914e-17, x);
    }

    // v0 has k = round(-|x| * 8192 / ln 2) in its integer bits, t is k: e^(-2|x|) = 2^(k/4096) * e^r.
    const double Scale = -11818.557774962388;
    var v0 = Math.FusedMultiplyAdd(ax, Scale, 50331648.5);
    var jt = (ulong)BitConverter.DoubleToInt64Bits(v0);
    var t = BitConverter.Int64BitsToDouble((long)(jt & 0xfffffffff8000000UL)) - 50331648.0;
    var i1 = (int)(jt >> 27 & 0x3f);
    var i0 = (int)(jt >> 33 & 0x3f);
    var ie = (long)(jt << 13) >> 52;
    var sp = BitConverter.Int64BitsToDouble((long)((ulong)(1023 + ie) << 52));
    var t0h = _sinhExpCoarse[2 * i0 + 1];
    var t1h = _sinhExpFine[2 * i1 + 1];
    var th = t0h * t1h;
    double tl;
    if (aix < 0x400d76c8b4395810UL) {
      // |x| < 0x1.d76c8b4395810p+1
      if (aix < 0x3fd0000000000000UL) {
        // |x| < 0.25
        if (aix <= 0x3e4d12ed0af1a27fUL) {
          if (aix == 0)
            return x;

          return Math.FusedMultiplyAdd(x, -2.7755575615628914e-17, x);
        }

        var x2 = x * x;
        var x3 = x2 * x;
        var x4 = x2 * x2;
        var x8 = x4 * x4;
        var p1 = (_tanhSmall[4] + x2 * _tanhSmall[5]) + x4 * (_tanhSmall[6] + x2 * _tanhSmall[7]);
        var p0 = (_tanhSmall[0] + x2 * _tanhSmall[1]) + x4 * (_tanhSmall[2] + x2 * _tanhSmall[3]);
        p0 += x8 * p1;
        p0 *= x3;
        var zh = SinhFastTwoSum(x, p0, lo);
        var zl = lo[0];
        var ze = x3 * 3.885780586188048e-16;
        var zlb = zh + (zl - ze);
        var zub = zh + (zl + ze);
        if (zlb == zub)
          return zlb;

        return TanhZero(x);
      }

      var t0l = _sinhExpCoarse[2 * i0];
      var t1l = _sinhExpFine[2 * i1];
      tl = t0h * t1l + t1h * t0l + Math.FusedMultiplyAdd(t0h, t1h, -th);
      th *= sp;
      tl *= sp;
      const double Ln2High = -8.461269294457452e-05;
      const double Ln2Low = -5.128070157081402e-15;
      var dx = (Ln2High * t - ax) - Ln2Low * t;
      var dx2 = dx * dx;
      var p = dx * ((_tanhExpFast[0] + dx * _tanhExpFast[1]) + dx2 * (_tanhExpFast[2] + dx * _tanhExpFast[3]));
      var rh = th;
      var rl = tl + rh * p;
      rh = SinhFastTwoSum(rh, rl, lo);
      rl = lo[0];

      // rh + rl = e^(-2|x|); tanh |x| = 1 - 2 * (rh + rl) / (1 + rh + rl), the reciprocal in double-double.
      var ph = rh;
      var pl = rl;
      var qh = SinhFastTwoSum(1.0, rh, lo);
      var ql = rl + lo[0];
      var rqh = 1 / qh;
      var rql = (ql * rqh + Math.FusedMultiplyAdd(rqh, qh, -1)) * -rqh;
      ph = TanhMulDDAcc(ph, pl, rqh, rql, lo);
      pl = lo[0];
      var e = rh * 2.278518628114068e-19;
      rh = TanhFastTwoSub(0.5, ph, lo);
      rl = lo[0];
      rl -= pl;
      rh *= CopySign(2.0, x);
      rl *= CopySign(2.0, x);
      var lb = rh + (rl - e);
      var ub = rh + (rl + e);
      if (lb == ub)
        return lb;
    } else {
      // 0x1.d76c8b4395810p+1 <= |x| < 0x1.30fc1931f09cap+4
      const double Ln2 = -8.461269293944645e-05;
      var dx = Math.FusedMultiplyAdd(Ln2, t, -ax);
      var dx2 = dx * dx;
      var p = dx * ((_tanhExpFast[0] + dx * _tanhExpFast[1]) + dx2 * (_tanhExpFast[2] + dx * _tanhExpFast[3]));
      var rh = th * sp;
      rh += (p + 2 * 3.2959746043559335e-17 * ax) * rh;
      var e = rh * 1.887379141862766e-15;
      rh = 2 * rh / (1 + rh);
      var one = CopySign(1.0, x);
      rh = CopySign(rh, x);
      var lb = one - (rh + e);
      var ub = one - (rh - e);
      if (lb == ub)
        return lb;

      var t0l = _sinhExpCoarse[2 * i0];
      var t1l = _sinhExpFine[2 * i1];
      tl = t0h * t1l + t1h * t0l + Math.FusedMultiplyAdd(t0h, t1h, -th);
      th *= sp;
      tl *= sp;
    }

    // The accurate path: e^(-2|x|) = sh + sl, then 1 - 2 * (sh + sl) / (1 + sh + sl).
    var sh = TanhExpAccurate(-2 * ax, t, th, tl, lo);
    var sl = lo[0];
    var dh = SinhFastTwoSum(1.0, sh, lo);
    var dl = sl + lo[0];
    dh = SinhFastTwoSum(dh, dl, lo);
    dl = lo[0];
    var rdh = 1 / dh;
    var rdl = (dl * rdh + Math.FusedMultiplyAdd(rdh, dh, -1)) * -rdh;
    var nh = TanhMulDDAcc(sh, sl, rdh, rdl, lo);
    var nl = lo[0];
    var resh = TanhFastTwoSub(0.5, nh, lo);
    var resl = lo[0] - nl;
    resh = SinhFastTwoSum(resh, resl, lo);
    resl = lo[0];
    var res = CopySign(2.0, x) * resh + CopySign(2.0, x) * resl;
    // The low part close to a multiple of 2^-52 of its binade means a halfway case or nearly one.
    if ((unchecked((ulong)BitConverter.DoubleToInt64Bits(resl) + 32) & 0xfffffffffffffUL) < 65)
      return TanhException(x, res);

    return res;
  }

  /// <summary>tanh(x) for 0.25 > |x| > 0x1.d12ed0af1a27fp-27 in double-double arithmetic.</summary>
  private static double TanhZero(double x) {
    var lo = new double[1];
    var x2 = x * x;
    var x2l = Math.FusedMultiplyAdd(x, x, -x2);
    lo[0] = x2 * (_tanhZeroLow[0] + x2 * (_tanhZeroLow[1] + x2 * (_tanhZeroLow[2] + x2 * (_tanhZeroLow[3] + x2 * _tanhZeroLow[4]))));
    var y1 = TanhPolyDD(x2, x2l, TanhZeroPolynomial, 9, lo);
    var y2 = lo[0];
    y1 = TanhMulDDDAcc(y1, y2, x, lo);
    y2 = lo[0];
    y1 = TanhMulDDAcc(y1, y2, x2, x2l, lo);
    y2 = lo[0];
    var y0 = SinhFastTwoSum(x, y1, lo);
    y1 = lo[0];
    y1 = SinhFastTwoSum(y1, y2, lo);
    y2 = lo[0];
    // A middle part with an all-zero significand hides a halfway case: move it towards y2.
    var bits = BitConverter.DoubleToInt64Bits(y1);
    if ((bits & 0xfffffffffffffL) == 0) {
      if ((BitConverter.DoubleToInt64Bits(y2) ^ bits) < 0)
        bits--;
      else
        bits++;

      y1 = BitConverter.Int64BitsToDouble(bits);
      if (Math.Abs(x) == 0.2090839974007307)
        return TanhException(x, y0 + y1);
    }

    return y0 + y1;
  }

  /// <summary>The result of an argument of <c>_tanhExceptions</c>, otherwise <paramref name="f"/>.</summary>
  private static double TanhException(double x, double f) {
    var a = 0;
    var b = _tanhExceptions.Length / 3 - 1;
    var m = (a + b) / 2;
    var ax = Math.Abs(x);
    while (a <= b) {
      if (_tanhExceptions[3 * m] < ax)
        a = m + 1;
      else if (_tanhExceptions[3 * m] == ax)
        return CopySign(1.0, x) * _tanhExceptions[3 * m + 1] + CopySign(1.0, x) * _tanhExceptions[3 * m + 2];
      else
        b = m - 1;

      m = (a + b) / 2;
    }

    return f;
  }

  /// <summary>
  /// <c>e^x</c> as a double-double, high part returned and low part in <paramref name="lo"/>, from
  /// <c>2^(t/4096) = th + tl</c> and the remainder <c>x - t * ln 2 / 4096</c>.
  /// </summary>
  private static double TanhExpAccurate(double x, double t, double th, double tl, double[] lo) {
    const double Ln2High = 0.00016922538588914904;
    const double Ln2Low = 1.0256140314162804e-14;
    const double Ln2LowLow = 3.2042720746546034e-31;
    var dx = x - Ln2High * t;
    var dxl = Ln2Low * t;
    var dxll = Ln2LowLow * t + Math.FusedMultiplyAdd(Ln2Low, t, -dxl);
    var dxh = dx + dxl;
    dxl = ((dx - dxh) + dxl) + dxll;
    lo[0] = dxh * (0.041666666666666664 + dxh * (0.008333333338507347 + dxh * 0.0013888888882434365));
    var fh = TanhPolyDD(dxh, dxl, TanhExpPolynomial, 3, lo);
    fh = TanhMulDDAcc(dxh, dxl, fh, lo[0], lo);
    fh = TanhMulDDAcc(th, tl, fh, lo[0], lo);
    var fl = lo[0];
    var zh = th + fh;
    var zl = (th - zh) + fh;
    var uh = zh + tl;
    var ul = ((zh - uh) + tl) + zl;
    var vh = uh + fl;
    lo[0] = ((uh - vh) + fl) + ul;
    return vh;
  }

  /// <summary>
  /// The double-double polynomial of the <paramref name="n"/> coefficients of <c>_tanhDoubleDouble</c>
  /// from pair <paramref name="first"/> at <c>xh + xl</c> by Horner's rule with renormalized products;
  /// <paramref name="lo"/> brings the sum of the higher terms in doubles and takes the low part of the
  /// result.
  /// </summary>
  private static double TanhPolyDD(double xh, double xl, int first, int n, double[] lo) {
    var i = n - 1;
    var ch = SinhFastTwoSum(_tanhDoubleDouble[2 * (first + i)], lo[0], lo);
    var cl = lo[0] + _tanhDoubleDouble[2 * (first + i) + 1];
    while (--i >= 0) {
      ch = TanhMulDDAcc(xh, xl, ch, cl, lo);
      cl = lo[0];
      ch = SinhFastTwoSum(_tanhDoubleDouble[2 * (first + i)], ch, lo);
      cl = (cl + _tanhDoubleDouble[2 * (first + i) + 1]) + lo[0];
    }

    lo[0] = cl;
    return ch;
  }

  /// <summary><c>(xh + xl) * (ch + cl)</c> renormalized, high part returned and low part in <paramref name="lo"/>.</summary>
  private static double TanhMulDDAcc(double xh, double xl, double ch, double cl, double[] lo) {
    var plh = xl * ch;
    var phl = xh * cl;
    var phh = xh * ch;
    var rest = Math.FusedMultiplyAdd(xh, ch, -phh);
    rest += phl + plh;
    return SinhFastTwoSum(phh, rest, lo);
  }

  /// <summary><c>(xh + xl) * c</c> renormalized, high part returned and low part in <paramref name="lo"/>.</summary>
  private static double TanhMulDDDAcc(double xh, double xl, double c, double[] lo) {
    var pl = xl * c;
    var ph = xh * c;
    var rest = Math.FusedMultiplyAdd(xh, c, -ph);
    rest += pl;
    return SinhFastTwoSum(ph, rest, lo);
  }

  /// <summary><c>x - y</c> with its rounding error in <paramref name="lo"/>, for <c>|x| &gt;= |y|</c>.</summary>
  private static double TanhFastTwoSub(double x, double y, double[] lo) {
    var s = x - y;
    lo[0] = (x - s) - y;
    return s;
  }

}
