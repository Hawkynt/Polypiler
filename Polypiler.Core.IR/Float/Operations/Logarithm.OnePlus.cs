using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The logarithm of one plus the operand, converted from CORE-MATH's <c>log1p.c</c> and
/// <c>log1pf.c</c> (MIT), correctly rounded in every case. See
/// <c>THIRD-PARTY-NOTICE.CORE-MATH.Logarithm.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>ln(1 + x)</c>, correctly rounded: a polynomial in x for |x| &lt; 1/16, otherwise a table of 64
  /// reciprocals and a polynomial of degree 7 in double-double; when that cannot decide the rounding,
  /// a triple-double refinement.
  /// </summary>
  /// <param name="x">The number.</param>
  /// <returns><c>ln(1 + x)</c>; negative infinity for -1, NaN below -1 and for a NaN.</returns>
  public static double Log1P(double x) {
    var ix = (ulong)BitConverter.DoubleToInt64Bits(x);
    var ax = ix << 1;
    double ln1;
    double ln0;
    double eps;
    var low = new double[1];
    if (ax < 0x7f60000000000000UL) {
      // |x| < 2^-53: x - x^2 / 2 rounds as x - |x| 2^-54 does.
      if (ax < 0x7940000000000000UL)
        return ax == 0 ? x : Math.FusedMultiplyAdd(Math.Abs(x), -5.551115123125783E-17, x); // -0x1p-54

      var x2 = x * x;
      if (ax < 0x7e60000000000000UL) {
        // |x| < 2^-12
        ln1 = x;
        eps = 8.809142651444724E-20 * x; // 0x1.ap-64
        if (ax < 0x7d43360000000000UL)
          ln0 = x2 * (_log1PSmall[0] + x * _log1PSmall[1]);
        else
          ln0 = x2 * ((_log1PNear[0] + x * _log1PNear[1]) + x2 * (_log1PNear[2] + x * _log1PNear[3]));
      } else {
        // |x| < 1/16
        var x3 = x2 * x;
        var x4 = x2 * x2;
        var hx = -0.5 * x;
        ln1 = Math.FusedMultiplyAdd(hx, x, x);
        ln0 = Math.FusedMultiplyAdd(hx, x, x - ln1);
        var f = ((_log1PMedium[0] + x * _log1PMedium[1]) + x2 * (_log1PMedium[2] + x * _log1PMedium[3]))
          + x4 * (((_log1PMedium[4] + x * _log1PMedium[5]) + x2 * (_log1PMedium[6] + x * _log1PMedium[7]))
            + x4 * ((_log1PMedium[8] + x * _log1PMedium[9]) + x2 * (_log1PMedium[10] + x * _log1PMedium[11])));
        ln0 += x3 * f;
        eps = x3 * 3.7990444123892075E-16; // 0x1.b6p-52
      }
    } else {
      // 1 + x as the double-double t + dt.
      double t;
      double dt;
      if ((long)ix < 0x4340000000000000L && ix < 0xbff0000000000000UL) {
        t = LogFastTwoSum(1.0, x, low);
        dt = low[0];
      } else if (ix < 0x4690000000000000UL) {
        t = x;
        dt = 1;
      } else if (ix < 0x7ff0000000000000UL) {
        t = x;
        dt = 0;
      } else {
        if (ax > 0xffe0000000000000UL)
          return x + x;

        if (ix == 0x7ff0000000000000UL)
          return x;

        return ix == 0xbff0000000000000UL ? double.NegativeInfinity : double.NaN;
      }

      var j = BitConverter.DoubleToInt64Bits(t) - 0x3fe6a00000000000L;
      var j1 = (int)((j >> (52 - 6)) & 0x3f);
      var je = j >> 52;
      var rs = BitConverter.DoubleToInt64Bits(_log1PReciprocal[j1]);
      if (je < 1022) {
        rs -= je << 52;
      } else {
        // Scale t instead of the reciprocal, which would be subnormal.
        rs -= 1021L << 52;
        t *= _log1PScale[(int)(je - 1022)];
        dt *= _log1PScale[(int)(je - 1022)];
      }

      var rsf = BitConverter.Int64BitsToDouble(rs);
      var dh = rsf * t;
      var dl = Math.FusedMultiplyAdd(rsf, t, -dh) + rsf * dt;
      var xh = LogFastTwoSum(dh - 1.0, dl, low);
      var xl = low[0];
      var x2 = xh * xh;
      xl += x2 * ((_log1PPolynomial[0] + xh * _log1PPolynomial[1]) + x2 * ((_log1PPolynomial[2] + xh * _log1PPolynomial[3])
        + x2 * (_log1PPolynomial[4] + xh * _log1PPolynomial[5])));
      var l1 = 0.6931471805601177 * je; // 0x1.62e42fefa4p-1
      var l0 = -1.7239444525614835E-13 * je; // -0x1.8432a1b0e2634p-43
      ln1 = _log1PReciprocalLog[2 * j1 + 1] + l1;
      ln0 = _log1PReciprocalLog[2 * j1] + l0;
      ln1 = Log1PFastSum(ln1, ln0, xh, xl, low);
      ln0 = low[0];
      eps = 4.404571325722362E-20; // 0x1.ap-65
    }

    var lb = ln1 + (ln0 - eps);
    var ub = ln1 + (ln0 + eps);
    return lb != ub ? Log1PRefine(x, lb) : lb;
  }

  /// <summary>
  /// <c>ln(1 + x)</c> of a <c>float</c>, correctly rounded: a polynomial of degree 8 for
  /// |x| &lt; 0x1.1p-6, otherwise a table of 32 reciprocals and a polynomial of degree 4, refined in
  /// double-double when that cannot decide the rounding.
  /// </summary>
  /// <param name="x">The number.</param>
  /// <returns><c>ln(1 + x)</c>; negative infinity for -1, NaN below -1 and for a NaN.</returns>
  public static float Log1PF(float x) {
    double z = x;
    var ux = (uint)BitConverter.SingleToInt32Bits(x);
    if (ux >= 0xbf800000u)
      return Log1PSpecialF(x);

    var ax = ux & (~0u >> 1);
    if (ax >= 0x7f800000u)
      return Log1PSpecialF(x);

    if (ax < 0x3c880000u) {
      // |x| < 2^-25: x - x^2 rounds as x - x^2 / 2 does.
      if (ax < 0x33000000u)
        return ax == 0 ? x : MathF.FusedMultiplyAdd(x, -x, x);

      var z2 = z * z;
      var z4 = z2 * z2;
      var f = z2 * ((_log1PPolynomialF[1] + z * _log1PPolynomialF[2]) + z2 * (_log1PPolynomialF[3] + z * _log1PPolynomialF[4])
        + z4 * (_log1PPolynomialF[5] + z * (_log1PPolynomialF[6] + z * _log1PPolynomialF[7])));
      var r = z + f;
      // A result whose last 28 bits are zero may be a halfway case: add the error of the sum.
      if ((BitConverter.DoubleToInt64Bits(r) & 0xfffffffL) == 0)
        r += 16384.0 * (f + (z - r)); // 0x1p14

      return (float)r;
    } else {
      var tp = (ulong)BitConverter.DoubleToInt64Bits(z + 1.0);
      var e = (int)(tp >> 52) - 0x3ff;
      var m52 = tp & (~0UL >> 12);
      var j = (int)((tp >> (52 - 5)) & 31);
      var xd = BitConverter.Int64BitsToDouble((long)(m52 | 0x3ffUL << 52));
      z = xd * _log1PReciprocalF[j] - 1;
      const double Ln2 = 0.6931471805599453; // 0x1.62e42fefa39efp-1
      var z2 = z * z;
      var r = (Ln2 * e + _log1PReciprocalLogF[j]) + z * ((_log1PFastF[0] + z * _log1PFastF[1]) + z2 * (_log1PFastF[2] + z * _log1PFastF[3]));
      const double Epsilon = 2.1555E-11;
      var ub = (float)r;
      var lb = (float)(r - Epsilon);
      if (ub != lb) {
        var z4 = z2 * z2;
        var f = z2 * ((_log1PPolynomialF[1] + z * _log1PPolynomialF[2]) + z2 * (_log1PPolynomialF[3] + z * _log1PPolynomialF[4])
          + z4 * (_log1PPolynomialF[5] + z * (_log1PPolynomialF[6] + z * _log1PPolynomialF[7])));
        // Subtract the offset of the table, which keeps the first approximation above the result.
        var lj = _log1PReciprocalLogF[j] - 1.063904520037795E-11; // 0x1.7654p-37
        const double Ln2Low = 1.4286068203094173E-06; // 0x1.7f7d1cf79abcap-20
        const double Ln2High = 0.693145751953125; // 0x1.62e4p-1
        var lh = Ln2High * e;
        var ll = Ln2Low * e;
        ll += z;
        var rh = lh + lj;
        var rl = ((lh - rh) + lj) + (ll + f);
        var fh = (float)(rh + rl);
        var fl64 = (rh - fh) + rl;
        var fl = (float)fl64;
        var tfl = fl * 2.0f;
        // When fl is half an ulp of fh, push it away from the halfway point by the sign of what is left.
        if ((fh + tfl) - fh == tfl)
          fl += CopySignF(0.5f, (float)(fl64 - fl)) * MathF.Abs(fl);

        ub = fh + fl;
      }

      return ub;
    }
  }

  /// <summary>
  /// <c>ln(1 + x)</c> refined: for |x| &lt; 2^-10 a double-double polynomial in x, otherwise 1 + x
  /// times four reciprocals from tables of 16, which <paramref name="a"/>, the first approximation,
  /// selects, and a double-double polynomial of degree 9, in triple-double.
  /// </summary>
  private static double Log1PRefine(double x, double a) {
    var ix = (ulong)BitConverter.DoubleToInt64Bits(x);
    var ax = ix << 1;
    var low = new double[1];
    double ln22;
    double ln21;
    double ln20;
    if (ax < 0x7ea0000000000000UL) {
      // |x| < 2^-10; Log1P has returned below 2^-53 already.
      var x2h = x * x;
      var x2l = Math.FusedMultiplyAdd(x, x, -x2h);
      var x3h = Log1PMultiplyD(x, x2h, x2l, low);
      var x3l = low[0];
      var sl = x * ((_log1PRefineSmallTail[0] + x * _log1PRefineSmallTail[1]) + x2h * (_log1PRefineSmallTail[2] + x * _log1PRefineSmallTail[3]));

      // The double-double polynomial in x, Horner from the coefficient of degree 4.
      var ch = LogFastTwoSum(_log1PRefineSmall[8], sl, low);
      var cl = _log1PRefineSmall[9] + low[0];
      for (var degree = 3; degree >= 0; degree--) {
        ch = Log1PMultiplyD(x, ch, cl, low);
        cl = low[0];
        ch = Log1PFastSum(_log1PRefineSmall[2 * degree], _log1PRefineSmall[2 * degree + 1], ch, cl, low);
        cl = low[0];
      }

      var sh = Log1PMultiplyDD(ch, cl, x3h, x3l, low);
      sl = low[0];
      x2h *= -0.5;
      x2l *= -0.5;
      ln22 = x;
      ln21 = Log1PFastSum(x2h, x2l, sh, sl, low);
      ln20 = low[0];
    } else {
      var i = (long)Math.Round(a * 94548.4621996991) + 34952; // 0x1.71547652b82fep+16
      var j1 = (int)((i >> 12) & 0xf);
      var j2 = (int)((i >> 8) & 0xf);
      var j3 = (int)((i >> 4) & 0xf);
      var j4 = (int)(i & 0xf);
      var je = (int)(i >> 16);

      // The logarithms of the four reciprocals as a triple-double, ln[n][j][part] at (16 n + j) * 3 + part.
      var n1 = j1 * 3;
      var n2 = (16 + j2) * 3;
      var n3 = (32 + j3) * 3;
      var n4 = (48 + j4) * 3;
      var l0 = (_log1PRefineLog[n1] + _log1PRefineLog[n2]) + (_log1PRefineLog[n3] + _log1PRefineLog[n4]);
      var l1 = (_log1PRefineLog[n1 + 1] + _log1PRefineLog[n2 + 1]) + (_log1PRefineLog[n3 + 1] + _log1PRefineLog[n4 + 1]);
      var l2 = (_log1PRefineLog[n1 + 2] + _log1PRefineLog[n2 + 2]) + (_log1PRefineLog[n3 + 2] + _log1PRefineLog[n4 + 2]);

      // 1 + x as t + dt, scaled by 2^-je; dt is zero unless the sum is inexact.
      double t;
      var dt = 0.0;
      if ((long)ix < 0x4690000000000000L && ix < 0xbfe0000000000000UL) {
        t = Log1PTwoSum(1.0, x, low);
        dt = low[0];
      } else {
        t = (long)ix >= 0x4690000000000000L ? x : 1 + x;
      }

      t = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(t) - ((long)je << 52));
      var t12 = _log1PRefineReciprocal[j1] * _log1PRefineReciprocal[16 + j2];
      var t34 = _log1PRefineReciprocal[32 + j3] * _log1PRefineReciprocal[48 + j4];
      var th = t12 * t34;
      var tl = Math.FusedMultiplyAdd(t12, t34, -th);
      var dh = th * t;
      var dl = Math.FusedMultiplyAdd(th, t, -dh);
      var sh = tl * t;
      var sl = Math.FusedMultiplyAdd(tl, t, -sh);
      var xh = LogFastTwoSum(dh - 1, dl, low);
      var xl = low[0];
      xh = Log1PFastSum(xh, xl, sh, sl, low);
      xl = low[0];
      if (dt != 0) {
        dt = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(dt) - ((long)je << 52));
        var ddh = th * dt;
        var ddl = Math.FusedMultiplyAdd(th, dt, -ddh) + tl * dt;
        xh = Log1PFastSum(xh, xl, ddh, ddl, low);
        xl = low[0];
      }

      // The polynomial: four low coefficients in double, five high ones in double-double.
      sl = xh * ((_log1PRefineTail[0] + xh * _log1PRefineTail[1]) + (xh * xh) * (_log1PRefineTail[2] + xh * _log1PRefineTail[3]));
      var ch = LogFastTwoSum(_log1PRefinePolynomial[8], sl, low);
      var cl = _log1PRefinePolynomial[9] + low[0];
      for (var degree = 3; degree >= 0; degree--) {
        ch = Log1PMultiplyDD(xh, xl, ch, cl, low);
        cl = low[0];
        ch = Log1PFastSum(_log1PRefinePolynomial[2 * degree], _log1PRefinePolynomial[2 * degree + 1], ch, cl, low);
        cl = low[0];
      }

      sh = Log1PMultiplyDD(xh, xl, ch, cl, low);
      sl = low[0];
      if (je != 0) {
        ln22 = 0.6931471805601177 * je; // 0x1.62e42fefa4p-1
        ln21 = -1.7239444525615996E-13 * je; // -0x1.8432a1b0e28p-43
        ln20 = 1.1612227229362532E-26 * je; // 0x1.cc01f97b57a08p-87
        ln22 += l2;
        ln21 = Log1PFastSum(ln21, ln20, l1, l0, low);
        ln20 = low[0];
      } else {
        ln22 = l2;
        ln21 = l1;
        ln20 = l0;
      }

      ln21 = Log1PTwoSum(ln21, sh, low);
      ln20 = (ln20 + sl) + low[0];
    }

    ln22 = LogFastTwoSum(ln22, ln21, low);
    ln21 = low[0];
    ln21 = LogFastTwoSum(ln21, ln20, low);
    ln20 = low[0];

    // A middle part that is a power of two may hide a halfway case: move it one ulp toward the low part.
    var w = BitConverter.DoubleToInt64Bits(ln21);
    if ((w & (long)(~0UL >> 12)) == 0) {
      var sign = (BitConverter.DoubleToInt64Bits(ln20) ^ w) < 0;
      ln21 = BitConverter.Int64BitsToDouble(sign ? w - 1 : w + 1);
    }

    return ln22 + ln21;
  }

  /// <summary>The exact sum of two numbers in any order; the rounded sum is returned and its error written to <c>low[0]</c>.</summary>
  private static double Log1PTwoSum(double xh, double ch, double[] low) {
    var s = xh + ch;
    var d = s - xh;
    low[0] = (ch - d) + (xh + (d - s));
    return s;
  }

  /// <summary>The sum of two double-doubles, |xh| &gt;= |yh|; the high part is returned, the low one written to <c>low[0]</c>.</summary>
  private static double Log1PFastSum(double xh, double xl, double yh, double yl, double[] low) {
    var sh = LogFastTwoSum(xh, yh, low);
    low[0] = (xl + yl) + low[0];
    return sh;
  }

  /// <summary>The product of two double-doubles; the high part is returned, the low one written to <c>low[0]</c>.</summary>
  private static double Log1PMultiplyDD(double xh, double xl, double ch, double cl, double[] low) {
    var ahhh = ch * xh;
    low[0] = (cl * xh + ch * xl) + Math.FusedMultiplyAdd(ch, xh, -ahhh);
    return ahhh;
  }

  /// <summary>The product of a double and a double-double; the high part is returned, the low one written to <c>low[0]</c>.</summary>
  private static double Log1PMultiplyD(double x, double ch, double cl, double[] low) {
    var ahhh = ch * x;
    low[0] = cl * x + Math.FusedMultiplyAdd(ch, x, -ahhh);
    return ahhh;
  }

  /// <summary>
  /// <c>ln(1 + x)</c> of a <c>float</c> that is -1 or below, infinite or a NaN: negative infinity for -1,
  /// the number itself for +infinity and a NaN, NaN otherwise.
  /// </summary>
  private static float Log1PSpecialF(float x) {
    var ux = (uint)BitConverter.SingleToInt32Bits(x);
    if (ux == 0xbf800000u)
      return float.NegativeInfinity;

    if (ux == 0x7f800000u || ux << 1 > 0xff000000u)
      return x + x;

    return float.NaN;
  }

  // binary32/log1p/log1pf.c c: 4 entries
  private static readonly double[] _log1PFastF = [
    0.9999999964978914, -0.49999999241150506, 0.33339251544971726, -0.2500690494115668
  ];

  // binary64/log1p/log1p.c c@3: 12 entries
  private static readonly double[] _log1PMedium = [
    0.3333333333333333, -0.25, 0.20000000000001175, -0.16666666666668725,
    0.14285714283910433, -0.1249999999746985, 0.11111112429520291, -0.10000001594045592,
    0.09090414682710687, -0.08332796485529335, 0.07784130897129093, -0.07234720451604476
  ];

  // binary64/log1p/log1p.c c@2: 4 entries
  private static readonly double[] _log1PNear = [
    -0.49999999999999983, 0.3333333333333331, -0.2500000124176421, 0.20000001241526807
  ];

  // binary64/log1p/log1p.c c@4: 6 entries
  private static readonly double[] _log1PPolynomial = [
    -0.5000000000000068, 0.3333333333332143, -0.2499999994364062, 0.20000000456291744,
    -0.16668167978041729, 0.1428137457240121
  ];

  // binary32/log1p/log1pf.c b: 8 entries
  private static readonly double[] _log1PPolynomialF = [
    1.0, -0.5, 0.333333333333704, -0.2500000000005929,
    0.1999999921853749, -0.16666665744658113, 0.1429098594542405, -0.125052714602758
  ];

  // binary64/log1p/log1p.c rf: 64 entries
  private static readonly double[] _log1PReciprocal = [
    1.4065934121608734, 1.3913043439388275, 1.376344084739685, 1.3617021143436432,
    1.3473684191703796, 1.3333333432674408, 1.319587618112564, 1.3061224520206451,
    1.2929292917251587, 1.280000001192093, 1.2673267424106598, 1.2549019753932953,
    1.2427184581756592, 1.2307692170143127, 1.2190476059913635, 1.2075471580028534,
    1.1962616741657257, 1.185185194015503, 1.1743119359016418, 1.1636363565921783,
    1.1531531512737274, 1.1428571343421936, 1.1327433586120605, 1.1228070259094238,
    1.113043487071991, 1.1034482717514038, 1.0940170884132385, 1.0847457647323608,
    1.075630247592926, 1.0666666626930237, 1.0578512251377106, 1.0491803288459778,
    1.0406503975391388, 1.0322580635547638, 1.0239999890327454, 1.0158730149269104,
    1.0078740119934082, 1.0, 0.9846153855323792, 0.969696968793869,
    0.955223873257637, 0.9411764740943909, 0.9275362342596054, 0.9142857193946838,
    0.9014084488153458, 0.8888888955116272, 0.8767123222351074, 0.8648648709058762,
    0.8533333390951157, 0.8421052694320679, 0.8311688303947449, 0.8205128163099289,
    0.8101265877485275, 0.7999999970197678, 0.790123462677002, 0.7804878056049347,
    0.77108433842659, 0.7619047611951828, 0.7529411762952805, 0.7441860437393188,
    0.7356321811676025, 0.7272727340459824, 0.7191011309623718, 0.7111111134290695
  ];

  // binary32/log1p/log1pf.c x0: 32 entries
  private static readonly double[] _log1PReciprocalF = [
    0.9846153259277344, 0.9552239179611206, 0.9275362491607666, 0.9014084339141846,
    0.8767123222351074, 0.8533333539962769, 0.8311688899993896, 0.810126543045044,
    0.790123462677002, 0.7710843086242676, 0.7529411315917969, 0.7356321811676025,
    0.719101071357727, 0.7032966613769531, 0.6881721019744873, 0.6736842393875122,
    0.6597938537597656, 0.6464647054672241, 0.6336634159088135, 0.6213592290878296,
    0.6095237731933594, 0.5981308221817017, 0.5871559381484985, 0.5765765905380249,
    0.5663716793060303, 0.5565217733383179, 0.5470085144042969, 0.5378150939941406,
    0.5289256572723389, 0.5203251838684082, 0.5119999647140503, 0.5039370059967041
  ];

  // binary64/log1p/log1p.c lf: 128 entries
  private static readonly double[] _log1PReciprocalLog = [
    -4.4317381942116306E-13, -0.3411707613604449, 4.0013912138283334E-13, -0.33024168407700927,
    -5.834312866157871E-14, -0.3194307698349803, 2.0589937312616874E-13, -0.3087354718709321,
    -4.10542279907291E-13, -0.2981533709216819, -3.9179600956244317E-13, -0.2876820799019697,
    2.901974483200145E-13, -0.277319277267452, 3.910306005521957E-13, -0.2670627875777427,
    -3.465251888791423E-13, -0.25691041285335814, -3.6574839178152967E-13, -0.24686007886248262,
    -1.1415887706053916E-13, -0.23690975476165477, 2.3822881743566464E-13, -0.22705746227711643,
    2.7945714282238234E-13, -0.21730128500348656, 2.708615599425364E-13, -0.2076393536026444,
    -1.0576762150473066E-14, -0.19806990305187355, -2.540192818466683E-13, -0.18859116002840892,
    1.5113494587817901E-13, -0.17920142270577344, -4.3834330703842754E-13, -0.1698990442455397,
    -3.792449453029353E-13, -0.16068238960633607, -1.1428429246927808E-13, -0.1515498920734899,
    4.4049225141438904E-13, -0.14250006097790902, 1.9554170652836846E-13, -0.13353138517413754,
    1.5136391349708576E-13, -0.12464244148213766, -2.509147301913318E-13, -0.11583182297545136,
    -3.2365244047734355E-13, -0.1070981434722853, -2.612379657200911E-13, -0.09844006908770098,
    2.2426493126728326E-13, -0.08985632399981114, -4.50032233526228E-13, -0.08134564131614752,
    -8.398714884436042E-14, -0.0729067666170522, -2.0614111664603966E-13, -0.06453851741207473,
    1.3723794718518895E-13, -0.056239704586005246, 1.3631357719125273E-13, -0.048009220117819495,
    4.993089539480097E-14, -0.03984589993251575, 3.334806483348034E-13, -0.03174869738359121,
    -1.800157615691043E-13, -0.02371651590692636, 1.4896708715749982E-13, -0.01574835603696556,
    -9.877715405793228E-14, -0.00784317373563681, 1.1858461261560205E-20, 0.0,
    -4.530194485971635E-13, 0.0155041856050957, 4.529858811723982E-14, 0.030771659598030965,
    3.9257853815446455E-13, 0.045809543714312895, -2.795029310822492E-13, 0.060624618091424054,
    -1.6407972428595914E-13, 0.07522341867661453, 3.8120527306180076E-13, 0.0896121531013705,
    -1.5138510210342702E-13, 0.10379679577727075, -4.651953004204541E-14, 0.1177830282058494,
    1.0198511967129953E-13, 0.1315763652391979, -7.715560696105245E-14, 0.14518200285965577,
    -2.044955059142679E-14, 0.1586050234245704, 1.4090904680505034E-13, 0.17185024947593774,
    -2.7685839756515255E-13, 0.18492233942561143, -2.9340599350926274E-13, 0.19782574845248746,
    4.544050862071814E-13, 0.2105647623548066, 1.8568266035034247E-13, 0.22314355503931438,
    -9.306682751802988E-14, 0.2355660638622794, 4.417179992524854E-13, 0.24783616297281696,
    2.3999639248253294E-13, 0.2599575230397022, -3.687038376190463E-13, 0.27193371641533304,
    -9.383413327312139E-14, 0.2837681733635691, 4.1482013779496806E-13, 0.29546421661871136,
    8.403851380030668E-14, 0.30702503902011813, 4.371652995193829E-13, 0.31845372180487175,
    -1.1113422665234201E-13, 0.32975327612803085, 1.391337365710706E-13, 0.34092658371082507
  ];

  // binary32/log1p/log1pf.c lix: 32 entries
  private static readonly double[] _log1PReciprocalLogF = [
    0.015504246151250851, 0.04580949692638588, 0.07522340262177525, 0.10379681231873428,
    0.13157636524993893, 0.15860500597289098, 0.18492226772413786, 0.21056481754676373,
    0.2355660638728254, 0.2599575617004688, 0.2837682327459302, 0.3070250390308412,
    0.3297533590262705, 0.3519764827724638, 0.37371632412254996, 0.3949937654106705,
    0.41582783554970704, 0.43623667551594897, 0.45623735526113346, 0.4758458955673773,
    0.4950773264131371, 0.5139457827778414, 0.5324648417209502, 0.5506470937489146,
    0.5685047390885981, 0.5860489835469292, 0.6032909110533698, 0.6202404693671432,
    0.6369073914671951, 0.6533013092762884, 0.6694307228711411, 0.6853040068348488
  ];

  // binary64/log1p/log1p.c ln: 192 entries
  private static readonly double[] _log1PRefineLog = [
    3.490488883071821E-29, -3.657776900127222E-14, -0.3465735942390893,
    -1.272877066394603E-28, 1.128688558916184E-13, -0.30325189897030214,
    2.650191937567658E-29, 2.2227775419296888E-13, -0.25993020072019135,
    -1.6022661538905634E-28, -1.5289396211038625E-13, -0.2166085057106102,
    3.0321091170169657E-29, 5.069297698582655E-14, -0.17328678826652322,
    -2.7991976676364525E-29, 2.6770221006250374E-13, -0.12996510139782913,
    -4.635994551634659E-30, 2.3415355238480876E-13, -0.08664340956238448,
    -1.9173852981559953E-29, -1.734359053957514E-13, -0.0433217095769578,
    0.0, 0.0, 0.0,
    -1.3799658208253054E-29, 2.4805886252056986E-13, 0.043321703925357724,
    -3.790535294855601E-29, 1.2128199025240673E-13, 0.08664339144161204,
    -1.3913370763532092E-28, 2.1196719565490138E-13, 0.12996509109598264,
    -8.550365205465717E-29, 4.039406366217252E-14, 0.1732867981409072,
    -1.7933681318627213E-28, -6.025273770489546E-14, 0.21660850003263477,
    -1.8880446316802402E-28, -1.9029947617630476E-13, 0.259930197943504,
    -1.604129377092513E-28, 3.6675038976677543E-13, 0.3032519013777346,
    2.346114658914315E-29, 9.03614080295525E-14, -0.02166083814972808,
    -6.232044190254348E-29, -2.2133018132246406E-13, -0.018953232390231278,
    1.9935150436737024E-28, -3.977016158101138E-13, -0.016245635461928032,
    1.2349341255741007E-28, -1.7881106211080862E-14, -0.013538037966100092,
    1.9600991825647759E-28, 3.5762821230998044E-13, -0.010830430346686626,
    -1.9878191392801914E-28, -3.593318524471263E-13, -0.008122832450681017,
    9.085502763279599E-29, -2.413181416410097E-13, -0.0054152050070115365,
    1.8051038340171896E-28, -9.024391839988719E-14, -0.002707597426706343,
    0.0, 0.0, 0.0,
    -3.15700554895806E-29, -4.438879756174488E-13, 0.0027076120850324514,
    -9.937762599854552E-29, -1.8890618343455317E-13, 0.005415219015958428,
    1.354079384383042E-28, 2.648787547416538E-13, 0.008122815843307762,
    -2.1122510366764825E-30, -7.340773574192948E-14, 0.010830427745531779,
    8.929076391630039E-29, -2.0948791037969876E-13, 0.013538034913835872,
    1.9305385687165429E-28, -2.1247264178488656E-13, 0.016245632524260145,
    2.4990170554789388E-29, -5.657531516759797E-14, 0.018953246127239254,
    -2.8344613129851583E-29, 1.8576854033204012E-13, -0.0013538073581003118,
    1.1686217358759985E-28, -4.034104270472757E-13, -0.0011845662875202834,
    1.404009973870391E-28, -4.0774796000332313E-13, -0.0010153561142942635,
    -5.433498024814066E-29, 1.6618415933064737E-13, -0.0008461173047180637,
    8.80108153457782E-29, 4.1280134134017224E-13, -0.0006769094125047559,
    -1.4532145801396695E-28, -4.438534624609597E-13, -0.0005076728830317734,
    1.6859322229114166E-28, 9.410162557367793E-14, -0.0003384375013411045,
    -1.1090049766742229E-30, -1.8006683696216685E-13, -0.00016923306793614756,
    0.0, 0.0, 0.0,
    1.6033889529050001E-28, -1.7342749815711204E-13, 0.0001692319056019187,
    1.6510080690668139E-28, -2.690203937922561E-13, 0.00033844773679447826,
    -2.2865107345493895E-29, 1.8869831490256793E-14, 0.0005076772977190558,
    1.9822155314551572E-28, 6.526775125133817E-16, 0.0006769056799384998,
    -4.922500256052395E-29, -3.130564265885368E-13, 0.0008461328779958421,
    -3.5533487733002313E-29, -4.807077231744788E-15, 0.0010153588855246198,
    -3.2767427117080355E-29, 2.120117271892569E-14, 0.0011845836979773594,
    6.318855149463929E-29, 2.60414824845909E-13, -8.460521439701552E-05,
    -9.712140128135025E-29, -2.986506832639214E-13, -7.402622850349871E-05,
    3.932399183726781E-29, 1.000448588440564E-13, -6.34471316516283E-05,
    -1.9091446271098117E-28, 3.0637457461517507E-13, -5.289772343530785E-05,
    1.5385934816018816E-28, -4.160612369426621E-13, -4.23184019382461E-05,
    1.1308489491448164E-28, -1.7363853975856337E-13, -3.1738969482830726E-05,
    -1.6003354773735187E-28, 1.2651615762873333E-13, -2.1159425159567036E-05,
    -1.4870885186807698E-28, -4.22723578155497E-13, -1.0579768058960326E-05,
    0.0, 0.0, 0.0,
    -1.7228450156377404E-28, -4.2193409271485873E-13, 1.0579880836303346E-05,
    -1.066603640926905E-28, 1.3283204115511192E-13, 2.1159872630960308E-05,
    7.582077643761001E-29, 2.843132779626563E-13, 3.172507513227174E-05,
    -1.977812516276072E-28, -8.656180116755515E-14, 4.230529157212004E-05,
    1.635630552961741E-28, -3.8312875896217467E-13, 5.288561987981666E-05,
    -8.368784719608391E-29, 3.0647590088119876E-13, 6.34660591458669E-05,
    1.3231985095898426E-28, -2.8183958406165062E-14, 7.40317091185716E-05
  ];

  // binary64/log1p/log1p.c cy: 10 entries
  private static readonly double[] _log1PRefinePolynomial = [
    1.0, 0.0,
    -0.5, 0.0,
    0.3333333333333333, 1.850371707708594E-17,
    -0.25, -9.244510330277069E-33,
    0.2, -1.1102230246251552E-17
  ];

  // binary64/log1p/log1p.c rt: 64 entries
  private static readonly double[] _log1PRefineReciprocal = [
    1.4142135679721832, 1.3542555570602417, 1.296839565038681, 1.2418578267097473,
    1.1892071068286896, 1.138788640499115, 1.0905077457427979, 1.0442737936973572,
    1.0, 0.9576032757759094, 0.9170040488243103, 0.8781260848045349,
    0.840896412730217, 0.8052451610565186, 0.7711054086685181, 0.7384130656719208,
    1.0218971371650696, 1.019133985042572, 1.0163783133029938, 1.0136300921440125,
    1.0108892917633057, 1.0081559121608734, 1.005429893732071, 1.0027112662792206,
    1.0, 0.9972960501909256, 0.9945994168519974, 0.9919100850820541,
    0.9892280101776123, 0.9865531921386719, 0.9838856160640717, 0.9812252372503281,
    1.0013547241687775, 1.001185268163681, 1.0010158717632294, 1.0008464753627777,
    1.0006771385669708, 1.000507801771164, 1.0003384947776794, 1.0001692473888397,
    1.0, 0.9998307824134827, 0.9996616095304489, 0.9994924515485764,
    0.9993233233690262, 0.9991542249917984, 0.998985156416893, 0.99881611764431,
    1.0000846087932587, 1.000074028968811, 1.0000634491443634, 1.0000528991222382,
    1.0000423192977905, 1.000031739473343, 1.0000211596488953, 1.0000105798244476,
    1.0, 0.9999894201755524, 0.9999788403511047, 0.9999682754278183,
    0.9999576956033707, 0.999947115778923, 0.9999365359544754, 0.999925971031189
  ];

  // binary64/log1p/log1p.c cz: 10 entries
  private static readonly double[] _log1PRefineSmall = [
    0.3333333333333333, 1.8503717077085944E-17,
    -0.25, 1.480959314983063E-26,
    0.2, -1.1102230271317301E-17,
    -0.16666666666666666, -9.338820764972004E-18,
    0.14285714285714285, 8.043908218114221E-18
  ];

  // binary64/log1p/log1p.c czl: 4 entries
  private static readonly double[] _log1PRefineSmallTail = [
    -0.12499999999979157, 0.1111111111108786, -0.10000021855066944, 0.09090930947431196
  ];

  // binary64/log1p/log1p.c cl: 4 entries
  private static readonly double[] _log1PRefineTail = [
    -0.16666666666666666, 0.14285714285714285, -0.12500000001309675, 0.11111111112421457
  ];

  // binary64/log1p/log1p.c sc: 3 entries
  private static readonly double[] _log1PScale = [0.5, 0.25, 0.125];

  // binary64/log1p/log1p.c c@1: 2 entries
  private static readonly double[] _log1PSmall = [
    -0.5000000000000516, 0.33333333333338494
  ];

}
