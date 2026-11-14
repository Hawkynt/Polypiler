using System;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// The paths of the logarithms that the reference vectors do not reach: hard-to-round inputs found by
/// running CORE-MATH's own code instrumented, over its worst-case files and over random inputs, with
/// the results computed by MPFR; and the 128-bit arithmetic of the exact path, whose rare branches no
/// known input takes, against the results of CORE-MATH's <c>dint.h</c> itself.
/// </summary>
[TestFixture]
public sealed class LogarithmTests {

  /// <summary>Inputs that end in a rare path of a function of <c>double</c>, with the correctly rounded result.</summary>
  private static readonly TestCaseData[] _doubleCases = [
    // The exact path of log with the exponent 1, where the product with log(2) has no high half.
    new TestCaseData("Log", 0x3ff7be9f580c7cd9L, 0x3fd943ddafdaa250L).SetArgDisplayNames("Log", "exponent 1"),
    new TestCaseData("Log", 0x3ffbbfff9457d5c3L, 0x3fe19ee63842b127L).SetArgDisplayNames("Log", "worst case, exponent 1"),
    new TestCaseData("Log10", 0x3ff751bfa632c1c8L, 0x3fc4f0b7863cad50L).SetArgDisplayNames("Log10", "worst case, exponent 1"),
    new TestCaseData("Log10", 0x3fddc07bc5c79296L, unchecked((long)0xbfd54a63f66453a6UL)).SetArgDisplayNames("Log10", "exponent -1"),
    // log2's refinement with a middle part that is a power of two.
    new TestCaseData("Log2", 0x3f71ba39ff28e3eaL, unchecked((long)0xc01f688984494e19UL)).SetArgDisplayNames("Log2", "power-of-two middle part"),
    new TestCaseData("Log2", 0x1ffd30a43773dd1bL, unchecked((long)0xc07ff21f1e7648dbUL)).SetArgDisplayNames("Log2", "power-of-two middle part, tiny"),
    // log1p's refinement for 1 + x that needs no double-double, and for both directions of the last correction.
    new TestCaseData("Log1P", 0x46b4341e70e3c555L, 0x4052c5f672fdc5d8L).SetArgDisplayNames("Log1P", "x above 2^106"),
    new TestCaseData("Log1P", unchecked((long)0xbfe044c84888cd2fUL), unchecked((long)0xbfe6b8fe9a7d0dc8UL)).SetArgDisplayNames("Log1P", "x below -1/2"),
    new TestCaseData("Log1P", 0x3d5031b2d07a561bL, 0x3d5031b2d07a540eL).SetArgDisplayNames("Log1P", "correction toward zero"),
    new TestCaseData("Log1P", 0x3dd00800000156abL, 0x3dd007ffffff54abL).SetArgDisplayNames("Log1P", "correction away from zero"),
  ];

  /// <summary>Inputs that end in a rare path of a function of <c>float</c>, with the correctly rounded result.</summary>
  private static readonly TestCaseData[] _singleCases = [
    // A result of the refined logf or log10f whose last 28 bits are zero, which the error of the sum decides.
    new TestCaseData("Log", 0x41178febL, 0x400fe5e7L).SetArgDisplayNames("LogF", "halfway refinement"),
    new TestCaseData("Log", 0x3c413d3aL, 0xc08e158fL).SetArgDisplayNames("LogF", "halfway refinement, below 1"),
    new TestCaseData("Log10", 0x45bdedc8L, 0x407228d0L).SetArgDisplayNames("Log10F", "halfway refinement"),
    new TestCaseData("Log10", 0x0efeee7aL, 0xc1e99d23L).SetArgDisplayNames("Log10F", "halfway refinement, below 1"),
    // log1pf: the polynomial for small x whose last 28 bits are zero, and the refinement exactly half an ulp from a float.
    new TestCaseData("Log1P", 0x35400003L, 0x353fffffL).SetArgDisplayNames("Log1PF", "small, halfway polynomial"),
    new TestCaseData("Log1P", 0x3710001bL, 0x370ffff3L).SetArgDisplayNames("Log1PF", "small, halfway polynomial, larger"),
    new TestCaseData("Log1P", 0x3ddbfec3L, 0x3dd0f671L).SetArgDisplayNames("Log1PF", "refinement half an ulp off"),
    new TestCaseData("Log1P", 0x41078febL, 0x400fe5e7L).SetArgDisplayNames("Log1PF", "refinement half an ulp off, large"),
  ];

  [TestCaseSource(nameof(_doubleCases))]
  public void Double_RarePath_IsCorrectlyRounded(string method, long input, long expected) {
    Assert.That(FloatImplementations.Compiled(method, IrType.F64, [input]), Is.EqualTo(expected));
    Assert.That(FloatImplementations.Interpreted(method, IrType.F64, [input]), Is.EqualTo(expected));
  }

  [TestCaseSource(nameof(_singleCases))]
  public void Single_RarePath_IsCorrectlyRounded(string method, long input, long expected) {
    Assert.That(FloatImplementations.Compiled(method, IrType.F32, [input]), Is.EqualTo(expected));
    Assert.That(FloatImplementations.Interpreted(method, IrType.F32, [input]), Is.EqualTo(expected));
  }

