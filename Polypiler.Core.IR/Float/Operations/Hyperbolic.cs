using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The hyperbolic functions, correctly rounded: <c>sinh</c>, <c>cosh</c> and <c>tanh</c> in both
/// formats, converted from CORE-MATH (MIT). This part holds the <c>float</c> forms, which evaluate in
/// doubles: <c>e^x</c> splits into <c>2^(k/32)</c> from a table and a short polynomial, and only when
/// the result lies too close to a rounding boundary does a longer polynomial decide it. See
/// <c>THIRD-PARTY-NOTICE.CORE-MATH.Hyperbolic.txt</c>.
/// </summary>
public static partial class FloatOperations {

  // 2^(i/32 - 1) for i in 0..31, as the bits of doubles; shared by SinhF and CoshF.
  private static readonly ulong[] _sinhFExp2 = [
    0x3fe0000000000000UL, 0x3fe059b0d3158574UL, 0x3fe0b5586cf9890fUL, 0x3fe11301d0125b51UL,
    0x3fe172b83c7d517bUL, 0x3fe1d4873168b9aaUL, 0x3fe2387a6e756238UL, 0x3fe29e9df51fdee1UL,
    0x3fe306fe0a31b715UL, 0x3fe371a7373aa9cbUL, 0x3fe3dea64c123422UL, 0x3fe44e086061892dUL,
    0x3fe4bfdad5362a27UL, 0x3fe5342b569d4f82UL, 0x3fe5ab07dd485429UL, 0x3fe6247eb03a5585UL,
    0x3fe6a09e667f3bcdUL, 0x3fe71f75e8ec5f74UL, 0x3fe7a11473eb0187UL, 0x3fe82589994cce13UL,
    0x3fe8ace5422aa0dbUL, 0x3fe93737b0cdc5e5UL, 0x3fe9c49182a3f090UL, 0x3fea5503b23e255dUL,
    0x3feae89f995ad3adUL, 0x3feb7f76f2fb5e47UL, 0x3fec199bdd85529cUL, 0x3fecb720dcef9069UL,
    0x3fed5818dcfba487UL, 0x3fedfc97337b9b5fUL, 0x3feea4afa2a490daUL, 0x3fef50765b6e4540UL
  ];

  // 2^(h/32) ~ even + h * odd, the fast polynomial of SinhF and CoshF.
  private static readonly double[] _sinhFFast = [1, 0.021660849391257477, 0.0002345984913513542, 1.6938658699950235e-06];

  // The accurate polynomial of SinhF and CoshF.
  private static readonly double[] _sinhFAccurate = [
    1, 0.02166084939249829, 0.0002345961982022468, 1.6938509724129055e-06, 9.172562701702629e-09,
    3.973729405780548e-11, 1.4345723178374038e-13
  ];

  // sinh(x) = x + x^3 * P(x^2) for 0.125 > |x| > 0x1.250bfep-11.
  private static readonly double[] _sinhFSmall = [0.16666666666666666, 0.00833333333335723, 0.00019841269076590929, 2.756514913511476e-06];

  // cosh(x) = 1 + x^2 * P(x^2) for 0.125 > |x| >= 2^-11.
  private static readonly double[] _coshFSmall = [0.4999999999999984, 0.04166666666748819, 0.0013888887416776143, 2.4812354013894482e-05];

  // tanh(x) = x * N(x^2) / D(x^2) for 0x1.205966p+3 >= |x| >= 2^-13.
  private static readonly double[] _tanhFNumerator = [
    1.0, 0.14869591254532963, 0.00551287098907202, 7.653349704714027e-05,
    4.4724281332217524e-07, 1.0666590627970085e-09, 8.352093632538344e-13, 9.376645859884988e-17
  ];

  private static readonly double[] _tanhFDenominator = [
    1.0, 0.4820292458786627, 0.03285595294862704, 0.0007262056643542124,
    6.510296665448557e-06, 2.4619801106746077e-08, 3.5204157099784045e-11, 1.2726168760182741e-14
  ];

