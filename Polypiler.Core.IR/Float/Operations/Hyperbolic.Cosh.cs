using System;

namespace Polypiler.Core.IR;

public static partial class FloatOperations {

  // cosh(x) = 1 + x^2 * P(x^2) for 0.125 > |x| >= 2^-26.
  private static readonly double[] _coshSmall = [
    0.5, 0.04166666666666666, 0.0013888888888944767,
    2.4801586239054082e-05, 2.756532858526234e-07
  ];

  // The coefficients of degree 10 to 16 of the accurate polynomial in doubles; those of degree 2 to
  // 8 in double-double are in _sinhDoubleDouble.
  private static readonly double[] _coshZeroLow = [2.755731922398589e-07, 2.087675698791894e-09, 1.1470745275937053e-11, 4.7805756776808083e-14];

  // The arguments whose double-double result still rounds the wrong way: {|x|, high, low} by |x|.
  private static readonly double[] _coshExceptions = [
    0.1001875279194748, 1.005022969789496, -6.162975822039155e-33,
    0.1653463259254159, 1.0137008756106034, 4.930380657631324e-32,
    0.2182674846488639, 1.0239150658903182, 6.162975822039155e-33,
    0.2726533410627696, 1.037400760741349, 4.930380657631324e-32,
    0.30972193314499713, 1.0483484876877136, -6.162975822039155e-33,
    0.5069751491863607, 1.131288143314319, -5.551115123125783e-17,
    0.8242424975584222, 1.3593599859505612, -6.162975822039155e-33,
    1.0636504029546148, 1.6210600367549, -1.5407439555097887e-33,
    1.08406746741621, 1.6474491841784524, 2.465190328815662e-32,
    1.9132766022536596, 3.461424127615681, -1.1102230246251565e-16,
    2.524171669189694, 6.280338804628224, 1.9721522630525295e-31,
    5.786625352653461, 162.95719950977306, -7.888609052210118e-31,
    10.843425258684126, 25598.21953169233, 8.077935669463161e-28,
    11.675069251598568, 58801.466327066475, -2.0194839173657902e-28,
    11.993967969642801, 80888.00229316112, 3.2311742677852644e-27,
    23.26497462840841, 6350678203.498573, -2.384185791015625e-07,
    60.06174039698168, 6.073687348575258e+25, 5.960464477539063e-08,
    89.11403663154066, 2.5159637154374956e+38, -1048576.0,
    170.40063412375622, 5.046904851182803e+73, 4.3556142965880123e+40,
    301.663664262519, 5.126663166814058e+130, 1.093625362391506e+99,
    470.2797657541442, 8.687148032216733e+203, -9.661343807543146e+170
  ];

