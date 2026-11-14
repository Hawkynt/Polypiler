using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The operations of IEEE 754-2019 §5.4.1 that round once: the fused multiply-add, the remainder and
/// the reciprocal. The fused multiply-add of <c>double</c> is converted from musl's <c>fma.c</c>
/// (MIT), which computes the exact product in 128 bits of integers; the one of <c>float</c> from
/// musl's <c>fmaf.c</c> (FreeBSD, BSD-2-Clause), which needs only doubles. The remainder is converted
/// from musl's <c>remquo.c</c> and <c>remquof.c</c>. See <c>THIRD-PARTY-NOTICE.musl.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>x * y + z</c> rounded once, to nearest with ties to even. The product is formed exactly in
  /// 128 bits, the addend aligned to it with a sticky bit, and the sum converted to a double with
  /// one rounding; a result below the normal range is prepared so that the final scaling rounds once.
  /// </summary>
  /// <param name="x">The first factor.</param>
  /// <param name="y">The second factor.</param>
  /// <param name="z">The addend.</param>
  /// <returns>The correctly rounded result.</returns>
  public static double FusedMultiplyAdd(double x, double y, double z) {
    const int ZeroInfinityNaN = 0x7ff - 0x3ff - 52 - 1;
    var ex = FusedExponent(x);
    var ey = FusedExponent(y);
    var ez = FusedExponent(z);
    if (ex >= ZeroInfinityNaN || ey >= ZeroInfinityNaN)
      return x * y + z;

    // A zero addend leaves the product, rounded once; musl adds the zero, which turns a product that
    // underflows to -0 into +0 although the exact sum is negative.
    if (ez >= ZeroInfinityNaN)
      return ez > ZeroInfinityNaN ? x * y : z;

    // The exact product; either its top 20 or 21 bits and its last 2 bits are zero.
    var mx = FusedSignificand(x);
    var my = FusedSignificand(y);
    var mz = FusedSignificand(z);
    var rlo = unchecked(mx * my);
    var rhi = MultiplyHigh(mx, my);

    // Align the exponents: z is shifted left by kz and the product right by kr, kz + kr == d.
    var e = ex + ey;
    var d = ez - e;
    ulong zlo;
    ulong zhi;
    if (d > 0) {
      if (d < 64) {
        zlo = mz << d;
        zhi = mz >> (64 - d);
      } else {
        zlo = 0;
        zhi = mz;
        e = ez - 64;
        d -= 64;
        if (d >= 64) {
          rlo = 1;
          rhi = 0;
        } else if (d > 0) {
          rlo = rhi << (64 - d) | rlo >> d | Sticky(rlo << (64 - d));
          rhi >>= d;
        }
      }
    } else {
      zhi = 0;
      d = -d;
      if (d == 0)
        zlo = mz;
      else if (d < 64)
        zlo = mz >> d | Sticky(mz << (64 - d));
      else
        zlo = 1;
    }

    // Add or subtract.
    var negative = (BitConverter.DoubleToInt64Bits(x) < 0) != (BitConverter.DoubleToInt64Bits(y) < 0);
    var sameSign = negative == BitConverter.DoubleToInt64Bits(z) < 0;
    var nonzero = true;
    if (sameSign) {
      rlo = unchecked(rlo + zlo);
      rhi = unchecked(rhi + zhi + (rlo < zlo ? 1UL : 0UL));
    } else {
      var before = rlo;
      rlo = unchecked(rlo - zlo);
      rhi = unchecked(rhi - zhi - (before < rlo ? 1UL : 0UL));
      if (rhi >> 63 != 0) {
        rlo = unchecked(0 - rlo);
        rhi = unchecked(0 - rhi - (rlo != 0 ? 1UL : 0UL));
        negative = !negative;
      }

      nonzero = rhi != 0;
    }

    // The top 63 bits of the result into rhi, the last of them sticky.
    if (nonzero) {
      e += 64;
      d = System.Numerics.BitOperations.LeadingZeroCount(rhi) - 1;
      rhi = rhi << d | rlo >> (64 - d) | Sticky(rlo << d);
    } else if (rlo != 0) {
      d = System.Numerics.BitOperations.LeadingZeroCount(rlo) - 1;
      rhi = d < 0 ? rlo >> 1 | (rlo & 1) : rlo << d;
    } else {
      // An exact zero, whose sign the plain expression gets right.
      return x * y + z;
    }

    e -= d;

    // Convert to a double: |r| is in [2^62, 2^63].
    var i = (long)rhi;
    var r = (double)(negative ? -i : i);
    if (e < -1022 - 62) {
      // The result is subnormal before rounding.
      if (e == -1022 - 63) {
        var c = negative ? -9223372036854775808.0 : 9223372036854775808.0;
        if (r == c)
          return negative ? -2.2250738585072014E-308 : 2.2250738585072014E-308;

        // One bit is lost when the result is scaled: add another top bit, so that the conversion
        // rounds once if it is inexact.
        if (rhi << 53 != 0) {
          i = (long)(rhi >> 1 | (rhi & 1) | 1UL << 62);
          r = negative ? -i : i;
          r = 2 * r - c;
        }
      } else {
        // Round only once, when scaled.
        i = (long)((rhi >> 10 | Sticky(rhi << 54)) << 10);
        r = negative ? -i : i;
      }
    }

    return ScaleB(r, e);
  }

  /// <summary>
  /// <c>x * y + z</c> of <c>float</c> rounded once. The product of two floats is exact in a double,
  /// and the double sum differs from the exact one only where rounding it to a float again could
  /// round twice: when it lies halfway between two floats and is inexact, its last bit is moved
  /// towards the exact sum.
  /// </summary>
  /// <param name="x">The first factor.</param>
  /// <param name="y">The second factor.</param>
  /// <param name="z">The addend.</param>
  /// <returns>The correctly rounded result.</returns>
  public static float FusedMultiplyAddF(float x, float y, float z) {
    var xy = (double)x * y;
    var result = xy + z;
    var bits = BitConverter.DoubleToInt64Bits(result);
    var e = (int)(bits >> 52 & 0x7ff);
    // The bits of the double below those the float keeps: 29 of a normal float, more of a subnormal
    // one. From 54 on the double is below half the smallest float and rounds to zero as the exact
    // sum does; at 53 the half is the implicit bit, so the double is halfway when its fraction is 0.
    var dropped = e < 0x3ff - 126 ? 0x3ff - 126 - e + 29 : 29;
    if (dropped > 53)
      return (float)result;

    var half = dropped == 53 ? 0L : 1L << (dropped - 1);
    var rest = bits & (dropped == 53 ? (1L << 52) - 1 : (1L << dropped) - 1);
    if (rest != half || e == 0x7ff || (result - xy == z && result - z == xy))
      return (float)result;

    // An inexact result halfway between two floats: the last bit goes in the direction of the error.
    var below = bits < 0;
    var error = below == z > xy ? xy - result + z : z - result + xy;
    bits = below == error < 0 ? bits + 1 : bits - 1;
    return (float)BitConverter.Int64BitsToDouble(bits);
  }

  /// <summary>
  /// The remainder of IEEE 754-2019 §5.3.1: <c>x - n * y</c> with <c>n</c> the integer nearest to
  /// <c>x / y</c>, a tie going to the even one. It is exact; a zero result has the sign of <c>x</c>,
  /// and a zero <c>y</c>, an infinite <c>x</c> or a NaN give a NaN.
  /// </summary>
  /// <param name="x">The dividend.</param>
  /// <param name="y">The divisor.</param>
  /// <returns>The remainder.</returns>
  public static double IeeeRemainder(double x, double y) {
    var ux = BitConverter.DoubleToInt64Bits(x);
    var uy = BitConverter.DoubleToInt64Bits(y);
    var ex = (int)(ux >> 52 & 0x7ff);
    var ey = (int)(uy >> 52 & 0x7ff);
    var sx = ux < 0;
    var sy = uy < 0;
    if (uy << 1 == 0 || IsNaN(y) || ex == 0x7ff)
      return x * y / (x * y);

    if (ux << 1 == 0)
      return x;

    // Normalize the significands.
    var mx = (ulong)ux;
    var my = (ulong)uy;
    if (ex == 0) {
      for (var i = mx << 12; i >> 63 == 0; i <<= 1)
        --ex;

      mx <<= -ex + 1;
    } else {
      mx &= ulong.MaxValue >> 12;
      mx |= 1UL << 52;
    }

    if (ey == 0) {
      for (var i = my << 12; i >> 63 == 0; i <<= 1)
        --ey;

      my <<= -ey + 1;
    } else {
      my &= ulong.MaxValue >> 12;
      my |= 1UL << 52;
    }

    var q = 0U;
    if (ex < ey && ex + 1 != ey)
      return x;

    if (ex >= ey) {
      // x mod y, one quotient bit at a time.
      for (; ex > ey; --ex) {
        var i = unchecked(mx - my);
        if (i >> 63 == 0) {
          mx = i;
          ++q;
        }

        mx <<= 1;
        q <<= 1;
      }

      var last = unchecked(mx - my);
      if (last >> 63 == 0) {
        mx = last;
        ++q;
      }

      if (mx == 0)
        ex = -60;
      else
        for (; mx >> 52 == 0; mx <<= 1)
          --ex;
    }

    // Scale the result and decide between |x| and |x| - |y|.
    if (ex > 0) {
      mx -= 1UL << 52;
      mx |= (ulong)ex << 52;
    } else {
      mx >>= -ex + 1;
    }

    var r = BitConverter.Int64BitsToDouble((long)mx);
    var m = sy ? -y : y;
    if (ex == ey || (ex + 1 == ey && (2 * r > m || (2 * r == m && q % 2 != 0))))
      r -= m;

    return sx ? -r : r;
  }

  /// <summary>The remainder of IEEE 754-2019 §5.3.1 of <c>float</c>; see <see cref="IeeeRemainder"/>.</summary>
  /// <param name="x">The dividend.</param>
  /// <param name="y">The divisor.</param>
  /// <returns>The remainder.</returns>
  public static float IeeeRemainderF(float x, float y) {
    var ux = (uint)BitConverter.SingleToInt32Bits(x);
    var uy = (uint)BitConverter.SingleToInt32Bits(y);
    var ex = (int)(ux >> 23 & 0xff);
    var ey = (int)(uy >> 23 & 0xff);
    var sx = ux >> 31 != 0;
    var sy = uy >> 31 != 0;
    if (uy << 1 == 0 || IsNaNF(y) || ex == 0xff)
      return x * y / (x * y);

    if (ux << 1 == 0)
      return x;

    var mx = ux;
    var my = uy;
    if (ex == 0) {
      for (var i = mx << 9; i >> 31 == 0; i <<= 1)
        --ex;

      mx <<= -ex + 1;
    } else {
      mx &= uint.MaxValue >> 9;
      mx |= 1U << 23;
    }

    if (ey == 0) {
      for (var i = my << 9; i >> 31 == 0; i <<= 1)
        --ey;

      my <<= -ey + 1;
    } else {
      my &= uint.MaxValue >> 9;
      my |= 1U << 23;
    }

    var q = 0U;
    if (ex < ey && ex + 1 != ey)
      return x;

    if (ex >= ey) {
      for (; ex > ey; --ex) {
        var i = unchecked(mx - my);
        if (i >> 31 == 0) {
          mx = i;
          ++q;
        }

        mx <<= 1;
        q <<= 1;
      }

      var last = unchecked(mx - my);
      if (last >> 31 == 0) {
        mx = last;
        ++q;
      }

      if (mx == 0)
        ex = -30;
      else
        for (; mx >> 23 == 0; mx <<= 1)
          --ex;
    }

    if (ex > 0) {
      mx -= 1U << 23;
      mx |= (uint)ex << 23;
    } else {
      mx >>= -ex + 1;
    }

    var r = BitConverter.Int32BitsToSingle((int)mx);
    var m = sy ? -y : y;
    if (ex == ey || (ex + 1 == ey && (2 * r > m || (2 * r == m && q % 2 != 0))))
      r -= m;

    return sx ? -r : r;
  }

  /// <summary>The reciprocal <c>1 / x</c>, rounded once.</summary>
  /// <param name="x">The operand.</param>
  /// <returns>The reciprocal.</returns>
  public static double Reciprocal(double x) => 1.0 / x;

  /// <summary>The reciprocal <c>1 / x</c> of <c>float</c>, rounded once.</summary>
  /// <param name="x">The operand.</param>
  /// <returns>The reciprocal.</returns>
  public static float ReciprocalF(float x) => 1.0f / x;

  /// <summary>
  /// The high 64 bits of the 128-bit product of two <c>ulong</c>, from four products of 32-bit halves
  /// (musl's <c>mul</c> of <c>fma.c</c>); the low 64 bits are the wrapping product.
  /// </summary>
  /// <param name="x">The first factor.</param>
  /// <param name="y">The second factor.</param>
  /// <returns>The high half of the product.</returns>
  public static ulong MultiplyHigh(ulong x, ulong y) {
    var xlo = x & 0xffffffffUL;
    var xhi = x >> 32;
    var ylo = y & 0xffffffffUL;
    var yhi = y >> 32;
    var t1 = xlo * ylo;
    var t2 = unchecked(xlo * yhi + xhi * ylo);
    var carry = t2 < xlo * yhi ? 1UL << 32 : 0UL;
    var t3 = xhi * yhi;
    var lo = unchecked(t1 + (t2 << 32));
    return unchecked(t3 + (t2 >> 32) + carry + (t1 > lo ? 1UL : 0UL));
  }

  /// <summary>1 when any bit is set, otherwise 0: the sticky bit of what a shift drops.</summary>
  private static ulong Sticky(ulong bits) => bits != 0 ? 1UL : 0UL;

  /// <summary>
  /// The exponent musl's <c>fma.c</c> normalizes a double to: that of its significand shifted so that
  /// the top 10 and the last bit of 64 are zero; 972 for a zero and 971 for an infinity or a NaN.
  /// </summary>
  private static int FusedExponent(double x) {
    var bits = BitConverter.DoubleToInt64Bits(x);
    var e = (int)(bits >> 52 & 0x7ff);
    if (e == 0) {
      e = (int)(BitConverter.DoubleToInt64Bits(x * 9223372036854775808.0) >> 52 & 0x7ff);
      e = e != 0 ? e - 63 : 0x800;
    }

    return e - (0x3ff + 52 + 1);
  }

  /// <summary>The significand of a finite, non-zero double as <see cref="FusedExponent"/> scales it.</summary>
  private static ulong FusedSignificand(double x) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    if ((bits >> 52 & 0x7ff) == 0)
      bits = (ulong)BitConverter.DoubleToInt64Bits(x * 9223372036854775808.0);

    return ((bits & ((1UL << 52) - 1)) | 1UL << 52) << 1;
  }

}
