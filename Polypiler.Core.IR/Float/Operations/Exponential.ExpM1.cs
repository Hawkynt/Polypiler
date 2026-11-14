using System;

namespace Polypiler.Core.IR;

/// <summary>
/// <c>e^x - 1</c>, converted from CORE-MATH's <c>src/binary64/expm1/expm1.c</c> and
/// <c>src/binary32/expm1/expm1f.c</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>e^x - 1</c>, correctly rounded and accurate near zero: for <c>|x| &lt; 1/4</c> the argument
  /// is split into <c>i/2^7 + z/2^7</c> with <c>e^(i/2^7) - 1</c> from a table; above, as for
  /// <see cref="Exp"/>, with the 1 subtracted exactly. An error bound decides the rounding, and where
  /// it does not, a double-double evaluation does.
  /// </summary>
  /// <param name="x">The exponent.</param>
  /// <returns><c>e^x - 1</c>; <c>-1</c> for <c>-∞</c>, <c>+∞</c> above the normal range.</returns>
  public static double ExpM1(double x) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    var aix = bits & 0x7fffffffffffffffUL;
    var lo = new double[1];
    if (aix < 0x3fd0000000000000UL) {
      // |x| < 1/4.
      if (aix < 0x3ca0000000000000UL) {
        // |x| < 2^-53: x + x^2/2 rounds to x + 2^-54 |x|.
        if (aix == 0)
          return x;

        return Math.FusedMultiplyAdd(5.551115123125783e-17, Math.Abs(x), x);
      }

      var sx = 128.0 * x;
      var fx = Math.Round(sx);
      var z = sx - fx;
      var z2 = z * z;
      var i = (int)fx;
      var th = _expM1TableHigh[i + 32];
      var tl = _expM1TableLow[i + 32];
      const double C0 = 0.0078125; // 0x1p-7
      const double C1 = 3.0517578125e-05; // 0x1p-15
      const double C2 = 7.947285970050844e-08; // 0x1.55555555551adp-24
      const double C3 = 1.5522042910260805e-10; // 0x1.555555555599cp-33
      const double C4 = 2.4253205264225237e-13; // 0x1.11111ad1ad69dp-42
      const double C5 = 3.1579677127348593e-16; // 0x1.6c16c168b1fb5p-52
      var fh = z * C0;
      var fl = z2 * ((C1 + z * C2) + z2 * (C3 + z * (C4 + z * C5)));
      const double E0 = 4.404571325722362e-20; // 0x1.ap-65
      var eps = z2 * E0 + 4.930380657631324e-32;

      // (e^(i/2^7) - 1) + (e^(z/2^7) - 1) + the product of the two.
      var rh = ExpFastTwoSum(th, fh, lo);
      var rl = lo[0];
      rl += tl + fl;
      fh = ExpMulDD(th, tl, fh, fl, lo);
      fl = lo[0];
      fh = ExpFastSum(rh, rl, fh, fl, lo);
      fl = lo[0];
      var upper = fh + (fl + eps);
      var lower = fh + (fl - eps);
      if (upper != lower)
        return ExpM1Accurate(x);

      return lower;
    }

    if (aix >= 0x40862e42fefa39f0UL) {
      // |x| >= 0x1.62e42fefa39fp+9.
      if (aix > 0x7ff0000000000000UL)
        return x + x;

      if (aix == 0x7ff0000000000000UL)
        return bits >> 63 != 0 ? -1.0 : x;

      if (bits >> 63 == 0) {
        const double Huge = 8.98846567431158e+307; // 0x1p1023
        return Huge * Huge;
      }
    }

    if (bits >= 0xc0425e4f7b2737faUL) {
      // x <= -0x1.25e4f7b2737fap+5: e^x is below half a unit of 1.
      if (bits >= 0xc042b708872320e2UL)
        return -1.0 + 2.7755575615628914e-17;

      return (36.7368005696771 + x + 6.739832990259606e-16) * 8.008566259537294e-17 - 0.9999999999999999;
    }

    const double S = 5909.278887481194; // 0x1.71547652b82fep+12
    var t = Math.Round(x * S);
    var jt = (long)t;
    var i0 = (int)((jt >> 6) & 0x3f);
    var i1 = (int)(jt & 0x3f);
    var ie = jt >> 12;
    var ph = ExpMulDD(_expT0High[i0], _expT0Low[i0], _expT1High[i1], _expT1Low[i1], lo);
    var pl = lo[0];
    const double L2h = 0.00016922538588914904; // 0x1.62e42ffp-13
    const double L2l = 1.0256140314162804e-14; // 0x1.718432a1b0e26p-47
    var dx = (x - L2h * t) + L2l * t;
    var dx2 = dx * dx;
    const double D0 = 1.0; // 0x1p+0
    const double D1 = 0.5; // 0x1p-1
    const double D2 = 0.16666666674124284; // 0x1.55555557e54ffp-3
    const double D3 = 0.04166666665427057; // 0x1.55555553a12f4p-5
    var p = (D0 + dx * D1) + dx2 * (D2 + dx * D3);
    var qh = ph;
    var tx = ph * dx;
    var ql = pl + tx * p;
    var margin = 1.64e-19 * ph;

    // Subtract 2^-ie, which becomes the 1 after the scaling by 2^ie.
    var off = BitConverter.Int64BitsToDouble((2048 + 1023 - ie) << 52);
    var e = 0.0;
    if (ie < 53) {
      qh = ExpFastTwoSum(off, qh, lo);
      e = lo[0];
    } else if (ie < 75) {
      qh = ExpFastTwoSum(qh, off, lo);
      e = lo[0];
    }

    ql += e;
    var ub = qh + (ql + margin);
    var lb = qh + (ql - margin);
    if (ub != lb)
      return ExpM1Accurate(x);

    return ExpScale(lb, ie);
  }

  /// <summary><c>e^x - 1</c> in double-double arithmetic, for the arguments whose rounding the fast path leaves open.</summary>
  private static double ExpM1Accurate(double x) {
    var lo = new double[1];
    if (Math.Abs(x) < 0.25) {
      // x + x^2/2 + x^3 * P(x), with the coefficients of degree 14 and up in double.
      const double Cl0 = 1.147074559877144e-11; // 0x1.93974a8ca5354p-37
      const double Cl1 = 7.647163730401437e-13; // 0x1.ae7f3e71e4908p-41
      const double Cl2 = 4.7794758077432926e-14; // 0x1.ae7f357341648p-45
      const double Cl3 = 2.811458162324019e-15; // 0x1.952c7f96664cbp-49
      const double Cl4 = 1.5631408654506689e-16; // 0x1.686f8ce633aaep-53
      const double Cl5 = 8.220634658394565e-18; // 0x1.2f49b2fbfb5b6p-57
      lo[0] = x * (Cl0 + x * (Cl1 + x * (Cl2 + x * (Cl3 + x * (Cl4 + x * Cl5)))));
      var fh = ExpM1PolyDDD(x, lo);
      var fl = lo[0];
      fh = ExpMulDDD(x, fh, fl, lo);
      fl = lo[0];
      fh = ExpMulDDD(x, fh, fl, lo);
      fl = lo[0];
      fh = ExpMulDDD(x, fh, fl, lo);
      fl = lo[0];
      var hx = 0.5 * x;
      var x2h = x * hx;
      var x2l = Math.FusedMultiplyAdd(x, hx, -x2h);
      fh = ExpFastSum(x2h, x2l, fh, fl, lo);
      fl = lo[0];

      // x + (fh + fl) as a triple-double v0 + v1 + v2.
      var v0 = ExpFastTwoSum(x, fh, lo);
      var v2 = lo[0];
      var v1 = ExpFastTwoSum(v2, fl, lo);
      v2 = lo[0];
      v0 = ExpFastTwoSum(v0, v1, lo);
      v1 = lo[0];
      v1 = ExpFastTwoSum(v1, v2, lo);
      v2 = lo[0];
      var ix = BitConverter.DoubleToInt64Bits(v1);
      if ((ix & 0xfffffffffffffL) == 0) {
        // v1 is a power of two or zero: the rounding is decided by v2 or is an exceptional case.
        if (ix << 1 == 0)
          return ExpM1Database(x, v0);

        var d = (((ix >> 63) ^ (BitConverter.DoubleToInt64Bits(v2) >> 63)) << 1) + 1;
        v1 = BitConverter.Int64BitsToDouble(ix + d);
      }

      return v0 + v1;
    }

    const double S = 5909.278887481194; // 0x1.71547652b82fep+12
    var t = Math.Round(x * S);
    var jt = (long)t;
    var i0 = (int)((jt >> 6) & 0x3f);
    var i1 = (int)(jt & 0x3f);
    var ie = jt >> 12;
    var th = ExpMulDD(_expT0High[i0], _expT0Low[i0], _expT1High[i1], _expT1Low[i1], lo);
    var tl = lo[0];
    const double L2h = 0.00016922538588914904; // 0x1.62e42ffp-13
    const double L2l = 1.0256140314162804e-14; // 0x1.718432a1b0e26p-47
    const double L2ll = 3.2042720746546034e-31; // 0x1.9ff0342542fc3p-102
    var dx = x - L2h * t;
    var dxl = L2l * t;
    var dxll = L2ll * t + Math.FusedMultiplyAdd(L2l, t, -dxl);
    var dxh = dx + dxl;
    dxl = (dx - dxh) + dxl + dxll;
    var qh = ExpM1PolyDD(dxh, dxl, lo);
    var ql = lo[0];
    qh = ExpMulDD(dxh, dxl, qh, ql, lo);
    ql = lo[0];
    qh = ExpMulDD(qh, ql, th, tl, lo);
    ql = lo[0];
    qh = ExpFastSum(th, tl, qh, ql, lo);
    ql = lo[0];

    // Subtract 2^-ie, which becomes the 1 after the scaling by 2^ie.
    var off = BitConverter.Int64BitsToDouble((2048 + 1023 - ie) << 52);
    var e = 0.0;
    if (ie < 53) {
      qh = ExpFastTwoSum(off, qh, lo);
      e = lo[0];
    } else if (ie < 104) {
      qh = ExpFastTwoSum(qh, off, lo);
      e = lo[0];
    }

    ql += e;
    qh = ExpFastTwoSum(qh, ql, lo);
    ql = lo[0];
    var near = ((ulong)BitConverter.DoubleToInt64Bits(ql) + 8) & 0xfffffffffffffUL;
    if (near <= 8)
      qh = ExpM1Database(x, qh);

    return ExpScale(qh, ie);
  }

  /// <summary>
  /// The value of a polynomial with double-double coefficients at a double-double point, in Horner's
  /// scheme; the low part goes into <c>low[0]</c>.
  /// </summary>
  private static double ExpM1PolyDD(double xh, double xl, double[] low) {
    var ch = _expAccurateHigh[_expAccurateHigh.Length - 1];
    var cl = _expAccurateLow[_expAccurateHigh.Length - 1];
    for (var i = _expAccurateHigh.Length - 2; i >= 0; --i) {
      ch = ExpMulDD(xh, xl, ch, cl, low);
      cl = low[0];
      ch = ExpFastSum(_expAccurateHigh[i], _expAccurateLow[i], ch, cl, low);
      cl = low[0];
    }

    low[0] = cl;
    return ch;
  }

  /// <summary>
  /// The value of a polynomial with double-double coefficients at a double, in Horner's scheme, with
  /// <c>low[0]</c> added to the highest coefficient on entry; the low part goes into <c>low[0]</c>.
  /// </summary>
  private static double ExpM1PolyDDD(double x, double[] low) {
    var n = _expM1SmallHigh.Length - 1;
    var ch = ExpFastTwoSum(_expM1SmallHigh[n], low[0], low);
    var cl = _expM1SmallLow[n] + low[0];
    for (var i = n - 1; i >= 0; --i) {
      ch = ExpMulDDD(x, ch, cl, low);
      cl = low[0];
      ch = ExpFastSum(_expM1SmallHigh[i], _expM1SmallLow[i], ch, cl, low);
      cl = low[0];
    }

    low[0] = cl;
    return ch;
  }

  /// <summary>
  /// The correctly rounded <c>e^x - 1</c> of the arguments the accurate path rounds wrongly, or
  /// <paramref name="f"/> for any other; the list is laid out as the one of <see cref="Exp2Database"/>,
  /// the correction scaled to <paramref name="f"/>.
  /// </summary>
  private static double ExpM1Database(double x, double f) {
    const ulong Sign = 0x300e81651cUL;
    var key = (ulong)BitConverter.DoubleToInt64Bits(x);
    var a = 0;
    var b = _expM1Database.Length - 1;
    var m = (a + b) / 2;
    while (a <= b) {
      var c = (ulong)BitConverter.DoubleToInt64Bits(_expM1Database[m]);
      if (c < key) {
        a = m + 1;
      } else if (c == key) {
        var jf = BitConverter.DoubleToInt64Bits(f);
        var dr = BitConverter.Int64BitsToDouble((long)((Sign >> m) << 63 | (((ulong)(jf >> 52) & 0x7ff) - 54) << 52));
        var t = (_expM1DatabaseLast[m >> 5] >> ((m << 1) & 63)) & 3;
        var r = jf - 1;
        while ((ulong)(r & 3) != t)
          ++r;

        return BitConverter.Int64BitsToDouble(r) + dr;
      } else {
        b = m - 1;
      }

      m = (a + b) >> 1;
    }

    return f;
  }

  /// <summary>
  /// <c>e^x - 1</c> of <c>float</c>, correctly rounded: a polynomial of degree 9 for
  /// <c>|x| &lt; 0.15625</c>; above, <c>2^(k/2^5)</c> from a table times a polynomial in the rest,
  /// minus 1, and where the result is too close to the midpoint of two floats a longer reduction and
  /// a polynomial of degree 6 decide.
  /// </summary>
  /// <param name="x">The exponent.</param>
  /// <returns><c>e^x - 1</c>; <c>-1</c> for <c>-∞</c>, <c>+∞</c> above the normal range.</returns>
  public static float ExpM1F(float x) {
    const double Iln2 = 46.16624130844683; // 0x1.71547652b82fep+5
    const double Big = 6755399441055744.0; // 0x1.8p52
    var ux = (uint)BitConverter.SingleToInt32Bits(x);
    double z = x;
    var ax = ux << 1;
    if (ax < 0x7c400000u) {
      // |x| < 0.15625.
      if (ax < 0x676a09e8u) {
        // |x| < 0x1.6a09e8p-24: x + x^2/2 rounds to x + 2^-25 |x|.
        if (ax == 0)
          return x;

        return MathF.FusedMultiplyAdd(MathF.Abs(x), 2.9802322387695312e-08f, x);
      }

      const double B0 = 0.49999999999999656; // 0x1.fffffffffffc2p-2
      const double B1 = 0.16666666666667135; // 0x1.55555555555fep-3
      const double B2 = 0.041666666668544565; // 0x1.555555559767fp-5
      const double B3 = 0.008333333332479211; // 0x1.1111111098dc1p-7
      const double B4 = 0.0013888886118215516; // 0x1.6c16bca988aa9p-10
      const double B5 = 0.00019841274040338812; // 0x1.a01a07658483fp-13
      const double B6 = 2.4816724201894197e-05; // 0x1.a05b04d2c3503p-16
      const double B7 = 2.755731951095977e-06; // 0x1.71de3a960b5e3p-19
      var z2 = z * z;
      var z4 = z2 * z2;
      return (float)(z + z2 * ((B0 + z * B1) + z2 * (B2 + z * B3) + z4 * ((B4 + z * B5) + z2 * (B6 + z * B7))));
    }

    if (ax >= 0x8562e430u) {
      // |x| >= 0x1.62e43p+6.
      if (ax > 0xffu << 24)
        return x + x;

      if (ux >> 31 != 0) {
        if (ax == 0xffu << 24)
          return -1.0f;

        return -1.0f + 1.4901161193847656e-08f;
      }

      if (ax == 0xffu << 24)
        return x * x;

      return 3.4028234663852886e+38f * x;
    }

    var a = Iln2 * z;
    var ia = Math.Round(a);
    var h = a - ia;
    var h2 = h * h;
    var u = (ulong)BitConverter.DoubleToInt64Bits(ia + Big);
    const double C0 = 1;
    const double C1 = 0.021660849391257477; // 0x1.62e42fef4c4e7p-6
    const double C2 = 0.0002345984913513542; // 0x1.ebfd1b232f475p-13
    const double C3 = 1.6938658699950235e-06; // 0x1.c6b19384ecd93p-20
    var c2 = C2 + h * C3;
    var c0 = C0 + h * C1;
    var sv = BitConverter.Int64BitsToDouble((long)((ulong)BitConverter.DoubleToInt64Bits(_expT0High[2 * (int)(u & 0x1f)]) + ((u >> 5) << 52)));
    var r = (c0 + h2 * c2) * sv - 1.0;
    var ub = (float)r;
    var lb = (float)(r - sv * 1.433306806575274e-10);
    if (ub != lb) {
      // Rounding to nearest, no x < -17.32 gets here (checked on every float), so the shortcut to
      // -1 that upstream takes for those is left out.
      const double Iln2h = 46.16624128818512; // 0x1.7154765p+5
      const double Iln2l = 2.026170940661134e-08; // 0x1.5c17f0bbbe88p-26
      const double Ch0 = 0.02166084939249829; // 0x1.62e42fefa39efp-6
      const double Ch1 = 0.0002345961982022468; // 0x1.ebfbdff82c58fp-13
      const double Ch2 = 1.6938509724129055e-06; // 0x1.c6b08d702e0edp-20
      const double Ch3 = 9.172562701702629e-09; // 0x1.3b2ab6fb92e5ep-27
      const double Ch4 = 3.973729405780548e-11; // 0x1.5d886e6d54203p-35
      const double Ch5 = 1.4345723178374038e-13; // 0x1.430976b8ce6efp-43
      var s = sv;
      h = (Iln2h * z - ia) + Iln2l * z;
      h2 = h * h;
      var w = s * h;
      r = (s - 1) + w * ((Ch0 + h * Ch1) + h2 * ((Ch2 + h * Ch3) + h2 * (Ch4 + h * Ch5)));
      ub = (float)r;
    }

    return ub;
  }

  // e^(i/2^7) - 1 for -32 <= i <= 32, as double-double numbers.
  private static readonly double[] _expM1TableHigh = [
    -0.22119921692859512, -0.21509100668250825, -0.20893488914970404, -0.2027304885886756,
    -0.19647742631093923, -0.19017532065792075, -0.18382378697766022, -0.17742243760133536,
    -0.17097088181959963, -0.16446872585873495, -0.15791557285661761, -0.15131102283849607,
    -0.14465467269257748, -0.1379461161454243, -0.13118494373715683, -0.12437074279646176,
    -0.11750309741540454, -0.11058158842404442, -0.10360579336484954, -0.09657528646691327,
    -0.08948963861996584, -0.08234841734818421, -0.07515118678379518, -0.06789750764047242,
    -0.06058693718652419, -0.05321902921787114, -0.04579333403081165, -0.0383093983945747,
    -0.03076676552365587, -0.023164975049937975, -0.015503562994591547, -0.007782061739756485,
    0.0, 0.007843097206447935, 0.015747708586685727, 0.0237143166023579,
    0.03174340749910265, 0.03983547133623, 0.047991002016632756, 0.05621049731693195,
    0.06449445891785943, 0.07284339243487747, 0.08125780744903965, 0.08973821753809319,
    0.09828514030782587, 0.10689909742365744, 0.1155806146424807, 0.12433022184475073,
    0.13314845306682632, 0.1420358465335656, 0.1509929446911764, 0.16002029424032516,
    0.16911844616950444, 0.1782879557886632, 0.1875293827631006, 0.19684329114762478,
    0.20623024942098067, 0.2156908305205475, 0.2252256118773076, 0.234835175451091,
    0.24452010776609512, 0.25428099994668374, 0.2641184477534664, 0.27403305161966096,
    0.2840254166877415
  ];
  private static readonly double[] _expM1TableLow = [
    -1.0231869534531498e-17, -5.313807232301296e-17, 5.426586044764942e-17, 2.6020337682703143e-17,
    -3.661886830920417e-17, 5.0008114075382227e-17, 6.554697808700811e-18, -5.149396189997403e-17,
    -2.7604408719539223e-17, 2.561592821517568e-17, -3.8967887440685524e-17, 2.2088650680117402e-17,
    1.7204900005057594e-17, 5.763785158040174e-18, 6.146598011714697e-19, -2.1452399010158893e-17,
    -5.224526916735663e-17, 5.530240945009792e-17, -4.7460497709066285e-17, -2.0811956998712977e-17,
    -3.325048324577564e-17, 2.2920689673580445e-17, 1.0614261758612887e-17, -1.167464604196626e-18,
    -2.152447043447057e-17, 3.5480066918496995e-17, -3.392457164103672e-17, -3.5877873473605866e-18,
    -4.801151707083219e-17, 8.59650273368323e-18, -4.7493026566356186e-17, -2.8192701381719798e-18,
    0.0, 4.229455495208723e-17, 2.0530467874932267e-17, 1.8124461803844703e-17,
    2.1578125052084337e-17, 1.1038442468719412e-18, -5.327900898877614e-17, 2.011958971782554e-17,
    -2.2934210303960824e-18, -2.976174935473522e-17, -5.08832530424018e-17, 4.0889548002981385e-17,
    -2.0315852964578147e-17, 4.184587797682552e-17, 5.298211318168963e-17, -1.4900036878700333e-17,
    -5.370737708558031e-18, -1.2069701773647767e-17, 3.7613173622701076e-17, -9.002941214515411e-18,
    -4.156742078931154e-17, 5.439076284168112e-17, 6.415816207759217e-19, -5.89991778046089e-18,
    3.9295715071105525e-17, -4.2223555445971666e-17, -2.822851245069697e-17, -3.104366491258746e-19,
    3.66171795099051e-17, 1.3050032175111173e-17, -1.541497933603795e-17, -3.187472928105996e-17,
    -2.133257464457841e-17
  ];

  // The coefficients of degree 3 to 13 of e^x - 1 on |x| < 1/4, as double-double numbers.
  private static readonly double[] _expM1SmallHigh = [
    0.16666666666666666, 0.041666666666666664, 0.008333333333333333, 0.001388888888888889,
    0.0001984126984126984, 2.48015873015873e-05, 2.7557319223985893e-06, 2.755731922398589e-07,
    2.5052108385441717e-08, 2.0876756987867674e-09, 1.6059043836822621e-10
  ];
  private static readonly double[] _expM1SmallLow = [
    9.251858538542969e-18, 2.312964634635329e-18, 1.1564823173247946e-19, -5.3005439427021786e-20,
    1.7209547147978358e-22, 2.1496993363906636e-23, -1.8583040755160046e-22, 2.4805440528173178e-23,
    1.4639725357390741e-24, 1.8498580855208408e-25, 1.2583393781282993e-26
  ];

  // The arguments for which the accurate path does not round right, sorted by their bits, and two
  // bits for each, which of the numbers next to the accurate result to correct.
  private static readonly double[] _expM1Database = [
    0.11941886519949067, 0.2754060095862775, 0.27755765846625235, 0.2890189893844592,
    0.37641383522340355, 0.3864676908333609, 0.4342466565055342, 0.5006933289508785,
    0.837522455340574, 0.9072748486163491, 4.613042960553475, 6.0598594832526835,
    16.55983372179868, 17.83500450381271, 28.26876911181362, 37.862797029174125,
    49.229206176268114, 94.5946582241399, 143.5266815223847, 170.40063412375622,
    170.71172754077605, 301.663664262519, 414.48199762232366, 470.2797657541442,
    535.5022871124662, -0.10437659580587229, -0.26719162429499793, -0.2727912012622591,
    -0.31963083006878507, -0.36778644247009107, -0.3776469183707618, -0.40383574410560363,
    -0.5382665940940305, -0.5681328666204585, -0.9300183637496172, -1.0403571407112755,
    -1.1664532184587633, -1.8912684161004483
  ];
  private static readonly ulong[] _expM1DatabaseLast = [
    0x76f58b0d65bd5553UL, 0x0000000000000c06UL
  ];

}
