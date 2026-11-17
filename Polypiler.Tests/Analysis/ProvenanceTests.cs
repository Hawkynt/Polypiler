using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis;
using Polypiler.Core.Analysis.Checking;
using Polypiler.Core.Analysis.Provenance;
using Polypiler.Core.Analysis.Range;
using Polypiler.Core.IR;
using static Polypiler.Tests.Analysis.AnalysisIr;

namespace Polypiler.Tests.Analysis;

/// <summary>Pointer provenance (#93): the sites and offsets of the bounded pointers, and the in-bounds query; the rows of the issue's test design are named in each test.</summary>
[TestFixture]
public sealed class ProvenanceTests {

  private static IrType P(IrType element) => IrType.Pointer(element);

  private static IrExpr Alloc(IrType element, int count) => new IrIntrinsic(IrIntrinsicOp.StackAlloc, P(element), [new IrConst(IrType.I32, count)]);

  private static IrExpr Add(IrExpr pointer, IrExpr count) => new IrIntrinsic(IrIntrinsicOp.PointerAdd, pointer.Type, [pointer, count]);

  private static IrExpr Add(IrExpr pointer, int count) => Add(pointer, new IrConst(IrType.I32, count));

  private static IrLocal Ptr(string name, IrType element) => new(name, P(element));

  /// <summary>A function whose last statement returns 0; facts are asked before it.</summary>
  private static (ValueFacts Facts, ProgramPoint End) Analysed(IrLocal[] parameters, params IrStmt[] body) {
    var function = Function(parameters, [.. body, new IrReturn(K(0))]);
    return (new ValueFacts(function).Add(new ProvenanceAnalysis(function)), new ProgramPoint(function.Body, body.Length));
  }

  private static string Describe(ValueFacts facts, IrLocal local, ProgramPoint point) => facts.ProvenanceAt(R(local), point).ToString();

  /// <summary>Test design 1: constant offsets add up on one site.</summary>
  [Test]
  public void PointerArithmetic_KeepsTheSiteAndTracksTheOffset() {
    var (a, p, q) = (Ptr("a", IrType.U8), Ptr("p", IrType.U8), Ptr("q", IrType.U8));
    var (facts, end) = Analysed([], new IrDeclare(a, Alloc(IrType.U8, 64)), new IrDeclare(p, Add(R(a), 16)), new IrDeclare(q, Add(R(p), 4)));
    Assert.That(Describe(facts, q, end), Is.EqualTo("{a} offset [20, 20]"));
    Assert.That(facts.ProvenanceAt(R(q), end).IsSingleSite, Is.True);
  }

  /// <summary>Test design 2 and the scenarios of the ranged index: a mask bounds the offset; in bounds when it fits, Unknown when it may not.</summary>
  [TestCase(7, "{a} offset [0, 28]", Truth.True)]
  [TestCase(31, "{a} offset [0, 124]", Truth.Unknown)]
  public void ARangedIndex_GivesAnOffsetInterval(int mask, string expected, Truth inBounds) {
    var (a, p, i) = (Ptr("a", IrType.U32), Ptr("p", IrType.U32), new IrLocal("i", IrType.U32));
    var (facts, end) = Analysed([X], new IrDeclare(a, Alloc(IrType.U32, 16)), new IrDeclare(i, Op(IrBinaryOp.And, R(X), K(mask))), new IrDeclare(p, Add(R(a), R(i))));
    Assert.That(Describe(facts, p, end), Is.EqualTo(expected));
    Assert.That(facts.IsInBounds(R(p), 4, end), Is.EqualTo(inBounds));
  }

  /// <summary>Test design 3 and 4: the last valid access, one byte beyond, one past the end, before the start and back again.</summary>
  [TestCase(4, 4, Truth.True)]
  [TestCase(5, 4, Truth.False)]
  [TestCase(8, 1, Truth.False)]
  [TestCase(-4, 1, Truth.False)]
  [TestCase(0, 8, Truth.True)]
  [TestCase(0, 9, Truth.False)]
  public void TheBoundsOfOneSite(int offset, int access, Truth expected) {
    var (a, p) = (Ptr("a", IrType.U8), Ptr("p", IrType.U8));
    var (facts, end) = Analysed([], new IrDeclare(a, Alloc(IrType.U8, 8)), new IrDeclare(p, Add(R(a), offset)));
    Assert.That(facts.IsInBounds(R(p), access, end), Is.EqualTo(expected));
  }

