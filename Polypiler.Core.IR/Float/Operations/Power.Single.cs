using System;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>
/// The power function of <c>float</c>, converted from CORE-MATH's <c>binary32/pow/powf.c</c>
/// (Alexei Sibidanov and Paul Zimmermann, MIT): a fast path in double precision with a rounding test,
/// and an accurate one in double-double. See <c>THIRD-PARTY-NOTICE.CORE-MATH.Power.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>x^y</c> of <c>float</c>, correctly rounded, with the special cases of <see cref="Pow"/>:
  /// <c>pow(x, ±0) = 1</c>, <c>pow(+1, y) = 1</c> and <c>pow(-1, ±∞) = 1</c>, even for a NaN.
  /// <c>log2(x)</c> is approximated by a table and a polynomial, <c>2^(y log2 x)</c> by a table of
  /// 2^(i/16) and a polynomial; a result too close to a rounding boundary is recomputed in
  /// double-double, where exact results are recognized.
  /// </summary>
  /// <param name="x0">The base.</param>
  /// <param name="y0">The exponent.</param>
  /// <returns>The correctly rounded power.</returns>
  public static float PowF(float x0, float y0) {
    double x = x0;
    double y = y0;
    var tx = (ulong)BitConverter.DoubleToInt64Bits(x);
    var ty = (ulong)BitConverter.DoubleToInt64Bits(y);
    var xsgn = false;

    // |x| = 1.
    if (tx << 1 == 0x3ffUL << 53) {
      if (tx >> 63 != 0) {
        if (ty << 1 > 0x7ffUL << 53)
          return y0 + y0;

        if (PowFIsInteger(y0))
          return PowFIsOdd(y0) ? x0 : -x0;

        return float.NaN;
      }

      return x0;
    }

    if (ty << 1 == 0)
      return 1.0f;

    if (ty == 0x3ffUL << 52)
      return x0;

    // y is an infinity or a NaN.
    if (ty << 1 >= 0x7ffUL << 53) {
      if (tx << 1 > 0x7ffUL << 53)
        return x0 + y0;

      if (ty << 1 == 0x7ffUL << 53)
        return (tx << 1 < 0x3ffUL << 53) != (ty >> 63 != 0) ? 0.0f : float.PositiveInfinity;

      return x0 + y0;
    }

    // x is an infinity, a NaN or not above zero.
    if (tx >= 0x7ffUL << 52) {
      if (tx << 1 == 0x7ffUL << 53) {
        if (!PowFIsOdd(y0))
          x0 = MathF.Abs(x0);

        return ty >> 63 != 0 ? 1 / x0 : x0;
      }

      if (tx << 1 > 0x7ffUL << 53)
        return x0 + x0;

      xsgn = true;
      if (!PowFIsInteger(y0) && x != 0)
        return float.NaN;
    }

    if (tx << 1 == 0) {
      if (ty >> 63 != 0)
        return PowFIsOdd(y0) ? 1.0f / CopySignF(0.0f, x0) : float.PositiveInfinity;

      return PowFIsOdd(y0) ? CopySignF(1.0f, x0) * 0.0f : 0.0f;
    }

    // log2(x) = e + log2(t) with t = 1 + z near the j-th table point.
    var m = tx & (ulong.MaxValue >> 12);
    var e = (int)((tx >> 52) & 0x7ff) - 0x3ff;
    var j = (int)((m + (1UL << 46)) >> 47);
    e += j > 13 ? 1 : 0;
    var xd = BitConverter.Int64BitsToDouble((long)(m | (0x3ffUL << 52)));
    var z = Math.FusedMultiplyAdd(xd, _powFInverse[j], -1.0);
    var z2 = z * z;
    var z4 = z2 * z2;
    var c6 = _powFLog2Polynomial[6] + z * _powFLog2Polynomial[7];
    var c4 = _powFLog2Polynomial[4] + z * _powFLog2Polynomial[5];
    var c2 = _powFLog2Polynomial[2] + z * _powFLog2Polynomial[3];
    var c0 = _powFLog2Polynomial[0] + z * _powFLog2Polynomial[1];
    c0 += z2 * c2;
    c4 += z2 * c6;
    c0 += z4 * c4;
    var l = z * c0 - _powFLogInverse[2 * j + 1];
    y *= 16;
    var zt = (e - _powFLogInverse[2 * j]) * y;
    z = l * y + zt;

    // z = 16 * y * log2(x).
    if (z > 2048)
      return PowFIsOdd(y0) ? CopySignF(1.70141183E+38f, x0) * 1.70141183E+38f : 1.70141183E+38f * 1.70141183E+38f;

    if (z < -2400)
      return PowFIsOdd(y0) ? CopySignF(1.17549435E-38f, x0) * 1.17549435E-38f : 1.17549435E-38f * 1.17549435E-38f;

    if (Math.Abs(z) < 1.4901161193847656E-08)
      return (float)(1.0 + z);

    var ia = Math.Floor(z);
    var h = Math.FusedMultiplyAdd(l, y, zt - ia);
    var il = (long)ia;
    var jl = il & 0xf;
    var el = (il - jl) >> 4;
    var s = _powFExp2Table[(int)jl] * BitConverter.Int64BitsToDouble((long)((ulong)(el + 0x3ff) << 52));
    var h2 = h * h;
    c0 = _powFExp2Polynomial[0] + h * _powFExp2Polynomial[1];
    c2 = _powFExp2Polynomial[2] + h * _powFExp2Polynomial[3];
    c4 = _powFExp2Polynomial[4] + h * _powFExp2Polynomial[5];
    c0 += h2 * (c2 + h2 * c4);
    var w = s * h;
    var rr = s + w * c0;

    // Too close to a rounding boundary of float: recompute.
    const ulong Offset = 468;
    if ((((ulong)BitConverter.DoubleToInt64Bits(rr) + Offset) & 0xfffffff) <= 2 * Offset)
      return PowFAccurate(x0, y0, PowFIsExact(x0, y0));

    if (xsgn && PowFIsOdd(y0))
      rr = -rr;

    return (float)rr;
  }

  /// <summary>
  /// <c>x^y</c> of <c>float</c> as ECMA-262 defines it; see <see cref="PowEcma"/>. ECMA-262 itself
  /// knows only doubles; this is the same rule applied to <see cref="PowF"/>.
  /// </summary>
  /// <param name="x">The base.</param>
  /// <param name="y">The exponent.</param>
  /// <returns>The correctly rounded power.</returns>
  public static float PowEcmaF(float x, float y) {
    if (IsNaNF(y))
      return y;

    if (IsInfinityF(y) && MathF.Abs(x) == 1.0f)
      return float.NaN;

    return PowF(x, y);
  }

  /// <summary>Whether a <c>float</c> is an integer, infinities included (upstream <c>isint</c>).</summary>
  private static bool PowFIsInteger(float y0) {
    var u = (uint)BitConverter.SingleToInt32Bits(y0);
    var ey = (int)((u >> 23) & 0xff) - 127;
    var s = ey + 9;
    if (ey >= 0)
      return s >= 32 || u << s == 0;

    return u << 1 == 0;
  }

  /// <summary>Whether a <c>float</c> is an odd integer (upstream <c>isodd</c>).</summary>
  private static bool PowFIsOdd(float y0) {
    var u = (uint)BitConverter.SingleToInt32Bits(y0);
    var ey = (int)((u >> 23) & 0xff) - 127;
    var s = ey + 9;
    var odd = 0U;
    if (ey >= 0) {
      if (s < 32 && u << s == 0)
        odd = (u >> (32 - s)) & 1;

      if (s == 32)
        odd = u & 1;
    }

    return odd != 0;
  }

  /// <summary>
  /// Whether <c>x^y</c> is exact and representable as a <c>float</c> (upstream <c>is_exact</c> of
  /// <c>powf.c</c>): an integer <c>y</c> from 2 to 15; for <c>y &lt; 0</c> a power of two <c>x</c>; for
  /// <c>y = n * 2^f</c> with <c>-4 &lt;= f &lt;= -1</c> an exact root. <see cref="PowF"/> calls it on
  /// its accurate path only, after <c>|x| = 1</c>, <c>y = 0</c>, <c>y = 1</c>, overflow and underflow
  /// have returned; so upstream's tests for <c>|x| = 1</c>, for a subnormal <c>x</c> with an integer
  /// <c>y</c> (then <c>|x^y| &lt; 2^-252</c>), for a power of an integer above 2^128 and for a
  /// subnormal <c>y</c> (then <c>x^y</c> rounds to 1 early) are left out.
  /// </summary>
  private static bool PowFIsExact(float x, float y) {
    var v = (uint)BitConverter.SingleToInt32Bits(x);
    var w = (uint)BitConverter.SingleToInt32Bits(y);
    if (w << 16 != 0)
      return false;

    if (y >= 0 && PowFIsInteger(y)) {
      var mi = v & 0x7fffff | 0x800000;
      var ei = (int)((v << 1) >> 24) - 0x96;
      var ti = BitOperations.TrailingZeroCount(mi);
      mi >>= ti;
      ei += ti;
      if (mi == 1)
        return -149 <= y * ei && y * ei < 128;

      if (15 < y)
        return false;

      var yInt = (int)y;
      if (mi > _powFExactMaximum[yInt])
        return false;

      // 2^(t-1) <= m < 2^t.
      ti = 32 - BitOperations.LeadingZeroCount(mi);
      var ez = ei * yInt + ti;
      if (ez <= -149)
        return false;

      return ei * yInt >= -149;
    }

    var n = w & 0x7fffff | 0x800000;
    var f = (int)((w << 1) >> 24) - 0x96;

    var t = BitOperations.TrailingZeroCount(n);
    n >>= t;
    f += t;
    var m = v & 0x7fffff;
    var e = (int)((v << 1) >> 24) - 0x96;
    if (e >= -149)
      m |= 0x800000;
    else
      e++;

    t = BitOperations.TrailingZeroCount(m);
    m >>= t;
    e += t;

    // |x| = m * 2^e and |y| = n * 2^f with m, n odd; a negative y needs x = 2^e.
    if (y < 0) {
      if (m != 1)
        return false;

      int ez;
      if (f >= 0) {
        ez = (int)((uint)(e >= 0 ? -(e << f) : -e << f) * n);
      } else {
        t = BitOperations.TrailingZeroCount((uint)e);
        if (-f > t)
          return false;

        ez = (int)((uint)(-e >> -f) * n);
      }

      return -149 <= ez && ez < 128;
    }

    // y > 0 is not an integer: x has to be an exact (2^-f)-th power.
    while (f++ != 0) {
      if ((e & 1) != 0)
        return false;

      e /= 2;
      var dm = (float)m;
      var root = MathF.Round(MathF.Sqrt(dm), MidpointRounding.AwayFromZero);
      if (root * root != dm)
        return false;

      m = (uint)root;
    }

    if (m > 1) {
      if (15 < n)
        return false;

      if (m > _powFExactMaximum[(int)n])
        return false;
    }

    var my = m;
    var n0 = n;
    while (n0-- > 1)
      my *= m;

    t = 32 - BitOperations.LeadingZeroCount(my);
    return -149 <= e * (int)n && e * (int)n + t <= 128;
  }

  /// <summary>For 1 &lt;= y &lt;= 15, the largest odd m whose y-th power fits in 24 bits (upstream <c>xmax</c> of <c>powf.c</c>).</summary>
  private static readonly uint[] _powFExactMaximum = [0, 0xffffff, 4095, 255, 63, 27, 15, 9, 7, 5, 5, 3, 3, 3, 3, 3];

  /// <summary>
  /// The double-double product <c>(xh + xl) * (ch + cl)</c>; returns the high part and writes the low
  /// one into <c>low[0]</c> (upstream <c>muldd</c>).
  /// </summary>
  private static double PowFMultiplyDD(double xh, double xl, double ch, double cl, double[] low) {
    var ahlh = ch * xl;
    var alhh = cl * xh;
    var ahhh = ch * xh;
    var ahhl = Math.FusedMultiplyAdd(ch, xh, -ahhh);
    ahhl += alhh + ahlh;
    var hi = ahhh + ahhl;
    low[0] = ahhh - hi + ahhl;
    return hi;
  }

  /// <summary>The product <c>(xh + xl) * c</c> as a double-double (upstream <c>mulddd</c>).</summary>
  private static double PowFMultiplyDDByDouble(double xh, double xl, double c, double[] low) {
    var ahlh = c * xl;
    var ahhh = c * xh;
    var ahhl = Math.FusedMultiplyAdd(c, xh, -ahhh);
    ahhl += ahlh;
    var hi = ahhh + ahhl;
    low[0] = ahhh - hi + ahhl;
    return hi;
  }

  /// <summary>
  /// The polynomial with the <paramref name="n"/> double-double coefficients of
  /// <see cref="_powFAccurateExpPolynomial"/> (<paramref name="exponential"/>) or
  /// <see cref="_powFAccurateLogPolynomial"/> at <c>xh + xl</c>, by Horner's rule (upstream <c>polydd</c>).
  /// </summary>
  private static double PowFPolynomialDD(double xh, double xl, int n, bool exponential, double[] low) {
    var i = n - 1;
    var ch = exponential ? _powFAccurateExpPolynomial[2 * i] : _powFAccurateLogPolynomial[2 * i];
    var cl = exponential ? _powFAccurateExpPolynomial[2 * i + 1] : _powFAccurateLogPolynomial[2 * i + 1];
    while (--i >= 0) {
      ch = PowFMultiplyDD(xh, xl, ch, cl, low);
      cl = low[0];
      var c0 = exponential ? _powFAccurateExpPolynomial[2 * i] : _powFAccurateLogPolynomial[2 * i];
      var c1 = exponential ? _powFAccurateExpPolynomial[2 * i + 1] : _powFAccurateLogPolynomial[2 * i + 1];
      var th = ch + c0;
      var tl = c0 - th + ch;
      ch = th;
      cl += tl + c1;
    }

    low[0] = cl;
    return ch;
  }

  /// <summary>
  /// <c>x^y</c> of <c>float</c> in double-double: <c>log2(x)</c> through <c>atanh</c> of
  /// <c>(t-1)/(t+1)</c>, then 2^x by a polynomial. A result whose last 28 bits look exact is moved
  /// off the boundary by its low part, unless the power is exact (upstream <c>as_powf_accurate2</c>).
  /// </summary>
  private static float PowFAccurate(float x0, float y0, bool isExact) {
    double y = y0;
    var t = (ulong)BitConverter.DoubleToInt64Bits(x0);
    var e = (int)((t >> 52) & 0x7ff) - 0x3ff;
    var xsgn = t >> 63 != 0;
    t &= ulong.MaxValue >> 12;
    var k = t > 0x6a09e667f3bcd ? 1 : 0;
    e += k;
    t |= 0x3ffUL << 52;
    var x = BitConverter.Int64BitsToDouble((long)t);
    var o = k == 1 ? 2.0 : 1.0;
    var xm = x - o;
    var xp = x + o;
    var zh = xm / xp;
    var zl = Math.FusedMultiplyAdd(zh, -xp, xm) / xp;
    var low = new double[1];
    var z2h = PowFMultiplyDD(zh, zl, zh, zl, low);
    var z2l = low[0];
    z2h = PowFPolynomialDD(z2h, z2l, 13, false, low);
    z2l = low[0];
    zh = PowFMultiplyDD(zh, zl, z2h, z2l, low);
    zl = low[0];
    zh = PowFMultiplyDDByDouble(zh, zl, y, low);
    zl = low[0];
    var ey = e * y;
    var eh = ey + zh;
    var el = ey - eh + zh + zl;
    var ee = Math.Round(eh);
    eh -= ee;
    eh = PowFPolynomialDD(eh, el, 18, true, low);
    el = low[0];
    var r = BitConverter.Int64BitsToDouble((long)((ulong)(0x3ff + (long)ee) << 52));
    var lh = (ulong)BitConverter.DoubleToInt64Bits(eh);
    if (!isExact && (lh & 0xfffffff) == 0 || isExact && ((lh + 1) & 0xfffffff) <= 2) {
      if (Math.Abs(el) > 4.0389678347315804E-28) {
        lh = el < 0 ? lh - 1 : lh + 1;
        eh = BitConverter.Int64BitsToDouble((long)lh);
      }
    }

    eh *= r;
    if (xsgn && PowFIsOdd(y0))
      eh = -eh;

    return (float)eh;
  }

}
