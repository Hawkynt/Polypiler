// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// <c>2^x</c>, converted from CORE-MATH's <c>src/binary64/exp2/exp2.c</c> and
/// <c>src/binary32/exp2/exp2f.c</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>2^x</c>, correctly rounded: <c>x</c> is split into <c>k/2^12 + z</c>, <c>2^(k/2^12)</c> comes
  /// from two tables and <c>2^z</c> from a polynomial; an error bound decides the rounding, and where
  /// it does not, a double-double evaluation does. An integer <c>x</c> gives the exact power.
  /// </summary>
  /// <param name="x">The exponent.</param>
  /// <returns><c>2^x</c>; <c>+0</c> below the subnormal range, <c>+∞</c> from 1024 on.</returns>
  public static double Exp2(double x) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    var ax = bits << 1;
    if (ax == 0)
      return 1.0;

    // |x| >= 1024.
    if (ax >= 0x8120000000000000UL) {
      if (ax > 0xffe0000000000000UL)
        return x + x;

      if (ax == 0xffe0000000000000UL)
        return bits >> 63 != 0 ? 0.0 : x;

      if (bits >> 63 == 0)
        return 8.98846567431158e+307 * x;

      // x <= -1075 underflows to zero.
      if (bits >= 0xc090cc0000000000UL) {
        const double Tiny = 2.2250738585072014e-308; // 0x1p-1022
        return Tiny * Tiny;
      }
    }

    // 2^x rounds to 1 for |x| <= 0x1.71547652b82fep-54.
    if (ax <= 0x792e2a8eca5705fcUL)
      return 1.0 + CopySign(5.551115123125783e-17, x);

    // frac is zero exactly when x is an integer.
    var m = bits << 12;
    var ex = (ax >> 53) - 0x3ff;
    var frac = ex >> 63 | m << (int)(ex & 63);
    var sx = 4096.0 * x;
    var fx = Math.Round(sx);
    var z = sx - fx;
    var z2 = z * z;
    var k = (long)fx;
    var i1 = (int)(k & 0x3f);
    var i0 = (int)((k >> 6) & 0x3f);
    var ie = k >> 12;
    var lo = new double[1];
    var th = ExpMulDD(_expT0High[i0], _expT0Low[i0], _expT1High[i1], _expT1Low[i1], lo);
    var tl = lo[0];
    const double C0 = 0.0001692253858788929; // 0x1.62e42fefa39efp-13
    const double C1 = 1.4318615612930102e-08; // 0x1.ebfbdff82c58fp-27
    const double C2 = 8.076910844779527e-13; // 0x1.c6b08d73b3e01p-41
    const double C3 = 3.417045885940598e-17; // 0x1.3b2ab6fdda001p-55
    var tz = th * z;
    var fh = th;
    var fl = tz * ((C0 + z * C1) + z2 * (C2 + z * C3)) + tl;
    const double Eps = 2.1556988507621944e-19; // 0x1.fdp-63
    if (bits <= 0xc08ff00000000000UL) {
      // x >= -1022: the result is normal.
      if (frac != 0) {
        var ub = fh + (fl + Eps);
        fh += fl - Eps;
        if (ub != fh)
          return Exp2Accurate(x);
      }

      return ExpScale(fh, ie);
    }

    // A subnormal result: add 2^(-1022 - ie), which rounds at the subnormal grid.
    fh = ExpFastTwoSum(BitConverter.Int64BitsToDouble((1 - ie) << 52), fh, lo);
    fl += lo[0];
    if (frac != 0) {
      var ub = fh + (fl + Eps);
      fh += fl - Eps;
      if (ub != fh)
        return Exp2Accurate(x);
    }

    return ExpToSubnormal(fh);
  }

  /// <summary><c>2^x</c> in double-double arithmetic, for the arguments whose rounding the fast path leaves open.</summary>
  private static double Exp2Accurate(double x) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    var sx = 4096.0 * x;
    var fx = Math.Round(sx);
    var z = sx - fx;
    var k = (long)fx;
    var i1 = (int)(k & 0x3f);
    var i0 = (int)((k >> 6) & 0x3f);
    var ie = k >> 12;
    var lo = new double[1];
    var th = ExpMulDD(_expT0High[i0], _expT0Low[i0], _expT1High[i1], _expT1Low[i1], lo);
    var tl = lo[0];
    var fh = Exp2PolyDD(z, lo);
    var fl = lo[0];
    fh = ExpMulDDD(z, fh, fl, lo);
    fl = lo[0];
    if (bits <= 0xc08ff00000000000UL) {
      // x >= -1022. For -0x1.71547652b82fep-54 <= x <= 0x1.71547652b82fdp-53, 2^x rounds to 1 + x/2.
      if (-8.008566259537294e-17 <= x && x <= 1.6017132519074586e-16)
        return Math.FusedMultiplyAdd(x, 0.5, 1.0);

      if ((k & 0xfff) == 0) {
        // 4096 x rounds to a multiple of 4096: 2^(k/2^12) is exact.
        fh = ExpFastTwoSum(th, fh, lo);
        var e = lo[0];
        fl = ExpFastTwoSum(e, fl, lo);
        e = lo[0];
        var flBits = BitConverter.DoubleToInt64Bits(fl);
        if ((flBits & 0xfffffffffffffL) == 0) {
          // A power of two: move it one unit towards the remaining error.
          var d = ((flBits ^ BitConverter.DoubleToInt64Bits(e)) >> 63) * 2 + 1;
          fl = BitConverter.Int64BitsToDouble(flBits + d);
        }
      } else {
        fh = ExpMulDD(fh, fl, th, tl, lo);
        fl = lo[0];
        fh = ExpFastSum(th, tl, fh, fl, lo);
        fl = lo[0];
      }

      fh = ExpFastTwoSum(fh, fl, lo);
      fl = lo[0];
      var near = ((ulong)BitConverter.DoubleToInt64Bits(fl) + 2) & 0xfffffffffffffUL;
      if (near <= 2)
        fh = Exp2Database(x, fh);

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
  /// The value of a polynomial with double-double coefficients at a double, in Horner's scheme; the
  /// low part goes into <c>low[0]</c>.
  /// </summary>
  private static double Exp2PolyDD(double xh, double[] low) {
    var ch = _exp2AccurateHigh[_exp2AccurateHigh.Length - 1];
    var cl = _exp2AccurateLow[_exp2AccurateHigh.Length - 1];
    for (var i = _exp2AccurateHigh.Length - 2; i >= 0; --i) {
      ch = ExpMulDDD(xh, ch, cl, low);
      cl = low[0];
      var th = ch + _exp2AccurateHigh[i];
      var tl = (_exp2AccurateHigh[i] - th) + ch;
      ch = th;
      cl += tl + _exp2AccurateLow[i];
    }

    low[0] = cl;
    return ch;
  }

  /// <summary>
  /// The correctly rounded <c>2^x</c> of the arguments the accurate path rounds wrongly, or
  /// <paramref name="f"/> for any other. The list is sorted by the bits of its entries; for each, two
  /// bits say which of the three numbers next to <paramref name="f"/> the result is near, and one
  /// the side of the correction.
  /// </summary>
  private static double Exp2Database(double x, double f) {
    const ulong Sign = 8677191773140;
    var key = (ulong)BitConverter.DoubleToInt64Bits(x);
    var a = 0;
    var b = _exp2Database.Length - 1;
    var m = (a + b) / 2;
    while (a <= b) {
      var t = (ulong)BitConverter.DoubleToInt64Bits(_exp2Database[m]);
      if (t < key) {
        a = m + 1;
      } else if (t == key) {
        var p = (_exp2DatabaseLast[m >> 5] >> ((m * 2) & 63)) & 3;
        var dy = BitConverter.Int64BitsToDouble((long)((0x3c90 | ((Sign >> m) << 15)) << 48));
        var y = BitConverter.DoubleToInt64Bits(f) - 1;
        while ((ulong)(y & 3) != p)
          ++y;

        return BitConverter.Int64BitsToDouble(y) + dy;
      } else {
        b = m - 1;
      }

      m = (a + b) / 2;
    }

    return f;
  }

  /// <summary>
  /// <c>2^x</c> of <c>float</c>, correctly rounded: <c>2^(k/2^6)</c> from a table times a polynomial
  /// in the rest; where the result is too close to the midpoint of two floats, a polynomial of
  /// degree 6 decides. An integer <c>x</c> gives the exact power.
  /// </summary>
  /// <param name="x">The exponent.</param>
  /// <returns><c>2^x</c>; <c>+0</c> below the subnormal range, <c>+∞</c> from 128 on.</returns>
  public static float Exp2F(float x) {
    var bits = (uint)BitConverter.SingleToInt32Bits(x);
    if ((bits & 0xffff) == 0) {
      // An integer x with 1 <= |x| < 2^9 gives the power exactly, also a subnormal one.
      var k = (int)((bits >> 23) & 0xff) - 127;
      if (k >= 0 && k < 9 && bits << (9 + k) == 0) {
        var msk = (int)bits >> 31;
        var m = (int)(((bits & 0x7fffff) | (1u << 23)) >> (23 - k));
        m = (m ^ msk) - msk + 127;
        if (m > 0 && m < 255)
          return BitConverter.Int32BitsToSingle(m << 23);

        if (m <= 0 && m > -23)
          return BitConverter.Int32BitsToSingle(1 << (22 + m));
      }
    }

    // |x| >= 128, a NaN, or |x| < 2^-26.
    var ux = bits << 1;
    if (ux >= 0x86000000u || ux < 0x65000000u) {
      if (ux < 0x65000000u)
        return 1.0f + x;

      if (!(bits >= 0xc3000000u && bits < 0xc3150000u))
        return Exp2Special(x);
    }

    const double Offd = 105553116266496.0; // 0x1.8p46
    double xd = x;
    var h = xd - ((xd + Offd) - Offd);
    var h2 = h * h;
    var u = (uint)BitConverter.SingleToInt32Bits(x + 196608.0f);
    var sv = BitConverter.Int64BitsToDouble((long)((ulong)BitConverter.DoubleToInt64Bits(_expT0High[(int)(u & 0x3f)]) + ((ulong)(u >> 6) << 52)));
    const double B0 = 1;
    const double B1 = 0.6931471805202393; // 0x1.62e42fef4c4e7p-1
    const double B2 = 0.2402288551437867; // 0x1.ebfd1b232f475p-3
    const double B3 = 0.05550459682799693; // 0x1.c6b19384ecd93p-5
    const double Eps = 1.4438228390645236e-10; // 0x1.3d8p-33
    var r = sv * ((B0 + h * B1) + h2 * (B2 + h * B3));
    var ub = (float)r;
    var lb = (float)(r - r * Eps);
    if (ub != lb) {
      if (ux <= 0x79e7526eu) {
        if (bits == 0x3b429d37u)
          return 1.0020605325698853f - 2.9802322387695312e-08f;

        if (bits == 0xbcf3a937u)
          return 0.9795942902565002f - 1.4901161193847656e-08f;

        if (bits == 0xb8d3d026u)
          return 0.9999299645423889f + 1.4901161193847656e-08f;
      }

      const double C0 = 0.6931471805599453; // 0x1.62e42fefa39efp-1
      const double C1 = 0.24022650695910072; // 0x1.ebfbdff82c58fp-3
      const double C2 = 0.05550410866402609; // 0x1.c6b08d702e0edp-5
      const double C3 = 0.009618129107500536; // 0x1.3b2ab6fb92e5ep-7
      const double C4 = 0.001333362331326638; // 0x1.5d886e6d54203p-10
      const double C5 = 0.00015403602972146417; // 0x1.430976b8ce6efp-13
      r = sv + (sv * h) * ((C0 + h * C1) + h2 * ((C2 + h * C3) + h2 * (C4 + h * C5)));
      ub = (float)r;
    }

    return ub;
  }

  /// <summary><c>2^x</c> of <c>float</c> for a NaN, an infinity, <c>x &lt; -149</c> and <c>x &gt;= 128</c>.</summary>
  private static float Exp2Special(float x) {
    var bits = (uint)BitConverter.SingleToInt32Bits(x);
    var ux = bits << 1;
    if (ux >= 0xffu << 24) {
      if (ux > 0xffu << 24)
        return x + x;

      return bits >> 31 != 0 ? 0.0f : x;
    }

    // x < -149: the linear approximation rounds to the right subnormal or zero.
    if (bits >= 0xc3150000u) {
      double z = x;
      var y = 1.401298464324817e-45 + (z + 149) * 7.006492321624085e-46;
      y = Math.Max(y, 3.503246160812043e-46);
      return (float)y;
    }

    const float Huge = 1.7014118346046923e+38f; // 0x1p127
    return Huge * Huge;
  }

  // The coefficients of 2^(z/2^12) - 1 = z * P(z), degree 1 to 6, as double-double numbers.
  private static readonly double[] _exp2AccurateHigh = [
    0.0001692253858788929, 1.4318615612930102e-08, 8.076910841165457e-13, 3.4170458845140945e-17,
    1.1565018170905508e-21, 3.261824437744861e-26
  ];
  private static readonly double[] _exp2AccurateLow = [
    5.66173538536829e-21, -5.658823998678428e-25, -4.611190128844e-29, 1.0045988996358037e-33,
    -8.74789726929187e-38, -1.8822354885763482e-42
  ];

  // The arguments for which the accurate path does not round right, sorted by their bits, and two
  // bits for each, which of the numbers next to the accurate result to correct.
  private static readonly double[] _exp2Database = [
    0.0018476455676928627, 0.00185919233062784, 0.00284846580791962, 0.0028763223538210525,
    0.007714926958388578, 0.024231918711723883, 0.08770224256779562, 0.09578891800370937,
    0.12355887769708004, 0.1778592311907721, 0.19649005786728485, 0.6214979415205624,
    0.8499330453128527, 1.6214979415205624, -0.00015437939628496522, -0.0001790952260489193,
    -0.00018180307074928715, -0.00038686796803203853, -0.00039076062694585036, -0.0005263730620179473,
    -0.0006883713813297973, -0.0007454354460954939, -0.001045424250995566, -0.0012160551746951969,
    -0.0025002480913021924, -0.0030030613364612027, -0.003376149446912915, -0.00416730405122838,
    -0.004390090261576362, -0.005659344016099413, -0.007506895627101766, -0.00805569542034636,
    -0.01777907660294222, -0.018912661424181635, -0.041311682156002484, -0.07779822632567704,
    -0.15006695468714726, -0.22876988070583898, -0.24928514095356566, -0.37850205847943763,
    -0.47095680395564515, -0.9042110819962906, -1.3785020584794376
  ];
  private static readonly ulong[] _exp2DatabaseLast = [
    0x3b216fbd5fd7665fUL, 0x000000000034c797UL
  ];

}
