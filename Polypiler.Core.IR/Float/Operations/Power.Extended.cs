using System;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>
/// The second and third phase of <see cref="FloatOperations.Pow"/>: CORE-MATH's 128-bit
/// (<c>dint.h</c>) and 256-bit (<c>qint.h</c>) floating-point numbers and the logarithm and
/// exponential built on them. A 128-bit number is a <c>ulong[4]</c> { hi, lo, ex, sgn } with the
/// value <c>(-1)^sgn * (hi + lo/2^64) * 2^(ex-63)</c>, <c>ex</c> held in two's complement; a 256-bit
/// number a <c>ulong[6]</c> { hh, hl, lh, ll, ex, sgn } with four limbs, most significant first.
/// </summary>
public static partial class FloatOperations {

  /// <summary>A 128-bit number from its fields.</summary>
  private static ulong[] PowDint(ulong hi, ulong lo, long ex, ulong sgn) => new ulong[] { hi, lo, (ulong)ex, sgn };

  /// <summary>The numbers of the tables of 128-bit and 256-bit numbers, for <see cref="PowDintAt"/> and <see cref="PowQintAt"/>.</summary>
  private const int PowTableInverse21 = 0;
  private const int PowTableInverse22 = 1;
  private const int PowTableLogInverse21 = 2;
  private const int PowTableLogInverse22 = 3;
  private const int PowTableExp2High2 = 4;
  private const int PowTableExp2Low2 = 5;
  private const int PowTableLogPolynomial2 = 6;
  private const int PowTableExpPolynomial2 = 7;
  private const int PowTableInverse31 = 8;
  private const int PowTableInverse32 = 9;
  private const int PowTableLogInverse31 = 10;
  private const int PowTableLogInverse32 = 11;
  private const int PowTableExp2High3 = 12;
  private const int PowTableExp2Low3 = 13;
  private const int PowTableLogPolynomial3 = 14;
  private const int PowTableExpPolynomial3 = 15;

  /// <summary>Entry <paramref name="index"/> of the table of 128-bit numbers <paramref name="table"/>.</summary>
  private static ulong[] PowDintAt(int table, int index) {
    switch (table) {
      case PowTableInverse21:
        return PowDint(_powInverse21Bits[2 * index], _powInverse21Bits[2 * index + 1], _powInverse21Scale[2 * index], (ulong)_powInverse21Scale[2 * index + 1]);
      case PowTableInverse22:
        return PowDint(_powInverse22Bits[2 * index], _powInverse22Bits[2 * index + 1], _powInverse22Scale[2 * index], (ulong)_powInverse22Scale[2 * index + 1]);
      case PowTableLogInverse21:
        return PowDint(_powLogInverse21Bits[2 * index], _powLogInverse21Bits[2 * index + 1], _powLogInverse21Scale[2 * index], (ulong)_powLogInverse21Scale[2 * index + 1]);
      case PowTableLogInverse22:
        return PowDint(_powLogInverse22Bits[2 * index], _powLogInverse22Bits[2 * index + 1], _powLogInverse22Scale[2 * index], (ulong)_powLogInverse22Scale[2 * index + 1]);
      case PowTableExp2High2:
        return PowDint(_powExp2High2Bits[2 * index], _powExp2High2Bits[2 * index + 1], _powExp2High2Scale[2 * index], (ulong)_powExp2High2Scale[2 * index + 1]);
      case PowTableExp2Low2:
        return PowDint(_powExp2Low2Bits[2 * index], _powExp2Low2Bits[2 * index + 1], _powExp2Low2Scale[2 * index], (ulong)_powExp2Low2Scale[2 * index + 1]);
      case PowTableLogPolynomial2:
        return PowDint(_powLogPolynomial2Bits[2 * index], _powLogPolynomial2Bits[2 * index + 1], _powLogPolynomial2Scale[2 * index], (ulong)_powLogPolynomial2Scale[2 * index + 1]);
      default:
        return PowDint(_powExpPolynomial2Bits[2 * index], _powExpPolynomial2Bits[2 * index + 1], _powExpPolynomial2Scale[2 * index], (ulong)_powExpPolynomial2Scale[2 * index + 1]);
    }
  }

  /// <summary>Copies a 128-bit number.</summary>
  private static void PowCopyDint(ulong[] r, ulong[] a) {
    r[0] = a[0];
    r[1] = a[1];
    r[2] = a[2];
    r[3] = a[3];
  }

  /// <summary>Sets a 128-bit number to the zero that converts to the double 0 (upstream <c>ZERO</c>).</summary>
  private static void PowSetDintZero(ulong[] r) {
    r[0] = 0;
    r[1] = 0;
    r[2] = unchecked((ulong)-1076L);
    r[3] = 0;
  }

  /// <summary>Compares the magnitudes of two 128-bit numbers: -1, 0 or +1 (upstream <c>cmp_dint_abs</c>).</summary>
  private static int PowCompareDintAbs(ulong[] a, ulong[] b) {
    if (a[0] == 0)
      return b[0] == 0 ? 0 : -1;

    if (b[0] == 0)
      return 1;

    if (a[2] != b[2])
      return (long)a[2] > (long)b[2] ? 1 : -1;

    if (a[0] != b[0])
      return a[0] > b[0] ? 1 : -1;

    if (a[1] != b[1])
      return a[1] > b[1] ? 1 : -1;

    return 0;
  }

  /// <summary>The high limb of a 128-bit integer shifted left by 0 to 127 bits.</summary>
  private static ulong PowShiftLeftHigh(ulong h, ulong l, int k) {
    if (k == 0)
      return h;

    return k < 64 ? h << k | l >> (64 - k) : l << (k - 64);
  }

  /// <summary>The low limb of a 128-bit integer shifted left by 0 to 127 bits.</summary>
  private static ulong PowShiftLeftLow(ulong l, int k) => k < 64 ? l << k : 0;

