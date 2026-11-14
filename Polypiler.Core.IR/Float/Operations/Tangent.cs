// SPDX-License-Identifier: MIT
using System;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>
/// The tangent, correctly rounded. The one of <c>double</c> is converted from CORE-MATH's
/// <c>binary64/tan/tan.c</c> (MIT): a fast path in double-double arithmetic and, where its error
/// bound does not decide the rounding, an accurate path with 128-bit significands. The one of
/// <c>float</c> from CORE-MATH's <c>binary32/tan/tanf.c</c> (MIT), a rational approximation in double
/// precision. See <c>THIRD-PARTY-NOTICE.CORE-MATH.Tangent.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>Arguments of <see cref="TanF"/> whose tangent the rational approximation cannot round.</summary>
  private static readonly float[] _tanFExceptionArgument = [
    1.079082727432251f, // 0x1.143ec4p+0
    225260880.0f, // 0x1.ada6aap+27
    474309780832256.0f, // 0x1.af61dap+48
    4512995942072320.0f, // 0x1.0088bcp+52
    9.316613437639948e+21f, // 0x1.f90dfcp+72
    6.955935609971147e+25f, // 0x1.cc4e22p+85
    1.277849588535595e+26f, // 0x1.a6ce12p+86
    7.17103545462961e+30f, // 0x1.6a0b76p+102
  ];

  /// <summary>The tangents of <see cref="_tanFExceptionArgument"/>, each as the sum of two floats (high, low).</summary>
  private static readonly float[] _tanFExceptionResult = [
    1.8670953512191772f, -3.409718953073569e-16f, // 0x1.ddf9f6p+0, -0x1.891d24p-52
    0.23828700184822083f, 4.358793082672146e-18f, // 0x1.e80304p-3, 0x1.419f46p-58
    0.3445502519607544f, -3.268032112391647e-17f, // 0x1.60d1c8p-2, -0x1.2d6c3ap-55
    1.7895326614379883f, 2.1771658420191705e-16f, // 0x1.ca1edp+0, 0x1.f6053p-53
    0.6748017072677612f, 1.7449127529366843e-16f, // 0x1.597f9cp-1, 0x1.925978p-53
    -3.9000706672668457f, 8.173074377011539e-16f, // -0x1.f33584p+1, 0x1.d7254ap-51
    -0.8855070471763611f, -1.2783292861530832e-16f, // -0x1.c5612ep-1, -0x1.26c33ep-53
    -1.8912676572799683f, -2.4787918922562627e-16f, // -0x1.e42a1ep+0, -0x1.1dc906p-52
  ];

  /// <summary>
  /// The 256 bits of <c>2/pi</c> that the reduction of a <c>float</c> of at least <c>2^28</c> needs,
  /// least significant word first.
  /// </summary>
  private static readonly ulong[] _tanFTwoOverPi = [0xfe5163abdebbc562, 0xdb6295993c439041, 0xfc2757d1f534ddc0, 0xa2f9836e4e441529];

  /// <summary>
  /// The tangent of a <c>float</c>, correctly rounded to nearest. The argument is reduced to
  /// <c>2/pi * x = q + z</c> with <c>|z| &lt;= 1/2</c>, the tangent of <c>pi/2 * z</c> approximated
  /// by a rational function in double precision, and inverted (with the sign changed) for an odd
  /// <c>q</c>; the few arguments whose result that leaves undecided are looked up.
  /// </summary>
  /// <param name="x">The angle in radians.</param>
  /// <returns><c>tan(x)</c>; NaN for an infinity or a NaN.</returns>
  public static float TanF(float x) {
    var bits = (uint)BitConverter.SingleToInt32Bits(x);
    var e = (int)((bits >> 23) & 0xff);
    double z;
    int q;
    if (e < 127 + 28) {
      if (e < 115) {
        // |x| < 2^-13: tan(x) = x + x^3/3 + ..., and below 2^-26 already x + x*|x| rounds alike.
        if (e < 102)
          return MathF.FusedMultiplyAdd(x, MathF.Abs(x), x);

        var x2 = x * x;
        return MathF.FusedMultiplyAdd(x, 0.3333333432674408f * x2, x); // 0x1.555556p-2f
      }

      // 2/pi * x = q + z with 2/pi split so that its high part times x is exact.
      double wide = x;
      var low = -3.944788223297384e-10 * wide; // -0x1.b1bbead603d8bp-32
      var high = 0.6366197727620602 * wide; // 0x1.45f306ep-1
      var nearest = Math.Round(high);
      q = (int)(long)nearest;
      z = (high - nearest) + low;
    } else if (e < 0xff) {
      var quadrant = new int[1];
      z = TanFReduceLarge(bits, quadrant);
      q = quadrant[0];
    } else {
      // An infinity has no tangent; x - x is a NaN for both.
      return x - x;
    }

    var z2 = z * z;
    var z4 = z2 * z2;
    var n = 1.5707963267948966 + z2 * -0.49720165641032027; // 0x1.921fb54442d18p+0, -0x1.fd226e573289fp-2
    var n2 = 0.02683402276915988 + z2 * -0.00017660096093977045; // 0x1.b7a60c8dac9f6p-6, -0x1.725beb40f33e5p-13
    n += z4 * n2;
    var d = 1.0 + z2 * -1.1389954387488281; // 0x1p+0, -0x1.2395347fb829dp+0
    var d2 = 0.1421268437745497 + z2 * -0.0031314039049681057; // 0x1.2313660f29c36p-3, -0x1.9a707ab98d1c1p-9
    d += z4 * d2;
    n *= z;
    // n/d approximates tan(pi/2 * z); an odd quadrant takes -d/n instead.
    double s0 = q & 1;
    var s1 = 1 - s0;
    var r1 = (n * s1 - d * s0) / (n * s0 + d * s1);
    var tail = ((ulong)BitConverter.DoubleToInt64Bits(r1) + 7) & (ulong.MaxValue >> 35);
    if (tail <= 14) {
      var ax = MathF.Abs(x);
      for (var j = 0; j < 8; j++)
        if (_tanFExceptionArgument[j] == ax)
          return bits >> 31 != 0
            ? -_tanFExceptionResult[2 * j] - _tanFExceptionResult[2 * j + 1]
            : _tanFExceptionResult[2 * j] + _tanFExceptionResult[2 * j + 1];
    }

    return (float)r1;
  }

  /// <summary>
  /// Reduces a <c>float</c> with <c>2^28 &lt;= |x| &lt; 2^128</c>: <c>2/pi * x = q + z</c> with
  /// <c>|z| &lt;= 1/2</c>, from the product of the significand with 256 bits of <c>2/pi</c>.
  /// </summary>
  /// <param name="bits">The bits of <c>x</c>.</param>
  /// <param name="quadrant">Receives <c>q</c> in its first element.</param>
  /// <returns><c>z</c>.</returns>
  private static double TanFReduceLarge(uint bits, int[] quadrant) {
    var e = (int)((bits >> 23) & 0xff);
    ulong m = (bits & (~0u >> 9)) | 1u << 23;

    // The product m * 2/pi in 320 bits, as the carry chain p0 .. p3 of 128-bit partial products.
    var p0High = MultiplyHigh(m, _tanFTwoOverPi[0]);
    var p1Low = unchecked(m * _tanFTwoOverPi[1] + p0High);
    var p1High = MultiplyHigh(m, _tanFTwoOverPi[1]) + TanBelow(p1Low, p0High);
    var p2Low = unchecked(m * _tanFTwoOverPi[2] + p1High);
    var p2High = MultiplyHigh(m, _tanFTwoOverPi[2]) + TanBelow(p2Low, p1High);
    var p3Low = unchecked(m * _tanFTwoOverPi[3] + p2High);
    var p3High = MultiplyHigh(m, _tanFTwoOverPi[3]) + TanBelow(p3Low, p2High);

    // 28 <= k <= 127 and 5 <= s <= 104: the integer part i and the fraction a, shifted out of the product.
    var k = e - 127;
    var s = k - 23;
    int i;
    long a;
    if (s < 64) {
      i = (int)(p3High << s | p3Low >> (64 - s));
      a = (long)(p3Low << s | p2Low >> (64 - s));
    } else if (s == 64) {
      i = (int)p3Low;
      a = (long)p2Low;
    } else {
      i = (int)(p3Low << (s - 64) | p2Low >> (128 - s));
      a = (long)(p2Low << (s - 64) | p1Low >> (128 - s));
    }

    // Round the quotient to nearest (a negative fraction borrows from i), then apply the sign of x.
    var sign = (int)bits >> 31;
    var borrow = a >> 63;
    i = (int)(i - borrow);
    var z = (a ^ sign) * 5.421010862427522e-20; // 0x1p-64
    quadrant[0] = (i ^ sign) - sign;
    return z;
  }

  /// <summary>
  /// The tangent of a <c>double</c>, correctly rounded to nearest. Below
  /// <c>0x1.d12ed0af1a27ep-27</c> the result is <c>x + x^3/3</c> rounded; above, a fast path in
  /// double-double arithmetic yields an approximation and an error bound, and when the two ends of
  /// that interval round differently, the accurate path recomputes in 128-bit arithmetic.
  /// </summary>
  /// <param name="x">The angle in radians.</param>
  /// <returns><c>tan(x)</c>; NaN for an infinity or a NaN.</returns>
  public static double Tan(double x) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    var e = (int)((bits >> 52) & 0x7ff);
    if (e == 0x7ff)
      return bits << 1 == 0x7ffUL << 53 ? x - x : x + x;

    // For |x| <= 0x1.d12ed0af1a27ep-27, |tan(x) - x| < x^3/3 is below half an ulp of x.
    if ((bits & 0x7fffffffffffffffUL) <= 0x3e4d12ed0af1a27eUL)
      return x == 0 ? x : Math.FusedMultiplyAdd(x, 5.551115123125783e-17, x); // 0x1p-54

    var approximation = new double[2];
    var err = TanFast(approximation, x);
    var left = approximation[0] + (approximation[1] - err);
    var right = approximation[0] + (approximation[1] + err);
    return left == right ? left : TanAccurate(x);
  }

  /// <summary>
  /// The fast path of <see cref="Tan"/> for a finite <c>|x| &gt; 0x1.d12ed0af1a27ep-27</c>: the
  /// reduction <c>i/2^11 + h + l</c> of <c>frac(x/(2pi))</c> to the first octant, the sine and
  /// cosine of <c>h + l</c> by polynomials, combined with the table values at <c>i</c>, and their
  /// quotient.
  /// </summary>
  /// <param name="result">Receives the approximation <c>h + l</c> of <c>tan(x)</c> in its first two elements.</param>
  /// <param name="x">The angle.</param>
  /// <returns>The bound on <c>|h + l - tan(x)|</c>; 1 where the reduction is too close to 0 for the bound.</returns>
  private static double TanFast(double[] result, double x) {
    var negative = x < 0;
    var isTan = true;
    var absx = negative ? -x : x;
    var i = TanReduceFast(result, absx);
    var h = result[0];
    var l = result[1];

    // tan(pi + x) = tan(x); tan(pi/2 + x) = -cot(x); tan(pi/2 - x) = cot(x).
    i &= 0x3ff;
    if ((i & 0x200) != 0) {
      isTan = !isTan;
      negative = !negative;
    }

    i &= 0x1ff;
    if ((i & 0x100) != 0) {
      isTan = !isTan;
      i = 0x1ff - i;
      h = 0.00048828125 - h; // 0x1p-11 - h, exact
      l = -l;
    }

    // Too close to a multiple of pi for the relative error bound: the accurate path decides.
    if (i == 0 && h < 7.275957614183426e-12) { // 0x1p-37
      result[0] = h;
      result[1] = l;
      return 1.0;
    }

    // The table holds sin2pi and cos2pi at i/2^11 + SC[3i]; h - SC[3i] is exact.
    var sc = 3 * i;
    h -= _tanSineCosine[sc];
    var sum = h + l;
    l -= sum - h;
    h = sum;

    // (h + l)^2 as uh + ul.
    var uh = h * h;
    var ul = Math.FusedMultiplyAdd(h, h, -uh);
    ul = Math.FusedMultiplyAdd(h + h, l, ul);

    // sin2pi(h + l): odd polynomial of degree 7, its leading coefficient in double-double.
    var p = -76.77083125858339; // -0x1.331554ca19669p6
    p = Math.FusedMultiplyAdd(p, uh, 81.60524929831949); // 0x1.466bc678d8d63p6
    p = Math.FusedMultiplyAdd(p, uh, -41.34170224039976); // -0x1.4abbce625be53p5
    var ph = p * uh;
    var pl = Math.FusedMultiplyAdd(p, uh, -ph);
    pl = Math.FusedMultiplyAdd(p, ul, pl);
    sum = 6.283185307179586 + ph; // 0x1.921fb54442d18p+2
    var t = ph - (sum - 6.283185307179586);
    pl += 2.4492937487389357e-16 + t; // 0x1.1a62645446203p-52
    ph = sum;
    var sh = ph * h;
    var s = Math.FusedMultiplyAdd(ph, h, -sh);
    s = Math.FusedMultiplyAdd(pl, h, s);
    var sl = Math.FusedMultiplyAdd(ph, l, s);

    // cos2pi(h + l): even polynomial of degree 6, its constant term in double-double.
    p = -85.41188646817469; // -0x1.55a5c590f9e6ap6
    p = Math.FusedMultiplyAdd(p, uh, 64.93939400729349); // 0x1.03c1f080ad892p6
    p = Math.FusedMultiplyAdd(p, uh, -19.739208802178716); // -0x1.3bd3cc9be45dep4
    ph = p * uh;
    pl = Math.FusedMultiplyAdd(p, uh, -ph);
    pl = Math.FusedMultiplyAdd(p, ul, pl);
    var ch = 1.0 + ph;
    t = ph - (ch - 1.0);
    var cl = pl + (-1.0396311785461432e-23 + t); // -0x1.923015cp-77

    // sin2pi(R) = sin2pi(xi) * cos2pi(h + l) + cos2pi(xi) * sin2pi(h + l), R = i/2^11 + h + l.
    var sinAt = _tanSineCosine[sc + 1];
    var cosAt = _tanSineCosine[sc + 2];
    var sh0 = cosAt * sh;
    var sl0 = Math.FusedMultiplyAdd(cosAt, sh, -sh0);
    sl0 = Math.FusedMultiplyAdd(cosAt, sl, sl0);
    var ch0 = sinAt * ch;
    var cl0 = Math.FusedMultiplyAdd(sinAt, ch, -ch0);
    cl0 = Math.FusedMultiplyAdd(sinAt, cl, cl0);
    var h1 = ch0 + sh0;
    var l1 = sh0 - (h1 - ch0);
    l1 += sl0 + cl0;

    // cos2pi(R) = cos2pi(xi) * cos2pi(h + l) - sin2pi(xi) * sin2pi(h + l).
    var product = cosAt * ch;
    cl = Math.FusedMultiplyAdd(cosAt, cl, Math.FusedMultiplyAdd(cosAt, ch, -product));
    ch = product;
    product = sinAt * sh;
    sl = Math.FusedMultiplyAdd(sinAt, sl, Math.FusedMultiplyAdd(sinAt, sh, -product));
    sh = product;
    var h2 = ch + -sh;
    var l2 = -sh - (h2 - ch);
    l2 += cl - sl;

    // The quotient by Karp-Markstein: z = b * y with y = 1/a, corrected by y * (b - a * z).
    double bh, bl, ah, al;
    if (isTan) {
      bh = h1;
      bl = l1;
      ah = h2;
      al = l2;
    } else {
      bh = h2;
      bl = l2;
      ah = h1;
      al = l1;
    }

    var y = 1.0 / ah;
    h = bh * y;
    var eh = Math.FusedMultiplyAdd(ah, -h, bh);
    var el = Math.FusedMultiplyAdd(al, -h, bl);
    l = y * (eh + el);

    var sign = negative ? -1.0 : 1.0;
    h *= sign;
    l *= sign;
    result[0] = h;
    result[1] = l;
    return h * 1.4928955695357043e-20; // 0x1.1ap-66 > 2^-65.864
  }

  /// <summary>
  /// Reduces a finite <c>x &gt; 0x1.7137449123ef6p-26</c>: <c>i/2^11 + h + l</c> approximates
  /// <c>frac(x/(2pi))</c> to <c>2^-104.815</c>, with <c>0 &lt;= h &lt; 2^-11</c>. Below <c>2pi</c>
  /// by a double-double <c>1/(2pi)</c>; above, from the words of <c>1/(2pi)</c> that the exponent of
  /// <c>x</c> selects.
  /// </summary>
  /// <param name="result">Receives <c>h</c> and <c>l</c> in its first two elements.</param>
  /// <param name="x">The angle.</param>
  /// <returns><c>i</c>, below <c>2^11</c>.</returns>
  private static int TanReduceFast(double[] result, double x) {
    double h;
    if (x <= 6.283185307179585) { // 0x1.921fb54442d17p+2
      // CH + CL approximates 1/(2pi) to 2^-110.523.
      h = 0.15915494309189535 * x; // 0x1.45f306dc9c883p-3
      var l = Math.FusedMultiplyAdd(0.15915494309189535, x, -h);
      result[1] = Math.FusedMultiplyAdd(-9.839338337591243e-18, x, l); // -0x1.6b01ec5417056p-57
    } else {
      var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
      var e = (int)((bits >> 52) & 0x7ff);
      var m = (1UL << 52) | (bits & 0xfffffffffffffUL);
      ulong c0;
      ulong c1;
      ulong c2;
      ulong u;
      if (e <= 1074) {
        // 2^2 <= x < 2^52: m * (T[0] + T[1]/2^64 + T[2]/2^128), the low half of m * T[2] dropped.
        var v = MultiplyHigh(m, _tanInverseTwoPi[2]);
        u = unchecked(m * _tanInverseTwoPi[1]);
        c0 = unchecked(u + v);
        c1 = MultiplyHigh(m, _tanInverseTwoPi[1]) + TanBelow(c0, u);
        u = unchecked(m * _tanInverseTwoPi[0]);
        c1 = unchecked(c1 + u);
        c2 = MultiplyHigh(m, _tanInverseTwoPi[0]) + TanBelow(c1, u);
        // The low 1075 - e bits of c2 belong to the fraction.
        e = 1075 - e;
      } else {
        // 2^52 <= x: only the words T[i] .. T[i+3] reach the fraction.
        var i = (e - 1138 + 63) / 64;
        var v = MultiplyHigh(m, _tanInverseTwoPi[i + 3]);
        u = unchecked(m * _tanInverseTwoPi[i + 2]);
        c0 = unchecked(u + v);
        c1 = MultiplyHigh(m, _tanInverseTwoPi[i + 2]) + TanBelow(c0, u);
        u = unchecked(m * _tanInverseTwoPi[i + 1]);
        c1 = unchecked(c1 + u);
        c2 = MultiplyHigh(m, _tanInverseTwoPi[i + 1]) + TanBelow(c1, u);
        c2 = unchecked(c2 + m * _tanInverseTwoPi[i]);
        e = 1139 + (i << 6) - e;
      }

      // Drop the integer bits: keep the e low bits of c2 and the words below.
      if (e == 64) {
        c0 = c1;
        c1 = c2;
      } else {
        c0 = (c1 << (64 - e)) | c0 >> e;
        c1 = (c2 << (64 - e)) | c1 >> e;
      }

      TanFractionToDoubleDouble(result, c1, c0);
      h = result[0];
    }

    var index = Math.Floor(h * 2048.0);
    result[0] = Math.FusedMultiplyAdd(index, -0.00048828125, h); // -0x1p-11
    return (int)index;
  }

  /// <summary>
  /// <c>c1/2^64 + c0/2^128</c> as a double-double <c>h + l</c>, truncated. The fraction of an
  /// argument of a double is at least <c>2^-62</c>, so <c>c1</c> is never zero.
  /// </summary>
  /// <param name="result">Receives <c>h</c> and <c>l</c> in its first two elements.</param>
  /// <param name="c1">The upper word.</param>
  /// <param name="c0">The lower word.</param>
  private static void TanFractionToDoubleDouble(double[] result, ulong c1, ulong c0) {
    var e = (ulong)BitOperations.LeadingZeroCount(c1);
    if (e != 0) {
      c1 = (c1 << (int)e) | (c0 >> (int)(64 - e));
      c0 <<= (int)e;
    }

    var f = 0x3fe - e;
    result[0] = BitConverter.Int64BitsToDouble((long)((f << 52) | ((c1 << 1) >> 12)));
    c0 = (c1 << 53) | (c0 >> 11);
    if (c0 == 0) {
      result[1] = 0;
      return;
    }

    var g = BitOperations.LeadingZeroCount(c0);
    c0 <<= g;
    result[1] = BitConverter.Int64BitsToDouble((long)(((f - 53 - (ulong)g) << 52) | ((c0 << 1) >> 12)));
  }

  /// <summary>1 if <c>x &lt; y</c>, else 0: the carry out of a sum <c>x = y + z</c>, or the borrow of <c>y - x</c>.</summary>
  private static ulong TanBelow(ulong x, ulong y) => x < y ? 1UL : 0UL;

}
