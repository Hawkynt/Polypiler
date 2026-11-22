using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis;
using Polypiler.Core.Analysis.Scev;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Analysis.Scev;

/// <summary>The shapes of the tree IR the C# frontend does not write, built by hand: steps as expressions, every kind of write and exit.</summary>
[TestFixture]
public sealed class ScalarEvolutionShapeTests {

  private static readonly IrLocal _i = new("i", IrType.I32);
  private static readonly IrLocal _x = new("x", IrType.I32);

  private static IrExpr I => new IrLocalRef(_i);

  private static IrConst Int(long value) => new(IrType.I32, value);

  /// <summary><c>int i = 0; while (test) { body; step }</c> with a parameter x.</summary>
  private static (ScalarEvolution Analysis, IrStmt Loop, IrFunction Function) Loop(IrExpr test, IReadOnlyList<IrStmt> body, IReadOnlyList<IrLocal>? locals = null) {
    var loop = new IrWhile(test, new IrBlock(body));
    var function = new IrFunction("F", true, IrType.Void, [_x], [_i, .. locals ?? []], new IrBlock([new IrDeclare(_i, Int(0)), loop, new IrReturn(null)]));
    var analysis = new ScalarEvolution(function);
    return (analysis, analysis.Loops[0], function);
  }

  private static IrStmt Increment(bool up = true, bool trap = false) => new IrExprStmt(new IrStep(I, up, false, trap));

  private static IrExpr Below(long bound) => new IrBinary(IrBinaryOp.Less, I, Int(bound));

