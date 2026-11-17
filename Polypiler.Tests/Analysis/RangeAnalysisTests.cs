using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis;
using Polypiler.Core.Analysis.Checking;
using Polypiler.Core.Analysis.Range;
using Polypiler.Core.IR;
using static Polypiler.Tests.Analysis.AnalysisIr;

namespace Polypiler.Tests.Analysis;

/// <summary>The minimal range provider the provenance stands on: intervals of integers, edges of comparisons, decided comparisons.</summary>
[TestFixture]
public sealed class RangeAnalysisTests {

  private static readonly IrLocal _i = new("i", IrType.I32);

  private static IrExpr I(int value) => new IrConst(IrType.I32, value);

  /// <summary>The range of an expression over <c>x: u32</c> and <c>i: i32</c> at the entry of the body.</summary>
  private static ValueRange RangeOf(IrExpr expression) {
    var function = Function([X, _i, C], new IrReturn(K(0)));
    return new RangeAnalysis(function).At(expression, ProgramPoint.Entry(function.Body));
  }

  [TestCase("const", 7, 7)]
  [TestCase("x & 7", 0, 7)]
  [TestCase("7 & x", 0, 7)]
  [TestCase("(x & 7) & (x & 3)", 0, 3)]
  [TestCase("i & -1", -2147483648, 2147483647)]
  [TestCase("(x & 7) + 1", 1, 8)]
  [TestCase("(x & 7) - 1", 0, 4294967295L)]
  [TestCase("(x & 7) * 3", 0, 21)]
  [TestCase("(x & 7) * (x & 7) checked", 0, 49)]
  [TestCase("x + 1 checked", 1, 4294967295L)]
  [TestCase("x % 5", 0, 4)]
  [TestCase("i % 5", -2147483648, 2147483647)]
  [TestCase("x / 4", 0, 1073741823)]
  [TestCase("x >> 1", 0, 4294967295L)]
  [TestCase("(u64) (x & 7)", 0, 7)]
  [TestCase("(u8) x", 0, 255)]
  [TestCase("x == 3 ? 1 : 9", 1, 9)]
  [TestCase("x < 0", 0, 0)]
  [TestCase("!(x < 0)", 1, 1)]
  [TestCase("c", 0, 1)]
  [TestCase("string", null, null)]
  public void Expressions_HaveTheirRanges(string expression, long? min, long? max) {
    IrExpr Mask(uint mask) => Op(IrBinaryOp.And, R(X), K(mask));
    var parsed = expression switch {
      "const" => K(7),
      "x & 7" => Mask(7),
      "7 & x" => Op(IrBinaryOp.And, K(7), R(X)),
      "(x & 7) & (x & 3)" => Op(IrBinaryOp.And, Mask(7), Mask(3)),
      "i & -1" => Op(IrBinaryOp.And, R(_i), I(-1)),
      "(x & 7) + 1" => Op(IrBinaryOp.Add, Mask(7), K(1)),
      "(x & 7) - 1" => Op(IrBinaryOp.Subtract, Mask(7), K(1)),
      "(x & 7) * 3" => Op(IrBinaryOp.Multiply, Mask(7), K(3)),
      "(x & 7) * (x & 7) checked" => Op(IrBinaryOp.MultiplyChecked, Mask(7), Mask(7)),
      "x + 1 checked" => Op(IrBinaryOp.AddChecked, R(X), K(1)),
      "x % 5" => Op(IrBinaryOp.Remainder, R(X), K(5)),
      "i % 5" => Op(IrBinaryOp.Remainder, R(_i), I(5)),
      "x / 4" => Op(IrBinaryOp.Divide, R(X), K(4)),
      "x >> 1" => Op(IrBinaryOp.ShiftRightLogical, R(X), I(1)),
      "(u64) (x & 7)" => new IrConvert(IrType.U64, Mask(7), IrConversion.Wrap),
      "(u8) x" => new IrConvert(IrType.U8, R(X), IrConversion.Wrap),
      "x == 3 ? 1 : 9" => new IrConditional(Op(IrBinaryOp.Equal, R(X), K(3)), K(1), K(9)),
      "x < 0" => Op(IrBinaryOp.Less, R(X), K(0)),
      "!(x < 0)" => new IrUnary(IrUnaryOp.LogicalNot, Op(IrBinaryOp.Less, R(X), K(0))),
      "c" => R(C),
      _ => new IrStrConst("s")
    };
    var expected = min is null ? ValueRange.Unknown : new ValueRange(min.Value, max!.Value);
    Assert.That(RangeOf(parsed), Is.EqualTo(expected));
  }

