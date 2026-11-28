using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Analysis.Dynamic;

/// <summary>
/// The lattice of dynamic places (#1581, ADR 0300 decision 2): the joins of the issue's table, the
/// bound of a union, the identity and the absorbing element, certainty, the meet, the types of the IR
/// the elements become, and the lattice laws - commutative, associative and idempotent joins, a meet
/// below both, a monotone widening - exhaustively over every pair and triple of a generated set.
/// </summary>
[TestFixture]
public sealed class DynamicTypeTests {

  /// <summary>JavaScript's rule: every two numbers are one Number, which the wider type holds.</summary>
  internal sealed class NumberJoins : IJoinPolicy {

    public static NumberJoins Instance { get; } = new();

    private static readonly IrType[] _ranks = [IrType.I16, IrType.I32, IrType.F64];

    public IrType? Join(IrType left, IrType right) {
      var a = Array.IndexOf(_ranks, left);
      var b = Array.IndexOf(_ranks, right);
      return a < 0 || b < 0 ? null : _ranks[Math.Max(a, b)];
    }

  }

  private static readonly IJoinPolicy Js = NumberJoins.Instance;

  private static readonly IJoinPolicy Vb = DistinctJoins.Instance;

  private static DynamicType E(IrType type) => DynamicType.Exact(type);

  private static DynamicType S(params (string Name, DynamicType Type)[] fields) => DynamicType.Shape(fields.ToDictionary(field => field.Name, field => field.Type));

  private static readonly IrStructType _point = Point();

  private static IrStructType Point() {
    var point = new IrStructType("Point");
    point.Define([new IrField("x", IrType.I32, false)]);
    return point;
  }

  [Test]
  public void Joins_GoUp_AsTheIssueTableSays() {
    Assert.That(DynamicType.Join(E(IrType.I32), E(IrType.I32), Js), Is.EqualTo(E(IrType.I32)));
    Assert.That(DynamicType.Join(E(IrType.I32), E(IrType.F64), Js), Is.EqualTo(E(IrType.F64)));
    var union = DynamicType.Join(E(IrType.I16), E(IrType.I32), Vb);
    Assert.That(union, Is.InstanceOf<DynamicUnion>());
    Assert.That(union.ToString(), Is.EqualTo("union<i16, i32>"));
    Assert.That(DynamicType.Join(S(("x", E(IrType.I32))), S(("x", E(IrType.F64))), Js), Is.EqualTo(S(("x", E(IrType.F64)))));
  }

  [Test]
  public void Shapes_WithOtherFieldNames_AreAUnion() {
    var joined = DynamicType.Join(S(("x", E(IrType.I32))), S(("y", E(IrType.I32))), Js);
    Assert.That(joined, Is.InstanceOf<DynamicUnion>());
    Assert.That(((DynamicUnion)joined).Members, Has.Count.EqualTo(2));
    Assert.That(joined.ToString(), Is.EqualTo("union<{x: i32}, {y: i32}>"));
  }

  [Test]
  public void Optional_AbsorbsTheValue() {
    var optional = DynamicType.Optional(E(IrType.I32), DynamicMissing.Null);
    Assert.That(DynamicType.Join(E(IrType.I32), optional, Vb), Is.EqualTo(optional));
    Assert.That(DynamicType.Join(optional, E(IrType.F64), Js), Is.EqualTo(DynamicType.Optional(E(IrType.F64), DynamicMissing.Null)));
    Assert.That(DynamicType.Join(optional, DynamicType.Optional(DynamicType.Nothing, DynamicMissing.Empty), Vb), Is.EqualTo(DynamicType.Optional(E(IrType.I32), DynamicMissing.Null | DynamicMissing.Empty)));
    Assert.That(DynamicType.Optional(E(IrType.I32), DynamicMissing.None), Is.EqualTo(E(IrType.I32)));
    Assert.That(DynamicType.Optional(DynamicType.Variant, DynamicMissing.Null), Is.EqualTo(DynamicType.Variant));
    Assert.That(DynamicType.Optional(optional, DynamicMissing.Empty), Is.EqualTo(DynamicType.Optional(E(IrType.I32), DynamicMissing.Null | DynamicMissing.Empty)));
    Assert.That(optional.ToString(), Is.EqualTo("optional<i32; Null>"));
  }

