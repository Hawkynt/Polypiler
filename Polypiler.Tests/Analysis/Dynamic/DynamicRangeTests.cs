using NUnit.Framework;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.Analysis.Range;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Analysis.Dynamic;

/// <summary>
/// The range facts of exact integer elements (#1591, ADR 0300 decision 2, level 1 "with the range
/// facts of Range/"): a range is kept only for an integer type and only where it says more than the
/// type, joins to the hull, meets to the intersection, is left out of what tells kinds apart, and is
/// dropped by the widening where it grew, so every chain of widenings ends.
/// </summary>
[TestFixture]
public sealed class DynamicRangeTests {

  private static readonly IJoinPolicy Vb = DistinctJoins.Instance;

  private static DynamicExact R(IrType type, long min, long max) => (DynamicExact)DynamicType.Exact(type, null, new ValueRange(min, max));

  [Test]
  public void Exact_KeepsARangeOnlyWhereItSaysMoreThanTheType() {
    Assert.That(R(IrType.I16, 1, 10).Range, Is.EqualTo(new ValueRange(1, 10)));
    Assert.That(((DynamicExact)DynamicType.Exact(IrType.I16, null, new ValueRange(short.MinValue, short.MaxValue))).Range, Is.Null, "the whole type");
    Assert.That(R(IrType.I16, -100000, 5).Range, Is.EqualTo(new ValueRange(short.MinValue, 5)), "clipped to the type");
    Assert.That(((DynamicExact)DynamicType.Exact(IrType.F64, null, new ValueRange(1, 2))).Range, Is.Null, "no integer");
    Assert.That(DynamicType.Exact(IrType.I16, null, new ValueRange(40000, 50000)), Is.EqualTo(DynamicType.Nothing), "outside the type");
    Assert.That(DynamicType.Exact(IrType.I16, null, ValueRange.Unreachable), Is.EqualTo(DynamicType.Nothing), "empty");
    Assert.That(DynamicType.Exact(IrType.F64, null, ValueRange.Unreachable), Is.EqualTo(DynamicType.Exact(IrType.F64)), "an empty range of no integer says nothing");
    Assert.That(DynamicType.Exact(IrType.I16, null, null), Is.EqualTo(DynamicType.Exact(IrType.I16)));
    Assert.That(R(IrType.I16, 1, 10).ToString(), Is.EqualTo("i16"), "a range names no kind");
    Assert.That(() => DynamicType.Exact(IrType.Variant, null, new ValueRange(1, 2)), Throws.ArgumentException);
  }

  [Test]
  public void Ranges_JoinToTheHull_AndMeetToTheIntersection() {
    Assert.That(DynamicType.Join(R(IrType.I16, 1, 3), R(IrType.I16, 7, 9), Vb), Is.EqualTo(R(IrType.I16, 1, 9)));
    Assert.That(DynamicType.Join(R(IrType.I16, 1, 3), DynamicType.Exact(IrType.I16), Vb), Is.EqualTo(DynamicType.Exact(IrType.I16)), "with every value");
    Assert.That(DynamicType.Join(R(IrType.I16, 1, 3), R(IrType.I32, 7, 9), Vb), Is.EqualTo(DynamicType.Union(Vb, R(IrType.I16, 1, 3), R(IrType.I32, 7, 9))), "two kinds stay two");
    Assert.That(DynamicType.Meet(R(IrType.I16, 1, 8), R(IrType.I16, 5, 9), Vb), Is.EqualTo(R(IrType.I16, 5, 8)));
    Assert.That(DynamicType.Meet(R(IrType.I16, 1, 3), R(IrType.I16, 5, 9), Vb), Is.EqualTo(DynamicType.Nothing), "disjoint");
    Assert.That(DynamicType.Meet(R(IrType.I16, 1, 3), DynamicType.Exact(IrType.I16), Vb), Is.EqualTo(R(IrType.I16, 1, 3)));
    Assert.That(DynamicType.Meet(DynamicType.Exact(IrType.I16), R(IrType.I16, 1, 3), Vb), Is.EqualTo(R(IrType.I16, 1, 3)));
    Assert.That(R(IrType.I16, 2, 3).LessOrEqual(R(IrType.I16, 1, 9), Vb), Is.True);
    Assert.That(R(IrType.I16, 0, 3).LessOrEqual(R(IrType.I16, 1, 9), Vb), Is.False);
  }

