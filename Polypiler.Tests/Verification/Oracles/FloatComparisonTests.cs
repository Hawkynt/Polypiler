using System;
using NUnit.Framework;
using Polypiler.Core.Verification.Oracles;

namespace Polypiler.Tests.Verification.Oracles;

/// <summary>The element comparison of ADR 0005: the comparison distance, the special-value rules and the modes.</summary>
[TestFixture]
public sealed class FloatComparisonTests {

  private const ulong One64 = 0x3FF0000000000000;
  private const ulong One32 = 0x3F800000;
  private const ulong QuietNaN = 0x7FF8000000000000;
  private const ulong PositiveInfinity = 0x7FF0000000000000;
  private const ulong LargestFinite = 0x7FEFFFFFFFFFFFFF;
  private const ulong NegativeZero = 0x8000000000000000;

  private static ExactnessPolicy Ulp(int bound, FloatFormat format = FloatFormat.F64, SpecialValues rule = SpecialValues.SignedZero)
    => new ExactnessPolicy.Ulp(bound, format, reason: "test") { SpecialValues = rule };

  private static ExactnessPolicy Exact(SpecialValues rule = SpecialValues.SignedZero, FloatFormat format = FloatFormat.F64)
    => new ExactnessPolicy.Exact(format) { SpecialValues = rule };

  [TestCase(One64, One64, 0UL, TestName = "Distance: a value from itself is 0")]
  [TestCase(One64, One64 + 1, 1UL, TestName = "Distance: the next value is 1")]
  [TestCase(0x3FEFFFFFFFFFFFFFUL, One64, 1UL, TestName = "Distance: across a binade boundary a step is a step")]
  [TestCase(0UL, NegativeZero, 0UL, TestName = "Distance: both zeros are one point")]
  [TestCase(1UL, 0x8000000000000001UL, 2UL, TestName = "Distance: the smallest subnormals on both sides are 2 apart")]
  [TestCase(NegativeZero | One64, One64, 0x7FE0000000000000UL, TestName = "Distance: -1 and 1 count every value between")]
  [TestCase(NegativeZero | LargestFinite, LargestFinite, 0xFFDFFFFFFFFFFFFEUL, TestName = "Distance: beyond long, the whole finite range")]
  public void Distance_F64(ulong a, ulong b, ulong expected) {
    Assert.That(FloatComparison.Distance(FloatFormat.F64, a, b), Is.EqualTo(expected));
    Assert.That(FloatComparison.Distance(FloatFormat.F64, b, a), Is.EqualTo(expected));
  }

  [Test]
  public void Distance_F32_ReadsTheLow32Bits()
    => Assert.That(FloatComparison.Distance(FloatFormat.F32, 0xFFFFFFFF_00000000UL | 0x80000001, 1), Is.EqualTo(2));

  /// <summary>Test design 5: on, inside and outside the limit.</summary>
  [TestCase(0UL, true)]
  [TestCase(1UL, true)]
  [TestCase(2UL, false)]
  public void Ulp_PassesUpToTheBound(ulong steps, bool passes) {
    var verdict = FloatComparison.Compare(FloatFormat.F64, 0x3FD3333333333333, 0x3FD3333333333333 + steps, Ulp(1));
    Assert.That((verdict.Passed, verdict.Distance), Is.EqualTo((passes, (ulong?)steps)));
    Assert.That(verdict.Failure, Is.EqualTo(passes ? ElementFailure.None : ElementFailure.Tolerance));
    Assert.That(verdict.Excess, Is.EqualTo(passes ? null : "2"));
  }

