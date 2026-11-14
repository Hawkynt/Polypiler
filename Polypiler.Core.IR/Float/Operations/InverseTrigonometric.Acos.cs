// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The arc cosine, correctly rounded in both formats. Converted from CORE-MATH's
/// <c>src/binary64/acos/acos.c</c> and <c>src/binary32/acos/acosf.c</c> (MIT); see
/// <c>THIRD-PARTY-NOTICE.CORE-MATH.InverseTrigonometric.txt</c>. The tables both share with the arc
/// sine are those of <see cref="Asin"/> and <see cref="AsinF"/>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>0 and <c>pi</c>, as double-doubles.</summary>
  private static readonly double[] _acosPi = [
    0.0, 0.0, 3.141592653589793, 1.2246467991473532e-16
  ];

  /// <summary>
  /// The inputs whose arc cosine the refinement cannot round, then the result as the sum of two
  /// doubles.
  /// </summary>
  private static readonly double[] _acosExceptions = [
    0.999999999999936, 3.5762786865234566e-07, 1.3234889800848443e-23,
    0.08298723566019295, 1.4877135408445967, 5.551115123125783e-17,
    0.0009717008771394217, 1.5698246257648432, 1.6653345369377348e-16,
    0.999999999999984, 1.788139343261721e-07, 6.617444900424222e-24,
    0.038214879531014476, 1.5325721397906955, -1.5407439555097887e-33
  ];

  /// <summary>The polynomial for <c>|x| &lt; 0x1.c2a1dcp-1</c> in <c>float</c>; see <see cref="AcosF"/>.</summary>
  private static readonly double[] _acosFFast = [
    0.9999999997220561, 0.1666675305523315, 0.07491953938381704, 0.047534405138862854,
    -0.024905344107261872, 0.6698889818036169, -5.003757071019054, 27.02642690834356,
    -103.66551324982036, 288.04495822181497, -580.9121849063603, 842.6925540871983,
    -857.2868238883075, 581.0567760763246, -235.92908248702702, 43.51567221246845
  ];

  /// <summary>
  /// The arc cosine of <c>x</c> in radians, correctly rounded: a NaN outside <c>[-1, 1]</c>. Up to 0.5
  /// in magnitude <c>pi/2 - asin(x)</c>, below <c>2^-15</c> by a short series; above,
  /// <c>2 asin(sqrt((1 - x)/2))</c> or <c>pi - 2 asin(sqrt((1 + x)/2))</c>, the arc sine by the
  /// polynomials of <see cref="Asin"/>. When that does not round safely, a rotation by a multiple of
  /// <c>pi/64</c> and a Taylor series in double-double arithmetic decide.
  /// </summary>
  /// <param name="x">The cosine.</param>
  /// <returns>The angle in <c>[0, pi]</c>.</returns>
  public static double Acos(double x) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(x);
    var ax = bits << 1;
    double t;
    double z;
    double zl;
    double jd;
    double f0h;
    double f0l;
    double eps;
    if (ax > 0x7fc0000000000000UL) {
      // |x| > 0.5: acos(x) = 2 asin(sqrt((1 - x)/2)) for x > 0, pi - 2 asin(sqrt((1 + x)/2)) for x < 0.
      var k = (int)(bits >> 63) * 2;
      f0h = _acosPi[k];
      f0l = _acosPi[k + 1];
      if (ax >= 0x7fe0000000000000UL) {
        if (ax == 0x7fe0000000000000UL)
          return f0h + f0l;

        if (ax > 0xffe0000000000000UL)
          return x + x;

        return 0.0 / 0.0;
      }

      t = 2 - 2 * Math.Abs(x);
      jd = Math.Round(t * 32.0);
      z = CopySign(Math.Sqrt(t), x);
      zl = Math.FusedMultiplyAdd(z, z, -t) * ((-0.5 / t) * z);
      t = 0.25 * t - jd * 0.0078125;
      eps = Math.Abs(z * t) * 3.434752482434078e-16 + 2.465190328815662e-32;
    } else {
      f0h = 1.5707963267948966;
      f0l = 6.123233995736766e-17;
      if (ax <= 0x7e00000000000000UL) {
        // |x| < 2^-15: pi/2 - x - x^3/6; the cube is left out where it would underflow.
        const double Sixth = -0.16666666666666666;
        var v = ax <= 0x791967670d2e8fe8UL ? 0 : (x * x) * (Sixth * x);
        var h = f0h + -x;
        var w = -x - (h - f0h);
        var l = v + (w + f0l);
        const double Error = 1.990403348955723e-24;
        var lower = h + (l - Error);
        var upper = h + (l + Error);
        if (lower != upper)
          return AcosRefine(x, lower);

        return lower;
      }

      // acos(x) = pi/2 - asin(x), with the arc sine of Asin.
      t = x * x;
      jd = Math.Round(t * 128.0);
      t = Math.FusedMultiplyAdd(x, x, -0.0078125 * jd);
      z = -x;
      zl = 0;
      eps = (z * t) * 3.3393426912553537e-16;
    }

    // asin(z + zl) = (z + zl) * (c0 + (c1 + t * P(t))) with t = z^2 - j/128.
    var c = (int)jd * 8;
    var t2 = t * t;
    var d = t * ((_asinCoefficients[c + 2] + t * _asinCoefficients[c + 3])
      + t2 * ((_asinCoefficients[c + 4] + t * _asinCoefficients[c + 5]) + t2 * (_asinCoefficients[c + 6] + t * _asinCoefficients[c + 7])));
    var fh = _asinCoefficients[c];
    var fl = _asinCoefficients[c + 1] + d;

    // (fh, fl) *= (z, zl), then += (f0h, f0l).
    var product = z * fh;
    fl = (z * fl + zl * fh) + Math.FusedMultiplyAdd(z, fh, -product);
    fh = product;
    var sum = f0h + fh;
    var sumError = fh - (sum - f0h);
    fl = (f0l + fl) + sumError;
    fh = sum;

    var lb = fh + (fl - eps);
    var ub = fh + (fl + eps);
    if (lb != ub)
      return AcosRefine(x, lb);

    return lb;
  }

  /// <summary>
  /// The accurate path of <see cref="Acos"/>: with <c>x = cos(phi)</c> and <c>sqrt(1 - x^2)</c> in
  /// double-double, <c>phi = jt pi/64 - delta</c> for the nearest multiple, and <c>delta</c> by the
  /// Taylor series of its sine.
  /// </summary>
  private static double AcosRefine(double x, double phi) {
    // c2h + c2l = 1 - x^2.
    var s2 = x * x;
    var dx2 = Math.FusedMultiplyAdd(x, x, -s2);
    var c2h = 1.0 - s2;
    var c2l = -s2 - (c2h - 1.0);
    c2l -= dx2;
    var sum = c2h + c2l;
    c2l -= sum - c2h;
    c2h = sum;

    // ch + cl = sqrt(1 - x^2).
    var ch = Math.Sqrt(c2h);
    var cl = (c2l - Math.FusedMultiplyAdd(ch, ch, -c2h)) * (0.5 / ch);

    // jf = round(|phi - pi/2| * 64/pi), 0 <= jf <= 32.
    var jf = (int)Math.Round(Math.Abs(phi - 1.5707963267948966) * 20.371832715762604);
    var cosHigh = _asinSines[(32 - jf) * 2 + 1];
    var cosLow = _asinSines[(32 - jf) * 2];
    var sinHigh = _asinSines[jf * 2 + 1];
    var sinLow = _asinSines[jf * 2];

    // sin(delta) = sgn(x) ((Ch + Cl)(dsh + dsl) - (Sh + Sl)(dch + dcl)).
    var ax = Math.Abs(x);
    var dsh = ax - sinHigh;
    var dsl = -sinLow;
    var dch = ch - cosHigh;
    var dcl = cl - cosLow;

    // The products rounded to multiples of 2^-56 through the magic constant, so that v is exact.
    const double Magic = 0.09375;
    var sc = Math.FusedMultiplyAdd(sinHigh, dch, Magic) - Magic;
    var dsc = Math.FusedMultiplyAdd(sinHigh, dch, -sc);
    var cs = Math.FusedMultiplyAdd(cosHigh, dsh, Magic) - Magic;
    var dcs = Math.FusedMultiplyAdd(cosHigh, dsh, -cs);

    var v = cs - sc;
    var dv = (cosHigh * dsl + cosLow * dsh) - (sinHigh * dcl + sinLow * dch) - (dsc - dcs);
    sum = v + dv;
    dv -= sum - v;
    v = sum;
    var sgn = CopySign(1.0, x);
    var jt = 32 - jf * sgn;

    // v2 + dv2 = (v + dv)^2.
    var v2 = v * v;
    var dv2 = (v * dv + dv * v) + Math.FusedMultiplyAdd(v, v, -v2);
    v *= -sgn;
    dv *= -sgn;

    // The Taylor series of asin in v2: the tail in doubles, the head in double-double by Horner.
    var fl = v2 * (_asinTaylorTail[0] + v2 * (_asinTaylorTail[1] + v2 * _asinTaylorTail[2]));
    var fh = _asinTaylor[8] + fl;
    fl -= fh - _asinTaylor[8];
    fl = _asinTaylor[9] + fl;
    for (var i = 3; i >= 0; i--) {
      var high = v2 * fh;
      fl = (v2 * fl + dv2 * fh) + Math.FusedMultiplyAdd(v2, fh, -high);
      sum = _asinTaylor[2 * i] + high;
      var error = high - (sum - _asinTaylor[2 * i]);
      fl = (_asinTaylor[2 * i + 1] + fl) + error;
      fh = sum;
    }

    // fh + fl = -delta.
    var times = v * fh;
    fl = (v * fl + dv * fh) + Math.FusedMultiplyAdd(v, fh, -times);
    fh = times;

    // ph + pl + ps = jt * pi/64, exact products for 0 <= jt <= 64; then add fh + fl.
    var ph = jt * 0.04908738521234035;
    var pl = 1.684469643174416e-16 * jt;
    var ps = -3.917809439666406e-31 * jt;
    sum = fh + pl;
    var fhPart = sum - pl;
    var plPart = sum - fhPart;
    var low = (fh - fhPart) + (pl - plPart);
    ps = (fl + ps) + low;
    pl = sum;
    sum = ph + pl;
    pl -= sum - ph;
    ph = sum;
    sum = pl + ps;
    ps -= sum - pl;
    pl = sum;
    sum = ph + pl;
    pl -= sum - ph;
    ph = sum;
    sum = pl + ps;
    ps -= sum - pl;
    pl = sum;

    // When pl is a tie for ph + pl at the precision that matters, ps decides the direction.
    var tail = (ulong)BitConverter.DoubleToInt64Bits(pl);
    var e = (long)(tail >> 52 & 0x7ff) - 1023;
    e = 52 - (107 + e);
    e = e < 0 ? 0 : e;
    e = e > 52 ? 52 : e;
    var m = (1UL << 52) - (1UL << (int)e);
    e = e == 0 ? 64 : e;
    if (((tail + (1UL << (int)(e - 1))) & m) == 0) {
      for (var i = 0; i < _acosExceptions.Length; i += 3)
        if (x == _acosExceptions[i])
          return _acosExceptions[i + 1] + _acosExceptions[i + 2];

      var below = (ulong)BitConverter.DoubleToInt64Bits(ps);
      if (((below ^ tail) >> 63) != 0)
        tail--;
      else
        tail++;

      pl = BitConverter.Int64BitsToDouble((long)tail);
    }

    return ph + pl;
  }

  /// <summary>
  /// The arc cosine of a <c>float</c> in radians, correctly rounded: a NaN outside <c>[-1, 1]</c>. A
  /// polynomial evaluated in doubles answers when its error bounds round alike; otherwise the more
  /// accurate polynomials of <see cref="AsinF"/> give <c>pi/2 - asin(x)</c>, near 1 in
  /// <c>sqrt(1 - |x|)</c>.
  /// </summary>
  /// <param name="x">The cosine.</param>
  /// <returns>The angle in <c>[0, pi]</c>.</returns>
  public static float AcosF(float x) {
    const double HalfPi = 1.5707963267948966;
    var xs = (double)x;
    var bits = (uint)BitConverter.SingleToInt32Bits(x);
    var ax = bits << 1;
    if (ax >= 0x7fu << 24) {
      if (bits == 0x7fu << 23)
        return 0.0f;

      if (bits == 0x17fu << 23)
        return 3.1415927410125732f + -5.960464477539063e-08f;

      return ax > 0xffu << 24 ? x + x : (float)(0.0 / 0.0);
    }

    double r;
    if (ax < 0x7ec2a1dcu) {
      // |x| < 0x1.c2a1dcp-1; below 2^-63 the result is pi/2 rounded, without a spurious underflow.
      if (ax < 0x40000000u)
        return 1.5707963705062866f + -4.371138828673793e-08f;

      var z = xs;
      var z2 = z * z;
      var z4 = z2 * z2;
      var z8 = z4 * z4;
      var z16 = z8 * z8;
      r = z * ((((_acosFFast[0] + z2 * _acosFFast[1]) + z4 * (_acosFFast[2] + z2 * _acosFFast[3]))
          + z8 * ((_acosFFast[4] + z2 * _acosFFast[5]) + z4 * (_acosFFast[6] + z2 * _acosFFast[7])))
        + z16 * (((_acosFFast[8] + z2 * _acosFFast[9]) + z4 * (_acosFFast[10] + z2 * _acosFFast[11]))
          + z8 * ((_acosFFast[12] + z2 * _acosFFast[13]) + z4 * (_acosFFast[14] + z2 * _acosFFast[15]))));
      var ub = (float)(1.5707963270725467 - r);
      var lb = (float)(1.5707963265172467 - r);
      if (ub == lb)
        return ub;
    }

    if (ax < 0x7eu << 24) {
      if (bits == 0x328885a3u)
        return 1.5707963705062866f + 2.9802322387695312e-08f;

      if (bits == 0x39826222u)
        return 1.5705476999282837f + 2.9802322387695312e-08f;

      var x2 = xs * xs;
      r = (HalfPi - xs) - (xs * x2) * AsinFPolynomial12(x2, 0);
    } else {
      var bx = Math.Abs(xs);
      var z = 1.0 - bx;
      var s = CopySign(Math.Sqrt(z), xs);
      r = _acosFOffset[bits >> 31] + s * AsinFPolynomial12(z, 12);
    }

    return (float)r;
  }

  /// <summary>0 and <c>pi</c>, what <see cref="AcosF"/> adds near 1 and near -1.</summary>
  private static readonly double[] _acosFOffset = [
    0.0, 3.141592653589793
  ];

}
