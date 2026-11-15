using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;

namespace Polypiler.Tests.Helpers;

/// <summary>
/// The passes over a legalized module, each on hand-built IR: where it fires, the boundaries of its
/// limits on, inside and outside them, the cases where it must not fire, and that the module computes
/// the same before and after (test design 2 to 5 of #1353).
/// </summary>
[TestFixture]
public sealed class IrPassTests {

  private static readonly IrLocal _x = new("x", IrType.I32);
  private static readonly IrLocal _p = new("p", IrType.I32);

  private static IrExpr X => new IrLocalRef(_x);

  private static IrExpr P => new IrLocalRef(_p);

  private static IrConst Int(long value) => new(IrType.I32, value);

  private static IrExpr Op(IrBinaryOp op, IrExpr left, IrExpr right) => new IrBinary(op, left, right);

  private static IrBlock Block(params IrStmt[] statements) => new(statements);

  /// <summary>A module of <c>F(x)</c> and the library function <c>H(p)</c>, the one legalization would have copied in.</summary>
  private static IrModule Module(IrBlock body, IrBlock helper, IReadOnlyList<IrLocal>? locals = null, IReadOnlyList<IrLocal>? helperLocals = null, IrType? returns = null)
    => new("M", [
      new IrFunction("F", true, IrType.I32, [_x], locals ?? [], body),
      new IrFunction("H", false, returns ?? IrType.I32, [_p], helperLocals ?? [], helper)
    ]) { Implementations = [("H", "Library")] };

  private static IrCall CallH(IrExpr argument) => new("H", IrType.I32, [argument]);

  private static (IrModule Module, List<string> Applied) Run(IIrPass pass, IrModule module, Func<IrFunction, bool>? scope = null) {
    var applied = new List<string>();
    var result = pass.Run(module, scope ?? (_ => true), (function, position) => applied.Add($"{function}@{position}"));
    Assert.That(IrVerifier.Verify(result), Is.Empty);
    return (result, applied);
  }

  private static void SameOutcomes(IrModule before, IrModule after, params int[] inputs) {
    foreach (var input in inputs)
      Assert.That(Outcome(after, input), Is.EqualTo(Outcome(before, input)), $"F({input})");
  }

  private static string Outcome(IrModule module, int input) {
    try {
      return Convert.ToString(IrInterpreter.Call(module, "F", [input]), System.Globalization.CultureInfo.InvariantCulture)!;
    } catch (IrException exception) {
      return "throws " + exception.Class.Name;
    }
  }

  private static bool Calls(IrModule module, string function, string callee)
    => module.Functions.Single(candidate => candidate.Name == function).Body.Statements.SelectMany(IrTree.Nodes).OfType<IrCall>().Any(call => call.Function == callee);

  private static readonly int[] _inputs = [0, 1, -1, 7, -9, 100, int.MaxValue, int.MinValue];

  // ---------------------------------------------------------------- O0006 inlining

  [Test]
  public void Inlining_ReplacesTheCallOfALibraryFunctionByItsBody() {
    var module = Module(Block(new IrReturn(Op(IrBinaryOp.Add, CallH(X), Int(1)))), Block(new IrReturn(Op(IrBinaryOp.Multiply, P, Int(2)))));
    var (inlined, applied) = Run(new O0006Inlining(), module);
    Assert.That(Calls(inlined, "F", "H"), Is.False);
    Assert.That(applied, Does.Contain("F@1"));
    Assert.That(inlined.Inlined, Is.EqualTo(new[] { "Library" }));
    SameOutcomes(module, inlined, _inputs);
  }

  /// <summary>The budget: a body of <see cref="O0006Inlining.Budget"/> is inlined, one just over it is not.</summary>
  [TestCase(31, true)]
  [TestCase(32, false)]
  public void Inlining_KeepsToItsBudget(int additions, bool inlined) {
    IrExpr sum = P;
    for (var index = 0; index < additions; ++index)
      sum = Op(IrBinaryOp.Add, sum, Int(index));

    var helper = Block(new IrReturn(sum));
    Assert.That(IrPassSupportProbe.Size(helper), Is.EqualTo(2 * additions + 2));
    var module = Module(Block(new IrReturn(CallH(X))), helper);
    var (result, _) = Run(new O0006Inlining(), module);
    Assert.That(Calls(result, "F", "H"), Is.EqualTo(!inlined));
    SameOutcomes(module, result, _inputs);
  }

