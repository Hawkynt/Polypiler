using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Jvm;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Jvm.JvmTestRunner;

namespace Polypiler.Tests.Backends.Jvm;

[TestFixture]
public sealed class JvmControlFlowAndExceptionsTests {

  [Test]
  public void ControlFlow_Loops_Switches_Steps_Bind_Coalesce_Globals_AndConstants_ExecuteCorrectly() {
    var tableConst = new IrModuleConst("Table", IrType.Array(IrType.I32), [10, 20, 30]);
    var strTableConst = IrModuleConst.OfStrings("Names", IrType.Array(IrType.String), ["alpha", "beta"]);
    var scalarConst = new IrModuleConst("Magic", IrType.I32, [7]);
    var strConst = IrModuleConst.OfStrings("Greeting", IrType.String, ["hi"]);

    var counterGlobal = new IrGlobal("Counter", IrType.I32, I(10));
    var byteGlobal = new IrGlobal("ByteReg", IrType.U8, C(IrType.U8, 250));
    var uninitGlobal = new IrGlobal("Uninit", IrType.I32, null);

    var n = L("n", IrType.I32);
    var sum = L("sum", IrType.I32);
    var i = L("i", IrType.I32);
    var x64 = L("x", IrType.I64);
    var s = L("s", IrType.String);
    var b = L("b", IrType.Bool);
    var arr = L("arr", IrType.Array(IrType.U8));
    var f32 = L("f", IrType.F32);
    var f64 = L("d", IrType.F64);
    var nb = L("nb", IrType.U8);
    var stepLong = L("sl", IrType.I64);

    var initFn = Function("ModuleInit", false, IrType.Void, [],
      new IrGlobalAssign(uninitGlobal, I(99)));

    var module = new IrModule("Ctrl", [
      initFn,
      // If / else + unreachable pruning after return
      Function("Classify", IrType.I32, [n],
        new IrIf(Bin(IrBinaryOp.Less, R(n), I(0)), Block(new IrReturn(I(-1))), Block(
          new IrIf(Bin(IrBinaryOp.Equal, R(n), I(0)), Block(new IrReturn(I(0))), null),
          new IrReturn(I(1))))),
      // While + DoWhile + For with break and continue
      Function("Loops", IrType.I32, [n],
        new IrDeclare(sum, I(0)),
        new IrDeclare(i, I(0)),
        new IrWhile(Bin(IrBinaryOp.Less, R(i), R(n)), Block(
          new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))),
          new IrIf(Bin(IrBinaryOp.Equal, R(i), I(2)), Block(new IrContinue()), null),
          new IrIf(Bin(IrBinaryOp.Equal, R(i), I(5)), Block(new IrBreak()), null),
          new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(i))))),
        // DoWhile adds 100 once
        new IrDoWhile(Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), I(100)))), B(false)),
        // For loop adds 1+2+3 skipping 2 -> +4
        new IrAssign(i, I(0)),
        new IrFor(Bin(IrBinaryOp.Less, R(i), I(4)),
          Block(
            new IrIf(Bin(IrBinaryOp.Equal, R(i), I(2)), Block(new IrContinue()), null),
            new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(i)))),
          Block(new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))))),
        new IrReturn(R(sum))),
      // Dense switch (tableswitch)
      Function("DenseSwitch", IrType.I32, [n],
        new IrSwitch(R(n), [
          new IrSwitchSection([1], false, Block(new IrReturn(I(10)))),
          new IrSwitchSection([2, 3], false, Block(new IrReturn(I(20)))),
          new IrSwitchSection([], true, Block(new IrReturn(I(99))))
        ]),
        new IrReturn(I(-1))),
      // Sparse switch (lookupswitch) + break inside switch
      Function("SparseSwitch", IrType.I32, [n],
        new IrDeclare(sum, I(0)),
        new IrSwitch(R(n), [
          new IrSwitchSection([10000], false, Block(new IrAssign(sum, I(1)), new IrBreak())),
          new IrSwitchSection([-5000], false, Block(new IrAssign(sum, I(2))))
        ]),
        new IrReturn(R(sum))),
      // Default-only switch (0 labels)
      Function("EmptySwitch", IrType.I32, [n],
        new IrSwitch(R(n), [
          new IrSwitchSection([], true, Block(new IrReturn(I(77))))
        ]),
        new IrReturn(I(0))),
      // 64-bit switch
      Function("LongSwitch", IrType.I32, [x64],
        new IrSwitch(R(x64), [
          new IrSwitchSection([10000000000L], false, Block(new IrReturn(I(1)))),
          new IrSwitchSection([20000000000L], false, Block(new IrReturn(I(2)))),
          new IrSwitchSection([], true, Block(new IrReturn(I(3))))
        ]),
        new IrReturn(I(0))),
      // Recursive call + void call + IrExprStmt dropping 1-slot & 2-slot results
      Function("Fib", IrType.I32, [n],
        new IrIf(Bin(IrBinaryOp.LessOrEqual, R(n), I(1)), Block(new IrReturn(R(n))), null),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrCall("Fib", IrType.I32, [Bin(IrBinaryOp.Subtract, R(n), I(1))]),
          new IrCall("Fib", IrType.I32, [Bin(IrBinaryOp.Subtract, R(n), I(2))])))),
      Function("VoidHelper", IrType.Void, [],
        new IrGlobalAssign(counterGlobal, Bin(IrBinaryOp.Add, new IrGlobalRef(counterGlobal), I(1)))),
      Function("DropResults", IrType.I32, [],
        new IrExprStmt(new IrCall("VoidHelper", IrType.Void, [])),
        new IrExprStmt(I(123)),
        new IrExprStmt(I64(456)),
        new IrReturn(Bin(IrBinaryOp.Add, new IrGlobalRef(counterGlobal), new IrGlobalRef(uninitGlobal)))),
      // Steps (local, global, array u8, f32, f64, i64, checked narrow & checked wide)
      Function("StepTests", IrType.I32, [arr],
        new IrDeclare(i, I(5)),
        new IrDeclare(sum, new IrStep(R(i), true, true, false)), // sum=5, i=6
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrStep(R(i), false, false, true))), // i=5, sum=10
        new IrDeclare(stepLong, I64(10)),
        new IrExprStmt(new IrStep(R(stepLong), true, false, false)), // 11
        new IrDeclare(f32, F32(1.0f)),
        new IrExprStmt(new IrStep(R(f32), true, false, false)),
        new IrDeclare(f64, F64(2.0)),
        new IrExprStmt(new IrStep(R(f64), false, false, false)),
        new IrDeclare(nb, C(IrType.U8, 10)),
        new IrExprStmt(new IrStep(R(nb), true, false, true)), // 11
        new IrExprStmt(new IrStep(new IrGlobalRef(byteGlobal), true, false, false)), // 251
        new IrExprStmt(new IrStep(new IrArrayLoad(R(arr), I(0)), true, true, false)),
        new IrReturn(Bin(IrBinaryOp.Add, R(sum), new IrConvert(IrType.I32, new IrGlobalRef(byteGlobal))))),
      // Bind, IsNull, Optional (None/Some/IsNone/Unwrap/Coalesce), Conditional null merge
      Function("NullAndBind", IrType.String, [s, b],
        new IrDeclare(i, I(0)),
        new IrExprStmt(new IrBind(i, I(42))),
        new IrIf(new IrIsNull(R(s)), Block(new IrReturn(new IrStrConst("fallback"))), null),
        new IrReturn(new IrConditional(R(b), new IrNull(IrType.String), R(s)))),
      Function("OptionalOps", IrType.I32, [L("opt", IrType.Optional(IrType.I32))],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrCoalesce(new IrCoalesce(new IrLocalRef(L("opt", IrType.Optional(IrType.I32))), new IrNone(IrType.Optional(IrType.I32))), I(10)),
          new IrConditional(new IrIsNone(new IrLocalRef(L("opt", IrType.Optional(IrType.I32)))), I(0), new IrUnwrap(new IrSome(I(5))))))),
      // Module constants (scalar, string, primitive array, string array)
      Function("ReadConsts", IrType.I32, [],
        new IrReturn(Bin(IrBinaryOp.Add,
          Bin(IrBinaryOp.Add, new IrConstRef(scalarConst), new IrArrayLoad(new IrConstRef(tableConst), I(1))),
          Bin(IrBinaryOp.Add,
            new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrConstRef(strConst)]),
            new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrArrayLoad(new IrConstRef(strTableConst), I(0))])))))
    ], [tableConst, strTableConst, scalarConst, strConst]) {
      Globals = [counterGlobal, byteGlobal, uninitGlobal],
      Initializer = "ModuleInit"
    };

    var optParam = L("opt", IrType.Optional(IrType.I32));
    var fList = module.Functions.ToList();
    fList[^2] = Function("OptionalOps", IrType.I32, [optParam],
      new IrReturn(Bin(IrBinaryOp.Add,
        new IrCoalesce(new IrCoalesce(R(optParam), new IrNone(IrType.Optional(IrType.I32))), I(10)),
        new IrConditional(new IrIsNone(R(optParam)), I(0), new IrUnwrap(new IrSome(I(5)))))));
    module = module with { Functions = fList };

    var files = JvmEmitter.Emit(module);
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(Ctrl.Classify(-5) + "," + Ctrl.Classify(0) + "," + Ctrl.Classify(5));
          // While: i=1 (+1), i=2 (cont), i=3 (+3), i=4 (+4), i=5 (break) => 8; DoWhile => +100; For: 0+1+3 => +4 => 112
          System.out.println(Ctrl.Loops(10));
          System.out.println(Ctrl.DenseSwitch(1) + "," + Ctrl.DenseSwitch(2) + "," + Ctrl.DenseSwitch(3) + "," + Ctrl.DenseSwitch(9));
          System.out.println(Ctrl.SparseSwitch(10000) + "," + Ctrl.SparseSwitch(-5000) + "," + Ctrl.SparseSwitch(0));
          System.out.println(Ctrl.EmptySwitch(123));
          System.out.println(Ctrl.LongSwitch(10000000000L) + "," + Ctrl.LongSwitch(20000000000L) + "," + Ctrl.LongSwitch(0L));
          System.out.println(Ctrl.Fib(10));
          System.out.println(Ctrl.DropResults());
          byte[] buf = new byte[] { (byte)254 };
          System.out.println(Ctrl.StepTests(buf) + "," + (buf[0] & 0xFF));
          System.out.println(Ctrl.NullAndBind(null, false) + "|" + Ctrl.NullAndBind("ok", true) + "|" + Ctrl.NullAndBind("ok", false));
          System.out.println(Ctrl.OptionalOps(null) + "," + Ctrl.OptionalOps(7));
          System.out.println(Ctrl.ReadConsts());
        }
      }
      """);
    Assert.That(output, Is.EqualTo(
      "-1,0,1\n"
      + "112\n"
      + "10,20,20,99\n"
      + "1,2,0\n"
      + "77\n"
      + "1,2,3\n"
      + "55\n"
      + "110\n"
      + "261,255\n"
      + "fallback|null|ok\n"
      + "10,12\n"
      + "34"));
  }

  [Test]
  public void Exceptions_TryCatchFinally_Throw_Rethrow_Filter_AndBreakContinueReturnCrossingFinally_ExecuteCorrectly() {
    var logGlobal = new IrGlobal("Log", IrType.I32, I(0));
    var x = L("x", IrType.I32);
    var res = L("res", IrType.I32);
    var ex = L("ex", IrType.Object(IrExceptionClass.Exception));
    var aex = L("aex", IrType.Object(IrExceptionClass.ArithmeticException));
    var i = L("i", IrType.I32);

    var module = new IrModule("ExTest", [
      // Try/catch division by zero & explicit throw with Message + InnerException
      Function("CatchDivZero", IrType.I32, [x],
        new IrTry(
          Block(new IrReturn(Bin(IrBinaryOp.Divide, I(100), R(x)))),
          [new IrCatch(IrExceptionClass.DivideByZeroException, null, null, Block(new IrReturn(I(-1))))],
          null)),
      // Try/finally preserving return value computed BEFORE finally mutates global
      Function("ReturnWithFinally", IrType.I32, [],
        new IrGlobalAssign(logGlobal, I(10)),
        new IrTry(
          Block(new IrReturn(new IrGlobalRef(logGlobal))),
          [],
          Block(new IrGlobalAssign(logGlobal, Bin(IrBinaryOp.Add, new IrGlobalRef(logGlobal), I(5)))))),
      // Void return running finally
      Function("VoidReturnWithFinally", IrType.Void, [],
        new IrTry(
          Block(new IrReturn(null)),
          [],
          Block(new IrGlobalAssign(logGlobal, I(77))))),
      // Break and continue inside try crossing finally
      Function("LoopFinally", IrType.I32, [],
        new IrDeclare(res, I(0)),
        new IrDeclare(i, I(0)),
        new IrWhile(Bin(IrBinaryOp.Less, R(i), I(4)), Block(
          new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))),
          new IrTry(
            Block(
              new IrIf(Bin(IrBinaryOp.Equal, R(i), I(2)), Block(new IrContinue()), null),
              new IrIf(Bin(IrBinaryOp.Equal, R(i), I(3)), Block(new IrBreak()), null)),
            [],
            Block(new IrAssign(res, Bin(IrBinaryOp.Add, R(res), I(10))))))),
        new IrReturn(R(res))),
      // Catch with filter + rethrow + finally on catch
      Function("FilterAndRethrow", IrType.I32, [x],
        new IrDeclare(res, I(0)),
        new IrTry(
          Block(
            new IrTry(
              Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.ArithmeticException), IrExceptionClass.Constructor(IrExceptionClass.ArithmeticException, 1), [new IrStrConst("boom")]))),
              [new IrCatch(IrExceptionClass.ArithmeticException, aex, Bin(IrBinaryOp.Equal, R(x), I(1)), Block(
                new IrAssign(res, I(42)),
                new IrIf(Bin(IrBinaryOp.Equal, R(x), I(9)), Block(new IrRethrow()), null)))],
              Block(new IrAssign(res, Bin(IrBinaryOp.Add, R(res), I(1)))))),
          [new IrCatch(IrExceptionClass.Exception, ex, null, Block(
            new IrAssign(res, Bin(IrBinaryOp.Add, R(res), new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrFieldLoad(R(ex), IrExceptionClass.Message)])))))],
          null),
        new IrReturn(R(res))),
      // Rethrow from catch
      Function("RethrowPropagates", IrType.Void, [],
        new IrTry(
          Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.InvalidOperationException), IrExceptionClass.Constructor(IrExceptionClass.InvalidOperationException, 0), []))),
          [new IrCatch(IrExceptionClass.InvalidOperationException, null, null, Block(new IrRethrow()))],
          Block(new IrGlobalAssign(logGlobal, I(555))))),
      // Exception constructors (0-arg, 1-arg, 2-arg native & initCause) + Message + InnerException + IrThrowExpression
      Function("CauseMessage", IrType.String, [x],
        new IrTry(
          Block(
            new IrIf(Bin(IrBinaryOp.Equal, R(x), I(1)),
              Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.ArgumentException), IrExceptionClass.Constructor(IrExceptionClass.ArgumentException, 2), [
                new IrStrConst("outer1"),
                new IrNew(IrType.Object(IrExceptionClass.IndexOutOfRangeException), IrExceptionClass.Constructor(IrExceptionClass.IndexOutOfRangeException, 1), [new IrStrConst("inner1")])
              ]))),
              Block(new IrExprStmt(new IrThrowExpression(IrType.I32,
                new IrNew(IrType.Object(IrExceptionClass.FormatException), IrExceptionClass.Constructor(IrExceptionClass.FormatException, 2), [
                  new IrStrConst("outer2"),
                  new IrNew(IrType.Object(IrExceptionClass.NullReferenceException), IrExceptionClass.Constructor(IrExceptionClass.NullReferenceException, 1), [new IrStrConst("inner2")])
                ])))))),
          [new IrCatch(IrExceptionClass.Exception, ex, null, Block(
            new IrReturn(new IrFieldLoad(new IrFieldLoad(R(ex), IrExceptionClass.InnerException), IrExceptionClass.Message))))],
          null),
        new IrReturn(new IrStrConst("none"))),
      // Cover remaining exception class mappings (KeyNotFound, InvalidCast, Assertion)
      Function("OtherExceptions", IrType.I32, [x],
        new IrTry(
          Block(
            new IrIf(Bin(IrBinaryOp.Equal, R(x), I(1)),
              Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.KeyNotFoundException), IrExceptionClass.Constructor(IrExceptionClass.KeyNotFoundException, 0), []))),
              Block(new IrIf(Bin(IrBinaryOp.Equal, R(x), I(2)),
                Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.InvalidCastException), IrExceptionClass.Constructor(IrExceptionClass.InvalidCastException, 0), []))),
                Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.AssertionException), IrExceptionClass.Constructor(IrExceptionClass.AssertionException, 0), []))))))),
          [
            new IrCatch(IrExceptionClass.KeyNotFoundException, null, null, Block(new IrReturn(I(1)))),
            new IrCatch(IrExceptionClass.InvalidCastException, null, null, Block(new IrReturn(I(2)))),
            new IrCatch(IrExceptionClass.AssertionException, null, null, Block(new IrReturn(I(3))))
          ],
          null),
        new IrReturn(I(0)))
    ]) { Globals = [logGlobal] };

    var files = JvmEmitter.Emit(module);
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(ExTest.CatchDivZero(4) + "," + ExTest.CatchDivZero(0));
          int ret = ExTest.ReturnWithFinally();
          System.out.println(ret + "," + ExTest.Log);
          ExTest.VoidReturnWithFinally();
          System.out.println(ExTest.Log);
          // i=1 (normal +10), i=2 (continue +10), i=3 (break +10) => 30
          System.out.println(ExTest.LoopFinally());
          // x=1: filter passes -> res=42, finally +1 => 43; x=0: filter fails -> finally +1, outer catches "boom" (+4) => 5
          System.out.println(ExTest.FilterAndRethrow(1) + "," + ExTest.FilterAndRethrow(0));
          boolean rethrown = false;
          try { ExTest.RethrowPropagates(); } catch (IllegalStateException e) { rethrown = true; }
          System.out.println(rethrown + "," + ExTest.Log);
          System.out.println(ExTest.CauseMessage(1) + "," + ExTest.CauseMessage(2));
          System.out.println(ExTest.OtherExceptions(1) + "," + ExTest.OtherExceptions(2) + "," + ExTest.OtherExceptions(3));
        }
      }
      """);
    Assert.That(output, Is.EqualTo(
      "25,-1\n"
      + "10,15\n"
      + "77\n"
      + "30\n"
      + "43,5\n"
      + "true,555\n"
      + "inner1,inner2\n"
      + "1,2,3"));
  }

}
