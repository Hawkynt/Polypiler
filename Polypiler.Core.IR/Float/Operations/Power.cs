// SPDX-License-Identifier: MIT
using System;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>
/// The power functions: <c>pow</c> of IEEE 754-2019 §9.2 with the special cases of C99 Annex F, the
/// one of ECMA-262 (<c>Math.pow</c> and <c>**</c>), and <c>rootn</c>. <see cref="Pow"/> is converted
/// from CORE-MATH's <c>binary64/pow/pow.c</c> with <c>pow.h</c>, <c>dint.h</c> and <c>qint.h</c>
/// (Tom Hubrecht and Paul Zimmermann, MIT), <see cref="PowF"/> from CORE-MATH's
/// <c>binary32/pow/powf.c</c> (Alexei Sibidanov and Paul Zimmermann, MIT). Both are correctly
/// rounded. See <c>THIRD-PARTY-NOTICE.CORE-MATH.Power.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>x^y</c>, correctly rounded, with the special cases of IEEE 754-2019 §9.2.1 and C99 F.9.4.4:
  /// <c>pow(x, ±0) = 1</c> for every <c>x</c>, even a NaN; <c>pow(+1, y) = 1</c> for every <c>y</c>,
  /// even a NaN; <c>pow(-1, ±∞) = 1</c>; a negative <c>x</c> with a <c>y</c> that is not an integer
  /// is a NaN. The value is approximated in three phases of growing precision (double-double, 128 and
  /// 256 bits), each with a rounding test; exact results and midpoints are found between the last two.
  /// </summary>
  /// <param name="x">The base.</param>
  /// <param name="y">The exponent.</param>
  /// <returns>The correctly rounded power.</returns>
  public static double Pow(double x, double y) {
    var s = 1.0;
    var x0 = x;
    var ux = (ulong)BitConverter.DoubleToInt64Bits(x);
    var uy = (ulong)BitConverter.DoubleToInt64Bits(y);

    // An infinity, a NaN or a negative x.
    if (ux >= 0x7ff0000000000000 || uy >= 0x7ff0000000000000) {
      if (IsNaN(x))
        return y == 0.0 ? 1.0 : x + x;

      if (IsNaN(y))
        return x == 1.0 ? 1.0 : y + y;

      if (ux == 0x7ff0000000000000) {
        if (y == 0.0)
          return 1.0;

        return y < 0.0 ? 0.0 : double.PositiveInfinity;
      }

      if (ux == 0xfff0000000000000) {
        if (uy == 0x7ff0000000000000)
          return y;

        if (uy == 0xfff0000000000000)
          return 0.0;

        if (PowIsInteger(y) && !PowIsInteger(y * 0.5))
          return y < 0.0 ? -0.0 : double.NegativeInfinity;

        if (y < 0.0)
          return 0.0;

        if (y > 0.0)
          return double.PositiveInfinity;
      }

      if (uy == 0x7ff0000000000000) {
        if (x == 0.0)
          return 0.0;

        if (x == -1.0 || x == 1.0)
          return 1.0;

        return -1.0 < x && x < 1.0 ? 0.0 : double.PositiveInfinity;
      }

      if (uy == 0xfff0000000000000) {
        if (x == 0.0)
          return double.PositiveInfinity;

        if (x == -1.0 || x == 1.0)
          return 1.0;

        return -1.0 < x && x < 1.0 ? double.PositiveInfinity : 0.0;
      }
    }

    // From here on x and y are finite.
    if (x <= 0.0) {
      if (y == 0.0)
        return 1.0;

      var odd = PowIsInteger(y) && !PowIsInteger(y * 0.5);
      if (ux == 0) {
        if (odd)
          return y < 0.0 ? double.PositiveInfinity : 0.0;

        return y > 0.0 ? 0.0 : double.PositiveInfinity;
      }

      if (ux == 0x8000000000000000) {
        if (odd)
          return y < 0.0 ? double.NegativeInfinity : -0.0;

        return y > 0.0 ? 0.0 : double.PositiveInfinity;
      }

      if (!PowIsInteger(y))
        return double.NaN;

      // The sign of the result: -1 for an odd y.
      var parity = Math.Abs(y) >= 9007199254740992.0 ? 0L : (long)y & 1;
      s = parity == 1 ? -1.0 : 1.0;
      x = -x;
    }

    // The first phase: log(x) in a double-double, times y, then exp.
    var pair = new double[2];
    var cancel = PowLog1(x, pair);
    var lh = pair[0];
    var ll = pair[1];

    // A |y| below 2^-969 or from 2^1014 on would underflow or overflow y * log(x) spuriously.
    var ey = (int)((uy >> 52) & 0x7ff);
    if (ey < 0x36 || ey >= 0x7f5) {
      lh = double.NaN;
      ll = double.NaN;
    }

    var rh = y * lh;
    var rl = Math.FusedMultiplyAdd(y, ll, Math.FusedMultiplyAdd(y, lh, -rh));
    var resH = PowExp1(rh, rl, s, pair);
    var resL = pair[0];
    var err = cancel == 1 ? 4.6485168145316E-18 : 6.246867986000465E-20;
    var resMin = resH + Math.FusedMultiplyAdd(err, -resH, resL);
    var resMax = resH + Math.FusedMultiplyAdd(err, resH, resL);
    if (resMin == resMax)
      return resMax;

    if (y == 1.0)
      return s * x;

    if (y == 2.0)
      return x * x;

    if (y == 0.5)
      return Math.Sqrt(x);

    if (y == 0.0)
      return 1.0;

    // The second phase: 128 bits.
    var exact = PowIsExact(x, y);
    var bigX = PowDintFromDouble(x);
    var bigY = PowDintFromDouble(y);
    bigX[3] = 0;
    var r = new ulong[4];
    PowLog2(r, bigX);
    PowMultiplyDint21(r, r, bigY);
    PowExp2(r, r);
    var rEx = (long)r[2];
    if (rEx < -1075)
      return 0.5 * (s * 5E-324);

    r[3] = s == -1.0 ? 1UL : 0UL;
    if (!PowIsHardToRound2(r))
      return PowDintToDouble(r);

    var boundary = PowExactPower(x0, y, r, exact);
    if (!IsNaN(boundary))
      return boundary;

    // The third phase: 256 bits.
    var qx = PowQintFromDouble(x);
    var qy = PowQintFromDouble(y);
    qx[5] = 0;
    var qr = new ulong[6];
    PowLog3(qr, qx);
    PowMultiplyQint(qr, qr, qy, 0, 3);
    var qz = new ulong[6];
    PowExp3(qz, qr);

    // Upstream tests the rounding once more: what fails it is a power within 2^-240 of 1, which it
    // returns as 1 +- 2^-100, rounded to 1, the value the conversion gives as well.
    qz[5] = s == -1.0 ? 1UL : 0UL;
    qz[3] &= ulong.MaxValue << 10;
    return PowQintToDouble(qz);
  }

  /// <summary>
  /// <c>x^y</c> as ECMA-262 §6.1.6.1.3 (<c>Number::exponentiate</c>) defines it for <c>Math.pow</c>
  /// and <c>**</c>: the same as <see cref="Pow"/>, except that a NaN exponent gives a NaN even for
  /// <c>x = 1</c>, and <c>|x| = 1</c> with an infinite exponent gives a NaN. <c>x^±0</c> stays 1,
  /// even for a NaN <c>x</c>.
  /// </summary>
  /// <param name="x">The base.</param>
  /// <param name="y">The exponent.</param>
  /// <returns>The correctly rounded power.</returns>
  public static double PowEcma(double x, double y) {
    if (IsNaN(y))
      return y;

    if (IsInfinity(y) && Math.Abs(x) == 1.0)
      return double.NaN;

    return Pow(x, y);
  }

  /// <summary>Whether a finite number is an integer.</summary>
  private static bool PowIsInteger(double x) => x == Math.Round(x);

  /// <summary>
  /// The first approximation of <c>log(x)</c> for <c>2^-1074 &lt;= x &lt; 2^1024</c>, as a
  /// double-double <c>h + l</c> written into <paramref name="result"/>; 1 when <c>x</c> is so close
  /// to 1 that the error bound of the cancelling case applies, else 0 (upstream <c>log_1</c>).
  /// </summary>
  private static int PowLog1(double x, double[] result) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    var m = bits & (ulong.MaxValue >> 12);
    var e = (long)((bits >> 52) & 0x7ff);
    ulong t;
    if (e != 0) {
      t = m | (0x3ffUL << 52);
      m += 1UL << 52;
      e -= 0x3ff;
    } else {
      // A subnormal x.
      var k = BitOperations.LeadingZeroCount(m) - 11;
      e = -0x3fe - k;
      m <<= k;
      t = m | (0x3ffUL << 52);
    }

    // Now x = 2^e * t with 1 <= t < 2; above sqrt(2), t is halved.
    var c = m >= 0x16a09e667f3bcd ? 1 : 0;
    e += c;
    var bigE = (double)e;
    var i = (int)(m >> (c == 1 ? 45 : 44));
    var tf = BitConverter.Int64BitsToDouble((long)t) * (c == 1 ? 0.5 : 1.0);
    var inverse = _powInverse[i - 181];
    var l1 = _powLogInverse[2 * (i - 181)];
    var l2 = _powLogInverse[2 * (i - 181) + 1];
    var z = Math.FusedMultiplyAdd(inverse, tf, -1.0);

    // log(2) = 0x1.62e42fefa38p-1 + 0x1.ef35793c7673p-45.
    var th = Math.FusedMultiplyAdd(bigE, 0.6931471805598903, l1);
    var tl = Math.FusedMultiplyAdd(bigE, 5.497923018708371E-14, l2);
    var h = th + z;
    var l = z - (h - th) + tl;
    var ph = PowLogPolynomial1(z, result);
    var pl = result[0];
    var bl = l + pl;
    var h2 = h + ph;
    l = ph - (h2 - h) + bl;
    h = h2;
    if (e == 0 && Math.Abs(l) > Math.Abs(h) * 5.960464477539063E-08) {
      var sum = h + l;
      l -= sum - h;
      h = sum;
      result[0] = h;
      result[1] = l;
      return 1;
    }

    result[0] = h;
    result[1] = l;
    return 0;
  }

  /// <summary>
  /// <c>log(1+z) - z</c> for <c>|z| &lt;= 33 * 2^-13</c> as a double-double; returns the high part
  /// and writes the low one into <c>low[0]</c> (upstream <c>p_1</c>).
  /// </summary>
  private static double PowLogPolynomial1(double z, double[] low) {
    var wh = z * z;
    var wl = Math.FusedMultiplyAdd(z, z, -wh);
    var t = Math.FusedMultiplyAdd(_powLogPolynomial1[5], z, _powLogPolynomial1[4]);
    var u = Math.FusedMultiplyAdd(_powLogPolynomial1[3], z, _powLogPolynomial1[2]);
    var v = Math.FusedMultiplyAdd(_powLogPolynomial1[1], z, _powLogPolynomial1[0]);
    u = Math.FusedMultiplyAdd(t, wh, u);
    v = Math.FusedMultiplyAdd(u, wh, v);
    u = v * wh;
    low[0] = Math.FusedMultiplyAdd(u, z, -0.5 * wl);
    return -0.5 * wh;
  }

  /// <summary>
  /// <c>s * exp(rh + rl)</c> as a double-double; returns the high part and writes the low one into
  /// <c>low[0]</c>. Outside the range it handles it returns the largest double (overflow), zero
  /// (underflow) or NaN, which makes the rounding test fail (upstream <c>exp_1</c>).
  /// </summary>
  private static double PowExp1(double rh, double rl, double s, double[] low) {
    if (IsNaN(rh) || rh > 709.7826672320183) {
      if (!IsNaN(rh) && rh > 709.7827585515907) {
        low[0] = 1.7976931348623157E+308 * s;
        return 1.7976931348623157E+308 * s;
      }

      low[0] = double.NaN;
      return double.NaN;
    }

    if (rh < -656.4652340719966) {
      if (rh < -745.1332670341407) {
        low[0] = 5E-324 * (0.5 * s);
        return 0.0 * s;
      }

      low[0] = double.NaN;
      return double.NaN;
    }

    // rh = k * log(2)/2^12 + z with 2^12/log(2) = 0x1.71547652b82fep+12 and log(2)/2^12 split in
    // 0x1.62e42fefa39efp-13 + 0x1.abc9e3b39803fp-68.
    var k = Math.Round(rh * 5909.278887481194);
    var zh = Math.FusedMultiplyAdd(0.0001692253858788929, -k, rh);
    var zl = Math.FusedMultiplyAdd(5.661735385366942E-21, -k, rl);
    var bigK = (long)k;
    var bigM = (bigK >> 12) + 0x3ff;
    var i2 = (int)((bigK >> 6) & 0x3f);
    var i1 = (int)(bigK & 0x3f);
    var t1h = _powExp2High[2 * i2];
    var t1l = _powExp2High[2 * i2 + 1];
    var t2h = _powExp2Low[2 * i1];
    var t2l = _powExp2Low[2 * i1 + 1];
    var eh = t2h * t1h;
    var el = Math.FusedMultiplyAdd(t2h, t1l, Math.FusedMultiplyAdd(t2l, t1h, Math.FusedMultiplyAdd(t2h, t1h, -eh)));
    var qh = PowExpPolynomial1(zh + zl, low);
    var ql = low[0];
    var ph = eh * qh;
    el = Math.FusedMultiplyAdd(eh, ql, Math.FusedMultiplyAdd(el, qh, Math.FusedMultiplyAdd(eh, qh, -ph)));
    var d = BitConverter.Int64BitsToDouble((long)((ulong)bigM << 52)) * s;
    low[0] = el * d;
    return ph * d;
  }

  /// <summary>
  /// <c>exp(z)</c> for <c>|z| &lt; 2^-12.905</c> as a double-double; returns the high part and
  /// writes the low one into <c>low[0]</c> (upstream <c>q_1</c>).
  /// </summary>
  private static double PowExpPolynomial1(double z, double[] low) {
    var q = Math.FusedMultiplyAdd(_powExpPolynomial1[4], z, _powExpPolynomial1[3]);
    q = Math.FusedMultiplyAdd(q, z, _powExpPolynomial1[2]);
    var h0 = Math.FusedMultiplyAdd(q, z, _powExpPolynomial1[1]);
    var h1 = z * h0;
    var l1 = Math.FusedMultiplyAdd(z, h0, -h1);
    var hi = _powExpPolynomial1[0] + h1;
    low[0] = h1 - (hi - _powExpPolynomial1[0]) + l1;
    return hi;
  }

  /// <summary>
  /// Whether <c>x^y</c> (<c>x &gt; 0</c>) is exact and representable as a double (upstream
  /// <c>is_exact</c>): <c>|x| = 1</c>; an integer <c>y</c> from 0 to 33; for <c>y &lt; 0</c> a power
  /// of two <c>x</c>; for <c>y = n * 2^f</c> with <c>-5 &lt;= f &lt;= -1</c> an exact root.
  /// </summary>
  private static bool PowIsExact(double x, double y) {
    var v = (ulong)BitConverter.DoubleToInt64Bits(x);
    var w = (ulong)BitConverter.DoubleToInt64Bits(y);
    if (v << 1 != 0x7fe0000000000000 && w << 22 != 0)
      return false;

    if (v << 1 == 0x7fe0000000000000)
      return true;

    var m = v & 0xfffffffffffff;
    var e = (long)((v << 1) >> 53) - 0x433;
    if (e >= -1074)
      m |= 0x10000000000000;
    else
      e++;

    var t = BitOperations.TrailingZeroCount(m);
    m >>= t;
    e += t;

    // Now x = m * 2^e with m odd.
    if (y >= 0 && PowIsInteger(y)) {
      if (m == 1)
        return -1074 <= y * e && y * e < 1024;

      if (33 < y)
        return false;

      var yInt = (int)y;
      if (m > _powExactMaximum[yInt])
        return false;

      // 2^(t-1) <= m < 2^t.
      t = 64 - BitOperations.LeadingZeroCount(m);
      var ez = e * yInt + t;
      if (ez <= -1074 || 1024 < ez)
        return false;

      return e * yInt >= -1074;
    }

    var n = w & 0xfffffffffffff;
    var f = (long)((w << 1) >> 53) - 0x433;
    if (f >= -1074)
      n |= 0x10000000000000;
    else
      f++;

    t = BitOperations.TrailingZeroCount(n);
    n >>= t;
    f += t;

    // |y| = n * 2^f with n odd; a negative y gives an exact power only for x = 2^e.
    if (y < 0) {
      if (m != 1)
        return false;

      long ez;
      if (f >= 0)
        ez = f < 12 ? (long)((0UL - n) * (ulong)e << (int)f) : 1024;
      else {
        t = BitOperations.TrailingZeroCount((ulong)e);
        if (-f > t)
          return false;

        ez = (long)((ulong)(-e >> (int)-f) * n);
      }

      return -1074 <= ez && ez < 1024;
    }

    // y > 0 is not an integer: x has to be an exact (2^-f)-th power.
    while (f++ != 0) {
      if ((e & 1) != 0)
        return false;

      e /= 2;
      var dm = (double)m;
      var root = Math.Round(Math.Sqrt(dm), MidpointRounding.AwayFromZero);
      if (root * root != dm)
        return false;

      m = (ulong)root;
    }

    if (m > 1) {
      if (33 < n)
        return false;

      if (m > _powExactMaximum[(int)n])
        return false;
    }

    var my = m;
    var n0 = n;
    while (n0-- > 1)
      my *= m;

    t = 64 - BitOperations.LeadingZeroCount(my);
    return -1074 <= e * (int)n && e * (int)n + t <= 1024;
  }

  /// <summary>For 1 &lt;= y &lt;= 33, the largest odd m whose y-th power fits in 53 bits (upstream <c>xmax</c> of <c>is_exact</c>).</summary>
  private static readonly ulong[] _powExactMaximum = [
    0, 0xffffffffffffffff, 94906265, 208063, 9741, 1551, 455, 189, 97,
    59, 39, 27, 21, 15, 13, 11, 9, 7, 7, 5, 5, 5, 5, 4, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3
  ];

  /// <summary>
  /// The exact value of <c>x^y</c> when it is exact or a midpoint of two doubles, else NaN; <c>z</c>
  /// is the 128-bit approximation with relative error below 2^-117 and the sign of the result
  /// (upstream <c>exact_pow</c>, Algorithm detectRoundingBoundaryCase of Lauter and Lefèvre).
  /// </summary>
  private static double PowExactPower(double x, double y, ulong[] z, bool exact) {
    var sign = z[3] != 0 ? -1L : 1L;

    // x = 2^bigE * m with m odd.
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    var biased = (long)((bits >> 52) & 0x7ff);
    var m = (bits & (ulong.MaxValue >> 12)) + (biased != 0 ? 1UL << 52 : 0UL);
    var tm = BitOperations.TrailingZeroCount(m);
    m >>= tm;
    var bigE = biased + tm - (0x433 - (biased == 0 ? 1 : 0));
    if (m == 1) {
      var power = (double)bigE * y;
      if (!PowIsInteger(power))
        return double.NaN;

      return PowScaleByPowerOfTwo(z[3] != 0 ? -1.0 : 1.0, (long)power);
    }

    if (y < 0.0 || y > 34.0)
      return double.NaN;

    // y = 2^bigF * n with n odd.
    var ybits = (ulong)BitConverter.DoubleToInt64Bits(y);
    var ybiased = (long)((ybits >> 52) & 0x7ff);
    var n = (ybits & (ulong.MaxValue >> 12)) + (ybiased != 0 ? 1UL << 52 : 0UL);
    var tn = BitOperations.TrailingZeroCount(n);
    n >>= tn;
    var bigF = ybiased + tn - (0x433 - (ybiased == 0 ? 1 : 0));
    if (n > 34 || bigF < -5)
      return double.NaN;

    if (bigF < 0) {
      // E has to be divisible by 2^-F.
      if ((bigE & (long)(ulong.MaxValue >> (int)(64 + bigF))) != 0)
        return double.NaN;

      var g = (long)((ulong)(bigE >> (int)-bigF) * n);

      // z rounded to 54 bits, k * 2^bigG.
      var bigG = (long)z[2] - 53;
      var k = (long)((z[0] >> 10) + ((z[0] >> 9) & 1));
      if (!exact) {
        var cnt = BitOperations.LeadingZeroCount((ulong)k);
        var d = PowDint((ulong)k << cnt, 0, bigG + 63 - cnt, 1 - z[3]);
        PowAddDint(d, z, d);
        d[2] += 116;
        if (PowCompareDintAbs(d, z) >= 0)
          return double.NaN;
      }

      if (bigG > g)
        return double.NaN;

      var shift = (int)(g - bigG);
      if (((ulong)k & ~(~1UL << shift)) != 1UL << shift)
        return double.NaN;

      return PowScaleByPowerOfTwo((k >> shift) * sign, g);
    }

    // y is an integer from 2 to 34: m^y has to fit in 54 bits.
    var t = n << (int)bigF;
    var product = 1L;
    while (t != 0) {
      if ((t & 1) != 0) {
        var low = m * (ulong)product;
        if (MultiplyHigh(m, (ulong)product) != 0 || low > long.MaxValue)
          return double.NaN;

        product = (long)low;
      }

      t >>= 1;
      if (t != 0) {
        if (MultiplyHigh(m, m) != 0)
          return double.NaN;

        m *= m;
      }
    }

    if (product >> 54 != 0)
      return double.NaN;

    return PowScaleByPowerOfTwo(product * sign, (long)((ulong)bigE * (n << (int)bigF)));
  }

  /// <summary><c>x * 2^e</c> in two steps, so that a scale into the subnormal range rounds once (upstream <c>pow2</c>).</summary>
  private static double PowScaleByPowerOfTwo(double x, long e) {
    if ((e & 1) != 0)
      x *= 2.0;

    var e2 = BitConverter.Int64BitsToDouble((long)((((ulong)((e >> 1) + 0x3ff)) & 0x7ff) << 52));
    return x * e2 * e2;
  }

}
