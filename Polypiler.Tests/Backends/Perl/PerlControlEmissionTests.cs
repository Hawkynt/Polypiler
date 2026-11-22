using NUnit.Framework;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Perl.PerlIr;

namespace Polypiler.Tests.Backends.Perl;

/// <summary>Tests for Perl control-flow emission (#430): branches, modifiers, loops, do-while, switches and bind.</summary>
[TestFixture]
public sealed class PerlControlEmissionTests {

  [Test]
  public void Branches_IfElsifElse_AndStatementModifier() {
    var x = L("x", IrType.I32);
    var fn = Function("classify", IrType.I32, [x],
      new IrIf(Bin(IrBinaryOp.Less, R(x), I(0)), Block(new IrReturn(I(-1))), null),
      new IrIf(Bin(IrBinaryOp.Equal, R(x), I(0)),
        Block(new IrReturn(I(0))),
        Block(new IrIf(Bin(IrBinaryOp.Less, R(x), I(10)),
          Block(new IrReturn(I(1))),
          Block(new IrReturn(I(2)))))));
    var src = Emit(new IrModule("Branches", [fn]));
    Assert.That(src, Does.Contain("return -1 if "));
    Assert.That(src, Does.Contain("elsif ("));
    Assert.That(src, Does.Contain("else {"));
  }

  [Test]
  public void Loops_CountedAscending_Descending_ElementLoop_While_AndGeneralFor() {
    var n = L("n", IrType.I32);
    var arr = L("arr", IrType.Array(IrType.I32));
    var sum = L("sum", IrType.I32);
    var i = L("i", IrType.I32);
    var j = L("j", IrType.I32);
    var k = L("k", IrType.I32);
    var m = L("m", IrType.I32);
    var v = L("v", IrType.I32);

    var fn = Function("loops", IrType.I32, [n, arr],
      new IrDeclare(sum, I(0)),
      // Ascending counted for -> for my $i (0 .. $n - 1)
      new IrDeclare(i, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(i), R(n)),
        Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(i)))),
        Block(new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))))),
      // Descending counted for -> for my $j (reverse 0 .. $n - 1)
      new IrDeclare(j, Bin(IrBinaryOp.Subtract, R(n), I(1))),
      new IrFor(Bin(IrBinaryOp.GreaterOrEqual, R(j), I(0)),
        Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(j)))),
        Block(new IrAssign(j, Bin(IrBinaryOp.Subtract, R(j), I(1))))),
      // Read-only element loop -> for my $v (@$arr)
      new IrDeclare(m, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(m), new IrArrayLength(R(arr))),
        Block(new IrDeclare(v, new IrArrayLoad(R(arr), R(m))),
          new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(v)))),
        Block(new IrAssign(m, Bin(IrBinaryOp.Add, R(m), I(1))))),
      // General for (step 2)
      new IrDeclare(k, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(k), R(n)),
        Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(k)))),
        Block(new IrAssign(k, Bin(IrBinaryOp.Add, R(k), I(2))))),
      // While with break/continue
      new IrWhile(Bin(IrBinaryOp.Greater, R(sum), I(100)), Block(
        new IrIf(Bin(IrBinaryOp.Equal, R(sum), I(105)), Block(new IrBreak()), null),
        new IrAssign(sum, Bin(IrBinaryOp.Subtract, R(sum), I(1))),
        new IrIf(Bin(IrBinaryOp.Equal, R(sum), I(102)), Block(new IrContinue()), null))),
      new IrReturn(R(sum)));

    var src = Emit(new IrModule("Loops", [fn]));
    Assert.That(src, Does.Contain("for my $i (0 .. "));
    Assert.That(src, Does.Contain("reverse "));
    Assert.That(src, Does.Contain("for my $v (@$arr)"));
    Assert.That(src, Does.Contain("while ("));
    Assert.That(src, Does.Contain("last if "));
    Assert.That(src, Does.Contain("next if "));
  }

  [Test]
  public void DoWhile_HandlesSimple_Break_AndContinueSafely() {
    var n = L("n", IrType.I32);
    var c = L("c", IrType.I32);
    var simple = Function("do_simple", IrType.I32, [n],
      new IrDeclare(c, I(0)),
      new IrDoWhile(Block(new IrAssign(c, Bin(IrBinaryOp.Add, R(c), I(1)))), Bin(IrBinaryOp.Less, R(c), R(n))),
      new IrReturn(R(c)));

    var withBreak = Function("do_brk", IrType.I32, [n],
      new IrDeclare(c, I(0)),
      new IrDoWhile(Block(
        new IrAssign(c, Bin(IrBinaryOp.Add, R(c), I(1))),
        new IrIf(Bin(IrBinaryOp.Equal, R(c), I(5)), Block(new IrBreak()), null)),
        Bin(IrBinaryOp.Less, R(c), R(n))),
      new IrReturn(R(c)));

    var withContinue = Function("do_cont", IrType.I32, [n],
      new IrDeclare(c, I(0)),
      new IrDoWhile(Block(
        new IrAssign(c, Bin(IrBinaryOp.Add, R(c), I(1))),
        new IrIf(Bin(IrBinaryOp.Equal, R(c), I(2)), Block(new IrContinue()), null),
        new IrIf(Bin(IrBinaryOp.Equal, R(c), I(5)), Block(new IrBreak()), null)),
        Bin(IrBinaryOp.Less, R(c), R(n))),
      new IrReturn(R(c)));

    var src = Emit(new IrModule("DoWhile", [simple, withBreak, withContinue]));
    Assert.That(src, Does.Contain("} while ("));
    Assert.That(src, Does.Contain("while (1)"));
    Assert.That(src, Does.Contain("last unless"));
    Assert.That(src, Does.Contain("BODY"));
  }

  [Test]
  public void Switch_CompressesConsecutiveRanges_AndHandlesDefaultAndBind() {
    var x = L("x", IrType.I32);
    var r = L("r", IrType.I32);
    var fn = Function("dispatch", IrType.I32, [x],
      new IrDeclare(r, I(0)),
      new IrSwitch(R(x), [
        new IrSwitchSection([1, 2, 3, 4, 10], false, Block(new IrAssign(r, I(100)), new IrBreak())),
        new IrSwitchSection([20], false, Block(new IrAssign(r, I(200)), new IrBreak())),
        new IrSwitchSection([], true, Block(new IrAssign(r, I(-1)), new IrBreak()))
      ]),
      new IrReturn(R(r)));
    var src = Emit(new IrModule("Switches", [fn]));
    Assert.That(src, Does.Contain(">= 1 && "));
    Assert.That(src, Does.Contain("<= 4"));
    Assert.That(src, Does.Contain("== 10"));
    Assert.That(src, Does.Contain("elsif ("));
    Assert.That(src, Does.Contain("else {"));

    var t = L("t", IrType.I32);
    var bindFn = Function("with_bind", IrType.I32, [x],
      new IrDeclare(t, I(0)),
      new IrReturn(Bin(IrBinaryOp.Multiply, new IrBind(t, Bin(IrBinaryOp.Add, R(x), I(1))), I(2))));
    var bindSrc = Emit(new IrModule("Binds", [bindFn]));
    Assert.That(bindSrc, Does.Contain("$t = "));
  }

}