  /// <summary>Test design 4: eight forward and four back is offset 4.</summary>
  [Test]
  public void NegativeOffsets_MoveBack() {
    var (a, p) = (Ptr("a", IrType.U8), Ptr("p", IrType.U8));
    var (facts, end) = Analysed([], new IrDeclare(a, Alloc(IrType.U8, 8)), new IrDeclare(p, Add(Add(R(a), 8), -4)));
    Assert.That(Describe(facts, p, end), Is.EqualTo("{a} offset [4, 4]"));
  }

  /// <summary>Test design 5 and the phi scenario: branches unite their sites up to four; a fifth gives them up.</summary>
  [TestCase(2, "{a0, a1} offset [0, 0]")]
  [TestCase(4, "{a0, a1, a2, a3} offset [0, 0]")]
  [TestCase(5, "unknown")]
  public void BranchesUniteTheirSites(int sites, string expected) {
    var p = Ptr("p", IrType.U8);
    var allocations = Enumerable.Range(0, sites).Select(index => Ptr("a" + index, IrType.U8)).ToList();
    var body = new List<IrStmt>(allocations.Select(allocation => new IrDeclare(allocation, Alloc(IrType.U8, 8))));
    body.Add(new IrDeclare(p, R(allocations[0])));
    body.AddRange(allocations.Skip(1).Select((allocation, index) => new IrIf(Op(IrBinaryOp.Equal, R(X), K(index)), Block(new IrAssign(p, R(allocation))), null)));
    var (facts, end) = Analysed([X], [.. body]);
    Assert.That(Describe(facts, p, end), Is.EqualTo(expected));
  }

  /// <summary>Test design 6: no access through a zero-sized allocation is in bounds.</summary>
  [Test]
  public void AZeroSizedAllocation_HoldsNoAccess() {
    var a = Ptr("a", IrType.U8);
    var (facts, end) = Analysed([], new IrDeclare(a, Alloc(IrType.U8, 0)));
    Assert.That(facts.IsInBounds(R(a), 1, end), Is.EqualTo(Truth.False));
  }

  /// <summary>Test design 7: each source the bounded pointers have - stackalloc, pin, pin of an element, address of a local, null, a parameter.</summary>
  [Test]
  public void EachSource_HasItsFact() {
    var array = new IrLocal("arr", IrType.Array(IrType.U32));
    var (pin, element, address, none, unknown) = (Ptr("pin", IrType.U32), Ptr("e", IrType.U32), Ptr("addr", IrType.U32), Ptr("none", IrType.U32), Ptr("param", IrType.U32));
    var count = new IrLocal("n", IrType.I32);
    var dynamic = Ptr("dyn", IrType.U32);
    var (facts, end) = Analysed([array, unknown, count],
      new IrDeclare(pin, new IrIntrinsic(IrIntrinsicOp.Pin, P(IrType.U32), [R(array)])),
      new IrDeclare(element, new IrIntrinsic(IrIntrinsicOp.Pin, P(IrType.U32), [R(array), new IrConst(IrType.I32, 2)])),
      new IrDeclare(Y, K(1)),
      new IrDeclare(address, new IrIntrinsic(IrIntrinsicOp.AddressOf, P(IrType.U32), [R(Y)])),
      new IrDeclare(none, new IrNull(P(IrType.U32))),
      new IrDeclare(dynamic, new IrIntrinsic(IrIntrinsicOp.StackAlloc, P(IrType.U32), [R(count)])));
    Assert.That(Describe(facts, pin, end), Is.EqualTo("{pin} offset [0, 0] or null"));
    Assert.That(facts.IsInBounds(R(pin), 4, end), Is.EqualTo(Truth.Unknown));
    Assert.That(Describe(facts, element, end), Is.EqualTo("{e} offset [8, 8]"));
    Assert.That(facts.IsInBounds(R(element), 4, end), Is.EqualTo(Truth.Unknown));
    Assert.That(Describe(facts, address, end), Is.EqualTo("{addr} offset [0, 0]"));
    Assert.That(facts.IsInBounds(R(address), 4, end), Is.EqualTo(Truth.True));
    Assert.That(facts.IsInBounds(R(address), 5, end), Is.EqualTo(Truth.False));
    Assert.That(Describe(facts, none, end), Is.EqualTo("null offset [0, 0]"));
    Assert.That(facts.IsInBounds(R(none), 1, end), Is.EqualTo(Truth.False));
    Assert.That(Describe(facts, unknown, end), Is.EqualTo("unknown"));
    Assert.That(facts.IsInBounds(R(unknown), 1, end), Is.EqualTo(Truth.Unknown));
    Assert.That(facts.IsInBounds(R(dynamic), 1, end), Is.EqualTo(Truth.Unknown));
    Assert.That(facts.ProvenanceAt(new IrIntrinsic(IrIntrinsicOp.PointerCast, P(IrType.U8), [R(address)]), end).ToString(), Is.EqualTo("{addr} offset [0, 0]"));
    Assert.That(facts.ProvenanceAt(new IrConditional(new IrConst(IrType.Bool, 1), R(address), R(none)), end).ToString(), Is.EqualTo("{addr} offset [0, 0] or null"));
  }