  /// <summary>Test design 6: the sign of zero is not a distance; only the class rule lets it pass.</summary>
  [TestCase(SpecialValues.Bitwise, false)]
  [TestCase(SpecialValues.SignedZero, false)]
  [TestCase(SpecialValues.Class, true)]
  public void SignedZero_IsDecidedByTheRule(SpecialValues rule, bool passes) {
    foreach (var policy in new[] { Exact(rule), Ulp(1, rule: rule), new ExactnessPolicy.Absolute(1, FloatFormat.F64, reason: "r") { SpecialValues = rule } }) {
      var verdict = FloatComparison.Compare(FloatFormat.F64, 0, NegativeZero, policy);
      Assert.That((verdict.Passed, verdict.Distance), Is.EqualTo((passes, (ulong?)0)), policy.Mode);
      Assert.That(verdict.Failure, Is.EqualTo(passes ? ElementFailure.None : ElementFailure.SpecialValues));
    }
  }

  /// <summary>Test design 7: an infinity matches only the same infinity, at any bound.</summary>
  [TestCase(PositiveInfinity, LargestFinite)]
  [TestCase(LargestFinite, PositiveInfinity)]
  [TestCase(PositiveInfinity, NegativeZero | PositiveInfinity)]
  public void Infinity_MatchesOnlyItself(ulong reference, ulong actual) {
    var verdict = FloatComparison.Compare(FloatFormat.F64, reference, actual, Ulp(int.MaxValue));
    Assert.That((verdict.Failure, verdict.Distance), Is.EqualTo((ElementFailure.SpecialValues, (ulong?)null)));
    Assert.That(FloatComparison.Compare(FloatFormat.F64, reference, reference, Ulp(1)).Passed, Is.True);
  }

  /// <summary>Test design 11: a NaN payload counts only under the bitwise rule; a NaN never matches a number.</summary>
  [TestCase(SpecialValues.Bitwise, false)]
  [TestCase(SpecialValues.SignedZero, true)]
  [TestCase(SpecialValues.Class, true)]
  public void NaNPayloads_AreDecidedByTheRule(SpecialValues rule, bool passes) {
    foreach (var policy in new[] { Exact(rule), Ulp(1, rule: rule), new ExactnessPolicy.Relative(0.5, FloatFormat.F64, reason: "r") { SpecialValues = rule } }) {
      var verdict = FloatComparison.Compare(FloatFormat.F64, QuietNaN, QuietNaN + 1, policy);
      Assert.That((verdict.Passed, verdict.Distance), Is.EqualTo((passes, (ulong?)null)), policy.Mode);
      Assert.That(FloatComparison.Compare(FloatFormat.F64, QuietNaN, QuietNaN, policy).Passed, Is.True);
      Assert.That(FloatComparison.Compare(FloatFormat.F64, QuietNaN, One64, policy).Failure, Is.EqualTo(ElementFailure.SpecialValues));
      Assert.That(FloatComparison.Compare(FloatFormat.F64, One64, QuietNaN, policy).Failure, Is.EqualTo(ElementFailure.SpecialValues));
    }
  }

  [Test]
  public void Exact_DifferentNumbers_FailOnTheirBits() {
    var verdict = FloatComparison.Compare(FloatFormat.F64, One64, One64 + 1, Exact());
    Assert.That((verdict.Failure, verdict.Distance), Is.EqualTo((ElementFailure.Bits, (ulong?)1)));
  }

  [Test]
  public void AZeroAndANumber_AreComparedByTheMode() {
    Assert.That(FloatComparison.Compare(FloatFormat.F64, 0, 1, Ulp(1)).Passed, Is.True, "underflow within the bound");
    Assert.That(FloatComparison.Compare(FloatFormat.F64, NegativeZero, 1, Ulp(1)).Passed, Is.True, "the sign of a zero is not a distance");
    Assert.That(FloatComparison.Compare(FloatFormat.F64, 0, 1, Exact()).Failure, Is.EqualTo(ElementFailure.Bits));
  }