  /// <summary>The low limb of a 128-bit integer shifted right by 1 to 127 bits.</summary>
  private static ulong PowShiftRightLow(ulong h, ulong l, int k) => k < 64 ? l >> k | h << (64 - k) : h >> (k - 64);

  /// <summary>The high limb of a 128-bit integer shifted right by 0 to 127 bits.</summary>
  private static ulong PowShiftRightHigh(ulong h, int k) => k < 64 ? h >> k : 0;

  /// <summary>Whether the 128-bit integer (h1, l1) is below (h2, l2).</summary>
  private static bool PowLess128(ulong h1, ulong l1, ulong h2, ulong l2) => h1 < h2 || h1 == h2 && l1 < l2;

  /// <summary>
  /// <c>r = a + b</c> with an error of at most 2 ulps, none when Sterbenz's lemma applies (upstream
  /// <c>add_dint</c>). <paramref name="r"/> may be one of the operands.
  /// </summary>
  private static void PowAddDint(ulong[] r, ulong[] a, ulong[] b) {
    if ((a[0] | a[1]) == 0) {
      PowCopyDint(r, b);
      return;
    }

    // Opposite numbers give zero. Two equal numbers of the same sign take the general path, which
    // gives upstream's a with the exponent raised by one.
    var order = PowCompareDintAbs(a, b);
    if (order == 0 && a[3] != b[3]) {
      PowSetDintZero(r);
      return;
    }

    if (order < 0) {
      var swap = a;
      a = b;
      b = swap;
    }

    // Now |a| >= |b|: align b.
    var ah = a[0];
    var al = a[1];
    var bh = b[0];
    var bl = b[1];
    var k = a[2] - b[2];
    if (k > 0) {
      if (k < 128) {
        bl = PowShiftRightLow(bh, bl, (int)k);
        bh = PowShiftRightHigh(bh, (int)k);
      } else {
        bh = 0;
        bl = 0;
      }
    }

    var sgn = a[3];
    var ex = (long)a[2];
    ulong ch;
    ulong cl;
    if ((a[3] ^ b[3]) != 0) {
      cl = al - bl;
      ch = ah - bh - (al < bl ? 1UL : 0UL);
      var shift = ch != 0 ? BitOperations.LeadingZeroCount(ch) : 64 + BitOperations.LeadingZeroCount(cl);
      if (shift > 0) {
        // Subtract again from the operands shifted first, so that no bit of b is lost.
        var sh = PowShiftLeftHigh(ah, al, shift);
        var sl = PowShiftLeftLow(al, shift);
        ulong th;
        ulong tl;
        if (k == 1) {
          th = PowShiftLeftHigh(b[0], b[1], shift - 1);
          tl = PowShiftLeftLow(b[1], shift - 1);
        } else {
          th = PowShiftLeftHigh(bh, bl, shift);
          tl = PowShiftLeftLow(bl, shift);
        }

        cl = sl - tl;
        ch = sh - th - (sl < tl ? 1UL : 0UL);
        ex -= shift;
        shift = BitOperations.LeadingZeroCount(ch);
      }

      ch = PowShiftLeftHigh(ch, cl, shift);
      cl = PowShiftLeftLow(cl, shift);
      ex -= shift;
    } else {
      cl = al + bl;
      ch = ah + bh + (cl < al ? 1UL : 0UL);
      if (PowLess128(ch, cl, ah, al)) {
        cl = ch << 63 | cl >> 1;
        ch = 1UL << 63 | ch >> 1;
        ex++;
      }
    }

    r[0] = ch;
    r[1] = cl;
    r[2] = (ulong)ex;
    r[3] = sgn;
  }

  /// <summary>
  /// <c>r = a + b</c> of the high limbs only, with an error of at most 2 ulps of 64 bits; the low
  /// limb of <paramref name="r"/> is left as it was (upstream <c>add_dint_11</c>). It adds a
  /// coefficient <paramref name="a"/> of a polynomial to the product <paramref name="b"/> of the
  /// previous sum and the variable, which the error analysis of <c>p_2</c> and <c>q_2</c> bounds well
  /// below <c>|a|</c> and below the next binade of the sum; the cases of upstream for a zero or
  /// smaller <paramref name="a"/>, equal magnitudes and a carry out of the sum cannot arise there.
  /// </summary>
  private static void PowAddDint11(ulong[] r, ulong[] a, ulong[] b) {
    if (b[0] == 0) {
      PowCopyDint(r, a);
      return;
    }

    var bigA = a[0];
    var k = a[2] - b[2];
    var bigB = k < 64 ? b[0] >> (int)k : 0;
    var ex = (long)a[2];
    ulong c;
    if ((a[3] ^ b[3]) != 0) {
      c = bigA - bigB;
      var shift = BitOperations.LeadingZeroCount(c);
      if (shift > 0) {
        // Subtract again from the operands shifted first, so that no bit of b is lost.
        c = (bigA << shift) - (bigB << shift);
        ex -= shift;
        shift = BitOperations.LeadingZeroCount(c);
      }

      c <<= shift;
      ex -= shift;
    } else {
      c = bigA + bigB;
    }

    r[0] = c;
    r[2] = (ulong)ex;
    r[3] = a[3];
  }

  /// <summary>
  /// Normalizes the 128-bit product (h, l) of two significands into <paramref name="r"/>: shifted
  /// left by one bit when its top bit is clear.
  /// </summary>
  private static void PowNormalizeDintProduct(ulong[] r, ulong h, ulong l, long ex, ulong sgn) {
    var top = h >> 63;
    if (top == 0) {
      h = h << 1 | l >> 63;
      l <<= 1;
    }

    r[0] = h;
    r[1] = l;
    r[2] = (ulong)(ex + (long)top);
    r[3] = sgn;
  }