  /// <summary>Test design 9: element addressing scales the index range by the element size.</summary>
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(4)]
  [TestCase(8)]
  public void ElementAddressing_ScalesByTheElementSize(int size) {
    var element = size switch { 1 => IrType.U8, 2 => IrType.U16, 4 => IrType.U32, _ => IrType.U64 };
    var (a, p, i) = (Ptr("a", element), Ptr("p", element), new IrLocal("i", IrType.U32));
    var (facts, end) = Analysed([X], new IrDeclare(a, Alloc(element, 4)), new IrDeclare(i, Op(IrBinaryOp.And, R(X), K(3))), new IrDeclare(p, Add(R(a), R(i))));
    Assert.That(facts.ProvenanceAt(R(p), end).Offset, Is.EqualTo(new ValueRange(0, 3 * size)));
    Assert.That(facts.IsInBounds(R(p), size, end), Is.EqualTo(Truth.True));
  }

  /// <summary>
  /// Test design 8 and 10: a pointer bumped in a loop keeps its site; the widened offset leaves 64 bits and is
  /// Unknown, where the issue's <c>[0, 32]</c> needs a trip count the walk does not narrow by yet.
  /// </summary>
  [Test]
  public void ALoopBump_KeepsTheSiteAndLosesTheOffset() {
    var (a, p, i) = (Ptr("a", IrType.U32), Ptr("p", IrType.U32), new IrLocal("i", IrType.I32));
    var body = Block(new IrAssign(p, Add(R(p), 1)));
    var (facts, end) = Analysed([],
      new IrDeclare(a, Alloc(IrType.U32, 8)),
      new IrDeclare(p, R(a)),
      new IrDeclare(i, new IrConst(IrType.I32, 0)),
      new IrFor(Op(IrBinaryOp.Less, R(i), new IrConst(IrType.I32, 8)), body, Block(new IrAssign(i, Op(IrBinaryOp.Add, R(i), new IrConst(IrType.I32, 1))))));
    var fact = facts.ProvenanceAt(R(p), end);
    Assert.That(fact.Sites.Select(site => site.Name), Is.EqualTo(new[] { "a" }));
    Assert.That(fact.Offset.IsUnknown, Is.True);
    Assert.That(facts.IsInBounds(R(p), 4, end), Is.EqualTo(Truth.Unknown));
    Assert.That(fact.ToString(), Is.EqualTo("{a} offset unknown"));
  }

  /// <summary>Code the ranges prove unreachable but the provenance walks gives its offsets up rather than inventing them.</summary>
  [Test]
  public void AnOffsetWhereTheRangesSeeNoValue_IsUnknown() {
    var (a, p) = (Ptr("a", IrType.U8), Ptr("p", IrType.U8));
    var then = Block(new IrAssign(p, Add(R(a), R(X))));
    var (facts, _) = Analysed([X], new IrDeclare(a, Alloc(IrType.U8, 8)), new IrDeclare(p, R(a)), new IrIf(Op(IrBinaryOp.Less, R(X), K(0)), then, null));
    Assert.That(Describe(facts, p, ProgramPoint.End(then)), Is.EqualTo("{a} offset unknown"));
  }

  /// <summary>Without a provenance provider the facade answers Unknown; the access size has to be positive.</summary>
  [Test]
  public void WithoutAProvider_ProvenanceIsUnknown() {
    var a = Ptr("a", IrType.U8);
    var function = Function([], new IrDeclare(a, Alloc(IrType.U8, 8)), new IrReturn(K(0)));
    var facts = new ValueFacts(function);
    var point = new ProgramPoint(function.Body, 1);
    Assert.That(facts.ProvenanceAt(R(a), point), Is.EqualTo(Provenance.Unknown));
    Assert.That(facts.IsInBounds(R(a), 1, point), Is.EqualTo(Truth.Unknown));
    Assert.That(() => ProvenanceAnalysis.InBounds(Provenance.Unknown, 0), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => ProvenanceFacts.ProvenanceAt(null!, R(a), point), Throws.ArgumentNullException);
    Assert.That(ProvenanceAnalysis.InBounds(Provenance.Unreachable, 1), Is.EqualTo(Truth.Unknown));
  }

  /// <summary>The lattice of provenance facts.</summary>
  [Test]
  public void TheProvenanceDomain_IsALattice() {
    var domain = ProvenanceDomain.Instance;
    var nodes = Enumerable.Range(0, 6).Select(index => new IrIntrinsic(IrIntrinsicOp.StackAlloc, P(IrType.U8), [new IrConst(IrType.I32, index)])).ToList();
    var sites = nodes.Select((node, index) => new AllocationSite(node, AllocationKind.StackAlloc, index, "s" + index)).ToList();
    var (one, two) = (Provenance.Into(sites[0], new ValueRange(0, 4)), Provenance.Into(sites[1], new ValueRange(2, 8)));
    Assert.That(domain.Join(one, two).ToString(), Is.EqualTo("{s0, s1} offset [0, 8]"));
    Assert.That(domain.Join(one, two), Is.EqualTo(domain.Join(two, one)));
    Assert.That(domain.Join(one, domain.Unreachable), Is.EqualTo(one));
    Assert.That(domain.Join(domain.Unreachable, one), Is.EqualTo(one));
    Assert.That(domain.Join(one, domain.Unknown), Is.EqualTo(domain.Unknown));
    Assert.That(domain.Join(one, Provenance.Null).ToString(), Is.EqualTo("{s0} offset [0, 4] or null"));
    Assert.That(domain.Meet(domain.Join(one, two), one), Is.EqualTo(one));
    Assert.That(domain.Meet(one, two), Is.EqualTo(domain.Unreachable));
    Assert.That(domain.Meet(one, Provenance.Into(sites[0], new ValueRange(5, 6))), Is.EqualTo(domain.Unreachable));
    Assert.That(domain.Meet(domain.Unknown, one), Is.EqualTo(one));
    Assert.That(domain.Meet(one, domain.Unknown), Is.EqualTo(one));
    Assert.That(domain.Meet(domain.Unreachable, one), Is.EqualTo(domain.Unreachable));
    Assert.That(domain.Meet(one, domain.Unreachable), Is.EqualTo(domain.Unreachable));
    Assert.That(domain.Meet(domain.Join(one, Provenance.Null), Provenance.Null), Is.EqualTo(Provenance.Null));
    Assert.That(domain.Widen(one, Provenance.Into(sites[0], new ValueRange(0, 5))).Offset.Max, Is.EqualTo(Int128.MaxValue));
    Assert.That(domain.Implies(one, domain.Join(one, two)) && !domain.Implies(two, one), Is.True);
    Assert.That(new Provenance([.. sites], new ValueRange(0, 0), false), Is.EqualTo(Provenance.Unknown));
    Assert.That(new Provenance(default, new ValueRange(0, 0), false).MayBeNull, Is.True);
    Assert.That(Provenance.Unknown.GetHashCode(), Is.EqualTo(new Provenance(default, ValueRange.Of(3), false).GetHashCode()));
    Assert.That(domain.Describe(domain.Unreachable) + "," + domain.Describe(domain.Unknown), Is.EqualTo("unreachable,unknown"));
    Assert.That(Provenance.Domain, Is.SameAs(domain));
    Assert.That(sites[0], Is.Not.EqualTo(new AllocationSite(nodes[1], AllocationKind.StackAlloc, 0, "s0")));
    Assert.That(sites[0], Is.EqualTo(sites[0] with { Name = "other" }));
    Assert.That(sites[0].Equals(null), Is.False);
    Assert.That(sites[0].ToString(), Is.EqualTo("s0"));
    Assert.That(sites[0].GetHashCode(), Is.EqualTo((sites[0] with { Size = 9 }).GetHashCode()));
  }

  /// <summary>What a fact admits of an observed pointer: its offsets, the size of a site, the null pointer.</summary>
  [Test]
  public void Contains_ChecksOffsetSizeAndNull() {
    var domain = ProvenanceDomain.Instance;
    var node = new IrIntrinsic(IrIntrinsicOp.StackAlloc, P(IrType.U8), [new IrConst(IrType.I32, 8)]);
    var sized = Provenance.Into(new AllocationSite(node, AllocationKind.StackAlloc, 8, "a"), new ValueRange(0, 4));
    var unsized = Provenance.Into(new AllocationSite(node, AllocationKind.Pin, null, "a"), new ValueRange(0, 4));
    var region = new object();
    Assert.That(domain.Contains(sized, new IrObservedPointer(region, 4, 8)), Is.True);
    Assert.That(domain.Contains(sized, new IrObservedPointer(region, 5, 8)), Is.False);
    Assert.That(domain.Contains(sized, new IrObservedPointer(region, 4, 16)), Is.False);
    Assert.That(domain.Contains(unsized, new IrObservedPointer(region, 4, 16)), Is.True);
    Assert.That(domain.Contains(sized, new IrObservedPointer(null, 0, 0)), Is.False);
    Assert.That(domain.Contains(Provenance.Null, new IrObservedPointer(null, 0, 0)), Is.True);
    Assert.That(domain.Contains(Provenance.Unknown, 5), Is.True);
    Assert.That(domain.Contains(sized, new IrConst(IrType.U32, 0)), Is.False);
    Assert.That(domain.Contains(Provenance.Unreachable, new IrObservedPointer(region, 0, 8)), Is.False);
  }

  /// <summary>Test design 12: no fact refuted on 25 seeded pointer functions (the full 2 000 of seed 310 run in the slow tier).</summary>
  [Test]
  public void ProvenanceIsSoundOnRandomPointerFunctions() => AssertSound(310, 25);

  /// <summary>Test design 12 in full: 2 000 seeded pointer functions, seed 310.</summary>
  [Test, Category("Slow")]
  public void ProvenanceIsSoundOnTwoThousandRandomPointerFunctions() => AssertSound(310, 2_000);

  private static void AssertSound(int seed, int functions) {
    var violations = new List<string>();
    var known = 0;
    for (var index = 0; index < functions; ++index) {
      var function = RandomPointerIr.Generate(seed + index);
      var facts = new ValueFacts(function).Add(new ProvenanceAnalysis(function)).Add(new RangeAnalysis(function));
      var locals = function.Parameters.Concat(function.Locals).ToList();
      foreach (var block in Blocks(function))
        for (var position = 0; position <= block.Statements.Count; ++position) {
          var point = new ProgramPoint(block, position);
          foreach (var local in locals.Where(local => facts.Layout.Defined(local, point))) {
            if (local.Type.IsPointer)
              known += facts.Get<Provenance>(local, point).IsUnknown ? 0 : 1;
            else
              facts.Get<ValueRange>(local, point);
          }
        }

      var random = new Random(seed + index);
      object[][] inputs = [.. Enumerable.Range(0, 16).Select(_ => new object[] { (uint)random.Next(0, 40), random.Next(3) == 0 ? (uint)random.Next() : (uint)random.Next(0, 9) })];
      violations.AddRange(FactChecker.Run(facts, inputs).Select(diagnostic => $"seed {seed + index}: {diagnostic.Message}"));
    }

    Assert.That(violations, Is.Empty);
    Assert.That(known, Is.GreaterThan(functions));
  }

}

