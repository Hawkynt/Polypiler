// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The correctly rounded cube root, converted from CORE-MATH's <c>cbrt.c</c> and <c>cbrtf.c</c>
/// (MIT). See <c>THIRD-PARTY-NOTICE.CORE-MATH.Roots.txt</c>.
/// </summary>
public static partial class FloatOperations {

  // 1, 2^(1/3) and 2^(2/3): 0x1.428a2f98d728bp+0, 0x1.965fea53d6e3dp+0.
  private static readonly double[] _cbrtScale = [1.0, 1.2599210498948732, 1.5874010519681996];

  // c0 + c1*x + c2*x^2 + c3*x^3 approximates x^(1/3) on [1, 2] with an error below 9.2e-5:
  // 0x1.1b0babccfef9cp-1, 0x1.2c9a3e94d1da5p-1, -0x1.4dc30b1a1ddbap-3, 0x1.7a8d3e4ec9b07p-6.
  private static readonly double[] _cbrtPolynomial = [0.5528234184016472, 0.5871142918266982, -0.16296967194987905, 0.02310496411078147];

  // The reciprocal of the scale, with the sign: index 2 * it + sign.
  private static readonly double[] _cbrtReciprocalScale = [1.0, -1.0, 0.5, -0.5, 0.25, -0.25];

  // The polynomial of cbrtf on [1, 2]: 0x1.2319d352ea5d5p-1, 0x1.67ad8ee258d1ap-1,
  // -0x1.9342edf9cbad9p-2, 0x1.b6388fc510a75p-3, -0x1.6002455599e2fp-4, 0x1.7b096936192c4p-6,
  // -0x1.e5577187e8bf8p-9, 0x1.169ef81d6c34ep-12.
  private static readonly double[] _cbrtSinglePolynomial = [
    0.5685564078059381, 0.7024960185339382, -0.39381000363475277, 0.21397507019181075,
    -0.08593966563932363, 0.023134567971640832, -0.003702862366439682, 0.00026571366637555694
  ];

  /// <summary>
  /// The cube root of <paramref name="x"/>, correctly rounded to nearest with ties to even. A zero,
  /// an infinity and a NaN are returned as they are; a negative <paramref name="x"/> has a negative root.
  /// </summary>
  /// <param name="x">The radicand.</param>
  /// <returns>The correctly rounded cube root.</returns>
  public static double Cbrt(double x) {
    const double U0 = 0.3333333333333333;   // 0x1.5555555555555p-2
    const double U1 = 0.2222222222222222;   // 0x1.c71c71c71c71cp-3
    const double Half = 1.1102230246251565E-16;   // 0x1p-53, half an ulp of 1
    const double Ulp = 2.220446049250313E-16;     // 0x1p-52
    var hx = (ulong)BitConverter.DoubleToInt64Bits(x);
    var mant = hx & ulong.MaxValue >> 12;
    var sign = (int)(hx >> 63);
    var e = (int)(hx >> 52 & 0x7ff);
    if (((e + 1) & 0x7ff) < 2) {
      var ix = hx & ulong.MaxValue >> 1;
      if (e == 0x7ff || ix == 0)
        return x + x;

      // A subnormal: normalize the significand.
      var nz = System.Numerics.BitOperations.LeadingZeroCount(ix) - 11;
      mant <<= nz;
      mant &= ulong.MaxValue >> 12;
      e -= nz - 1;
    }

    e += 3072;
    var et = e / 3;
    var it = e % 3;

    // 2^(3k+it) <= |x| < 2^(3k+it+1): cbrt(x) = cbrt(zz) * 2^(et-1365) with 1 <= |zz| < 8, and
    // cbrt(zz) = cbrt(z) * scale[it] with 1 <= z < 2.
    var oneBits = mant | 0x3ffUL << 52;
    var zz = BitConverter.Int64BitsToDouble((long)(unchecked(oneBits + ((ulong)it << 52)) | (ulong)sign << 63));
    var scale = BitConverter.Int64BitsToDouble((long)((ulong)BitConverter.DoubleToInt64Bits(_cbrtScale[it]) | (ulong)sign << 63));
    var z = BitConverter.Int64BitsToDouble((long)oneBits);
    var r = 1 / z;
    var rr = r * _cbrtReciprocalScale[it << 1 | sign];
    var z2 = z * z;
    var c0 = _cbrtPolynomial[0] + z * _cbrtPolynomial[1];
    var c2 = _cbrtPolynomial[2] + z * _cbrtPolynomial[3];
    var y = c0 + z2 * c2;
    var y2 = y * y;

    // A cubic variant of Newton's method on f(y) = 1 - z/y^3.
    var h = y2 * (y * r) - 1;
    y -= h * y * (U0 - U1 * h);
    y *= scale;

    // One linear Newton step on zz with y^3 to about 106 bits.
    y2 = y * y;
    var y2l = Math.FusedMultiplyAdd(y, y, -y2);
    var y3 = y2 * y;
    var y3l = Math.FusedMultiplyAdd(y, y2, -y3) + y * y2l;
    h = (y3 - zz + y3l) * rr;
    var dy = h * (y * U0);
    var y1 = y - dy;
    dy = y - y1 - dy;

    // The root is y1 + dy with |dy| < 1/2 ulp(y1); close to a midpoint another step decides.
    var ady = Math.Abs(dy);
    var ady0 = Math.Abs(ady - Half);
    var ady1 = Math.Abs(ady - (Ulp + Half));
    if (ady0 < 2.6469779601696886E-23 || ady1 < 2.6469779601696886E-23) {   // 0x1p-75
      y2 = y1 * y1;
      y2l = Math.FusedMultiplyAdd(y1, y1, -y2);
      y3 = y2 * y1;
      y3l = Math.FusedMultiplyAdd(y1, y2, -y3) + y1 * y2l;
      h = (y3 - zz + y3l) * rr;
      dy = h * (y1 * U0);
      y = y1 - dy;
      dy = y1 - y - dy;
      y1 = y;
      ady = Math.Abs(dy);
      ady0 = Math.Abs(ady - Half);
      ady1 = Math.Abs(ady - (Ulp + Half));
      if (ady0 < 3.1554436208840472E-30 || ady1 < 3.1554436208840472E-30) {   // 0x1p-98
        // The two radicands whose roots lie too close to a midpoint even for this.
        var azz = Math.Abs(zz);
        if (azz == 3.2146036897957497)   // 0x1.9b78223aa307cp+1
          y1 = CopySign(1.4758508835342132, zz);   // 0x1.79d15d0e8d59cp+0
        if (azz == 6.531417795099968)   // 0x1.a202bfc89ddffp+2
          y1 = CopySign(1.86925759992312, zz);   // 0x1.de87aa837820fp+0
      }
    }

    // Scale by 2^(et-1365); an exact root (its last 16 bits nearly zero) is rounded to it.
    var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(y1) + ((ulong)(et - 342 - 1023) << 52));
    var m0 = (long)(bits << 30);
    var m1 = m0 >> 63;
    if ((ulong)(m0 ^ m1) <= 1UL << 30) {
      var rounded = BitConverter.Int64BitsToDouble((long)(((ulong)BitConverter.DoubleToInt64Bits(y1) + (1UL << 15)) & 0xffffffffffff0000UL));
      if (Math.Abs(rounded - y1 - dy) < 8.673617379884035E-19 || Math.Abs(zz) == 1.0)   // 0x1p-60
        bits = (bits + (1UL << 15)) & 0xffffffffffff0000UL;
    }

