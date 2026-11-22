using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis;
using Polypiler.Core.Analysis.Scev;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Analysis.Scev;

/// <summary>Scalar evolution of the loops the C# frontend writes, one case per row of the test design of #350.</summary>
[TestFixture]
public sealed class ScalarEvolutionTests {

  private static IrFunction Method(string method) => PrototypeFixture.Import("Loops.cs", PrototypeFixture.Wrap(method)).Functions[0];

  private static IrLocal Local(IrFunction function, string name) => function.Parameters.Concat(function.Locals).First(local => local.Name == name);

  private static IrExpr Read(IrFunction function, string name) => new IrLocalRef(Local(function, name));

  private static IrConst Const(IrType type, long value) => new(type, IrOperators.Wrap(type, value));

  private static Evolution.AddRec Rec(IrStmt loop, IrType type, long start, long step, bool nuw = false, bool nsw = false)
    => new(new Evolution.Invariant(Const(type, start)), new Evolution.Invariant(Const(type, step)), loop, type, nuw, nsw);

  private static (ScalarEvolution Analysis, IrStmt Loop) Analyse(IrFunction function, int loop = 0) {
    var analysis = new ScalarEvolution(function);
    return (analysis, analysis.Loops[loop]);
  }

  // 1 - happy path

  [Test]
  public void ACanonicalCounter_IsAnAddRecWithAnExactCount() {
    var function = Method("public static uint F() { uint s = 0; for (uint i = 0; i < 100; i++) s += i; return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.InductionVariables(loop).Select(local => local.Name), Is.EqualTo(new[] { "i" }));
    Assert.That(analysis.Of(Read(function, "i"), loop), Is.EqualTo(Rec(loop, IrType.U32, 0, 1, nuw: true, nsw: true)));
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Exact(100)));
    Assert.That(analysis.Of(Read(function, "i"), loop).ToString(), Is.EqualTo("AddRec(start 0, step 1) nuw nsw"));
  }

  [TestCase(10u, 20u, 3u, 4)]
  [TestCase(0u, 10u, 3u, 4)]
  [TestCase(5u, 5u, 1u, 0)]
  [TestCase(9u, 5u, 1u, 0)]
  [TestCase(0u, 9u, 3u, 3)]
  public void StartBoundAndStep_GiveTheCeilingOfTheDistance(uint start, uint bound, uint step, int count) {
    var function = Method($"public static uint F() {{ uint s = 0; for (uint i = {start}; i < {bound}; i += {step}) s++; return s; }}");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.Of(Read(function, "i"), loop), Is.EqualTo(Rec(loop, IrType.U32, start, step, nuw: true, nsw: true)));
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Exact(count)));
  }

  [Test]
  public void AnAffineValueOfTheCounter_IsAnAddRec() {
    var function = Method("public static uint F() { uint s = 0; for (uint i = 0; i < 100; i++) { uint o = i << 2; uint a = o + 2; s += a; } return s; }");
    var (analysis, loop) = Analyse(function);
    var body = ((IrWhile)loop).Body;
    // a is read after its definition at position 1, before the counter steps at the end of the body.
    Assert.That(analysis.At(Read(function, "a"), new ProgramPoint(body, 2), loop), Is.EqualTo(Rec(loop, IrType.U32, 2, 4, nuw: true, nsw: true)));
    var expression = new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.ShiftLeft, Read(function, "i"), Const(IrType.I32, 2)), Const(IrType.U32, 2));
    Assert.That(analysis.Of(expression, loop), Is.EqualTo(Rec(loop, IrType.U32, 2, 4, nuw: true, nsw: true)));
  }

  [Test]
  public void ASymbolicBound_GivesASymbolicCount() {
    var function = Method("public static uint F(uint n) { uint s = 0; for (uint i = 0; i < n; i++) s++; return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Symbolic(Read(function, "n"), 0)));
    Assert.That(analysis.TripCountOf(loop).ToString(), Is.EqualTo("Symbolic(n, 0)"));
    Assert.That(analysis.Of(Read(function, "i"), loop), Is.EqualTo(Rec(loop, IrType.U32, 0, 1, nuw: true)), "i reaches at most n, which is a u32; as signed it may wrap");
  }

  [Test]
  public void ASymbolicBoundAboveTheStart_CountsFromTheStart() {
    var function = Method("public static int F(int n) { int s = 0; if (n >= 3) { for (int i = 3; i < n; i++) s++; } return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Symbolic(Read(function, "n"), -3)));
  }

  [Test]
  public void ALessOrEqualBound_CountsOneMore() {
    var function = Method("public static int F(int n) { int s = 0; if (n >= 0 && n < 1000) { for (int i = 0; i <= n; i++) s++; } return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Symbolic(Read(function, "n"), 1)));
    Assert.That(analysis.Of(Read(function, "i"), loop), Is.EqualTo(Rec(loop, IrType.I32, 0, 1, nuw: true, nsw: true)), "n < 1000 bounds the counter");
  }

  [Test]
  public void ANonAffineRecurrence_IsUnknown() {
    var function = Method("public static uint F() { uint x = 1; for (uint i = 0; i < 10; i++) x = x * 3; return x; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.Of(Read(function, "x"), loop), Is.SameAs(Evolution.Unknown));
    Assert.That(Evolution.Unknown.ToString(), Is.EqualTo("Unknown"));
  }

  [TestCase(10, "Unknown")]
  [TestCase(9, "Exact(3)")]
  [TestCase(0, "Exact(0)")]
  public void AnInequalityExit_IsCountedOnlyWhenTheStepDividesTheDistance(int bound, string count) {
    var function = Method($"public static int F() {{ int s = 0; for (int i = 0; i != {bound}; i += 3) s++; return s; }}");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.TripCountOf(loop).ToString(), Is.EqualTo(count));
  }

  // 2 - ported from PB-Compiler's IrScalarEvolutionTests

  [TestCase("for (short i = 0; i < 4; i++)", 4)]
  [TestCase("for (short i = 3; i >= 0; i--)", 4)]
  [TestCase("for (short i = 0; !(i >= 4); i++)", 4)]
  public void ThePbCompilerCases_HaveTheSameCounts(string loop, int count) {
    var function = Method($"public static int F() {{ int s = 0; {loop} s++; return s; }}");
    var (analysis, statement) = Analyse(function);
    // C# widens a short to int for the arithmetic; the counter is then not v = v + s at its own type.
    Assert.That(analysis.TripCountOf(statement), Is.SameAs(TripCount.Unknown));

    var wide = Method($"public static int F() {{ int s = 0; {loop.Replace("short", "int", StringComparison.Ordinal)} s++; return s; }}");
    var (wideAnalysis, wideLoop) = Analyse(wide);
    Assert.That(wideAnalysis.TripCountOf(wideLoop), Is.EqualTo(new TripCount.Exact(count)));
  }

  [Test]
  public void AnInvariantNonConstantStep_KeepsTheRecurrenceButNotTheCount() {
    var function = Method("public static int F(int step) { int s = 0; for (int i = 0; i < 8; i += step) s++; return s; }");
    var (analysis, loop) = Analyse(function);
    var evolution = (Evolution.AddRec)analysis.Of(Read(function, "i"), loop);
    Assert.That(evolution.Step, Is.EqualTo(new Evolution.Invariant(Read(function, "step"))));
    Assert.That(evolution.Step.ToString(), Is.EqualTo("step"));
    Assert.That(analysis.TripCountOf(loop), Is.SameAs(TripCount.Unknown));
  }

  // 3 - boundaries

  [Test]
  public void ACounterThatWrapsBeforeTheBound_IsNotCounted() {
    var function = Method("public static int F() { int s = 0; for (uint i = 0; i < 0xFFFFFFF0u; i += 0x40000000u) { s++; if (s > 10) return s; } return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.TripCountOf(loop), Is.SameAs(TripCount.Unknown));
    Assert.That(analysis.Of(Read(function, "i"), loop), Is.EqualTo(Rec(loop, IrType.U32, 0, 0x40000000)));
  }

  [Test]
  public void ADownCountingLoop_HasANegativeStep() {
    var function = Method("public static uint F() { uint s = 0; for (uint i = 10; i > 0; i--) s += i; return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.Of(Read(function, "i"), loop), Is.EqualTo(Rec(loop, IrType.U32, 10, -1, nuw: true, nsw: true)));
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Exact(10)));
  }

  [Test]
  public void ABoundAtTheTopOfTheType_NeverEndsALessOrEqualTest() {
    var function = Method("public static int F() { int s = 0; for (int i = 0; i <= int.MaxValue; i++) { s++; if (s > 5) return s; } return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.TripCountOf(loop), Is.SameAs(TripCount.Unknown));
  }

  [TestCase("i < 10", "i += -1", "Exact(0)", 11)]
  [TestCase("i > 10", "i++", "Exact(0)", 9)]
  [TestCase("i >= 10", "i++", "Exact(0)", 9)]
  [TestCase("i <= 10", "i += -1", "Exact(0)", 11)]
  [TestCase("i > 3", "i += -2", "Exact(3)", 9)]
  [TestCase("i >= 3", "i += -2", "Exact(4)", 9)]
  public void ATestFalseAtTheStartOrCountedDown_IsExact(string test, string step, string count, int start) {
    var function = Method($"public static int F() {{ int s = 0; for (int i = {start}; {test}; {step}) s++; return s; }}");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.TripCountOf(loop).ToString(), Is.EqualTo(count));
  }

  [Test]
  public void ATestThatAStepAwayFromTheBoundNeverEnds_IsUnknown() {
    var function = Method("public static int F() { int s = 0; for (int i = 0; i < 10; i += -1) { s++; if (s > 5) return s; } return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.TripCountOf(loop), Is.SameAs(TripCount.Unknown));
  }

  // 4 - derived values

  [Test]
  public void SumsDifferencesNegationsAndProducts_CombineComponentWise() {
    var function = Method("public static int F(int k) { int s = 0; int j = 5; for (int i = 0; i < 10; i++) { s++; j += 2; } return s; }");
    var (analysis, loop) = Analyse(function);
    var (i, j) = (Read(function, "i"), Read(function, "j"));
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.Add, i, j), loop), Is.EqualTo(Rec(loop, IrType.I32, 5, 3, nuw: true, nsw: true)));
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.Subtract, j, i), loop), Is.EqualTo(Rec(loop, IrType.I32, 5, 1, nuw: true, nsw: true)));
    Assert.That(analysis.Of(new IrUnary(IrUnaryOp.Negate, i), loop), Is.EqualTo(Rec(loop, IrType.I32, 0, -1, nsw: true)));
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.Multiply, Const(IrType.I32, 3), i), loop), Is.EqualTo(Rec(loop, IrType.I32, 0, 3, nuw: true, nsw: true)));
    var scaled = (Evolution.AddRec)analysis.Of(new IrBinary(IrBinaryOp.Multiply, i, Read(function, "k")), loop);
    Assert.That(scaled.Step, Is.EqualTo(new Evolution.Invariant(Read(function, "k"))));
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.Multiply, i, j), loop), Is.SameAs(Evolution.Unknown));
    Assert.That(analysis.Of(Read(function, "k"), loop), Is.EqualTo(new Evolution.Invariant(Read(function, "k"))));
    Assert.That(analysis.Of(Read(function, "s"), loop), Is.EqualTo(Rec(loop, IrType.I32, 0, 1, nuw: true, nsw: true)), "s steps once per pass as well");
  }

  [Test]
  public void ATruncation_KeepsTheRecurrenceModuloTheNarrowerWidth() {
    var function = Method("public static long F() { long s = 0; for (long i = 250; i < 260; i++) s += i; return s; }");
    var (analysis, loop) = Analyse(function);
    var truncated = analysis.Of(new IrConvert(IrType.U8, Read(function, "i")), loop);
    // 250, …, 255, 0, …, 4 wraps as unsigned bytes but not as signed ones (-6 … 4).
    Assert.That(truncated, Is.EqualTo(new Evolution.AddRec(new Evolution.Invariant(Const(IrType.U8, 250)), new Evolution.Invariant(Const(IrType.U8, 1)), loop, IrType.U8, false, true)));
  }

  [Test]
  public void AnExtension_KeepsTheRecurrenceOnlyWithItsFlag() {
    var bounded = Method("public static long F() { long s = 0; for (int i = 10; i > -5; i--) s += i; return s; }");
    var (analysis, loop) = Analyse(bounded);
    Assert.That(analysis.Of(new IrConvert(IrType.I64, Read(bounded, "i")), loop),
      Is.EqualTo(new Evolution.AddRec(new Evolution.Invariant(Const(IrType.I64, 10)), new Evolution.Invariant(Const(IrType.I64, -1)), loop, IrType.I64, false, true)));

    var zeroExtended = Method("public static ulong F() { ulong s = 0; for (uint i = 0; i < 10; i++) s += i; return s; }");
    var (zeroAnalysis, zeroLoop) = Analyse(zeroExtended);
    Assert.That(zeroAnalysis.Of(new IrConvert(IrType.U64, Read(zeroExtended, "i")), zeroLoop),
      Is.EqualTo(new Evolution.AddRec(new Evolution.Invariant(Const(IrType.U64, 0)), new Evolution.Invariant(Const(IrType.U64, 1)), zeroLoop, IrType.U64, true, true)));

    var unbounded = Method("public static long F(int n) { long s = 0; for (int i = 0; i != n; i++) s += i; return s; }");
    var (unboundedAnalysis, unboundedLoop) = Analyse(unbounded);
    Assert.That(unboundedAnalysis.Of(new IrConvert(IrType.I64, Read(unbounded, "i")), unboundedLoop), Is.SameAs(Evolution.Unknown));
  }

  [Test]
  public void ASameWidthConversion_KeepsTheRecurrenceAndItsFlags() {
    var function = Method("public static int F() { int s = 0; for (int i = 0; i < 10; i++) s++; return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.Of(new IrConvert(IrType.U32, Read(function, "i")), loop), Is.EqualTo(Rec(loop, IrType.U32, 0, 1, nuw: true, nsw: true)));
    Assert.That(analysis.Of(new IrConvert(IrType.I64, new IrConvert(IrType.I32, Const(IrType.U32, 7))), loop), Is.EqualTo(new Evolution.Invariant(Const(IrType.I64, 7))));
  }

  // 5 - nested loops

  [Test]
  public void AnInnerCounter_StartsAtARecurrenceOfTheOuterLoop() {
    var function = Method("public static int F() { int s = 0; for (int i = 0; i < 10; i++) { int j = i * 4; while (j < 100) { s++; j++; } } return s; }");
    var analysis = new ScalarEvolution(function);
    var (outer, inner) = (analysis.Loops[0], analysis.Loops[1]);
    Assert.That(analysis.Parent(inner), Is.SameAs(outer));
    Assert.That(analysis.Parent(outer), Is.Null);
    var evolution = (Evolution.AddRec)analysis.Of(Read(function, "j"), inner);
    Assert.That(evolution.Start, Is.EqualTo(Rec(outer, IrType.I32, 0, 4)));
    Assert.That(evolution.Step, Is.EqualTo(new Evolution.Invariant(Const(IrType.I32, 1))));
    Assert.That(analysis.Of(Read(function, "i"), inner), Is.EqualTo(new Evolution.Invariant(Read(function, "i"))), "the outer counter does not change in the inner loop");
    Assert.That(analysis.TripCountOf(inner), Is.InstanceOf<TripCount.Max>());
  }

  // 6 - exceptions

  [TestCase("public static int F(int k) { int s = 0; for (int i = 0; i < 10; i += k) { s++; k++; } return s; }")]
  [TestCase("public static int F() { int s = 0; for (int i = 0; i < 10; i++) { s++; if (s > 3) i++; } return s; }")]
  [TestCase("public static int F() { int s = 0; int i = 0; while (i < 10) { if (s++ > 3) continue; i++; } return s; }")]
  [TestCase("public static int F() { int s = 0; int i = 0; System.Func<int> f = () => i; while (i < 10) { s += f(); i++; } return s; }")]
  public void AStepThatIsNotOnceAndInvariant_IsNoInduction(string method) {
    var function = Method(method);
    var analysis = new ScalarEvolution(function);
    var loop = analysis.Loops.Last();
    Assert.That(analysis.InductionVariables(loop).Select(local => local.Name), Does.Not.Contain("i"));
    Assert.That(analysis.Of(Read(function, "i"), loop), Is.SameAs(Evolution.Unknown));
    Assert.That(analysis.TripCountOf(loop), Is.SameAs(TripCount.Unknown));
  }

  [TestCase("if (s > 3) break;", "Max(10)")]
  [TestCase("if (s > 3) return s;", "Max(10)")]
  [TestCase("if (s > 3) throw new System.Exception();", "Max(10)")]
  [TestCase("switch (s) { case 3: break; }", "Exact(10)")]
  [TestCase("for (int k = 0; k < 2; k++) { if (s > 3) break; }", "Exact(10)")]
  public void AnotherExit_MakesTheExactCountABound(string exit, string count) {
    var function = Method($"public static int F() {{ int s = 0; for (int i = 0; i < 10; i++) {{ s++; {exit} }} return s; }}");
    var analysis = new ScalarEvolution(function);
    Assert.That(analysis.TripCountOf(analysis.Loops[0]).ToString(), Is.EqualTo(count));
  }

  [Test]
  public void TwoTests_GiveTheSmallerCount() {
    var function = Method("public static int F(int n) { int s = 0; for (int i = 0, j = 20; i < 10 && j > 5; i++, j -= 2) s++; return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Exact(8)));

    var oneUncountable = Method("public static int F(int n) { int s = 0; for (int i = 0; i < 10 && s != n; i++) s++; return s; }");
    var (uncountable, uncountableLoop) = Analyse(oneUncountable);
    Assert.That(uncountable.TripCountOf(uncountableLoop), Is.EqualTo(new TripCount.Max(10)));

    var neither = Method("public static int F(int n) { int s = 0; for (int i = 0; s != n && s != 2 * n; i++) s++; return s; }");
    var (none, noneLoop) = Analyse(neither);
    Assert.That(none.TripCountOf(noneLoop), Is.SameAs(TripCount.Unknown));
  }

  [Test]
  public void ARangedBound_GivesAMaximum() {
    var function = Method("public static int F(int n) { int s = 0; if (n < 50) { for (int i = 0; i < n; i++) s++; } return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Max(49)), "n may be negative, so no symbolic count; at most 49 passes");
    Assert.That(analysis.TripCountOf(loop).ToString(), Is.EqualTo("Max(49)"));
  }

  [Test]
  public void ADoWhileLoop_HasRecurrencesButNoCount() {
    var function = Method("public static int F() { int s = 0; int i = 0; do { s++; i++; } while (i < 10); return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(loop, Is.InstanceOf<IrDoWhile>());
    Assert.That(analysis.Of(Read(function, "i"), loop), Is.EqualTo(Rec(loop, IrType.I32, 0, 1)));
    Assert.That(analysis.TripCountOf(loop), Is.SameAs(TripCount.Unknown));
  }

  [Test]
  public void AForWithContinue_StepsInItsStepBlock() {
    var function = Method("public static int F() { int s = 0; for (int i = 0; i < 12; i += 2) { if (i == 4) continue; s++; } return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(loop, Is.InstanceOf<IrFor>());
    Assert.That(analysis.Of(Read(function, "i"), loop), Is.EqualTo(Rec(loop, IrType.I32, 0, 2, nuw: true, nsw: true)));
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Exact(6)));
    var step = ((IrFor)loop).Step;
    Assert.That(analysis.At(Read(function, "i"), ProgramPoint.End(step), loop), Is.EqualTo(Rec(loop, IrType.I32, 2, 2, nuw: true, nsw: true)));
    Assert.That(analysis.At(Read(function, "i"), ProgramPoint.Entry(step), loop), Is.EqualTo(Rec(loop, IrType.I32, 0, 2, nuw: true, nsw: true)));
  }

  [Test]
  public void ACheckedStep_CannotWrapOnAnyExecutionThatGoesOn() {
    var function = Method("public static int F(int n) { int s = 0; int i = 0; while (i != n) { s++; i = checked(i + 1); } return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.Of(Read(function, "i"), loop), Is.EqualTo(Rec(loop, IrType.I32, 0, 1, nsw: true)));
  }

  [Test]
  public void ADefinitionAContinueMaySkip_IsUnknownInTheStep() {
    var function = Method("public static int F() { int s = 0; int d = 0; for (int i = 0; i < 10; i++) { if (s++ > 3) continue; d = i * 2; } return s + d; }");
    var (analysis, loop) = Analyse(function);
    var forLoop = (IrFor)loop;
    Assert.That(analysis.At(Read(function, "d"), ProgramPoint.End(forLoop.Body), loop), Is.EqualTo(Rec(loop, IrType.I32, 0, 2, nuw: true, nsw: true)));
    Assert.That(analysis.At(Read(function, "d"), ProgramPoint.Entry(forLoop.Step), loop), Is.SameAs(Evolution.Unknown));
    Assert.That(analysis.Of(Read(function, "d"), loop), Is.SameAs(Evolution.Unknown), "before its definition d holds the previous pass's value");
  }

  [Test]
  public void TheFacadeExtensions_UseTheRegisteredFacts() {
    var function = Method("public static uint F() { uint s = 0; for (uint i = 0; i < 100; i++) s += i; return s; }");
    var loop = new ScalarEvolution(function).Loops[0];
    var bare = new ValueFacts(function);
    Assert.That(bare.TripCountOf(loop), Is.EqualTo(new TripCount.Max(100)), "without facts the start is any u32, which is at least 0");
    Assert.That(bare.EvolutionOf(Read(function, "i"), loop), Is.EqualTo(new Evolution.AddRec(new Evolution.Invariant(Read(function, "i")), new Evolution.Invariant(Const(IrType.U32, 1)), loop, IrType.U32, false, false)));
    var facts = new ValueFacts(function).Add(new Core.Analysis.Constant.ConstantAnalysis(function)).Add(new Core.Analysis.Range.RangeAnalysis(function));
    Assert.That(facts.TripCountOf(loop), Is.EqualTo(new TripCount.Exact(100)));
    Assert.That(facts.EvolutionOf(Read(function, "i"), loop), Is.EqualTo(Rec(loop, IrType.U32, 0, 1, nuw: true, nsw: true)));
  }

  [Test]
  public void AStatementThatIsNoLoop_OrAPointOutsideTheLoop_IsAnError() {
    var function = Method("public static int F() { int s = 0; for (int i = 0; i < 10; i++) s++; return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.Throws<ArgumentException>(() => analysis.Of(Read(function, "s"), function.Body.Statements[0]));
    Assert.Throws<ArgumentException>(() => analysis.At(Read(function, "s"), ProgramPoint.End(function.Body), loop));
    Assert.Throws<ArgumentNullException>(() => analysis.Of(null!, loop));
    Assert.Throws<ArgumentNullException>(() => analysis.TripCountOf(null!));
    Assert.Throws<ArgumentNullException>(() => new ScalarEvolution((ValueFacts)null!));
  }

  [Test]
  public void ValuesOfOtherTypes_AreUnknown() {
    var function = Method("public static int F(bool b) { int s = 0; for (int i = 0; i < 10; i++) s++; return s; }");
    var (analysis, loop) = Analyse(function);
    Assert.That(analysis.Of(Read(function, "b"), loop), Is.SameAs(Evolution.Unknown));
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.Divide, Read(function, "i"), Const(IrType.I32, 2)), loop), Is.SameAs(Evolution.Unknown));
  }
}