  /// <summary>
  /// The cost rule: a body over <see cref="O0006Inlining.Small"/> called from two places is inlined only
  /// where the call has a constant argument; one of <see cref="O0006Inlining.Small"/> everywhere.
  /// </summary>
  [TestCase(12, false, false)]
  [TestCase(12, true, true)]
  [TestCase(11, false, true)]
  public void Inlining_CopiesALargerBodyOnlyWhereItPays(int additions, bool constant, bool inlined) {
    IrExpr sum = P;
    for (var index = 0; index < additions; ++index)
      sum = Op(IrBinaryOp.Add, sum, Int(index));

    var helper = Block(new IrReturn(sum));
    Assert.That(IrPassSupportProbe.Size(helper) > O0006Inlining.Small, Is.EqualTo(additions > 11));
    var module = Module(Block(new IrReturn(Op(IrBinaryOp.Add, CallH(constant ? Int(5) : X), CallH(X)))), helper);
    var (result, _) = Run(new O0006Inlining(), module);
    var left = result.Functions[0].Body.Statements.SelectMany(IrTree.Nodes).OfType<IrCall>().Count();
    Assert.That(left, Is.EqualTo(inlined ? (constant ? 1 : 0) : 2), "with a constant, only the call that has it is copied");
    SameOutcomes(module, result, _inputs);
  }

  [Test]
  public void Inlining_NeverInlinesARecursiveHelper() {
    var module = Module(Block(new IrReturn(CallH(X))), Block(
      new IrIf(Op(IrBinaryOp.LessOrEqual, P, Int(0)), Block(new IrReturn(Int(0))), null),
      new IrReturn(CallH(Op(IrBinaryOp.Subtract, P, Int(1))))));
    Assert.That(Run(new O0006Inlining(), module).Module, Is.SameAs(module));
  }

  [Test]
  public void Inlining_LeavesTheProgramsOwnFunctions() {
    var module = Module(Block(new IrReturn(CallH(X))), Block(new IrReturn(P))) with { Implementations = [] };
    Assert.That(Run(new O0006Inlining(), module).Module, Is.SameAs(module));
  }

  [Test]
  public void Inlining_MakesTheReturnsOfAChoiceAssignments() {
    var helper = Block(
      new IrIf(Op(IrBinaryOp.Less, P, Int(0)), Block(new IrReturn(Int(-1))), null),
      new IrIf(Op(IrBinaryOp.Equal, P, Int(0)), Block(new IrReturn(Int(0))), Block(new IrReturn(Int(1)))));
    var module = Module(Block(new IrReturn(Op(IrBinaryOp.Multiply, CallH(X), Int(3)))), helper);
    var (inlined, _) = Run(new O0006Inlining(), module);
    Assert.That(Calls(inlined, "F", "H"), Is.False);
    SameOutcomes(module, inlined, _inputs);
  }

  [Test]
  public void Inlining_KeepsACallWhoseReturnStandsInALoop() {
    var helper = Block(new IrWhile(new IrConst(IrType.Bool, 1), Block(new IrReturn(P))));
    var module = Module(Block(new IrReturn(CallH(X))), helper);
    Assert.That(Run(new O0006Inlining(), module).Module, Is.SameAs(module));
  }

  [Test]
  public void Inlining_KeepsACallInTheConditionOfALoop() {
    var module = Module(Block(new IrWhile(Op(IrBinaryOp.Less, CallH(X), Int(0)), Block(new IrAssign(_x, Op(IrBinaryOp.Add, X, Int(1))))), new IrReturn(X)),
      Block(new IrReturn(P)));
    Assert.That(Calls(Run(new O0006Inlining(), module).Module, "F", "H"), Is.True);
  }

  [Test]
  public void Inlining_KeepsACallAfterAnOperandThatMayTrapOrActFirst() {
    // x / 0 traps before H runs; H would run first were it moved in front of the statement.
    var module = Module(Block(new IrReturn(Op(IrBinaryOp.Add, Op(IrBinaryOp.Divide, X, Int(0)), CallH(X)))), Block(new IrReturn(P)));
    Assert.That(Calls(Run(new O0006Inlining(), module).Module, "F", "H"), Is.True);
    var lazy = Module(Block(new IrReturn(new IrConditional(Op(IrBinaryOp.Less, X, Int(0)), CallH(X), Int(0)))), Block(new IrReturn(P)));
    Assert.That(Calls(Run(new O0006Inlining(), lazy).Module, "F", "H"), Is.True, "a branch of a choice runs only where it is taken");
  }

  [Test]
  public void Inlining_TakesACallInAnArgumentFirst() {
    var module = Module(Block(new IrReturn(CallH(CallH(X)))), Block(new IrReturn(Op(IrBinaryOp.Add, P, Int(1)))));
    var (inlined, applied) = Run(new O0006Inlining(), module);
    Assert.That(Calls(inlined, "F", "H"), Is.False);
    Assert.That(applied, Has.Count.EqualTo(2));
    SameOutcomes(module, inlined, _inputs);
  }

