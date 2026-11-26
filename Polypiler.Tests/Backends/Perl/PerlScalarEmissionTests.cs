using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.Backends.Perl;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Perl.PerlIr;

namespace Polypiler.Tests.Backends.Perl;

/// <summary>Tests for Perl scalar emission (#433): integers, masks, use integer, checked ops, floats, conversions and intrinsics.</summary>
[TestFixture]
public sealed class PerlScalarEmissionTests {

  [Test]
  public void FixedWidthIntegers_MaskAndSignExtendCorrectly() {
    var a = L("a", IrType.I32);
    var b = L("b", IrType.I32);
    var src = Emit(Returning(IrType.I32, [a, b], Bin(IrBinaryOp.Add, R(a), R(b))));
    Assert.That(src, Does.Contain("package M;"));
    Assert.That(src, Does.Contain("use strict;"));
    Assert.That(src, Does.Contain("use warnings;"));
    Assert.That(src, Does.Contain("use integer;"));
    Assert.That(src, Does.Contain("0xFFFFFFFF"));

    var u32a = L("a", IrType.U32);
    var u32b = L("b", IrType.U32);
    var u32Src = Emit(Returning(IrType.U32, [u32a, u32b], Bin(IrBinaryOp.Add, R(u32a), R(u32b))));
    Assert.That(u32Src, Does.Contain("& 0xFFFFFFFF"));

    // Narrow conversions wrap to 8/16 bits.
    Assert.That(Emit(Returning(IrType.U8, [a], new IrConvert(IrType.U8, R(a)))), Does.Contain("& 0xFF"));
    Assert.That(Emit(Returning(IrType.I8, [a], new IrConvert(IrType.I8, R(a)))), Does.Contain("0x80"));
    Assert.That(Emit(Returning(IrType.I16, [a], new IrConvert(IrType.I16, R(a)))), Does.Contain("0x8000"));
    Assert.That(Emit(Returning(IrType.U16, [a], new IrConvert(IrType.U16, R(a)))), Does.Contain("& 0xFFFF"));
  }

  [Test]
  public void SignedAndUnsigned64Bit_DivisionRemainderShiftsAndBitwise() {
    var x = L("x", IrType.I64);
    var y = L("y", IrType.I64);
    var d = L("d", IrType.I64);
    var r = L("r", IrType.I64);
    var s = L("s", IrType.I64);
    var l = L("l", IrType.I64);
    var fn = Function("ops64", IrType.I64, [x, y],
      new IrDeclare(d, Bin(IrBinaryOp.Divide, R(x), R(y))),
      new IrDeclare(r, Bin(IrBinaryOp.Remainder, R(x), R(y))),
      new IrDeclare(s, Bin(IrBinaryOp.ShiftRight, R(x), I(3))),
      new IrDeclare(l, Bin(IrBinaryOp.ShiftLeft, R(x), I(5))),
      new IrReturn(Bin(IrBinaryOp.Add, R(d), Bin(IrBinaryOp.Or, R(r), Bin(IrBinaryOp.And, R(s), R(l))))));
    var src = Emit(new IrModule("Ops64", [fn]));
    Assert.That(src, Does.Contain("use integer;"));
    Assert.That(src, Does.Contain("Overflow"));

    var ux = L("x", IrType.U64);
    var uy = L("y", IrType.U64);
    var ud = L("d", IrType.U64);
    var ur = L("r", IrType.U64);
    var us = L("s", IrType.U64);
    var um = L("m", IrType.U64);
    var ufn = Function("uops64", IrType.U64, [ux, uy],
      new IrDeclare(ud, Bin(IrBinaryOp.Divide, R(ux), R(uy))),
      new IrDeclare(ur, Bin(IrBinaryOp.Remainder, R(ux), R(uy))),
      new IrDeclare(us, Bin(IrBinaryOp.ShiftRight, R(ux), I(4))),
      new IrDeclare(um, Bin(IrBinaryOp.Multiply, R(ux), R(uy))),
      new IrReturn(Bin(IrBinaryOp.Add, R(ud), Bin(IrBinaryOp.Add, R(ur), Bin(IrBinaryOp.Xor, R(us), R(um))))));
    var usrc = Emit(new IrModule("UOps64", [ufn]));
    Assert.That(usrc, Does.Contain("18446744073709551615"));
    Assert.That(usrc, Does.Contain("$_l % $_r"));
  }