  /// <summary>
  /// The hyperbolic sine of a <c>float</c>, correctly rounded. Tiny arguments take the Taylor
  /// expansion, small ones an odd polynomial, the others <c>(e^|x| - e^-|x|) / 2</c> with both
  /// exponentials from one table; beyond <c>0x1.65a9f8p+6</c> the result overflows.
  /// </summary>
  /// <param name="x">The argument.</param>
  /// <returns><c>sinh(x)</c>.</returns>
  public static float SinhF(float x) {
    const double InverseLn2 = 46.16624130844683; // 32 / ln 2
    var z = (double)x;
    var ux = (uint)BitConverter.SingleToInt32Bits(x) << 1;
    if (ux > 0x8565a9f8u) {
      // |x| > 0x1.65a9f8p+6: infinity, NaN, or an overflow.
      if (ux >= 0xff000000u)
        return ux << 8 != 0 ? x + x : x;

      return CopySignF(2.0f, x) * 3.4028234663852886e+38f;
    }

    if (ux < 0x7c000000u) {
      // |x| < 0.125
      if (ux <= 0x74250bfeu) {
        // |x| <= 0x1.250bfep-11
        if (ux < 0x66000000u)
          return MathF.FusedMultiplyAdd(x, MathF.Abs(x), x);

        // The one argument whose cubic term rounds the wrong way.
        if (ux == 0x74250bfeu) {
          var sign = CopySignF(1.0f, x);
          return sign * 0.0005589424981735647f + sign * 1.4551915228366852e-11f;
        }

        return x * 0.1666666716337204f * (x * x) + x;
      }

      var z2 = z * z;
      var z4 = z2 * z2;
      return (float)(z + z2 * z * ((_sinhFSmall[0] + z2 * _sinhFSmall[1]) + z4 * (_sinhFSmall[2] + z2 * _sinhFSmall[3])));
    }

    // x = (k + h) * ln 2 / 32 with k integer; e^x = 2^(k/32) * 2^(h/32).
    var a = InverseLn2 * z;
    var ia = Math.Round(a);
    var h = a - ia;
    var h2 = h * h;
    var jp = BitConverter.DoubleToInt64Bits(ia + 6755399441055744.0);
    var jm = -jp;
    var sp = BitConverter.Int64BitsToDouble(unchecked((long)(_sinhFExp2[(int)(jp & 31)] + ((ulong)(jp >> 5) << 52))));
    var sm = BitConverter.Int64BitsToDouble(unchecked((long)(_sinhFExp2[(int)(jm & 31)] + ((ulong)(jm >> 5) << 52))));
    var te = _sinhFFast[0] + h2 * _sinhFFast[2];
    var to = _sinhFFast[1] + h2 * _sinhFFast[3];
    var rp = sp * (te + h * to);
    var rm = sm * (te - h * to);
    var r = rp - rm;
    var ub = (float)r;
    var lb = (float)(r - 1.52e-10 * r);
    if (ub != lb) {
      // Too close to a rounding boundary: h again in more bits, the longer polynomial.
      const double InverseLn2High = 46.16624128818512;
      const double InverseLn2Low = 2.026170940661134e-08;
      h = (InverseLn2High * z - ia) + InverseLn2Low * z;
      h2 = h * h;
      te = _sinhFAccurate[0] + h2 * _sinhFAccurate[2] + h2 * h2 * (_sinhFAccurate[4] + h2 * _sinhFAccurate[6]);
      to = _sinhFAccurate[1] + h2 * (_sinhFAccurate[3] + h2 * _sinhFAccurate[5]);
      r = sp * (te + h * to) - sm * (te - h * to);
      ub = (float)r;
    }

    return ub;
  }

