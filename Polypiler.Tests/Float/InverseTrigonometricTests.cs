using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// The arc sine, arc cosine and arc tangents on the inputs the reference vectors do not reach: the
/// signed zeros and infinities of <c>atan2</c>, and the hard-to-round cases of CORE-MATH's worst-case
/// files (<c>*.wc</c>) and exception tables that take the rare branches of the accurate paths. The
/// expected results are computed with MPFR 4.2 (precision 53 or 24, the exponent range of the format,
/// <c>mpfr_subnormalize</c>); the compiled implementation and the imported one in the interpreter must
/// both give them bit for bit.
/// </summary>
[TestFixture]
public sealed class InverseTrigonometricTests {

  // atan2(y, x) for y and x each of +0, -0, +1, -1, +Inf and -Inf.
  [TestCase(0x0000000000000000L, 0x0000000000000000L, 0x0000000000000000L)]
  [TestCase(0x0000000000000000L, unchecked((long)0x8000000000000000UL), 0x400921fb54442d18L)]
  [TestCase(0x0000000000000000L, 0x3ff0000000000000L, 0x0000000000000000L)]
  [TestCase(0x0000000000000000L, unchecked((long)0xbff0000000000000UL), 0x400921fb54442d18L)]
  [TestCase(0x0000000000000000L, 0x7ff0000000000000L, 0x0000000000000000L)]
  [TestCase(0x0000000000000000L, unchecked((long)0xfff0000000000000UL), 0x400921fb54442d18L)]
  [TestCase(unchecked((long)0x8000000000000000UL), 0x0000000000000000L, unchecked((long)0x8000000000000000UL))]
  [TestCase(unchecked((long)0x8000000000000000UL), unchecked((long)0x8000000000000000UL), unchecked((long)0xc00921fb54442d18UL))]
  [TestCase(unchecked((long)0x8000000000000000UL), 0x3ff0000000000000L, unchecked((long)0x8000000000000000UL))]
  [TestCase(unchecked((long)0x8000000000000000UL), unchecked((long)0xbff0000000000000UL), unchecked((long)0xc00921fb54442d18UL))]
  [TestCase(unchecked((long)0x8000000000000000UL), 0x7ff0000000000000L, unchecked((long)0x8000000000000000UL))]
  [TestCase(unchecked((long)0x8000000000000000UL), unchecked((long)0xfff0000000000000UL), unchecked((long)0xc00921fb54442d18UL))]
  [TestCase(0x3ff0000000000000L, 0x0000000000000000L, 0x3ff921fb54442d18L)]
  [TestCase(0x3ff0000000000000L, unchecked((long)0x8000000000000000UL), 0x3ff921fb54442d18L)]
  [TestCase(0x3ff0000000000000L, 0x3ff0000000000000L, 0x3fe921fb54442d18L)]
  [TestCase(0x3ff0000000000000L, unchecked((long)0xbff0000000000000UL), 0x4002d97c7f3321d2L)]
  [TestCase(0x3ff0000000000000L, 0x7ff0000000000000L, 0x0000000000000000L)]
  [TestCase(0x3ff0000000000000L, unchecked((long)0xfff0000000000000UL), 0x400921fb54442d18L)]
  [TestCase(unchecked((long)0xbff0000000000000UL), 0x0000000000000000L, unchecked((long)0xbff921fb54442d18UL))]
  [TestCase(unchecked((long)0xbff0000000000000UL), unchecked((long)0x8000000000000000UL), unchecked((long)0xbff921fb54442d18UL))]
  [TestCase(unchecked((long)0xbff0000000000000UL), 0x3ff0000000000000L, unchecked((long)0xbfe921fb54442d18UL))]
  [TestCase(unchecked((long)0xbff0000000000000UL), unchecked((long)0xbff0000000000000UL), unchecked((long)0xc002d97c7f3321d2UL))]
  [TestCase(unchecked((long)0xbff0000000000000UL), 0x7ff0000000000000L, unchecked((long)0x8000000000000000UL))]
  [TestCase(unchecked((long)0xbff0000000000000UL), unchecked((long)0xfff0000000000000UL), unchecked((long)0xc00921fb54442d18UL))]
  [TestCase(0x7ff0000000000000L, 0x0000000000000000L, 0x3ff921fb54442d18L)]
  [TestCase(0x7ff0000000000000L, unchecked((long)0x8000000000000000UL), 0x3ff921fb54442d18L)]
  [TestCase(0x7ff0000000000000L, 0x3ff0000000000000L, 0x3ff921fb54442d18L)]
  [TestCase(0x7ff0000000000000L, unchecked((long)0xbff0000000000000UL), 0x3ff921fb54442d18L)]
  [TestCase(0x7ff0000000000000L, 0x7ff0000000000000L, 0x3fe921fb54442d18L)]
  [TestCase(0x7ff0000000000000L, unchecked((long)0xfff0000000000000UL), 0x4002d97c7f3321d2L)]
  [TestCase(unchecked((long)0xfff0000000000000UL), 0x0000000000000000L, unchecked((long)0xbff921fb54442d18UL))]
  [TestCase(unchecked((long)0xfff0000000000000UL), unchecked((long)0x8000000000000000UL), unchecked((long)0xbff921fb54442d18UL))]
  [TestCase(unchecked((long)0xfff0000000000000UL), 0x3ff0000000000000L, unchecked((long)0xbff921fb54442d18UL))]
  [TestCase(unchecked((long)0xfff0000000000000UL), unchecked((long)0xbff0000000000000UL), unchecked((long)0xbff921fb54442d18UL))]
  [TestCase(unchecked((long)0xfff0000000000000UL), 0x7ff0000000000000L, unchecked((long)0xbfe921fb54442d18UL))]
  [TestCase(unchecked((long)0xfff0000000000000UL), unchecked((long)0xfff0000000000000UL), unchecked((long)0xc002d97c7f3321d2UL))]
  public void Atan2_OfSignedZerosOnesAndInfinities_IsTheIeeeResult(long y, long x, long expected)
    => AssertBoth("Atan2", IrType.F64, expected, y, x);