  [Test]
  public void Inlining_OfAFunctionWithoutResult_KeepsItsEffect() {
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var store = new IrStore(new IrLocalRef(array), Int(0), Int(5));
    var module = new IrModule("M", [
      new IrFunction("F", true, IrType.I32, [_x], [array], Block(
        new IrDeclare(array, new IrNewArray(IrType.Array(IrType.I32), Int(1))),
        new IrExprStmt(new IrCall("H", IrType.Void, [new IrLocalRef(array)])),
        new IrReturn(new IrArrayLoad(new IrLocalRef(array), Int(0))))),
      new IrFunction("H", false, IrType.Void, [array], [], Block(store, new IrReturn(null)))
    ]) { Implementations = [("H", "Library")] };
    var (inlined, _) = Run(new O0006Inlining(), module);
    Assert.That(Calls(inlined, "F", "H"), Is.False);
    SameOutcomes(module, inlined, 3);
  }

  // ---------------------------------------------------------------- O0003 constant propagation

  [Test]
  public void Propagation_CarriesAConstantForward() {
    var y = new IrLocal("y", IrType.I32);
    var module = Module(Block(new IrDeclare(y, Int(3)), new IrAssign(_x, Op(IrBinaryOp.Add, X, new IrLocalRef(y))), new IrReturn(Op(IrBinaryOp.Multiply, new IrLocalRef(y), X))),
      Block(new IrReturn(P)), [y]);
    var (result, applied) = Run(new O0003ConstantPropagation(), module);
    Assert.That(applied, Is.Not.Empty);
    Assert.That(result.Functions[0].Body.Statements.SelectMany(IrTree.Nodes).OfType<IrLocalRef>().Any(reference => reference.Local == y), Is.False);
    SameOutcomes(module, result, _inputs);
  }

  [Test]
  public void Propagation_KeepsWhatBothBranchesAgreeOn() {
    var y = new IrLocal("y", IrType.I32);
    var z = new IrLocal("z", IrType.I32);
    var module = Module(Block(
      new IrDeclare(y, Int(1)), new IrDeclare(z, Int(1)),
      new IrIf(Op(IrBinaryOp.Less, X, Int(0)), Block(new IrAssign(z, Int(2))), Block(new IrAssign(z, Int(3)))),
      new IrReturn(Op(IrBinaryOp.Add, new IrLocalRef(y), new IrLocalRef(z)))), Block(new IrReturn(P)), [y, z]);
    var (result, _) = Run(new O0003ConstantPropagation(), module);
    var last = (IrReturn)result.Functions[0].Body.Statements[^1];
    Assert.That(IrTree.Nodes(last.Value!).OfType<IrLocalRef>().Select(reference => reference.Local), Is.EqualTo(new[] { z }), "y is 1 on every way, z is not");
    SameOutcomes(module, result, _inputs);
  }

  [Test]
  public void Propagation_ForgetsWhatALoopWrites() {
    var y = new IrLocal("y", IrType.I32);
    var module = Module(Block(
      new IrDeclare(y, Int(0)),
      new IrWhile(Op(IrBinaryOp.Less, new IrLocalRef(y), X), Block(new IrAssign(y, Op(IrBinaryOp.Add, new IrLocalRef(y), Int(1))))),
      new IrReturn(new IrLocalRef(y))), Block(new IrReturn(P)), [y]);
    var (result, _) = Run(new O0003ConstantPropagation(), module);
    Assert.That(result.Functions[0].Body.Statements[^1], Is.EqualTo(module.Functions[0].Body.Statements[^1]));
    SameOutcomes(module, result, 0, 3, -2);
  }