/// <summary>
/// A seeded generator of small functions over the bounded pointers for the provenance soundness tests: a few
/// <c>stackalloc</c>s, pointer locals moved by constants and by masked or reduced parameters, branches,
/// bounded loops that bump a pointer, and loads, which trap when they leave their region.
/// </summary>
internal static class RandomPointerIr {

  public static IrFunction Generate(int seed) {
    var random = new Random(seed);
    var x = new IrLocal("x", IrType.U32);
    var y = new IrLocal("y", IrType.U32);
    var element = random.Next(2) == 0 ? IrType.U8 : IrType.U32;
    var pointer = IrType.Pointer(element);
    var allocations = Enumerable.Range(0, random.Next(1, 4)).Select(index => new IrLocal("a" + index, pointer)).ToList();
    var (p, q, v) = (new IrLocal("p", pointer), new IrLocal("q", pointer), new IrLocal("v", element));
    var counters = new List<IrLocal>();
    var body = new List<IrStmt>();
    foreach (var allocation in allocations)
      body.Add(new IrDeclare(allocation, new IrIntrinsic(IrIntrinsicOp.StackAlloc, pointer, [new IrConst(IrType.I32, random.Next(0, 17))])));

    IrExpr Base() => new IrLocalRef(random.Next(3) == 0 ? p : allocations[random.Next(allocations.Count)]);
    IrExpr Count() => random.Next(3) switch {
      0 => new IrConst(IrType.I32, random.Next(-4, 21)),
      1 => new IrBinary(IrBinaryOp.And, new IrLocalRef(random.Next(2) == 0 ? x : y), new IrConst(IrType.U32, random.Next(0, 16))),
      _ => new IrBinary(IrBinaryOp.Remainder, new IrLocalRef(x), new IrConst(IrType.U32, random.Next(1, 9)))
    };
    IrExpr Moved() => new IrIntrinsic(IrIntrinsicOp.PointerAdd, pointer, [Base(), Count()]);
    IrStmt Load(IrLocal from) => new IrAssign(v, new IrIntrinsic(IrIntrinsicOp.PointerLoad, element, [new IrLocalRef(from)]));

    body.Add(new IrDeclare(p, new IrLocalRef(allocations[0])));
    body.Add(new IrDeclare(q, Moved()));
    body.Add(new IrDeclare(v, new IrConst(element, 0)));
    var statements = random.Next(3, 8);
    for (var index = 0; index < statements; ++index)
      switch (random.Next(5)) {
        case 0:
          body.Add(new IrAssign(p, Moved()));
          break;
        case 1:
          body.Add(new IrIf(new IrBinary(IrBinaryOp.Less, new IrLocalRef(x), new IrConst(IrType.U32, random.Next(0, 40))), new IrBlock([new IrAssign(q, Moved())]), new IrBlock([new IrAssign(q, Base())])));
          break;
        case 2:
          var counter = new IrLocal("i" + counters.Count, IrType.I32);
          counters.Add(counter);
          body.Add(new IrDeclare(counter, new IrConst(IrType.I32, 0)));
          var step = new IrBlock([new IrAssign(counter, new IrBinary(IrBinaryOp.Add, new IrLocalRef(counter), new IrConst(IrType.I32, 1)))]);
          var bump = new IrAssign(p, new IrIntrinsic(IrIntrinsicOp.PointerAdd, pointer, [new IrLocalRef(p), new IrConst(IrType.I32, 1)]));
          body.Add(new IrFor(new IrBinary(IrBinaryOp.Less, new IrLocalRef(counter), new IrConst(IrType.I32, random.Next(0, 6))), new IrBlock([bump]), step));
          break;
        default:
          body.Add(Load(random.Next(2) == 0 ? p : q));
          break;
      }

    body.Add(new IrReturn(new IrLocalRef(x)));
    return new IrFunction("f", true, IrType.U32, [x, y], [.. allocations, p, q, v, .. counters], new IrBlock(body));
  }

}