  /// <summary>A product whose bounds leave 128 bits is the whole type; an operand with no value makes no value.</summary>
  [Test]
  public void Extremes_AreHandled() {
    var big = new IrLocal("big", IrType.U64);
    var function = Function([big], new IrReturn(K(0)));
    var analysis = new RangeAnalysis(function);
    var entry = ProgramPoint.Entry(function.Body);
    Assert.That(analysis.At(Op(IrBinaryOp.Multiply, Op(IrBinaryOp.Multiply, R(big), R(big)), R(big)), entry), Is.EqualTo(ValueRange.Of(IrType.U64)));
    Assert.That(analysis.At(Op(IrBinaryOp.MultiplyChecked, R(big), Op(IrBinaryOp.Multiply, R(big), R(big))), entry), Is.EqualTo(ValueRange.Of(IrType.U64)));
    Assert.That(analysis.At(Op(IrBinaryOp.Subtract, R(big), R(big)), entry), Is.EqualTo(ValueRange.Of(IrType.U64)));
  }

  /// <summary>Comparisons narrow their variables on both edges, from either side; equal ranges decide; contradictions make no value.</summary>
  [TestCase(IrBinaryOp.Less, true, 0, 9)]
  [TestCase(IrBinaryOp.Less, false, 10, 4294967295L)]
  [TestCase(IrBinaryOp.LessOrEqual, true, 0, 10)]
  [TestCase(IrBinaryOp.Greater, true, 11, 4294967295L)]
  [TestCase(IrBinaryOp.GreaterOrEqual, false, 0, 9)]
  [TestCase(IrBinaryOp.Equal, true, 10, 10)]
  [TestCase(IrBinaryOp.NotEqual, true, 0, 4294967295L)]
  [TestCase(IrBinaryOp.NotEqual, false, 10, 10)]
  public void Comparisons_NarrowTheirEdges(IrBinaryOp op, bool edge, long min, long max) {
    var (then, otherwise) = (Block(), Block());
    var function = Function([X], new IrIf(Op(op, R(X), K(10)), then, otherwise), new IrReturn(K(0)));
    var flipped = Function([X], new IrIf(Op(op switch {
      IrBinaryOp.Less => IrBinaryOp.Greater, IrBinaryOp.LessOrEqual => IrBinaryOp.GreaterOrEqual,
      IrBinaryOp.Greater => IrBinaryOp.Less, IrBinaryOp.GreaterOrEqual => IrBinaryOp.LessOrEqual, _ => op
    }, K(10), R(X)), then, otherwise), new IrReturn(K(0)));
    var expected = new ValueRange(min, max);
    Assert.That(new RangeAnalysis(function).At(R(X), ProgramPoint.Entry(edge ? then : otherwise)), Is.EqualTo(expected));
    Assert.That(new RangeAnalysis(flipped).At(R(X), ProgramPoint.Entry(edge ? then : otherwise)), Is.EqualTo(expected));
  }