  /// <summary>
  /// The hyperbolic cosine of a <c>double</c>, correctly rounded. Below <c>0.125</c> an even
  /// polynomial, above it <c>(e^|x| + e^-|x|) / 2</c> with <c>e^x = 2^(k/64) * 2^(j/4096) * e^r</c>;
  /// from about 36.7 the second exponential no longer matters, and beyond
  /// <c>0x1.633ce8fb9f87dp+9</c> the result overflows. When the fast result cannot be rounded safely,
  /// both exponentials are evaluated again in double-double arithmetic, and the few arguments that
  /// remain are looked up.
  /// </summary>
  /// <param name="x">The argument.</param>
  /// <returns><c>cosh(x)</c>.</returns>
  public static double Cosh(double x) {
    const double Scale = 5909.278887481194; // 4096 / ln 2
    var lo = new double[1];

    // v0 has k = round(|x| * 4096 / ln 2) in its integer bits, t is k.
    var ax = Math.Abs(x);
    var v0 = Math.FusedMultiplyAdd(ax, Scale, 100663296.5);
    var jt = (ulong)BitConverter.DoubleToInt64Bits(v0);
    var t = BitConverter.Int64BitsToDouble((long)(jt & 0xfffffffffc000000UL)) - 100663296.0;
    var aix = (ulong)BitConverter.DoubleToInt64Bits(ax);
    if (aix < 0x3fc0000000000000UL) {
      // |x| < 0.125
      if (aix < 0x3e50000000000000UL)
        return Math.FusedMultiplyAdd(ax, 2.7755575615628914e-17, 1.0);

      var x2 = x * x;
      var x4 = x2 * x2;
      var p = x2 * ((_coshSmall[0] + x2 * _coshSmall[1]) + x4 * ((_coshSmall[2] + x2 * _coshSmall[3]) + x4 * _coshSmall[4]));
      var e = x2 * 6.730727086790012e-16;
      var lb = 1 + (p - e);
      var ub = 1 + (p + e);
      if (lb == ub)
        return lb;

      return CoshZero(x);
    }

    if (aix > 0x408633ce8fb9f87dUL) {
      // |x| > 0x1.633ce8fb9f87dp+9: NaN, an infinity, or an overflow.
      if (aix > 0x7ff0000000000000UL)
        return x + x;

      if (aix == 0x7ff0000000000000UL)
        return ax;

      return 8.98846567431158e+307 * 2.0;
    }

    // 0.125 <= |x| <= 0x1.633ce8fb9f87dp+9: k = 4096 * ie + 64 * i0 + i1 and -k likewise.
    var il = (long)((jt << 14) >> 40);
    var jl = -il;
    var i1 = (int)(il & 0x3f);
    var i0 = (int)(il >> 6 & 0x3f);
    var ie = il >> 12;
    var j1 = (int)(jl & 0x3f);
    var j0 = (int)(jl >> 6 & 0x3f);
    var je = jl >> 12;
    var sp = BitConverter.Int64BitsToDouble((long)((ulong)(1022 + ie) << 52));
    var sm = BitConverter.Int64BitsToDouble((long)((ulong)(1022 + je) << 52));
    var t0h = _sinhExpCoarse[2 * i0 + 1];
    var t0l = _sinhExpCoarse[2 * i0];
    var t1h = _sinhExpFine[2 * i1 + 1];
    var t1l = _sinhExpFine[2 * i1];
    var th = t0h * t1h;
    var tl = t0h * t1l + t1h * t0l + Math.FusedMultiplyAdd(t0h, t1h, -th);
    const double Ln2High = 0.00016922538588914904;
    const double Ln2Low = 1.0256140314162804e-14;
    var dx = (ax - Ln2High * t) + Ln2Low * t;
    var dx2 = dx * dx;
    var mx = -dx;
    var pp = dx * ((_sinhExpFast[0] + dx * _sinhExpFast[1]) + dx2 * (_sinhExpFast[2] + dx * _sinhExpFast[3]));
    double rh;
    double rl;
    if (aix > 0x4014000000000000UL) {
      // |x| > 5
      if (aix > 0x40425e4f7b2737faUL) {
        // |x| > 0x1.25e4f7b2737fap+5: e^-|x| is below the last bit.
        sp = BitConverter.Int64BitsToDouble((1021 + ie) << 52);
        rh = th;
        rl = tl + th * pp;
        var e = 0.12e-18 * th;
        var lb = rh + (rl - e);
        var ub = rh + (rl + e);
        if (lb == ub)
          return lb * sp * 2;

        th = SinhExpAccurate(ax, t, th, tl, lo);
        tl = lo[0];
        th = SinhFastTwoSum(th, tl, lo);
        tl = lo[0];
        var tml = (long)(unchecked((ulong)BitConverter.DoubleToInt64Bits(tl) + 8) & 0xfffffffffffffUL);
        th += tl;
        th *= 2;
        th *= sp;
        if (tml <= 16)
          return CoshException(x, th);

        return th;
      }

      // 5 < |x| <= 0x1.25e4f7b2737fap+5: e^-|x| in doubles only.
      var q0h = _sinhExpCoarse[2 * j0 + 1];
      var q1h = _sinhExpFine[2 * j1 + 1];
      var qh = q0h * q1h;
      th *= sp;
      tl *= sp;
      qh *= sm;
      var pm = mx * ((_sinhExpFast[0] + mx * _sinhExpFast[1]) + dx2 * (_sinhExpFast[2] + mx * _sinhExpFast[3]));
      var em = qh + qh * pm;
      rh = th;
      rl = (tl + em) + th * pp;
      var e5 = 1.2202568396382264e-19 * rh;
      var lb5 = rh + (rl - e5);
      var ub5 = rh + (rl + e5);
      if (lb5 == ub5)
        return lb5;

      th = SinhExpAccurate(ax, t, th, tl, lo);
      tl = lo[0];
      if (aix > 0x403f666666666666UL) {
        // |x| > 0x1.f666666666666p+4
        rh = th + qh;
        rl = ((th - rh) + qh) + tl;
      } else {
        qh = q0h * q1h;
        var q0l = _sinhExpCoarse[2 * j0];
        var q1l = _sinhExpFine[2 * j1];
        var ql = q0h * q1l + q1h * q0l + Math.FusedMultiplyAdd(q0h, q1h, -qh);
        qh *= sm;
        ql *= sm;
        qh = SinhExpAccurate(-ax, -t, qh, ql, lo);
        ql = lo[0];
        rh = th + qh;
        rl = (((th - rh) + qh) + ql) + tl;
      }
    } else {
      // 0.125 <= |x| <= 5
      var q0h = _sinhExpCoarse[2 * j0 + 1];
      var q0l = _sinhExpCoarse[2 * j0];
      var q1h = _sinhExpFine[2 * j1 + 1];
      var q1l = _sinhExpFine[2 * j1];
      var qh = q0h * q1h;
      var ql = q0h * q1l + q1h * q0l + Math.FusedMultiplyAdd(q0h, q1h, -qh);
      th *= sp;
      tl *= sp;
      qh *= sm;
      ql *= sm;
      var pm = mx * ((_sinhExpFast[0] + mx * _sinhExpFast[1]) + dx2 * (_sinhExpFast[2] + mx * _sinhExpFast[3]));
      var fph = th;
      var fpl = tl + th * pp;
      var fmh = qh;
      var fml = ql + qh * pm;
      rh = fph + fmh;
      rl = ((fph - rh) + fmh) + fml + fpl;
      var e = 3.800001559619605e-19 * rh;
      var lb = rh + (rl - e);
      var ub = rh + (rl + e);
      if (lb == ub)
        return lb;

      th = SinhExpAccurate(ax, t, th, tl, lo);
      tl = lo[0];
      qh = SinhExpAccurate(-ax, -t, qh, ql, lo);
      ql = lo[0];
      rh = th + qh;
      rl = ((th - rh) + qh) + ql + tl;
    }

    rh = SinhFastTwoSum(rh, rl, lo);
    rl = lo[0];
    // The low part close to a multiple of 2^-52 of its binade means a halfway case or nearly one.
    var ml = (long)(unchecked((ulong)BitConverter.DoubleToInt64Bits(rl) + 8) & 0xfffffffffffffUL);
    rh += rl;
    if (ml <= 16)
      return CoshException(x, rh);

    return rh;
  }

