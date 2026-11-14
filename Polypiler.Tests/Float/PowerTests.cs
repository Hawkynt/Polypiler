using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// The power functions where the reference vectors do not reach: the hard-to-round inputs of
/// CORE-MATH's <c>pow.wc</c> and <c>powf.wc</c> that take the second and third phase, the exact and
/// midpoint powers, the rounding boundaries of <c>rootn</c>, and the rules of ECMA-262 that
/// <see cref="FloatOperations.PowEcma"/> adds to <see cref="FloatOperations.Pow"/>. Every expected
/// value was computed with GNU MPFR (<c>mpfr_pow</c>, <c>mpfr_rootn_si</c>, rounded to nearest with
/// the exponent range of the format).
/// </summary>
[TestFixture]
public sealed class PowerTests {

  /// <summary>
  /// x, y and x^y as bits: inputs of <c>pow.wc</c> that reach the second or the third phase, its
  /// exact and midpoint cases, its subnormal and overflowing results, and powers of two from 2^-1075
  /// to 2^1024.
  /// </summary>
  private static readonly string[] _pow = [
    "431c4269c893fd34 3fd4000000000000 40ed79ca618b9632",
    "3feae37ed1670326 3fef967df66a2020 3feaf2f1054bc4b4",
    "3fe619e4bd9e9323 3fd4d31ec6d44040 3fec5e8c40cb428a",
    "3fe0e991a7b1cbec 3ff0800000000000 3fe09424e4495841",
    "3fe11b01f3d05083 3ff0800000000000 3fe0c62174045eb0",
    "3fe1c496a0ae7945 3ff0800000000000 3fe171b9435b097b",
    "3fe3691896218b76 3ff0800000000000 3fe31c11ce8c79d0",
    "3fe42b91c59f5208 3ff0800000000000 3fe3e1a1768c7e4b",
    "400ac97287c10735 3fd4000000000000 3ff757716df953d8",
    "401c4269c893fd34 3fd4000000000000 3ffd79ca618b9632",
    "400524ebae943097 3fde000000000000 3ff93bd0cd47eb5f",
    "3920000000000000 4033000000000000 0000000000000000",
    "2990000000000000 4008000000000000 0000000000000001",
    "3ffbe5a1bcf1f434 3ffe000000000000 4006affb727c10d5",
    "1e60000000000001 4000000000000000 0000000000000001",
    "1e63988e1409212d 4000000000000000 0000000000000001",
    "1e63988e1409212e 4000000000000000 0000000000000001",
    "3657c6a1f29e2ce6 401c000000000000 0000000000000001",
    "3d249218c6f13ec6 4038000000000000 0000000000000007",
    "0290a57de9c16bc7 3ff1800000000000 0000000000000001",
    "113d3bd9fc32cc31 3ff7000000000000 0000000000000001",
    "114192accad3e889 3ff6000000000000 0000858795b49778",
    "27f2a9c215cbdc93 4006000000000000 0000000000061bc8",
    "3298000000000000 4014000000000000 000000000000007a",
    "32a4000000000000 4014000000000000 000000000000061a",
    "bff1000000000000 4000000000000000 3ff2100000000000",
    "b920000000000000 4033000000000000 8000000000000000",
    "bfe05314db73c549 4022000000000000 bf632b5c46978760",
    "bfe200ff43ef3e04 4022000000000000 bf77231723a3ad87",
    "bfe3a8cda58d3f9d 4022000000000000 bf898892688b3207",
    "b298000000000000 4014000000000000 800000000000007a",
    "3fe4501b8b480000 3ff8000000000000 3fe02f14f9f7b4d4",
    "3fe446e644820000 3ff8000000000000 3fe024150df22cb0",
    "3fe463d869480000 3ff8000000000000 3fe046b15c0e2eac",
    "3fe4308912820000 3ff8000000000000 3fe0096854a92e10",
    "3fe4332abb200000 3ff8000000000000 3fe00c8b123e33a0",
    "3fe435cc8fa20000 3ff8000000000000 3fe00fae3870900c",
    "3fefffffffffffff 0000000000000001 3ff0000000000000",
    "3ff0000000000001 0000000000000001 3ff0000000000000",
    "3fefffffffffffff 0020000000000000 3ff0000000000000",
    "2f3d460000000000 4010000000000000 000000000000002d",
    "1cb9a10000000000 3ffe000000000000 0000000000036bca",
    "4000000000000000 c090c80000000000 0000000000000001",
    "c000000000000000 c090c80000000000 0000000000000001",
    "4000000000000000 c090c40000000000 0000000000000002",
    "43f0000000000000 4030040000000000 7ff0000000000000",
    "43f0000000000000 40300c0000000000 7ff0000000000000",
    "0000000000000001 0000000000000000 3ff0000000000000",
    "0000000000000001 0000000000000001 3ff0000000000000",
    "0000000000000001 000fffffffffffff 3ff0000000000000",
    "0000000000000001 0010000000000000 3ff0000000000000",
    "0000000000000001 3ff0000000000000 0000000000000001",
    "0000000000000001 8010000000000000 3ff0000000000000",
    "000fffffffffffff 3ff0000000000000 000fffffffffffff",
    "0010000000000000 3ff0000000000000 0010000000000000",
    "0010000000000000 8010000000000000 3ff0000000000000",
    "7fefffffffffffff 3ff0000000000001 7ff0000000000000",
    "0000800000000000 bfefe811f28a186f 7ff0000000000000",
    "000000d500000001 0002000000000000 3ff0000000000000",
    "3ff0000000000001 3fe0000000000000 3ff0000000000000",
    "2519000000000000 403f800000000000 0000000000000000",
    "3fe0000000000000 4090c00000000000 0000000000000004",
    "3fe0000000000000 4090c80000000000 0000000000000001",
    "3fe0000000000000 c090000000000000 7ff0000000000000",
    "4000000000000000 c090ca0000000000 0000000000000001",
    "4000000000000000 40900000a7c5ac47 7ff0000000000000",
    "4000000000000000 0000100000000000 3ff0000000000000",
    "3fe0000000000000 0000100000000000 3ff0000000000000",
    "2b58000000000000 4008000000000000 022b000000000000",
    "2988000000000000 4008000000000000 0000000000000000",
    "2f24000000000000 4010000000000000 0000000000000001",
    "3c32000000000000 4031800000000000 0000000007dafc7d",
    "4000000000000000 4090000000000000 7ff0000000000000",
    "4000000000000000 c090cc0000000000 0000000000000000",
    "bfe0000000000000 4090cc0000000000 8000000000000000",
    "c000000000000000 c090cc0000000000 8000000000000000",
    "bfe0000000000000 4090c80000000000 0000000000000001",
    "bff45eb6ea7e51dd 4049800000000000 c10b3a4721905aef",
    "bfeb3f453f14a136 407f700000000000 b8a3fc1604a0c75c",
    "3c42000000000000 4031800000000000 00001637ed9b2613"
  ];