  /// <summary><c>r = a * b</c> with an error of at most 6 ulps (upstream <c>mul_dint</c>).</summary>
  private static void PowMultiplyDint(ulong[] r, ulong[] a, ulong[] b) {
    var m1 = MultiplyHigh(a[0], b[1]);
    var m2 = MultiplyHigh(a[1], b[0]);
    var h = MultiplyHigh(a[0], b[0]);
    var l = a[0] * b[0];
    var t = m1 + m2;
    var tc = t < m1 ? 1UL : 0UL;
    l += t;
    h += tc + (l < t ? 1UL : 0UL);
    PowNormalizeDintProduct(r, h, l, (long)a[2] + (long)b[2], a[3] ^ b[3]);
  }

  /// <summary><c>r = a * b</c> for a <paramref name="b"/> with a zero low limb, error at most 2 ulps (upstream <c>mul_dint_21</c>).</summary>
  private static void PowMultiplyDint21(ulong[] r, ulong[] a, ulong[] b) {
    var h = MultiplyHigh(a[0], b[0]);
    var l = a[0] * b[0];
    var t = MultiplyHigh(a[1], b[0]);
    l += t;
    h += l < t ? 1UL : 0UL;
    PowNormalizeDintProduct(r, h, l, (long)a[2] + (long)b[2], a[3] ^ b[3]);
  }

  /// <summary><c>r = a * b</c> of the high limbs, exact (upstream <c>mul_dint_11</c>).</summary>
  private static void PowMultiplyDint11(ulong[] r, ulong[] a, ulong[] b)
    => PowNormalizeDintProduct(r, MultiplyHigh(a[0], b[0]), a[0] * b[0], (long)a[2] + (long)b[2], a[3] ^ b[3]);

  /// <summary><c>r = a * b</c> for an integer <paramref name="b"/>, error below 1 ulp (upstream <c>mul_dint_int64</c>).</summary>
  private static void PowMultiplyDintByInteger(ulong[] r, ulong[] a, long b) {
    if (b == 0) {
      PowSetDintZero(r);
      return;
    }

    var c = b < 0 ? 0UL - (ulong)b : (ulong)b;
    var sgn = a[3] ^ (b < 0 ? 1UL : 0UL);
    var ex = (long)a[2] + 64;
    var h = MultiplyHigh(a[0], c);
    var l = a[0] * c;

    // For c = 1 the high limb is zero.
    var m = h != 0 ? BitOperations.LeadingZeroCount(h) : 64;
    h = PowShiftLeftHigh(h, l, m);
    l = PowShiftLeftLow(l, m);
    ex -= m;

    // a.lo * c, shifted right by 64 bits and left by m.
    var lh = MultiplyHigh(a[1], c);
    var ll = a[1] * c;
    var uh = PowShiftLeftHigh(lh, ll, m - 1);
    var ul = PowShiftLeftLow(ll, m - 1);
    ll = PowShiftRightLow(uh, ul, 63);
    lh = PowShiftRightHigh(uh, 63);
    // a is log(2) and |b| < 2^23 here: the normalized product of the high limbs is not within
    // 2^64 of 2^128, so this sum does not carry out (upstream handles a carry nonetheless).
    l += ll;
    h += lh + (l < ll ? 1UL : 0UL);
    r[0] = h;
    r[1] = l;
    r[2] = (ulong)ex;
    r[3] = sgn;
  }