  // atan2f(y, x) for y and x each of +0, -0, +1, -1, +Inf and -Inf.
  [TestCase(0x00000000L, 0x00000000L, 0x00000000L)]
  [TestCase(0x00000000L, 0x80000000L, 0x40490fdbL)]
  [TestCase(0x00000000L, 0x3f800000L, 0x00000000L)]
  [TestCase(0x00000000L, 0xbf800000L, 0x40490fdbL)]
  [TestCase(0x00000000L, 0x7f800000L, 0x00000000L)]
  [TestCase(0x00000000L, 0xff800000L, 0x40490fdbL)]
  [TestCase(0x80000000L, 0x00000000L, 0x80000000L)]
  [TestCase(0x80000000L, 0x80000000L, 0xc0490fdbL)]
  [TestCase(0x80000000L, 0x3f800000L, 0x80000000L)]
  [TestCase(0x80000000L, 0xbf800000L, 0xc0490fdbL)]
  [TestCase(0x80000000L, 0x7f800000L, 0x80000000L)]
  [TestCase(0x80000000L, 0xff800000L, 0xc0490fdbL)]
  [TestCase(0x3f800000L, 0x00000000L, 0x3fc90fdbL)]
  [TestCase(0x3f800000L, 0x80000000L, 0x3fc90fdbL)]
  [TestCase(0x3f800000L, 0x3f800000L, 0x3f490fdbL)]
  [TestCase(0x3f800000L, 0xbf800000L, 0x4016cbe4L)]
  [TestCase(0x3f800000L, 0x7f800000L, 0x00000000L)]
  [TestCase(0x3f800000L, 0xff800000L, 0x40490fdbL)]
  [TestCase(0xbf800000L, 0x00000000L, 0xbfc90fdbL)]
  [TestCase(0xbf800000L, 0x80000000L, 0xbfc90fdbL)]
  [TestCase(0xbf800000L, 0x3f800000L, 0xbf490fdbL)]
  [TestCase(0xbf800000L, 0xbf800000L, 0xc016cbe4L)]
  [TestCase(0xbf800000L, 0x7f800000L, 0x80000000L)]
  [TestCase(0xbf800000L, 0xff800000L, 0xc0490fdbL)]
  [TestCase(0x7f800000L, 0x00000000L, 0x3fc90fdbL)]
  [TestCase(0x7f800000L, 0x80000000L, 0x3fc90fdbL)]
  [TestCase(0x7f800000L, 0x3f800000L, 0x3fc90fdbL)]
  [TestCase(0x7f800000L, 0xbf800000L, 0x3fc90fdbL)]
  [TestCase(0x7f800000L, 0x7f800000L, 0x3f490fdbL)]
  [TestCase(0x7f800000L, 0xff800000L, 0x4016cbe4L)]
  [TestCase(0xff800000L, 0x00000000L, 0xbfc90fdbL)]
  [TestCase(0xff800000L, 0x80000000L, 0xbfc90fdbL)]
  [TestCase(0xff800000L, 0x3f800000L, 0xbfc90fdbL)]
  [TestCase(0xff800000L, 0xbf800000L, 0xbfc90fdbL)]
  [TestCase(0xff800000L, 0x7f800000L, 0xbf490fdbL)]
  [TestCase(0xff800000L, 0xff800000L, 0xc016cbe4L)]
  public void Atan2F_OfSignedZerosOnesAndInfinities_IsTheIeeeResult(long y, long x, long expected)
    => AssertBoth("Atan2", IrType.F32, expected, y, x);

