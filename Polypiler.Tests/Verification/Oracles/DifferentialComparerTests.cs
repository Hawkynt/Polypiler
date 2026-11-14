using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Oracles;
using static Polypiler.Tests.Verification.Oracles.Support.OracleTesting;

namespace Polypiler.Tests.Verification.Oracles;

/// <summary>The comparison of whole outputs: byte offsets, element reading, the report and its diagnostics.</summary>
[TestFixture]
public sealed class DifferentialComparerTests {

  private static readonly ExactnessPolicy _bytes = new ExactnessPolicy.Exact();
  private static readonly ExactnessPolicy _ulp1 = new ExactnessPolicy.Ulp(1, FloatFormat.F64, reason: "test");

  /// <summary>Test design 1 to 3: the published vectors compared with themselves pass, the empty output included.</summary>
  [Test]
  public void PublishedVectors_Pass() {
    var file = VectorFile.Load(AdlerVectors);
    var expected = file.Vectors.Select(vector => vector.Expected!).ToArray();
    var report = DifferentialComparer.Compare(file.Vectors, expected, [.. expected.Select(bytes => bytes.ToArray())], file.Exactness);
    Assert.That((report.Total, report.Passed, report.Verified, report.BoundKind), Is.EqualTo((4, 4, true, BoundKind.None)));
    Assert.That(report.Outcomes.Select(outcome => (outcome.Name, outcome.ActualHex)), Is.EqualTo(new[] {
      ("empty", "00000001"), ("a", "00620062"), ("abc", "024d0127"), ("300 x ff", "b90f2ae4")
    }));
    Assert.That(report.Outcomes.All(outcome => outcome.Identical && outcome.UlpDistance is null), Is.True);
    Assert.That(report.Diagnostics("cipher.adler32"), Is.Empty);
  }

  /// <summary>Test design 4: no vectors is nothing verified.</summary>
  [Test]
  public void NoVectors_VerifyNothing() {
    var report = DifferentialComparer.Compare([], [], [], _bytes);
    Assert.That((report.Total, report.Passed, report.Verified), Is.EqualTo((0, 0, false)));
    var notCompared = ComparisonReport.NotCompared(3);
    Assert.That((notCompared.Verified, notCompared.Total, notCompared.Policy, notCompared.BoundKind), Is.EqualTo((false, 3, (ExactnessPolicy?)null, BoundKind.None)));
  }

  /// <summary>Test design 8: the first and the last byte.</summary>
  [TestCase("00000000", "ff000000", 0)]
  [TestCase("00000000", "000000ff", 3)]
  public void TheFirstDifferingByte_IsReported(string reference, string actual, int offset) {
    var outcome = CompareOne(Convert.FromHexString(reference), Convert.FromHexString(actual), _bytes);
    Assert.That((outcome.Passed, outcome.FirstDifferingOffset, outcome.ReferenceHex, outcome.ActualHex), Is.EqualTo((false, (int?)offset, reference, actual)));
  }

  /// <summary>Test design 12: an output of another length differs where the shorter one ends.</summary>
  [Test]
  public void AShorterOutput_DiffersWhereItEnds() {
    var outcome = CompareOne([1, 2, 3, 4], [1, 2, 3], _bytes);
    Assert.That((outcome.Passed, outcome.FirstDifferingOffset), Is.EqualTo((false, (int?)3)));
  }

  /// <summary>Test design 13: the wrong-modulus checksum, reported with both values.</summary>
  [Test]
  public void TheWrongModulus_IsPLY0051() {
    var report = DifferentialComparer.Compare(Named("abc", "300 x ff"), [[0x02, 0x4d, 0x01, 0x27], [0xb9, 0x0f, 0x2a, 0xe4]], [[0x02, 0x4d, 0x01, 0x27], [0xae, 0xce, 0x2a, 0xd5]], _bytes);
    Assert.That((report.Total, report.Passed, report.Verified), Is.EqualTo((2, 1, false)));
    var diagnostic = report.Diagnostics("cipher.adler32").Single();
    Assert.That((diagnostic.Code, diagnostic.ExitCode), Is.EqualTo(("PLY0051", (ExitCode?)ExitCode.SemanticMismatch)));
    Assert.That(diagnostic.Message, Is.EqualTo("'cipher.adler32' vector '300 x ff': expected b90f2ae4, actual aece2ad5, first difference at byte 0"));
  }

