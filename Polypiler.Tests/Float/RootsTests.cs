using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// The branches of the roots that the reference vectors do not reach: the cube roots closest to a
/// midpoint, the subnormal and the exact-midpoint distances, and the corrected reciprocal square
/// roots of <c>float</c>. Every expected result is MPFR's, rounded to nearest; the compiled and the
/// imported implementation have to give it.
/// </summary>
[TestFixture]
public sealed class RootsTests {

  // The two radicands CORE-MATH's cbrt.c resolves by name, 0x1.9b78223aa307cp+1 and
  // 0x1.a202bfc89ddffp+2, with either sign.
  [TestCase(0x4009b78223aa307cL, 0x3ff79d15d0e8d59cL)]
  [TestCase(0x401a202bfc89ddffL, 0x3ffde87aa837820fL)]
  [TestCase(unchecked((long)0xc009b78223aa307cUL), unchecked((long)0xbff79d15d0e8d59cUL))]
  [TestCase(unchecked((long)0xc01a202bfc89ddffUL), unchecked((long)0xbffde87aa837820fUL))]
  public void Cbrt_OfARadicandWhoseRootIsClosestToAMidpoint_IsCorrectlyRounded(long x, long expected)
    => AssertResult("Cbrt", IrType.F64, [x], expected);

  // Subnormal operands whose first integer root is too large and is stepped down.
  [TestCase(0x000a06390887d960L, 0x000a9481aba3a87cL, 0x000e93275092f2dbL)]
  [TestCase(0x000158a571c7757eL, 0x000ab2e250d8fa07L, 0x000ac87bab85468eL)]
  // Pythagorean triples a^2 + b^2 = c^2 with c odd of 54 bits: the result is a midpoint, rounded to even.
  [TestCase(0x433c9e2cbf2e0ec9L, 0x432fe935c8771380L, 0x434061f2f6beab64L)]
  [TestCase(0x433e0f1767f16e71L, 0x4330d4669e6fbcb0L, 0x4341398e6f3ae978L)]
  [TestCase(0x4338cf1b789fb2e3L, 0x4337ea66d2d6a1b4L, 0x43413acae9030932L)]
  public void Hypot_OfSubnormalOrMidpointOperands_IsCorrectlyRounded(long x, long y, long expected)
    => AssertResult("Hypot", IrType.F64, [x, y], expected);

  // The operands rsqrtf.c corrects by a factor just below 1, of the families 0x1.7bf15p-128 * 4^k
  // and 0x1.ab6f7ap-125 * 4^k: 0x1.7bf15p-128 (subnormal), 0x1.7bf15p+0 and 0x1.ab6f7ap-1.
  [TestCase(0x002f7e2aL, 0x5f5222e0L)]
  [TestCase(0x3fbdf8a8L, 0x3f5222e0L)]
  [TestCase(0x3f55b7bdL, 0x3f8c1740L)]
  public void ReciprocalSqrtF_OfACorrectedSignificand_IsCorrectlyRounded(long x, long expected)
    => AssertResult("ReciprocalSqrt", IrType.F32, [x], expected);

  private static void AssertResult(string method, IrType format, long[] operands, long expected) {
    Assert.That(FloatImplementations.Compiled(method, format, operands), Is.EqualTo(expected), "compiled");
    Assert.That(FloatImplementations.Interpreted(method, format, operands), Is.EqualTo(expected), "interpreted");
  }

}
