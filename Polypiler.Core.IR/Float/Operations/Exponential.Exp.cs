using System;

namespace Polypiler.Core.IR;

/// <summary>
/// <c>e^x</c>, converted from CORE-MATH's <c>src/binary64/exp/exp.c</c> and
/// <c>src/binary32/exp/expf.c</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>e^x</c>, correctly rounded: <c>x / log(2)</c> is split into <c>k/2^12 + r</c>, <c>2^(k/2^12)</c>
  /// comes from two tables and <c>e^r</c> from a polynomial of degree 4; an error bound decides the
  /// rounding, and where it does not, a double-double evaluation does.
  /// </summary>
  /// <param name="x">The exponent.</param>
  /// <returns><c>e^x</c>; <c>+0</c> below the subnormal range, <c>+∞</c> above the normal one.</returns>
  public static double Exp(double x) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    var aix = bits & 0x7fffffffffffffffUL;

    // e^x rounds to 1 for |x| <= 2^-54.
    if (aix <= 0x3c90000000000000UL)
      return 1.0 + x;

    if (aix >= 0x40862e42fefa39f0UL) {
      if (aix > 0x7ff0000000000000UL)
        return x + x;

      if (aix == 0x7ff0000000000000UL)
        return bits >> 63 != 0 ? 0.0 : x;

      // x >= 0x1.62e42fefa39fp+9 overflows.
      if (bits >> 63 == 0) {
        const double Huge = 8.98846567431158e+307; // 0x1p1023
        return Huge * Huge;
      }

      // x <= -0x1.74910d52d3052p+9 underflows to zero.
      if (aix >= 0x40874910d52d3052UL) {
        const double Tiny = 2.2250738585072014e-308; // 0x1p-1022
        return Tiny * Tiny;
      }
    }

    // x = (k/2^12) * log(2) + dx with a Cody-Waite reduction: l2h has 29 bits and |t| < 2^23, so
    // l2h * t is exact.
    const double S = 5909.278887481194; // 0x1.71547652b82fep+12
    const double L2h = 0.00016922538588914904; // 0x1.62e42ffp-13
    const double L2l = 1.0256140314162804e-14; // 0x1.718432a1b0e26p-47
    var t = Math.Round(x * S);
    var jt = (long)t;
    var i0 = (int)((jt >> 6) & 0x3f);
    var i1 = (int)(jt & 0x3f);
    var ie = jt >> 12;
    var lo = new double[1];
    var th = ExpMulDD(_expT0High[i0], _expT0LowTuned[i0], _expT1High[i1], _expT1LowTuned[i1], lo);
    var tl = lo[0];
    var dx = (x - L2h * t) + L2l * t;
    var dx2 = dx * dx;

    // 1 + dx * (c0 + c1 dx + c2 dx^2 + c3 dx^3) approximates e^dx within 2^-76.173.
    const double C0 = 1.0; // 0x1p+0
    const double C1 = 0.5; // 0x1p-1
    const double C2 = 0.16666666674124284; // 0x1.55555557e54ffp-3
    const double C3 = 0.04166666665427057; // 0x1.55555553a12f4p-5
    var p = (C0 + dx * C1) + dx2 * (C2 + dx * C3);
    var fh = th;
    var tx = th * dx;
    var fl = tl + tx * p;
    const double Eps = 2.498747194400186e-19; // 0x1.27p-62
    if (bits > 0xc086232bdd7abcd2UL) {
      // x < -0x1.6232bdd7abcd2p+9: the result is subnormal.
      fh = ExpFastTwoSum(BitConverter.Int64BitsToDouble((1 - ie) << 52), fh, lo);
      fl += lo[0];
      var upper = fh + (fl + Eps);
      var lower = fh + (fl - Eps);
      if (upper != lower)
        return ExpAccurate(x);

      return ExpToSubnormal(lower);
    }

    var ub = fh + (fl + Eps);
    var lb = fh + (fl - Eps);
    if (ub != lb)
      return ExpAccurate(x);

    return ExpScale(lb, ie);
  }

  /// <summary>
  /// <c>e^x</c> in double-double arithmetic, for the arguments whose rounding the fast path leaves
  /// open; <c>|x| &gt; 2^-54</c>.
  /// </summary>
  private static double ExpAccurate(double x) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    const double S = 5909.278887481194; // 0x1.71547652b82fep+12
    var t = Math.Round(x * S);
    var jt = (long)t;
    var i0 = (int)((jt >> 6) & 0x3f);
    var i1 = (int)(jt & 0x3f);
    var ie = jt >> 12;
    var lo = new double[1];
    var th = ExpMulDD(_expT0High[i0], _expT0LowTuned[i0], _expT1High[i1], _expT1LowTuned[i1], lo);
    var tl = lo[0];

    // The Cody-Waite reduction in three parts.
    const double L2h = 0.00016922538588914904; // 0x1.62e42ffp-13
    const double L2l = 1.0256140314162804e-14; // 0x1.718432a1b0e26p-47
    const double L2ll = 3.2042720746546034e-31; // 0x1.9ff0342542fc3p-102
    var dx = x - L2h * t;
    var dxl = L2l * t;
    var dxll = L2ll * t + Math.FusedMultiplyAdd(L2l, t, -dxl);
    var dxh = ExpFastTwoSum(dx, dxl, lo);
    dxl = lo[0] + dxll;

    // e^dx - 1 = dx * Q(dx), and the product with 2^(k/2^12).
    var fh = ExpPolyDD(dxh, dxl, lo);
    var fl = lo[0];
    fh = ExpMulDD(dxh, dxl, fh, fl, lo);
    fl = lo[0];
    fh = ExpMulDD(fh, fl, th, tl, lo);
    fl = lo[0];
    fh = ExpFastSum(th, tl, fh, fl, lo);
    fl = lo[0];
    if (bits > 0xc086232bdd7abcd2UL) {
      // x < -0x1.6232bdd7abcd2p+9: add 2^(-1022 - ie), which rounds at the subnormal grid.
      fh = ExpFastTwoSum(BitConverter.Int64BitsToDouble((1 - ie) << 52), fh, lo);
      fl += lo[0];
      return ExpToSubnormal(fh + fl);
    }

    fh = ExpFastTwoSum(fh, fl, lo);
    fl = lo[0];

    // A low part within 4 units of a power of two leaves the rounding open: an exceptional case.
    var d = ((ulong)BitConverter.DoubleToInt64Bits(fl) + 4) & 0xfffffffffffffUL;
    if (d <= 4)
      fh = ExpDatabase(x, fh);

    return ExpScale(fh, ie);
  }

  /// <summary>
  /// The value of a polynomial with double-double coefficients at a double-double point, in Horner's
  /// scheme with the fused double-word multiply-add of Jeannerod, Joldes, Louvet and Muller; the low
  /// part goes into <c>low[0]</c>.
  /// </summary>
  private static double ExpPolyDD(double xh, double xl, double[] low) {
    var ch = _expAccurateHigh[_expAccurateHigh.Length - 1];
    var cl = _expAccurateLow[_expAccurateHigh.Length - 1];
    for (var i = _expAccurateHigh.Length - 2; i >= 0; --i) {
      var dh = Math.FusedMultiplyAdd(xh, ch, _expAccurateHigh[i]);
      var e = Math.FusedMultiplyAdd(xh, ch, _expAccurateHigh[i] - dh);
      var f = e + _expAccurateLow[i];
      var g = xh * cl + f;
      cl = xl * ch + g;
      ch = dh;
    }

    low[0] = cl;
    return ch;
  }

  /// <summary>
  /// The correctly rounded <c>e^x</c> of the arguments the accurate path rounds wrongly, or
  /// <paramref name="f"/> for any other.
  /// </summary>
  private static double ExpDatabase(double x, double f) {
    var a = 0;
    var b = _expDatabaseX.Length;
    while (a + 1 < b) {
      var m = (a + b) / 2;
      if (x < _expDatabaseX[m])
        b = m;
      else
        a = m;
    }

    return x == _expDatabaseX[a] ? _expDatabaseHigh[a] + _expDatabaseLow[a] : f;
  }

  /// <summary>
  /// <c>e^x</c> of <c>float</c>, correctly rounded: <c>2^(k/2^6)</c> from a table times a polynomial
  /// in the rest, in double; where the result is too close to the midpoint of two floats, a longer
  /// reduction and a polynomial of degree 6 decide.
  /// </summary>
  /// <param name="x">The exponent.</param>
  /// <returns><c>e^x</c>; <c>+0</c> below the subnormal range, <c>+∞</c> above the normal one.</returns>
  public static float ExpF(float x) {
    const double Iln2 = 1.4426950408889634; // 0x1.71547652b82fep+0
    const double Big = 105553116266496.0; // 0x1.8p46
    var bits = (uint)BitConverter.SingleToInt32Bits(x);
    double z = x;
    var a = Iln2 * z;
    var shifted = a + Big;
    var u = (ulong)BitConverter.DoubleToInt64Bits(shifted);
    var ux = bits << 1;
    if (ux > 0x8562e42eu || ux < 0x6f93813eu) {
      // |x| < 0x1.93813ep-16.
      if (ux < 0x6f93813eu)
        return (float)(1.0 + z * (1 + z * 0.5));

      if (ux >= 0xffu << 24) {
        if (ux > 0xffu << 24)
          return x + x;

        return bits >> 31 != 0 ? 0.0f : x;
      }

      // x < -0x1.9d1d8p+6: a subnormal result rounds the linear approximation right.
      if (bits > 0xc2ce8ec0u) {
        var y = 1.401298464324817e-45 + (z + 103.27892990343184) * 1.0108231726433641e-45;
        y = Math.Max(y, 3.503246160812043e-46);
        return (float)y;
      }

      // x > 0x1.62e42ep+6 overflows.
      if (bits >> 31 == 0 && bits > 0x42b17217u) {
        const float Huge = 1.7014118346046923e+38f; // 0x1p127
        return Huge * Huge;
      }
    }

    // The low 6 bits of the shifted sum index the table, the ones above form the exponent.
    var ia = Big - shifted;
    var h = a + ia;
    var sv = BitConverter.Int64BitsToDouble((long)((ulong)BitConverter.DoubleToInt64Bits(_expT0High[(int)(u & 0x3f)]) + ((u >> 6) << 52)));
    const double B0 = 1;
    const double B1 = 0.6931471805202393; // 0x1.62e42fef4c4e7p-1
    const double B2 = 0.2402288551437867; // 0x1.ebfd1b232f475p-3
    const double B3 = 0.05550459682799693; // 0x1.c6b19384ecd93p-5
    var h2 = h * h;
    var r = ((B0 + h * B1) + h2 * (B2 + h * B3)) * sv;
    var ub = (float)r;
    var lb = (float)(r - r * 1.45e-10);
    if (ub != lb) {
      const double Iln2h = 1.442695040255785; // 0x1.7154765p+0
      const double Iln2l = 6.331784189566044e-10; // 0x1.5c17f0bbbe88p-31
      const double C0 = 0.6931471805599453; // 0x1.62e42fefa39efp-1
      const double C1 = 0.24022650695910072; // 0x1.ebfbdff82c58fp-3
      const double C2 = 0.05550410866402609; // 0x1.c6b08d702e0edp-5
      const double C3 = 0.009618129107500536; // 0x1.3b2ab6fb92e5ep-7
      const double C4 = 0.001333362331326638; // 0x1.5d886e6d54203p-10
      const double C5 = 0.00015403602972146417; // 0x1.430976b8ce6efp-13
      h = (Iln2h * z + ia) + Iln2l * z;
      var s = sv;
      h2 = h * h;
      var w = s * h;
      r = s + w * ((C0 + h * C1) + h2 * ((C2 + h * C3) + h2 * (C4 + h * C5)));
      ub = (float)r;
    }

    return ub;
  }

  // The double-double 2^(i/2^6) and 2^(i/2^12) of exp: the high parts are those of _expT0High and
  // _expT1High, some low parts are moved off the nearest value to leave fewer exceptional cases.
  private static readonly double[] _expT0LowTuned = [
    0.0, -1.5234778603368574e-17, 5.1092250289734445e-17, 7.600838874027085e-18,
    8.551889705537965e-17, 1.7593257387720928e-18, -7.899853966841582e-17, -6.656660436056593e-17,
    -3.046782079812471e-17, 5.2660368715706944e-17, 1.0410278456845571e-16, 5.1658567587954574e-17,
    8.912812676025407e-17, 3.250710218863828e-17, 3.8292048369240935e-17, 5.554203254218078e-17,
    3.982015231465646e-17, 6.644981499252301e-17, -7.712630692681486e-17, -1.8987816313025275e-17,
    4.6580275918369356e-17, -6.7113898212968784e-18, 2.667932131342186e-18, 1.713594918243561e-17,
    2.5382502794888315e-17, -7.181536135519454e-17, -2.85873121003886e-17, 8.927282594831731e-17,
    7.700948379802988e-17, 9.593797919118849e-17, -6.770511658794786e-17, -9.614213209051323e-17,
    -9.667293313452913e-17, -1.2031642489053654e-17, -3.023758134993988e-17, -5.600377186075217e-17,
    -3.4839945568927964e-17, 1.4192920154284036e-17, -1.016455327754295e-16, -1.1024941712342561e-16,
    7.949834809697623e-17, 3.7812070533575294e-17, -1.0136916471278304e-17, -1.0094406542311962e-16,
    2.4707192569797888e-17, -6.712955084707083e-17, -1.0125679913674775e-16, 5.890992696713101e-17,
    8.199010020581498e-17, -8.0237193703977e-18, -1.851380418263111e-17, 3.164389299292957e-17,
    2.960140695448874e-17, 6.429731796556572e-17, 1.822745842791208e-17, -9.969531538920349e-17,
    3.2831072242456266e-17, 9.761887490727595e-17, -6.122763413004143e-17, 3.40340353521653e-17,
    -1.0619946056195964e-16, 1.0332385960676327e-16, 8.96076779103667e-17, 4.038875310927817e-17
  ];
  private static readonly double[] _expT1LowTuned = [
    0.0, 9.336185335478464e-17, -5.141333931318957e-18, 6.962424022020571e-17,
    -5.115123297685667e-17, 8.422990024586488e-17, -2.8245220747761684e-17, -7.180424565592133e-17,
    -1.8973728416792996e-17, 9.06044106726912e-17, -7.173276349900319e-17, -1.3307196246722662e-17,
    2.5726925943221133e-17, -3.929937785484514e-17, 8.461377247994717e-17, -4.1948832416399384e-17,
    -3.6366159286922646e-17, -2.610944063243938e-17, 1.7530784779823324e-17, 5.753923525628267e-17,
    -8.684922005117958e-18, 9.490035430981776e-17, -8.710380605818421e-17, 3.4958916958571545e-17,
    9.753787549840241e-17, -1.0576221196292857e-16, 4.209188738127126e-17, -1.6700166857554785e-17,
    -1.6231463554124514e-17, 2.3028539278028117e-17, 1.6418046976773032e-17, 3.726698431828413e-17,
    9.499186535455033e-17, -8.68093131444458e-17, 4.0005474910301175e-17, 7.190499111509974e-17,
    -1.3908068671065786e-17, -8.14020864257305e-17, -5.762151043749534e-17, 6.745278477310456e-17,
    1.8998557240346293e-17, -9.637430032316406e-17, -1.2528654462453979e-17, 3.020578887843696e-17,
    -4.8693942586085655e-17, 5.224029937687454e-17, -9.361543551478456e-17, -8.65251323306195e-17,
    -3.252058756084309e-17, -9.917232268060917e-17, -7.136047404162522e-17, -1.7268683712243214e-17,
    -6.619954693673941e-17, 3.56545690151302e-17, 3.717310013708818e-17, 7.062572406825526e-17,
    -1.432141230342882e-17, 1.5668188013134103e-17, -1.1043695780393691e-16, -5.767317427160399e-17,
    4.835484978440381e-18, 7.015121289715443e-17, 7.161802873619573e-17, 1.050465913408405e-16
  ];

  // The coefficients of degree 1 to 7 of e^x - 1 on |x| < 2^-13.528, as double-double numbers.
  private static readonly double[] _expAccurateHigh = [
    1.0, 0.5, 0.16666666666666666, 0.041666666666666664,
    0.008333333333333333, 0.0013888888892440137, 0.0001984126983733902
  ];
  private static readonly double[] _expAccurateLow = [
    0.0, 2.2752803697148587e-30, 9.251858538539695e-18, 2.311375602431149e-18,
    1.1645814375470794e-19, -7.022792706270539e-20, -1.0602991841308864e-20
  ];

  // The arguments, sorted, for which the accurate path does not round right, and their e^x as the
  // sum of two doubles.
  private static readonly double[] _expDatabaseX = [
    -24.96896403739838, -7.421123488171717, -0.25769183680034063, -0.11595781086814885,
    -0.1037516737020303, -0.019571676994419418, -0.0001997928992905134, -8.542005785074093e-05,
    -3.78359350122444e-09, -1.8593351875450545e-09, -1.4184426421272212e-09, -6.03594674424366e-10,
    1.1102230246251565e-16, 2.9103719434007728e-11, 9.023326530003611e-11, 5.843446835809514e-10,
    1.8367444215357543e-09, 1.1033070105112662e-06, 1.809480788007787e-06, 0.005528277479060492,
    1.6744336219614115
  ];
  private static readonly double[] _expDatabaseHigh = [
    1.9689130580912089, 1.2256796396039498, 1.5456667084280928, 1.7810255849331564,
    1.8028982463054148, 1.9612372097506556, 1.9996004541159633, 1.9998291671806772,
    0.9999999962164065, 0.9999999981406648, 0.9999999985815574, 0.9999999993964054,
    1.0000000000000002, 1.0000000000291036, 1.0000000000902334, 1.0000000005843448,
    1.0000000018367443, 1.0000011033076193, 1.000001809482425, 1.0055435866030302,
    1.3339430572217077
  ];
  private static readonly double[] _expDatabaseLow = [
    -1.3533717516041823e-32, -2.8836527279112737e-32, 1.1102230246251563e-16, -3.594938872459226e-33,
    1.1102230246251564e-16, 1.1102230246251562e-16, 5.80132046194103e-34, -1.1102230246251564e-16,
    5.551115123125782e-17, 5.551115123125782e-17, -5.551115123125782e-17, -5.551115123125782e-17,
    -1.1102230246251564e-16, 1.1102230246251564e-16, -1.1102230246251564e-16, -1.1102230246251564e-16,
    1.1102230246251564e-16, -1.1102230246251564e-16, 1.1102230246251563e-16, 1.1102230246251563e-16,
    -1.1102230246251564e-16
  ];

}