  /// <summary>
  /// A foreach over a sequence writes in its body what is unknown after it; the passes walk into it and
  /// into the values a function yields.
  /// </summary>
  [Test]
  public void Propagation_ForgetsWhatAForEachOverASequenceWrites() {
    var count = new IrLocal("count", IrType.I32);
    var element = new IrLocal("element", IrType.I32);
    var sequence = new IrArraySequence(new IrNewArray(IrType.Array(IrType.I32), Op(IrBinaryOp.Add, Int(1), Int(2))));
    var module = Module(Block(
      new IrDeclare(count, Int(1)),
      new IrForEachSequence(element, sequence, Block(new IrAssign(count, Op(IrBinaryOp.Add, new IrLocalRef(count), X)))),
      new IrReturn(new IrLocalRef(count))), Block(new IrReturn(P)), [count, element]);
    var (result, _) = Run(new O0003ConstantPropagation(), module);
    Assert.That(result.Functions[0].Body.Statements[^1], Is.EqualTo(new IrReturn(new IrLocalRef(count))), "the loop writes count");
    SameOutcomes(module, result, _inputs);
    var (folded, _) = Run(new O0001ConstantFolding(), module);
    var loop = (IrForEachSequence)folded.Functions[0].Body.Statements[1];
    Assert.That(((IrArraySequence)loop.Sequence).Array, Is.EqualTo(new IrNewArray(IrType.Array(IrType.I32), Int(3))), "folding reaches into the sequence");
    var iterator = new IrFunction("G", true, IrType.Sequence(IrType.I32, true), [], [],
      Block(new IrYield(Op(IrBinaryOp.Add, Int(1), Int(2))), new IrYieldFrom(sequence)));
    var (yields, _) = Run(new O0001ConstantFolding(), module with { Functions = [.. module.Functions, iterator] });
    var body = yields.Functions.Single(function => function.Name == "G").Body.Statements;
    Assert.That(body[0], Is.EqualTo(new IrYield(Int(3))));
    Assert.That(((IrArraySequence)((IrYieldFrom)body[1]).Sequence).Array, Is.EqualTo(new IrNewArray(IrType.Array(IrType.I32), Int(3))));
  }

  [Test]
  public void Propagation_MakesACopyTheOriginalAndAValueReadOnceItsUse() {
    var copy = new IrLocal("c", IrType.I32);
    var once = new IrLocal("o", IrType.I32);
    var module = Module(Block(
      new IrDeclare(copy, X),
      new IrDeclare(once, Op(IrBinaryOp.Add, new IrLocalRef(copy), Int(5))),
      new IrReturn(Op(IrBinaryOp.Multiply, new IrLocalRef(once), Int(2)))), Block(new IrReturn(P)), [copy, once]);
    var (result, _) = Run(new O0003ConstantPropagation(), module);
    Assert.That(result.Functions[0].Body.Statements, Has.Count.EqualTo(1));
    SameOutcomes(module, result, _inputs);
  }

  [Test]
  public void Propagation_LeavesAVariableALambdaNames() {
    var y = new IrLocal("y", IrType.I32);
    var f = new IrLocal("f", IrType.Function(IrType.I32));
    var lambda = new IrLambda(IrType.Function(IrType.I32), [y], [], [], Block(new IrReturn(new IrLocalRef(y))));
    var module = Module(Block(new IrDeclare(y, Int(4)), new IrDeclare(f, lambda), new IrReturn(new IrInvoke(IrType.I32, new IrLocalRef(f), []))), Block(new IrReturn(P)), [y, f]);
    var (result, applied) = Run(new O0003ConstantPropagation(), module);
    Assert.That(applied, Has.None.StartsWith("F@"));
    Assert.That(result, Is.SameAs(module));
  }

  // ---------------------------------------------------------------- O0001 constant folding

  [Test]
  public void Folding_ComputesWhatHasConstantOperands() {
    var module = Module(Block(new IrReturn(Op(IrBinaryOp.Add, X, Op(IrBinaryOp.ShiftLeft, Int(3), Int(4))))), Block(new IrReturn(P)));
    var (result, applied) = Run(new O0001ConstantFolding(), module);
    Assert.That(((IrBinary)((IrReturn)result.Functions[0].Body.Statements[0]).Value!).Right, Is.EqualTo(Int(48)));
    Assert.That(applied, Does.Contain("F@1"));
  }

  [Test]
  public void Folding_LeavesATrapWhereItIs() {
    var module = Module(Block(new IrReturn(Op(IrBinaryOp.Divide, Int(1), Int(0)))), Block(new IrReturn(P)));
    Assert.That(Run(new O0001ConstantFolding(), module).Module, Is.SameAs(module));
  }

  [TestCase(IrBinaryOp.Add, 0L, true)]
  [TestCase(IrBinaryOp.Or, 0L, true)]
  [TestCase(IrBinaryOp.Xor, 0L, true)]
  [TestCase(IrBinaryOp.ShiftLeft, 0L, true)]
  [TestCase(IrBinaryOp.ShiftRightLogical, 0L, true)]
  [TestCase(IrBinaryOp.Multiply, 1L, true)]
  [TestCase(IrBinaryOp.And, -1L, true)]
  [TestCase(IrBinaryOp.And, 0L, false)]
  [TestCase(IrBinaryOp.Multiply, 0L, false)]
  [TestCase(IrBinaryOp.Add, 1L, null)]
  public void Folding_DropsANeutralOperand(IrBinaryOp op, long constant, bool? toOperand) {
    var expression = Op(op, X, Int(constant));
    var folded = O0001ConstantFolding.Fold(expression);
    Assert.That(folded, toOperand switch { true => Is.EqualTo(X), false => Is.EqualTo(Int(0)), _ => Is.Null });
  }