  /// <summary>
  /// Sums of 128-bit numbers <c>[hi, lo, ex, sign]</c> on the branches no logarithm is known to take:
  /// equal exponents and high halves, equal magnitudes, a cancellation of more than 64 bits and
  /// alignments by 64 bits and more. The results are those of <c>add_dint</c> in CORE-MATH's <c>dint.h</c>.
  /// </summary>
  [TestCase(0x8000000000000005UL, 9UL, 3L, 0UL, 0x8000000000000005UL, 4UL, 3L, 0UL, 0x8000000000000005UL, 7UL, 4L, 0UL, TestName = "Add_EqualHighHalves_LowerLowHalfSecond")]
  [TestCase(0x8000000000000005UL, 4UL, 3L, 0UL, 0x8000000000000005UL, 9UL, 3L, 1UL, 0xa000000000000000UL, 0UL, -122L, 1UL, TestName = "Add_EqualHighHalves_LowerLowHalfFirst_CancelsBeyond64Bits")]
  [TestCase(0x8000000000000005UL, 4UL, 3L, 0UL, 0x8000000000000005UL, 4UL, 3L, 1UL, 0UL, 0UL, 0L, 0UL, TestName = "Add_EqualMagnitudes_OppositeSigns_IsZero")]
  [TestCase(0x8000000000000005UL, 4UL, 3L, 1UL, 0x8000000000000005UL, 4UL, 3L, 1UL, 0x8000000000000005UL, 4UL, 4L, 1UL, TestName = "Add_EqualMagnitudes_SameSign_Doubles")]
  [TestCase(0x8000000000000000UL, 7UL, 0L, 0UL, 0x8000000000000000UL, 2UL, 0L, 1UL, 0xa000000000000000UL, 0UL, -125L, 0UL, TestName = "Add_Cancellation_LeavesOnlyTheLowHalf")]
  [TestCase(0x8000000000000000UL, 0UL, 0L, 0UL, 0xffffffffffffffffUL, 0x8000000000000000UL, -64L, 0UL, 0x8000000000000000UL, 0xffffffffffffffffUL, 0L, 0UL, TestName = "Add_Alignment64")]
  [TestCase(0x8000000000000000UL, 0UL, 0L, 0UL, 0xc000000000000021UL, 0x1234UL, -70L, 0UL, 0x8000000000000000UL, 0x0300000000000000UL, 0L, 0UL, TestName = "Add_Alignment70")]
  [TestCase(0x8000000000000000UL, 0UL, 0L, 1UL, 0xc000000000000000UL, 0UL, -128L, 1UL, 0x8000000000000000UL, 0UL, 0L, 1UL, TestName = "Add_Alignment128")]
  [TestCase(0x8000000000000000UL, 0UL, 0L, 0UL, 0xc000000000000000UL, 0UL, -200L, 1UL, 0x8000000000000000UL, 0UL, 0L, 0UL, TestName = "Add_Alignment200")]
  [TestCase(0x8000000000000000UL, 0UL, 0L, 0UL, 0x8000000000000001UL, 0xffffffffffffffffUL, -1L, 0UL, 0xc000000000000001UL, 0UL, 0L, 0UL, TestName = "Add_RoundingCarriesIntoTheHighHalf")]
  public void Dint_Add_IsTheOneOfDintH(ulong ah, ulong al, long ae, ulong asign, ulong bh, ulong bl, long be, ulong bsign, ulong rh, ulong rl, long re, ulong rsign) {
    var result = (ulong[])Private("LogDintAdd").Invoke(null, [new[] { ah, al, (ulong)ae, asign }, new[] { bh, bl, (ulong)be, bsign }])!;
    Assert.That(result, Is.EqualTo(new[] { rh, rl, (ulong)re, rsign }));
  }

  /// <summary>
  /// Products with an integer, as <c>mul_dint_2</c> in CORE-MATH's <c>dint.h</c> gives them: one whose
  /// rounding overflows 128 bits, which log(2), the only factor of the logarithms, never causes.
  /// </summary>
  [TestCase(7L, 0x8000000000000000UL, 0xbfffffffffffffffUL, 3L, 0UL, TestName = "MultiplyInteger_OverflowingRounding")]
  [TestCase(-3L, 0xdb6db6db6db6db6eUL, 0xfffffffffffffffeUL, 1L, 1UL, TestName = "MultiplyInteger_Negative")]
  public void Dint_MultiplyInteger_IsTheOneOfDintH(long factor, ulong rh, ulong rl, long re, ulong rsign) {
    // hi = (2^66 - 1) / 7, lo = 2^64 - 1
    var a = new[] { 0x9249249249249249UL, 0xffffffffffffffffUL, 0UL, 0UL };
    var result = (ulong[])Private("LogDintMultiplyInteger").Invoke(null, [factor, a])!;
    Assert.That(result, Is.EqualTo(new[] { rh, rl, (ulong)re, rsign }));
  }

  private static MethodInfo Private(string name)
    => typeof(FloatOperations).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

}
