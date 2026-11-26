using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Backends.VBScript;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.VBScript.VBScriptTestHost;

namespace Polypiler.Tests.Backends.VBScript;

/// <summary>
/// Live <c>wine cscript //Nologo</c> execution tests verifying emitted VBScript 5.8 matches
/// <see cref="IrInterpreter.Call(IrModule, string, object[])"/> across the IR surface.
/// </summary>
[TestFixture]
public sealed class VBScriptExecutionTests {

  [Test]
  public void SignedAndUnsigned32Bit_WrappingShiftsAndBitwise_MatchInterpreter() {
    var a = L("a", IrType.I32);
    var b = L("b", IrType.I32);
    var ua = L("a", IrType.U32);
    var ub = L("b", IrType.U32);
    var s = L("s", IrType.I32);
    var mod = new IrModule("Int32Ops", [
      Function("iAdd", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.Add, R(a), R(b)))),
      Function("iSub", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.Subtract, R(a), R(b)))),
      Function("iMul", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.Multiply, R(a), R(b)))),
      Function("iDiv", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.Divide, R(a), R(b)))),
      Function("iRem", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.Remainder, R(a), R(b)))),
      Function("iNeg", IrType.I32, [a], new IrReturn(new IrUnary(IrUnaryOp.Negate, R(a)))),
      Function("iNot", IrType.I32, [a], new IrReturn(new IrUnary(IrUnaryOp.BitNot, R(a)))),
      Function("iBit", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.Xor, Bin(IrBinaryOp.Or, R(a), R(b)), Bin(IrBinaryOp.And, R(a), R(b))))),
      Function("iShl", IrType.I32, [a, s], new IrReturn(Bin(IrBinaryOp.ShiftLeft, R(a), R(s)))),
      Function("iShr", IrType.I32, [a, s], new IrReturn(Bin(IrBinaryOp.ShiftRight, R(a), R(s)))),
      Function("iUShr", IrType.I32, [a, s], new IrReturn(Bin(IrBinaryOp.ShiftRightLogical, R(a), R(s)))),
      Function("uAdd", IrType.U32, [ua, ub], new IrReturn(Bin(IrBinaryOp.Add, R(ua), R(ub)))),
      Function("uSub", IrType.U32, [ua, ub], new IrReturn(Bin(IrBinaryOp.Subtract, R(ua), R(ub)))),
      Function("uMul", IrType.U32, [ua, ub], new IrReturn(Bin(IrBinaryOp.Multiply, R(ua), R(ub)))),
      Function("uDiv", IrType.U32, [ua, ub], new IrReturn(Bin(IrBinaryOp.Divide, R(ua), R(ub)))),
      Function("uRem", IrType.U32, [ua, ub], new IrReturn(Bin(IrBinaryOp.Remainder, R(ua), R(ub)))),
      Function("uNot", IrType.U32, [ua], new IrReturn(new IrUnary(IrUnaryOp.BitNot, R(ua)))),
      Function("uBit", IrType.U32, [ua, ub], new IrReturn(Bin(IrBinaryOp.Xor, Bin(IrBinaryOp.Or, R(ua), R(ub)), Bin(IrBinaryOp.And, R(ua), R(ub))))),
      Function("uShl", IrType.U32, [ua, s], new IrReturn(Bin(IrBinaryOp.ShiftLeft, R(ua), R(s)))),
      Function("uShr", IrType.U32, [ua, s], new IrReturn(Bin(IrBinaryOp.ShiftRight, R(ua), R(s)))),
      Function("uShrL", IrType.U32, [ua, s], new IrReturn(Bin(IrBinaryOp.ShiftRightLogical, R(ua), R(s)))),
      Function("uGt", IrType.Bool, [ua, ub], new IrReturn(Bin(IrBinaryOp.Greater, R(ua), R(ub)))),
      Function("narrow", IrType.I32, [a], new IrReturn(Bin(IrBinaryOp.Add,
        new IrConvert(IrType.I32, new IrConvert(IrType.I8, R(a))),
        Bin(IrBinaryOp.Add,
          new IrConvert(IrType.I32, new IrConvert(IrType.U8, R(a))),
          Bin(IrBinaryOp.Add,
            new IrConvert(IrType.I32, new IrConvert(IrType.I16, R(a))),
            new IrConvert(IrType.I32, new IrConvert(IrType.U16, R(a))))))))
    ]);

    var vbs = Emit(mod);
    var cases = new (string Call, string Fn, object[] Args)[] {
      ("iAdd(2147483647, 1)", "iAdd", [int.MaxValue, 1]),
      ("iSub(-2147483648.0, 1)", "iSub", [int.MinValue, 1]),
      ("iMul(2147483647, 2147483647)", "iMul", [int.MaxValue, int.MaxValue]),
      ("iDiv(-17, 5)", "iDiv", [-17, 5]),
      ("iRem(-17, 5)", "iRem", [-17, 5]),
      ("iNeg(-2147483648.0)", "iNeg", [int.MinValue]),
      ("iNot(12345)", "iNot", [12345]),
      ("iBit(170, 204)", "iBit", [170, 204]),
      ("iShl(1, 31)", "iShl", [1, 31]),
      ("iShl(1, 0)", "iShl", [1, 0]),
      ("iShr(-7, 1)", "iShr", [-7, 1]),
      ("iShr(-7, 0)", "iShr", [-7, 0]),
      ("iUShr(-1, 1)", "iUShr", [-1, 1]),
      ("iUShr(-1, 0)", "iUShr", [-1, 0]),
      ("uAdd(4294967295.0, 2)", "uAdd", [uint.MaxValue, 2u]),
      ("uSub(1, 5)", "uSub", [1u, 5u]),
      ("uMul(4000000000.0, 3)", "uMul", [4000000000u, 3u]),
      ("uDiv(4000000000.0, 3)", "uDiv", [4000000000u, 3u]),
      ("uRem(4000000000.0, 3)", "uRem", [4000000000u, 3u]),
      ("uNot(0)", "uNot", [0u]),
      ("uBit(3000000000.0, 2500000000.0)", "uBit", [3000000000u, 2500000000u]),
      ("uShl(1, 31)", "uShl", [1u, 31]),
      ("uShr(4294967295.0, 1)", "uShr", [uint.MaxValue, 1]),
      ("uShrL(4294967295.0, 4)", "uShrL", [uint.MaxValue, 4]),
      ("uGt(3000000000.0, 10)", "uGt", [3000000000u, 10u]),
      ("narrow(100000)", "narrow", [100000])
    };

    var driver = string.Join("\n", cases.Select(c => $"WScript.Echo test_fmt({c.Call})"));
    var actual = RunLines(vbs, driver);
    var expected = cases.Select(c => FormatExpected(IrInterpreter.Call(mod, c.Fn, c.Args))).ToArray();
    Assert.That(actual, Is.EqualTo(expected));
  }

  [Test]
  public void Int64AndUint64_Operations_MatchInterpreter() {
    var a = L("a", IrType.I64);
    var b = L("b", IrType.I64);
    var ua = L("a", IrType.U64);
    var ub = L("b", IrType.U64);
    var s = L("s", IrType.I32);
    var mod = new IrModule("Int64Ops", [
      Function("s64", IrType.I64, [a, b],
        new IrReturn(Bin(IrBinaryOp.Add, Bin(IrBinaryOp.Subtract, Bin(IrBinaryOp.Multiply, R(a), R(b)), Bin(IrBinaryOp.Divide, R(a), R(b))), Bin(IrBinaryOp.Remainder, R(a), R(b))))),
      Function("b64", IrType.I64, [a, b, s],
        new IrReturn(Bin(IrBinaryOp.Add,
          Bin(IrBinaryOp.Xor, Bin(IrBinaryOp.Or, R(a), R(b)), Bin(IrBinaryOp.And, R(a), R(b))),
          Bin(IrBinaryOp.Add, new IrUnary(IrUnaryOp.BitNot, R(a)),
            Bin(IrBinaryOp.Add, Bin(IrBinaryOp.ShiftLeft, R(a), R(s)),
              Bin(IrBinaryOp.Add, Bin(IrBinaryOp.ShiftRight, R(a), R(s)), Bin(IrBinaryOp.ShiftRightLogical, R(a), R(s)))))))),
      Function("u64", IrType.U64, [ua, ub, s],
        new IrReturn(Bin(IrBinaryOp.Add,
          Bin(IrBinaryOp.Xor, Bin(IrBinaryOp.Or, R(ua), R(ub)), Bin(IrBinaryOp.And, R(ua), R(ub))),
          Bin(IrBinaryOp.Add, Bin(IrBinaryOp.ShiftLeft, R(ua), R(s)),
            Bin(IrBinaryOp.Add, Bin(IrBinaryOp.ShiftRight, R(ua), R(s)), Bin(IrBinaryOp.ShiftRightLogical, R(ua), R(s))))))),
      Function("uNot64", IrType.U64, [ua], new IrReturn(new IrUnary(IrUnaryOp.BitNot, R(ua)))),
      Function("c64", IrType.I64, [], new IrReturn(C(IrType.I64, 5000000000L)))
    ]);

    var vbs = Emit(mod);
    var lines = RunLines(vbs, """
      WScript.Echo s64(100000.0, 37.0)
      WScript.Echo b64(1024.0, 768.0, 2)
      WScript.Echo u64(1024.0, 768.0, 2)
      WScript.Echo c64()
      """);
    Assert.That(lines, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(mod, "s64", [100000L, 37L])),
      FormatExpected(IrInterpreter.Call(mod, "b64", [1024L, 768L, 2])),
      FormatExpected(IrInterpreter.Call(mod, "u64", [1024UL, 768UL, 2])),
      FormatExpected(IrInterpreter.Call(mod, "c64", []))
    }));
  }

  [Test]
  public void CheckedArithmeticAndConversions_TrapOnOverflow() {
    var a = L("a", IrType.I32);
    var b = L("b", IrType.I32);
    var f = L("f", IrType.F64);
    var r = L("r", IrType.I32);
    var mod = new IrModule("Checked", [
      Function("chkAdd", IrType.I32, [a, b],
        new IrDeclare(r, I(0)),
        new IrTry(
          Block(new IrAssign(r, Bin(IrBinaryOp.AddChecked, R(a), R(b)))),
          [new IrCatch(IrExceptionClass.OverflowException, null, null, Block(new IrAssign(r, I(-1))))],
          null),
        new IrReturn(R(r))),
      Function("chkSub", IrType.I32, [a, b],
        new IrDeclare(r, I(0)),
        new IrTry(
          Block(new IrAssign(r, Bin(IrBinaryOp.SubtractChecked, R(a), R(b)))),
          [new IrCatch(IrExceptionClass.OverflowException, null, null, Block(new IrAssign(r, I(-2))))],
          null),
        new IrReturn(R(r))),
      Function("chkMul", IrType.I32, [a, b],
        new IrDeclare(r, I(0)),
        new IrTry(
          Block(new IrAssign(r, Bin(IrBinaryOp.MultiplyChecked, R(a), R(b)))),
          [new IrCatch(IrExceptionClass.OverflowException, null, null, Block(new IrAssign(r, I(-3))))],
          null),
        new IrReturn(R(r))),
      Function("chkNeg", IrType.I32, [a],
        new IrDeclare(r, I(0)),
        new IrTry(
          Block(new IrAssign(r, new IrUnary(IrUnaryOp.NegateChecked, R(a)))),
          [new IrCatch(IrExceptionClass.OverflowException, null, null, Block(new IrAssign(r, I(-4))))],
          null),
        new IrReturn(R(r))),
      Function("chkConv", IrType.I32, [a],
        new IrDeclare(r, I(0)),
        new IrTry(
          Block(new IrAssign(r, new IrConvert(IrType.I32, new IrConvert(IrType.U8, R(a), IrConversion.Trap)))),
          [new IrCatch(IrExceptionClass.OverflowException, null, null, Block(new IrAssign(r, I(-5))))],
          null),
        new IrReturn(R(r))),
      Function("f2iSat", IrType.I32, [f], new IrReturn(new IrConvert(IrType.I32, R(f), IrConversion.Saturate))),
      Function("f2iTrap", IrType.I32, [f],
        new IrDeclare(r, I(0)),
        new IrTry(
          Block(new IrAssign(r, new IrConvert(IrType.I32, R(f), IrConversion.Trap))),
          [new IrCatch(IrExceptionClass.OverflowException, null, null, Block(new IrAssign(r, I(-6))))],
          null),
        new IrReturn(R(r)))
    ]);

    var vbs = Emit(mod);
    var lines = RunLines(vbs, """
      WScript.Echo chkAdd(2147483647, 1)
      WScript.Echo chkAdd(10, 20)
      WScript.Echo chkSub(-2147483648.0, 1)
      WScript.Echo chkMul(100000, 100000)
      WScript.Echo chkNeg(-2147483648.0)
      WScript.Echo chkConv(300)
      WScript.Echo chkConv(42)
      WScript.Echo f2iSat(1E+20)
      WScript.Echo f2iSat(-1E+20)
      WScript.Echo f2iSat(12.9)
      WScript.Echo f2iTrap(1E+20)
      """);
    Assert.That(lines, Is.EqualTo(new[] {
      "-1", "30", "-2", "-3", "-4", "-5", "42",
      int.MaxValue.ToString(CultureInfo.InvariantCulture),
      int.MinValue.ToString(CultureInfo.InvariantCulture),
      "12", "-6"
    }));
  }

  [Test]
  public void FloatsAndBitIntrinsics_AndLegalization_MatchInterpreter() {
    var x = L("x", IrType.F64);
    var y = L("y", IrType.F64);
    var i = L("i", IrType.I32);
    var u = L("u", IrType.U32);
    var n = L("n", IrType.I32);
    var mod = new IrModule("FloatAndBits", [
      Function("fOps", IrType.F64, [x, y],
        new IrReturn(Bin(IrBinaryOp.Add, Bin(IrBinaryOp.Subtract, Bin(IrBinaryOp.Multiply, R(x), R(y)), Bin(IrBinaryOp.Divide, R(x), R(y))), Bin(IrBinaryOp.Remainder, R(x), R(y))))),
      Function("fNeg", IrType.F64, [x], new IrReturn(new IrUnary(IrUnaryOp.Negate, R(x)))),
      Function("fAbs", IrType.F64, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F64, [R(x)]))),
      Function("fMin", IrType.F64, [x, y], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Min, IrType.F64, [R(x), R(y)]))),
      Function("fMax", IrType.F64, [x, y], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Max, IrType.F64, [R(x), R(y)]))),
      Function("fSgn", IrType.I32, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(x)]))),
      Function("fSqrt", IrType.F64, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [R(x)]))),
      Function("fFloor", IrType.F64, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Floor, IrType.F64, [R(x)]))),
      Function("fCeil", IrType.F64, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Ceiling, IrType.F64, [R(x)]))),
      Function("fTrunc", IrType.F64, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Truncate, IrType.F64, [R(x)]))),
      Function("fREven", IrType.F64, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundEven, IrType.F64, [R(x)]))),
      Function("fRAway", IrType.F64, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundAway, IrType.F64, [R(x)]))),
      Function("fRHUp", IrType.F64, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundHalfUp, IrType.F64, [R(x)]))),
      Function("fCopy", IrType.F64, [x, y], new IrReturn(new IrIntrinsic(IrIntrinsicOp.CopySign, IrType.F64, [R(x), R(y)]))),
      Function("fToI32", IrType.I32, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ToInt32, IrType.I32, [R(x)]))),
      Function("fToU32", IrType.U32, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ToUint32, IrType.U32, [R(x)]))),
      Function("iAbs", IrType.I32, [i], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.I32, [R(i)]))),
      Function("uAbs", IrType.U32, [u], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.U32, [R(u)]))),
      Function("rotl", IrType.U32, [u, n], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RotateLeft, IrType.U32, [R(u), R(n)]))),
      Function("rotr", IrType.U32, [u, n], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RotateRight, IrType.U32, [R(u), R(n)]))),
      Function("popc", IrType.I32, [u], new IrReturn(new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [R(u)]))),
      Function("clz", IrType.I32, [u], new IrReturn(new IrIntrinsic(IrIntrinsicOp.LeadingZeroCount, IrType.I32, [R(u)]))),
      Function("ctz", IrType.I32, [u], new IrReturn(new IrIntrinsic(IrIntrinsicOp.TrailingZeroCount, IrType.I32, [R(u)]))),
      Function("bswap", IrType.U32, [u], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.U32, [R(u)]))),
      Function("fConsts", IrType.F64, [], new IrReturn(Bin(IrBinaryOp.Add, F64(3.0), new IrConvert(IrType.F64, I(2)))))
    ]);

    var vbs = LegalizedEmit(mod);
    var calls = new (string Vbs, string Fn, object[] Args)[] {
      ("fOps(7.5, 2.5)", "fOps", [7.5, 2.5]),
      ("fNeg(3.5)", "fNeg", [3.5]),
      ("fAbs(-9.25)", "fAbs", [-9.25]),
      ("fMin(3.0, -2.0)", "fMin", [3.0, -2.0]),
      ("fMax(3.0, -2.0)", "fMax", [3.0, -2.0]),
      ("fSgn(-5.0)", "fSgn", [-5.0]),
      ("fSqrt(81.0)", "fSqrt", [81.0]),
      ("fFloor(-2.3)", "fFloor", [-2.3]),
      ("fCeil(-2.3)", "fCeil", [-2.3]),
      ("fTrunc(-2.9)", "fTrunc", [-2.9]),
      ("fREven(2.5)", "fREven", [2.5]),
      ("fRAway(2.5)", "fRAway", [2.5]),
      ("fRAway(-2.5)", "fRAway", [-2.5]),
      ("fRHUp(2.5)", "fRHUp", [2.5]),
      ("fCopy(4.5, -1.0)", "fCopy", [4.5, -1.0]),
      ("fToI32(-42.9)", "fToI32", [-42.9]),
      ("fToU32(3000000000.7)", "fToU32", [3000000000.7]),
      ("iAbs(-77)", "iAbs", [-77]),
      ("uAbs(99)", "uAbs", [99u]),
      ("rotl(2147483649.0, 1)", "rotl", [0x80000001u, 1]),
      ("rotr(3, 1)", "rotr", [3u, 1]),
      ("popc(2863311530.0)", "popc", [0xAAAAAAAAu]),
      ("clz(16)", "clz", [16u]),
      ("ctz(40)", "ctz", [40u]),
      ("bswap(305419896)", "bswap", [0x12345678u]),
      ("fConsts()", "fConsts", [])
    };

    var lines = RunLines(vbs, string.Join("\n", calls.Select(c => $"WScript.Echo test_fmt({c.Vbs})")));
    var expected = calls.Select(c => FormatExpected(IrInterpreter.Call(mod, c.Fn, c.Args))).ToArray();
    Assert.That(lines, Is.EqualTo(expected));
  }

  [Test]
  public void LoopsBreakContinueSwitchAndShortCircuit_MatchInterpreter() {
    var n = L("n", IrType.I32);
    var i = L("i", IrType.I32);
    var sum = L("sum", IrType.I32);
    var a = L("a", IrType.I32);
    var b = L("b", IrType.I32);

    var mod = new IrModule("Ctrl", [
      // Short-circuit && and || must NOT divide by zero when LHS decides!
      Function("scAnd", IrType.Bool, [a, b],
        new IrReturn(Bin(IrBinaryOp.LogicalAnd, Bin(IrBinaryOp.NotEqual, R(a), I(0)), Bin(IrBinaryOp.Greater, Bin(IrBinaryOp.Divide, R(b), R(a)), I(2))))),
      Function("scOr", IrType.Bool, [a, b],
        new IrReturn(Bin(IrBinaryOp.LogicalOr, Bin(IrBinaryOp.Equal, R(a), I(0)), Bin(IrBinaryOp.Greater, Bin(IrBinaryOp.Divide, R(b), R(a)), I(2))))),
      // If / ElseIf / Else including nested ElseIf with short-circuit condition!
      Function("classify", IrType.I32, [a, b],
        new IrIf(Bin(IrBinaryOp.Less, R(a), I(0)),
          Block(new IrReturn(I(-1))),
          Block(new IrIf(Bin(IrBinaryOp.LogicalAnd, Bin(IrBinaryOp.Greater, R(a), I(0)), Bin(IrBinaryOp.Greater, R(b), I(0))),
            Block(new IrReturn(I(1))),
            Block(new IrIf(Bin(IrBinaryOp.Equal, R(a), I(0)),
              Block(new IrReturn(I(0))),
              Block(new IrReturn(I(2))))))))),
      // Counted For..To..Next loop
      Function("sumCounted", IrType.I32, [n],
        new IrDeclare(sum, I(0)),
        new IrDeclare(i, I(0)),
        new IrFor(Bin(IrBinaryOp.Less, R(i), R(n)),
          Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(i)))),
          Block(new IrExprStmt(new IrStep(R(i), true, true, false)))),
        new IrReturn(R(sum))),
      // General IrFor with BOTH continue (skip odd) and break (stop at 10)
      Function("sumEvenUntil10", IrType.I32, [n],
        new IrDeclare(sum, I(0)),
        new IrDeclare(i, I(0)),
        new IrFor(Bin(IrBinaryOp.Less, R(i), R(n)),
          Block(
            new IrIf(Bin(IrBinaryOp.Equal, R(i), I(10)), Block(new IrBreak()), null),
            new IrIf(Bin(IrBinaryOp.NotEqual, Bin(IrBinaryOp.Remainder, R(i), I(2)), I(0)), Block(new IrContinue()), null),
            new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(i)))),
          Block(new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))))),
        new IrReturn(R(sum))),
      // While with short-circuit condition + break/continue
      Function("whileSc", IrType.I32, [n],
        new IrDeclare(sum, I(0)),
        new IrDeclare(i, I(1)),
        new IrWhile(Bin(IrBinaryOp.LogicalAnd, Bin(IrBinaryOp.LessOrEqual, R(i), R(n)), Bin(IrBinaryOp.Less, R(sum), I(20))),
          Block(
            new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(i))),
            new IrExprStmt(new IrStep(R(i), true, false, false)))),
        new IrReturn(R(sum))),
      // DoWhile with continue and short-circuit condition
      Function("doWhileTest", IrType.I32, [n],
        new IrDeclare(sum, I(0)),
        new IrDeclare(i, I(0)),
        new IrDoWhile(
          Block(
            new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))),
            new IrIf(Bin(IrBinaryOp.Equal, R(i), I(3)), Block(new IrContinue()), null),
            new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(i)))),
          Bin(IrBinaryOp.LogicalAnd, Bin(IrBinaryOp.Less, R(i), R(n)), Bin(IrBinaryOp.Less, R(sum), I(50)))),
        new IrReturn(R(sum))),
      // Switch with multi-labels, default, early break inside If, and continue targeting enclosing loop!
      Function("switchLoop", IrType.I32, [n],
        new IrDeclare(sum, I(0)),
        new IrDeclare(i, I(0)),
        new IrWhile(Bin(IrBinaryOp.Less, R(i), R(n)),
          Block(
            new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))),
            new IrSwitch(R(i), [
              new([1, 2], false, Block(
                new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), I(10))),
                new IrIf(Bin(IrBinaryOp.Equal, R(i), I(1)), Block(new IrBreak()), null),
                new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), I(1))))),
              new([3], false, Block(new IrContinue())),
              new([], true, Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(i))), new IrBreak()))
            ]),
            new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), I(100))))),
        new IrReturn(R(sum))),
      // Conditional expression + IrBind + postfix/prefix IrStep
      Function("exprMix", IrType.I32, [a],
        new IrDeclare(b, I(0)),
        new IrDeclare(sum, new IrConditional(Bin(IrBinaryOp.Greater, R(a), I(0)),
          Bin(IrBinaryOp.Add, new IrBind(b, R(a)), new IrStep(R(b), true, true, false)),
          new IrStep(R(a), false, false, false))),
        new IrReturn(Bin(IrBinaryOp.Add, R(sum), R(b))))
    ]);

    var vbs = Emit(mod);
    Assert.That(vbs, Does.Contain("For "));
    Assert.That(vbs, Does.Contain("Select Case "));

    var calls = new (string Vbs, string Fn, object[] Args)[] {
      ("scAnd(0, 10)", "scAnd", [0, 10]),
      ("scAnd(2, 10)", "scAnd", [2, 10]),
      ("scOr(0, 10)", "scOr", [0, 10]),
      ("scOr(5, 10)", "scOr", [5, 10]),
      ("classify(-3, 1)", "classify", [-3, 1]),
      ("classify(3, 1)", "classify", [3, 1]),
      ("classify(0, -1)", "classify", [0, -1]),
      ("classify(3, -1)", "classify", [3, -1]),
      ("sumCounted(6)", "sumCounted", [6]),
      ("sumEvenUntil10(20)", "sumEvenUntil10", [20]),
      ("whileSc(10)", "whileSc", [10]),
      ("doWhileTest(6)", "doWhileTest", [6]),
      ("switchLoop(5)", "switchLoop", [5]),
      ("exprMix(5)", "exprMix", [5]),
      ("exprMix(-2)", "exprMix", [-2])
    };

    var lines = RunLines(vbs, string.Join("\n", calls.Select(c => $"WScript.Echo test_fmt({c.Vbs})")));
    var expected = calls.Select(c => FormatExpected(IrInterpreter.Call(mod, c.Fn, c.Args))).ToArray();
    Assert.That(lines, Is.EqualTo(expected));
  }

  [Test]
  public void ArraysStructsClassesEnumsGlobalsAndOptionals_MatchInterpreter() {
    // Struct with value semantics
    var point = new IrStructType("Point");
    var px = new IrField("X", IrType.I32, false);
    var py = new IrField("Y", IrType.I32, false);
    point.Define([px, py]);
    var pType = IrType.Struct(point);

    // Enum
    var cRed = new IrEnumMember("Red", 1);
    var cGreen = new IrEnumMember("Green", 2);
    var cBlue = new IrEnumMember("Blue", 4);
    var color = new IrEnumType("Color", IrType.I32, [cRed, cGreen, cBlue], isFlags: true);
    var cType = IrType.Enum(color);

    // Module constants & globals
    var tbl = new IrModuleConst("Table", IrType.Array(IrType.I32), [10, 20, 30]);
    var kScalar = new IrModuleConst("Factor", IrType.I32, [7]);
    var kStr = IrModuleConst.OfStrings("Greeting", IrType.String, ["hi\nthere"]);
    var gCount = new IrGlobal("Counter", IrType.I32, I(5));

    // Class hierarchy with virtual dispatch & record equality
    var animal = new IrClassType("Animal");
    var aAge = new IrField("Age", IrType.I32, false);
    animal.Define([aAge]);
    animal.Inherit(null, []);
    var animalCtor = new IrConstructor("Animal_ctor");
    var animalSpeak = new IrMethod("Speak", "Animal_Speak", false) { Dispatch = IrDispatch.Virtual };
    animal.DefineMembers([animalCtor], [animalSpeak], [], []);

    var dog = new IrClassType("Dog") { IsRecord = true };
    var dTag = new IrField("Tag", IrType.I32, false);
    dog.Define([dTag]);
    dog.Inherit(animal, []);
    var dogCtor = new IrConstructor("Dog_ctor");
    var dogSpeak = new IrMethod("Speak", "Dog_Speak", false) { Dispatch = IrDispatch.Override, Overrides = animalSpeak };
    dog.DefineMembers([dogCtor], [dogSpeak], [], []);

    var thisA = L("this", IrType.Object(animal));
    var thisD = L("this", IrType.Object(dog));
    var ageP = L("age", IrType.I32);
    var tagP = L("tag", IrType.I32);

    var p1 = L("p1", pType);
    var p2 = L("p2", pType);
    var pArr = L("pa", IrType.Array(pType));
    var arr = L("arr", IrType.Array(IrType.I32));
    var buf = L("buf", IrType.Array(IrType.U8));
    var opt = L("opt", IrType.Optional(IrType.I32));
    var optP = L("optP", IrType.Optional(pType));
    var d1 = L("d1", IrType.Object(dog));
    var d2 = L("d2", IrType.Object(dog));
    var aRef = L("aRef", IrType.Object(animal));
    var eVal = L("eVal", cType);
    var eStr = L("eStr", IrType.String);

    var mod = new IrModule("Rich", [
      Function("Animal_ctor", false, IrType.Void, [thisA, ageP],
        new IrFieldStore(R(thisA), aAge, R(ageP))),
      Function("Animal_Speak", false, IrType.I32, [thisA],
        new IrReturn(new IrFieldLoad(R(thisA), aAge))),
      Function("Dog_ctor", false, IrType.Void, [thisD, ageP, tagP],
        new IrExprStmt(new IrCall("Animal_ctor", IrType.Void, [R(thisD), R(ageP)])),
        new IrFieldStore(R(thisD), dTag, R(tagP))),
      Function("Dog_Speak", false, IrType.I32, [thisD],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrMethodCall(IrType.I32, animalSpeak, R(thisD), []) { Kind = IrMethodCallKind.Base },
          I(100)))),
      Function("testStruct", IrType.I32, [],
        new IrDeclare(p1, new IrStructNew(pType, [I(3), I(4)])),
        new IrDeclare(p2, R(p1)),
        new IrFieldStore(R(p2), px, I(99)),
        new IrDeclare(pArr, new IrNewArray(IrType.Array(pType), I(2))),
        new IrStore(R(pArr), I(0), R(p1)),
        new IrFieldStore(new IrArrayLoad(R(pArr), I(1)), py, I(8)),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrFieldLoad(R(p1), px),
          Bin(IrBinaryOp.Add,
            new IrFieldLoad(R(p2), px),
            Bin(IrBinaryOp.Add,
              new IrFieldLoad(new IrArrayLoad(R(pArr), I(1)), py),
              new IrConditional(new IrStructEquals(R(p1), new IrStructNew(pType, [I(3), I(4)])), I(1000), I(0))))))),
      Function("testArrays", IrType.I32, [],
        new IrDeclare(arr, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(1), I(2), I(3), I(4)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [R(arr), I(0), R(arr), I(1), I(3)])),
        new IrDeclare(buf, new IrNewArray(IrType.Array(IrType.U8), I(8))),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [R(buf), C(IrType.U8, 5)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(buf), I(0), C(IrType.U32, 0x01020304)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(buf), I(4), C(IrType.I16, -2)])),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrArrayLoad(R(arr), I(3)),
          Bin(IrBinaryOp.Add,
            new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U32, [R(buf), I(0)])),
            Bin(IrBinaryOp.Add,
              new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.I16, [R(buf), I(4)])),
              Bin(IrBinaryOp.Add, new IrArrayLength(new IrNewArray(IrType.Array(IrType.I32), I(0))),
                new IrArrayLoad(new IrConstRef(tbl), I(2)))))))),
      Function("testGlobals", IrType.I32, [],
        new IrGlobalAssign(gCount, Bin(IrBinaryOp.Add, new IrGlobalRef(gCount), new IrConstRef(kScalar))),
        new IrReturn(Bin(IrBinaryOp.Add, new IrGlobalRef(gCount), new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrConstRef(kStr)])))),
      Function("testClass", IrType.I32, [],
        new IrDeclare(d1, new IrNew(IrType.Object(dog), dogCtor, [I(7), I(5)])),
        new IrDeclare(d2, new IrNew(IrType.Object(dog), dogCtor, [I(7), I(5)])),
        new IrDeclare(aRef, R(d1)),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrMethodCall(IrType.I32, animalSpeak, R(aRef), []) { Kind = IrMethodCallKind.Virtual },
          Bin(IrBinaryOp.Add,
            new IrConditional(new IrTypeTest(R(aRef), IrType.Object(dog)), I(10), I(0)),
            Bin(IrBinaryOp.Add,
              new IrConditional(new IrStructEquals(R(d1), R(d2)), I(20), I(0)),
              Bin(IrBinaryOp.Add,
                new IrConditional(new IrReferenceEquals(R(d1), R(d2)), I(40), I(0)),
                new IrFieldLoad(new IrCast(IrType.Object(dog), R(aRef)), aAge))))))),
      Function("testOptionals", IrType.I32, [opt],
        new IrDeclare(optP, new IrNone(IrType.Optional(pType))),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrCoalesce(R(opt), I(42)),
          new IrConditional(new IrIsNone(R(optP)),
            new IrFieldLoad(new IrCoalesce(R(optP), new IrStructDefault(pType)), px),
            I(999))))),
      Function("testEnum", IrType.String, [eVal, eStr],
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [
          new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(eVal)]),
          new IrStrConst(":"),
          new IrIntrinsic(IrIntrinsicOp.FormatBoolEcma, IrType.String, [new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [R(eVal)])]),
          new IrStrConst(":"),
          new IrIntrinsic(IrIntrinsicOp.FormatBoolDotNet, IrType.String, [new IrIntrinsic(IrIntrinsicOp.EnumHasFlag, IrType.Bool, [R(eVal), C(cType, 1)])]),
          new IrStrConst(":"),
          new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.EnumParse, cType, [R(eStr), B(false)]))])
        ])))
    ], [tbl, kScalar, kStr]) {
      Types = [point, color, animal, dog],
      Globals = [gCount]
    };

    var vbs = Emit(mod);
    var lines = RunLines(vbs, """
      WScript.Echo testStruct()
      WScript.Echo testArrays()
      WScript.Echo testGlobals()
      WScript.Echo testClass()
      WScript.Echo testOptionals(Null)
      WScript.Echo testOptionals(10)
      WScript.Echo testEnum(Color_Red, "Blue")
      """);
    Assert.That(lines, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(mod, "testStruct", [])),
      FormatExpected(IrInterpreter.Call(mod, "testArrays", [])),
      FormatExpected(IrInterpreter.Call(mod, "testGlobals", [])),
      FormatExpected(IrInterpreter.Call(mod, "testClass", [])),
      "42",
      "10",
      "Red:true:True:4"
    }));
  }

  [Test]
  public void TryCatchFinallyThrowRethrowAndFilters_MatchInterpreter() {
    var mode = L("mode", IrType.I32);
    var acc = L("acc", IrType.I32);
    var ex = L("ex", IrExceptionClass.Reference);
    var aex = L("aex", IrType.Object(IrExceptionClass.ArithmeticException));
    var msg = L("msg", IrType.String);
    var i = L("i", IrType.I32);

    var mod = new IrModule("Exc", [
      Function("divZero", IrType.I32, [mode],
        new IrDeclare(acc, I(0)),
        new IrTry(
          Block(
            new IrIf(Bin(IrBinaryOp.Equal, R(mode), I(1)),
              Block(new IrAssign(acc, Bin(IrBinaryOp.Divide, I(10), I(0)))),
              Block(new IrIf(Bin(IrBinaryOp.Equal, R(mode), I(2)),
                Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.InvalidOperationException), IrExceptionClass.Constructor(IrExceptionClass.InvalidOperationException, 1), [new IrStrConst("bad state")]))),
                Block(new IrReturn(I(77)))))),
            new IrAssign(acc, I(999))),
          [
            new IrCatch(IrExceptionClass.ArithmeticException, aex, null,
              Block(new IrAssign(acc, I(100)))),
            new IrCatch(IrExceptionClass.Exception, ex,
              new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [new IrFieldLoad(R(ex), IrExceptionClass.Message), new IrStrConst("bad state")]),
              Block(new IrAssign(acc, I(200))))
          ],
          Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), I(1))))),
        new IrReturn(R(acc))),
      Function("rethrowTest", IrType.String, [],
        new IrDeclare(msg, new IrStrConst("")),
        new IrTry(
          Block(
            new IrTry(
              Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.InvalidOperationException), IrExceptionClass.Constructor(IrExceptionClass.InvalidOperationException, 1), [new IrStrConst("inner")]))),
              [new IrCatch(IrExceptionClass.Exception, null, null, Block(new IrRethrow()))],
              null)),
          [new IrCatch(IrExceptionClass.Exception, ex, null, Block(new IrAssign(msg, new IrFieldLoad(R(ex), IrExceptionClass.Message))))],
          null),
        new IrReturn(R(msg))),
      Function("finallyBreakContinue", IrType.I32, [],
        new IrDeclare(acc, I(0)),
        new IrDeclare(i, I(0)),
        new IrWhile(Bin(IrBinaryOp.Less, R(i), I(5)),
          Block(
            new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))),
            new IrTry(
              Block(
                new IrIf(Bin(IrBinaryOp.Equal, R(i), I(2)), Block(new IrContinue()), null),
                new IrIf(Bin(IrBinaryOp.Equal, R(i), I(4)), Block(new IrBreak()), null),
                new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), I(10)))),
              [],
              Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), I(1))))))),
        new IrReturn(R(acc)))
    ]);

    var vbs = Emit(mod);
    var lines = RunLines(vbs, """
      WScript.Echo divZero(0)
      WScript.Echo divZero(1)
      WScript.Echo divZero(2)
      WScript.Echo rethrowTest()
      WScript.Echo finallyBreakContinue()
      """);
    Assert.That(lines, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(mod, "divZero", [0])),
      FormatExpected(IrInterpreter.Call(mod, "divZero", [1])),
      FormatExpected(IrInterpreter.Call(mod, "divZero", [2])),
      FormatExpected(IrInterpreter.Call(mod, "rethrowTest", [])),
      FormatExpected(IrInterpreter.Call(mod, "finallyBreakContinue", []))
    }));
  }

  [Test]
  public void StringIntrinsics_AllOperations_MatchInterpreter() {
    var s = L("s", IrType.String);
    var t = L("t", IrType.String);
    var units = L("u", IrType.Array(IrType.U16));
    var parts = L("p", IrType.Array(IrType.String));

    var mod = new IrModule("StrOps", [
      Function("sLen", IrType.I32, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [R(s)]))),
      Function("sUnit", IrType.U16, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [R(s), I(1)]))),
      Function("sCat", IrType.String, [s, t], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [R(s), new IrStrConst("-"), R(t)]))),
      Function("sFromU", IrType.String, [],
        new IrDeclare(units, new IrIntrinsic(IrIntrinsicOp.StrToCodeUnits, IrType.Array(IrType.U16), [new IrStrConst("ABCD")])),
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnits, IrType.String, [R(units), I(1), I(2)]))),
      Function("sRep", IrType.String, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrRepeat, IrType.String, [R(s), I(3)]))),
      Function("sSub", IrType.String, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [R(s), I(2), I(3)]))),
      Function("sSubEnd", IrType.String, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [R(s), I(3)]))),
      Function("sEq", IrType.Bool, [s, t], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [R(s), R(t)]))),
      Function("sCmp", IrType.I32, [s, t], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [R(s), R(t)]))),
      Function("sIdx", IrType.I32, [s, t], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [R(s), R(t), I(1)]))),
      Function("sLIdx", IrType.I32, [s, t], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrLastIndexOf, IrType.I32, [R(s), R(t)]))),
      Function("sSW", IrType.Bool, [s, t], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrStartsWith, IrType.Bool, [R(s), R(t)]))),
      Function("sEW", IrType.Bool, [s, t], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrEndsWith, IrType.Bool, [R(s), R(t)]))),
      Function("sHas", IrType.Bool, [s, t], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrContains, IrType.Bool, [R(s), R(t)]))),
      Function("sRepl", IrType.String, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrReplaceAll, IrType.String, [R(s), new IrStrConst("a"), new IrStrConst("X")]))),
      Function("sRepl1", IrType.String, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrReplaceFirst, IrType.String, [R(s), new IrStrConst("a"), new IrStrConst("X")]))),
      Function("sSplitJoin", IrType.String, [s],
        new IrDeclare(parts, new IrIntrinsic(IrIntrinsicOp.StrSplit, IrType.Array(IrType.String), [R(s), new IrStrConst(","), I(2)])),
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrJoin, IrType.String, [new IrStrConst("|"), R(parts)]))),
      Function("sTrim", IrType.String, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrTrim, IrType.String, [R(s), StringOperations.Set(WhitespaceSet.DotNet)]))),
      Function("sTrimS", IrType.String, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrTrimStart, IrType.String, [R(s), StringOperations.Set(WhitespaceSet.DotNet)]))),
      Function("sTrimE", IrType.String, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrTrimEnd, IrType.String, [R(s), StringOperations.Set(WhitespaceSet.DotNet)]))),
      Function("sPadS", IrType.String, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrPadStart, IrType.String, [R(s), I(6), new IrStrConst("0")]))),
      Function("sPadE", IrType.String, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrPadEnd, IrType.String, [R(s), I(6), new IrStrConst(".")])))
    ]);

    var vbs = Emit(mod);
    var calls = new (string Vbs, string Fn, object[] Args)[] {
      ("sLen(\"hello\")", "sLen", ["hello"]),
      ("sUnit(\"ABC\")", "sUnit", ["ABC"]),
      ("sCat(\"hi\", \"ok\")", "sCat", ["hi", "ok"]),
      ("sFromU()", "sFromU", []),
      ("sRep(\"ab\")", "sRep", ["ab"]),
      ("sSub(\"abcdef\")", "sSub", ["abcdef"]),
      ("sSubEnd(\"abcdef\")", "sSubEnd", ["abcdef"]),
      ("sEq(\"Abc\", \"abc\")", "sEq", ["Abc", "abc"]),
      ("sEq(\"Abc\", \"Abc\")", "sEq", ["Abc", "Abc"]),
      ("sCmp(\"abc\", \"abd\")", "sCmp", ["abc", "abd"]),
      ("sIdx(\"banana\", \"na\")", "sIdx", ["banana", "na"]),
      ("sLIdx(\"banana\", \"na\")", "sLIdx", ["banana", "na"]),
      ("sSW(\"hello\", \"he\")", "sSW", ["hello", "he"]),
      ("sEW(\"hello\", \"lo\")", "sEW", ["hello", "lo"]),
      ("sHas(\"hello\", \"ell\")", "sHas", ["hello", "ell"]),
      ("sHas(\"hello\", \"\")", "sHas", ["hello", ""]),
      ("sRepl(\"aAa\")", "sRepl", ["aAa"]),
      ("sRepl1(\"banana\")", "sRepl1", ["banana"]),
      ("sSplitJoin(\"a,b,c\")", "sSplitJoin", ["a,b,c"]),
      ("sTrim(\"  hi  \")", "sTrim", ["  hi  "]),
      ("sTrimS(\"  hi  \")", "sTrimS", ["  hi  "]),
      ("sTrimE(\"  hi  \")", "sTrimE", ["  hi  "]),
      ("sPadS(\"42\")", "sPadS", ["42"]),
      ("sPadE(\"42\")", "sPadE", ["42"])
    };

    var lines = RunLines(vbs, string.Join("\n", calls.Select(c => $"WScript.Echo test_fmt({c.Vbs})")));
    var expected = calls.Select(c => FormatExpected(IrInterpreter.Call(mod, c.Fn, c.Args))).ToArray();
    Assert.That(lines, Is.EqualTo(expected));
  }

  [Test]
  public void CSharpFrontendImport_Adler32AndFibonacci_MatchInterpreterLive() {
    var mod = ImportCSharp("""
      public static uint Adler32(byte[] data) {
        uint a = 1;
        uint b = 0;
        for (int i = 0; i < data.Length; ++i) {
          a = (a + data[i]) % 65521u;
          b = (b + a) % 65521u;
        }
        return (b << 16) | a;
      }

      public static int Fib(int n) {
        if (n <= 1) return n;
        int a = 0;
        int b = 1;
        for (int i = 2; i <= n; ++i) {
          int t = a + b;
          a = b;
          b = t;
        }
        return b;
      }
      """);

    var vbs = LegalizedEmit(mod);
    var wikiBytes = Encoding.ASCII.GetBytes("Wikipedia");
    var seqBytes = Enumerable.Range(0, 64).Select(x => (byte)((x * 37 + 13) & 0xFF)).ToArray();

    string VbsByteArray(byte[] data) => data.Length == 0
      ? "Array()"
      : "Array(" + string.Join(", ", data.Select(b => b.ToString(CultureInfo.InvariantCulture))) + ")";

    var driver = $"""
      WScript.Echo Adler32({VbsByteArray([])})
      WScript.Echo Adler32({VbsByteArray(wikiBytes)})
      WScript.Echo Adler32({VbsByteArray(seqBytes)})
      WScript.Echo Fib(0)
      WScript.Echo Fib(10)
      WScript.Echo Fib(20)
      """;

    var lines = RunLines(vbs, driver);
    Assert.That(lines, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(mod, "Adler32", [Array.Empty<byte>()])),
      FormatExpected(IrInterpreter.Call(mod, "Adler32", [wikiBytes])),
      FormatExpected(IrInterpreter.Call(mod, "Adler32", [seqBytes])),
      FormatExpected(IrInterpreter.Call(mod, "Fib", [0])),
      FormatExpected(IrInterpreter.Call(mod, "Fib", [10])),
      FormatExpected(IrInterpreter.Call(mod, "Fib", [20]))
    }));
    // Sanity check well-known Adler-32("Wikipedia") == 0x11E60398 == 300286872.
    Assert.That(lines[1], Is.EqualTo("300286872"));
  }

}