  [Test]
  public void Folding_TakesTheBranchOfAConstantChoice() {
    Assert.That(O0001ConstantFolding.Fold(new IrConditional(new IrConst(IrType.Bool, 1), X, Int(2))), Is.EqualTo(X));
    Assert.That(O0001ConstantFolding.Fold(new IrConditional(new IrConst(IrType.Bool, 0), X, Int(2))), Is.EqualTo(Int(2)));
    var truth = new IrBinary(IrBinaryOp.Less, X, Int(0));
    Assert.That(O0001ConstantFolding.Fold(Op(IrBinaryOp.LogicalAnd, new IrConst(IrType.Bool, 1), truth)), Is.EqualTo(truth));
    Assert.That(O0001ConstantFolding.Fold(Op(IrBinaryOp.LogicalOr, new IrConst(IrType.Bool, 1), truth)), Is.EqualTo(new IrConst(IrType.Bool, 1)));
    Assert.That(O0001ConstantFolding.Fold(Op(IrBinaryOp.LogicalAnd, truth, new IrConst(IrType.Bool, 0))), Is.EqualTo(new IrConst(IrType.Bool, 0)));
  }

  [Test]
  public void Folding_UndoesAWideningAndAMaskAWrapKeeps() {
    var narrow = new IrLocal("n", IrType.U16);
    var widened = new IrConvert(IrType.U32, new IrLocalRef(narrow));
    Assert.That(O0001ConstantFolding.Fold(new IrConvert(IrType.U16, widened)), Is.EqualTo(new IrLocalRef(narrow)));
    Assert.That(O0001ConstantFolding.Fold(Op(IrBinaryOp.And, widened, new IrConst(IrType.U32, 0xFFFF))), Is.EqualTo(widened));
    var masked = new IrConvert(IrType.U8, Op(IrBinaryOp.And, X, Int(0x1FF)));
    Assert.That(O0001ConstantFolding.Fold(masked), Is.EqualTo(new IrConvert(IrType.U8, X)));
    Assert.That(O0001ConstantFolding.Fold(new IrConvert(IrType.U8, Op(IrBinaryOp.And, X, Int(0x7F)))), Is.Null, "a mask that clears a kept bit stays");
  }

  // ---------------------------------------------------------------- O0005 dead code

  [Test]
  public void DeadCode_KeepsTheBranchAConstantChoiceTakes() {
    var y = new IrLocal("y", IrType.I32);
    var module = Module(Block(
      new IrIf(new IrConst(IrType.Bool, 1), Block(new IrDeclare(y, X), new IrAssign(_x, Op(IrBinaryOp.Add, new IrLocalRef(y), Int(1)))), Block(new IrAssign(_x, Int(9)))),
      new IrIf(new IrConst(IrType.Bool, 0), Block(new IrAssign(_x, Int(7))), null),
      new IrReturn(X)), Block(new IrReturn(P)), [y]);
    var (result, _) = Run(new O0005DeadCode(), module);
    Assert.That(result.Functions[0].Body.Statements.SelectMany(IrStatementsProbe.All).OfType<IrIf>(), Is.Empty);
    Assert.That(result.Functions[0].Body.Statements[0], Is.InstanceOf<IrBlock>(), "a branch that declares stays a block of its own");
    SameOutcomes(module, result, _inputs);
  }

  [Test]
  public void DeadCode_RemovesWhatNothingReachesOrReads() {
    var unread = new IrLocal("u", IrType.I32);
    var trapping = new IrLocal("t", IrType.I32);
    var module = Module(Block(
      new IrDeclare(unread, Op(IrBinaryOp.Add, X, Int(1))),
      new IrDeclare(trapping, Op(IrBinaryOp.Divide, Int(1), X)),
      new IrWhile(new IrConst(IrType.Bool, 0), Block(new IrAssign(_x, Int(3)))),
      new IrReturn(X),
      new IrAssign(_x, Int(4))), Block(new IrReturn(P)), [unread, trapping]);
    var (result, _) = Run(new O0005DeadCode(), module);
    var statements = result.Functions[0].Body.Statements;
    Assert.That(statements, Has.Count.EqualTo(2), "the division may trap and stays; the unread sum, the loop and what follows the return go");
    Assert.That(((IrDeclare)statements[0]).Local, Is.EqualTo(trapping));
    SameOutcomes(module, result, 0, 2);
  }