  [Test]
  public void AnIncrementStatement_IsAStepOfOne() {
    var (analysis, loop, _) = Loop(Below(10), [Increment()]);
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Exact(10)));
    Assert.That(analysis.Of(I, loop).ToString(), Is.EqualTo("AddRec(start 0, step 1) nuw nsw"));
  }

  [Test]
  public void ACheckedDecrement_IsANegativeStepThatCannotWrap() {
    var (analysis, loop, _) = Loop(new IrBinary(IrBinaryOp.NotEqual, I, new IrLocalRef(_x)), [Increment(up: false, trap: true)]);
    Assert.That(analysis.Of(I, loop).ToString(), Is.EqualTo("AddRec(start 0, step -1) nsw"));
    Assert.That(analysis.TripCountOf(loop), Is.SameAs(TripCount.Unknown));
  }

  [Test]
  public void AnIncrementInsideAnExpression_IsAWriteButNoStep() {
    var (analysis, loop, _) = Loop(Below(10), [new IrExprStmt(new IrBinary(IrBinaryOp.Add, new IrStep(I, true, true, false), Int(1)))]);
    Assert.That(analysis.InductionVariables(loop), Is.Empty);
  }

  [TestCase(IrBinaryOp.Equal, "Exact(10)")]
  [TestCase(IrBinaryOp.NotEqual, "Unknown")]
  public void ANegatedTest_IsTheOppositeComparison(IrBinaryOp op, string count) {
    // while (!(i == 10)) is while (i != 10); while (!(i != 10)) is while (i == 10), which no rule counts.
    var (analysis, loop, _) = Loop(new IrUnary(IrUnaryOp.LogicalNot, new IrBinary(op, I, Int(10))), [Increment()]);
    Assert.That(analysis.TripCountOf(loop).ToString(), Is.EqualTo(count));
  }

  [Test]
  public void AnOtherTest_IsNotCounted() {
    var (equal, equalLoop, _) = Loop(new IrBinary(IrBinaryOp.Equal, I, Int(0)), [Increment()]);
    Assert.That(equal.TripCountOf(equalLoop), Is.SameAs(TripCount.Unknown));
    var flag = new IrLocal("flag", IrType.Bool);
    var (analysis, loop, _) = Loop(new IrLocalRef(flag), [Increment()], [flag]);
    Assert.That(analysis.TripCountOf(loop), Is.SameAs(TripCount.Unknown));
    var (mixed, mixedLoop, _) = Loop(new IrBinary(IrBinaryOp.Less, I, I), [Increment()]);
    Assert.That(mixed.TripCountOf(mixedLoop), Is.SameAs(TripCount.Unknown), "the bound changes with the counter");
  }

  [Test]
  public void EveryKindOfWrite_IsSeen() {
    var field = new IrField("v", IrType.I32, false);
    var s = new IrLocal("s", IrType.Struct(new IrStructType("P", [field])));
    var e = new IrLocal("e", IrType.I32);
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var (analysis, loop, _) = Loop(Below(10), [
      new IrFieldStore(new IrLocalRef(s), field, Int(1)),
      new IrForEachSequence(e, new IrLocalRef(array), new IrBlock([])),
      new IrExprStmt(new IrBind(_x, Int(3))),
      Increment()
    ], [s, e, array]);
    Assert.That(analysis.Of(new IrLocalRef(_x), loop), Is.SameAs(Evolution.Unknown), "x is bound in the body");
    Assert.That(analysis.Of(new IrLocalRef(e), loop), Is.SameAs(Evolution.Unknown));
    Assert.That(analysis.Of(I, loop), Is.InstanceOf<Evolution.AddRec>());
  }

  [Test]
  public void ExitsOtherThanTheTest_MakeTheCountABound() {
    foreach (var exit in new IrStmt[] { new IrReturn(null), new IrBreak(), new IrBlock([new IrBreak()]) }) {
      var (analysis, loop, _) = Loop(Below(10), [new IrIf(new IrBinary(IrBinaryOp.Equal, I, Int(3)), new IrBlock([exit]), null), Increment()]);
      Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Max(10)), exit.GetType().Name);
    }
  }

  [Test]
  public void ABreakOfAnInnerLoop_LeavesOnlyThatLoop() {
    var inner = new IrWhile(new IrConst(IrType.Bool, 1), new IrBlock([new IrBreak()]));
    var (analysis, loop, _) = Loop(Below(10), [inner, Increment()]);
    Assert.That(analysis.Loops, Has.Count.EqualTo(2));
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Exact(10)));
    Assert.That(analysis.TripCountOf(analysis.Loops[1]), Is.SameAs(TripCount.Unknown));
  }

  [Test]
  public void AContinueInANestedBlock_CanSkipAStep() {
    var (analysis, loop, _) = Loop(Below(10), [new IrBlock([new IrIf(new IrLocalRef(new IrLocal("b", IrType.Bool)), new IrBlock([new IrContinue()]), null)]), Increment()]);
    Assert.That(analysis.InductionVariables(loop), Is.Empty);
  }

  [Test]
  public void ARecurrenceOfAnotherLoop_DoesNotAddToThisLoopsOnes() {
    var j = new IrLocal("j", IrType.I32);
    var innerLoop = new IrWhile(new IrBinary(IrBinaryOp.Less, new IrLocalRef(j), Int(5)), new IrBlock([new IrAssign(j, new IrBinary(IrBinaryOp.Add, new IrLocalRef(j), Int(1)))]));
    var outer = new IrWhile(Below(10), new IrBlock([new IrAssign(j, I), innerLoop, Increment()]));
    var function = new IrFunction("F", true, IrType.Void, [_x], [_i, j], new IrBlock([new IrDeclare(_i, Int(0)), outer, new IrReturn(null)]));
    var analysis = new ScalarEvolution(function);
    var inner = analysis.Loops[1];
    var start = (Evolution.AddRec)((Evolution.AddRec)analysis.Of(new IrLocalRef(j), inner)).Start;
    Assert.That(start.Loop, Is.SameAs(outer));
    // j + i: a recurrence of the inner loop plus one whose start is the outer loop's.
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.Add, new IrLocalRef(j), I), inner), Is.InstanceOf<Evolution.AddRec>());
  }

  [Test]
  public void AWideningWithASymbolicStep_IsUnknown() {
    var (analysis, loop, _) = Loop(Below(10), [new IrAssign(_i, new IrBinary(IrBinaryOp.Add, I, new IrLocalRef(_x)))]);
    Assert.That(analysis.Of(new IrConvert(IrType.I64, I), loop), Is.SameAs(Evolution.Unknown));
    Assert.That(analysis.Of(new IrConvert(IrType.I64, new IrLocalRef(_x)), loop), Is.EqualTo(new Evolution.Invariant(new IrConvert(IrType.I64, new IrLocalRef(_x)))));
  }

  [Test]
  public void AProductOfMismatchedTypes_IsUnknown() {
    var (analysis, loop, _) = Loop(Below(10), [Increment()]);
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.Multiply, new IrBinary(IrBinaryOp.Multiply, I, Int(2)), new IrLocalRef(_x)), loop), Is.InstanceOf<Evolution.AddRec>());
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.ShiftLeft, I, Int(40)), loop), Is.SameAs(Evolution.Unknown), "a shift past the width is no product");
    Assert.That(analysis.Of(new IrBinary(IrBinaryOp.Remainder, I, Int(3)), loop), Is.SameAs(Evolution.Unknown));
  }

  [Test]
  public void AnUnreachableLoop_RunsNever() {
    var (analysis, loop, function) = Loop(Below(10), [Increment()]);
    var unreachable = new IrIf(new IrConst(IrType.Bool, 0), new IrBlock([loop]), null);
    var guarded = function with { Body = new IrBlock([new IrDeclare(_i, Int(0)), unreachable, new IrReturn(null)]) };
    var guardedAnalysis = new ScalarEvolution(guarded);
    Assert.That(guardedAnalysis.TripCountOf(guardedAnalysis.Loops.Single()), Is.EqualTo(new TripCount.Exact(0)));
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Exact(10)));
  }

  [Test]
  public void TheRecurrenceRecords_CompareByLoopNodeAndPrintTheirParts() {
    var (analysis, loop, _) = Loop(Below(10), [Increment()]);
    var recurrence = (Evolution.AddRec)analysis.Of(I, loop);
    Assert.That(recurrence.GetHashCode(), Is.EqualTo(((Evolution.AddRec)analysis.Of(I, loop)).GetHashCode()));
    Assert.That(recurrence with { Loop = new IrWhile(Below(10), new IrBlock([Increment()])) }, Is.Not.EqualTo(recurrence), "an equal loop elsewhere is another loop");
    Assert.That(recurrence.Equals(null), Is.False);
    Assert.That(new Evolution.Invariant(new IrBinary(IrBinaryOp.Add, I, Int(1))).IsConstant(out _), Is.False);
    Assert.That(TripCount.Unknown.ToString(), Is.EqualTo("Unknown"));
    Assert.That(new TripCount.Exact(3).ToString(), Is.EqualTo("Exact(3)"));
  }

  [Test]
  public void AStepOfAnArrayElement_WritesNoVariable() {
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var (analysis, loop, _) = Loop(Below(10), [new IrExprStmt(new IrStep(new IrArrayLoad(new IrLocalRef(array), Int(0)), true, false, false)), Increment()], [array]);
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Exact(10)));
  }

  [Test]
  public void AStoreIntoAFieldOfAField_WritesTheOuterVariable() {
    var inner = new IrField("v", IrType.I32, false);
    var innerType = IrType.Struct(new IrStructType("Q", [inner]));
    var outer = new IrField("q", innerType, false);
    var p = new IrLocal("p", IrType.Struct(new IrStructType("P", [outer])));
    var (analysis, loop, _) = Loop(Below(10), [new IrFieldStore(new IrFieldLoad(new IrLocalRef(p), outer), inner, Int(1)), Increment()], [p]);
    Assert.That(analysis.Of(new IrFieldLoad(new IrFieldLoad(new IrLocalRef(p), outer), inner), loop), Is.SameAs(Evolution.Unknown));
  }

  [Test]
  public void TheStepPutBeforeTheVariable_IsAStepToo() {
    var (analysis, loop, _) = Loop(Below(10), [new IrAssign(_i, new IrBinary(IrBinaryOp.Add, Int(2), I))]);
    Assert.That(analysis.TripCountOf(loop), Is.EqualTo(new TripCount.Exact(5)));
    var (other, otherLoop, _) = Loop(Below(10), [new IrAssign(_i, new IrBinary(IrBinaryOp.Subtract, Int(2), I))]);
    Assert.That(other.InductionVariables(otherLoop), Is.Empty, "2 - i is no step of i");
    var (checkedDown, checkedLoop, _) = Loop(new IrBinary(IrBinaryOp.Greater, I, Int(-10)), [new IrAssign(_i, new IrBinary(IrBinaryOp.SubtractChecked, I, Int(1)))]);
    Assert.That(checkedDown.Of(I, checkedLoop).ToString(), Is.EqualTo("AddRec(start 0, step -1) nsw"), "ten passes from 0 down to -10 wrap as unsigned at once, never as signed");
  }

  [Test]
  public void AnAssignmentOfAnotherSum_IsADefinitionAndNoStep() {
    var (analysis, loop, _) = Loop(Below(10), [new IrAssign(_i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(_x), Int(1)))]);
    Assert.That(analysis.InductionVariables(loop), Is.Empty);
    Assert.That(analysis.At(I, ProgramPoint.End(((IrWhile)loop).Body), loop), Is.InstanceOf<Evolution.Invariant>());
  }
}