  /// <summary>x, y and x^y as bits of <c>float</c>: inputs of <c>powf.wc</c> and exact powers that take the accurate path.</summary>
  private static readonly string[] _powF = [
    "35cd0800 3ff00000 2d5af26b",
    "19cd0800 3ff00000 000000db",
    "7bd63180 3f800cf1 7bdd643b",
    "58ac1f65 3f800de6 58aeb185",
    "303b3eb9 3f801404 3038d8ac",
    "52053a80 3f801566 52077b6e",
    "23efc19f 3f806572 23d505ad",
    "40bbecad 3f806cf3 40bd08ae",
    "0a17cecc 3eff0000 24e3ca01",
    "27a7c936 35480000 3f7ffe63",
    "2f5fe937 3e420000 3c6f0129",
    "a7400000 40400000 8000000e",
    "00000002 bf000000 64800000",
    "00000008 bf000000 64000000",
    "00000020 bf000000 63800000",
    "807fffff bf800000 fe800001",
    "9ffffffe bf800000 df000001",
    "1f800000 c0000000 7f800000",
    "2f800000 c0800000 7f800000",
    "37800000 c1000000 7f800000",
    "40000000 43000000 7f800000",
    "40800000 42800000 7f800000",
    "41800000 42000000 7f800000",
    "32c80000 3f000000 39200000",
    "33440000 3fc00000 2d2b8000",
    "1ac00000 40000000 00000004",
    "9ac00000 40000000 00000004",
    "1f810080 3fc00000 0f818180",
    "20810080 3fc00000 11018180",
    "21810080 3fc00000 12818180",
    "23100000 40200000 0000007a",
    "23c80000 40200000 0000061a",
    "3f3c655b 42540000 33bbee9a",
    "4ca34000 401d0000 5fc7c367",
    "40000000 40400000 41000000",
    "3f000000 41200000 3a800000",
    "26c00000 40400000 00000002",
    "40800000 bfc00000 3e000000",
    "41100000 3fc00000 41d80000",
    "c0000000 40400000 c1000000",
    "00000200 bf000000 62800000",
    "00000100 bf400000 745744fd",
    "00000240 3fc00000 00000000",
    "3f000000 43160000 00000000",
    "40000000 c3160000 00000000",
    "3f000000 c3000000 7f800000"
  ];