  [Test]
  public void DeadCode_RemovesALibraryFunctionNothingCalls() {
    var module = Module(Block(new IrReturn(X)), Block(new IrReturn(P)));
    var (result, applied) = Run(new O0005DeadCode(), module);
    Assert.That(result.Functions.Select(function => function.Name), Is.EqualTo(new[] { "F" }));
    Assert.That(result.Implementations, Is.Empty);
    Assert.That(applied, Does.Contain("H@0"));
    var called = Module(Block(new IrReturn(CallH(X))), Block(new IrReturn(P)));
    Assert.That(Run(new O0005DeadCode(), called).Module, Is.SameAs(called));
  }

  /// <summary>
  /// The counter an unrolled loop leaves: its last value is stored where nothing reads it before its
  /// block ends, although a variable of the same name in another block is read.
  /// </summary>
  [Test]
  public void DeadCode_RemovesAStoreNothingReadsBeforeItsBlockEnds() {
    var i = new IrLocal("i", IrType.I32);
    var module = Module(Block(
      Block(new IrDeclare(i, Int(0)), new IrAssign(_x, Op(IrBinaryOp.Add, X, new IrLocalRef(i))), new IrAssign(i, Int(6))),
      Block(new IrDeclare(i, Int(4))),
      Block(new IrDeclare(i, X), new IrAssign(_x, Op(IrBinaryOp.Add, X, new IrLocalRef(i)))),
      new IrReturn(X)), Block(new IrReturn(P)), [i]);
    var (result, applied) = Run(new O0005DeadCode(), module);
    var statements = result.Functions[0].Body.Statements;
    Assert.That(((IrBlock)statements[0]).Statements, Is.EqualTo(new IrStmt[] { new IrDeclare(i, Int(0)), new IrAssign(_x, Op(IrBinaryOp.Add, X, new IrLocalRef(i))) }));
    Assert.That(statements, Has.Count.EqualTo(3), "the block that only declares goes with its declaration");
    Assert.That(((IrBlock)statements[1]).Statements, Is.EqualTo(((IrBlock)module.Functions[0].Body.Statements[2]).Statements));
    Assert.That(applied, Does.Contain("F@1"));
    SameOutcomes(module, result, _inputs);
  }

  /// <summary>
  /// Must not fire: a store read later in its block, a store of a variable declared outside the block
  /// (a loop reads it in the next pass), a store that may trap, and one before the declaration of its
  /// name in the block.
  /// </summary>
  [Test]
  public void DeadCode_KeepsAStoreThatMayBeRead() {
    var i = new IrLocal("i", IrType.I32);
    var s = new IrLocal("s", IrType.I32);
    var module = Module(Block(
      new IrDeclare(s, Int(0)),
      new IrWhile(Op(IrBinaryOp.Less, new IrLocalRef(s), Int(3)), Block(new IrAssign(_x, Op(IrBinaryOp.Add, X, new IrLocalRef(s))), new IrAssign(s, Op(IrBinaryOp.Add, new IrLocalRef(s), Int(1))))),
      Block(new IrAssign(s, Int(9)), new IrDeclare(i, Op(IrBinaryOp.Divide, Int(10), X)), new IrAssign(i, Op(IrBinaryOp.Add, new IrLocalRef(i), new IrLocalRef(s))), new IrAssign(_x, new IrLocalRef(i)), new IrAssign(i, Op(IrBinaryOp.Divide, Int(1), X))),
      new IrReturn(X)), Block(new IrReturn(P)), [i, s]);
    var (result, applied) = Run(new O0005DeadCode(), module);
    Assert.That(applied, Has.None.StartsWith("F@"));
    var statements = result.Functions[0].Body.Statements;
    Assert.That(statements, Has.Count.EqualTo(4));
    Assert.That(((IrBlock)statements[2]).Statements, Is.EqualTo(((IrBlock)module.Functions[0].Body.Statements[2]).Statements));
    SameOutcomes(module, result, _inputs);
  }

  // ---------------------------------------------------------------- O0023 unrolling

  private static IrModule Counted(long start, IrBinaryOp compare, long bound, IrStmt step, bool breaks = false) {
    var i = new IrLocal("i", IrType.I32);
    var t = new IrLocal("t", IrType.I32);
    IrStmt[] body = [new IrDeclare(t, Op(IrBinaryOp.Multiply, new IrLocalRef(i), Int(3))), new IrAssign(_x, Op(IrBinaryOp.Add, X, new IrLocalRef(t)))];
    if (breaks)
      body = [.. body, new IrIf(Op(IrBinaryOp.Greater, X, Int(1000)), Block(new IrBreak()), null)];

    return Module(Block(new IrDeclare(i, Int(start)), new IrWhile(Op(compare, new IrLocalRef(i), Int(bound)), Block([.. body, step])), new IrReturn(X)),
      Block(new IrReturn(P)), [i, t]);
  }

