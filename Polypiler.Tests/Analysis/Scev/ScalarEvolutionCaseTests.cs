using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis;
using Polypiler.Core.Analysis.Scev;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Analysis.Scev;

/// <summary>The remaining equivalence classes of the tests and values scalar evolution reads, on C# source.</summary>
[TestFixture]
public sealed class ScalarEvolutionCaseTests {

  private static IrFunction Method(string method) => PrototypeFixture.Import("Cases.cs", PrototypeFixture.Wrap(method)).Functions.First(function => function.Name == "F");

  private static IrExpr Read(IrFunction function, string name) => new IrLocalRef(function.Parameters.Concat(function.Locals).First(local => local.Name == name));

  private static string Count(string method, int loop = 0) {
    var analysis = new ScalarEvolution(Method(method));
    return analysis.TripCountOf(analysis.Loops[loop]).ToString();
  }

  [TestCase("for (int i = 0; 10 > i; i++)", "Exact(10)")]
  [TestCase("for (int i = 0; 10 >= i; i++)", "Exact(11)")]
  [TestCase("for (int i = 5; 0 < i; i--)", "Exact(5)")]
  [TestCase("for (int i = 5; 0 <= i; i--)", "Exact(6)")]
  [TestCase("for (int i = 0; 9 != i; i += 3)", "Exact(3)")]
  public void ABoundOnTheLeft_IsTheMirroredTest(string loop, string count)
    => Assert.That(Count($"public static int F() {{ int s = 0; {loop} s++; return s; }}"), Is.EqualTo(count));

  [TestCase("if (n < 0) { for (int i = 5; i < n; i--) s++; }", "Exact(0)")]
  [TestCase("if (n < 5) { for (int i = 5; i <= n; i--) s++; }", "Exact(0)")]
  [TestCase("if (n > 5) { for (int i = 5; i > n; i++) s++; }", "Exact(0)")]
  [TestCase("if (n > 5) { for (int i = 5; i >= n; i++) s++; }", "Exact(0)")]
  [TestCase("if (n > -5) { for (int i = 5; i >= n; i++) { s++; if (s > 9) return s; } }", "Unknown")]
  public void AStepAwayFromARangedBound_RunsNeverOrIsUnknown(string loop, string count)
    => Assert.That(Count($"public static int F(int n) {{ int s = 0; {loop} return s; }}"), Is.EqualTo(count));

  [Test]
  public void AnExtensionInTheTest_CannotUseFlagsItsOwnCountWouldProve()
    => Assert.That(Count("public static int F(long n) { int s = 0; for (int i = 0; (long)i < n; i++) s++; return s; }"), Is.EqualTo("Unknown"));

  [Test]
  public void ASymbolicCountWithABreak_IsBoundedByTheBoundsRange()
    => Assert.That(Count("public static int F(uint n) { int s = 0; for (uint i = 0; i < n; i++) { if (i == 7u) break; s++; } return s; }"), Is.EqualTo("Max(4294967295)"));

  [Test]
  public void AnUncountableTestFirst_StillLeavesTheOtherAsABound()
    => Assert.That(Count("public static int F(int n) { int s = 0; for (int i = 0; s != n && i < 10; i++) s++; return s; }"), Is.EqualTo("Max(10)"));

  [Test]
  public void AStepOfZero_NeverEndsALoop()
    => Assert.That(Count("public static int F() { int s = 0; for (int i = 0; i < 10; i = i + 0) { s++; if (s > 5) return s; } return s; }"), Is.EqualTo("Unknown"));

  [Test]
  public void ABoundTheConstantFactsKnow_GivesAnExactCount()
    => Assert.That(Count("public static int F() { int s = 0; int m = 10; for (int i = 0; i < m; i++) s++; return s; }"), Is.EqualTo("Exact(10)"));

