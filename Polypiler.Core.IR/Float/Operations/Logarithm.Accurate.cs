// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The exact path of the natural and the decimal logarithm of <c>double</c>, converted from the
/// <c>dint.h</c> of CORE-MATH's <c>log</c> and <c>log10</c> (MIT): numbers with a significand of 128
/// bits, held as four <c>ulong</c> <c>[hi, lo, ex, sign]</c> standing for
/// <c>(-1)^sign * (hi + lo / 2^64) * 2^(ex - 63)</c>, the exponent stored as the bits of a <c>long</c>.
/// No operation writes to its operands. See <c>THIRD-PARTY-NOTICE.CORE-MATH.Logarithm.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>The logarithm of a positive number of 128 bits, from a table of 240 reciprocals and a polynomial of degree 13.</summary>
  private static ulong[] LogDintLog(ulong[] x) {
    var exponent = (long)x[2];
    var i = (int)(x[0] >> 55);
    if (x[0] > 0xb504f333f9de6484UL) {
      exponent++;
      i >>= 1;
    }

    // z = x / 2^exponent * inverse - 1
    var scaled = LogDint(x[0], x[1], (long)x[2] - exponent, x[3]);
    var z = LogDintMultiply(scaled, LogDintAccurateInverse(i - 128));
    z = LogDintAdd(LogDint(0x8000000000000000UL, 0, 0, 1), z);

    // exponent * log(2) + log(1 / inverse) + log(1 + z)
    var r = LogDintMultiplyInteger(exponent, LogDint(0xb17217f7d1cf79abUL, 0xc9e3b39803f2f6afUL, -1, 0));
    var p = LogDintAccuratePolynomial(0);
    for (var k = 1; k <= 12; k++)
      p = LogDintAdd(LogDintAccuratePolynomial(k), LogDintMultiply(z, p));

    p = LogDintMultiply(z, p);
    p = LogDintAdd(LogDintAccurateInverseLog(i - 128), p);
    return LogDintAdd(p, r);
  }

  /// <summary>A number of 128 bits from its parts.</summary>
  private static ulong[] LogDint(ulong hi, ulong lo, long ex, ulong sign) => [hi, lo, (ulong)ex, sign];

  /// <summary>A positive double as a number of 128 bits.</summary>
  private static ulong[] LogDintFromDouble(double b) {
    var bits = (ulong)BitConverter.DoubleToInt64Bits(b);
    var e = (long)((bits >> 52) & 0x7ff);
    var m = (bits & 0xfffffffffffffUL) + (e != 0 ? 1UL << 52 : 0);
    var t = System.Numerics.BitOperations.LeadingZeroCount(m);
    return LogDint(m << t, 0, e - 0x3ff - (t > 11 ? t - 12 : 0), 0);
  }

  /// <summary>
  /// A number of 128 bits rounded to a double in the normal range. No result of the logarithms lies
  /// halfway or exactly on a double, so the bits after the round bit always count as nonzero.
  /// </summary>
  private static double LogDintToDouble(ulong[] a) {
    var r = BitConverter.Int64BitsToDouble((long)(a[0] >> 11 | 0x3ffUL << 52 | a[3] << 63));
    var rd = 5.551115123125783E-17; // 0x1p-54
    if (((a[0] >> 10) & 1) != 0)
      rd += 1.1102230246251565E-16; // 0x1p-53

    r += a[3] == 0 ? rd : -rd;
    var e = BitConverter.Int64BitsToDouble((long)((((ulong)((long)a[2] + 1023)) & 0x7ff) << 52));
    return r * e;
  }

  /// <summary>Compares the magnitudes: the exponents, then the significands.</summary>
  private static int LogDintCompare(ulong[] a, ulong[] b) {
    var ae = (long)a[2];
    var be = (long)b[2];
    if (ae != be)
      return ae > be ? 1 : -1;

    if (a[0] != b[0])
      return a[0] > b[0] ? 1 : -1;

    return a[1] > b[1] ? 1 : a[1] < b[1] ? -1 : 0;
  }

  /// <summary>The sum, with the smaller operand rounded to nearest where it is aligned.</summary>
  private static ulong[] LogDintAdd(ulong[] a, ulong[] b) {
    if ((a[0] | a[1]) == 0)
      return b;

    if ((b[0] | b[1]) == 0)
      return a;

    var order = LogDintCompare(a, b);
    if (order == 0)
      return (a[3] ^ b[3]) != 0 ? LogDint(0, 0, 0, 0) : LogDint(a[0], a[1], (long)a[2] + 1, a[3]);

    if (order < 0)
      return LogDintAdd(b, a);

    // From here on |a| > |b|: align b to a, rounding to nearest.
    var ah = a[0];
    var al = a[1];
    var bh = b[0];
    var bl = b[1];
    var exponent = (long)a[2];
    if ((long)a[2] > (long)b[2]) {
      var sh = (int)((long)a[2] - (long)b[2]);
      if (sh <= 128) {
        var k = sh - 1;
        var bit = k < 64 ? (bl >> k) & 1 : (bh >> (k - 64)) & 1;
        var sum = bl + bit;
        bh += LogCarry(bl, bit, sum);
        bl = sum;
      }

      if (sh < 64) {
        bl = bl >> sh | bh << (64 - sh);
        bh >>= sh;
      } else if (sh < 128) {
        bl = bh >> (sh - 64);
        bh = 0;
      } else {
        bl = 0;
        bh = 0;
      }
    }

    ulong ch;
    ulong cl;
    if ((a[3] ^ b[3]) != 0) {
      cl = al - bl;
      ch = ah - bh - LogBorrow(al, bl, cl);
    } else {
      cl = al + bl;
      ch = ah + bh + LogCarry(al, bl, cl);
      if (LogCarry(ah, bh, ch) != 0) {
        // Round the bit shifted out to nearest and put the carry on top.
        var bit = cl & 1;
        var sum = cl + bit;
        ch += LogCarry(cl, bit, sum);
        cl = sum;
        cl = cl >> 1 | ch << 63;
        ch = 1UL << 63 | ch >> 1;
        exponent++;
      }
    }

    // Normalize: the sum is never zero, as |a| > |b|.
    int shift;
    if (ch != 0) {
      shift = System.Numerics.BitOperations.LeadingZeroCount(ch);
      ch = ch << shift | cl >> (63 - shift) >> 1;
      cl <<= shift;
    } else {
      shift = 64 + System.Numerics.BitOperations.LeadingZeroCount(cl);
      ch = cl << (shift - 64);
      cl = 0;
    }

    return LogDint(ch, cl, exponent - shift, a[3]);
  }

  /// <summary>The product to 126 bits: the product of the two low halves is left out.</summary>
  private static ulong[] LogDintMultiply(ulong[] a, ulong[] b) {
    var th = MultiplyHigh(a[0], b[0]);
    var tl = unchecked(a[0] * b[0]);
    var m1h = MultiplyHigh(a[0], b[1]);
    var m1l = unchecked(a[0] * b[1]);
    var m2h = MultiplyHigh(a[1], b[0]);
    var m2l = unchecked(a[1] * b[0]);

    // m = m1 + m2, whose carry goes into the high half of t; then t += m >> 64.
    var ml = m1l + m2l;
    var mh = m1h + m2h + LogCarry(m1l, m2l, ml);
    th += LogCarry(m1h, m2h, mh);
    var sum = tl + mh;
    th += LogCarry(tl, mh, sum);
    tl = sum;

    // The top bit of hi set.
    var ex = (int)(1 - (th >> 63));
    if (ex != 0) {
      th = th << 1 | tl >> 63;
      tl <<= 1;
    }

    var round = ml >> 63;
    sum = tl + round;
    th += LogCarry(tl, round, sum);
    tl = sum;
    return LogDint(th, tl, (long)a[2] + (long)b[2] - ex + 1, a[3] ^ b[3]);
  }

  /// <summary>The product with an integer of at most 63 bits.</summary>
  private static ulong[] LogDintMultiplyInteger(long b, ulong[] a) {
    if (b == 0)
      return LogDint(0, 0, 0, 0);

    var c = b < 0 ? (ulong)(-b) : (ulong)b;
    var sign = b < 0 ? a[3] ^ 1 : a[3];
    var th = MultiplyHigh(a[0], c);
    var tl = unchecked(a[0] * c);

    // Normalize t, then add the part of a.lo * c it reaches.
    int m;
    if (th == 0) {
      m = 64;
      th = tl;
      tl = 0;
    } else {
      m = System.Numerics.BitOperations.LeadingZeroCount(th);
      th = th << m | tl >> (63 - m) >> 1;
      tl <<= m;
    }

    var lh = MultiplyHigh(a[1], c);
    var ll = unchecked(a[1] * c);
    var k = m - 1;
    var sh = lh << k | ll >> (63 - k) >> 1;
    var sl = ll << k;
    ll = sl >> 63 | sh << 1;
    lh = sh >> 63;

    var rl = ll + tl;
    var rh = lh + th + LogCarry(ll, tl, rl);
    if (LogCarry(lh, th, rh) != 0) {
      var bit = rl & 1;
      var sum = rl + bit;
      rh += LogCarry(rl, bit, sum);
      rl = sum;
      rl = rl >> 1 | rh << 63;
      rh = 1UL << 63 | rh >> 1;
      m--;
    }

    return LogDint(rh, rl, (long)a[2] + 64 - m, sign);
  }

  /// <summary>The carry out of the sum <c>s = a + b</c> (+ a carry in), as 0 or 1.</summary>
  private static ulong LogCarry(ulong a, ulong b, ulong s) => ((a & b) | ((a | b) & ~s)) >> 63;

  /// <summary>The borrow out of the difference <c>d = a - b</c>, as 0 or 1.</summary>
  private static ulong LogBorrow(ulong a, ulong b, ulong d) => ((~a & b) | (~(a ^ b) & d)) >> 63;

  /// <summary>An entry of the reciprocals 1 / (1 + i / 2^8).</summary>
  private static ulong[] LogDintAccurateInverse(int k)
    => LogDint(_logAccurateInverseBits[2 * k], _logAccurateInverseBits[2 * k + 1], _logAccurateInverseScale[2 * k], (ulong)_logAccurateInverseScale[2 * k + 1]);

  /// <summary>An entry of the logarithms -log(1 / (1 + i / 2^8)).</summary>
  private static ulong[] LogDintAccurateInverseLog(int k)
    => LogDint(_logAccurateInverseLogBits[2 * k], _logAccurateInverseLogBits[2 * k + 1], _logAccurateInverseLogScale[2 * k], (ulong)_logAccurateInverseLogScale[2 * k + 1]);

  /// <summary>A coefficient of the polynomial for log(1 + z), the highest degree first.</summary>
  private static ulong[] LogDintAccuratePolynomial(int k)
    => LogDint(_logAccuratePolynomialBits[2 * k], _logAccuratePolynomialBits[2 * k + 1], _logAccuratePolynomialScale[2 * k], (ulong)_logAccuratePolynomialScale[2 * k + 1]);

  // binary64/log/dint.h _INVERSE_2: 240 entries
  private static readonly ulong[] _logAccurateInverseBits = [
    0x8000000000000000, 0x0000000000000000, 0xfe03f80fe03f80ff, 0x0000000000000000,
    0xfc0fc0fc0fc0fc10, 0x0000000000000000, 0xfa232cf252138ac0, 0x0000000000000000,
    0xf83e0f83e0f83e10, 0x0000000000000000, 0xf6603d980f6603da, 0x0000000000000000,
    0xf4898d5f85bb3951, 0x0000000000000000, 0xf2b9d6480f2b9d65, 0x0000000000000000,
    0xf0f0f0f0f0f0f0f1, 0x0000000000000000, 0xef2eb71fc4345239, 0x0000000000000000,
    0xed7303b5cc0ed731, 0x0000000000000000, 0xebbdb2a5c1619c8c, 0x0000000000000000,
    0xea0ea0ea0ea0ea0f, 0x0000000000000000, 0xe865ac7b7603a197, 0x0000000000000000,
    0xe6c2b4481cd8568a, 0x0000000000000000, 0xe525982af70c880f, 0x0000000000000000,
    0xe38e38e38e38e38f, 0x0000000000000000, 0xe1fc780e1fc780e2, 0x0000000000000000,
    0xe070381c0e070382, 0x0000000000000000, 0xdee95c4ca037ba58, 0x0000000000000000,
    0xdd67c8a60dd67c8b, 0x0000000000000000, 0xdbeb61eed19c5958, 0x0000000000000000,
    0xda740da740da740e, 0x0000000000000000, 0xd901b2036406c80e, 0x0000000000000000,
    0xd79435e50d79435f, 0x0000000000000000, 0xd62b80d62b80d62c, 0x0000000000000000,
    0xd4c77b03531dec0e, 0x0000000000000000, 0xd3680d3680d3680e, 0x0000000000000000,
    0xd20d20d20d20d20e, 0x0000000000000000, 0xd0b69fcbd2580d0c, 0x0000000000000000,
    0xcf6474a8819ec8ea, 0x0000000000000000, 0xce168a7725080ce2, 0x0000000000000000,
    0xcccccccccccccccd, 0x0000000000000000, 0xcb8727c065c393e1, 0x0000000000000000,
    0xca4587e6b74f032a, 0x0000000000000000, 0xc907da4e871146ad, 0x0000000000000000,
    0xc7ce0c7ce0c7ce0d, 0x0000000000000000, 0xc6980c6980c6980d, 0x0000000000000000,
    0xc565c87b5f9d4d1c, 0x0000000000000000, 0xc4372f855d824ca6, 0x0000000000000000,
    0xc30c30c30c30c30d, 0x0000000000000000, 0xc1e4bbd595f6e948, 0x0000000000000000,
    0xc0c0c0c0c0c0c0c1, 0x0000000000000000, 0xbfa02fe80bfa02ff, 0x0000000000000000,
    0xbe82fa0be82fa0bf, 0x0000000000000000, 0xbd69104707661aa3, 0x0000000000000000,
    0xbc52640bc52640bd, 0x0000000000000000, 0xbb3ee721a54d880c, 0x0000000000000000,
    0xba2e8ba2e8ba2e8c, 0x0000000000000000, 0xb92143fa36f5e02f, 0x0000000000000000,
    0xb81702e05c0b8171, 0x0000000000000000, 0xb70fbb5a19be3659, 0x0000000000000000,
    0xb60b60b60b60b60c, 0x0000000000000000, 0xb509e68a9b948220, 0x0000000000000000,
    0xb40b40b40b40b40c, 0x0000000000000000, 0xb30f63528917c80c, 0x0000000000000000,
    0xb21642c8590b2165, 0x0000000000000000, 0xb11fd3b80b11fd3c, 0x0000000000000000,
    0xb02c0b02c0b02c0c, 0x0000000000000000, 0xaf3addc680af3ade, 0x0000000000000000,
    0xae4c415c9882b932, 0x0000000000000000, 0xad602b580ad602b6, 0x0000000000000000,
    0xac7691840ac76919, 0x0000000000000000, 0xab8f69e28359cd12, 0x0000000000000000,
    0xaaaaaaaaaaaaaaab, 0x0000000000000000, 0xa9c84a47a07f5638, 0x0000000000000000,
    0xa8e83f5717c0a8e9, 0x0000000000000000, 0xa80a80a80a80a80b, 0x0000000000000000,
    0xa72f05397829cbc2, 0x0000000000000000, 0xa655c4392d7b73a8, 0x0000000000000000,
    0xa57eb50295fad40b, 0x0000000000000000, 0xa4a9cf1d96833752, 0x0000000000000000,
    0xa3d70a3d70a3d70b, 0x0000000000000000, 0xa3065e3fae7cd0e1, 0x0000000000000000,
    0xa237c32b16cfd773, 0x0000000000000000, 0xa16b312ea8fc377d, 0x0000000000000000,
    0xa0a0a0a0a0a0a0a1, 0x0000000000000000, 0x9fd809fd809fd80a, 0x0000000000000000,
    0x9f1165e7254813e3, 0x0000000000000000, 0x9e4cad23dd5f3a21, 0x0000000000000000,
    0x9d89d89d89d89d8a, 0x0000000000000000, 0x9cc8e160c3fb19b9, 0x0000000000000000,
    0x9c09c09c09c09c0a, 0x0000000000000000, 0x9b4c6f9ef03a3caa, 0x0000000000000000,
    0x9a90e7d95bc609aa, 0x0000000000000000, 0x99d722dabde58f07, 0x0000000000000000,
    0x991f1a515885fb38, 0x0000000000000000, 0x9868c809868c8099, 0x0000000000000000,
    0x97b425ed097b425f, 0x0000000000000000, 0x97012e025c04b80a, 0x0000000000000000,
    0x964fda6c0964fda7, 0x0000000000000000, 0x95a02568095a0257, 0x0000000000000000,
    0x94f2094f2094f20a, 0x0000000000000000, 0x9445809445809446, 0x0000000000000000,
    0x939a85c40939a85d, 0x0000000000000000, 0x92f113840497889d, 0x0000000000000000,
    0x924924924924924a, 0x0000000000000000, 0x91a2b3c4d5e6f80a, 0x0000000000000000,
    0x90fdbc090fdbc091, 0x0000000000000000, 0x905a38633e06c43b, 0x0000000000000000,
    0x8fb823ee08fb823f, 0x0000000000000000, 0x8f1779d9fdc3a219, 0x0000000000000000,
    0x8e78356d1408e784, 0x0000000000000000, 0x8dda520237694809, 0x0000000000000000,
    0x8d3dcb08d3dcb08e, 0x0000000000000000, 0x8ca29c046514e024, 0x0000000000000000,
    0x8c08c08c08c08c09, 0x0000000000000000, 0x8b70344a139bc75b, 0x0000000000000000,
    0x8ad8f2fba9386823, 0x0000000000000000, 0x8a42f8705669db47, 0x0000000000000000,
    0x89ae4089ae4089af, 0x0000000000000000, 0x891ac73ae9819b51, 0x0000000000000000,
    0x8888888888888889, 0x0000000000000000, 0x87f78087f78087f8, 0x0000000000000000,
    0x8767ab5f34e47ef2, 0x0000000000000000, 0x86d905447a34acc7, 0x0000000000000000,
    0x864b8a7de6d1d609, 0x0000000000000000, 0x85bf37612cee3c9b, 0x0000000000000000,
    0x8534085340853409, 0x0000000000000000, 0x84a9f9c8084a9f9d, 0x0000000000000000,
    0x8421084210842109, 0x0000000000000000, 0x839930523fbe3368, 0x0000000000000000,
    0x83126e978d4fdf3c, 0x0000000000000000, 0x828cbfbeb9a020a4, 0x0000000000000000,
    0x8208208208208209, 0x0000000000000000, 0x81848da8faf0d278, 0x0000000000000000,
    0x8102040810204082, 0x0000000000000000, 0x8000000000000000, 0x0000000000000000,
    0x8000000000000000, 0x0000000000000000, 0xff00ff00ff00ff02, 0x0000000000000000,
    0xfe03f80fe03f80ff, 0x0000000000000000, 0xfd08e5500fd08e56, 0x0000000000000000,
    0xfc0fc0fc0fc0fc11, 0x0000000000000000, 0xfb18856506ddaba7, 0x0000000000000000,
    0xfa232cf252138ac1, 0x0000000000000000, 0xf92fb2211855a866, 0x0000000000000000,
    0xf83e0f83e0f83e11, 0x0000000000000000, 0xf74e3fc22c700f76, 0x0000000000000000,
    0xf6603d980f6603db, 0x0000000000000000, 0xf57403d5d00f5741, 0x0000000000000000,
    0xf4898d5f85bb3951, 0x0000000000000000, 0xf3a0d52cba872337, 0x0000000000000000,
    0xf2b9d6480f2b9d66, 0x0000000000000000, 0xf1d48bcee0d399fb, 0x0000000000000000,
    0xf0f0f0f0f0f0f0f2, 0x0000000000000000, 0xf00f00f00f00f010, 0x0000000000000000,
    0xef2eb71fc4345239, 0x0000000000000000, 0xee500ee500ee5010, 0x0000000000000000,
    0xed7303b5cc0ed731, 0x0000000000000000, 0xec979118f3fc4da3, 0x0000000000000000,
    0xebbdb2a5c1619c8d, 0x0000000000000000, 0xeae56403ab959010, 0x0000000000000000,
    0xea0ea0ea0ea0ea10, 0x0000000000000000, 0xe939651fe2d8d35d, 0x0000000000000000,
    0xe865ac7b7603a198, 0x0000000000000000, 0xe79372e225fe30da, 0x0000000000000000,
    0xe6c2b4481cd8568a, 0x0000000000000000, 0xe5f36cb00e5f36cc, 0x0000000000000000,
    0xe525982af70c880f, 0x0000000000000000, 0xe45932d7dc52100f, 0x0000000000000000,
    0xe38e38e38e38e38f, 0x0000000000000000, 0xe2c4a6886a4c2e11, 0x0000000000000000,
    0xe1fc780e1fc780e3, 0x0000000000000000, 0xe135a9c97500e137, 0x0000000000000000,
    0xe070381c0e070383, 0x0000000000000000, 0xdfac1f74346c5760, 0x0000000000000000,
    0xdee95c4ca037ba58, 0x0000000000000000, 0xde27eb2c41f3d9d2, 0x0000000000000000,
    0xdd67c8a60dd67c8b, 0x0000000000000000, 0xdca8f158c7f91ab9, 0x0000000000000000,
    0xdbeb61eed19c5959, 0x0000000000000000, 0xdb2f171df770291a, 0x0000000000000000,
    0xda740da740da740f, 0x0000000000000000, 0xd9ba4256c0366e92, 0x0000000000000000,
    0xd901b2036406c80f, 0x0000000000000000, 0xd84a598ec9151f44, 0x0000000000000000,
    0xd79435e50d79435f, 0x0000000000000000, 0xd6df43fca482f00e, 0x0000000000000000,
    0xd62b80d62b80d62d, 0x0000000000000000, 0xd578e97c3f5fe552, 0x0000000000000000,
    0xd4c77b03531dec0e, 0x0000000000000000, 0xd4173289870ac52f, 0x0000000000000000,
    0xd3680d3680d3680e, 0x0000000000000000, 0xd2ba083b445250ac, 0x0000000000000000,
    0xd20d20d20d20d20e, 0x0000000000000000, 0xd161543e28e50275, 0x0000000000000000,
    0xd0b69fcbd2580d0c, 0x0000000000000000, 0xd00d00d00d00d00e, 0x0000000000000000,
    0xcf6474a8819ec8ea, 0x0000000000000000, 0xcebcf8bb5b4169cc, 0x0000000000000000,
    0xce168a7725080ce2, 0x0000000000000000, 0xcd712752a886d243, 0x0000000000000000,
    0xccccccccccccccce, 0x0000000000000000, 0xcc29786c7607f9a0, 0x0000000000000000,
    0xcb8727c065c393e1, 0x0000000000000000, 0xcae5d85f1bbd6c96, 0x0000000000000000,
    0xca4587e6b74f032a, 0x0000000000000000, 0xc9a633fcd967300e, 0x0000000000000000,
    0xc907da4e871146ae, 0x0000000000000000, 0xc86a78900c86a78a, 0x0000000000000000,
    0xc7ce0c7ce0c7ce0d, 0x0000000000000000, 0xc73293d789b9f839, 0x0000000000000000,
    0xc6980c6980c6980d, 0x0000000000000000, 0xc5fe740317f9d00d, 0x0000000000000000,
    0xc565c87b5f9d4d1d, 0x0000000000000000, 0xc4ce07b00c4ce07c, 0x0000000000000000,
    0xc4372f855d824ca7, 0x0000000000000000, 0xc3a13de60495c774, 0x0000000000000000,
    0xc30c30c30c30c30d, 0x0000000000000000, 0xc2780613c0309e03, 0x0000000000000000,
    0xc1e4bbd595f6e948, 0x0000000000000000, 0xc152500c152500c2, 0x0000000000000000,
    0xc0c0c0c0c0c0c0c2, 0x0000000000000000, 0xc0300c0300c0300d, 0x0000000000000000,
    0xbfa02fe80bfa0300, 0x0000000000000000, 0xbf112a8ad278e8de, 0x0000000000000000,
    0xbe82fa0be82fa0c0, 0x0000000000000000, 0xbdf59c91700bdf5b, 0x0000000000000000,
    0xbd69104707661aa4, 0x0000000000000000, 0xbcdd535db1cc5b7c, 0x0000000000000000,
    0xbc52640bc52640bd, 0x0000000000000000, 0xbbc8408cd63069a2, 0x0000000000000000,
    0xbb3ee721a54d880d, 0x0000000000000000, 0xbab656100bab6562, 0x0000000000000000,
    0xba2e8ba2e8ba2e8d, 0x0000000000000000, 0xb9a7862a0ff46589, 0x0000000000000000,
    0xb92143fa36f5e02f, 0x0000000000000000, 0xb89bc36ce3e0453b, 0x0000000000000000,
    0xb81702e05c0b8171, 0x0000000000000000, 0xb79300b79300b794, 0x0000000000000000,
    0xb70fbb5a19be365a, 0x0000000000000000, 0xb68d31340e4307d9, 0x0000000000000000,
    0xb60b60b60b60b60c, 0x0000000000000000, 0xb58a485518d1e7e5, 0x0000000000000000,
    0xb509e68a9b948220, 0x0000000000000000, 0xb48a39d44685fe98, 0x0000000000000000,
    0xb40b40b40b40b40c, 0x0000000000000000, 0xb38cf9b00b38cf9c, 0x0000000000000000,
    0xb30f63528917c80c, 0x0000000000000000, 0xb2927c29da5519d0, 0x0000000000000000
  ];

  private static readonly int[] _logAccurateInverseScale = [
    1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0,
    -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0,
    -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0,
    -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0,
    -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0,
    -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0,
    -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0,
    -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0,
    -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0,
    -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0,
    -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0,
    -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0,
    -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0,
    -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0, -1, 0
  ];

  // binary64/log/dint.h _LOG_INV_2: 240 entries
  private static readonly ulong[] _logAccurateInverseLogBits = [
    0xb17217f7d1cf79ab, 0xc9e3b39803f2f6af, 0xaf74155120c9011d, 0x046d235ee63073dc,
    0xad7a02e1b24efd32, 0x160864fd949b4bd3, 0xab83d135dc633301, 0xffe6607ba902ef3b,
    0xa991713433c2b999, 0x0ba4aea614d05700, 0xa7a2d41ad270c9d7, 0xcd362382a7688479,
    0xa5b7eb7cb860fb89, 0x7b6a62a0dec6e072, 0xa3d0a93f45169a4b, 0x09594fab088c0d64,
    0xa1ecff97c91e267b, 0x1b7efae08e597e16, 0xa00ce1092e5498c4, 0x69879c5a30cd1241,
    0x9e304061b5fda91a, 0x04603d87b6df81ac, 0x9c5710b8cbb73a42, 0xaa554b2dd4619e63,
    0x9a81456cec642e10, 0x4d49f9aaea3cb5e0, 0x98aed221a03458b6, 0x732f89321647b358,
    0x96dfaabd86fa1647, 0xd61188fbc94e2f14, 0x9513c36876083696, 0xb5cbc416a2418011,
    0x934b1089a6dc93c2, 0xbf5bb3b60554e151, 0x918586c5f5e4bf01, 0x9f92199ed1a4bab0,
    0x8fc31afe30b2c6de, 0xe300bf167e95da66, 0x8e03c24d7300395a, 0xcddae1ccce247837,
    0x8c47720791e53314, 0x762ad19415fe25a5, 0x8a8e1fb794b09134, 0x9eb628dba173c82d,
    0x88d7c11e3ad53cdc, 0x8a3111a707b6de2c, 0x87244c308e670a66, 0x85e005d06dbfa8f7,
    0x8573b71682a7d21b, 0xb21f9f89c1ab80b2, 0x83c5f8299e2b4091, 0xb8f6fafe8fbb68b8,
    0x821b05f3b01d6774, 0xdb0d58c3f7e2ea1e, 0x8072d72d903d588c, 0x7dd1b09c70c40109,
    0xfd9ac57bd2442180, 0xaf05924d258c14c4, 0xfa553f7018c966f4, 0x2780a545a1b54dce,
    0xf7150ab5a09f27f6, 0x0a470250d40ebe8e, 0xf3da161eed6b9ab1, 0x248d42f78d3e65d2,
    0xf0a450d139366ca7, 0x7c66eb6408ff6432, 0xed73aa4264b0adeb, 0x5391cf4b33e42996,
    0xea481236f7d35bb2, 0x39a767a80d6d97e6, 0xe72178c0323a1a0f, 0xcc4e1653e71d9973,
    0xe3ffce3a2aa64923, 0x8eadb651b49ac539, 0xe0e30349fd1cec82, 0x03e8e1802aba24d5,
    0xddcb08dc0717d85c, 0x940a666c87842842, 0xdab7d02231484a93, 0xbec20cca6efe2ac4,
    0xd7a94a92466e833c, 0xcd88bba7d0cee8df, 0xd49f69e456cf1b7b, 0x7f53bd2e406e66e6,
    0xd19a201127d3c646, 0x279d79f51dcc7301, 0xce995f50af69d863, 0x432f3f4f861ad6a8,
    0xcb9d1a189ab56e77, 0x7d7e9307c70c0667, 0xc8a5431adfb44ca6, 0x048ce7c1a75e341a,
    0xc5b1cd44596fa51f, 0xf218fb8f9f9ef27f, 0xc2c2abbb6e5fd570, 0x03337789d592e296,
    0xbfd7d1dec0a8df70, 0x37eda996244bccaf, 0xbcf13343e7d9ec7f, 0x2afd17781bb3afea,
    0xba0ec3b633dd8b0b, 0x91dc60b2b059a609, 0xb730773578cb90b3, 0xaa1116c3466beb6c,
    0xb45641f4e350a0d4, 0xe756eba00bc33976, 0xb1801859d56249de, 0x98ce51fff99479cb,
    0xaeadeefacaf97d37, 0x9dd6e688ebb13b01, 0xabdfba9e468fd6f9, 0x472ea07749ce6bd1,
    0xa9157039c51ebe72, 0xe164c759686a2207, 0xa64f04f0b961df78, 0x54f5275c2d15c21e,
    0xa38c6e138e20d834, 0xd698298adddd7f30, 0xa0cda11eaf46390e, 0x632438273918db7d,
    0x9e1293b9998c1dad, 0x3b035eae273a855c, 0x9b5b3bb5f088b768, 0x5078bbe3d392be24,
    0x98a78f0e9ae71d87, 0x64dec34784707838, 0x95f783e6e49a9cfc, 0x025004f3ef063312,
    0x934b1089a6dc93c2, 0xdf5bb3b60554e151, 0x90a22b6875c6a1f8, 0x8e91aeba609c8876,
    0x8dfccb1ad35ca6ef, 0x9947bdb6ddcaf59a, 0x8b5ae65d67db9acf, 0x7ba5168126a58b99,
    0x88bc74113f23def3, 0xbc5a0fe396f40f1c, 0x86216b3b0b17188c, 0x363ceae88f720f1d,
    0x8389c3026ac3139d, 0x6adda9d2270fa1f3, 0x80f572b1363487bc, 0xedbd0b5b3479d5f2,
    0xfcc8e3659d9bcbf1, 0x8a0cdf301431b60b, 0xf7ad6f26e7ff2efc, 0x9cd2238f75f969ad,
    0xf29877ff38809097, 0x2b020fa1820c948d, 0xed89ed86a44a01ab, 0x09d49f96cb88317a,
    0xe881bf932af3dac3, 0x2524848e3443e03f, 0xe37fde37807b84e3, 0x5e9a750b6b68781c,
    0xde8439c1dec5687c, 0x9d57da945b5d0aa6, 0xd98ec2bade71e53e, 0xd0a98f2ad65bee96,
    0xd49f69e456cf1b7a, 0x5f53bd2e406e66e7, 0xcfb6203844b3209b, 0x18cb02f33f79c16b,
    0xcad2d6e7b80bf915, 0xcc507fb7a3d0bf69, 0xc5f57f59c7f46156, 0x9a8b6997a402bf30,
    0xc11e0b2a8d1e0de1, 0xda631e830fd308fe, 0xbc4c6c2a226399f6, 0x276ebcfb2016a433,
    0xb780945bab55dcea, 0xb4c7bc3d32750fd9, 0xb2ba75f46099cf8f, 0x243c2e77904afa76,
    0xadfa035aa1ed8fdd, 0x549767e410316d2b, 0xa93f2f250dac67d5, 0x9ad2fb8d48054add,
    0xa489ec199dab06f4, 0x59fb6cf0ecb411b7, 0x9fda2d2cc9465c52, 0x6b2b9565f5355180,
    0x9b2fe580ac80b182, 0x011a5b944aca8705, 0x968b08643409ceb9, 0xd5c0da506a088482,
    0x91eb89524e100d28, 0xbfd3df5c52d67e77, 0x8d515bf11fb94f22, 0xa0713268840cbcbb,
    0x88bc74113f23def7, 0x9c5a0fe396f40f19, 0x842cc5acf1d0344b, 0x6fecdfa819b96092,
    0xff4489cedeab2ca6, 0xe17bd40d8d9291ec, 0xf639cc185088fe62, 0x5066e87f2c0f733d,
    0xed393b1c22351281, 0xff4e2e660317d55f, 0xe442c00de2591b4c, 0xe96ab34ce0bccd10,
    0xdb56446d6ad8df09, 0x28112e35a60e636f, 0xd273b2058de1bd4b, 0x36bbf837b4d320c6,
    0xc99af2eaca4c457b, 0xeaf51f66692844b2, 0xc0cbf17a071f80e9, 0x396ffdf76a147cc2,
    0xb8069857560707a7, 0x0a677b4c8bec22e0, 0xaf4ad26cbc8e5bef, 0x9e8b8b88a14ff0c9,
    0xa6988ae903f562f1, 0x7e858f08597b3a68, 0x9defad3e8f732186, 0x476d3b5b45f6ca02,
    0x9550252238bd2468, 0x658e5a0b811c596d, 0x8cb9de8a32ab3694, 0x97c9859530a4514c,
    0x842cc5acf1d0344c, 0x1fecdfa819b96094, 0xf7518e0035c3dd92, 0x606d89093278a931,
    0xe65b9e6eed965c4f, 0x609f5fe2058d5ff2, 0xd5779687d887e0ee, 0x49dda17056e45ebb,
    0xc4a550a4fd9a19bb, 0x3e97660a23cc5402, 0xb3e4a796a5dac213, 0x07cca0bcc06c2f8e,
    0xa33576a16f1f4c79, 0x121016bd904dc95a, 0x9297997c68c1f4e6, 0x610db3d4dd423bc9,
    0x820aec4f3a222397, 0xb9e3aea6c444eef6, 0xe31e9760a5578c6d, 0xf9eb2f284f31c35a,
    0xc24929464655f482, 0xda5f3cc0b3251da6, 0xa195492cc0660519, 0x4a18dff7cdb4ae33,
    0x8102b2c49ac23a86, 0x91d082dce3ddcd08, 0xc122451c45155150, 0xb16137f09a002b0e,
    0x8080abac46f389c4, 0x662d417ced0079c9, 0x0000000000000000, 0x0000000000000000,
    0x0000000000000000, 0x0000000000000000, 0xff805515885e014e, 0x435ab4da6a5bb50f,
    0xff015358833c4762, 0xbb481c8ee1416999, 0xbee23afc0853b6a8, 0xa89782c20df350c2,
    0xfe054587e01f1e2b, 0xf6d3a69bd5eab72f, 0x9e75221a352ba751, 0x452b7ea62f2198ea,
    0xbdc8d83ead88d518, 0x7faa638b5e00ee90, 0xdcfe013d7c8cbfc5, 0x632dbac46f30d009,
    0xfc14d873c1980236, 0xc7e09e3de453f5fc, 0x8d86cc491ecbfe03, 0xf1776453b7e82558,
    0x9cf43dcff5eafd2f, 0x2ad90155c8a7236a, 0xac52dd7e4726a456, 0xa47a963a91bb3018,
    0xbba2c7b196e7e224, 0xe7950f7252c163cf, 0xcae41876471f5bde, 0x91d00a417e330f8e,
    0xda16eb88cb8df5fb, 0x28a63ecfb66e94c0, 0xe93b5c56d85a9083, 0xce2992bfea38e76b,
    0xf85186008b1532f9, 0xe64b8b7759978998, 0x83acc1acc7238978, 0x5a5333c45b7f442e,
    0x8b29b7751bd7073b, 0x02e0b9ee992f2372, 0x929fb17850a0b7be, 0x5b4d3807660516a4,
    0x9a0ebcb0de8e848e, 0x2c1bb082689ba814, 0xa176e5f5323781d2, 0xdcf935996c92e8d4,
    0xa8d839f830c1fb40, 0x4c7343517c8ac264, 0xb032c549ba861d83, 0x774e27bc92ce3373,
    0xb78694572b5a5cd3, 0x24cdcf68cdb2067c, 0xbed3b36bd8966419, 0x7c0644d7d9ed08b4,
    0xc61a2eb18cd907a1, 0xe5a1532f6d5a1ac1, 0xcd5a1231019d66d7, 0x761e3e7b171e44b2,
    0xd49369d256ab1b1f, 0x9e9154e1d5263cda, 0xdbc6415d876d0839, 0x3e33c0c9f8824f54,
    0xe2f2a47ade3a18a8, 0xa0bf7c0b0d8bb4ef, 0xea189eb3659aeaeb, 0x93b2a3b21f448259,
    0xf1383b7157972f48, 0x543fff0ff4f0aaf1, 0xf85186008b153302, 0x5e4b8b7759978993,
    0xff64898edf55d548, 0x428ccfc99271dffa, 0x8338a89652cb714a, 0xb247eb86498c2ce7,
    0x86bbf3e68472cb2f, 0x0b8bd20615747126, 0x8a3c2c233a156341, 0x9027c74fe0e6f64f,
    0x8db956a97b3d0143, 0xf023472cd739f9e1, 0x913378c852d65be6, 0x977e3013d10f7525,
    0x94aa97c0ffa91a5d, 0x4ee3880fb7d34429, 0x981eb8c723fe97f2, 0x1f1c134fb702d433,
    0x9b8fe100f47ba1d8, 0x04b62af189fcba0d, 0x9efe158766314e4f, 0x4d71827efe892fc8,
    0xa2695b665be8f338, 0x4eca87c3f0f06211, 0xa5d1b79cd2af2aca, 0x8837986ceabfbed6,
    0xa9372f1d0da1bd10, 0x580eb71e58cd36e5, 0xac99c6ccc1042e94, 0x3dd557528315838d,
    0xaff983853c9e9e40, 0x5f105039091dd7f5, 0xb3566a13956a86f4, 0x471b1e1574d9fd55,
    0xb6b07f38ce90e463, 0x7bb2e265d0de37e1, 0xba07c7aa01bd2648, 0x43f9d57b324bd05f,
    0xbd5c481086c848db, 0xbb596b5030403242, 0xc0ae050a1abf56ad, 0x2f7f8c5fa9c50d76,
    0xc3fd03290648847d, 0x30480bee4cbbd698, 0xc74946f4436a054e, 0xf4f5cb531201c0d3,
    0xca92d4e7a2b5a3ad, 0xc983a9c5c4b3b135, 0xcdd9b173efdc1aaa, 0x8863e007c184a1e7,
    0xd11de0ff15ab18c6, 0xd88d83d4cc613f21, 0xd45f67e44178c612, 0x5486e73c615158b4,
    0xd79e4a7405ff96c3, 0x1300c9be67ae5da0, 0xdada8cf47dad236d, 0xdffb833c3409ee7e,
    0xde1433a16c66b14c, 0xde744870f54f0f18, 0xe14b42ac60c60512, 0x4e38eb8092a01f06,
    0xe47fbe3cd4d10d5b, 0x2ec0f797fdcd125c, 0xe7b1aa704e2ee240, 0xb40faab6d2ad0841,
    0xeae10b5a7ddc8ad8, 0x806b2fc9a8038790, 0xee0de5055f63eb01, 0x90a33316df83ba5a,
    0xf1383b7157972f4a, 0xb43fff0ff4f0aaf1, 0xf460129552d2ff41, 0xe62e3201bb2bbdce,
    0xf7856e5ee2c9b28a, 0x76f2a1b84190a7dc, 0xfaa852b25bd9b833, 0xa6dbfa03186e0666,
    0xfdc8c36af1f15468, 0x0a3361bca696504a, 0x8073622d6a80e631, 0xe897009015316073,
    0x82012ca5a68206d5, 0x8fde85afdd2bc88a, 0x838dc2fe6ac868e7, 0x1a3fcbdef40100cb,
    0x851927139c871af8, 0x67bd00c38061c51f, 0x86a35abcd5ba5901, 0x5481c3cbd925ccd2,
    0x882c5fcd7256a8c1, 0x39055a6598e7c29e, 0x89b438149d4582f5, 0x34531dba493eb5a6,
    0x8b3ae55d5d30701a, 0xc63eab8837170480, 0x8cc0696ea11b7b36, 0x94361c9a28d38a6a,
    0x8e44c60b4ccfd7dc, 0x1473aa01c7778679, 0x8fc7fcf24517946a, 0x380cbe769f2c6793,
    0x914a0fde7bcb2d0e, 0xc429ed3aea197a60, 0x92cb0086fbb1cf75, 0xa29d47c50b1182d0,
    0x944ad09ef4351af1, 0xa49827e081cb16ba, 0x95c981d5c4e924ea, 0x45404f5aa577d6b4,
    0x974715d708e984dd, 0x6648d42840d9e6fb, 0x98c38e4aa20c27d2, 0x846767ec990d7333,
    0x9a3eecd4c3eaa6ae, 0xdb3a7f6e6087b947, 0x9bb93315fec2d790, 0x7f589fba0865790f,
    0x9d3262ab4a2f4e37, 0xa1ae6ba06846fae0, 0x9eaa7d2e0fb87c35, 0xff472bc6ce648a7d,
    0xa0218434353f1de4, 0xd493efa632530acc, 0xa197795027409daa, 0x1dd1d4a6df960357,
    0xa30c5e10e2f613e4, 0x9bd9bd99e39a20b3, 0xa4803402004e865c, 0x31cbe0e8824116cd,
    0xa5f2fcabbbc506d8, 0x68ca4fb7ec323d74, 0xa764b99300134d79, 0x0d04d10474301862,
    0xa8d56c396fc1684c, 0x01eb067d578c4756, 0xaa45161d6e93167b, 0x9b081cf72249f5b2,
    0xabb3b8ba2ad362a1, 0x1db6506cc17a01f5, 0xad215587a67f0cdf, 0xe890422cb86b7cb1,
    0xae8dedfac04e5282, 0xac707b8ffc22b3e8, 0xaff983853c9e9e3f, 0xc5105039091dd7f8,
    0xb1641795ce3ca978, 0xfaf915300e517393, 0xb2cdab981f0f940b, 0xc857c77dc1df600f,
    0xb43640f4d8a5761f, 0xf5f080a71c34b25d, 0xb59dd911aca1ec48, 0x1d2664cf09a0c1bf,
    0xb70475515d0f1c5e, 0x4c98c6b8be17818d, 0xb86a1713c491aeaa, 0xd37ee2872a6f1cd6
  ];

  private static readonly int[] _logAccurateInverseLogScale = [
    -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1,
    -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1,
    -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1,
    -1, 1, -1, 1, -1, 1, -1, 1, -2, 1, -2, 1, -2, 1, -2, 1,
    -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1,
    -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1,
    -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1,
    -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1,
    -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1, -2, 1,
    -3, 1, -3, 1, -3, 1, -3, 1, -3, 1, -3, 1, -3, 1, -3, 1,
    -3, 1, -3, 1, -3, 1, -3, 1, -3, 1, -3, 1, -3, 1, -3, 1,
    -3, 1, -3, 1, -3, 1, -3, 1, -3, 1, -3, 1, -3, 1, -3, 1,
    -3, 1, -3, 1, -4, 1, -4, 1, -4, 1, -4, 1, -4, 1, -4, 1,
    -4, 1, -4, 1, -4, 1, -4, 1, -4, 1, -4, 1, -4, 1, -4, 1,
    -4, 1, -5, 1, -5, 1, -5, 1, -5, 1, -5, 1, -5, 1, -5, 1,
    -5, 1, -6, 1, -6, 1, -6, 1, -6, 1, -7, 1, -7, 1, 127, 0,
    127, 0, -9, 0, -8, 0, -7, 0, -7, 0, -6, 0, -6, 0, -6, 0,
    -6, 0, -5, 0, -5, 0, -5, 0, -5, 0, -5, 0, -5, 0, -5, 0,
    -5, 0, -4, 0, -4, 0, -4, 0, -4, 0, -4, 0, -4, 0, -4, 0,
    -4, 0, -4, 0, -4, 0, -4, 0, -4, 0, -4, 0, -4, 0, -4, 0,
    -4, 0, -4, 0, -4, 0, -3, 0, -3, 0, -3, 0, -3, 0, -3, 0,
    -3, 0, -3, 0, -3, 0, -3, 0, -3, 0, -3, 0, -3, 0, -3, 0,
    -3, 0, -3, 0, -3, 0, -3, 0, -3, 0, -3, 0, -3, 0, -3, 0,
    -3, 0, -3, 0, -3, 0, -3, 0, -3, 0, -3, 0, -3, 0, -3, 0,
    -3, 0, -3, 0, -3, 0, -3, 0, -3, 0, -3, 0, -3, 0, -3, 0,
    -3, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0,
    -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0,
    -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0,
    -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0,
    -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0, -2, 0
  ];

  // binary64/log/dint.h P_2: 13 entries
  private static readonly ulong[] _logAccuratePolynomialBits = [
    0x99df88a0430813ca, 0xa1cffb6e966a70f6, 0xaaa02d43f696c3e4, 0x4dbe754667b6bc48,
    0xba2e7a1eaf856174, 0x70e5c5a5ebbe0226, 0xccccccb9ec017492, 0xf934e28d924e76d4,
    0xe38e38e3807cfa4b, 0xc976e6cbd22e203f, 0xfffffffffff924cc, 0x05b308e39fa7dfb5,
    0x924924924924911d, 0x862bc3d33abb3649, 0xaaaaaaaaaaaaaaaa, 0x6637fd4b19743eec,
    0xcccccccccccccccc, 0xccc2ca18b08fe343, 0xffffffffffffffff, 0xffffff2245823ae0,
    0xaaaaaaaaaaaaaaaa, 0xaaaaaaaaa5c48b54, 0xffffffffffffffff, 0xffffffffffffebd8,
    0x8000000000000000, 0x0000000000000000
  ];

  private static readonly int[] _logAccuratePolynomialScale = [
    -4, 0, -4, 1, -4, 0, -4, 1, -4, 0, -4, 1, -3, 0, -3, 1,
    -3, 0, -3, 1, -2, 0, -2, 1, 0, 0
  ];

}