  /// <summary>
  /// x, n and the root as bits: radicands whose root lies within 2^-62 of a midpoint of two doubles,
  /// so that the first phase cannot decide, for positive and negative degrees and radicands.
  /// </summary>
  private static readonly string[] _rootN = [
    "36f93e0408bcda42 7 3ea6fbd2ee54117a",
    "b6f93e0408bcda42 7 bea6fbd2ee54117a",
    "e9663c2328fba954 -3 b21cacefc0bf5bc5",
    "69663c2328fba954 -3 321cacefc0bf5bc5",
    "09db182fbc272051 4 3269cfecf8515cbf",
    "769ba0fc5ab33552 -4 3243bd2c7f0a884c",
    "69fbdadbfbbdae05 -17 3d75759b0b0c217f",
    "e9fbdadbfbbdae05 -17 bd75759b0b0c217f",
    "1ca09c66b317ac91 1023 3fe5d29a7641c07f",
    "9ca09c66b317ac91 1023 bfe5d29a7641c07f",
    "91af57e18abce892 5 b6afde18c401b7fc",
    "11af57e18abce892 5 36afde18c401b7fc",
    "313b9b1b25c757cf -2 47485c9e18bd4168",
    "7f846b3cffb322d5 2147483647 3ff00000582596e7",
    "ff846b3cffb322d5 2147483647 bff00000582596e7",
    "1301e447f34c8980 -1024 3ffa07292a75385e",
    "62a5105c4a221bab 17 41f976d0d6f83100",
    "e2a5105c4a221bab 17 c1f976d0d6f83100",
    "bb1aa8668283e54c 65537 bfeff94ef77301e6",
    "3b1aa8668283e54c 65537 3feff94ef77301e6",
    "55e3a0712dd497bf 100 4026d4d6b0045a62",
    "6446224ad815825a -2147483648 3fefffff9b3d3f84",
    "0ba4f95d5babd444 -65536 3ff024677771b168",
    "9f4e1afae7da0d0b 1000003 bfeffd0936d0960c",
    "1f4e1afae7da0d0b 1000003 3feffd0936d0960c"
  ];

  /// <summary>
  /// x, n and the root as bits of <c>float</c>: radicands whose root lies within 2^-63.5 of a
  /// midpoint of two floats, x = w^n for a midpoint w near 1 and n near 2^30.
  /// </summary>
  private static readonly string[] _rootNF = [
    "1d91adf6 1577213614 3f800000",
    "145f7ca0 2004759256 3f800000",
    "5d9a4ed0 -1401761551 3f800000",
    "dd9a4ed0 -1401761551 bf800000",
    "1a1544fb 1739203887 3f7fffff",
    "9a1544fb 1739203887 bf7fffff",
    "1fa86ab0 1479314499 3f800000",
    "9fa86ab0 1479314499 bf800000",
    "59f3d66e -1231049198 3f7fffff",
    "056b5a41 -1350384160 3f800001",
    "14c7921c 1985298910 3f800000",
    "25801e4f 1209393277 3f800000",
    "a5801e4f 1209393277 bf800000",
    "64743988 -1719523778 3f800000",
    "074c7bb4 -1306226694 3f800001",
    "1571e8a9 1955585141 3f800000",
    "9571e8a9 1955585141 bf800000",
    "19530826 1774101787 3f7fffff",
    "99530826 1774101787 bf7fffff",
    "1a11ef0d 1739962219 3f800000",
    "9a11ef0d 1739962219 bf800000",
    "1a486d6c 1729315425 3f800000",
    "9a486d6c 1729315425 bf800000",
    "1c24ec19 1642825164 3f7fffff"
  ];

  private static IEnumerable<TestCaseData> Cases(string[] rows) => rows.Select(row => new TestCaseData(row).SetArgDisplayNames(row));

  private static IEnumerable<TestCaseData> PowCases => Cases(_pow);

  private static IEnumerable<TestCaseData> PowFCases => Cases(_powF);

  private static IEnumerable<TestCaseData> RootNCases => Cases(_rootN);

  private static IEnumerable<TestCaseData> RootNFCases => Cases(_rootNF);

  private static string[] Fields(string row) => row.Split(' ');

  private static double Double(string bits) => BitConverter.Int64BitsToDouble(long.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

  private static float Single(string bits) => BitConverter.Int32BitsToSingle(int.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

  private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture);

  private static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture);

  [TestCaseSource(nameof(PowCases))]
  public void Pow_IsCorrectlyRounded_OnAHardCase(string row) {
    var fields = Fields(row);
    Assert.That(Bits(FloatOperations.Pow(Double(fields[0]), Double(fields[1]))), Is.EqualTo(fields[2]));
  }

  [TestCaseSource(nameof(PowFCases))]
  public void PowF_IsCorrectlyRounded_OnAHardCase(string row) {
    var fields = Fields(row);
    Assert.That(Bits(FloatOperations.PowF(Single(fields[0]), Single(fields[1]))), Is.EqualTo(fields[2]));
  }

