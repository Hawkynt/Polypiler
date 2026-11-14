using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// The arguments of the sine and cosine that the reference vectors do not reach: those of the tables
/// of exceptions, and the large binary32 cosines whose result lies close enough to a rounding boundary
/// to be looked up but is no exception. The expected bits are MPFR's, rounded to nearest; the compiled
/// and the imported implementation both have to produce them.
/// </summary>
[TestFixture]
public sealed class TrigonometricTests {

  [TestCase(0x3f3adc51L, 0x3f2ab445L, TestName = "SinF_Exception(0x1.75b8a2p-1)")]
  [TestCase(0xbf3adc51L, 0xbf2ab445L, TestName = "SinF_Exception(-0x1.75b8a2p-1)")]
  [TestCase(0x3fa7832aL, 0x3f7741b6L, TestName = "SinF_Exception(0x1.4f0654p+0)")]
  [TestCase(0xbfa7832aL, 0xbf7741b6L, TestName = "SinF_Exception(-0x1.4f0654p+0)")]
  [TestCase(0x46199998L, 0xbeb1fa5dL, TestName = "SinF_Exception(0x1.33333p+13)")]
  [TestCase(0xc6199998L, 0x3eb1fa5dL, TestName = "SinF_Exception(-0x1.33333p+13)")]
  [TestCase(0x4116cbe4L, 0xb2ccde2eL, TestName = "SinF_JustAboveTheNearReduction(0x1.2d97c8p+3)")]
  [TestCase(0xc116cbe4L, 0x32ccde2eL, TestName = "SinF_JustAboveTheNearReduction(-0x1.2d97c8p+3)")]
  public void SinF_IsCorrectlyRounded(long argument, long expected) => AssertBoth("Sin", IrType.F32, argument, expected);

  [TestCase(0x4096cbe4L, 0x324cde2eL, TestName = "CosF_Exception(0x1.2d97c8p+2)")]
  [TestCase(0xc096cbe4L, 0x324cde2eL, TestName = "CosF_Exception(-0x1.2d97c8p+2)")]
  [TestCase(0x5922aa80L, 0x3f08aebfL, TestName = "CosF_LargeException(0x1.4555p+51)")]
  [TestCase(0xd922aa80L, 0x3f08aebfL, TestName = "CosF_LargeException(-0x1.4555p+51)")]
  [TestCase(0x5aa4542cL, 0x3efa40a4L, TestName = "CosF_LargeException(0x1.48a858p+54)")]
  [TestCase(0x5f18b878L, 0x3f7f14bbL, TestName = "CosF_LargeException(0x1.3170fp+63)")]
  [TestCase(0x6115cb11L, 0x3f78142fL, TestName = "CosF_LargeException(0x1.2b9622p+67)")]
  [TestCase(0xe115cb11L, 0x3f78142fL, TestName = "CosF_LargeException(-0x1.2b9622p+67)")]
  [TestCase(0x4ca332e9L, 0xbf800000L, TestName = "CosF_LargeCloseToABoundary(0x1.4665d2p+26)")]
  [TestCase(0x4e5b65ffL, 0xbf62075dL, TestName = "CosF_LargeCloseToABoundary(0x1.b6cbfep+29)")]
  [TestCase(0xd0caaf91L, 0xbf5f62a9L, TestName = "CosF_LargeCloseToABoundary(-0x1.955f22p+34)")]
  public void CosF_IsCorrectlyRounded(long argument, long expected) => AssertBoth("Cos", IrType.F32, argument, expected);

  [TestCase(0x3e88000000000009L, 0x3fefffffffffff70L, TestName = "Cos_DirectedRoundingException(0x1.8000000000009p-23)")]
  [TestCase(0x3e98000000000024L, 0x3feffffffffffdc0L, TestName = "Cos_DirectedRoundingException(0x1.8000000000024p-22)")]
  [TestCase(0x3ea8000000000090L, 0x3feffffffffff700L, TestName = "Cos_DirectedRoundingException(0x1.800000000009p-21)")]
  [TestCase(unchecked((long)0xbeb20000000000f3UL), 0x3fefffffffffebc0L, TestName = "Cos_DirectedRoundingException(-0x1.20000000000f3p-20)")]
  public void Cos_IsCorrectlyRounded(long argument, long expected) => AssertBoth("Cos", IrType.F64, argument, expected);

  private static void AssertBoth(string method, IrType format, long argument, long expected) {
    Assert.That(FloatImplementations.Compiled(method, format, [argument]), Is.EqualTo(expected), "compiled");
    Assert.That(FloatImplementations.Interpreted(method, format, [argument]), Is.EqualTo(expected), "interpreted");
  }

}