  /// <summary>
  /// The hyperbolic cosine of a <c>float</c>, correctly rounded. Small arguments take an even
  /// polynomial, the others <c>(e^|x| + e^-|x|) / 2</c> with both exponentials from one table;
  /// beyond <c>0x1.65a9f8p+6</c> the result overflows.
  /// </summary>
  /// <param name="x">The argument.</param>
  /// <returns><c>cosh(x)</c>.</returns>
  public static float CoshF(float x) {
    const double InverseLn2 = 46.16624130844683; // 32 / ln 2
    var z = (double)x;
    var ax = (uint)BitConverter.SingleToInt32Bits(x) << 1;
    if (ax > 0x8565a9f8u) {
      // |x| > 0x1.65a9f8p+6: infinity, NaN, or an overflow.
      if (ax >= 0xff000000u)
        return x * x;

      return 2.0f * 3.4028234663852886e+38f;
    }

    if (ax < 0x7c000000u) {
      // |x| < 0.125
      if (ax < 0x74000000u) {
        // |x| < 2^-11
        if (ax < 0x66000000u)
          return MathF.FusedMultiplyAdd(MathF.Abs(x), 2.9802322387695312e-08f, 1.0f);

        return 0.5f * x * x + 1.0f;
      }

      var z2 = z * z;
      var z4 = z2 * z2;
      return (float)(1.0 + z2 * ((_coshFSmall[0] + z2 * _coshFSmall[1]) + z4 * (_coshFSmall[2] + z2 * _coshFSmall[3])));
    }

    // x = (k + h) * ln 2 / 32 with k integer; e^x = 2^(k/32) * 2^(h/32).
    var a = InverseLn2 * z;
    var ia = Math.Round(a);
    var h = a - ia;
    var h2 = h * h;
    var jp = BitConverter.DoubleToInt64Bits(ia + 6755399441055744.0);
    var jm = -jp;
    var sp = BitConverter.Int64BitsToDouble(unchecked((long)(_sinhFExp2[(int)(jp & 31)] + ((ulong)(jp >> 5) << 52))));
    var sm = BitConverter.Int64BitsToDouble(unchecked((long)(_sinhFExp2[(int)(jm & 31)] + ((ulong)(jm >> 5) << 52))));
    var te = _sinhFFast[0] + h2 * _sinhFFast[2];
    var to = _sinhFFast[1] + h2 * _sinhFFast[3];
    var rp = sp * (te + h * to);
    var rm = sm * (te - h * to);
    var r = rp + rm;
    var ub = (float)r;
    var lb = (float)(r - 1.45e-10 * r);
    if (ub != lb) {
      // Too close to a rounding boundary: h again in more bits, the longer polynomial.
      const double InverseLn2High = 46.16624128818512;
      const double InverseLn2Low = 2.026170940661134e-08;
      h = (InverseLn2High * z - ia) + InverseLn2Low * z;
      h2 = h * h;
      te = _sinhFAccurate[0] + h2 * _sinhFAccurate[2] + h2 * h2 * (_sinhFAccurate[4] + h2 * _sinhFAccurate[6]);
      to = _sinhFAccurate[1] + h2 * (_sinhFAccurate[3] + h2 * _sinhFAccurate[5]);
      r = sp * (te + h * to) + sm * (te - h * to);
      ub = (float)r;
    }

    return ub;
  }

  /// <summary>
  /// The hyperbolic tangent of a <c>float</c>, correctly rounded. Tiny arguments take the Taylor
  /// expansion, the others up to <c>0x1.205966p+3</c> a rational approximation in doubles; beyond it
  /// the result rounds to <c>±1</c>.
  /// </summary>
  /// <param name="x">The argument.</param>
  /// <returns><c>tanh(x)</c>.</returns>
  public static float TanhF(float x) {
    var z = (double)x;
    var ux = (uint)BitConverter.SingleToInt32Bits(x);
    var e = (int)(ux >> 23 & 0xff);
    if (e == 0xff) {
      // NaN or an infinity.
      if (ux << 9 != 0)
        return x + x;

      return ux >> 31 != 0 ? -1.0f : 1.0f;
    }

    if (e < 115) {
      // |x| < 2^-13
      if (e < 102) {
        // |x| < 2^-26
        if (ux << 1 == 0)
          return x;

        return MathF.FusedMultiplyAdd(-x, MathF.Abs(x), x);
      }

      var x2 = x * x;
      return MathF.FusedMultiplyAdd(x, -0.3333333432674408f * x2, x);
    }

    if (ux << 1 > 0x41102cb3u << 1)
      return CopySignF(1.0f, x) - CopySignF(2.9802322387695312e-08f, x);

    var z2 = z * z;
    var z4 = z2 * z2;
    var z8 = z4 * z4;
    var n0 = _tanhFNumerator[0] + z2 * _tanhFNumerator[1];
    var n2 = _tanhFNumerator[2] + z2 * _tanhFNumerator[3];
    var n4 = _tanhFNumerator[4] + z2 * _tanhFNumerator[5];
    var n6 = _tanhFNumerator[6] + z2 * _tanhFNumerator[7];
    n0 += z4 * n2;
    n4 += z4 * n6;
    n0 += z8 * n4;
    var d0 = _tanhFDenominator[0] + z2 * _tanhFDenominator[1];
    var d2 = _tanhFDenominator[2] + z2 * _tanhFDenominator[3];
    var d4 = _tanhFDenominator[4] + z2 * _tanhFDenominator[5];
    var d6 = _tanhFDenominator[6] + z2 * _tanhFDenominator[7];
    d0 += z4 * d2;
    d4 += z4 * d6;
    d0 += z8 * d4;
    return (float)(z * n0 / d0);
  }

}
