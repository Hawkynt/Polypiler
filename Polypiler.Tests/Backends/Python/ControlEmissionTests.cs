using System;
using NUnit.Framework;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Python.PythonIr;

namespace Polypiler.Tests.Backends.Python;

/// <summary>
/// The lowering table of the control issue (#444) as text: branches as <c>if</c>/<c>elif</c>, counted
/// loops as <c>for … in range(…)</c> only where that keeps every value and the trip count, post-test
/// loops as <c>while True:</c> with the test last, switches as <c>match</c> or as an <c>if</c> chain,
/// and the <c>break</c> of a switch taken out of the section. The differential tests of the
/// acceptance project run the same constructs under CPython.
/// </summary>
[TestFixture]
public sealed class ControlEmissionTests {

  private static readonly IrLocal _n = L("n", IrType.I32), _s = L("s", IrType.I32), _i = L("i", IrType.I32), _x = L("x", IrType.I32);

  private static IrStmt Assign(IrLocal local, IrExpr value) => new IrAssign(local, value);

  private static IrExpr Add(IrExpr left, IrExpr right) => Bin(IrBinaryOp.Add, left, right);

  /// <summary><c>s = s * 10 + i</c>, which keeps the order of the values a loop visits.</summary>
  private static IrStmt Fold => Assign(_s, Add(Bin(IrBinaryOp.Multiply, R(_s), I(10)), R(_i)));

  private static string Text(params IrStmt[] body) => Emit(new IrModule("M", [Function("f", IrType.I32, [_n], body)]));

  /// <summary>A counted loop of the scenario of #444: <c>s</c> folds the values of <c>i</c> from <paramref name="start"/> while <c>i cmp bound</c>, stepping by <paramref name="step"/>.</summary>
  private static string Counted(IrExpr start, IrBinaryOp compare, IrExpr bound, IrStmt step, params IrStmt[] after)
    => Text([new IrDeclare(_s, I(0)), new IrDeclare(_i, start), new IrFor(Bin(compare, R(_i), bound), Block(Fold), Block(step)), .. after, new IrReturn(R(_s))]);

  private static IrStmt Increment => new IrExprStmt(new IrStep(R(_i), true, false, false));

  [Test]
  public void CountedLoops_BecomeRangeLoops() {
    Assert.That(Counted(I(0), IrBinaryOp.Less, R(_n), Increment), Does.Contain("    for i in range(n):\n        s = "));
    Assert.That(Counted(I(1), IrBinaryOp.LessOrEqual, I(100), Increment), Does.Contain("for i in range(1, 101):"));
    Assert.That(Counted(R(_n), IrBinaryOp.GreaterOrEqual, I(1), new IrExprStmt(new IrStep(R(_i), false, true, false))), Does.Contain("for i in range(n, 0, -1):"));
    Assert.That(Counted(I(0), IrBinaryOp.LessOrEqual, I(10), Assign(_i, Add(R(_i), I(2)))), Does.Contain("for i in range(0, 11, 2):"));
    Assert.That(Counted(I(0), IrBinaryOp.Less, I(10), Assign(_i, Add(I(3), R(_i)))), Does.Contain("for i in range(0, 10, 3):"));
    Assert.That(Counted(I(9), IrBinaryOp.Greater, I(0), Assign(_i, Bin(IrBinaryOp.Subtract, R(_i), I(2)))), Does.Contain("for i in range(9, 0, -2):"));
  }