  /// <summary>A nonzero double as an exact 128-bit number (upstream <c>dint_fromd</c>).</summary>
  private static ulong[] PowDintFromDouble(double b) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(b);
    var e = (long)((bits >> 52) & 0x7ff);
    var m = (bits & (ulong.MaxValue >> 12)) + (e != 0 ? 1UL << 52 : 0UL);
    var t = BitOperations.LeadingZeroCount(m);
    return PowDint(m << t, 0, e - 0x3ff - (t > 11 ? t - 12 : 0), b < 0.0 ? 1UL : 0UL);
  }

  /// <summary>A 128-bit number rounded toward zero to an integer (upstream <c>dint_toi</c>).</summary>
  private static long PowDintToInteger(ulong[] a) {
    var ex = (long)a[2];
    if (ex < 0)
      return 0;

    var r = (long)(a[0] >> (int)(63 - ex));
    return a[3] != 0 ? -r : r;
  }

  /// <summary>Whether the 128-bit approximation of the second phase is too close to a rounding boundary (the negated <c>rd</c> of upstream <c>cr_pow</c>).</summary>
  private static bool PowIsHardToRound2(ulong[] r) {
    var ex = (long)r[2];
    if (ex < -1022) {
      // In the subnormal range the round bit moves left, which leaves room for a bound of 14.
      var shift = (int)(-(1022 + ex));
      var m = r[1] >> (10 + shift) | r[0] << (54 - shift);
      return m + 14 <= 28;
    }

    // The 64 bits after the round bit; 28 is floor(29126 / 2^10).
    var lo = r[1] >> 10 | r[0] << 54;
    return lo + 28 <= 56;
  }

  /// <summary>A 128-bit number rounded to the nearest double (upstream <c>dint_tod</c>).</summary>
  private static double PowDintToDouble(ulong[] a) {
    var ex = (long)a[2];
    if (ex < -1022)
      return PowDintToDoubleSubnormal(a);

    return PowRoundToDouble(a[0], a[1] != 0, ex, a[3]);
  }

  /// <summary>
  /// The double nearest to <c>(-1)^sgn * hi * 2^(ex-63)</c> plus less than a unit of
  /// <paramref name="hi"/>, not zero when <paramref name="sticky"/>, for <c>ex &gt;= -1022</c>: the
  /// significand is rounded by adding 2^-53 for the round bit and 2^-54 for the bits below it, and
  /// scaled. From 2^1025 on the result is +∞ whatever the sign, as upstream has it (the second and
  /// the third phase only see such magnitudes for a positive result).
  /// </summary>
  private static double PowRoundToDouble(ulong hi, bool sticky, long ex, ulong sgn) {
    var r = BitConverter.Int64BitsToDouble((long)((hi >> 11) | (0x3ffUL << 52)));
    var rd = 0.0;
    if (((hi >> 10) & 1) != 0)
      rd += 1.1102230246251565E-16;

    if ((hi & 0x3ff) != 0 || sticky)
      rd += 5.551115123125783E-17;

    // Rounding to nearest is symmetric: the sign is applied at the end.
    r += rd;
    double e;
    if (ex > 1023) {
      if (ex != 1024)
        return double.PositiveInfinity;

      r *= 2.0;
      e = 8.98846567431158E+307;
    } else {
      e = BitConverter.Int64BitsToDouble((long)(((ulong)(ex + 1023) & 0x7ff) << 52));
    }

    return BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(r * e) | (long)(sgn << 63));
  }

  /// <summary>
  /// A 128-bit number from 2^-1075 to 2^-1022 rounded to the nearest double (upstream
  /// <c>dint_tod_subnormal</c>). It is only called once the rounding test has passed: then neither
  /// the round bit nor the bits below it are all zero (the boundary cases fail the test), so a
  /// round bit rounds up, and a number below 2^-1074 rounds to 2^-1074.
  /// </summary>
  private static double PowDintToDoubleSubnormal(ulong[] a) {
    // The significand is shifted right so that its last bit weighs 2^-1074.
    var ex = (int)(-(1011 + (long)a[2]));
    var hi = ex >= 64 ? 1UL : (a[0] >> ex) + ((a[0] >> (ex - 1)) & 1);
    return BitConverter.Int64BitsToDouble((long)(hi | a[3] << 63));
  }

  /// <summary>
  /// <c>log(x)</c> of a positive 128-bit <paramref name="x"/> with a zero low limb, relative error
  /// below 2^-122.88; changes the exponent of <paramref name="x"/> (upstream <c>log_2</c>).
  /// </summary>
  private static void PowLog2(ulong[] r, ulong[] x) {
    var bigE = (long)x[2];

    // The first index i = floor(x * 2^7), x halved above sqrt(2).
    int i;
    if (x[0] > 0xb504f333f9de6484) {
      bigE++;
      i = (int)(x[0] >> 57);
    } else {
      i = (int)(x[0] >> 56);
    }

    x[2] = (ulong)((long)x[2] - bigE);
    var z = new ulong[4];
    PowMultiplyDint11(z, x, PowDintAt(PowTableInverse21, i - 90));

    // The second index j = floor(z * 2^13).
    var j = (int)(z[0] >> (int)(63 - 13 - (long)z[2]));
    PowMultiplyDint11(z, z, PowDintAt(PowTableInverse22, j - 8128));
    PowAddDint(z, PowDint(0x8000000000000000, 0, 0, 1), z);

    // E * log(2), log(2) = 0xb17217f7d1cf79ab c9e3b39803f2f6af * 2^-128.
    PowMultiplyDintByInteger(r, PowDint(0xb17217f7d1cf79ab, 0xc9e3b39803f2f6af, -1, 0), bigE);
    var p = new ulong[4];
    PowLogPolynomial2(p, z);
    PowAddDint(p, PowDintAt(PowTableLogInverse22, j - 8128), p);
    PowAddDint(p, PowDintAt(PowTableLogInverse21, i - 90), p);
    PowAddDint(r, p, r);
  }

  /// <summary><c>log(1+z)</c> for <c>|z| &lt;= 2^-13</c> with a zero low limb (upstream <c>p_2</c>).</summary>
  private static void PowLogPolynomial2(ulong[] r, ulong[] z) {
    PowMultiplyDint11(r, z, PowDintAt(PowTableLogPolynomial2, 0));
    PowAddDint11(r, PowDintAt(PowTableLogPolynomial2, 1), r);
    for (var k = 2; k < 4; k++) {
      PowMultiplyDint11(r, z, r);
      PowAddDint11(r, PowDintAt(PowTableLogPolynomial2, k), r);
    }

    PowMultiplyDint11(r, z, r);
    PowAddDint(r, PowDintAt(PowTableLogPolynomial2, 4), r);
    for (var k = 5; k < 9; k++) {
      PowMultiplyDint21(r, r, z);
      PowAddDint(r, PowDintAt(PowTableLogPolynomial2, k), r);
    }

    PowMultiplyDint21(r, r, z);
  }

  /// <summary>
  /// <c>exp(x)</c> for <c>|x| &lt; 744.45</c>, relative error below 2^-121.70; beyond, a number
  /// that converts to zero or to an overflow (upstream <c>exp_2</c>). <paramref name="r"/> may be
  /// <paramref name="x"/>.
  /// </summary>
  private static void PowExp2(ulong[] r, ulong[] x) {
    if ((long)x[2] >= 10) {
      var sgn = x[3];
      PowCopyDint(r, x);
      r[2] = sgn == 1 ? unchecked((ulong)-1076L) : 1025;
      r[3] = 0;
      return;
    }

    // k = trunc(x * 2^12 / log(2)), with 2^12/log(2) = 0xb8aa3b295c17f0bc * 2^-51.
    var bigK = new ulong[4];
    PowMultiplyDint11(bigK, x, PowDint(0xb8aa3b295c17f0bc, 0, 12, 0));
    var k = PowDintToInteger(bigK);

    // y = x - k * log(2)/2^12, exact by Sterbenz's lemma.
    PowMultiplyDintByInteger(bigK, PowDint(0xb17217f7d1cf79ab, 0xc9e3b39803f2f6af, -1, 0), k);
    bigK[2] -= 12;
    bigK[3] = bigK[3] == 0 ? 1UL : 0UL;
    var y = new ulong[4];
    PowAddDint(y, x, bigK);
    var bigM = k >> 12;
    var i2 = (int)((k >> 6) & 0x3f);
    var i1 = (int)(k & 0x3f);
    PowExpPolynomial2(r, y);
    PowMultiplyDint(r, PowDintAt(PowTableExp2High2, i2), r);
    PowMultiplyDint(r, PowDintAt(PowTableExp2Low2, i1), r);
    r[2] = (ulong)((long)r[2] + bigM);
  }

  /// <summary><c>exp(y)</c> for <c>|y| &lt; 0.00016923</c>, relative error below 2^-122.29 (upstream <c>q_2</c>).</summary>
  private static void PowExpPolynomial2(ulong[] r, ulong[] y) {
    PowMultiplyDint11(r, y, PowDintAt(PowTableExpPolynomial2, 0));
    PowAddDint11(r, PowDintAt(PowTableExpPolynomial2, 1), r);
    PowMultiplyDint11(r, y, r);
    PowAddDint11(r, PowDintAt(PowTableExpPolynomial2, 2), r);
    PowMultiplyDint11(r, y, r);
    PowAddDint(r, PowDintAt(PowTableExpPolynomial2, 3), r);
    for (var k = 4; k < 8; k++) {
      PowMultiplyDint(r, y, r);
      PowAddDint(r, PowDintAt(PowTableExpPolynomial2, k), r);
    }
  }

  /// <summary>A 256-bit number from its fields.</summary>
  private static ulong[] PowQint(ulong hh, ulong hl, ulong lh, ulong ll, long ex, ulong sgn)
    => new ulong[] { hh, hl, lh, ll, (ulong)ex, sgn };

  /// <summary>Entry <paramref name="index"/> of the table of 256-bit numbers <paramref name="table"/>.</summary>
  private static ulong[] PowQintAt(int table, int index) {
    switch (table) {
      case PowTableInverse31:
        return PowQint(_powInverse31Bits[4 * index], _powInverse31Bits[4 * index + 1], _powInverse31Bits[4 * index + 2], _powInverse31Bits[4 * index + 3], _powInverse31Scale[2 * index], (ulong)_powInverse31Scale[2 * index + 1]);
      case PowTableInverse32:
        return PowQint(_powInverse32Bits[4 * index], _powInverse32Bits[4 * index + 1], _powInverse32Bits[4 * index + 2], _powInverse32Bits[4 * index + 3], _powInverse32Scale[2 * index], (ulong)_powInverse32Scale[2 * index + 1]);
      case PowTableLogInverse31:
        return PowQint(_powLogInverse31Bits[4 * index], _powLogInverse31Bits[4 * index + 1], _powLogInverse31Bits[4 * index + 2], _powLogInverse31Bits[4 * index + 3], _powLogInverse31Scale[2 * index], (ulong)_powLogInverse31Scale[2 * index + 1]);
      case PowTableLogInverse32:
        return PowQint(_powLogInverse32Bits[4 * index], _powLogInverse32Bits[4 * index + 1], _powLogInverse32Bits[4 * index + 2], _powLogInverse32Bits[4 * index + 3], _powLogInverse32Scale[2 * index], (ulong)_powLogInverse32Scale[2 * index + 1]);
      case PowTableExp2High3:
        return PowQint(_powExp2High3Bits[4 * index], _powExp2High3Bits[4 * index + 1], _powExp2High3Bits[4 * index + 2], _powExp2High3Bits[4 * index + 3], _powExp2High3Scale[2 * index], (ulong)_powExp2High3Scale[2 * index + 1]);
      case PowTableExp2Low3:
        return PowQint(_powExp2Low3Bits[4 * index], _powExp2Low3Bits[4 * index + 1], _powExp2Low3Bits[4 * index + 2], _powExp2Low3Bits[4 * index + 3], _powExp2Low3Scale[2 * index], (ulong)_powExp2Low3Scale[2 * index + 1]);
      case PowTableLogPolynomial3:
        return PowQint(_powLogPolynomial3Bits[4 * index], _powLogPolynomial3Bits[4 * index + 1], _powLogPolynomial3Bits[4 * index + 2], _powLogPolynomial3Bits[4 * index + 3], _powLogPolynomial3Scale[2 * index], (ulong)_powLogPolynomial3Scale[2 * index + 1]);
      default:
        return PowQint(_powExpPolynomial3Bits[4 * index], _powExpPolynomial3Bits[4 * index + 1], _powExpPolynomial3Bits[4 * index + 2], _powExpPolynomial3Bits[4 * index + 3], _powExpPolynomial3Scale[2 * index], (ulong)_powExpPolynomial3Scale[2 * index + 1]);
    }
  }

  /// <summary>Copies a 256-bit number.</summary>
  private static void PowCopyQint(ulong[] r, ulong[] a) {
    for (var i = 0; i < 6; i++)
      r[i] = a[i];
  }

  /// <summary>A nonzero double as an exact 256-bit number (upstream <c>qint_fromd</c>).</summary>
  private static ulong[] PowQintFromDouble(double b) {
    var d = PowDintFromDouble(b);
    return PowQint(d[0], 0, 0, 0, (long)d[2], d[3]);
  }

  /// <summary>A 256-bit number rounded toward zero to an integer (upstream <c>qint_toi</c>).</summary>
  private static long PowQintToInteger(ulong[] a) {
    var ex = (long)a[4];
    if (ex < 0)
      return 0;

    var r = (long)(a[0] >> (int)(63 - ex));
    return a[5] != 0 ? -r : r;
  }

  /// <summary>Shifts the four limbs of a 256-bit integer, most significant first, left by 0 to 255 bits.</summary>
  private static void PowShiftLeft256(ulong[] v, int k) {
    var q = k >> 6;
    var s = k & 63;
    for (var i = 0; i < 4; i++) {
      var hi = i + q < 4 ? v[i + q] << s : 0UL;
      var lo = s != 0 && i + q + 1 < 4 ? v[i + q + 1] >> (64 - s) : 0UL;
      v[i] = hi | lo;
    }
  }

  /// <summary>Shifts the four limbs of a 256-bit integer, most significant first, right by 0 to 255 bits.</summary>
  private static void PowShiftRight256(ulong[] v, int k) {
    var q = k >> 6;
    var s = k & 63;
    for (var i = 3; i >= 0; i--) {
      var lo = i - q >= 0 ? v[i - q] >> s : 0UL;
      var hi = s != 0 && i - q - 1 >= 0 ? v[i - q - 1] << (64 - s) : 0UL;
      v[i] = hi | lo;
    }
  }

  /// <summary><c>r = x - y</c> of 256-bit integers modulo 2^256; <paramref name="r"/> may be <paramref name="x"/>.</summary>
  private static void PowSubtract256(ulong[] r, ulong[] x, ulong[] y) {
    var borrow = 0UL;
    for (var i = 3; i >= 0; i--) {
      var a = x[i];
      var b = y[i];
      var d = a - b - borrow;

      // The borrow out of a full subtractor, from the top bits.
      borrow = (~a & b | (~a | b) & d) >> 63;
      r[i] = d;
    }
  }

  /// <summary><c>r = x + y</c> of 256-bit integers; returns the carry out. <paramref name="r"/> may be an operand.</summary>
  private static ulong PowAdd256(ulong[] r, ulong[] x, ulong[] y) {
    var carry = 0UL;
    for (var i = 3; i >= 0; i--) {
      var a = x[i];
      var b = y[i];
      var sum = a + b + carry;

      // The carry out of a full adder, from the top bits.
      carry = (a & b | (a | b) & ~sum) >> 63;
      r[i] = sum;
    }

    return carry;
  }

  /// <summary>The leading zero bits of a 256-bit integer.</summary>
  private static int PowLeadingZeros256(ulong[] v) {
    // A limb counts only when every limb above it is zero, that is, has 64 leading zeros.
    var n0 = BitOperations.LeadingZeroCount(v[0]);
    var n1 = BitOperations.LeadingZeroCount(v[1]);
    var n2 = BitOperations.LeadingZeroCount(v[2]);
    var n3 = BitOperations.LeadingZeroCount(v[3]);
    return n0 + (n0 >> 6) * (n1 + (n1 >> 6) * (n2 + (n2 >> 6) * n3));
  }

  /// <summary>Compares the magnitudes of two 256-bit numbers by exponent and the first <paramref name="limbs"/> limbs (upstream <c>cmp_qint</c>, <c>cmp_qint_22</c>).</summary>
  private static int PowCompareQint(ulong[] a, ulong[] b, int limbs) {
    if (a[4] != b[4])
      return (long)a[4] > (long)b[4] ? 1 : -1;

    for (var i = 0; i < limbs; i++)
      if (a[i] != b[i])
        return a[i] > b[i] ? 1 : -1;

    return 0;
  }

  /// <summary>
  /// <c>r = a + b</c> with an error of at most 2 ulps, none when Sterbenz's lemma applies (upstream
  /// <c>add_qint</c>). <paramref name="r"/> may be one of the operands.
  /// </summary>
  private static void PowAddQint(ulong[] r, ulong[] a, ulong[] b) {
    if ((a[0] | a[1] | a[2] | a[3]) == 0) {
      PowCopyQint(r, b);
      return;
    }

    if ((b[0] | b[1] | b[2] | b[3]) == 0) {
      PowCopyQint(r, a);
      return;
    }

    // Opposite numbers give zero. Two equal numbers of the same sign take the general path, which
    // gives upstream's a with the exponent raised by one.
    var order = PowCompareQint(a, b, 4);
    if (order == 0 && a[5] != b[5]) {
      for (var i = 0; i < 6; i++)
        r[i] = 0;

      return;
    }

    if (order < 0) {
      var swap = a;
      a = b;
      b = swap;
    }

    // Now |a| >= |b|: align b.
    var bigA = new ulong[] { a[0], a[1], a[2], a[3] };
    var bigB = new ulong[] { b[0], b[1], b[2], b[3] };
    var k = (long)a[4] - (long)b[4];
    if (k >= 256) {
      for (var i = 0; i < 4; i++)
        bigB[i] = 0;
    } else if (k > 0) {
      PowShiftRight256(bigB, (int)k);
    }

    var sgn = a[5];
    var ex = (long)a[4];
    var c = new ulong[4];
    if ((a[5] ^ b[5]) != 0) {
      PowSubtract256(c, bigA, bigB);
      var shift = PowLeadingZeros256(c);
      if (shift > 0) {
        // Subtract again from the operands shifted first, so that no bit of b is lost.
        PowShiftLeft256(bigA, shift);
        bigB[0] = b[0];
        bigB[1] = b[1];
        bigB[2] = b[2];
        bigB[3] = b[3];
        var sh = shift - k;
        if (sh >= 0)
          PowShiftLeft256(bigB, (int)sh);
        else
          PowShiftRight256(bigB, (int)-sh);

        ex -= shift;
        PowSubtract256(c, bigA, bigB);
        shift = PowLeadingZeros256(c);
      }

      PowShiftLeft256(c, shift);
      ex -= shift;
    } else if (PowAdd256(c, bigA, bigB) != 0) {
      PowShiftRight256(c, 1);
      c[0] |= 1UL << 63;
      ex++;
    }

    r[0] = c[0];
    r[1] = c[1];
    r[2] = c[2];
    r[3] = c[3];
    r[4] = (ulong)ex;
    r[5] = sgn;
  }

  /// <summary>
  /// <c>r = a + b</c> of the upper two limbs only, error below 2 ulps of 128 bits; the low limbs of
  /// <paramref name="r"/> are cleared (upstream <c>add_qint_22</c>). It adds a coefficient
  /// <paramref name="a"/> of <c>p_3</c> or <c>q_3</c> to the product <paramref name="b"/> of the
  /// previous sum and a variable below 2^-12.5, so <c>|b| &lt; 2^-12 |a|</c>; none of these
  /// coefficients lies within 2^-3 above a power of two, so the sum stays in the binade of
  /// <paramref name="a"/> or the next one. The cases of upstream for a zero or smaller
  /// <paramref name="a"/>, equal magnitudes and a difference that loses leading bits cannot arise.
  /// </summary>
  private static void PowAddQint22(ulong[] r, ulong[] a, ulong[] b) {
    if ((b[0] | b[1]) == 0) {
      PowCopyQint(r, a);
      return;
    }

    var ah = a[0];
    var al = a[1];
    var k = a[4] - b[4];
    var bh = k < 128 ? PowShiftRightHigh(b[0], (int)k) : 0;
    var bl = k < 128 ? PowShiftRightLow(b[0], b[1], (int)k) : 0;
    var ex = (long)a[4];
    ulong ch;
    ulong cl;
    if ((a[5] ^ b[5]) != 0) {
      cl = al - bl;
      ch = ah - bh - (al < bl ? 1UL : 0UL);
    } else {
      cl = al + bl;
      ch = ah + bh + (cl < al ? 1UL : 0UL);
      if (ch < ah) {
        cl = ch << 63 | cl >> 1;
        ch = 1UL << 63 | ch >> 1;
        ex++;
      }
    }

    r[0] = ch;
    r[1] = cl;
    r[2] = 0;
    r[3] = 0;
    r[4] = (ulong)ex;
    r[5] = a[5];
  }

  /// <summary>
  /// Adds <c>x * y</c>, a product of limbs of degree <paramref name="degree"/>, to a 256-bit sum
  /// whose limbs weigh degrees 7 (first) down to 4; of a product of degree 3 only the high half counts.
  /// </summary>
  private static void PowAccumulateProduct(ulong[] acc, ulong x, ulong y, int degree) {
    if (degree >= 4)
      PowAccumulate(acc, 7 - degree, x * y);

    PowAccumulate(acc, 6 - degree, MultiplyHigh(x, y));
  }

  /// <summary>Adds a limb at position <paramref name="index"/> of a sum of four limbs, carrying upward.</summary>
  private static void PowAccumulate(ulong[] acc, int index, ulong value) {
    var carry = value;
    for (var i = index; i >= 0 && carry != 0; i--) {
      var sum = acc[i] + carry;
      carry = sum < carry ? 1UL : 0UL;
      acc[i] = sum;
    }
  }

  /// <summary>
  /// <c>r = a * b</c> from the limbs of <paramref name="a"/> from <paramref name="aLowest"/> and of
  /// <paramref name="b"/> from <paramref name="bLowest"/> on (limb 3 the most significant), keeping
  /// the products of degree 4 to 6 and the high halves of those of degree 3. That is upstream
  /// <c>mul_qint</c> (0, 0, error below 14 ulps), <c>mul_qint_33</c> (1, 1), <c>mul_qint_41</c>
  /// (0, 3), <c>mul_qint_31</c> (1, 3, exact), <c>mul_qint_22</c> (2, 2, exact), <c>mul_qint_21</c>
  /// (2, 3, exact) and <c>mul_qint_11</c> (3, 3, exact). <paramref name="r"/> may be an operand.
  /// </summary>
  private static void PowMultiplyQint(ulong[] r, ulong[] a, ulong[] b, int aLowest, int bLowest) {
    var acc = new ulong[4];
    for (var i = 3; i >= aLowest; i--)
      for (var j = 3; j >= bLowest; j--)
        if (i + j >= 3)
          PowAccumulateProduct(acc, a[3 - i], b[3 - j], i + j);

    var shift = acc[0] >> 63 == 0 ? 1 : 0;
    PowShiftLeft256(acc, shift);
    r[4] = (ulong)((long)a[4] + (long)b[4] + 1 - shift);
    r[5] = a[5] ^ b[5];
    r[0] = acc[0];
    r[1] = acc[1];
    r[2] = acc[2];
    r[3] = acc[3];
  }

  /// <summary><c>r = b * a</c> for an integer <paramref name="b"/>, error below 2 ulps (upstream <c>mul_qint_2</c>).</summary>
  private static void PowMultiplyQintByInteger(ulong[] r, long b, ulong[] a) {
    if (b == 0) {
      for (var i = 0; i < 6; i++)
        r[i] = 0;

      return;
    }

    var c = b < 0 ? 0UL - (ulong)b : (ulong)b;
    var sgn = (b < 0 ? 1UL : 0UL) ^ a[5];
    if (c == 1) {
      PowCopyQint(r, a);
      r[5] = sgn;
      return;
    }

    // Scale c so that 2^63 <= c < 2^64.
    var k = BitOperations.LeadingZeroCount(c);
    c <<= k;
    var ex = (long)a[4] + 64 - k;

    // The products of the limbs of a with c; of the lowest only the high half counts.
    var acc = new ulong[4];
    for (var i = 0; i < 4; i++) {
      if (i < 3)
        PowAccumulate(acc, i + 1, a[i] * c);

      PowAccumulate(acc, i, MultiplyHigh(a[i], c));
    }

    if (BitOperations.LeadingZeroCount(acc[0]) != 0) {
      PowShiftLeft256(acc, 1);
      ex--;
    }

    r[0] = acc[0];
    r[1] = acc[1];
    r[2] = acc[2];
    r[3] = acc[3];
    r[4] = (ulong)ex;
    r[5] = sgn;
  }

  /// <summary>
  /// <c>log(x)</c> of a positive 256-bit <paramref name="x"/> with one nonzero limb, relative error
  /// below 2^-250.74; changes the exponent of <paramref name="x"/> (upstream <c>log_3</c>).
  /// </summary>
  private static void PowLog3(ulong[] r, ulong[] x) {
    var bigE = (long)x[4];
    int i;
    if (x[0] > 0xb504f333f9de6484) {
      bigE++;
      i = (int)(x[0] >> 57);
    } else {
      i = (int)(x[0] >> 56);
    }

    x[4] = (ulong)((long)x[4] - bigE);
    var z = new ulong[6];
    PowMultiplyQint(z, x, PowQintAt(PowTableInverse31, i - 90), 0, 0);
    var j = (int)(z[0] >> (int)(63 - 13 - (long)z[4]));
    PowMultiplyQint(z, z, PowQintAt(PowTableInverse32, j - 8128), 0, 0);
    PowAddQint(z, PowQint(0x8000000000000000, 0, 0, 0, 0, 1), z);

    // E * log(2) to 256 bits.
    PowMultiplyQintByInteger(r, bigE, PowQint(0xb17217f7d1cf79ab, 0xc9e3b39803f2f6af, 0x40f343267298b62d, 0x8a0d175b8baafa2b, -1, 0));
    var p = new ulong[6];
    PowLogPolynomial3(p, z);
    PowAddQint(p, PowQintAt(PowTableLogInverse32, j - 8128), p);
    PowAddQint(p, PowQintAt(PowTableLogInverse31, i - 90), p);
    PowAddQint(r, p, r);
  }

  /// <summary><c>log(1+z)</c> for <c>|z| &lt;= 2^-13</c> with one nonzero limb (upstream <c>p_3</c>).</summary>
  private static void PowLogPolynomial3(ulong[] r, ulong[] z) {
    PowMultiplyQint(r, PowQintAt(PowTableLogPolynomial3, 0), z, 3, 3);
    PowAddQint22(r, PowQintAt(PowTableLogPolynomial3, 1), r);
    for (var k = 2; k < 4; k++) {
      PowMultiplyQint(r, r, z, 3, 3);
      PowAddQint22(r, PowQintAt(PowTableLogPolynomial3, k), r);
    }

    for (var k = 4; k < 8; k++) {
      PowMultiplyQint(r, r, z, 2, 3);
      PowAddQint22(r, PowQintAt(PowTableLogPolynomial3, k), r);
    }

    for (var k = 8; k < 14; k++) {
      PowMultiplyQint(r, r, z, 1, 3);
      PowAddQint(r, PowQintAt(PowTableLogPolynomial3, k), r);
    }

    for (var k = 14; k < 18; k++) {
      PowMultiplyQint(r, r, z, 0, 3);
      PowAddQint(r, PowQintAt(PowTableLogPolynomial3, k), r);
    }

    PowMultiplyQint(r, r, z, 0, 3);
  }

  /// <summary><c>exp(x)</c> for <c>|x| &lt; 744.45</c>, relative error below 2^-241.10 (upstream <c>exp_3</c>).</summary>
  private static void PowExp3(ulong[] r, ulong[] x) {
    var bigK = new ulong[6];
    PowMultiplyQint(bigK, x, PowQint(0xb8aa3b295c17f0bc, 0, 0, 0, 12, 0), 3, 3);
    var k = PowQintToInteger(bigK);
    PowMultiplyQintByInteger(bigK, k, PowQint(0xb17217f7d1cf79ab, 0xc9e3b39803f2f6af, 0x40f343267298b62d, 0x8a0d175b8baafa2b, -1, 0));
    bigK[4] -= 12;
    bigK[5] = bigK[5] == 0 ? 1UL : 0UL;
    var y = new ulong[6];
    PowAddQint(y, x, bigK);
    var bigM = k >> 12;
    var i2 = (int)((k >> 6) & 0x3f);
    var i1 = (int)(k & 0x3f);
    PowExpPolynomial3(r, y);
    PowMultiplyQint(r, PowQintAt(PowTableExp2High3, i2), r, 0, 0);
    PowMultiplyQint(r, PowQintAt(PowTableExp2Low3, i1), r, 0, 0);
    r[4] = (ulong)((long)r[4] + bigM);
  }

  /// <summary><c>exp(y)</c> for <c>|y| &lt; 0.00016923</c>, absolute error below 2^-241.11 (upstream <c>q_3</c>).</summary>
  private static void PowExpPolynomial3(ulong[] r, ulong[] y) {
    PowMultiplyQint(r, y, PowQintAt(PowTableExpPolynomial3, 0), 3, 3);
    PowAddQint22(r, PowQintAt(PowTableExpPolynomial3, 1), r);
    for (var k = 2; k < 7; k++) {
      PowMultiplyQint(r, y, r, 2, 2);
      PowAddQint22(r, PowQintAt(PowTableExpPolynomial3, k), r);
    }

    for (var k = 7; k < 12; k++) {
      PowMultiplyQint(r, y, r, 1, 1);
      PowAddQint(r, PowQintAt(PowTableExpPolynomial3, k), r);
    }

    for (var k = 12; k < 15; k++) {
      PowMultiplyQint(r, y, r, 0, 0);
      PowAddQint(r, PowQintAt(PowTableExpPolynomial3, k), r);
    }
  }

  /// <summary>Rounds a 256-bit number below 2^-1022 to the precision of a subnormal double (upstream <c>subnormalize_qint</c>).</summary>
  private static void PowSubnormalizeQint(ulong[] a) {
    var ex0 = (long)a[4];
    if (ex0 > -1023)
      return;

    var ex = (int)(-(1011 + ex0));
    var hi = a[0] >> ex;
    var md = (a[0] >> (ex - 1)) & 1;
    var lo = (a[0] & (ulong.MaxValue >> ex)) != 0 || a[1] != 0 || a[2] != 0 || a[3] != 0;
    hi += lo ? md : hi & md;
    a[0] = hi << ex;
    a[1] = 0;
    a[2] = 0;
    a[3] = 0;
    if (a[0] == 0) {
      a[4] = (ulong)(ex0 + 1);
      a[0] = 1UL << 63;
    }
  }

  /// <summary>A 256-bit number rounded to the nearest double (upstream <c>qint_tod</c>).</summary>
  private static double PowQintToDouble(ulong[] a) {
    PowSubnormalizeQint(a);
    var ex = (long)a[4];
    if (ex > -1023)
      return PowRoundToDouble(a[0], (a[1] | a[2] | a[3]) != 0, ex, a[5]);

    // Below 2^-1022 the significand has been rounded to the bits that remain, so the product is exact.
    var r = BitConverter.Int64BitsToDouble((long)((a[0] >> 11) | (0x3ffUL << 52)));
    double e;
    if (ex < -1074) {
      // Upstream drops the sign of a number below 2^-1075.
      if (ex != -1075)
        return 0.0;

      r *= 0.5;
      e = 5E-324;
    } else {
      e = BitConverter.Int64BitsToDouble(1L << (int)(ex + 1074));
    }

    return BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(r * e) | (long)(a[5] << 63));
  }

}
