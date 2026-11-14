using System;

namespace Polypiler.Core.IR;

/// <summary>
/// <c>10^x</c>, converted from CORE-MATH's <c>src/binary64/exp10/exp10.c</c> and
/// <c>src/binary32/exp10/exp10f.c</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>10^x</c>, correctly rounded: <c>x * log2(10)</c> is split into <c>k/2^12 + r</c>,
  /// <c>2^(k/2^12)</c> comes from two tables and <c>10^dx</c> from a polynomial; an error bound
  /// decides the rounding, and where it does not, a double-double evaluation does. An integer
  /// <c>x</c> from 0 to 22 gives the exact power.
  /// </summary>
  /// <param name="x">The exponent.</param>
  /// <returns><c>10^x</c>; <c>+0</c> below the subnormal range, <c>+∞</c> above the normal one.</returns>
  public static double Exp10(double x) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    var aix = bits & 0x7fffffffffffffffUL;
    if (aix > 0x40734413509f79feUL) {
      // |x| > 0x1.34413509f79fep+8.
      if (aix > 0x7ff0000000000000UL)
        return x + x;

      if (aix == 0x7ff0000000000000UL)
        return bits >> 63 != 0 ? 0.0 : x;

      if (bits >> 63 == 0)
        return 8.98846567431158e+307 * 2.0;

      // x < -0x1.439b746e36b52p+8 underflows to zero.
      if (aix > 0x407439b746e36b52UL)
        return 3.337610787760802e-308 * 2.7755575615628914e-17;
    }

    // An integer x from 0 to 22: 10^x is exact.
    if (bits << 16 == 0 && aix >> 48 <= 0x4036) {
      var kx = Math.Round(x);
      if (kx == x && kx >= 0) {
        var r = 1.0;
        for (var i = 0L; i < (long)kx; i++)
          r *= 10.0;

        return r;
      }
    }

    // 10^x rounds to 1 for |x| <= 0x1.bcb7b1526e50ep-56.
    if (aix <= 0x3c7bcb7b1526e50eUL)
      return 1.0 + x;

    var t = Math.Round(13606.617476658635 * x);
    var jt = (long)t;
    var i1 = (int)(jt & 0x3f);
    var i0 = (int)((jt >> 6) & 0x3f);
    var ie = jt >> 12;
    var lo = new double[1];
    var th = ExpMulDD(_expT0High[i0], _expT0Low[i0], _expT1High[i1], _expT1Low[i1], lo);
    var tl = lo[0];
    const double L0 = 7.349365125719487e-05; // 0x1.34413508p-14
    const double L1 = 2.795678930181113e-14; // 0x1.f79fef311f12bp-46
    var dx = (x - L0 * t) - L1 * t;
    var dx2 = dx * dx;
    const double C0 = 2.302585092994046; // 0x1.26bb1bbb55516p+1
    const double C1 = 2.650949055239199; // 0x1.53524c73cea69p+1
    const double C2 = 2.0346785932039073; // 0x1.0470591fd74e1p+1
    const double C3 = 1.171255149401227; // 0x1.2bd760a1f32a5p+0
    var p = (C0 + dx * C1) + dx2 * (C2 + dx * C3);
    var fh = th;
    var fx = th * dx;
    var fl = tl + fx * p;
    const double Eps = 2.17e-19;
    if (bits < 0xc0733a7146f72a42UL) {
      // x > -0x1.33a7146f72a42p+8: the result is normal.
      var ub = fh + (fl + Eps);
      var lb = fh + (fl - Eps);
      if (lb != ub)
        return Exp10Accurate(x);

      return ExpScale(fh + fl, ie);
    }

    // 10^x < 2^-1022: add 2^(-1022 - ie), which rounds at the subnormal grid.
    fh = ExpFastTwoSum(BitConverter.Int64BitsToDouble((1 - ie) << 52), fh, lo);
    fl += lo[0];
    var lower = fh + (fl - Eps);
    var upper = fh + (fl + Eps);
    if (lower != upper)
      return Exp10Accurate(x);

    return ExpToSubnormal(fh + fl);
  }

  /// <summary><c>10^x</c> in double-double arithmetic, for the arguments whose rounding the fast path leaves open.</summary>
  private static double Exp10Accurate(double x) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    var t = Math.Round(13606.617476658635 * x);
    var jt = (long)t;
    var i1 = (int)(jt & 0x3f);
    var i0 = (int)((jt >> 6) & 0x3f);
    var ie = jt >> 12;
    var lo = new double[1];
    var th = ExpMulDD(_expT0High[i0], _expT0Low[i0], _expT1High[i1], _expT1Low[i1], lo);
    var tl = lo[0];
    const double L0 = 7.349365125719487e-05; // 0x1.34413508p-14
    const double L1 = -2.795678930181113e-14; // -0x1.f79fef311f12bp-46
    const double L2 = -6.5950754538526695e-31; // -0x1.ac0b7c917826bp-101
    var dx = x - L0 * t;
    var dxl = L1 * t;
    var dxll = L2 * t + Math.FusedMultiplyAdd(L1, t, -dxl);
    var dxh = dx + dxl;
    dxl = ((dx - dxh) + dxl) + dxll;
    var fh = Exp10PolyDD(dxh, dxl, lo);
    var fl = lo[0];
    fh = ExpMulDD(dxh, dxl, fh, fl, lo);
    fl = lo[0];
    if (bits < 0xc0733a7146f72a42UL) {
      if ((jt & 0xfff) == 0) {
        // 2^(k/2^12) is a power of two: a result next to one is moved one unit towards the error.
        fh = ExpFastTwoSum(fh, fl, lo);
        fl = lo[0];
        th = ExpFastTwoSum(th, fh, lo);
        fh = lo[0];
        fh = ExpFastTwoSum(fh, fl, lo);
        fl = lo[0];
        var ix = BitConverter.DoubleToInt64Bits(fh);
        if (ix << 12 == 0) {
          var sfh = (ix >> 63) ^ (BitConverter.DoubleToInt64Bits(fl) >> 63);
          ix += (1L << 51) ^ sfh;
        }

        fh = th + BitConverter.Int64BitsToDouble(ix);
      } else {
        fh = ExpMulDD(fh, fl, th, tl, lo);
        fl = lo[0];
        fh = ExpFastSum(th, tl, fh, fl, lo);
        fl = lo[0];
        fh = ExpFastTwoSum(fh, fl, lo);

        // A low part near 0, 1/2 or 1 unit, or tiny, leaves the rounding open.
        var low = (ulong)BitConverter.DoubleToInt64Bits(lo[0]);
        if (((low + 4) & 0xfffffffffffffUL) <= 4 || ((low >> 52) & 0x7ff) < 918)
          fh = Exp10Database(x, fh);
      }

      return ExpScale(fh, ie);
    }

    fh = ExpMulDD(fh, fl, th, tl, lo);
    fl = lo[0];
    fh = ExpFastSum(th, tl, fh, fl, lo);
    fl = lo[0];
    fh = ExpFastTwoSum(BitConverter.Int64BitsToDouble((1 - ie) << 52), fh, lo);
    fl += lo[0];
    return ExpToSubnormal(fh + fl);
  }

  /// <summary>
  /// The value of a polynomial with double-double coefficients at a double-double point, in Horner's
  /// scheme; the low part goes into <c>low[0]</c>.
  /// </summary>
  private static double Exp10PolyDD(double xh, double xl, double[] low) {
    var ch = _exp10AccurateHigh[_exp10AccurateHigh.Length - 1];
    var cl = _exp10AccurateLow[_exp10AccurateHigh.Length - 1];
    for (var i = _exp10AccurateHigh.Length - 2; i >= 0; --i) {
      ch = ExpMulDD(xh, xl, ch, cl, low);
      cl = low[0];
      var th = ch + _exp10AccurateHigh[i];
      var tl = (_exp10AccurateHigh[i] - th) + ch;
      ch = th;
      cl += tl + _exp10AccurateLow[i];
    }

    low[0] = cl;
    return ch;
  }

  /// <summary>
  /// The correctly rounded <c>10^x</c> of the arguments the accurate path rounds wrongly, or
  /// <paramref name="f"/> for any other; the list is laid out as the one of <see cref="Exp2Database"/>.
  /// </summary>
  private static double Exp10Database(double x, double f) {
    const ulong Sign = 371470981966157;
    var key = (ulong)BitConverter.DoubleToInt64Bits(x);
    var a = 0;
    var b = _exp10Database.Length - 1;
    var m = (a + b) / 2;
    while (a <= b) {
      var t = (ulong)BitConverter.DoubleToInt64Bits(_exp10Database[m]);
      if (t < key) {
        a = m + 1;
      } else if (t == key) {
        var d = BitConverter.Int64BitsToDouble((long)(((Sign >> m) & 1) << 63 | 0x3c90000000000000UL));
        var p = (_exp10DatabaseLast[m >> 5] >> (2 * (m & 31))) & 3;
        var r = BitConverter.DoubleToInt64Bits(f) - 1;
        while ((ulong)(r & 3) != p)
          ++r;

        return BitConverter.Int64BitsToDouble(r) + d;
      } else {
        b = m - 1;
      }

      m = (a + b) / 2;
    }

    return f;
  }

  /// <summary>
  /// <c>10^x</c> of <c>float</c>, correctly rounded: <c>2^(k/2^5)</c> from a table times a polynomial
  /// in the rest; where the result is too close to the midpoint of two floats, a longer reduction and
  /// a polynomial of degree 6 decide. The exact powers from 10 to <c>10^10</c> come from a table.
  /// </summary>
  /// <param name="x">The exponent.</param>
  /// <returns><c>10^x</c>; <c>+0</c> below the subnormal range, <c>+∞</c> above the normal one.</returns>
  public static float Exp10F(float x) {
    const double Iln102 = 106.30169903639559; // 0x1.a934f0979a371p+6
    const double Iln102h = 3.3219280913472176; // 0x1.a934f09p+1
    const double Iln102l = 3.5401447880558664e-09; // 0x1.e68dc57f2496p-29
    var bits = (uint)BitConverter.SingleToInt32Bits(x);
    double z = x;
    var ux = bits << 1;
    if (ux > 0x84344134u || ux < 0x72adf1c6u) {
      // |x| < 0x1.adf1c6p-13.
      if (ux < 0x72adf1c6u)
        return (float)(1.0 + z * (2.302585092994046 + z * (2.650949055239199 + z * 2.034678592293476)));

      if (ux >= 0xffu << 24) {
        if (ux > 0xffu << 24)
          return x + x;

        return bits >> 31 != 0 ? 0.0f : x;
      }

      // x < -0x1.66d3e8p+5: a subnormal result rounds the linear approximation right.
      if (bits > 0xc23369f4u) {
        var y = 1.401298464324817e-45 + (z + 44.8534693539332) * 2.3275063689815626e-45;
        y = Math.Max(y, 3.503246160812043e-46);
        return (float)y;
      }

      // x > 0x1.344134p+5 overflows.
      if (bits < 0x80000000u) {
        const float Huge = 1.7014118346046923e+38f; // 0x1p127
        return Huge * Huge;
      }
    }

    // The integers 1 to 10, whose bits end in 12 zeros: the mask has a bit for each of their
    // exponent-and-leading-bits patterns.
    if (bits << 12 == 0) {
      var k = (bits >> 20) - 1016u;
      if (k <= 26) {
        var bt = 1u << (int)k;
        const uint Msk = 0x7551101;
        if ((bt & Msk) != 0)
          return _exp10FPowers[System.Numerics.BitOperations.PopCount(Msk & (bt - 1))];
      }
    }

    var a = Iln102 * z;
    var ia = Math.Round(a);
    var h = a - ia;
    var ja = (long)ia;
    var sv = BitConverter.Int64BitsToDouble((long)((ulong)BitConverter.DoubleToInt64Bits(_expT0High[2 * (int)(ja & 0x1f)]) + ((ulong)(ja >> 5) << 52)));
    const double B0 = 1;
    const double B1 = 0.021660849391257477; // 0x1.62e42fef4c4e7p-6
    const double B2 = 0.0002345984913513542; // 0x1.ebfd1b232f475p-13
    const double B3 = 1.6938658699950235e-06; // 0x1.c6b19384ecd93p-20
    var h2 = h * h;
    var r = ((B0 + h * B1) + h2 * (B2 + h * B3)) * sv;
    var ub = (float)r;
    var lb = (float)(r - r * 1.45e-10);
    if (ub != lb) {
      const double C0 = 0.6931471805599453; // 0x1.62e42fefa39efp-1
      const double C1 = 0.24022650695910072; // 0x1.ebfbdff82c58fp-3
      const double C2 = 0.05550410866402609; // 0x1.c6b08d702e0edp-5
      const double C3 = 0.009618129107500536; // 0x1.3b2ab6fb92e5ep-7
      const double C4 = 0.001333362331326638; // 0x1.5d886e6d54203p-10
      const double C5 = 0.00015403602972146417; // 0x1.430976b8ce6efp-13
      h = (Iln102h * z - ia * 0.03125) + Iln102l * z;
      var s = sv;
      h2 = h * h;
      var w = s * h;
      r = s + w * ((C0 + h * C1) + h2 * ((C2 + h * C3) + h2 * (C4 + h * C5)));
      ub = (float)r;
    }

    return ub;
  }

  // The coefficients of 10^x - 1 = x * P(x), degree 1 to 6, as double-double numbers.
  private static readonly double[] _exp10AccurateHigh = [
    2.302585092994046, 2.650949055239199, 2.034678592293476, 1.171255148912267,
    0.5393829293564817, 0.20699584875639732
  ];
  private static readonly double[] _exp10AccurateLow = [
    -2.1707562233820662e-16, -2.0935887830501475e-16, 1.673822301795714e-16, 6.627676914237881e-17,
    4.348239792565992e-17, -7.098878836043966e-18
  ];

  // The arguments for which the accurate path does not round right, sorted by their bits, and two
  // bits for each, which of the numbers next to the accurate result to correct.
  private static readonly double[] _exp10Database = [
    0.0007364605311275651, 0.0014505068037747198, 0.0015977584647815055, 0.007513367657211573,
    0.012232686966359051, 0.02332985862011024, 0.03582303488601718, 0.05012845490368938,
    0.05953461944860893, 0.2684332529078538, 0.4414850852827455, 0.46076371772845026,
    0.5623012489460103, 0.6604966849318432, 1.1159604077800511, 1.4607637177284503,
    1.6397630472104643, 1.797618453838866, 3.863966482921384, 4.708287458043986,
    24.56401351820388, 74.58761661405285, 230.94426408279878, -3.8846252234572564e-05,
    -3.913983282567077e-05, -4.256052144087184e-05, -4.691376711863881e-05, -6.656924445567075e-05,
    -0.00011539663640418542, -0.00018048137861368554, -0.0003393854240296026, -0.0003983436214502109,
    -0.00045885361041403806, -0.0006341499939259991, -0.0009124905646964254, -0.001875655562522814,
    -0.007674779510465974, -0.013314973137858817, -0.019261606163466862, -0.02118803680797971,
    -0.10395724288069548, -0.22039948306237964, -0.3349578431886918, -0.4376987510539897,
    -0.5392362822715497, -0.9091908472231233, -6.661465901292746, -20.80192488353596,
    -118.588931447397
  ];
  private static readonly ulong[] _exp10DatabaseLast = [
    0x7eb37ef5ac3fe7c6UL, 0x00000003781b19e1UL
  ];

  // 10^1 to 10^10, exact as floats.
  private static readonly float[] _exp10FPowers = [
    10.0f, 100.0f, 1000.0f, 10000.0f,
    100000.0f, 1000000.0f, 10000000.0f, 100000000.0f,
    1000000000.0f, 10000000000.0f
  ];

}