  [Test]
  public void CheckedArithmetic_EmitsOverflowChecks() {
    var a = L("a", IrType.I32);
    var b = L("b", IrType.I32);
    var s = L("s", IrType.I32);
    var d = L("d", IrType.I32);
    var p = L("p", IrType.I32);
    var n = L("n", IrType.I32);
    var fn = Function("chk", IrType.I32, [a, b],
      new IrDeclare(s, Bin(IrBinaryOp.AddChecked, R(a), R(b))),
      new IrDeclare(d, Bin(IrBinaryOp.SubtractChecked, R(a), R(b))),
      new IrDeclare(p, Bin(IrBinaryOp.MultiplyChecked, R(a), R(b))),
      new IrDeclare(n, new IrUnary(IrUnaryOp.NegateChecked, R(a))),
      new IrReturn(Bin(IrBinaryOp.Add, R(s), Bin(IrBinaryOp.Add, R(d), Bin(IrBinaryOp.Add, R(p), R(n))))));
    var src = Emit(new IrModule("Chk", [fn]));
    Assert.That(src, Does.Contain("Overflow"));

    var ua = L("a", IrType.U32);
    var ub = L("b", IrType.U32);
    var us = L("s", IrType.U32);
    var ud = L("d", IrType.U32);
    var up = L("p", IrType.U32);
    var ufn = Function("uchk", IrType.U32, [ua, ub],
      new IrDeclare(us, Bin(IrBinaryOp.AddChecked, R(ua), R(ub))),
      new IrDeclare(ud, Bin(IrBinaryOp.SubtractChecked, R(ua), R(ub))),
      new IrDeclare(up, Bin(IrBinaryOp.MultiplyChecked, R(ua), R(ub))),
      new IrReturn(Bin(IrBinaryOp.Add, R(us), Bin(IrBinaryOp.Add, R(ud), R(up)))));
    var usrc = Emit(new IrModule("UChk", [ufn]));
    Assert.That(usrc, Does.Contain("Overflow"));
  }

  [Test]
  public void FloatingPoint_F32Rounding_InfNaN_AndDivRemHelpers() {
    var a = L("a", IrType.F64);
    var b = L("b", IrType.F64);
    var q = L("q", IrType.F64);
    var r = L("r", IrType.F64);
    var fn = Function("fops", IrType.F64, [a, b],
      new IrDeclare(q, Bin(IrBinaryOp.Divide, R(a), R(b))),
      new IrDeclare(r, Bin(IrBinaryOp.Remainder, R(a), R(b))),
      new IrDeclare(L("inf", IrType.F64), F64(double.PositiveInfinity)),
      new IrDeclare(L("ninf", IrType.F64), F64(double.NegativeInfinity)),
      new IrDeclare(L("nan", IrType.F64), F64(double.NaN)),
      new IrDeclare(L("nz", IrType.F64), F64(-0.0)),
      new IrReturn(Bin(IrBinaryOp.Add, R(q), R(r))));
    var src = Emit(new IrModule("FOps", [fn]));
    Assert.That(src, Does.Contain("_ply_fdiv_f64"));
    Assert.That(src, Does.Contain("_ply_frem_f64"));
    Assert.That(src, Does.Contain("9**9**9"));
    Assert.That(src, Does.Contain("unpack('d', pack('Q',"));

    var fa = L("a", IrType.F32);
    var fb = L("b", IrType.F32);
    var f32Src = Emit(Returning(IrType.F32, [fa, fb], Bin(IrBinaryOp.Add, R(fa), R(fb))));
    Assert.That(f32Src, Does.Contain("_ply_round_f32"));
    Assert.That(f32Src, Does.Contain("unpack('f', pack('f',"));
  }