  /// <summary>Test design 10: each tolerance mode on f32 and f64, one step above 1.</summary>
  [TestCase(FloatFormat.F32, One32, One32 + 1, 1.2e-7, 1.1e-7)]
  [TestCase(FloatFormat.F64, One64, One64 + 1, 2.3e-16, 2.2e-16)]
  public void EachMode_PassesAndFailsAtItsLimit(FloatFormat format, ulong reference, ulong actual, double passing, double failing) {
    var step = FloatComparison.Value(format, actual) - FloatComparison.Value(format, reference);
    Assert.That(step, Is.GreaterThan(failing).And.LessThan(passing));
    Assert.That(FloatComparison.Compare(format, reference, actual, Ulp(1, format)).Passed, Is.True);
    Assert.That(FloatComparison.Compare(format, actual, reference, new ExactnessPolicy.Relative(passing, format, reason: "r")).Passed, Is.True);
    Assert.That(FloatComparison.Compare(format, reference, actual, new ExactnessPolicy.Relative(passing, format, reason: "r")).Passed, Is.True);
    Assert.That(FloatComparison.Compare(format, reference, actual, new ExactnessPolicy.Relative(failing, format, reason: "r")).Failure, Is.EqualTo(ElementFailure.Tolerance));
    Assert.That(FloatComparison.Compare(format, reference, actual, new ExactnessPolicy.Absolute(passing, format, reason: "r")).Passed, Is.True);
    Assert.That(FloatComparison.Compare(format, reference, actual, new ExactnessPolicy.Absolute(failing, format, reason: "r")).Failure, Is.EqualTo(ElementFailure.Tolerance));
  }

  /// <summary>The bound is compared exactly: a difference equal to the bound passes, the next one up fails, even where double arithmetic would round them together.</summary>
  [Test]
  public void Absolute_IsExact_OnTheBound() {
    var reference = BitConverter.DoubleToUInt64Bits(1e16);
    var actual = reference + 1;
    var step = FloatComparison.Value(FloatFormat.F64, actual) - 1e16;
    Assert.That(step, Is.EqualTo(2));
    Assert.That(FloatComparison.Compare(FloatFormat.F64, reference, actual, new ExactnessPolicy.Absolute(2, FloatFormat.F64, reason: "r")).Passed, Is.True);
    var below = FloatComparison.Compare(FloatFormat.F64, reference, actual, new ExactnessPolicy.Absolute(Math.BitDecrement(2.0), FloatFormat.F64, reason: "r"));
    Assert.That((below.Failure, below.Excess), Is.EqualTo((ElementFailure.Tolerance, "2")));
  }

  [Test]
  public void Relative_AgainstAZeroReference_FailsAnyNumber() {
    var verdict = FloatComparison.Compare(FloatFormat.F64, 0, One64, new ExactnessPolicy.Relative(1e300, FloatFormat.F64, reason: "r"));
    Assert.That((verdict.Failure, verdict.Excess), Is.EqualTo((ElementFailure.Tolerance, "Infinity")));
  }

  [Test]
  public void Relative_ScalesWithTheReference_AcrossSubnormals() {
    var policy = new ExactnessPolicy.Relative(0.5, FloatFormat.F64, reason: "r");
    Assert.That(FloatComparison.Compare(FloatFormat.F64, 2, 3, policy).Passed, Is.True, "|3 - 2| <= 0.5 * 2 in units of the smallest subnormal");
    Assert.That(FloatComparison.Compare(FloatFormat.F64, 2, 4, policy).Passed, Is.False);
    Assert.That(FloatComparison.Compare(FloatFormat.F64, NegativeZero | 2, NegativeZero | 3, policy).Passed, Is.True);
  }

  [Test]
  public void ATolerance_OverAnotherFormat_IsRefused()
    => Assert.That(() => FloatComparison.Compare(FloatFormat.F32, 0, 0, Ulp(1)), Throws.ArgumentException.With.Message.Contains("F64"));

  [Test]
  public void Compare_WithoutAPolicy_Throws()
    => Assert.That(() => FloatComparison.Compare(FloatFormat.F64, 0, 0, null!), Throws.ArgumentNullException);

}