  // From asin.wc: an input of the exception table, of either sign, and one the refinement flags but rounds.
  [TestCase(0x3e57137449123ef6L, 0x3e57137449123ef7L)]
  [TestCase(unchecked((long)0xbe57137449123ef6UL), unchecked((long)0xbe57137449123ef7UL))]
  [TestCase(0x3e64f747439b3489L, 0x3e64f747439b348aL)]
  public void Asin_OfAHardCase_IsCorrectlyRounded(long x, long expected)
    => AssertBoth("Asin", IrType.F64, expected, x);

  // The two exceptions of asinf.c, of either sign.
  [TestCase(0x3f2ab445L, 0x3f3adc51L)]
  [TestCase(0xbf2ab445L, 0xbf3adc51L)]
  [TestCase(0x3f083a1aL, 0x3f0fa5b2L)]
  public void AsinF_OfAnException_IsCorrectlyRounded(long x, long expected)
    => AssertBoth("Asin", IrType.F32, expected, x);

  // From acos.wc: ties of the refinement moved up and down; then the five exceptions of acos.c.
  [TestCase(0x3fe11b3c109f983bL, 0x3ff01bd20609b7b3L)]
  [TestCase(unchecked((long)0xbf81e26a517cd019UL), 0x3ff945c046b2999aL)]
  [TestCase(0x3feffffffffffdc0L, 0x3e98000000000024L)]
  [TestCase(0x3fb53ea6c7255e88L, 0x3ff7cdacb6bbe707L)]
  [TestCase(0x3f4fd737be914578L, 0x3ff91e006d41d8d9L)]
  [TestCase(0x3fefffffffffff70L, 0x3e88000000000009L)]
  [TestCase(0x3fa390e6939cd1a6L, 0x3ff8856a5d3296a4L)]
  public void Acos_OfAHardCase_IsCorrectlyRounded(long x, long expected)
    => AssertBoth("Acos", IrType.F64, expected, x);

  // The two exceptions of acosf.c.
  [TestCase(0x328885a3L, 0x3fc90fdbL)]
  [TestCase(0x39826222L, 0x3fc907b5L)]
  public void AcosF_OfAnException_IsCorrectlyRounded(long x, long expected)
    => AssertBoth("Acos", IrType.F32, expected, x);