  [Test]
  public void Conversions_Ternary_AndBool() {
    var x = L("x", IrType.F64);
    var big = L("big", IrType.I64);
    var flag = L("flag", IrType.Bool);
    var i = L("i", IrType.I32);
    var ic = L("ic", IrType.I32);
    var f = L("f", IrType.F32);
    var notf = L("notf", IrType.Bool);
    var cond = L("cond", IrType.I32);
    var fn = Function("conv", IrType.I32, [x, big, flag],
      new IrDeclare(i, new IrConvert(IrType.I32, R(x), IrConversion.Saturate)),
      new IrDeclare(ic, new IrConvert(IrType.I32, R(x), IrConversion.Trap)),
      new IrDeclare(f, new IrConvert(IrType.F32, R(big))),
      new IrDeclare(notf, new IrUnary(IrUnaryOp.LogicalNot, R(flag))),
      new IrDeclare(cond, new IrConditional(R(notf), R(i), R(ic))),
      new IrReturn(R(cond)));
    var src = Emit(new IrModule("Conv", [fn]));
    Assert.That(src, Does.Contain("_ply_int_to_f32"));
    Assert.That(src, Does.Contain("Overflow"));
  }

  [Test]
  public void UnaryOps_Step_AndLegalizedIntrinsics() {
    var x = L("x", IrType.I32);
    var u = L("u", IrType.U32);
    var n = L("n", IrType.I32);
    var bn = L("bn", IrType.U32);
    var pop = L("pop", IrType.I32);
    var rot = L("rot", IrType.U32);
    var abs = L("abs", IrType.I32);
    var min = L("min", IrType.I32);
    var st = L("st", IrType.I32);
    var fn = Function("bits", IrType.U32, [x, u],
      new IrDeclare(n, new IrUnary(IrUnaryOp.Negate, R(x))),
      new IrDeclare(bn, new IrUnary(IrUnaryOp.BitNot, R(u))),
      new IrDeclare(pop, new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [R(u)])),
      new IrDeclare(rot, new IrIntrinsic(IrIntrinsicOp.RotateLeft, IrType.U32, [R(u), I(5)])),
      new IrDeclare(abs, new IrIntrinsic(IrIntrinsicOp.Abs, IrType.I32, [R(n)])),
      new IrDeclare(min, new IrIntrinsic(IrIntrinsicOp.Min, IrType.I32, [R(abs), R(pop)])),
      new IrDeclare(st, I(0)),
      new IrExprStmt(new IrStep(R(st), true, true, false)),
      new IrExprStmt(new IrStep(R(st), false, false, false)),
      new IrReturn(Bin(IrBinaryOp.Xor, R(bn), R(rot))));
    var src = Legalized(new IrModule("Bits", [fn]));
    Assert.That(src, Does.Contain("sub bits"));
    Assert.That(src, Does.Contain("use strict;"));
    Assert.That(PerlEmitter.Writes(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.I32, [R(x)])), Is.True);
  }

  [Test]
  public void UnsupportedTypeAndOperation_ReportPLY1750AndPLY1751() {
    var bi = L("bi", IrType.BigInt);
    Assert.That(Problems(Returning(IrType.BigInt, [bi], R(bi))), Has.Some.Contains("PLY1750"));

    var a = L("a", IrType.String);
    var badIntrinsic = Returning(IrType.I32, [a], new IrIntrinsic(IrIntrinsicOp.StrCodePointAt, IrType.I32, [R(a), I(0)]));
    Assert.That(Problems(badIntrinsic), Has.Some.Contains("PLY1751"));
  }

}
