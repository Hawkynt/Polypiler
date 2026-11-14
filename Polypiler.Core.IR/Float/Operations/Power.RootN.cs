using System;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>
/// <c>rootn</c> of IEEE 754-2019 §9.2, the n-th root. No correctly rounded <c>rootn</c> with a
/// compatible licence exists to convert, so it is derived from CORE-MATH's <c>pow</c> (MIT): the
/// special cases are those of .NET's <c>Double.RootN</c> and <c>Single.RootN</c> (MIT), and
/// otherwise <c>|x|^(1/n)</c> is computed by <see cref="FloatOperations.Pow"/>'s logarithm and
/// exponential with the exponent <c>1/n</c> held in more precision than a double: a double-double
/// in the first phase, 256 bits in the last. See <c>THIRD-PARTY-NOTICE.CORE-MATH.Power.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// The <paramref name="n"/>-th root of <paramref name="x"/>, <c>x^(1/n)</c>, within one unit of the
  /// last place of the correctly rounded result (and correctly rounded except possibly where the
  /// exact root lies within 2^-240 of a rounding boundary). The special cases follow .NET's
  /// <c>Double.RootN</c>: <c>n = 0</c> gives NaN, <c>n = 1</c> gives <c>x</c>, <c>n = 2</c> the square
  /// root, <c>n = 3</c> <see cref="Cbrt"/>; a negative <c>x</c> with an even <c>n</c> gives NaN and an
  /// odd <c>n</c> keeps the sign; <c>rootn(±0, n)</c> is ±0 (+0 for an even <c>n</c>) for
  /// <c>n &gt; 0</c> and ±∞ (+∞ for an even <c>n</c>) for <c>n &lt; 0</c>; <c>rootn(+∞, n)</c> is +∞
  /// or +0, <c>rootn(-∞, n)</c> -∞ or -0 for an odd <c>n</c>.
  /// </summary>
  /// <remarks>
  /// The derivation from CORE-MATH's <c>pow</c>: the first phase forms <c>log|x|</c> as a
  /// double-double, multiplies it by <c>1/n = yh + yl</c> (<c>yl</c> the exact remainder of the
  /// division, rounded once) and takes the exponential as a double-double; the term <c>yl * log|x|</c>
  /// that <c>pow</c> does not have adds less than 2^-66.8 to the error, so the rounding test widens
  /// <c>pow</c>'s bound by 2^-66. When that test fails, the third phase computes the logarithm and the
  /// exponential with 256 bits, the exponent <c>1/n</c> truncated to 256 bits by long division, and
  /// rounds that once. An exact root is found by the first phase already, and no root can be a
  /// midpoint of two doubles (its <c>n</c>-th power would need more than 53 bits).
  /// </remarks>
  /// <param name="x">The radicand.</param>
  /// <param name="n">The degree of the root.</param>
  /// <returns>The root.</returns>
  public static double RootN(double x, int n) {
    if (n == 0)
      return double.NaN;

    if (n == 1)
      return x;

    if (n == 2)
      return x != 0.0 ? Math.Sqrt(x) : 0.0;

    if (n == 3)
      return Cbrt(x);

    if (n == -1)
      return 1.0 / x;

    var odd = (n & 1) != 0;
    if (!IsFinite(x)) {
      if (IsNaN(x))
        return x;

      if (x > 0)
        return n > 0 ? double.PositiveInfinity : 0.0;

      if (!odd)
        return double.NaN;

      return n > 0 ? double.NegativeInfinity : -0.0;
    }

    if (x == 0.0) {
      if (n > 0)
        return odd ? x : 0.0;

      return odd ? 1.0 / x : double.PositiveInfinity;
    }

    if (x < 0.0 && !odd)
      return double.NaN;

    var s = x < 0.0 ? -1.0 : 1.0;
    var ax = Math.Abs(x);
    var pair = new double[2];
    var result = RootNFirstPhase(ax, n, s, pair);
    if (!IsNaN(result))
      return result;

    var z = RootNThirdPhase(ax, n);
    z[5] = s < 0.0 ? 1UL : 0UL;
    z[3] &= ulong.MaxValue << 10;
    return PowQintToDouble(z);
  }

  /// <summary>
  /// The <paramref name="n"/>-th root of a <c>float</c>; see <see cref="RootN"/> for the special
  /// cases, with <see cref="CbrtF"/> for <c>n = 3</c>. The root is computed in double precision by the
  /// machinery of <see cref="RootN"/> and rounded once to <c>float</c>: the first phase decides on
  /// which side of the nearest midpoint of two floats the root lies, and when it cannot, the 256-bit
  /// root is rounded to <c>float</c> directly.
  /// </summary>
  /// <param name="x">The radicand.</param>
  /// <param name="n">The degree of the root.</param>
  /// <returns>The root.</returns>
  public static float RootNF(float x, int n) {
    if (n == 0)
      return float.NaN;

    if (n == 1)
      return x;

    if (n == 2)
      return x != 0.0f ? MathF.Sqrt(x) : 0.0f;

    if (n == 3)
      return CbrtF(x);

    if (n == -1)
      return 1.0f / x;

    var odd = (n & 1) != 0;
    if (!IsFiniteF(x)) {
      if (IsNaNF(x))
        return x;

      if (x > 0)
        return n > 0 ? float.PositiveInfinity : 0.0f;

      if (!odd)
        return float.NaN;

      return n > 0 ? float.NegativeInfinity : -0.0f;
    }

    if (x == 0.0f) {
      if (n > 0)
        return odd ? x : 0.0f;

      return odd ? 1.0f / x : float.PositiveInfinity;
    }

    if (x < 0.0f && !odd)
      return float.NaN;

    var s = x < 0.0f ? -1.0 : 1.0;
    double ax = MathF.Abs(x);
    var pair = new double[2];
    var resH = RootNExponential(ax, n, pair);
    var resL = pair[0];
    var err = pair[1];

    // The root of a float lies within 2^-75 and 2^75, in the normal range of float. mid is the
    // midpoint of the two floats around resH; the root lies on the side of it that resH + resL does.
    var bits = BitConverter.DoubleToInt64Bits(resH) & ~0x1fffffffL;
    var mid = BitConverter.Int64BitsToDouble(bits | 0x10000000L);
    var d = resH - mid + resL;
    if (Math.Abs(d) > 2.0 * err * resH)
      return (float)(s * BitConverter.Int64BitsToDouble(d > 0.0 ? bits + 0x20000000L : bits));

    // Round the 256-bit root to the 24 bits of float. The root is no midpoint of two floats, so the
    // round bit alone decides.
    var z = RootNThirdPhase(ax, n);
    var m = (z[0] >> 40) + ((z[0] >> 39) & 1);
    return (float)(s * ScaleB(m, (int)((long)z[4] - 23)));
  }

  /// <summary>
  /// The first phase of <see cref="RootN"/>: <c>s * |x|^(1/n)</c> when the rounding test decides it,
  /// else NaN.
  /// </summary>
  private static double RootNFirstPhase(double ax, int n, double s, double[] pair) {
    var resH = RootNExponential(ax, n, pair);
    var resL = pair[0];
    var err = pair[1];
    var resMin = resH + Math.FusedMultiplyAdd(err, -resH, resL);
    var resMax = resH + Math.FusedMultiplyAdd(err, resH, resL);
    if (resMin == resMax)
      return s * resMax;

    return double.NaN;
  }

  /// <summary>
  /// <c>ax^(1/n)</c> for a positive finite <paramref name="ax"/> as a double-double: returns the high
  /// part, writes the low one into <c>pair[0]</c> and the bound of the relative error into
  /// <c>pair[1]</c>.
  /// </summary>
  private static double RootNExponential(double ax, int n, double[] pair) {
    var cancel = PowLog1(ax, pair);
    var lh = pair[0];
    var ll = pair[1];

    // 1/n = yh + yl, yl from the exact remainder 1 - yh * n.
    var yh = 1.0 / n;
    var yl = Math.FusedMultiplyAdd(-yh, n, 1.0) / n;
    var rh = yh * lh;
    var rl = Math.FusedMultiplyAdd(yl, lh, Math.FusedMultiplyAdd(yh, ll, Math.FusedMultiplyAdd(yh, lh, -rh)));
    var resH = PowExp1(rh, rl, 1.0, pair);

    // pow's bounds 0x1.57p-58 and 0x1.27p-64, widened by 2^-66.
    pair[1] = (cancel == 1 ? 4.6485168145316E-18 : 6.246867986000465E-20) + 1.3552527156068805E-20;
    return resH;
  }

  /// <summary>
  /// The third phase of <see cref="RootN"/>: <c>ax^(1/n)</c> as a positive 256-bit number, from
  /// <see cref="Pow"/>'s 256-bit logarithm and exponential.
  /// </summary>
  private static ulong[] RootNThirdPhase(double ax, int n) {
    var qx = PowQintFromDouble(ax);
    var qr = new ulong[6];
    PowLog3(qr, qx);
    PowMultiplyQint(qr, qr, RootNReciprocal(n), 0, 0);
    var qz = new ulong[6];
    PowExp3(qz, qr);
    return qz;
  }

  /// <summary>
  /// <c>1/n</c> for <c>|n| &gt;= 2</c> as a 256-bit number, truncated: the significand is
  /// <c>floor(2^(k+256) / |n|)</c> with <c>2^k &lt; |n| &lt;= 2^(k+1)</c>, by long division.
  /// </summary>
  private static ulong[] RootNReciprocal(int n) {
    var divisor = n < 0 ? 0UL - (ulong)(long)n : (ulong)n;
    var k = 63 - BitOperations.LeadingZeroCount(divisor - 1);
    var remainder = 1UL << k;
    var q = new ulong[6];
    for (var i = 0; i < 256; i++) {
      remainder <<= 1;
      var bit = 0UL;
      if (remainder >= divisor) {
        remainder -= divisor;
        bit = 1;
      }

      q[0] = q[0] << 1 | q[1] >> 63;
      q[1] = q[1] << 1 | q[2] >> 63;
      q[2] = q[2] << 1 | q[3] >> 63;
      q[3] = q[3] << 1 | bit;
    }

    q[4] = (ulong)(long)(-(k + 1));
    q[5] = n < 0 ? 1UL : 0UL;
    return q;
  }

}