    return BitConverter.Int64BitsToDouble((long)bits);
  }

  /// <summary>
  /// The cube root of <paramref name="x"/> of <c>float</c>, correctly rounded to nearest with ties to
  /// even. A zero, an infinity and a NaN are returned as they are.
  /// </summary>
  /// <param name="x">The radicand.</param>
  /// <returns>The correctly rounded cube root.</returns>
  public static float CbrtF(float x) {
    var u = (uint)BitConverter.SingleToInt32Bits(x);
    var au = u << 1;
    var sgn = u >> 31;
    var e = (int)(au >> 24);
    if (au < 1u << 24 || au >= 0xffu << 24) {
      if (au >= 0xffu << 24)
        return x + x;

      if (au == 0)
        return x;

      // A subnormal: normalize the significand.
      var nz = System.Numerics.BitOperations.LeadingZeroCount(au) - 7;
      au <<= nz;
      e -= nz - 1;
    }

    var mant = au & 0xffffff;
    var z = BitConverter.Int64BitsToDouble((long)((ulong)mant << 28 | 0x3ffUL << 52));
    e += 899;
    var et = e / 3;
    var it = e % 3;

    // The scale 2^(et-342) * 2^(it/3) with the sign of x.
    var isc = unchecked((ulong)BitConverter.DoubleToInt64Bits(_cbrtScale[it]) + ((ulong)(et - 342) << 52));
    isc |= (ulong)sgn << 63;
    var scale = BitConverter.Int64BitsToDouble((long)isc);
    var r0 = -0.024975246527242426 / z;   // -0x1.9931c6c2d19d1p-6
    var z2 = z * z;
    var z4 = z2 * z2;
    var f = _cbrtSinglePolynomial[0] + z * _cbrtSinglePolynomial[1] + z2 * (_cbrtSinglePolynomial[2] + z * _cbrtSinglePolynomial[3])
      + z4 * (_cbrtSinglePolynomial[4] + z * _cbrtSinglePolynomial[5] + z2 * (_cbrtSinglePolynomial[6] + z * _cbrtSinglePolynomial[7])) + r0;
    var r = f * scale;
    var ub = (float)r;
    var lb = (float)(r - scale * 1.4182e-9);
    if (ub == lb)
      return ub;

    // One Newton step. Upstream then snaps a result close to an exact root onto it; rounding to
    // nearest, no float reaches that step (every one was tried): the fast path returns exact roots.
    const double U0 = -13.34654827009379;   // -0x1.ab16ec65d138fp+3
    var h = f * f * f - z;
    f -= f * r0 * U0 * h;
    return (float)(f * scale);
  }

}