  [Test]
  public void Union_OfFourMembers_IsOne_AFifthWidensToVariant() {
    var four = DynamicType.Union(Vb, E(IrType.I32), E(IrType.String), E(IrType.Bool), E(IrType.F64));
    Assert.That(four, Is.InstanceOf<DynamicUnion>());
    Assert.That(((DynamicUnion)four).Members, Has.Count.EqualTo(DynamicType.MostMembers));
    Assert.That(DynamicType.Join(four, DynamicType.Nominal(_point), Vb), Is.EqualTo(DynamicType.Variant));
    Assert.That(DynamicType.Join(four, E(IrType.I32), Vb), Is.EqualTo(four), "a member already there adds none");
    Assert.That(DynamicType.Union(Vb), Is.EqualTo(DynamicType.Nothing));
  }

  [Test]
  public void Nothing_IsTheIdentity_VariantAbsorbs() {
    foreach (var element in Elements()) {
      Assert.That(DynamicType.Join(DynamicType.Nothing, element, Vb), Is.EqualTo(element));
      Assert.That(DynamicType.Join(element, DynamicType.Nothing, Vb), Is.EqualTo(element));
      Assert.That(DynamicType.Join(DynamicType.Variant, element, Vb), Is.EqualTo(DynamicType.Variant));
      Assert.That(DynamicType.Join(element, DynamicType.Variant, Js), Is.EqualTo(DynamicType.Variant));
      Assert.That(DynamicType.Meet(DynamicType.Variant, element, Vb), Is.EqualTo(element));
      Assert.That(DynamicType.Meet(element, DynamicType.Nothing, Vb), Is.EqualTo(DynamicType.Nothing));
    }
  }

  [Test]
  public void Certainty_JoinsToTheWeaker_AndMeetsToTheStronger() {
    var domain = new DynamicFacts(Vb);
    var proven = DynamicFact.Proven(E(IrType.I32));
    var guarded = new DynamicFact(E(IrType.I32), Certainty.Guarded);
    Assert.That(domain.Join(proven, guarded), Is.EqualTo(guarded));
    Assert.That(domain.Meet(proven, guarded), Is.EqualTo(proven));
    Assert.That(Certainty.Proven > Certainty.Contextual && Certainty.Contextual > Certainty.Guarded && Certainty.Guarded > Certainty.Dynamic, Is.True);
    Assert.That(proven.IsStatic, Is.True);
    Assert.That(guarded.IsStatic, Is.False);
    Assert.That(DynamicFact.Proven(DynamicType.Variant).IsStatic || DynamicFact.Proven(DynamicType.Nothing).IsStatic, Is.False);
    Assert.That(proven.ToString(), Is.EqualTo("proven i32"));
    Assert.That(domain.Implies(proven, guarded), Is.True);
    Assert.That(domain.Implies(guarded, proven), Is.False);
    Assert.That(domain.Implies(proven, DynamicFact.Proven(E(IrType.I16))), Is.False);
  }