  /// <summary>cosh(x) for 0.125 > |x| >= 2^-26 in double-double arithmetic.</summary>
  private static double CoshZero(double x) {
    var lo = new double[1];
    var x2 = x * x;
    var x2l = Math.FusedMultiplyAdd(x, x, -x2);
    lo[0] = x2 * (_coshZeroLow[0] + x2 * (_coshZeroLow[1] + x2 * (_coshZeroLow[2] + x2 * _coshZeroLow[3])));
    var y1 = SinhPolyDD(x2, x2l, CoshZeroPolynomial, 4, lo);
    var y2 = lo[0];
    y1 = SinhMulDD(y1, y2, x2, x2l, lo);
    y2 = lo[0];
    var y0 = SinhFastTwoSum(1.0, y1, lo);
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
    }

    if ((bits & 0xfffffffffffffL) == 0xfffffffffffffL)
      return CoshException(x, y0 + y1);

    return y0 + y1;
  }

  /// <summary>The result of an argument of <c>_coshExceptions</c>, otherwise <paramref name="f"/>.</summary>
  private static double CoshException(double x, double f) {
    var a = 0;
    var b = _coshExceptions.Length / 3 - 1;
    var m = (a + b) / 2;
    var ax = Math.Abs(x);
    while (a <= b) {
      if (_coshExceptions[3 * m] < ax)
        a = m + 1;
      else if (_coshExceptions[3 * m] == ax)
        return _coshExceptions[3 * m + 1] + _coshExceptions[3 * m + 2];
      else
        b = m - 1;

      m = (a + b) / 2;
    }

    return f;
  }

}