  /// <summary>Negation, conjunction, disjunction and truth variables pass their outcome on; a range decides its comparisons.</summary>
  [Test]
  public void LogicalConditions_RefineAndDecide() {
    var (then, otherwise, inner) = (Block(), Block(), Block());
    var condition = Op(IrBinaryOp.LogicalAnd, Op(IrBinaryOp.Less, R(X), K(10)), new IrUnary(IrUnaryOp.LogicalNot, Op(IrBinaryOp.Less, R(X), K(3))));
    var function = Function([X, C],
      new IrIf(condition, Block(new IrIf(Op(IrBinaryOp.LogicalOr, R(C), Op(IrBinaryOp.Greater, R(X), K(5))), inner, otherwise)), then),
      new IrReturn(K(0)));
    var analysis = new RangeAnalysis(function);
    var facts = new ValueFacts(function).Add(analysis);
    var body = (IrBlock)((IrIf)function.Body.Statements[0]).Then;
    Assert.That(analysis.At(R(X), ProgramPoint.Entry(body)), Is.EqualTo(new ValueRange(3, 9)));
    Assert.That(analysis.At(R(X), ProgramPoint.Entry(otherwise)), Is.EqualTo(new ValueRange(3, 5)));
    Assert.That(analysis.At(R(C), ProgramPoint.Entry(otherwise)), Is.EqualTo(ValueRange.Of(0)));
    Assert.That(analysis.At(R(X), ProgramPoint.Entry(then)), Is.EqualTo(ValueRange.Of(IrType.U32)));
    var point = ProgramPoint.Entry(body);
    Assert.That(facts.Decide(Op(IrBinaryOp.Less, R(X), K(10)), point), Is.EqualTo(Truth.True));
    Assert.That(facts.Decide(Op(IrBinaryOp.GreaterOrEqual, R(X), K(10)), point), Is.EqualTo(Truth.False));
    Assert.That(facts.Decide(Op(IrBinaryOp.LessOrEqual, R(X), K(9)), point), Is.EqualTo(Truth.True));
    Assert.That(facts.Decide(Op(IrBinaryOp.Greater, R(X), K(9)), point), Is.EqualTo(Truth.False));
    Assert.That(facts.Decide(Op(IrBinaryOp.Equal, R(X), K(20)), point), Is.EqualTo(Truth.False));
    Assert.That(facts.Decide(Op(IrBinaryOp.NotEqual, R(X), K(20)), point), Is.EqualTo(Truth.True));
    Assert.That(facts.Decide(Op(IrBinaryOp.NotEqual, R(X), K(5)), point), Is.EqualTo(Truth.Unknown));
    Assert.That(facts.Decide(Op(IrBinaryOp.LogicalOr, Op(IrBinaryOp.Less, R(X), K(10)), R(C)), point), Is.EqualTo(Truth.True));
    Assert.That(facts.Decide(Op(IrBinaryOp.LogicalAnd, R(C), Op(IrBinaryOp.Less, R(X), K(1))), point), Is.EqualTo(Truth.False));
    Assert.That(facts.Decide(Op(IrBinaryOp.LogicalAnd, Op(IrBinaryOp.Less, R(X), K(10)), Op(IrBinaryOp.Greater, R(X), K(1))), point), Is.EqualTo(Truth.True));
    Assert.That(facts.Decide(Op(IrBinaryOp.LogicalAnd, R(C), R(C)), point), Is.EqualTo(Truth.Unknown));
    Assert.That(facts.Decide(new IrUnary(IrUnaryOp.LogicalNot, R(C)), point), Is.EqualTo(Truth.Unknown));
    Assert.That(facts.Decide(new IrConst(IrType.Bool, 1), point), Is.EqualTo(Truth.True));
    Assert.That(facts.Decide(new IrConst(IrType.Bool, 0), point), Is.EqualTo(Truth.False));
    Assert.That(facts.Decide(Op(IrBinaryOp.Equal, new IrConst(IrType.F64, 0), new IrConst(IrType.F64, 0)), point), Is.EqualTo(Truth.Unknown));
  }

  /// <summary>A contradiction on an edge makes the variable's range empty and its comparisons undecided; an unreachable point decides nothing.</summary>
  [Test]
  public void Contradictions_LeaveNoValue() {
    var inner = Block();
    var function = Function([X], new IrIf(Op(IrBinaryOp.LogicalAnd, Op(IrBinaryOp.Less, R(X), K(3)), Op(IrBinaryOp.Greater, R(X), K(5))), inner, null), new IrReturn(K(0)));
    var analysis = new RangeAnalysis(function);
    Assert.That(analysis.At(R(X), ProgramPoint.Entry(inner)), Is.EqualTo(ValueRange.Unreachable));
    Assert.That(analysis.Decide(Op(IrBinaryOp.Less, R(X), K(3)), ProgramPoint.End(function.Body)), Is.EqualTo(Truth.Unknown));
    Assert.That(analysis.At(Op(IrBinaryOp.Add, R(X), K(1)), ProgramPoint.Entry(inner)), Is.EqualTo(ValueRange.Unreachable));
    Assert.That(analysis.Decide(Op(IrBinaryOp.Less, R(X), K(3)), ProgramPoint.Entry(inner)), Is.EqualTo(Truth.Unknown));
  }