  [TestCaseSource(nameof(RootNCases))]
  public void RootN_IsCorrectlyRounded_NearAMidpoint(string row) {
    var fields = Fields(row);
    var n = int.Parse(fields[1], CultureInfo.InvariantCulture);
    Assert.That(Bits(FloatOperations.RootN(Double(fields[0]), n)), Is.EqualTo(fields[2]));
  }

  [TestCaseSource(nameof(RootNFCases))]
  public void RootNF_IsCorrectlyRounded_NearAMidpoint(string row) {
    var fields = Fields(row);
    var n = int.Parse(fields[1], CultureInfo.InvariantCulture);
    Assert.That(Bits(FloatOperations.RootNF(Single(fields[0]), n)), Is.EqualTo(fields[2]));
  }

  [TestCase(0.0)]
  [TestCase(-8.0)]
  [TestCase(double.NaN)]
  [TestCase(double.PositiveInfinity)]
  public void RootN_OfDegreeZero_IsNaN(double x) {
    Assert.That(FloatOperations.RootN(x, 0), Is.NaN);
    Assert.That(FloatOperations.RootNF((float)x, 0), Is.NaN);
  }

  /// <summary>
  /// ECMA-262 §6.1.6.1.3 on every reference vector of <c>pow</c>: the result of
  /// <see cref="FloatOperations.Pow"/> except for a NaN exponent and for <c>|x| = 1</c> with an
  /// infinite exponent, which give NaN.
  /// </summary>
  [Test]
  public void PowEcma_IsPow_ExceptForTheNaNsOfEcmaScript() {
    var failures = new List<string>();
    foreach (var vector in FloatVectors.Of("pow", IrType.F64)) {
      var x = FloatVectors.Value(IrType.F64, vector.Operands[0]);
      var y = FloatVectors.Value(IrType.F64, vector.Operands[1]);
      var nan = double.IsNaN(y) || double.IsInfinity(y) && Math.Abs(x) == 1.0;
      var expected = nan ? FloatVectors.Bits(IrType.F64, double.NaN) : vector.Result;
      var actual = BitConverter.DoubleToInt64Bits(FloatOperations.PowEcma(x, y));
      if (!FloatVectors.Same(IrType.F64, expected, actual))
        failures.Add($"{vector}: expected {expected:x}, got {actual:x}");
    }

    Assert.That(failures, Is.Empty);
  }

  /// <summary>The rule of <see cref="FloatOperations.PowEcma"/> on every reference vector of <c>pow</c> of <c>float</c>.</summary>
  [Test]
  public void PowEcmaF_IsPowF_ExceptForTheNaNsOfEcmaScript() {
    var failures = new List<string>();
    foreach (var vector in FloatVectors.Of("pow", IrType.F32)) {
      var x = (float)FloatVectors.Value(IrType.F32, vector.Operands[0]);
      var y = (float)FloatVectors.Value(IrType.F32, vector.Operands[1]);
      var nan = float.IsNaN(y) || float.IsInfinity(y) && Math.Abs(x) == 1.0f;
      var expected = nan ? FloatVectors.Bits(IrType.F32, double.NaN) : vector.Result;
      var actual = BitConverter.SingleToInt32Bits(FloatOperations.PowEcmaF(x, y)) & 0xffffffffL;
      if (!FloatVectors.Same(IrType.F32, expected, actual))
        failures.Add($"{vector}: expected {expected:x}, got {actual:x}");
    }

    Assert.That(failures, Is.Empty);
  }

  [TestCase(1.0, double.NaN, double.NaN)]
  [TestCase(-1.0, double.PositiveInfinity, double.NaN)]
  [TestCase(1.0, double.NegativeInfinity, double.NaN)]
  [TestCase(double.NaN, 0.0, 1.0)]
  [TestCase(double.NaN, -0.0, 1.0)]
  [TestCase(2.0, double.PositiveInfinity, double.PositiveInfinity)]
  [TestCase(-8.0, 1.0 / 3.0, double.NaN)]
  public void PowEcma_FollowsEcmaScript(double x, double y, double expected) {
    Assert.That(FloatOperations.PowEcma(x, y), Is.EqualTo(expected));
    Assert.That(FloatOperations.PowEcmaF((float)x, (float)y), Is.EqualTo((float)expected));
  }

  [TestCase(1.0, double.NaN)]
  [TestCase(-1.0, double.PositiveInfinity)]
  [TestCase(1.0, double.NegativeInfinity)]
  public void Pow_KeepsTheRulesOfIeee754_WhereEcmaScriptDiffers(double x, double y) {
    Assert.That(FloatOperations.Pow(x, y), Is.EqualTo(1.0));
    Assert.That(FloatOperations.PowF((float)x, (float)y), Is.EqualTo(1.0f));
  }

}