  [Test]
  public void Plain_LeavesTheRangesOut() {
    var union = DynamicType.Union(Vb, R(IrType.I16, 1, 3), DynamicType.Exact(IrType.String));
    Assert.That(R(IrType.I16, 1, 3).Plain, Is.EqualTo(DynamicType.Exact(IrType.I16)));
    Assert.That(union.Plain, Is.EqualTo(DynamicType.Union(Vb, DynamicType.Exact(IrType.I16), DynamicType.Exact(IrType.String))));
    var plain = DynamicType.Union(Vb, DynamicType.Exact(IrType.I16), DynamicType.Exact(IrType.String));
    Assert.That(plain.Plain, Is.SameAs(plain));
    Assert.That(DynamicType.Optional(R(IrType.I16, 1, 3), DynamicMissing.Null).Plain, Is.EqualTo(DynamicType.Optional(DynamicType.Exact(IrType.I16), DynamicMissing.Null)));
    Assert.That(DynamicType.Variant.Plain, Is.SameAs(DynamicType.Variant));
    var exact = DynamicType.Exact(IrType.I16);
    Assert.That(exact.Plain, Is.SameAs(exact));
  }

  [Test]
  public void Widening_DropsARangeThatGrew() {
    Assert.That(DynamicType.WidenRanges(R(IrType.I16, 1, 1), R(IrType.I16, 1, 2)), Is.EqualTo(DynamicType.Exact(IrType.I16)));
    Assert.That(DynamicType.WidenRanges(R(IrType.I16, 1, 9), R(IrType.I16, 2, 3)), Is.EqualTo(R(IrType.I16, 2, 3)), "covered: kept");
    Assert.That(DynamicType.WidenRanges(DynamicType.Exact(IrType.I16), R(IrType.I16, 2, 3)), Is.EqualTo(R(IrType.I16, 2, 3)), "below every value");
    Assert.That(DynamicType.WidenRanges(R(IrType.I16, 1, 1), DynamicType.Exact(IrType.I16)), Is.EqualTo(DynamicType.Exact(IrType.I16)));
    var union = DynamicType.Union(Vb, R(IrType.I16, 1, 2), DynamicType.Exact(IrType.String));
    Assert.That(DynamicType.WidenRanges(R(IrType.I16, 1, 1), union), Is.EqualTo(DynamicType.Union(Vb, DynamicType.Exact(IrType.I16), DynamicType.Exact(IrType.String))));
    var optional = DynamicType.Optional(R(IrType.I16, 1, 2), DynamicMissing.Empty);
    Assert.That(DynamicType.WidenRanges(DynamicType.Optional(R(IrType.I16, 1, 2), DynamicMissing.Empty), optional), Is.EqualTo(optional));
    Assert.That(DynamicType.WidenRanges(R(IrType.I32, 1, 1), R(IrType.I16, 1, 2)), Is.EqualTo(DynamicType.Exact(IrType.I16)), "a kind it did not hold");
    Assert.That(DynamicType.WidenRanges(DynamicType.Nothing, DynamicType.Variant), Is.SameAs(DynamicType.Variant));
    Assert.That(() => DynamicType.WidenRanges(null!, DynamicType.Variant), Throws.ArgumentNullException);
    Assert.That(() => DynamicType.WidenRanges(DynamicType.Variant, null!), Throws.ArgumentNullException);
    var domain = new DynamicFacts(Vb);
    Assert.That(domain.Widen(DynamicFact.Proven(R(IrType.I16, 1, 1)), DynamicFact.Proven(R(IrType.I16, 2, 2))), Is.EqualTo(DynamicFact.Proven(DynamicType.Exact(IrType.I16))));
  }

}