  /// <summary>A counting loop keeps its counter within the type: the head is widened, the body narrowed by the condition.</summary>
  [Test]
  public void ALoopCounter_IsBoundedByItsCondition() {
    var body = Block(new IrAssign(Y, Op(IrBinaryOp.Add, R(Y), K(1))));
    var function = Function([X], new IrDeclare(Y, K(0)), new IrWhile(Op(IrBinaryOp.Less, R(Y), K(8)), body), new IrReturn(R(Y)));
    var analysis = new RangeAnalysis(function);
    Assert.That(analysis.At(R(Y), ProgramPoint.Entry(body)), Is.EqualTo(new ValueRange(0, 7)));
    Assert.That(analysis.At(R(Y), new ProgramPoint(function.Body, 2)), Is.EqualTo(new ValueRange(8, 4294967295)));
  }

  /// <summary>The interval lattice.</summary>
  [Test]
  public void TheRangeDomain_IsALattice() {
    var domain = RangeDomain.Instance;
    var (low, high) = (new ValueRange(0, 4), new ValueRange(2, 8));
    Assert.That(domain.Join(low, high), Is.EqualTo(new ValueRange(0, 8)));
    Assert.That(domain.Join(domain.Unreachable, high), Is.EqualTo(high));
    Assert.That(domain.Join(low, domain.Unreachable), Is.EqualTo(low));
    Assert.That(domain.Meet(low, high), Is.EqualTo(new ValueRange(2, 4)));
    Assert.That(domain.Meet(low, new ValueRange(5, 6)), Is.EqualTo(domain.Unreachable));
    Assert.That(domain.Widen(low, high), Is.EqualTo(new ValueRange(0, Int128.MaxValue)));
    Assert.That(domain.Widen(high, low), Is.EqualTo(new ValueRange(Int128.MinValue, 8)));
    Assert.That(domain.Widen(domain.Unreachable, low), Is.EqualTo(low));
    Assert.That(domain.Implies(low, domain.Unknown) && !domain.Implies(high, low) && domain.Implies(domain.Unreachable, low), Is.True);
    Assert.That(domain.Contains(low, new IrConst(IrType.U32, 4)) && !domain.Contains(low, new IrConst(IrType.U32, 5)), Is.True);
    Assert.That(domain.Contains(new ValueRange(0, 1), new IrConst(IrType.Bool, 1)), Is.True);
    Assert.That(domain.Contains(new ValueRange(0, ulong.MaxValue), new IrConst(IrType.U64, -1)), Is.True);
    Assert.That(domain.Contains(domain.Unknown, "x") && !domain.Contains(low, "x"), Is.True);
    Assert.That(string.Join(",", domain.Describe(low), domain.Describe(domain.Unknown), domain.Describe(domain.Unreachable)), Is.EqualTo("[0, 4],unknown,unreachable"));
    Assert.That(low.ToString(), Is.EqualTo("[0, 4]"));
    Assert.That(ValueRange.Domain, Is.SameAs(domain));
    Assert.That(ValueRange.Of(IrType.Bool), Is.EqualTo(new ValueRange(0, 1)));
    Assert.That(ValueRange.Of(IrType.F64), Is.EqualTo(ValueRange.Unknown));
    Assert.That(() => ValueRange.Of(null!), Throws.ArgumentNullException);
  }

  /// <summary>Ranges hold on every run of 25 random functions, 16 inputs each.</summary>
  [Test]
  public void RangesAreSoundOnRandomFunctions() => AssertSound(25);

  /// <summary>Ranges hold on every run of 2 000 random functions, 16 inputs each.</summary>
  [Test, Category("Slow")]
  public void RangesAreSoundOnTwoThousandRandomFunctions() => AssertSound(2_000);

  private static void AssertSound(int functions) {
    var violations = 0;
    for (var seed = 0; seed < functions; ++seed) {
      var function = RandomIr.Generate(900 + seed);
      var facts = new ValueFacts(function).Add(new RangeAnalysis(function));
      var locals = function.Parameters.Concat(function.Locals).ToList();
      foreach (var block in Blocks(function))
        for (var index = 0; index <= block.Statements.Count; ++index) {
          var point = new ProgramPoint(block, index);
          foreach (var local in locals.Where(local => facts.Layout.Defined(local, point)))
            facts.Get<ValueRange>(local, point);
        }

      var random = new Random(seed);
      violations += FactChecker.Run(facts, [.. Enumerable.Range(0, 16).Select(_ => new object[] { (uint)random.Next(0, 9), (uint)random.Next() })]).Count;
    }

    Assert.That(violations, Is.Zero);
  }

}