  private static IrStmt Up => new IrAssign(new IrLocal("i", IrType.I32), Op(IrBinaryOp.Add, new IrLocalRef(new IrLocal("i", IrType.I32)), Int(1)));

  /// <summary>Test design 4: a trip count of 0, 1 and the limit is unrolled, one over the limit is not.</summary>
  [TestCase(0, 0, true)]
  [TestCase(0, 1, true)]
  [TestCase(0, O0023Unrolling.Limit, true)]
  [TestCase(0, O0023Unrolling.Limit + 1, false)]
  [TestCase(5, 3, true)]
  public void Unrolling_CopiesALoopOfAFewPasses(int start, int bound, bool unrolled) {
    var module = Counted(start, IrBinaryOp.Less, bound, Up);
    var (result, _) = Run(new O0023Unrolling(), module);
    Assert.That(result.Functions[0].Body.Statements.OfType<IrWhile>().Any(), Is.EqualTo(!unrolled));
    if (unrolled)
      Assert.That(result.Functions[0].Body.Statements.OfType<IrBlock>().Count(), Is.EqualTo(Math.Max(0, bound - start)));

    SameOutcomes(module, result, _inputs);
  }

  [Test]
  public void Unrolling_CountsDownAndBySteps() {
    var i = new IrLocal("i", IrType.I32);
    var down = Counted(6, IrBinaryOp.Greater, 0, new IrAssign(i, Op(IrBinaryOp.Subtract, new IrLocalRef(i), Int(2))));
    var (result, _) = Run(new O0023Unrolling(), down);
    Assert.That(result.Functions[0].Body.Statements.OfType<IrBlock>().Count(), Is.EqualTo(3));
    SameOutcomes(down, result, _inputs);
    var stepped = Counted(0, IrBinaryOp.NotEqual, 4, new IrExprStmt(new IrStep(new IrLocalRef(i), true, false, false)));
    var (steps, _) = Run(new O0023Unrolling(), stepped);
    Assert.That(steps.Functions[0].Body.Statements.OfType<IrBlock>().Count(), Is.EqualTo(4));
    SameOutcomes(stepped, steps, _inputs);
  }

  [Test]
  public void Unrolling_KeepsALoopThatBreaksOrHasNoConstantBound() {
    var breaks = Counted(0, IrBinaryOp.Less, 3, Up, breaks: true);
    Assert.That(Run(new O0023Unrolling(), breaks).Module.Functions[0].Body.Statements.OfType<IrWhile>().Any(), Is.True);
    var i = new IrLocal("i", IrType.I32);
    var open = Module(Block(new IrDeclare(i, Int(0)), new IrWhile(Op(IrBinaryOp.Less, new IrLocalRef(i), X), Block(Up)), new IrReturn(new IrLocalRef(i))), Block(new IrReturn(P)), [i]);
    Assert.That(Run(new O0023Unrolling(), open).Module.Functions[0].Body.Statements.OfType<IrWhile>().Any(), Is.True);
  }

  // ---------------------------------------------------------------- the pipeline

  /// <summary>A module where every pass has something to do: a helper with a constant argument, a branch on it, a loop of two passes and a variable left unread.</summary>
  private static IrModule Everything() {
    var i = new IrLocal("i", IrType.I32);
    var helper = Block(
      new IrIf(Op(IrBinaryOp.Equal, P, Int(0)), Block(new IrReturn(Int(1))), null),
      new IrDeclare(i, Int(0)),
      new IrWhile(Op(IrBinaryOp.Less, new IrLocalRef(i), Int(2)), Block(new IrAssign(_p, Op(IrBinaryOp.Add, P, P)), new IrAssign(i, Op(IrBinaryOp.Add, new IrLocalRef(i), Int(1))))),
      new IrReturn(P));
    return Module(Block(new IrReturn(Op(IrBinaryOp.Add, X, CallH(Int(3))))), helper, [], [i]);
  }