  [Test]
  public void AnInnerCounterWithAnUnknownInitializer_StartsAtItsValueOnEntry() {
    var function = Method("public static int F() { int s = 0; for (int i = 0; i < 10; i++) { int j = s * s; while (j < 100) { s++; j++; } } return s; }");
    var analysis = new ScalarEvolution(function);
    var inner = analysis.Loops[1];
    Assert.That(((Evolution.AddRec)analysis.Of(Read(function, "j"), inner)).Start, Is.EqualTo(new Evolution.Invariant(Read(function, "j"))));
    var first = Method("public static int F() { int s = 0; for (int i = 0; i < 10; i++) { while (s < 100) s++; } return s; }");
    var firstAnalysis = new ScalarEvolution(first);
    Assert.That(((Evolution.AddRec)firstAnalysis.Of(Read(first, "s"), firstAnalysis.Loops[1])).Start, Is.EqualTo(new Evolution.Invariant(Read(first, "s"))));
  }

  [Test]
  public void InvariantsAndTheirFolding_ReadAsWhatTheyAre() {
    var function = Method("public static int F(int x, int[] a) { int s = 0; for (int i = 0; i < 10; i++) s++; return s; }");
    var analysis = new ScalarEvolution(function);
    var loop = analysis.Loops[0];
    var (x, i) = (Read(function, "x"), Read(function, "i"));
    Assert.That(((Evolution.AddRec)analysis.Of(new IrBinary(IrBinaryOp.Add, x, i), loop)).Start, Is.EqualTo(new Evolution.Invariant(x)), "0 + x is x");
    Assert.That(((Evolution.AddRec)analysis.Of(new IrBinary(IrBinaryOp.Add, i, x), loop)).Start, Is.EqualTo(new Evolution.Invariant(x)), "x + 0 is x");
    Assert.That(((Evolution.AddRec)analysis.Of(new IrBinary(IrBinaryOp.Multiply, i, x), loop)).Step, Is.EqualTo(new Evolution.Invariant(x)), "1 * x is x");
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.Multiply, new IrConst(IrType.I32, 1), x), loop), Is.EqualTo(new Evolution.Invariant(x)), "x * 1 is x");
    Assert.That(analysis.Of(new IrUnary(IrUnaryOp.BitNot, x), loop), Is.EqualTo(new Evolution.Invariant(new IrUnary(IrUnaryOp.BitNot, x))));
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.And, x, new IrConst(IrType.I32, 3)), loop), Is.InstanceOf<Evolution.Invariant>());
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.Divide, x, new IrConst(IrType.I32, 2)), loop), Is.SameAs(Evolution.Unknown), "a division may trap");
    Assert.That(analysis.Of(new IrUnary(IrUnaryOp.Negate, new IrBinary(IrBinaryOp.Multiply, i, i)), loop), Is.SameAs(Evolution.Unknown));
    Assert.That(analysis.Of(new IrArrayLoad(Read(function, "a"), new IrConst(IrType.I32, 0)), loop), Is.SameAs(Evolution.Unknown));
    Assert.That(analysis.Of(new IrConvert(IrType.I32, new IrConst(IrType.F64, 0)) { }, loop), Is.SameAs(Evolution.Unknown));
    Assert.That(((Evolution.AddRec)analysis.Of(new IrUnary(IrUnaryOp.Negate, new IrBinary(IrBinaryOp.Multiply, i, x)), loop)).Step,
      Is.EqualTo(new Evolution.Invariant(new IrBinary(IrBinaryOp.Subtract, new IrConst(IrType.I32, 0), x))));
  }

  [Test]
  public void AVariableACatchClauseBinds_IsWritten() {
    var function = Method("public static int F() { int s = 0; for (int i = 0; i < 10; i++) { try { s++; } catch (System.Exception) { s--; } } return s; }");
    var analysis = new ScalarEvolution(function);
    Assert.That(analysis.TripCountOf(analysis.Loops[0]), Is.EqualTo(new TripCount.Exact(10)));
  }

  [Test]
  public void AYield_LeavesTheLoopForAWhile() {
    var module = PrototypeFixture.Import("Yield.cs", PrototypeFixture.Wrap("public static System.Collections.Generic.IEnumerable<int> F() { for (int i = 0; i < 10; i++) yield return i; }"));
    var function = module.Functions.First(candidate => candidate.Name == "F");
    var analysis = new ScalarEvolution(function);
    // The consumer may stop after any yield, so ten passes are only the most.
    Assert.That(analysis.TripCountOf(analysis.Loops.Single()), Is.EqualTo(new TripCount.Max(10)));
  }

  [Test]
  public void APointInANestedBlockOfTheBody_SeesTheStepsBeforeItsStatement() {
    var function = Method("public static int F(int x) { int s = 0; for (int i = 0; i < 10; i++) { if (x > 0) { s += 2; } s++; } return s; }");
    var analysis = new ScalarEvolution(function);
    var loop = (IrWhile)analysis.Loops[0];
    var nested = ((IrIf)loop.Body.Statements[0]).Then;
    Assert.That(analysis.At(Read(function, "i"), ProgramPoint.End(nested), loop).ToString(), Is.EqualTo("AddRec(start 0, step 1) nuw nsw"));
  }

  [Test]
  public void SumsAndProductsWithAnUnknownPart_AreUnknown() {
    var function = Method("public static int F() { int s = 0; for (int i = 0; i < 10; i++) s++; return s; }");
    var analysis = new ScalarEvolution(function);
    var (loop, i) = (analysis.Loops[0], Read(function, "i"));
    var square = new IrBinary(IrBinaryOp.Multiply, i, i);
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.Add, square, new IrConst(IrType.I32, 1)), loop), Is.SameAs(Evolution.Unknown));
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.Multiply, square, new IrConst(IrType.I32, 2)), loop), Is.SameAs(Evolution.Unknown));
  }

  [Test]
  public void ABoundedTestAndAnExactOne_GiveTheSmallerBound()
    => Assert.That(Count("public static int F(int n) { int s = 0; if (n < 50) { for (int i = 0, j = 0; i < n && j < 10; i++, j++) s++; } return s; }"), Is.EqualTo("Max(10)"));

  [TestCase("!(i < 10)", "i += -1", 20, "Exact(11)")]
  [TestCase("!(i <= 10)", "i += -1", 20, "Exact(10)")]
  [TestCase("!(i > 10)", "i++", 0, "Exact(11)")]
  public void EveryNegatedOrder_IsTheOppositeComparison(string test, string step, int start, string count)
    => Assert.That(Count($"public static int F() {{ int s = 0; for (int i = {start}; {test}; {step}) s++; return s; }}"), Is.EqualTo(count));

  [Test]
  public void AStepBlockThatSetsAnotherValue_GivesItNoDefinition() {
    var function = Method("public static int F() { int s = 0; int d = 0; for (int i = 0; i < 10; i++, d = i * 2) { if (s++ > 3) continue; } return s + d; }");
    var analysis = new ScalarEvolution(function);
    var loop = analysis.Loops[0];
    Assert.That(loop, Is.InstanceOf<IrFor>());
    Assert.That(analysis.Of(Read(function, "d"), loop), Is.SameAs(Evolution.Unknown));
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Exact(10)));
  }

  [Test]
  public void AnInnerCounterSetByAnAssignment_StartsAtItsOuterRecurrence() {
    var function = Method("public static int F() { int s = 0; int j = 0; for (int i = 0; i < 10; i++) { j = i * 4; while (j * 2 < 100) { s++; j++; } } return s; }");
    var analysis = new ScalarEvolution(function);
    var inner = analysis.Loops[1];
    Assert.That(((Evolution.AddRec)analysis.Of(Read(function, "j"), inner)).Start.ToString(), Is.EqualTo("AddRec(start 0, step 4)"));
    // The test reads j * 2, whose start is a recurrence of the outer loop: no range bounds it.
    Assert.That(analysis.TripCountOf(inner), Is.InstanceOf<TripCount.Max>());
  }

  [Test]
  public void TheElementOfAForEach_IsWritten() {
    var function = Method("public static int F(System.Collections.Generic.List<int> list) { int s = 0; for (int i = 0; i < 10; i++) { foreach (var v in list) s += v; } return s; }");
    var analysis = new ScalarEvolution(function);
    Assert.That(analysis.TripCountOf(analysis.Loops[0]), Is.EqualTo(new TripCount.Exact(10)));
  }
}
