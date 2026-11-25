using System;
using NUnit.Framework;
using Polypiler.Core.Backends.Cpp;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Cpp.CppTestHarness;

namespace Polypiler.Tests.Backends.Cpp;

/// <summary>
/// Live g++ tests for control flow (<c>if</c>/<c>else if</c>, <c>while</c>, <c>do..while</c>,
/// <c>for</c>, <c>switch</c>, <c>break</c>, <c>continue</c>) and exception handling
/// (<c>try</c>/<c>catch</c>/<c>finally</c> via RAII scope guard, <c>throw</c>, <c>rethrow</c>,
/// throw expressions, filters, and custom exception classes).
/// </summary>
[TestFixture]
public sealed class CppControlAndExceptionsTests {

  [Test]
  public void ControlFlowLoopsAndSwitch_ParityAcrossAllStandards() {
    var n = L("n", IrType.I32);
    var sum = L("sum", IrType.I32);
    var i = L("i", IrType.I32);
    var j = L("j", IrType.I32);
    var x = L("x", IrType.I32);

    var mod = new IrModule("Control", [
      // if / else if / else + nested block + uninitialized declare
      Function("classify", IrType.I32, [n],
        new IrDeclare(sum, null),
        new IrIf(Bin(IrBinaryOp.Less, R(n), I(0)),
          Block(new IrAssign(sum, I(-1))),
          Block(new IrIf(Bin(IrBinaryOp.Equal, R(n), I(0)),
            Block(new IrAssign(sum, I(0))),
            Block(new IrBlock([new IrAssign(sum, I(1))]))))),
        new IrReturn(R(sum))),

      // while + break + continue
      Function("whileLoop", IrType.I32, [n],
        new IrDeclare(sum, I(0)),
        new IrDeclare(i, I(0)),
        new IrWhile(Bin(IrBinaryOp.Less, R(i), R(n)), Block(
          new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))),
          new IrIf(Bin(IrBinaryOp.Equal, Bin(IrBinaryOp.Remainder, R(i), I(2)), I(0)), Block(new IrContinue()), null),
          new IrIf(Bin(IrBinaryOp.Greater, R(i), I(9)), Block(new IrBreak()), null),
          new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(i))))),
        new IrReturn(R(sum))),

      // do..while
      Function("doWhileLoop", IrType.I32, [n],
        new IrDeclare(sum, I(0)),
        new IrDeclare(i, I(0)),
        new IrDoWhile(Block(
          new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), I(1))),
          new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1)))),
          Bin(IrBinaryOp.Less, R(i), R(n))),
        new IrReturn(R(sum))),

      // for with 0-stmt step, 1-stmt assign step, 1-stmt expr step, and multi-stmt step
      Function("forVariants", IrType.I32, [n],
        new IrDeclare(sum, I(0)),
        new IrDeclare(i, I(0)),
        new IrFor(Bin(IrBinaryOp.Less, R(i), R(n)),
          Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(i)))),
          Block(new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))))),
        new IrDeclare(j, I(0)),
        new IrFor(Bin(IrBinaryOp.Less, R(j), R(n)),
          Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(j)))),
          Block(new IrExprStmt(new IrStep(R(j), true, false, false)))),
        new IrDeclare(x, I(0)),
        new IrFor(Bin(IrBinaryOp.Less, R(x), R(n)),
          Block(new IrAssign(x, Bin(IrBinaryOp.Add, R(x), I(1)))),
          Block()),
        new IrAssign(i, I(0)),
        new IrAssign(j, I(0)),
        new IrFor(Bin(IrBinaryOp.Less, R(i), R(n)),
          Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(j)))),
          Block(new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))), new IrAssign(j, Bin(IrBinaryOp.Add, R(j), I(2))))),
        new IrReturn(Bin(IrBinaryOp.Add, R(sum), R(x)))),

      // switch with default and switch without default
      Function("swWithDefault", IrType.I32, [n],
        new IrSwitch(R(n), [
          new IrSwitchSection([1, 2], false, Block(new IrReturn(I(10)))),
          new IrSwitchSection([3], false, Block(new IrReturn(I(30)))),
          new IrSwitchSection([], true, Block(new IrReturn(I(99))))
        ]),
        new IrReturn(I(0))),

      Function("swNoDefault", IrType.I32, [n],
        new IrDeclare(sum, I(5)),
        new IrSwitch(R(n), [
          new IrSwitchSection([1], false, Block(new IrAssign(sum, I(11)))),
          new IrSwitchSection([2], false, Block(new IrAssign(sum, I(22))))
        ]),
        new IrReturn(R(sum))),

      // void return
      Function("noop", IrType.Void, [], new IrReturn(null))
    ]);

    Assert.That(Eval(mod, "classify", -5), Is.EqualTo(-1));
    Assert.That(Eval(mod, "classify", 0), Is.EqualTo(0));
    Assert.That(Eval(mod, "classify", 5), Is.EqualTo(1));
    Assert.That(Eval(mod, "whileLoop", 20), Is.EqualTo(25));
    Assert.That(Eval(mod, "doWhileLoop", 0), Is.EqualTo(1));
    Assert.That(Eval(mod, "forVariants", 4), Is.EqualTo(28));
    Assert.That(Eval(mod, "swWithDefault", 2), Is.EqualTo(10));
    Assert.That(Eval(mod, "swWithDefault", 7), Is.EqualTo(99));
    Assert.That(Eval(mod, "swNoDefault", 7), Is.EqualTo(5));

    var driver = """
      #include "control.hpp"
      #include <cassert>
      using namespace control;
      int main() {
        assert(classify(-5) == -1 && classify(0) == 0 && classify(5) == 1);
        assert(while_loop(20) == 25);
        assert(do_while_loop(0) == 1 && do_while_loop(3) == 3);
        assert(for_variants(4) == 28);
        assert(sw_with_default(1) == 10 && sw_with_default(2) == 10 && sw_with_default(3) == 30 && sw_with_default(7) == 99);
        assert(sw_no_default(1) == 11 && sw_no_default(2) == 22 && sw_no_default(7) == 5);
        noop();
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void TryCatchFinallyRethrowAndCustomExceptions_ParityAcrossAllStandards() {
    var customEx = new IrClassType("CustomError");
    customEx.Define([]);
    customEx.Inherit(IrExceptionClass.Exception, []);
    var customCtor = new IrConstructor("CustomError..ctor");
    customEx.DefineMembers([customCtor], [], [], []);
    var ctorThis = L("this", IrType.Object(customEx));
    var ctorMsg = L("msg", IrType.String);
    var ctorFn = Function(customCtor.Function, false, IrType.Void, [ctorThis, ctorMsg],
      new IrExprStmt(new IrCall(IrExceptionClass.Constructor(IrExceptionClass.Exception, 1).Function, IrType.Void, [R(ctorThis), R(ctorMsg)])));

    var a = L("a", IrType.I32);
    var b = L("b", IrType.I32);
    var log = L("log", IrType.I32);
    var exLocal = L("ex", IrType.Object(customEx));
    var res = L("res", IrType.I32);

    var mod = new IrModule("ExHandling", [
      ctorFn,
      // try/catch specific library exceptions + catch-all with message read + stored exception local throw
      Function("safeDiv", IrType.I32, [a, b],
        new IrTry(
          Block(new IrReturn(Bin(IrBinaryOp.Divide, R(a), R(b)))),
          [
            new IrCatch(IrExceptionClass.DivideByZeroException, null, null, Block(new IrReturn(I(-1)))),
            new IrCatch(IrExceptionClass.OverflowException, null, null, Block(new IrReturn(I(-2)))),
            new IrCatch(IrExceptionClass.IndexOutOfRangeException, null, null, Block(new IrReturn(I(-3)))),
            new IrCatch(IrExceptionClass.ArithmeticException, null, null, Block(new IrReturn(I(-4))))
          ],
          null),
        new IrReturn(I(0))),

      Function("throwFromLocal", IrType.Void, [],
        new IrDeclare(L("e", IrType.Object(IrExceptionClass.ArithmeticException)), new IrNew(IrType.Object(IrExceptionClass.ArithmeticException), IrExceptionClass.Constructor(IrExceptionClass.ArithmeticException, 1), [new IrStrConst("arith")])),
        new IrThrow(R(L("e", IrType.Object(IrExceptionClass.ArithmeticException))))),

      // try/finally with early return: return value computed BEFORE finally runs!
      Function("finallyReturnOrder", IrType.I32, [L("out", IrType.Array(IrType.I32))],
        new IrDeclare(res, I(10)),
        new IrTry(
          Block(new IrReturn(R(res))),
          [],
          Block(
            new IrAssign(res, I(99)),
            new IrStore(R(L("out", IrType.Array(IrType.I32))), I(0), I(42)))),
        new IrReturn(I(0))),

      // try/catch/finally + throw expression + filter + rethrow
      Function("filterAndRethrow", IrType.I32, [a],
        new IrDeclare(log, I(0)),
        new IrTry(
          Block(
            new IrIf(Bin(IrBinaryOp.Equal, R(a), I(1)),
              Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.ArgumentException), IrExceptionClass.Constructor(IrExceptionClass.ArgumentException, 1), [new IrStrConst("bad arg")]))),
              Block(new IrIf(Bin(IrBinaryOp.Equal, R(a), I(2)),
                Block(new IrReturn(new IrThrowExpression(IrType.I32, new IrNew(IrType.Object(customEx), customCtor, [new IrStrConst("boom")])))),
                Block(new IrIf(Bin(IrBinaryOp.Equal, R(a), I(3)),
                  Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.InvalidOperationException), IrExceptionClass.Constructor(IrExceptionClass.InvalidOperationException, 0), []))),
                  Block(new IrIf(Bin(IrBinaryOp.Equal, R(a), I(4)),
                    Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.InvalidCastException), IrExceptionClass.Constructor(IrExceptionClass.InvalidCastException, 0), []))),
                    Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.Exception), IrExceptionClass.Constructor(IrExceptionClass.Exception, 0), []))))))))))),
          [
            new IrCatch(IrExceptionClass.ArgumentException, null, Bin(IrBinaryOp.Equal, R(a), I(1)), Block(new IrAssign(log, I(100)))),
            new IrCatch(customEx, exLocal, null, Block(
              new IrIf(new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [new IrFieldLoad(R(exLocal), IrExceptionClass.Message), new IrStrConst("boom")]),
                Block(new IrAssign(log, I(200))),
                Block(new IrRethrow())))),
            new IrCatch(IrExceptionClass.InvalidOperationException, null, null, Block(new IrRethrow())),
            new IrCatch(IrExceptionClass.InvalidCastException, null, null, Block(new IrAssign(log, I(400)))),
            new IrCatch(IrExceptionClass.Exception, null, null, Block(new IrAssign(log, I(500))))
          ],
          Block(new IrAssign(log, Bin(IrBinaryOp.Add, R(log), I(1))))),
        new IrReturn(R(log)))
    ]) { Types = [customEx] };

    Assert.That(Eval(mod, "safeDiv", 10, 2), Is.EqualTo(5));
    Assert.That(Eval(mod, "safeDiv", 10, 0), Is.EqualTo(-1));
    Assert.That(Eval(mod, "safeDiv", int.MinValue, -1), Is.EqualTo(-2));
    Assert.That(Eval(mod, "filterAndRethrow", 1), Is.EqualTo(101));
    Assert.That(Eval(mod, "filterAndRethrow", 2), Is.EqualTo(201));
    Assert.That(Eval(mod, "filterAndRethrow", 4), Is.EqualTo(401));
    Assert.That(Eval(mod, "filterAndRethrow", 5), Is.EqualTo(501));

    var driver = """
      #include "ex_handling.hpp"
      #include <cassert>
      #include <climits>
      #include <stdexcept>
      #include <vector>
      using namespace ex_handling;
      int main() {
        assert(safe_div(10, 2) == 5);
        assert(safe_div(10, 0) == -1);
        assert(safe_div(INT32_MIN, -1) == -2);

        std::vector<std::int32_t> out{0};
        assert(finally_return_order(out) == 10);
        assert(out[0] == 42);

        assert(filter_and_rethrow(1) == 101);
        assert(filter_and_rethrow(2) == 201);
        bool rethrown = false;
        try { (void)filter_and_rethrow(3); } catch (const std::logic_error&) { rethrown = true; }
        assert(rethrown);
        assert(filter_and_rethrow(4) == 401);
        assert(filter_and_rethrow(5) == 501);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

}