  [Test]
  public void Pipeline_RunsEveryPassAndKeepsTheResult() {
    var module = Everything();
    var result = IrPassPipeline.Default.Run(module, new OptimizerOptions { Verify = true });
    Assert.That(result.Applications.Select(application => application.Id).Distinct().Order(), Is.EqualTo(IrPassPipeline.Default.Ids.Order()));
    Assert.That(result.Module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "F" }));
    Assert.That(result.Module.Functions[0].Body.Statements, Is.EqualTo(new IrStmt[] { new IrReturn(Op(IrBinaryOp.Add, X, Int(12))) }));
    SameOutcomes(module, result.Module, _inputs);
  }

  /// <summary>Test design 5: a pass <c>--disable-opt</c> names never fires, and none fires at <c>--opt none</c>.</summary>
  [TestCaseSource(nameof(PassIds))]
  public void Pipeline_NeverRunsADisabledPass(string id) {
    var result = IrPassPipeline.Default.Run(Everything(), new OptimizerOptions { Disabled = new HashSet<string> { id }, Verify = true });
    Assert.That(result.Applications.Select(application => application.Id), Has.None.EqualTo(id));
    SameOutcomes(Everything(), result.Module, _inputs);
  }

  private static IEnumerable<string> PassIds() => IrPassPipeline.Default.Ids;

  /// <summary>
  /// The last round only cleans up: a loop that appears in each round is unrolled in every round but
  /// the last, so the loops of the last two rounds stay.
  /// </summary>
  [Test]
  public void Pipeline_UnrollsNothingInTheLastRound() {
    var pipeline = new IrPassPipeline([new O0023Unrolling(), new Inserting()]);
    var result = pipeline.Run(Everything(), new OptimizerOptions { Verify = true });
    var helper = result.Module.Functions.Single(function => function.Name == "H");
    Assert.That(helper.Body.Statements.OfType<IrBlock>().SelectMany(block => block.Statements).OfType<IrWhile>().Count(), Is.EqualTo(2), "the loops inserted in the last two rounds");
    Assert.That(result.Applications.Count(application => application.Id == "O0023"), Is.EqualTo(IrPassPipeline.RoundLimit - 1));
  }

  /// <summary>A pass that puts a loop of one pass, in a block of its own, before the body of <c>H</c> in every round.</summary>
  private sealed class Inserting : IIrPass {

    private int _count;

    public string Id => "O9998";

    public string Name => "inserting";

    public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied) {
      applied("H", "1");
      var counter = new IrLocal("n" + this._count++, IrType.I32);
      var loop = Block(new IrDeclare(counter, Int(0)),
        new IrWhile(Op(IrBinaryOp.Less, new IrLocalRef(counter), Int(1)), Block(new IrAssign(_p, Op(IrBinaryOp.Add, P, Int(1))), new IrAssign(counter, Op(IrBinaryOp.Add, new IrLocalRef(counter), Int(1))))));
      var helper = module.Functions.Single(function => function.Name == "H");
      return module with { Functions = [.. module.Functions.Select(function => function == helper ? helper with { Body = Block([loop, .. helper.Body.Statements]), Locals = [.. helper.Locals, counter] } : function)] };
    }

  }

  [Test]
  public void Pipeline_RunsNothingAtLevelNone() {
    var module = Everything();
    var result = IrPassPipeline.Default.Run(module, new OptimizerOptions { Level = OptimizationLevel.None });
    Assert.That((result.Module, result.Applications.Count), Is.EqualTo((module, 0)));
  }

  [Test]
  public void Pipeline_RefusesTwoPassesOfOneNumber()
    => Assert.That(() => new IrPassPipeline([new O0001ConstantFolding(), new O0001ConstantFolding()]), Throws.ArgumentException);

  [Test]
  public void Pipeline_StopsAtAPassThatBreaksTheTypes() {
    var pipeline = new IrPassPipeline([new Breaking()]);
    Assert.That(() => pipeline.Run(Everything(), new OptimizerOptions { Verify = true }), Throws.InvalidOperationException.With.Message.Contains("O9999"));
  }

  /// <summary>A pass that makes <c>F</c> return a truth value.</summary>
  private sealed class Breaking : IIrPass {

    public string Id => "O9999";

    public string Name => "breaking";

    public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied) {
      applied("F", "1");
      return module with { Functions = [module.Functions[0] with { Body = Block(new IrReturn(new IrConst(IrType.Bool, 1))) }, .. module.Functions.Skip(1)] };
    }

  }

}

/// <summary>What the tests of the passes ask of the IR's trees.</summary>
internal static class IrPassSupportProbe {

  /// <summary>The size of a block as the cost rules count it: every statement and every expression node.</summary>
  public static int Size(IrBlock block) => block.Statements.SelectMany(IrStatementsProbe.All).Count() + block.Statements.SelectMany(IrTree.Nodes).Count();

}

/// <summary>Every statement within a statement.</summary>
internal static class IrStatementsProbe {

  public static IEnumerable<IrStmt> All(IrStmt statement)
    => (statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(inner => inner.Statements)).SelectMany(All).Prepend(statement);

}