  // From atan.wc: ties of the refinement moved up and down; then the exceptions of atan.c, one negated.
  [TestCase(0x3e60a402fcc79299L, 0x3e60a402fcc79297L)]
  [TestCase(0x3f422e8d75e2bc7fL, 0x3f422e8d5694ad2cL)]
  [TestCase(0x3f80dc89a3b55010L, 0x3f80dc70ac228717L)]
  [TestCase(unchecked((long)0xbf80dc89a3b55010UL), unchecked((long)0xbf80dc70ac228717UL))]
  [TestCase(0x3f7e3fb41d2d2260L, 0x3f7e3f9013a852f8L)]
  [TestCase(0x3fe7ba49f739829fL, 0x3fe46ac372243536L)]
  [TestCase(0x3fca933fe176b375L, 0x3fca33f32ac5ceb5L)]
  [TestCase(0x3f7bb04a79820063L, 0x3f7bb02ed5c5e956L)]
  [TestCase(0x3f7cd30a9499618bL, 0x3f7cd2eb65f92a46L)]
  [TestCase(0x3f8f44aa37b8e66bL, 0x3f8f440b04187c87L)]
  [TestCase(0x3f7fd2ac95e57ef9L, 0x3f7fd2829febc03aL)]
  [TestCase(0x3f96419079bbf601L, 0x3f9640aade8f5427L)]
  [TestCase(0x3fcd768804487b07L, 0x3fccf5676f373ec1L)]
  public void Atan_OfAHardCase_IsCorrectlyRounded(long x, long expected)
    => AssertBoth("Atan", IrType.F64, expected, x);

  // From atan2.wc (with the operands negated, swapped or scaled into the subnormals) and the boundary
  // cases of the 192-bit path: a reduction that loses bits of y, exact tiny quotients with y above and
  // below 2^-915, quotients that cancel or carry in each word of the 192-bit significand, a quotient
  // below 2^-1074, huge operands, divisors in the subnormals, and atan(y/x) just below 2^-1022.
  [TestCase(0x001fffffffffffffL, 0x0090000000000000L, 0x3f7fffd555bbba96L)]
  [TestCase(0x3ff0000000000000L, 0x7fe0000000000000L, 0x0008000000000000L)]
  [TestCase(0x0000000000000001L, 0x000fffffffffffffL, 0x3cb0000000000001L)]
  [TestCase(0x0000000000000001L, 0x0010000000000000L, 0x3cb0000000000000L)]
  [TestCase(0x7fefffffffffffffL, 0x0000000000000001L, 0x3ff921fb54442d18L)]
  [TestCase(0x4515742da6768e82L, 0x4332be9b66b9fd5cL, 0x3ff921fb540c4220L)]
  [TestCase(0x4328f4f154d2451eL, 0x4333bf6138efe156L, 0x3fe2088a3454573eL)]
  [TestCase(0x0000000000000001L, unchecked((long)0xbff0000000000000UL), 0x400921fb54442d18L)]
  [TestCase(0x432fd91dd151d97eL, 0x433985ea7b4feb70L, 0x3fe1d99b16a756f4L)]
  [TestCase(unchecked((long)0x8000000000000001UL), 0x3ff0000000000001L, unchecked((long)0x8000000000000001UL))]
  [TestCase(unchecked((long)0xff7a79b84682c158UL), 0x7fec49b7d1db9b1cL, unchecked((long)0xbf7df2fa4fc3ac8eUL))]
  [TestCase(0x000924dbc8a33650L, 0x00061097e30d7d55L, 0x3fef866877892643L)]
  [TestCase(0x29bfffffffffffffL, 0x69a0000000000000L, 0x000fffffffffffffL)]
  public void Atan2_OfAHardCase_IsCorrectlyRounded(long y, long x, long expected)
    => AssertBoth("Atan2", IrType.F64, expected, y, x);

  // From atan2f.wc: a tiny quotient on a midpoint of float.
  [TestCase(0x0b7eff62L, 0x4b000000L, 0x003fbfd8L)]
  public void Atan2F_OfATinyQuotientOnAMidpoint_IsCorrectlyRounded(long y, long x, long expected)
    => AssertBoth("Atan2", IrType.F32, expected, y, x);

  private static void AssertBoth(string method, IrType format, long expected, params long[] operands) {
    Assert.That(FloatImplementations.Compiled(method, format, operands), Is.EqualTo(expected), "compiled");
    Assert.That(FloatImplementations.Interpreted(method, format, operands), Is.EqualTo(expected), "interpreted");
  }

}