  [Test]
  public void ACounterComparedFromTheRight_IsCountedToo() {
    var text = Text(new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)), new IrFor(Bin(IrBinaryOp.Greater, R(_n), R(_i)), Block(Fold), Block(Increment)), new IrReturn(R(_s)));
    Assert.That(text, Does.Contain("for i in range(n):"));
    foreach (var (compare, header) in new[] { (IrBinaryOp.Less, "range(0, 0, -1)"), (IrBinaryOp.LessOrEqual, "range(0, -1, -1)"), (IrBinaryOp.GreaterOrEqual, "range(101)") }) {
      var step = compare == IrBinaryOp.GreaterOrEqual ? Increment : new IrExprStmt(new IrStep(R(_i), false, false, false));
      var mirrored = Text(new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)), new IrFor(Bin(compare, compare == IrBinaryOp.GreaterOrEqual ? I(100) : I(0), R(_i)), Block(Fold), Block(step)), new IrReturn(R(_s)));
      Assert.That(mirrored, Does.Contain("for i in " + header), compare.ToString());
    }
  }

  [Test]
  public void ALoopThatIsNoPlainRange_StaysAWhile_WithItsStepAtTheEndOfEveryPass() {
    // The counter is read after the loop: Python's range would leave another value in it.
    Assert.That(Counted(I(0), IrBinaryOp.Less, R(_n), Increment, Assign(_s, Add(R(_s), R(_i)))), Does.Contain("while i < n:").And.Not.Contain("range"));
    // The body writes the counter.
    var writes = Text(new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)), new IrFor(Bin(IrBinaryOp.Less, R(_i), R(_n)), Block(Assign(_i, Add(R(_i), I(1)))), Block(Increment)), new IrReturn(R(_s)));
    Assert.That(writes, Does.Contain("while i < n:"));
    // The bound changes in the loop.
    var moves = Text(new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)), new IrFor(Bin(IrBinaryOp.Less, R(_i), R(_n)), Block(Assign(_n, Add(R(_n), I(-1)))), Block(Increment)), new IrReturn(R(_s)));
    Assert.That(moves, Does.Contain("while i < n:"));
    // The last value would wrap: the loop of the IR runs on.
    Assert.That(Counted(I(0), IrBinaryOp.LessOrEqual, I(int.MaxValue), Increment), Does.Contain("while i <= 2147483647:"));
    Assert.That(Counted(I(10), IrBinaryOp.GreaterOrEqual, I(int.MinValue), new IrExprStmt(new IrStep(R(_i), false, false, false))), Does.Contain("while i >= -2147483648:"));
    // A step of two up to a bound that is no constant could wrap.
    Assert.That(Counted(I(0), IrBinaryOp.Less, R(_n), Assign(_i, Add(R(_i), I(2)))), Does.Contain("while i < n:"));
    // A step that goes the wrong way, a step of nothing, a test of inequality, a test that does not name the counter.
    Assert.That(Counted(I(0), IrBinaryOp.Less, R(_n), new IrExprStmt(new IrStep(R(_i), false, false, false))), Does.Contain("while i < n:"));
    Assert.That(Counted(I(0), IrBinaryOp.Less, R(_n), Assign(_i, Add(R(_i), I(0)))), Does.Contain("while i < n:"));
    Assert.That(Counted(I(0), IrBinaryOp.NotEqual, R(_n), Increment), Does.Contain("while i != n:"));
    Assert.That(Counted(I(0), IrBinaryOp.Less, R(_s), Increment), Does.Contain("while i < s:"));
    var other = Text(new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)), new IrFor(Bin(IrBinaryOp.Less, R(_s), R(_n)), Block(Fold), Block(Increment)), new IrReturn(R(_s)));
    Assert.That(other, Does.Contain("while s < n:"));
  }

  /// <summary>
  /// An inclusive bound that may be the largest value of the counter's type (the smallest, counting
  /// down) leaves the IR's loop running on, the counter wrapping past it, where a range would stop:
  /// such a loop stays a <c>while</c>. A range is written only where the values the bound can have
  /// keep the step after the last pass inside the type.
  /// </summary>
  [Test]
  public void AnInclusiveBoundThatMayBeTheEndOfTheType_StaysAWhile() {
    Assert.That(Counted(I(1), IrBinaryOp.LessOrEqual, R(_n), Increment), Does.Contain("while i <= n:").And.Not.Contain("range"));
    Assert.That(Counted(I(10), IrBinaryOp.GreaterOrEqual, R(_n), new IrExprStmt(new IrStep(R(_i), false, false, false))), Does.Contain("while i >= n:").And.Not.Contain("range"));
    var mirrored = Text(new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)), new IrFor(Bin(IrBinaryOp.GreaterOrEqual, R(_n), R(_i)), Block(Fold), Block(Increment)), new IrReturn(R(_s)));
    Assert.That(mirrored, Does.Contain("while n >= i:").And.Not.Contain("range"));
    // The length of an array is an i32 that may be the largest one.
    var data = L("data", IrType.Array(IrType.I32));
    var length = Emit(new IrModule("M", [Function("f", IrType.I32, [data], new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)),
      new IrFor(Bin(IrBinaryOp.LessOrEqual, R(_i), new IrArrayLength(R(data))), Block(Fold), Block(Increment)), new IrReturn(R(_s)))]));
    Assert.That(length, Does.Contain("while i <= len(data):").And.Not.Contain("range"));
  }

  /// <summary>A bound converted from a narrower type is strictly inside the counter's type: the step after its last value cannot wrap.</summary>
  [Test]
  public void AnInclusiveBoundOfANarrowerType_IsARange() {
    var narrow = L("h", IrType.I16);
    string Converted(IrStmt[] body) => Emit(new IrModule("M", [Function("f", IrType.I32, [narrow], body)]));
    var up = Converted([new IrDeclare(_s, I(0)), new IrDeclare(_i, I(1)), new IrFor(Bin(IrBinaryOp.LessOrEqual, R(_i), new IrConvert(IrType.I32, R(narrow))), Block(Fold), Block(Increment)), new IrReturn(R(_s))]);
    Assert.That(up, Does.Contain("for i in range(1, h + 1):"));
    var down = Converted([new IrDeclare(_s, I(0)), new IrDeclare(_i, I(10)), new IrFor(Bin(IrBinaryOp.GreaterOrEqual, R(_i), new IrConvert(IrType.I32, R(narrow))), Block(Fold), Block(new IrExprStmt(new IrStep(R(_i), false, false, false)))), new IrReturn(R(_s))]);
    Assert.That(down, Does.Contain("for i in range(10, h - 1, -1):"));
    // A step of two up to a narrower bound cannot wrap either; up to a full-width one it could.
    var stepped = Converted([new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)), new IrFor(Bin(IrBinaryOp.Less, R(_i), new IrConvert(IrType.I32, R(narrow))), Block(Fold), Block(Assign(_i, Add(R(_i), I(2))))), new IrReturn(R(_s))]);
    Assert.That(stepped, Does.Contain("for i in range(0, h, 2):"));
    // A conversion that narrows has every value of its target type.
    var wide = L("w", IrType.I64);
    var narrowed = Emit(new IrModule("M", [Function("f", IrType.I32, [wide], new IrDeclare(_s, I(0)), new IrDeclare(_i, I(1)),
      new IrFor(Bin(IrBinaryOp.LessOrEqual, R(_i), new IrConvert(IrType.I32, R(wide))), Block(Fold), Block(Increment)), new IrReturn(R(_s)))]));
    Assert.That(narrowed, Does.Contain("while i <= ").And.Not.Contain("range"));
  }

  [Test]
  public void AWhileThatEndsWithItsStep_IsACountedLoop_UnlessItHasAContinue() {
    var counted = Text(new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)), new IrWhile(Bin(IrBinaryOp.Less, R(_i), R(_n)), Block(Fold, Increment)), new IrReturn(R(_s)));
    Assert.That(counted, Does.Contain("for i in range(n):"));
    var skipping = Text(new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)),
      new IrWhile(Bin(IrBinaryOp.Less, R(_i), R(_n)), Block(new IrIf(Bin(IrBinaryOp.Equal, R(_s), I(3)), Block(new IrContinue()), null), Fold, Increment)), new IrReturn(R(_s)));
    Assert.That(skipping, Does.Contain("while i < n:"));
    var empty = Text(new IrDeclare(_i, I(0)), new IrWhile(Bin(IrBinaryOp.Less, R(_i), R(_n)), Block()), new IrReturn(R(_i)));
    Assert.That(empty, Does.Contain("while i < n:\n        pass\n"));
  }

  [Test]
  public void ACounterThatStartsAtZero_IndexesWithoutACheck() {
    var data = L("data", IrType.Array(IrType.I32));
    var module = new IrModule("M", [Function("f", IrType.I32, [data], new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(_i), new IrArrayLength(R(data))), Block(Assign(_s, Add(R(_s), new IrArrayLoad(R(data), R(_i))))), Block(Increment)),
      new IrDeclare(_x, I(1)), new IrReturn(Add(R(_s), new IrArrayLoad(R(data), R(_x)))))]);
    var text = Emit(module);
    Assert.That(text, Does.Contain("for i in range(len(data)):").And.Contain("s = (((s + data[i]) & ").And.Contain("data[_ply_index(x)]"));
  }

  [Test]
  public void ContinueOfAForWrittenAsWhile_RunsTheStepFirst() {
    var text = Text(new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(_i), R(_n)), Block(new IrIf(Bin(IrBinaryOp.Equal, R(_i), I(2)), Block(new IrContinue()), null), Fold), Block(Assign(_i, Add(R(_i), I(2))))),
      new IrReturn(R(_s)));
    Assert.That(text, Does.Contain("        if i == 2:\n            i = (((i + 2) & 0xFFFFFFFF) ^ 0x80000000) - 0x80000000\n            continue\n"));
  }

  [Test]
  public void PostTestLoop_RunsItsBodyFirst_AndAContinueTestsFirst() {
    var text = Text(new IrDeclare(_s, I(0)),
      new IrDoWhile(Block(Assign(_s, Add(R(_s), I(1))), new IrIf(Bin(IrBinaryOp.Equal, R(_s), I(3)), Block(new IrContinue()), null)), Bin(IrBinaryOp.Less, R(_s), R(_n))),
      new IrReturn(R(_s)));
    Assert.That(text, Does.Contain("    while True:\n        s = ").And.Contain("        if s == 3:\n            if not s < n:\n                break\n            continue\n        if not s < n:\n            break\n"));
    var endless = Text(new IrDeclare(_s, I(0)), new IrDoWhile(Block(new IrIf(Bin(IrBinaryOp.Equal, R(_s), I(3)), Block(new IrContinue()), Block(new IrBreak()))), C(IrType.Bool, 1)), new IrReturn(R(_s)));
    Assert.That(endless, Does.Contain("    while True:\n        if s == 3:\n            continue\n        else:\n            break\n    return s"));
  }

  [Test]
  public void Branches_ElifAndEmptyArms() {
    var chained = Text(new IrIf(Bin(IrBinaryOp.Less, R(_n), I(0)), Block(new IrReturn(I(-1))), Block(new IrIf(Bin(IrBinaryOp.Equal, R(_n), I(0)), Block(new IrReturn(I(0))), Block(new IrReturn(I(1)))))));
    Assert.That(chained, Does.Contain("    if n < 0:\n        return -1\n    elif n == 0:\n        return 0\n    else:\n        return 1\n"));
    Assert.That(Text(new IrIf(Bin(IrBinaryOp.Less, R(_n), I(0)), Block(), Block(new IrReturn(I(1)))), new IrReturn(I(0))), Does.Contain("    if not n < 0:\n        return 1\n"));
    var call = new IrCall("f", IrType.I32, [I(1)]);
    Assert.That(Text(new IrIf(Bin(IrBinaryOp.Less, call, I(0)), Block(), null), new IrReturn(I(0))), Does.Contain("    f(1) < 0\n"));
  }

  private static IrSwitch Dense => new(R(_x), [
    new IrSwitchSection([0], false, Block(new IrReturn(I(10)))),
    new IrSwitchSection([1, 2], false, Block(new IrReturn(I(20)))),
    new IrSwitchSection([3, 4, 5, 6, 7, 8, 9], false, Block(new IrReturn(I(30)))),
    new IrSwitchSection([], true, Block(new IrReturn(I(40))))
  ]);

  private static IrModule Switching(params IrStmt[] body) => new("M", [Function("f", IrType.I32, [_x], body)]);

  [Test]
  public void DenseSwitch_IsAMatchStatement() {
    var text = Emit(Switching(Dense, new IrReturn(I(0))));
    Assert.That(text, Does.Contain("    match x:\n        case 0:\n            return 10\n        case 1 | 2:\n            return 20\n        case _ if 3 <= x <= 9:\n            return 30\n        case _:\n            return 40\n"));
  }

  [Test]
  public void TheSameSwitchBeforePython310_IsAnIfChain() {
    var text = Emit(Switching(Dense, new IrReturn(I(0))), "py3.9");
    Assert.That(text, Does.Contain("    if x == 0:\n        return 10\n    elif x in (1, 2):\n        return 20\n    elif 3 <= x <= 9:\n        return 30\n    else:\n        return 40\n").And.Not.Contain("match"));
  }

  [Test]
  public void SwitchSection_WithARunAndSingleLabels_TestsThemAll() {
    var selection = new IrSwitch(R(_x), [new IrSwitchSection([-1, 5, 6, 7, 9], false, Block(new IrReturn(I(1)))), new IrSwitchSection([-5], false, Block(new IrReturn(I(2))))]);
    Assert.That(Emit(Switching(selection, new IrReturn(I(0)))), Does.Contain("case -5:").And.Contain("case _ if x in (-1, 9) or 5 <= x <= 7:"));
    Assert.That(Emit(Switching(selection, new IrReturn(I(0))), "py3.8"), Does.Contain("if x in (-1, 9) or 5 <= x <= 7:").And.Contain("elif x == -5:"));
  }

  [Test]
  public void SwitchOnAnExpression_ComputesItOnce() {
    var selection = new IrSwitch(Bin(IrBinaryOp.Remainder, R(_x), I(4)), [new IrSwitchSection([0], false, Block(new IrReturn(I(1))))]);
    Assert.That(Emit(Switching(selection, new IrReturn(I(0)))), Does.Contain("    _t1 = _ply_srem(x, 4, -2147483648)\n    match _t1:\n"));
  }

  [Test]
  public void SwitchWithOnlyADefault_IsItsBody_AndAnEmptyOneIsNothing() {
    var only = new IrSwitch(R(_x), [new IrSwitchSection([], true, Block(new IrReturn(I(7))))]);
    Assert.That(Emit(Switching(only, new IrReturn(I(0)))), Does.Contain("def f(x: int) -> int:\n    return 7\n    return 0\n"));
    var empty = new IrSwitch(R(_x), [new IrSwitchSection([1], false, Block(new IrBreak()))]);
    Assert.That(Emit(Switching(empty, new IrReturn(I(0)))), Does.Contain("    match x:\n        case 1:\n            pass\n"));
    var nothing = new IrSwitch(new IrCall("f", IrType.I32, [I(1)]), []);
    Assert.That(Emit(Switching(nothing, new IrReturn(I(0)))), Does.Contain("    _t1 = f(1)\n    return 0\n"));
  }

  [Test]
  public void ABreakInsideASection_SkipsTheRestOfTheSection() {
    var section = Block(new IrIf(Bin(IrBinaryOp.Greater, R(_s), I(5)), Block(new IrBreak()), null), Assign(_s, Add(R(_s), I(10))), new IrBreak());
    var text = Emit(Switching(new IrDeclare(_s, I(0)), new IrSwitch(R(_x), [new IrSwitchSection([1], false, section)]), new IrReturn(R(_s))));
    Assert.That(text, Does.Contain("        case 1:\n            if not s > 5:\n                s = ").And.Not.Contain("break"));
    var both = Block(new IrIf(Bin(IrBinaryOp.Greater, R(_s), I(5)), Block(Assign(_s, I(1))), Block(new IrBreak())), Assign(_s, I(2)), new IrBreak());
    var copied = Emit(Switching(new IrDeclare(_s, I(0)), new IrSwitch(R(_x), [new IrSwitchSection([1], false, both)]), new IrReturn(R(_s))));
    Assert.That(copied, Does.Contain("            if s > 5:\n                s = 1\n                s = 2\n").And.Not.Contain("break"));
  }

  [Test]
  public void ASectionThatWouldCopyTooMuch_StandsInALoopOfOnePass() {
    var deep = Block(new IrIf(Bin(IrBinaryOp.Greater, R(_s), I(5)), Block(new IrIf(Bin(IrBinaryOp.Less, R(_s), I(9)), Block(new IrBreak()), null)), null),
      Assign(_s, I(1)), Assign(_s, I(2)), Assign(_s, I(3)), Assign(_s, I(4)), new IrBreak());
    var text = Emit(Switching(new IrDeclare(_s, I(0)), new IrSwitch(R(_x), [new IrSwitchSection([1], false, deep)]), new IrReturn(R(_s))));
    Assert.That(text, Does.Contain("    while True:\n        match x:\n            case 1:\n                if s > 5:\n                    if s < 9:\n                        break\n").And.Contain("                break\n        break\n"));
  }

  [Test]
  public void AContinueInsideASwitchInALoopOfOnePass_IsRefused() {
    var deep = Block(new IrIf(Bin(IrBinaryOp.Greater, R(_s), I(5)), Block(new IrIf(Bin(IrBinaryOp.Less, R(_s), I(9)), Block(new IrBreak()), null)), null),
      new IrIf(Bin(IrBinaryOp.Equal, R(_s), I(7)), Block(new IrContinue()), null), Assign(_s, I(1)), Assign(_s, I(2)), Assign(_s, I(3)), Assign(_s, I(4)), new IrBreak());
    var module = Switching(new IrDeclare(_s, I(0)), new IrWhile(Bin(IrBinaryOp.Less, R(_s), I(100)), Block(new IrSwitch(R(_x), [new IrSwitchSection([1], false, deep)]))), new IrReturn(R(_s)));
    Assert.That(Problems(module), Is.EqualTo(new[] { "error PLY1858: control construct 'a continue inside a switch that is left early' has no Python form (function 'f')" }));
  }

  [Test]
  public void ContinueInsideASwitch_ContinuesTheLoop() {
    var loop = new IrWhile(Bin(IrBinaryOp.Less, R(_s), I(10)), Block(Assign(_s, Add(R(_s), I(1))), new IrSwitch(R(_s), [new IrSwitchSection([3], false, Block(new IrContinue()))]), Assign(_x, R(_s))));
    Assert.That(Emit(Switching(new IrDeclare(_s, I(0)), loop, new IrReturn(R(_x)))), Does.Contain("            case 3:\n                continue\n"));
  }

  [Test]
  public void TrailingReturnOfAProcedure_IsLeftOut() {
    var module = new IrModule("M", [Function("f", IrType.Void, [_x], new IrExprStmt(new IrCall("f", IrType.Void, [R(_x)])), new IrReturn(null))]);
    Assert.That(Emit(module), Does.Contain("def f(x: int) -> None:\n    f(x)\n").And.Not.Contain("return"));
  }

}