  [Test]
  public void Domain_HasItsBoundsAndWords() {
    var domain = new DynamicFacts(Js);
    Assert.That(domain.Name, Is.EqualTo("dynamic"));
    Assert.That(domain.Policy, Is.SameAs(Js));
    Assert.That(domain.Unknown, Is.EqualTo(new DynamicFact(DynamicType.Variant, Certainty.Dynamic)));
    Assert.That(domain.Unreachable, Is.EqualTo(DynamicFact.Proven(DynamicType.Nothing)));
    Assert.That(domain.Describe(domain.Unknown), Is.EqualTo("dynamic variant"));
    Assert.That(domain.Join(domain.Unreachable, DynamicFact.Proven(E(IrType.I32))), Is.EqualTo(DynamicFact.Proven(E(IrType.I32))));
    Assert.That(() => new DynamicFacts(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Contains_RefutesOnlyWhatTheFactExcludes() {
    var domain = new DynamicFacts(Vb);
    var number = DynamicFact.Proven(DynamicType.Optional(DynamicType.Union(Vb, E(IrType.I32), DynamicType.Exact(IrType.I64, IrVariantTag.Currency)), DynamicMissing.Null | DynamicMissing.Empty));
    Assert.That(domain.Contains(number, new IrVariant(IrVariantTag.I32, 1)), Is.True);
    Assert.That(domain.Contains(number, new IrVariant(IrVariantTag.Currency, 1L)), Is.True);
    Assert.That(domain.Contains(number, new IrVariant(IrVariantTag.I64, 1L)), Is.False, "the tag is the kind, not the IR type");
    Assert.That(domain.Contains(number, IrVariant.Null) && domain.Contains(number, IrVariant.Empty), Is.True);
    Assert.That(domain.Contains(DynamicFact.Proven(E(IrType.I32)), IrVariant.Null), Is.False);
    Assert.That(domain.Contains(DynamicFact.Proven(E(IrType.I32)), new IrVariant(IrVariantTag.String, "a")), Is.False);
    Assert.That(domain.Contains(DynamicFact.Proven(DynamicType.Optional(E(IrType.I32), DynamicMissing.Null)), IrVariant.Empty), Is.False);
    Assert.That(domain.Contains(domain.Unknown, IrVariant.Empty), Is.True);
    Assert.That(domain.Contains(domain.Unreachable, IrVariant.Empty), Is.False);
    Assert.That(domain.Contains(DynamicFact.Proven(DynamicType.Nominal(_point)), IrVariant.Empty), Is.True);
    Assert.That(domain.Contains(number, new IrConst(IrType.I32, 1)), Is.True);
    Assert.That(domain.Contains(number, new IrConst(IrType.I16, 1)), Is.False);
    Assert.That(domain.Contains(domain.Unknown, new IrConst(IrType.I16, 1)), Is.True);
    Assert.That(domain.Contains(DynamicFact.Proven(DynamicType.Nominal(_point)), new IrConst(IrType.I16, 1)), Is.False);
    Assert.That(domain.Contains(DynamicFact.Proven(E(IrType.I32)), "anything"), Is.True);
    Assert.That(domain.Contains(domain.Unreachable, "anything"), Is.False);
  }

  [Test]
  public void Meet_IsWhatBothAllow() {
    Assert.That(DynamicType.Meet(DynamicType.Union(Vb, E(IrType.I32), E(IrType.String)), E(IrType.I32), Vb), Is.EqualTo(E(IrType.I32)));
    Assert.That(DynamicType.Meet(E(IrType.F64), E(IrType.I32), Js), Is.EqualTo(E(IrType.I32)));
    Assert.That(DynamicType.Meet(E(IrType.I32), E(IrType.F64), Js), Is.EqualTo(E(IrType.I32)));
    Assert.That(DynamicType.Meet(E(IrType.I32), E(IrType.F64), Vb), Is.EqualTo(DynamicType.Nothing));
    Assert.That(DynamicType.Meet(DynamicType.Optional(E(IrType.I32), DynamicMissing.Null), DynamicType.Optional(E(IrType.String), DynamicMissing.Null), Vb), Is.EqualTo(DynamicType.Optional(DynamicType.Nothing, DynamicMissing.Null)));
    Assert.That(DynamicType.Meet(S(("x", E(IrType.F64))), S(("x", E(IrType.I32))), Js), Is.EqualTo(S(("x", E(IrType.I32)))));
  }

  [Test]
  public void Elements_BecomeTypesOfTheIr() {
    var shape = new IrClassType("Shape");
    var contract = new IrInterfaceType("IShape");
    var colour = new IrEnumType("Colour", IrType.I32, [new("Red", 0)]);
    Assert.That(E(IrType.I32).ToIrType(), Is.EqualTo(IrType.I32));
    Assert.That(DynamicType.Nominal(_point).ToIrType(), Is.EqualTo(IrType.Struct(_point)));
    Assert.That(DynamicType.Nominal(shape).ToIrType(), Is.EqualTo(IrType.Object(shape)));
    Assert.That(DynamicType.Nominal(contract).ToIrType(), Is.EqualTo(IrType.Object(contract)));
    Assert.That(DynamicType.Nominal(colour).ToIrType(), Is.EqualTo(colour.Type));
    Assert.That(S(("x", E(IrType.I32))).ToIrType(), Is.Null);
    Assert.That(DynamicType.Optional(E(IrType.I32), DynamicMissing.Null).ToIrType(), Is.EqualTo(IrType.Optional(IrType.I32)));
    Assert.That(DynamicType.Optional(E(IrType.String), DynamicMissing.Undefined).ToIrType(), Is.EqualTo(IrType.String));
    Assert.That(DynamicType.Optional(E(IrType.Pointer(IrType.I32)), DynamicMissing.Null).ToIrType(), Is.Null, "a pointer has no optional type");
    Assert.That(DynamicType.Optional(E(IrType.I32), DynamicMissing.Null | DynamicMissing.Empty).ToIrType(), Is.Null, "two missing values are two values");
    Assert.That(DynamicType.Optional(DynamicType.Nothing, DynamicMissing.Null).ToIrType(), Is.Null);
    Assert.That(DynamicType.Union(Vb, E(IrType.I32), E(IrType.String)).ToIrType(), Is.EqualTo(IrType.Union(IrType.I32, IrType.String)));
    Assert.That(DynamicType.Union(Vb, E(IrType.I32), S(("x", E(IrType.I32)))).ToIrType(), Is.Null);
    Assert.That(DynamicType.Union(Vb, E(IrType.I32), E(IrType.Array(IrType.I32))).ToIrType(), Is.Null, "an array is no member of an IR union");
    Assert.That(DynamicType.Nothing.ToIrType() ?? DynamicType.Variant.ToIrType(), Is.Null);
    Assert.That(DynamicType.Exact(IrType.I64, IrVariantTag.Currency).ToString(), Is.EqualTo("i64 Currency"));
    Assert.That(DynamicType.Exact(IrType.I32, IrVariantTag.I32).ToString(), Is.EqualTo("i32"));
    Assert.That(DynamicType.Nominal(_point).ToString() + DynamicType.Nothing + DynamicType.Variant, Is.EqualTo("Pointnothingvariant"));
  }

  [Test]
  public void Levels_AreTheAdrs() {
    DynamicType[] elements = [DynamicType.Nothing, E(IrType.I32), DynamicType.Nominal(_point), S(("x", E(IrType.I32))),
      DynamicType.Optional(E(IrType.I32), DynamicMissing.Null), DynamicType.Union(Vb, E(IrType.I32), E(IrType.String)), DynamicType.Variant];
    Assert.That(elements.Select(element => element.Level), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6 }));
  }

  [Test]
  public void Widening_EndsEveryChainOfGrowingShapes() {
    var domain = new DynamicFacts(Js);
    var fact = DynamicFact.Proven(E(IrType.I32));
    var element = E(IrType.I32);
    for (var round = 0; round < 10; ++round) {
      element = S(("next", element));
      var widened = domain.Widen(fact, DynamicFact.Proven(element));
      Assert.That(domain.Implies(domain.Join(fact, DynamicFact.Proven(element)), widened), Is.True, "monotone: the widening is above the join");
      fact = widened;
    }

    Assert.That(fact.Type, Is.EqualTo(DynamicType.Variant));
    Assert.That(domain.Widen(DynamicFact.Proven(E(IrType.I16)), DynamicFact.Proven(E(IrType.I32))), Is.EqualTo(DynamicFact.Proven(E(IrType.I32))));
  }

  [Test]
  public void State_JoinsAndWidensPlaceByPlace() {
    var domain = new DynamicFacts(Vb);
    var x = new IrLocal("x", IrType.Variant);
    var y = new IrLocal("y", IrType.Variant);
    var empty = DynamicFact.Proven(DynamicType.Optional(DynamicType.Nothing, DynamicMissing.Empty));
    var before = new DynamicState<IrLocal>([new(x, DynamicFact.Proven(E(IrType.I32)))], empty);
    var after = before.With(y, DynamicFact.Proven(E(IrType.String)));
    Assert.That(before[y], Is.EqualTo(empty));
    Assert.That(after[y], Is.EqualTo(DynamicFact.Proven(E(IrType.String))));
    Assert.That(before.Places, Is.EqualTo(new[] { x }));
    var joined = before.Join(after, domain);
    Assert.That(joined[y], Is.EqualTo(DynamicFact.Proven(DynamicType.Optional(E(IrType.String), DynamicMissing.Empty))));
    Assert.That(joined[x], Is.EqualTo(DynamicFact.Proven(E(IrType.I32))));
    Assert.That(joined.Unbound, Is.EqualTo(empty));
    Assert.That(before.Join(after, domain, widen: true).SameAs(joined), Is.True);
    Assert.That(before.SameAs(after), Is.False);
    Assert.That(before.SameAs(before.With(x, DynamicFact.Proven(E(IrType.I32)))), Is.True);
    Assert.That(() => before.Join(null!, domain), Throws.ArgumentNullException);
    Assert.That(() => before.Join(after, null!), Throws.ArgumentNullException);
    Assert.That(() => before.SameAs(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void NullArguments_AreRefused() {
    Assert.That(() => DynamicType.Join(null!, E(IrType.I32), Vb), Throws.ArgumentNullException);
    Assert.That(() => DynamicType.Join(E(IrType.I32), null!, Vb), Throws.ArgumentNullException);
    Assert.That(() => DynamicType.Join(E(IrType.I32), E(IrType.I32), null!), Throws.ArgumentNullException);
    Assert.That(() => DynamicType.Meet(null!, E(IrType.I32), Vb), Throws.ArgumentNullException);
    Assert.That(() => DynamicType.Meet(E(IrType.I32), null!, Vb), Throws.ArgumentNullException);
    Assert.That(() => DynamicType.Meet(E(IrType.I32), E(IrType.I32), null!), Throws.ArgumentNullException);
    Assert.That(() => DynamicType.Exact(null!), Throws.ArgumentNullException);
    Assert.That(() => DynamicType.Exact(IrType.Variant), Throws.ArgumentException);
    Assert.That(() => DynamicType.Exact(IrType.Void), Throws.ArgumentException);
    Assert.That(() => DynamicType.Exact(IrType.Union(IrType.I32, IrType.String)), Throws.ArgumentException);
    Assert.That(() => DynamicType.Nominal(null!), Throws.ArgumentNullException);
    Assert.That(() => DynamicType.Shape(null!), Throws.ArgumentNullException);
    Assert.That(() => DynamicType.Shape(new Dictionary<string, DynamicType> { ["x"] = null! }), Throws.ArgumentNullException);
    Assert.That(() => DynamicType.Optional(null!, DynamicMissing.Null), Throws.ArgumentNullException);
    Assert.That(() => DynamicType.Union(Vb, null!), Throws.ArgumentNullException);
  }

  /// <summary>The generated set: 40 elements of every level, under both policies' interesting pairs.</summary>
  private static IReadOnlyList<DynamicType> Elements(IJoinPolicy? policy = null) {
    var vb = policy ?? Vb;
    var scalars = new[] { E(IrType.I16), E(IrType.I32), E(IrType.F64), E(IrType.String), E(IrType.Bool), DynamicType.Exact(IrType.I64, IrVariantTag.Currency) };
    var shapes = new[] { S(("x", E(IrType.I32))), S(("x", E(IrType.F64))), S(("y", E(IrType.String))), S(("x", E(IrType.I32)), ("y", E(IrType.Bool))) };
    var nominal = DynamicType.Nominal(_point);
    var elements = new List<DynamicType> { DynamicType.Nothing, DynamicType.Variant, nominal };
    elements.AddRange(scalars);
    elements.AddRange(shapes);
    foreach (var missing in new[] { DynamicMissing.Null, DynamicMissing.Empty, DynamicMissing.Null | DynamicMissing.Undefined })
      elements.Add(DynamicType.Optional(DynamicType.Nothing, missing));
    elements.AddRange(scalars.Take(4).Select(scalar => DynamicType.Optional(scalar, DynamicMissing.Null)));
    elements.Add(DynamicType.Optional(shapes[0], DynamicMissing.Empty));
    for (var a = 0; a < scalars.Length; ++a)
      for (var b = a + 1; b < scalars.Length && elements.Count < 34; b += 2)
        elements.Add(DynamicType.Union(vb, scalars[a], scalars[b]));
    elements.Add(DynamicType.Union(vb, scalars[0], scalars[3], shapes[0]));
    elements.Add(DynamicType.Union(vb, scalars[3], scalars[4], nominal, shapes[2]));
    elements.Add(DynamicType.Union(vb, scalars[1], scalars[3], scalars[4], scalars[5]));
    elements.Add(DynamicType.Optional(DynamicType.Union(vb, scalars[1], scalars[3]), DynamicMissing.Empty));
    elements.Add(S(("x", DynamicType.Union(vb, scalars[0], scalars[3]))));
    elements.Add(S(("x", DynamicType.Optional(scalars[2], DynamicMissing.Null))));
    elements.Add(DynamicType.Optional(nominal, DynamicMissing.Null));
    elements.Add(E(IrType.F32));
    elements.Add(DynamicType.Exact(IrType.F64, IrVariantTag.Date));
    elements.Add(S(("z", nominal)));
    return [.. elements.Distinct()];
  }

  [Test]
  public void TheGeneratedSet_HasFortyDistinctElements() {
    var elements = Elements();
    Assert.That(elements, Has.Count.EqualTo(40));
    Assert.That(elements.Distinct().Count(), Is.EqualTo(40));
    Assert.That(elements.Select(element => element.Level).Distinct().Order(), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6 }));
  }

  private static IEnumerable<TestCaseData> Policies() {
    yield return new TestCaseData(Vb).SetName("Laws_HoldExhaustively_UnderDistinctJoins");
    yield return new TestCaseData(Js).SetName("Laws_HoldExhaustively_UnderNumberJoins");
  }

  [TestCaseSource(nameof(Policies))]
  public void Laws_HoldExhaustively(IJoinPolicy policy) {
    var elements = Elements(policy);
    var domain = new DynamicFacts(policy);
    var pairs = 0;
    var triples = 0;
    foreach (var a in elements) {
      Assert.That(DynamicType.Join(a, a, policy), Is.EqualTo(a), $"idempotent: {a}");
      Assert.That(DynamicType.Meet(a, a, policy), Is.EqualTo(a), $"meet idempotent: {a}");
      foreach (var b in elements) {
        var ab = DynamicType.Join(a, b, policy);
        Assert.That(DynamicType.Join(b, a, policy), Is.EqualTo(ab), $"commutative: {a} and {b}");
        Assert.That(a.LessOrEqual(ab, policy) && b.LessOrEqual(ab, policy), Is.True, $"an upper bound: {a} and {b}");
        Assert.That(ab.GetHashCode(), Is.EqualTo(DynamicType.Join(b, a, policy).GetHashCode()));
        var meet = DynamicType.Meet(a, b, policy);
        Assert.That(meet.LessOrEqual(a, policy) && meet.LessOrEqual(b, policy), Is.True, $"a lower bound: {a} and {b} meet in {meet}");
        var widened = domain.Widen(DynamicFact.Proven(a), DynamicFact.Proven(b));
        Assert.That(ab.LessOrEqual(widened.Type, policy), Is.True, $"monotone widening: {a} and {b}");
        ++pairs;
        foreach (var c in elements) {
          Assert.That(DynamicType.Join(ab, c, policy), Is.EqualTo(DynamicType.Join(a, DynamicType.Join(b, c, policy), policy)), $"associative: {a}, {b} and {c}");
          ++triples;
        }
      }
    }

    Assert.That((pairs, triples), Is.EqualTo((elements.Count * elements.Count, elements.Count * elements.Count * elements.Count)));
    Assert.That(elements.Count, Is.GreaterThanOrEqualTo(policy == Vb ? 40 : 35));
  }

}
