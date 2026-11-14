using System;
using NUnit.Framework;
using Polypiler.Core.Verification.Oracles;

namespace Polypiler.Tests.Verification.Oracles;

/// <summary>The policies: their spelling, their defaults and the bounds and sources they accept.</summary>
[TestFixture]
public sealed class ExactnessPolicyTests {

  [Test]
  public void Exact_IsBytesBySignedZero_WithoutABound() {
    var policy = new ExactnessPolicy.Exact();
    Assert.That((policy.Mode, policy.ElementFormat, policy.SpecialValues, policy.BoundKind), Is.EqualTo(("exact", (FloatFormat?)null, SpecialValues.SignedZero, BoundKind.None)));
    Assert.That(new ExactnessPolicy.Exact(FloatFormat.F32).ElementFormat, Is.EqualTo(FloatFormat.F32));
  }

  /// <summary>Test design 22: the bound is derived from a document or declared with a reason, and says which.</summary>
  [Test]
  public void Tolerances_SayWhereTheirBoundComesFrom() {
    var derived = new ExactnessPolicy.Ulp(3, FloatFormat.F64, policy: "VERIFICATION.tolerance.sin");
    var declared = new ExactnessPolicy.Relative(1e-12, FloatFormat.F64, reason: "sum of 64 Sin results");
    var absolute = new ExactnessPolicy.Absolute(0.5, FloatFormat.F32, reason: "r");
    Assert.That((derived.Mode, derived.BoundText, derived.BoundKind, derived.Policy, derived.Reason), Is.EqualTo(("ulp", "3", BoundKind.Derived, "VERIFICATION.tolerance.sin", (string?)null)));
    Assert.That((declared.Mode, declared.BoundText, declared.BoundKind, declared.ElementFormat), Is.EqualTo(("relative", "1E-12", BoundKind.Declared, (FloatFormat?)FloatFormat.F64)));
    Assert.That((absolute.Mode, absolute.BoundText, absolute.Bound, absolute.Format), Is.EqualTo(("absolute", "0.5", 0.5, FloatFormat.F32)));
  }

  [TestCase(null, null, TestName = "neither policy nor reason")]
  [TestCase("VERIFICATION.tolerance.sin", "r", TestName = "both policy and reason")]
  [TestCase(" ", null, TestName = "an empty policy")]
  [TestCase(null, "", TestName = "an empty reason")]
  public void ATolerance_NeedsExactlyOneSource(string? policy, string? reason)
    => Assert.That(() => new ExactnessPolicy.Ulp(1, FloatFormat.F64, policy, reason), Throws.ArgumentException);

  [TestCase(0)]
  [TestCase(-1)]
  public void AnUlpBound_IsPositive(int bound)
    => Assert.That(() => new ExactnessPolicy.Ulp(bound, FloatFormat.F64, reason: "r"), Throws.InstanceOf<ArgumentOutOfRangeException>());

  [TestCase(0.0)]
  [TestCase(-1e-12)]
  [TestCase(double.NaN)]
  [TestCase(double.PositiveInfinity)]
  public void ARealBound_IsPositiveAndFinite(double bound) {
    Assert.That(() => new ExactnessPolicy.Relative(bound, FloatFormat.F64, reason: "r"), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => new ExactnessPolicy.Absolute(bound, FloatFormat.F64, reason: "r"), Throws.InstanceOf<ArgumentOutOfRangeException>());
  }

  [Test]
  public void AnUnknownFormat_IsRefused()
    => Assert.That(() => new ExactnessPolicy.Ulp(1, (FloatFormat)7, reason: "r"), Throws.InstanceOf<ArgumentOutOfRangeException>());

  [TestCase(FloatFormat.F32, 4)]
  [TestCase(FloatFormat.F64, 8)]
  public void SizeOf_IsTheElementWidth(FloatFormat format, int size) => Assert.That(ExactnessPolicy.SizeOf(format), Is.EqualTo(size));

  [TestCase(SpecialValues.Bitwise, "bitwise")]
  [TestCase(SpecialValues.SignedZero, "signed-zero")]
  [TestCase(SpecialValues.Class, "class")]
  public void Spelling_IsTheVectorFiles(SpecialValues rule, string spelling) => Assert.That(ExactnessPolicy.Spelling(rule), Is.EqualTo(spelling));

}