  /// <summary>Every vector is compared, even after one failed, and the report keeps file order.</summary>
  [Test]
  public void EveryVector_IsCompared_AfterAFailure() {
    var report = DifferentialComparer.Compare(Named("x", "y", "z"), [[1], [2], [3]], [[9], [2], [9]], _bytes);
    Assert.That(report.Outcomes.Select(outcome => (outcome.Name, outcome.Passed)), Is.EqualTo(new[] { ("x", false), ("y", true), ("z", false) }));
    Assert.That(report.Diagnostics("f").Select(diagnostic => diagnostic.Message[..10]), Is.EqualTo(new[] { "'f' vector", "'f' vector" }));
  }

  /// <summary>Test design 20: a tolerance needs whole elements of equal count.</summary>
  [TestCase(7, 7, 0)]
  [TestCase(15, 15, 8)]
  [TestCase(8, 16, 8)]
  public void ATolerance_OnIncompleteElements_IsPLY0051(int referenceLength, int actualLength, int offset) {
    var outcome = CompareOne(new byte[referenceLength], new byte[actualLength], _ulp1);
    Assert.That((outcome.Passed, outcome.FirstDifferingOffset, outcome.Mismatch), Is.EqualTo((false, (int?)offset, (ElementMismatch?)null)));
  }

  /// <summary>The scenario rows: typed f64 elements under each policy.</summary>
  [TestCase(0x3FD3333333333333UL, 0x3FD3333333333334UL, "exact", false, 1L, null)]
  [TestCase(0x3FD3333333333333UL, 0x3FD3333333333334UL, "ulp", true, 1L, null)]
  [TestCase(0x3FD3333333333333UL, 0x3FD3333333333335UL, "ulp", false, 2L, "2 exceeds ulp 1")]
  [TestCase(0x0000000000000000UL, 0x8000000000000000UL, "ulp", false, 0L, "special values differ under signed-zero")]
  [TestCase(0x0000000000000000UL, 0x8000000000000000UL, "ulp class", true, 0L, null)]
  [TestCase(0x7FF8000000000000UL, 0x7FF8000000000001UL, "ulp", true, null, null)]
  [TestCase(0x7FF8000000000000UL, 0x7FF8000000000001UL, "exact", true, null, null)]
  [TestCase(0x7FF8000000000000UL, 0x7FF8000000000001UL, "exact bitwise", false, null, null)]
  [TestCase(0x7FF0000000000000UL, 0x7FEFFFFFFFFFFFFFUL, "ulp", false, null, "special values differ under signed-zero")]
  public void TypedElements_AreComparedUnderThePolicy(ulong reference, ulong actual, string policy, bool passes, long? distance, string? reason) {
    ExactnessPolicy chosen = policy switch {
      "exact" => new ExactnessPolicy.Exact(FloatFormat.F64),
      "exact bitwise" => new ExactnessPolicy.Exact(FloatFormat.F64) { SpecialValues = SpecialValues.Bitwise },
      "ulp class" => _ulp1 with { SpecialValues = SpecialValues.Class },
      _ => _ulp1
    };
    var outcome = CompareOne(F64(reference), F64(actual), chosen);
    Assert.That((outcome.Passed, outcome.UlpDistance, outcome.Mismatch?.Reason), Is.EqualTo((passes, distance, reason)));
  }

  /// <summary>Test design 11: the same NaN payloads as untyped bytes differ.</summary>
  [Test]
  public void NaNPayloads_AsBytes_AreAMismatch() {
    var outcome = CompareOne(F64(0x7FF8000000000000), F64(0x7FF8000000000001), _bytes);
    Assert.That((outcome.Passed, outcome.FirstDifferingOffset), Is.EqualTo((false, (int?)0)));
  }

