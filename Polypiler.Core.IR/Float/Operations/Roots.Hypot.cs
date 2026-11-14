using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The correctly rounded Euclidean distance, converted from CORE-MATH's <c>hypot.c</c> and
/// <c>hypotf.c</c> (MIT). See <c>THIRD-PARTY-NOTICE.CORE-MATH.Roots.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>sqrt(x^2 + y^2)</c> without undue overflow or underflow, correctly rounded to nearest with
  /// ties to even. An infinite operand gives +infinity even when the other is a NaN (IEEE 754-2019
  /// §9.2.1); otherwise a NaN gives a NaN.
  /// </summary>
  /// <param name="x">The first coordinate.</param>
  /// <param name="y">The second coordinate.</param>
  /// <returns>The correctly rounded distance from the origin.</returns>
  public static double Hypot(double x, double y) {
    const ulong ExponentMask = 0x7ffUL << 52;
    var xi = (ulong)BitConverter.DoubleToInt64Bits(x);
    var yi = (ulong)BitConverter.DoubleToInt64Bits(y);
    var ex = xi & ExponentMask;
    var ey = yi & ExponentMask;
    x = Math.Abs(x);
    y = Math.Abs(y);
    if (ex == ExponentMask || ey == ExponentMask) {
      // An infinity wins over a NaN.
      if (xi << 1 == ExponentMask << 1)
        return x;

      if (yi << 1 == ExponentMask << 1)
        return y;

      return x + y;
    }

    // Both finite; u >= v.
    var u = x > y ? x : y;
    var v = x > y ? y : x;
    var xd = (ulong)BitConverter.DoubleToInt64Bits(u);
    var yd = (ulong)BitConverter.DoubleToInt64Bits(v);
    if (yd >> 52 == 0) {
      if (yd == 0)
        return u;

      if (xd >> 52 == 0)
        return HypotSubnormal(xd, yd);

      // Normalize v; its exponent field goes below zero, which the offset below makes up for.
      var nz = System.Numerics.BitOperations.LeadingZeroCount(yd);
      yd <<= nz - 11;
      yd &= ulong.MaxValue >> 12;
      yd = unchecked(yd - ((ulong)(nz - 12L) << 52));
    }

    // v below 2^-27 u: the result is u rounded up as far as v pushes it.
    if (unchecked(xd - yd) > 27UL << 52)
      return Math.FusedMultiplyAdd(7.450580596923828E-09, v, u);   // 0x1p-27

    // Scale u to [1, 2).
    var off = unchecked((long)(0x3ffUL << 52) - (long)(xd & ExponentMask));
    x = BitConverter.Int64BitsToDouble(unchecked((long)xd + off));
    y = BitConverter.Int64BitsToDouble(unchecked((long)yd + off));
    var x2 = x * x;
    var dx2 = Math.FusedMultiplyAdd(x, x, -x2);
    var y2 = y * y;
    var dy2 = Math.FusedMultiplyAdd(y, y, -y2);
    var r2 = x2 + y2;
    var ir2 = 0.5 / r2;
    var dr2 = x2 - r2 + y2 + (dx2 + dy2);
    var th = Math.Sqrt(r2);
    var rsqrt = th * ir2;
    var dz = dr2 - Math.FusedMultiplyAdd(th, th, -r2);
    var tl = rsqrt * dz;

    // Fast two-sum of th and tl.
    var s = th + tl;
    tl -= s - th;
    th = s;

    // The rounding is hard when tl is too close to a half ulp of th, or too small or too large to tell.
    var aidr = unchecked((ulong)BitConverter.DoubleToInt64Bits(Math.Abs(tl)) + (0x3feUL << 52) - ((ulong)BitConverter.DoubleToInt64Bits(th) & ExponentMask));
    var mid = unchecked(aidr - 0x3c90000000000000UL + 16) >> 5;
    var midm = unchecked(aidr - 0x3c80000000000000UL + 16) >> 5;
    if (mid == 0 || midm == 0 || aidr < 0x39b0000000000000UL || aidr > 0x3c9fffffffffff80UL)
      th = HypotExact(x, y);

    var result = unchecked((ulong)BitConverter.DoubleToInt64Bits(th) - (ulong)off);
    if (result >= 0x7ffUL << 52)
      return 1.0 / 0.0;

    return BitConverter.Int64BitsToDouble((long)result);
  }

  /// <summary>
  /// <c>sqrt(x^2 + y^2)</c> of <c>float</c>, correctly rounded to nearest with ties to even. An
  /// infinite operand gives +infinity even when the other is a NaN; otherwise a NaN gives a NaN.
  /// </summary>
  /// <param name="x">The first coordinate.</param>
  /// <param name="y">The second coordinate.</param>
  /// <returns>The correctly rounded distance from the origin.</returns>
  public static float HypotF(float x, float y) {
    var ax = MathF.Abs(x);
    var ay = MathF.Abs(y);
    var tx = (uint)BitConverter.SingleToInt32Bits(ax);
    var ty = (uint)BitConverter.SingleToInt32Bits(ay);
    if (tx >= 0xffu << 23 || ty >= 0xffu << 23) {
      // An infinity wins over a NaN.
      if (tx == 0xffu << 23)
        return ax;

      if (ty == 0xffu << 23)
        return ay;

      return ax + ay;
    }

    var at = MathF.Max(ax, ay);
    ay = MathF.Min(ax, ay);
    double xd = at;
    double yd = ay;
    var x2 = xd * xd;
    var y2 = yd * yd;
    var r2 = x2 + y2;

    // ay below 2^-13 at: the result is at rounded up as far as ay pushes it.
    if (yd < xd * 0.00024414061044808477)   // 0x1.fffffep-13
      return MathF.FusedMultiplyAdd(0.0001220703125f, ay, at);   // 0x1p-13f

    // The double square root rounds correctly to a float unless it lies close to a midpoint.
    var r = Math.Sqrt(r2);
    var t = (ulong)BitConverter.DoubleToInt64Bits(r);
    var c = (float)r;
    if (t > 0x47efffffe0000000UL || ((t + 1) & 0xfffffff) > 2)
      return c;

    double cd = c;
    if (cd * cd - x2 - y2 == 0.0)
      return c;

    // Correct r by one Newton step and move it off the midpoint towards the exact root.
    var ir2 = 0.5 / r2;
    var dr2 = x2 - r2 + y2;
    var rs = r * ir2;
    var dz = dr2 - Math.FusedMultiplyAdd(r, r, -r2);
    var dr = rs * dz;
    var rh = r + dr;
    var rl = dr + (r - rh);
    t = (ulong)BitConverter.DoubleToInt64Bits(rh);
    if ((t & 0xfffffff) == 0) {
      if (rl > 0.0)
        t += 1;
      if (rl < 0.0)
        t -= 1;
    }

    return (float)BitConverter.Int64BitsToDouble((long)t);
  }

  /// <summary>
  /// The distance of two subnormal operands, given as their encodings <paramref name="a"/> >=
  /// <paramref name="b"/> > 0: the integer square root of <c>(2a)^2 + (2b)^2</c> carries a round bit.
  /// </summary>
  private static double HypotSubnormal(ulong a, ulong b) {
    var af = (double)(long)a;
    var bf = (double)(long)b;
    a <<= 1;
    b <<= 1;
    var rm = (ulong)Math.Sqrt(af * af + bf * bf);
    var tm = (long)(rm << 1);
    var d = unchecked((long)(a * a + b * b - (ulong)tm * (ulong)tm));

    // tm = floor(sqrt(a^2 + b^2)) and 0 <= d = a^2 + b^2 - tm^2 < 2 tm + 1.
    while (d > 2 * tm) {
      d -= 2 * tm + 1;
      tm++;
    }

    while (d < 0) {
      d += 2 * tm - 1;
      tm--;
    }

    // A midpoint is impossible here: a^2 + b^2 is an integer, (k + 1/2)^2 is not.
    rm = (ulong)tm >> 1;
    if (d != 0)
      rm += (ulong)tm & 1;

    return BitConverter.Int64BitsToDouble((long)rm);
  }

  /// <summary>
  /// The correctly rounded distance of <paramref name="x"/> in [1, 2) and <paramref name="y"/> <= x,
  /// both scaled: the approximate root is stepped until its square, compared in integers with the
  /// exact <c>x^2 + y^2</c>, passes it, and then rounded.
  /// </summary>
  private static double HypotExact(double x, double y) {
    const int Shift = 2;
    var xi = (ulong)BitConverter.DoubleToInt64Bits(x);
    var yi = (ulong)BitConverter.DoubleToInt64Bits(y);
    var bm = (xi & ulong.MaxValue >> 12) | 1UL << 52;
    var lm = (yi & ulong.MaxValue >> 12) | 1UL << 52;
    var be = (int)(xi >> 52);
    var le = (int)(yi >> 52);
    var ri = (ulong)BitConverter.DoubleToInt64Bits(Math.Sqrt(x * x + y * y));
    var rm = ri & ulong.MaxValue >> 12;
    var re = (int)(ri >> 52) - 0x3ff;
    rm |= 1UL << 52;

    // Three ulps below the approximation.
    for (var i = 0; i < 3; i++) {
      if (rm == 1UL << 52) {
        rm = ulong.MaxValue >> 11;
        re--;
      } else {
        rm--;
      }
    }

    // m2 = x^2 + y^2 in the low 64 bits, with the bits of y^2 below them sticky.
    bm <<= Shift;
    var m2 = unchecked(bm * bm);
    var ls = Shift - (be - le);
    if (ls >= 0) {
      lm <<= ls;
      m2 = unchecked(m2 + lm * lm);
    } else {
      var low = unchecked(lm * lm);
      var high = MultiplyHigh(lm, lm);
      var drop = -2 * ls;
      m2 = unchecked(m2 + (low >> drop | high << (64 - drop)));
      if (low << (64 - drop) != 0)
        m2 |= 1;
    }

    var k = Shift + re;
    long d;
    do {
      rm += rm >= 1UL << 53 ? 2UL : 1UL;
      var tm = rm << k;
      d = unchecked((long)(m2 - tm * tm));
    } while (d > 0);

    if (d != 0) {
      // Compare with the square of the midpoint below rm.
      var tm = (rm << k) - (1UL << (k - (rm <= 1UL << 53 ? 1 : 0)));
      d = unchecked((long)(m2 - tm * tm));
      if (d != 0)
        rm = unchecked(rm + (ulong)(d >> 63));
      else
        rm -= rm & (1UL << (1 - (rm <= 1UL << 53 ? 1 : 0)));
    }

    if (rm >= 1UL << 53) {
      rm >>= 1;
      re++;
    }

    var e = (long)be - 1 + re;
    return BitConverter.Int64BitsToDouble(unchecked((e << 52) + (long)rm));
  }

}
