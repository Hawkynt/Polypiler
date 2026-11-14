// SPDX-License-Identifier: MIT
using System;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>
/// The accurate path of <see cref="FloatOperations.Tan"/>, in a 128-bit floating-point format: a
/// number is a <c>ulong[4]</c> of <c>hi</c>, <c>lo</c>, <c>ex</c> (a <c>long</c> in its bits) and
/// <c>sgn</c>, worth <c>(-1)^sgn * (hi/2^64 + lo/2^128) * 2^ex</c>, normalized so that the top bit of
/// <c>hi</c> is set unless the number is zero.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// The 128-bit tangent for a finite <c>|x| &gt; 0x1.d12ed0af1a27ep-27</c>: <c>x/(2pi) mod 1</c>
  /// in 128 bits, split into <c>i/2^11 + X</c>, the sine and cosine of <c>X</c> by polynomials and
  /// of <c>i/2^11</c> from tables, and their quotient rounded to a double.
  /// </summary>
  /// <param name="x">The angle.</param>
  /// <returns><c>tan(x)</c>, correctly rounded.</returns>
  private static double TanAccurate(double x) {
    var absx = x > 0 ? x : -x;
    var reduced = new ulong[4];
    TanFromDouble(reduced, absx);

    // |X - x/(2pi) mod 1| < 2^-126.67 * X, 0 <= X < 1; then X = i/2^11 + X' exactly.
    TanReduce(reduced);
    var isTan = true;
    var negative = x < 0;
    var i = TanSplitOctant(reduced);

    // tan(x - pi) = tan(x); tan(x) = -1/tan(x - pi/2); tan(x) = 1/tan(pi/2 - x).
    i &= 0x3ff;
    if ((i & 0x200) != 0) {
      isTan = false;
      negative = !negative;
      i &= 0x1ff;
    }

    if ((i & 0x100) != 0) {
      isTan = !isTan;
      // X <- 2^-11 - X
      reduced[3] = 1;
      var magic = new ulong[4];
      TanSet(magic, 0x8000000000000000UL, 0, -10, 0);
      TanAdd(reduced, magic, reduced);
      i = 0x1ff - i;
    }

    // Now 0 <= i < 256 and 0 <= X < 2^-11.
    var square = new ulong[4];
    var cosine = new ulong[4];
    var sine = new ulong[4];
    TanMultiply(square, reduced, reduced);
    TanCosinePolynomial(cosine, square);
    TanSinePolynomial(sine, reduced, square);

    // sin2pi(R) = sin2pi(i/2^11) * cos2pi(X) + cos2pi(i/2^11) * sin2pi(X)
    var sinAt = new ulong[4];
    var cosAt = new ulong[4];
    TanSet(sinAt, _tanSine[2 * i], _tanSine[2 * i + 1], _tanSineExponent[i], 0);
    TanSet(cosAt, _tanCosine[2 * i], _tanCosine[2 * i + 1], _tanCosineExponent[i], 0);
    var first = new ulong[4];
    var second = new ulong[4];
    var sinR = new ulong[4];
    TanMultiply(first, sinAt, cosine);
    TanMultiply(second, cosAt, sine);
    TanAdd(sinR, first, second);

    // cos2pi(R) = cos2pi(i/2^11) * cos2pi(X) - sin2pi(i/2^11) * sin2pi(X)
    var cosR = new ulong[4];
    TanMultiply(cosine, cosAt, cosine);
    TanMultiply(sine, sinAt, sine);
    sine[3] = 1 - sine[3];
    TanAdd(cosR, cosine, sine);

    var quotient = new ulong[4];
    if (isTan)
      TanDivide(quotient, sinR, cosR);
    else
      TanDivide(quotient, cosR, sinR);

    // The relative error is below 86 ulps of 128 bits; where that leaves the rounding undecided, the
    // two arguments with an even harder case are looked up.
    const ulong Err = 86;
    var lo0 = unchecked(quotient[1] - Err);
    var hi0 = quotient[0] - TanBelow(quotient[1], lo0);
    var lo1 = unchecked(quotient[1] + Err);
    var hi1 = quotient[0] + TanBelow(lo1, quotient[1]);
    if (hi0 >> 10 != hi1 >> 10)
      for (var j = 0; j < 2; j++)
        if (absx == _tanException[3 * j])
          return x > 0
            ? _tanException[3 * j + 1] + _tanException[3 * j + 2]
            : -_tanException[3 * j + 1] - _tanException[3 * j + 2];

    if (negative)
      quotient[3] = 1 - quotient[3];

    return TanToDouble(quotient);
  }

  /// <summary>
  /// The arguments whose tangent is closer to a rounding boundary than the accurate path resolves,
  /// each followed by its tangent as the sum of two doubles.
  /// </summary>
  private static readonly double[] _tanException = [
    // 78 identical bits after the round bit
    4.470348358154178e-07, 4.4703483581544753e-07, 2.6469779601696883e-23, // 0x1.dffffffffff1fp-22, 0x1.e000000000151p-22, 0x1.fffffffffffffp-76
    // 72 identical bits after the round bit
    8.940696716307641e-07, 8.940696716310023e-07, -7.828575563803332e-45, // 0x1.dfffffffffc7cp-21, 0x1.e000000000546p-21, -0x1.658bcedb6e1d4p-147
  ];

  /// <summary>Sets a 128-bit number from its parts.</summary>
  /// <param name="r">The number.</param>
  /// <param name="hi">The upper word of the significand.</param>
  /// <param name="lo">The lower word of the significand.</param>
  /// <param name="ex">The exponent.</param>
  /// <param name="sgn">1 for a negative number, else 0.</param>
  private static void TanSet(ulong[] r, ulong hi, ulong lo, long ex, ulong sgn) {
    r[0] = hi;
    r[1] = lo;
    r[2] = (ulong)ex;
    r[3] = sgn;
  }

  /// <summary>A positive normal double as a 128-bit number.</summary>
  /// <param name="a">Receives the number.</param>
  /// <param name="b">The double, at least <c>2^-1022</c>.</param>
  private static void TanFromDouble(ulong[] a, double b) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(b);
    var e = (long)((bits >> 52) & 0x7ff);
    var m = (bits & (ulong.MaxValue >> 12)) + (1UL << 52);
    TanSet(a, m << 11, 0, e - 0x3fe, 0);
  }

  /// <summary>Shifts a 128-bit number left until the top bit of <c>hi</c> is set, unless it is zero.</summary>
  /// <param name="x">The number.</param>
  private static void TanNormalize(ulong[] x) {
    if (x[0] != 0) {
      var count = BitOperations.LeadingZeroCount(x[0]);
      if (count != 0) {
        x[0] = (x[0] << count) | (x[1] >> (64 - count));
        x[1] <<= count;
      }

      x[2] = (ulong)((long)x[2] - count);
    } else if (x[1] != 0) {
      var count = BitOperations.LeadingZeroCount(x[1]);
      x[0] = x[1] << count;
      x[1] = 0;
      x[2] = (ulong)((long)x[2] - 64 - count);
    }
  }

  /// <summary>The upper word of a 128-bit integer shifted left by <c>0 &lt;= n &lt; 128</c>.</summary>
  private static ulong TanShiftLeftHigh(ulong hi, ulong lo, int n) {
    if (n == 0)
      return hi;

    return n < 64 ? (hi << n) | (lo >> (64 - n)) : lo << (n - 64);
  }

  /// <summary>The lower word of a 128-bit integer shifted left by <c>0 &lt;= n &lt; 128</c>.</summary>
  private static ulong TanShiftLeftLow(ulong lo, int n) => n < 64 ? lo << n : 0;

  /// <summary>
  /// <c>r = a + b</c>, with an error below 2 ulps (exact when Sterbenz's lemma applies). <c>r</c> may
  /// be <c>a</c> or <c>b</c>.
  /// </summary>
  /// <param name="r">Receives the sum.</param>
  /// <param name="a">The first addend.</param>
  /// <param name="b">The second addend.</param>
  private static void TanAdd(ulong[] r, ulong[] a, ulong[] b) {
    if ((a[0] | a[1]) == 0) {
      TanSet(r, b[0], b[1], (long)b[2], b[3]);
      return;
    }

    // Compare the magnitudes; the larger is called a from here on.
    var order = 1;
    if (b[0] != 0) {
      // The exponents as signed numbers, then the significand words.
      order = TanCompare(a[2] ^ (1UL << 63), b[2] ^ (1UL << 63));
      if (order == 0)
        order = TanCompare(a[0], b[0]);
      if (order == 0)
        order = TanCompare(a[1], b[1]);
    }

    if (order == 0) {
      if ((a[3] ^ b[3]) != 0)
        TanSet(r, 0, 0, -1076, 0);
      else
        TanSet(r, a[0], a[1], (long)a[2] + 1, a[3]);

      return;
    }

    var big = order > 0 ? a : b;
    var small = order > 0 ? b : a;
    var ah = big[0];
    var al = big[1];
    var bh = small[0];
    var bl = small[1];
    var originalBh = bh;
    var originalBl = bl;
    var k = (ulong)((long)big[2] - (long)small[2]);
    if (k > 0) {
      if (k < 128) {
        var shift = (int)k;
        bl = shift < 64 ? (bl >> shift) | (bh << (64 - shift)) : bh >> (shift - 64);
        bh = shift < 64 ? bh >> shift : 0;
      } else {
        bh = 0;
        bl = 0;
      }
    }

    var sgn = big[3];
    var ex = (long)big[2];
    ulong ch;
    ulong cl;
    if ((big[3] ^ small[3]) != 0) {
      // C = A - B; |a| > |b| excludes C = 0.
      cl = unchecked(al - bl);
      ch = unchecked(ah - bh - TanBelow(al, bl));
      var count = ch != 0 ? BitOperations.LeadingZeroCount(ch) : 64 + BitOperations.LeadingZeroCount(cl);
      if (count > 0) {
        // Recompute from the shifted operands, so that the bits of B below its ulp are not lost.
        var bShift = k == 1 ? count - 1 : count;
        var subtrahendHigh = k == 1 ? TanShiftLeftHigh(originalBh, originalBl, bShift) : TanShiftLeftHigh(bh, bl, bShift);
        var subtrahendLow = k == 1 ? TanShiftLeftLow(originalBl, bShift) : TanShiftLeftLow(bl, bShift);
        var minuendHigh = TanShiftLeftHigh(ah, al, count);
        var minuendLow = TanShiftLeftLow(al, count);
        cl = unchecked(minuendLow - subtrahendLow);
        ch = unchecked(minuendHigh - subtrahendHigh - TanBelow(minuendLow, subtrahendLow));
        ex -= count;
        count = BitOperations.LeadingZeroCount(ch);
      }

      var shiftedHigh = TanShiftLeftHigh(ch, cl, count);
      cl = TanShiftLeftLow(cl, count);
      ch = shiftedHigh;
      ex -= count;
    } else {
      cl = unchecked(al + bl);
      ch = unchecked(ah + bh + TanBelow(cl, al));
      if (ch < ah || ch == ah && cl < al) {
        // The carry out of 128 bits: C = 2^127 | C >> 1.
        cl = (cl >> 1) | (ch << 63);
        ch = (1UL << 63) | (ch >> 1);
        ex++;
      }
    }

    TanSet(r, ch, cl, ex, sgn);
  }

  /// <summary>1, 0 or -1 as <c>x</c> is above, equal to or below <c>y</c>.</summary>
  private static int TanCompare(ulong x, ulong y) => x > y ? 1 : x < y ? -1 : 0;

  /// <summary><c>r = a * b</c>, with an error below 6 ulps. <c>r</c> may be <c>a</c> or <c>b</c>.</summary>
  /// <param name="r">Receives the product.</param>
  /// <param name="a">The first factor.</param>
  /// <param name="b">The second factor.</param>
  private static void TanMultiply(ulong[] r, ulong[] a, ulong[] b) {
    var ah = a[0];
    var al = a[1];
    var bh = b[0];
    var bl = b[1];

    // hi*hi in 128 bits plus the upper halves of the two middle terms; lo*lo is neglected.
    var middle = unchecked(MultiplyHigh(ah, bl) + MultiplyHigh(al, bh));
    var middleCarry = TanBelow(middle, MultiplyHigh(ah, bl));
    var rl = unchecked(ah * bh + middle);
    var rh = unchecked(MultiplyHigh(ah, bh) + middleCarry + TanBelow(rl, middle));
    TanStoreProduct(r, rh, rl, (long)a[2] + (long)b[2], a[3] ^ b[3]);
  }

  /// <summary><c>r = a * b</c> where the lower word of <c>b</c> counts as zero, with an error below 2 ulps.</summary>
  /// <param name="r">Receives the product.</param>
  /// <param name="a">The first factor.</param>
  /// <param name="b">The second factor, of which only <c>hi</c> is used.</param>
  private static void TanMultiplyShort(ulong[] r, ulong[] a, ulong[] b) {
    var bh = b[0];
    var middle = MultiplyHigh(a[1], bh);
    var rl = unchecked(a[0] * bh + middle);
    var rh = MultiplyHigh(a[0], bh) + TanBelow(rl, middle);
    TanStoreProduct(r, rh, rl, (long)a[2] + (long)b[2], a[3] ^ b[3]);
  }

  /// <summary>Stores a product whose top bit is in one of the two highest places, shifting it up by one when needed.</summary>
  private static void TanStoreProduct(ulong[] r, ulong rh, ulong rl, long exponents, ulong sgn) {
    var top = rh >> 63;
    if (top == 0) {
      rh = (rh << 1) | (rl >> 63);
      rl <<= 1;
    }

    TanSet(r, rh, rl, exponents + (long)top - 1, sgn);
  }

  /// <summary>
  /// <c>r = 1/a</c> for a non-zero <c>a</c>, relative error below <c>2^-124.999</c>: three Newton
  /// steps on 64 bits from the table seed, then one in 128 bits.
  /// </summary>
  /// <param name="r">Receives the reciprocal; not <c>a</c>.</param>
  /// <param name="a">The number.</param>
  private static void TanInverse(ulong[] r, ulong[] a) {
    var h = a[0];
    var t = _tanReciprocalSeed[(int)((h >> 55) & 0xff)];

    // e = 2^127 - h*t, then t += t*e/2^127 in three steps of growing precision.
    var eh = unchecked((1UL << 63) - MultiplyHigh(h, t) - TanBelow(0, h * t));
    var el = unchecked(0 - h * t);
    t += MultiplyHigh(t, (eh << 9) | (el >> 55)) >> 8;
    eh = unchecked((1UL << 63) - MultiplyHigh(h, t) - TanBelow(0, h * t));
    el = unchecked(0 - h * t);
    t += MultiplyHigh(t, (eh << 17) | (el >> 47)) >> 16;
    eh = unchecked((1UL << 63) - MultiplyHigh(h, t) - TanBelow(0, h * t));
    el = unchecked(0 - h * t);
    t += MultiplyHigh(t, (eh << 33) | (el >> 31)) >> 32;

    // r -> r + r*(1 - a*r)
    var one = new ulong[4];
    var q = new ulong[4];
    TanSet(one, 0x8000000000000000UL, 0, 1, 0);
    TanSet(r, t, 0, 1 - (long)a[2], 1);
    TanMultiplyShort(q, a, r);
    r[3] = 0;
    TanAdd(q, one, q);
    TanMultiply(q, r, q);
    TanAdd(r, r, q);
  }

  /// <summary><c>r = b/a</c> for a non-zero <c>a</c>, relative error below <c>2^-123.67</c>.</summary>
  /// <param name="r">Receives the quotient; neither <c>a</c> nor <c>b</c>.</param>
  /// <param name="b">The dividend.</param>
  /// <param name="a">The divisor.</param>
  private static void TanDivide(ulong[] r, ulong[] b, ulong[] a) {
    TanInverse(r, a);
    TanMultiply(r, r, b);
  }

  /// <summary>
  /// Rounds a 128-bit number in the normal range of doubles to nearest: the significand to 53 bits
  /// with the next bit and a sticky bit added as a double, then scaled.
  /// </summary>
  /// <param name="a">The number, with <c>-1022 &lt; ex &lt;= 1024</c>.</param>
  /// <returns>The double.</returns>
  private static double TanToDouble(ulong[] a) {
    var hi = a[0];
    var sgn = a[3];
    var r = BitConverter.Int64BitsToDouble((long)((hi >> 11) | (0x3ffUL << 52) | (sgn << 63)));
    var rd = 0.0;
    if (((hi >> 10) & 1) != 0)
      rd += 1.1102230246251565e-16; // 0x1p-53

    if ((hi & 0x3ff) != 0 || a[1] != 0)
      rd += 5.551115123125783e-17; // 0x1p-54

    if (sgn != 0)
      rd = -rd;

    r += rd;
    return r * BitConverter.Int64BitsToDouble((long)((((ulong)a[2] + 1022) & 0x7ff) << 52));
  }

  /// <summary>
  /// Replaces a normalized <c>X</c> by <c>X/(2pi) mod 1</c>, normalized, with relative error below
  /// <c>2^-126.67</c>: from the words of <c>1/(2pi)</c> that the exponent of <c>X</c> selects.
  /// </summary>
  /// <param name="x">The number.</param>
  private static void TanReduce(ulong[] x) {
    var e = (int)(long)x[2];
    var hi = x[0];
    ulong u;
    ulong tiny;
    if (e <= 1) {
      // |X| < 2: multiply by T[0]/2^64 + T[1]/2^128.
      tiny = unchecked(hi * _tanInverseTwoPi[1]);
      var lo = MultiplyHigh(hi, _tanInverseTwoPi[1]);
      u = unchecked(hi * _tanInverseTwoPi[0]);
      lo = unchecked(lo + u);
      x[0] = MultiplyHigh(hi, _tanInverseTwoPi[0]) + TanBelow(lo, u);
      x[1] = lo;
      TanNormalize(x);
      // Shift the upper bits of tiny into lo.
      var shift = e - (int)(long)x[2];
      if (shift != 0)
        x[1] |= tiny >> (64 - shift);

      return;
    }

    // 2 <= e <= 1024: only T[i] .. T[i+4] contribute to the fraction.
    var i = e < 127 ? 0 : (e - 127 + 64 - 1) / 64;
    var c0 = unchecked(hi * _tanInverseTwoPi[i + 3]);
    var c1 = MultiplyHigh(hi, _tanInverseTwoPi[i + 3]);
    u = unchecked(hi * _tanInverseTwoPi[i + 2]);
    c1 = unchecked(c1 + u);
    var c2 = MultiplyHigh(hi, _tanInverseTwoPi[i + 2]) + TanBelow(c1, u);
    u = unchecked(hi * _tanInverseTwoPi[i + 1]);
    c2 = unchecked(c2 + u);
    var c3 = MultiplyHigh(hi, _tanInverseTwoPi[i + 1]) + TanBelow(c2, u);
    u = unchecked(hi * _tanInverseTwoPi[i]);
    c3 = unchecked(c3 + u);
    var c4 = MultiplyHigh(hi, _tanInverseTwoPi[i]) + TanBelow(c3, u);

    // hi*T[i]/2^128 is multiplied by 2^f; shift c4 .. c0 left by f bits.
    var f = e - 64 * i;
    if (f < 64) {
      x[0] = (c4 << f) | (c3 >> (64 - f));
      x[1] = (c3 << f) | (c2 >> (64 - f));
      tiny = (c2 << f) | (c1 >> (64 - f));
    } else if (f == 64) {
      x[0] = c3;
      x[1] = c2;
      tiny = c1;
    } else {
      // 65 <= f <= 127: one more word of 1/(2pi), added with its carry into c1 .. c3.
      var g = f - 64;
      u = MultiplyHigh(hi, _tanInverseTwoPi[i + 4]);
      c0 = unchecked(c0 + u);
      var carry = TanBelow(c0, u);
      c1 = unchecked(c1 + carry);
      carry = TanBelow(c1, carry);
      c2 = unchecked(c2 + carry);
      carry = TanBelow(c2, carry);
      c3 = unchecked(c3 + carry);
      x[0] = (c3 << g) | (c2 >> (64 - g));
      x[1] = (c2 << g) | (c1 >> (64 - g));
      tiny = (c1 << g) | (c0 >> (64 - g));
    }

    x[2] = 0;
    TanNormalize(x);
    // Put the upper -ex bits of tiny into lo; ex >= -61 for every double.
    var ex = (long)x[2];
    if (ex < 0)
      x[1] |= tiny >> (int)(64 + ex);
  }

  /// <summary>
  /// Splits <c>0 &lt;= X &lt; 1</c> exactly into <c>i/2^11 + X'</c> with <c>0 &lt;= X' &lt; 2^-11</c>.
  /// </summary>
  /// <param name="x">The number, replaced by <c>X'</c>, normalized.</param>
  /// <returns><c>i</c>.</returns>
  private static int TanSplitOctant(ulong[] x) {
    var ex = (long)x[2];
    if (ex <= -11)
      return 0;

    var shift = (int)(64 - 11 - ex);
    var i = (int)(x[0] >> shift);
    x[0] &= (1UL << shift) - 1;
    TanNormalize(x);
    return i;
  }

  /// <summary>
  /// <c>sin2pi(X)</c> for <c>0 &lt;= X &lt; 2^-11</c> by an odd polynomial of degree 11, relative
  /// error below <c>2^-124.648</c>.
  /// </summary>
  /// <param name="y">Receives the sine.</param>
  /// <param name="x">X.</param>
  /// <param name="square">An approximation of <c>X^2</c>.</param>
  private static void TanSinePolynomial(ulong[] y, ulong[] x, ulong[] square) {
    var coefficient = new ulong[4];
    TanSinCoefficient(coefficient, 5);
    TanMultiplyShort(y, square, coefficient);
    for (var k = 4; k >= 0; k--) {
      TanSinCoefficient(coefficient, k);
      TanAdd(y, y, coefficient);
      TanMultiply(y, y, k > 0 ? square : x);
    }
  }

  /// <summary>
  /// <c>cos2pi(X)</c> for <c>0 &lt;= X &lt; 2^-11</c> by an even polynomial of degree 10, error
  /// below <c>2^-125.999</c>.
  /// </summary>
  /// <param name="y">Receives the cosine.</param>
  /// <param name="square">An approximation of <c>X^2</c>.</param>
  private static void TanCosinePolynomial(ulong[] y, ulong[] square) {
    var coefficient = new ulong[4];
    TanCosCoefficient(coefficient, 5);
    TanMultiplyShort(y, square, coefficient);
    for (var k = 4; k >= 0; k--) {
      TanCosCoefficient(coefficient, k);
      TanAdd(y, y, coefficient);
      if (k > 0)
        TanMultiply(y, y, square);
    }
  }

  /// <summary>The coefficient of degree <c>2k+1</c> of the sine polynomial.</summary>
  private static void TanSinCoefficient(ulong[] r, int k)
    => TanSet(r, _tanSinePolynomial[2 * k], _tanSinePolynomial[2 * k + 1], _tanSinePolynomialExponent[k], (ulong)(k & 1));

  /// <summary>The coefficient of degree <c>2k</c> of the cosine polynomial.</summary>
  private static void TanCosCoefficient(ulong[] r, int k)
    => TanSet(r, _tanCosinePolynomial[2 * k], _tanCosinePolynomial[2 * k + 1], _tanCosinePolynomialExponent[k], (ulong)(k & 1));

  /// <summary>The coefficients of degrees 1, 3, .., 11 of <c>sin2pi</c> as (hi, lo); the signs alternate, starting positive.</summary>
  private static readonly ulong[] _tanSinePolynomial = [
    0xc90fdaa22168c234, 0xc4c6628b80dc1cd1,
    0xa55de7312df295f5, 0x5dc72f712aa57db4,
    0xa335e33bad570e92, 0x3f33be0021aa54d2,
    0x9969667315ec2d9d, 0xe59d6ab8509a2025,
    0xa83c1a43bf1c6485, 0x7d5f8f76fa7d74ed,
    0xf16ab2898eae62f9, 0xa7f0339113b8b3c5,
  ];

  /// <summary>The exponents of <see cref="_tanSinePolynomial"/>.</summary>
  private static readonly int[] _tanSinePolynomialExponent = [3, 6, 7, 7, 6, 4];

  /// <summary>The coefficients of degrees 0, 2, .., 10 of <c>cos2pi</c> as (hi, lo); the signs alternate, starting positive.</summary>
  private static readonly ulong[] _tanCosinePolynomial = [
    0x8000000000000000, 0x0,
    0x9de9e64df22ef2d2, 0x56e26cd9808c1949,
    0x81e0f840dad61d9a, 0x9980f00630cb655e,
    0xaae9e3f1e5ffcfe2, 0xa508509534006249,
    0xf0fa83448dd1e094, 0xe0603ce7044eeba,
    0xd368f6f4207cfe49, 0xec63157807ebffa,
  ];

  /// <summary>The exponents of <see cref="_tanCosinePolynomial"/>.</summary>
  private static readonly int[] _tanCosinePolynomialExponent = [1, 5, 7, 7, 6, 5];

}