  [Test]
  public void AToleranceFailure_IsPLY0052_NamingTheElement() {
    var report = DifferentialComparer.Compare(Named("sin"), [[.. F64(0x3FF0000000000000), .. F64(0x3FD3333333333333)]], [[.. F64(0x3FF0000000000000), .. F64(0x3FD3333333333335)]], _ulp1);
    var outcome = report.Outcomes.Single();
    Assert.That(outcome.Mismatch, Is.EqualTo(new ElementMismatch(1, "3fd3333333333333", "3fd3333333333335", "2 exceeds ulp 1")));
    var diagnostic = report.Diagnostics("math.sin").Single();
    Assert.That((diagnostic.Code, diagnostic.ExitCode), Is.EqualTo(("PLY0052", (ExitCode?)ExitCode.SemanticMismatch)));
    Assert.That(diagnostic.Message, Is.EqualTo("'math.sin' vector 'sin' element 1: expected 3fd3333333333333, actual 3fd3333333333335, 2 exceeds ulp 1"));
  }

  [Test]
  public void F32Elements_AreFourBytesEach() {
    var policy = new ExactnessPolicy.Relative(1e-6, FloatFormat.F32, reason: "r");
    var passing = CompareOne([.. F32(0x3F800000), .. F32(0x40000000)], [.. F32(0x3F800001), .. F32(0x40000000)], policy);
    Assert.That((passing.Passed, passing.UlpDistance, passing.Identical), Is.EqualTo((true, (long?)1, false)));
    var failing = CompareOne(F32(0x3F800000), F32(0x3F800100), policy);
    Assert.That(failing.Mismatch!.ReferenceHex, Is.EqualTo("3f800000"));
    Assert.That(failing.Mismatch.Reason, Does.EndWith("exceeds relative 1E-06"));
  }

  [Test]
  public void TheLargestDistance_OfThePassingElements_IsReported() {
    var policy = new ExactnessPolicy.Ulp(5, FloatFormat.F64, policy: "VERIFICATION.tolerance.cbrt");
    var report = DifferentialComparer.Compare(Named("v"), [[.. F64(0x4000000000000000), .. F64(0x4000000000000000), .. F64(0x7FF8000000000000)]],
      [[.. F64(0x4000000000000003), .. F64(0x4000000000000001), .. F64(0x7FF8000000000000)]], policy);
    Assert.That((report.Outcomes[0].UlpDistance, report.BoundKind, report.Policy), Is.EqualTo(((long?)3, BoundKind.Derived, (ExactnessPolicy?)policy)));
  }

  [Test]
  public void ADistanceBeyondLong_Saturates() {
    var outcome = CompareOne(F64(0xFFEFFFFFFFFFFFFF), F64(0x7FEFFFFFFFFFFFFF), _ulp1);
    Assert.That(outcome.UlpDistance, Is.EqualTo(long.MaxValue));
  }

  [Test]
  public void OutputsOfAnotherCount_AreRefused() {
    Assert.That(() => DifferentialComparer.Compare(Named("x"), [], [[1]], _bytes), Throws.ArgumentException);
    Assert.That(() => DifferentialComparer.Compare(Named("x"), [[1]], [], _bytes), Throws.ArgumentException);
    Assert.That(() => DifferentialComparer.Compare(null!, [], [], _bytes), Throws.ArgumentNullException);
    Assert.That(() => DifferentialComparer.Compare([], null!, [], _bytes), Throws.ArgumentNullException);
    Assert.That(() => DifferentialComparer.Compare([], [], null!, _bytes), Throws.ArgumentNullException);
    Assert.That(() => DifferentialComparer.Compare([], [], [], null!), Throws.ArgumentNullException);
  }

  [Test]
  public void TestVector_RefusesNull() {
    Assert.That(() => new TestVector(null!, [], null), Throws.ArgumentNullException);
    Assert.That(() => new TestVector("x", null!, null), Throws.ArgumentNullException);
  }

}
