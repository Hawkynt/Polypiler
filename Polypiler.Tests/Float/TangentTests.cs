using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// The tangent on the arguments whose rounding its approximations leave undecided, so that they are
/// looked up: the inputs are CORE-MATH's exceptions, and the arguments of <c>tanf</c> that reach the
/// lookup without being in it; the expected results are MPFR's <c>mpfr_tan</c>, rounded to nearest.
/// Each runs compiled and imported into the interpreter.
/// </summary>
[TestFixture]
public sealed class TangentTests {

  [TestCase(0x3e9dffffffffff1fUL, 0x3e9e000000000151UL)] // 0x1.dffffffffff1fp-22, the hardest case
  [TestCase(0xbe9dffffffffff1fUL, 0xbe9e000000000151UL)] // its negation
  [TestCase(0x3eadfffffffffc7cUL, 0x3eae000000000546UL)] // 0x1.dfffffffffc7cp-21, settled before the lookup
  public void Tan_OfAHardToRoundArgument_IsCorrectlyRounded(ulong x, ulong expected) {
    var operands = new[] { (long)x };
    Assert.That(FloatImplementations.Compiled("Tan", IrType.F64, operands), Is.EqualTo((long)expected));
    Assert.That(FloatImplementations.Interpreted("Tan", IrType.F64, operands), Is.EqualTo((long)expected));
  }

  [TestCase(0x3f8a1f62U, 0x3feefcfbU)] // 0x1.143ec4p+0, the first exception, below 2^28
  [TestCase(0xbf8a1f62U, 0xbfeefcfbU)] // its negation
  [TestCase(0x4d56d355U, 0x3e740182U)] // 0x1.ada6aap+27
  [TestCase(0x57d7b0edU, 0x3eb068e4U)] // 0x1.af61dap+48
  [TestCase(0x5980445eU, 0x3fe50f68U)] // 0x1.0088bcp+52
  [TestCase(0x63fc86feU, 0x3f2cbfceU)] // 0x1.f90dfcp+72
  [TestCase(0x6a662711U, 0xc0799ac2U)] // 0x1.cc4e22p+85
  [TestCase(0xead36709U, 0x3f62b097U)] // -0x1.a6ce12p+86
  [TestCase(0x72b505bbU, 0xbff2150fU)] // 0x1.6a0b76p+102, the last exception
  [TestCase(0x53ff1aefU, 0xbee4dd11U)] // 0x1.fe35dep+40, close to a boundary but not an exception
  [TestCase(0x39e89768U, 0x39e89769U)] // 0x1.d12edp-12, likewise
  [TestCase(0xba7a8d2fU, 0xba7a8d34U)] // -0x1.f51a5ep-11, likewise
  public void TanF_OfAHardToRoundArgument_IsCorrectlyRounded(uint x, uint expected) {
    var operands = new[] { (long)x };
    Assert.That(FloatImplementations.Compiled("Tan", IrType.F32, operands), Is.EqualTo((long)expected));
    Assert.That(FloatImplementations.Interpreted("Tan", IrType.F32, operands), Is.EqualTo((long)expected));
  }

}
