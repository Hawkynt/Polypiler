using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.Backends.Msil;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Msil.MsilTestHarness;

namespace Polypiler.Tests.Backends.Msil;

/// <summary>
/// Live execution tests that emit verifiable .NET PE assemblies, load them into a collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, and verify exact runtime parity with <see cref="IrInterpreter"/>.
/// </summary>
[TestFixture]
public sealed class MsilExecutionTests {

  [Test]
  public void ArithmeticAndBitwise_SignedUnsignedAndFloats() {
    var a32 = L("a", IrType.I32);
    var b32 = L("b", IrType.I32);
    var u32a = L("a", IrType.U32);
    var u32b = L("b", IrType.U32);
    var a64 = L("a", IrType.I64);
    var b64 = L("b", IrType.I64);
    var u64a = L("a", IrType.U64);
    var u64b = L("b", IrType.U64);
    var f32a = L("a", IrType.F32);
    var f32b = L("b", IrType.F32);
    var f64a = L("a", IrType.F64);
    var f64b = L("b", IrType.F64);
    var s = L("s", IrType.I32);
    var flag = L("flag", IrType.Bool);

    var mod = new IrModule("Arith", [
      Function("Add", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.Add, R(a32), R(b32)))),
      Function("Sub", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.Subtract, R(a32), R(b32)))),
      Function("Mul", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.Multiply, R(a32), R(b32)))),
      Function("Div", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.Divide, R(a32), R(b32)))),
      Function("Rem", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.Remainder, R(a32), R(b32)))),
      Function("UDiv", IrType.U32, [u32a, u32b], new IrReturn(Bin(IrBinaryOp.Divide, R(u32a), R(u32b)))),
      Function("URem", IrType.U32, [u32a, u32b], new IrReturn(Bin(IrBinaryOp.Remainder, R(u32a), R(u32b)))),
      Function("Bit", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.Xor, Bin(IrBinaryOp.Or, Bin(IrBinaryOp.And, R(a32), R(b32)), new IrUnary(IrUnaryOp.BitNot, R(a32))), R(b32)))),
      Function("Shl32", IrType.I32, [a32, s], new IrReturn(Bin(IrBinaryOp.ShiftLeft, R(a32), R(s)))),
      Function("Shr32", IrType.I32, [a32, s], new IrReturn(Bin(IrBinaryOp.ShiftRight, R(a32), R(s)))),
      Function("Shru32", IrType.I32, [a32, s], new IrReturn(Bin(IrBinaryOp.ShiftRightLogical, R(a32), R(s)))),
      Function("UShr32", IrType.U32, [u32a, s], new IrReturn(Bin(IrBinaryOp.ShiftRight, R(u32a), R(s)))),
      Function("Shl64", IrType.I64, [a64, s], new IrReturn(Bin(IrBinaryOp.ShiftLeft, R(a64), R(s)))),
      Function("Shr64", IrType.I64, [a64, s], new IrReturn(Bin(IrBinaryOp.ShiftRight, R(a64), R(s)))),
      Function("Shru64", IrType.U64, [u64a, s], new IrReturn(Bin(IrBinaryOp.ShiftRightLogical, R(u64a), R(s)))),
      Function("Neg", IrType.I32, [a32], new IrReturn(new IrUnary(IrUnaryOp.Negate, R(a32)))),
      Function("NotBool", IrType.Bool, [flag], new IrReturn(new IrUnary(IrUnaryOp.LogicalNot, R(flag)))),
      Function("F32Ops", IrType.F32, [f32a, f32b], new IrReturn(Bin(IrBinaryOp.Remainder, Bin(IrBinaryOp.Divide, Bin(IrBinaryOp.Multiply, Bin(IrBinaryOp.Subtract, Bin(IrBinaryOp.Add, R(f32a), R(f32b)), F32(1.5f)), F32(2.0f)), F32(3.0f)), F32(5.0f)))),
      Function("F64Ops", IrType.F64, [f64a, f64b], new IrReturn(Bin(IrBinaryOp.Divide, Bin(IrBinaryOp.Add, R(f64a), R(f64b)), F64(2.0)))),
      Function("U64Add", IrType.U64, [u64a, u64b], new IrReturn(Bin(IrBinaryOp.Add, R(u64a), R(u64b)))),
      Function("I64Neg", IrType.I64, [a64], new IrReturn(new IrUnary(IrUnaryOp.Negate, R(a64))))
    ]);

    AssertMatchesInterpreter(mod, "Add", int.MaxValue, 1);
    AssertMatchesInterpreter(mod, "Sub", 10, 25);
    AssertMatchesInterpreter(mod, "Mul", -7, 6);
    AssertMatchesInterpreter(mod, "Div", -20, 3);
    AssertMatchesInterpreter(mod, "Rem", -20, 3);
    AssertMatchesInterpreter(mod, "UDiv", 0xFFFF_FFF0u, 3u);
    AssertMatchesInterpreter(mod, "URem", 0xFFFF_FFF0u, 3u);
    AssertMatchesInterpreter(mod, "Bit", 0x0F0F, 0x3333);
    AssertMatchesInterpreter(mod, "Shl32", 1, 35);
    AssertMatchesInterpreter(mod, "Shr32", -16, 2);
    AssertMatchesInterpreter(mod, "Shru32", -16, 2);
    AssertMatchesInterpreter(mod, "UShr32", 0x8000_0000u, 4);
    AssertMatchesInterpreter(mod, "Shl64", 1L, 67);
    AssertMatchesInterpreter(mod, "Shr64", -64L, 2);
    AssertMatchesInterpreter(mod, "Shru64", 0x8000_0000_0000_0000UL, 4);
    AssertMatchesInterpreter(mod, "Neg", int.MinValue);
    AssertMatchesInterpreter(mod, "NotBool", true);
    AssertMatchesInterpreter(mod, "NotBool", false);
    AssertMatchesInterpreter(mod, "F32Ops", 10.5f, 4.25f);
    AssertMatchesInterpreter(mod, "F64Ops", 3.5, 4.5);
    AssertMatchesInterpreter(mod, "U64Add", ulong.MaxValue, 5UL);
    AssertMatchesInterpreter(mod, "I64Neg", 1234567890123L);
  }

  [Test]
  public void CheckedArithmetic_HappyAndOverflow() {
    var a = L("a", IrType.I32);
    var b = L("b", IrType.I32);
    var ua = L("a", IrType.U32);
    var ub = L("b", IrType.U32);
    var l = L("x", IrType.I64);

    var mod = new IrModule("Checked", [
      Function("AddS", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.AddChecked, R(a), R(b)))),
      Function("SubS", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.SubtractChecked, R(a), R(b)))),
      Function("MulS", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.MultiplyChecked, R(a), R(b)))),
      Function("AddU", IrType.U32, [ua, ub], new IrReturn(Bin(IrBinaryOp.AddChecked, R(ua), R(ub)))),
      Function("SubU", IrType.U32, [ua, ub], new IrReturn(Bin(IrBinaryOp.SubtractChecked, R(ua), R(ub)))),
      Function("MulU", IrType.U32, [ua, ub], new IrReturn(Bin(IrBinaryOp.MultiplyChecked, R(ua), R(ub)))),
      Function("NegS32", IrType.I32, [a], new IrReturn(new IrUnary(IrUnaryOp.NegateChecked, R(a)))),
      Function("NegS64", IrType.I64, [l], new IrReturn(new IrUnary(IrUnaryOp.NegateChecked, R(l))))
    ]);

    Assert.That(Invoke(mod, "AddS", 10, 20), Is.EqualTo(30));
    Assert.That(() => Invoke(mod, "AddS", int.MaxValue, 1), Throws.TypeOf<OverflowException>());
    Assert.That(Invoke(mod, "SubS", 10, 3), Is.EqualTo(7));
    Assert.That(() => Invoke(mod, "SubS", int.MinValue, 1), Throws.TypeOf<OverflowException>());
    Assert.That(Invoke(mod, "MulS", 10, 3), Is.EqualTo(30));
    Assert.That(() => Invoke(mod, "MulS", int.MaxValue, 2), Throws.TypeOf<OverflowException>());
    Assert.That(Invoke(mod, "AddU", 10u, 20u), Is.EqualTo(30u));
    Assert.That(() => Invoke(mod, "AddU", uint.MaxValue, 1u), Throws.TypeOf<OverflowException>());
    Assert.That(Invoke(mod, "SubU", 20u, 10u), Is.EqualTo(10u));
    Assert.That(() => Invoke(mod, "SubU", 0u, 1u), Throws.TypeOf<OverflowException>());
    Assert.That(Invoke(mod, "MulU", 10u, 20u), Is.EqualTo(200u));
    Assert.That(() => Invoke(mod, "MulU", uint.MaxValue, 2u), Throws.TypeOf<OverflowException>());
    Assert.That(Invoke(mod, "NegS32", 5), Is.EqualTo(-5));
    Assert.That(() => Invoke(mod, "NegS32", int.MinValue), Throws.TypeOf<OverflowException>());
    Assert.That(Invoke(mod, "NegS64", 5L), Is.EqualTo(-5L));
    Assert.That(() => Invoke(mod, "NegS64", long.MinValue), Throws.TypeOf<OverflowException>());
  }

  [Test]
  public void ComparisonsAndLogicalShortCircuit_IncludingNaN() {
    var a = L("a", IrType.I32);
    var b = L("b", IrType.I32);
    var ua = L("a", IrType.U32);
    var ub = L("b", IrType.U32);
    var fa = L("a", IrType.F64);
    var fb = L("b", IrType.F64);
    var g = new IrGlobal("Counter", IrType.I32, I(0));

    var bump = Function("Bump", IrType.Bool, [],
      new IrGlobalAssign(g, Bin(IrBinaryOp.Add, new IrGlobalRef(g), I(1))),
      new IrReturn(B(true)));

    var mod = new IrModule("Cmp", [
      bump,
      Function("Eq", IrType.Bool, [a, b], new IrReturn(Bin(IrBinaryOp.Equal, R(a), R(b)))),
      Function("Ne", IrType.Bool, [a, b], new IrReturn(Bin(IrBinaryOp.NotEqual, R(a), R(b)))),
      Function("Lt", IrType.Bool, [a, b], new IrReturn(Bin(IrBinaryOp.Less, R(a), R(b)))),
      Function("Le", IrType.Bool, [a, b], new IrReturn(Bin(IrBinaryOp.LessOrEqual, R(a), R(b)))),
      Function("Gt", IrType.Bool, [a, b], new IrReturn(Bin(IrBinaryOp.Greater, R(a), R(b)))),
      Function("Ge", IrType.Bool, [a, b], new IrReturn(Bin(IrBinaryOp.GreaterOrEqual, R(a), R(b)))),
      Function("ULt", IrType.Bool, [ua, ub], new IrReturn(Bin(IrBinaryOp.Less, R(ua), R(ub)))),
      Function("UGt", IrType.Bool, [ua, ub], new IrReturn(Bin(IrBinaryOp.Greater, R(ua), R(ub)))),
      Function("ULe", IrType.Bool, [ua, ub], new IrReturn(Bin(IrBinaryOp.LessOrEqual, R(ua), R(ub)))),
      Function("UGe", IrType.Bool, [ua, ub], new IrReturn(Bin(IrBinaryOp.GreaterOrEqual, R(ua), R(ub)))),
      Function("FLe", IrType.Bool, [fa, fb], new IrReturn(Bin(IrBinaryOp.LessOrEqual, R(fa), R(fb)))),
      Function("FGe", IrType.Bool, [fa, fb], new IrReturn(Bin(IrBinaryOp.GreaterOrEqual, R(fa), R(fb)))),
      Function("ShortAnd", IrType.I32, [a],
        new IrExprStmt(Bin(IrBinaryOp.LogicalAnd, Bin(IrBinaryOp.Greater, R(a), I(0)), new IrCall("Bump", IrType.Bool, []))),
        new IrReturn(new IrGlobalRef(g))),
      Function("ShortOr", IrType.I32, [a],
        new IrExprStmt(Bin(IrBinaryOp.LogicalOr, Bin(IrBinaryOp.Greater, R(a), I(0)), new IrCall("Bump", IrType.Bool, []))),
        new IrReturn(new IrGlobalRef(g)))
    ]) { Globals = [g] };

    AssertMatchesInterpreter(mod, "Eq", 5, 5);
    AssertMatchesInterpreter(mod, "Ne", 5, 6);
    AssertMatchesInterpreter(mod, "Lt", -1, 1);
    AssertMatchesInterpreter(mod, "Le", 5, 5);
    AssertMatchesInterpreter(mod, "Gt", 6, 5);
    AssertMatchesInterpreter(mod, "Ge", 5, 5);
    AssertMatchesInterpreter(mod, "ULt", 1u, 0xFFFF_FFFFu);
    AssertMatchesInterpreter(mod, "UGt", 0xFFFF_FFFFu, 1u);
    AssertMatchesInterpreter(mod, "ULe", 0xFFFF_FFFFu, 1u);
    AssertMatchesInterpreter(mod, "UGe", 0xFFFF_FFFFu, 1u);
    AssertMatchesInterpreter(mod, "FLe", double.NaN, 1.0);
    AssertMatchesInterpreter(mod, "FGe", double.NaN, 1.0);
    AssertMatchesInterpreter(mod, "FLe", 1.0, 2.0);
    AssertMatchesInterpreter(mod, "FGe", 2.0, 1.0);
    Assert.That(Invoke(mod, "ShortAnd", 0), Is.EqualTo(0));
    Assert.That(Invoke(mod, "ShortAnd", 1), Is.EqualTo(1));
    Assert.That(Invoke(mod, "ShortOr", 1), Is.EqualTo(0));
    Assert.That(Invoke(mod, "ShortOr", 0), Is.EqualTo(1));
  }

  [Test]
  public void Conversions_WrapTrapAndSaturate() {
    var i32 = L("x", IrType.I32);
    var u32 = L("x", IrType.U32);
    var i64 = L("x", IrType.I64);
    var u64 = L("x", IrType.U64);
    var f32 = L("x", IrType.F32);
    var f64 = L("x", IrType.F64);

    var mod = new IrModule("Conv", [
      // Wrapping
      Function("I32ToI8", IrType.I8, [i32], new IrReturn(new IrConvert(IrType.I8, R(i32), IrConversion.Wrap))),
      Function("I32ToU8", IrType.U8, [i32], new IrReturn(new IrConvert(IrType.U8, R(i32), IrConversion.Wrap))),
      Function("I32ToI16", IrType.I16, [i32], new IrReturn(new IrConvert(IrType.I16, R(i32), IrConversion.Wrap))),
      Function("I32ToU16", IrType.U16, [i32], new IrReturn(new IrConvert(IrType.U16, R(i32), IrConversion.Wrap))),
      Function("I64ToI32", IrType.I32, [i64], new IrReturn(new IrConvert(IrType.I32, R(i64), IrConversion.Wrap))),
      Function("I64ToU32", IrType.U32, [i64], new IrReturn(new IrConvert(IrType.U32, R(i64), IrConversion.Wrap))),
      Function("I32ToU64", IrType.U64, [i32], new IrReturn(new IrConvert(IrType.U64, R(i32), IrConversion.Wrap))),
      Function("U32ToI64", IrType.I64, [u32], new IrReturn(new IrConvert(IrType.I64, R(u32), IrConversion.Wrap))),
      Function("U64ToF64", IrType.F64, [u64], new IrReturn(new IrConvert(IrType.F64, R(u64), IrConversion.Wrap))),
      Function("I32ToF32", IrType.F32, [i32], new IrReturn(new IrConvert(IrType.F32, R(i32), IrConversion.Wrap))),
      // Trapping (signed & unsigned sources -> all 8 widths)
      Function("TrapI8S", IrType.I8, [i32], new IrReturn(new IrConvert(IrType.I8, R(i32), IrConversion.Trap))),
      Function("TrapI8U", IrType.I8, [u32], new IrReturn(new IrConvert(IrType.I8, R(u32), IrConversion.Trap))),
      Function("TrapU8S", IrType.U8, [i32], new IrReturn(new IrConvert(IrType.U8, R(i32), IrConversion.Trap))),
      Function("TrapU8U", IrType.U8, [u32], new IrReturn(new IrConvert(IrType.U8, R(u32), IrConversion.Trap))),
      Function("TrapI16S", IrType.I16, [i32], new IrReturn(new IrConvert(IrType.I16, R(i32), IrConversion.Trap))),
      Function("TrapI16U", IrType.I16, [u32], new IrReturn(new IrConvert(IrType.I16, R(u32), IrConversion.Trap))),
      Function("TrapU16S", IrType.U16, [i32], new IrReturn(new IrConvert(IrType.U16, R(i32), IrConversion.Trap))),
      Function("TrapU16U", IrType.U16, [u32], new IrReturn(new IrConvert(IrType.U16, R(u32), IrConversion.Trap))),
      Function("TrapI32S", IrType.I32, [i64], new IrReturn(new IrConvert(IrType.I32, R(i64), IrConversion.Trap))),
      Function("TrapI32U", IrType.I32, [u64], new IrReturn(new IrConvert(IrType.I32, R(u64), IrConversion.Trap))),
      Function("TrapU32S", IrType.U32, [i64], new IrReturn(new IrConvert(IrType.U32, R(i64), IrConversion.Trap))),
      Function("TrapU32U", IrType.U32, [u64], new IrReturn(new IrConvert(IrType.U32, R(u64), IrConversion.Trap))),
      Function("TrapI64U", IrType.I64, [u64], new IrReturn(new IrConvert(IrType.I64, R(u64), IrConversion.Trap))),
      Function("TrapI64F", IrType.I64, [f64], new IrReturn(new IrConvert(IrType.I64, R(f64), IrConversion.Trap))),
      Function("TrapU64S", IrType.U64, [i64], new IrReturn(new IrConvert(IrType.U64, R(i64), IrConversion.Trap))),
      Function("TrapU64F", IrType.U64, [f64], new IrReturn(new IrConvert(IrType.U64, R(f64), IrConversion.Trap))),
      // Saturating float -> int (all 8 widths + f32 source)
      Function("SatI8", IrType.I8, [f64], new IrReturn(new IrConvert(IrType.I8, R(f64), IrConversion.Saturate))),
      Function("SatU8", IrType.U8, [f64], new IrReturn(new IrConvert(IrType.U8, R(f64), IrConversion.Saturate))),
      Function("SatI16", IrType.I16, [f64], new IrReturn(new IrConvert(IrType.I16, R(f64), IrConversion.Saturate))),
      Function("SatU16", IrType.U16, [f64], new IrReturn(new IrConvert(IrType.U16, R(f64), IrConversion.Saturate))),
      Function("SatI32", IrType.I32, [f32], new IrReturn(new IrConvert(IrType.I32, R(f32), IrConversion.Saturate))),
      Function("SatU32", IrType.U32, [f64], new IrReturn(new IrConvert(IrType.U32, R(f64), IrConversion.Saturate))),
      Function("SatI64", IrType.I64, [f64], new IrReturn(new IrConvert(IrType.I64, R(f64), IrConversion.Saturate))),
      Function("SatU64", IrType.U64, [f64], new IrReturn(new IrConvert(IrType.U64, R(f64), IrConversion.Saturate)))
    ]);

    AssertMatchesInterpreter(mod, "I32ToI8", 255);
    AssertMatchesInterpreter(mod, "I32ToU8", -1);
    AssertMatchesInterpreter(mod, "I32ToI16", 70000);
    AssertMatchesInterpreter(mod, "I32ToU16", -2);
    AssertMatchesInterpreter(mod, "I64ToI32", 0x1_0000_0005L);
    AssertMatchesInterpreter(mod, "I64ToU32", -1L);
    AssertMatchesInterpreter(mod, "I32ToU64", -1);
    AssertMatchesInterpreter(mod, "U32ToI64", 0xFFFF_FFFFu);
    AssertMatchesInterpreter(mod, "U64ToF64", ulong.MaxValue);
    AssertMatchesInterpreter(mod, "I32ToF32", 42);

    Assert.That(Invoke(mod, "TrapI8S", 100), Is.EqualTo((sbyte)100));
    Assert.That(() => Invoke(mod, "TrapI8S", 200), Throws.TypeOf<OverflowException>());
    Assert.That(Invoke(mod, "TrapI8U", 100u), Is.EqualTo((sbyte)100));
    Assert.That(Invoke(mod, "TrapU8S", 200), Is.EqualTo((byte)200));
    Assert.That(Invoke(mod, "TrapU8U", 200u), Is.EqualTo((byte)200));
    Assert.That(Invoke(mod, "TrapI16S", 1000), Is.EqualTo((short)1000));
    Assert.That(Invoke(mod, "TrapI16U", 1000u), Is.EqualTo((short)1000));
    Assert.That(Invoke(mod, "TrapU16S", 1000), Is.EqualTo((ushort)1000));
    Assert.That(Invoke(mod, "TrapU16U", 1000u), Is.EqualTo((ushort)1000));
    Assert.That(Invoke(mod, "TrapI32S", 1000L), Is.EqualTo(1000));
    Assert.That(Invoke(mod, "TrapI32U", 1000UL), Is.EqualTo(1000));
    Assert.That(Invoke(mod, "TrapU32S", 1000L), Is.EqualTo(1000u));
    Assert.That(Invoke(mod, "TrapU32U", 1000UL), Is.EqualTo(1000u));
    Assert.That(Invoke(mod, "TrapI64U", 1000UL), Is.EqualTo(1000L));
    Assert.That(Invoke(mod, "TrapI64F", 100.5), Is.EqualTo(100L));
    Assert.That(() => Invoke(mod, "TrapI64F", double.NaN), Throws.TypeOf<OverflowException>());
    Assert.That(Invoke(mod, "TrapU64S", 100L), Is.EqualTo(100UL));
    Assert.That(Invoke(mod, "TrapU64F", 100.5), Is.EqualTo(100UL));

    AssertMatchesInterpreter(mod, "SatI8", 500.0);
    AssertMatchesInterpreter(mod, "SatI8", -500.0);
    AssertMatchesInterpreter(mod, "SatI8", double.NaN);
    AssertMatchesInterpreter(mod, "SatI8", 42.9);
    AssertMatchesInterpreter(mod, "SatU8", -10.0);
    AssertMatchesInterpreter(mod, "SatU8", 123.4);
    AssertMatchesInterpreter(mod, "SatI16", 1234.5);
    AssertMatchesInterpreter(mod, "SatU16", 1234.5);
    AssertMatchesInterpreter(mod, "SatI32", 1e30f);
    AssertMatchesInterpreter(mod, "SatI32", -1e30f);
    AssertMatchesInterpreter(mod, "SatI32", float.NaN);
    AssertMatchesInterpreter(mod, "SatI32", 42.7f);
    AssertMatchesInterpreter(mod, "SatU32", 42.7);
    AssertMatchesInterpreter(mod, "SatI64", 1e30);
    AssertMatchesInterpreter(mod, "SatI64", -1e30);
    AssertMatchesInterpreter(mod, "SatI64", 42.7);
    AssertMatchesInterpreter(mod, "SatU64", 1e30);
    AssertMatchesInterpreter(mod, "SatU64", -5.0);
    AssertMatchesInterpreter(mod, "SatU64", 42.7);
  }

  [Test]
  public void ControlFlow_LoopsSwitchTernaryStepsAndBind() {
    var n = L("n", IrType.I32);
    var i = L("i", IrType.I32);
    var sum = L("sum", IrType.I32);
    var u = L("u", IrType.U8);
    var sb = L("sb", IrType.I8);
    var sh = L("sh", IrType.I16);
    var ush = L("ush", IrType.U16);
    var lg = L("lg", IrType.I64);
    var ulg = L("ulg", IrType.U64);
    var fl = L("fl", IrType.F32);
    var db = L("db", IrType.F64);
    var arr = L("arr", IrType.Array(IrType.I32));

    var mod = new IrModule("Flow", [
      // If without else + while + break + continue
      Function("SumOdd", IrType.I32, [n],
        new IrDeclare(i, I(0)),
        new IrDeclare(sum, I(0)),
        new IrWhile(Bin(IrBinaryOp.Less, R(i), R(n)), Block(
          new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))),
          new IrIf(Bin(IrBinaryOp.Equal, Bin(IrBinaryOp.Remainder, R(i), I(2)), I(0)), Block(new IrContinue()), null),
          new IrIf(Bin(IrBinaryOp.Greater, R(i), I(9)), Block(new IrBreak()), null),
          new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(i)))
        )),
        new IrReturn(R(sum))),
      // DoWhile + For + Switch with continue inside switch targeting outer loop
      Function("ForSwitch", IrType.I32, [n],
        new IrDeclare(sum, I(0)),
        new IrDeclare(i, I(0)),
        new IrFor(
          Bin(IrBinaryOp.Less, R(i), R(n)),
          Block(
            new IrSwitch(R(i), [
              new IrSwitchSection([1], false, Block(new IrContinue())),
              new IrSwitchSection([2, 3], false, Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), I(10))), new IrBreak())),
              new IrSwitchSection([], true, Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(i)))))
            ])
          ),
          Block(new IrExprStmt(new IrStep(R(i), Increment: true, Postfix: true, Trap: false)))
        ),
        new IrReturn(R(sum))),
      // Switch without default + DoWhile
      Function("DoWhileSwitch", IrType.I32, [n],
        new IrDeclare(sum, I(0)),
        new IrDoWhile(Block(
          new IrSwitch(R(n), [
            new IrSwitchSection([1], false, Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), I(100)))))
          ]),
          new IrAssign(n, Bin(IrBinaryOp.Subtract, R(n), I(1)))
        ), Bin(IrBinaryOp.Greater, R(n), I(0))),
        new IrReturn(R(sum))),
      // Ternary + IrBind + IrDeclare without initializer
      Function("TernaryBind", IrType.I32, [n],
        new IrDeclare(sum, null),
        new IrReturn(new IrConditional(
          Bin(IrBinaryOp.Greater, new IrBind(sum, Bin(IrBinaryOp.Multiply, R(n), I(2))), I(10)),
          R(sum),
          I(0)))),
      // Steps: prefix/postfix, array element, narrow/wide/float, checked/unchecked
      Function("Steps", IrType.I32, [arr],
        new IrDeclare(u, C(IrType.U8, 5)),
        new IrDeclare(sb, C(IrType.I8, 5)),
        new IrDeclare(sh, C(IrType.I16, 5)),
        new IrDeclare(ush, C(IrType.U16, 5)),
        new IrDeclare(lg, I64(10)),
        new IrDeclare(ulg, U64(10)),
        new IrDeclare(fl, F32(1.0f)),
        new IrDeclare(db, F64(2.0)),
        new IrExprStmt(new IrStep(R(u), true, false, false)),
        new IrExprStmt(new IrStep(R(u), false, true, true)),
        new IrExprStmt(new IrStep(R(sb), true, false, true)),
        new IrExprStmt(new IrStep(R(sb), false, false, false)),
        new IrExprStmt(new IrStep(R(sh), true, false, true)),
        new IrExprStmt(new IrStep(R(sh), false, false, false)),
        new IrExprStmt(new IrStep(R(ush), true, false, true)),
        new IrExprStmt(new IrStep(R(ush), false, false, false)),
        new IrExprStmt(new IrStep(R(lg), true, false, true)),
        new IrExprStmt(new IrStep(R(ulg), true, false, true)),
        new IrExprStmt(new IrStep(R(fl), true, false, false)),
        new IrExprStmt(new IrStep(R(db), false, false, false)),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrStep(new IrArrayLoad(R(arr), I(0)), Increment: true, Postfix: true, Trap: false),
          new IrStep(new IrArrayLoad(R(arr), I(0)), Increment: true, Postfix: false, Trap: true)))),
      // Void function with implicit return (no IrReturn)
      Function("Touch", IrType.Void, [arr],
        new IrStore(R(arr), I(0), I(99))),
      // Non-void function whose IrIf covers both branches (tests fallthrough default epilogue)
      Function("Choose", IrType.I32, [n],
        new IrIf(Bin(IrBinaryOp.Greater, R(n), I(0)), Block(new IrReturn(I(1))), Block(new IrReturn(I(-1)))))
    ]);

    AssertMatchesInterpreter(mod, "SumOdd", 15);
    AssertMatchesInterpreter(mod, "ForSwitch", 5);
    AssertMatchesInterpreter(mod, "DoWhileSwitch", 3);
    AssertMatchesInterpreter(mod, "TernaryBind", 6);
    AssertMatchesInterpreter(mod, "TernaryBind", 3);
    AssertMatchesInterpreter(mod, "Steps", new int[] { 10 });
    AssertMatchesInterpreter(mod, "Choose", 5);
    AssertMatchesInterpreter(mod, "Choose", -5);

    var target = new[] { 0 };
    Invoke(mod, "Touch", (object)target);
    Assert.That(target[0], Is.EqualTo(99));
  }

  [Test]
  public void ArraysModuleConstantsAndIntrinsics() {
    var constScalar = new IrModuleConst("Magic", IrType.I32, [42]);
    var constStr = IrModuleConst.OfStrings("Greeting", IrType.String, ["hello"]);
    var constArr = new IrModuleConst("Table", IrType.Array(IrType.U8), [10, 20, 30]);
    var constStrs = IrModuleConst.OfStrings("Words", IrType.Array(IrType.String), ["a", "b"]);

    var a = L("a", IrType.Array(IrType.I32));
    var b = L("b", IrType.Array(IrType.I32));
    var idx = L("i", IrType.I32);
    var i8Arr = L("i8a", IrType.Array(IrType.I8));
    var i16Arr = L("i16a", IrType.Array(IrType.I16));
    var u16Arr = L("u16a", IrType.Array(IrType.U16));
    var u32Arr = L("u32a", IrType.Array(IrType.U32));
    var i64Arr = L("i64a", IrType.Array(IrType.I64));
    var f32Arr = L("f32a", IrType.Array(IrType.F32));
    var f64Arr = L("f64a", IrType.Array(IrType.F64));
    var sArr = L("sa", IrType.Array(IrType.String));

    var mod = new IrModule("Arrays", [
      Function("Consts", IrType.I32, [],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConstRef(constScalar),
          Bin(IrBinaryOp.Add,
            new IrConvert(IrType.I32, new IrArrayLoad(new IrConstRef(constArr), I(1)), IrConversion.Wrap),
            Bin(IrBinaryOp.Add,
              new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrConstRef(constStr)]),
              new IrArrayLength(new IrConstRef(constStrs))))))),
      Function("ArrayOps", IrType.I32, [],
        new IrDeclare(a, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(1), I(2), I(3), I(4)])),
        new IrDeclare(b, new IrNewArray(IrType.Array(IrType.I32), I(4))),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [R(b), I(7)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [R(a), I(1), R(b), I(0), I(2)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayClear, IrType.Void, [R(a)])),
        new IrReturn(Bin(IrBinaryOp.Add, new IrArrayLoad(R(a), I(0)), Bin(IrBinaryOp.Add, new IrArrayLoad(R(b), I(0)), new IrArrayLoad(R(b), I(3)))))),
      Function("BadCopy", IrType.Void, [idx],
        new IrDeclare(a, new IrNewArray(IrType.Array(IrType.I32), I(2))),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [R(a), R(idx), R(a), I(0), I(2)]))),
      Function("AllElemTypes", IrType.I32, [],
        new IrDeclare(i8Arr, new IrNewArray(IrType.Array(IrType.I8), I(1))),
        new IrDeclare(i16Arr, new IrNewArray(IrType.Array(IrType.I16), I(1))),
        new IrDeclare(u16Arr, new IrNewArray(IrType.Array(IrType.U16), I(1))),
        new IrDeclare(u32Arr, new IrNewArray(IrType.Array(IrType.U32), I(1))),
        new IrDeclare(i64Arr, new IrNewArray(IrType.Array(IrType.I64), I(1))),
        new IrDeclare(f32Arr, new IrNewArray(IrType.Array(IrType.F32), I(1))),
        new IrDeclare(f64Arr, new IrNewArray(IrType.Array(IrType.F64), I(1))),
        new IrDeclare(sArr, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.String), [new IrStrConst("")])),
        new IrStore(R(i8Arr), I(0), C(IrType.I8, -5)),
        new IrStore(R(i16Arr), I(0), C(IrType.I16, -10)),
        new IrStore(R(u16Arr), I(0), C(IrType.U16, 20)),
        new IrStore(R(u32Arr), I(0), U32(30)),
        new IrStore(R(i64Arr), I(0), I64(40)),
        new IrStore(R(f32Arr), I(0), F32(1.5f)),
        new IrStore(R(f64Arr), I(0), F64(2.5)),
        new IrStore(R(sArr), I(0), new IrStrConst("ok")),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConvert(IrType.I32, new IrArrayLoad(R(i8Arr), I(0)), IrConversion.Wrap),
          Bin(IrBinaryOp.Add,
            new IrConvert(IrType.I32, new IrArrayLoad(R(i16Arr), I(0)), IrConversion.Wrap),
            Bin(IrBinaryOp.Add,
              new IrConvert(IrType.I32, new IrArrayLoad(R(u16Arr), I(0)), IrConversion.Wrap),
              Bin(IrBinaryOp.Add,
                new IrConvert(IrType.I32, new IrArrayLoad(R(u32Arr), I(0)), IrConversion.Wrap),
                Bin(IrBinaryOp.Add,
                  new IrConvert(IrType.I32, new IrArrayLoad(R(i64Arr), I(0)), IrConversion.Wrap),
                  Bin(IrBinaryOp.Add,
                    new IrConvert(IrType.I32, new IrArrayLoad(R(f32Arr), I(0)), IrConversion.Saturate),
                    Bin(IrBinaryOp.Add,
                      new IrConvert(IrType.I32, new IrArrayLoad(R(f64Arr), I(0)), IrConversion.Saturate),
                      new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrArrayLoad(R(sArr), I(0))]))))))))))
    ], [constScalar, constStr, constArr, constStrs]);

    AssertMatchesInterpreter(mod, "Consts");
    AssertMatchesInterpreter(mod, "ArrayOps");
    AssertMatchesInterpreter(mod, "AllElemTypes");
    Assert.That(() => Invoke(mod, "BadCopy", -1), Throws.TypeOf<IndexOutOfRangeException>());
    Assert.That(() => Invoke(mod, "BadCopy", 1), Throws.TypeOf<IndexOutOfRangeException>());
  }

  [Test]
  public void EnumsStructsGlobalsAndInitializer() {
    var color = new IrEnumType("Color", IrType.I32, [new("Red", 1), new("Green", 2), new("Blue", 4)]);
    var smallEnum = new IrEnumType("Small", IrType.U8, [new("Zero", 0), new("One", 1)]);
    var signedByteEnum = new IrEnumType("SByteEnum", IrType.I8, [new("Neg", -1)]);
    var i16Enum = new IrEnumType("I16Enum", IrType.I16, [new("A", 1)]);
    var u16Enum = new IrEnumType("U16Enum", IrType.U16, [new("A", 1)]);
    var u32Enum = new IrEnumType("U32Enum", IrType.U32, [new("A", 1)]);
    var i64Enum = new IrEnumType("I64Enum", IrType.I64, [new("A", 1)]);
    var u64Enum = new IrEnumType("U64Enum", IrType.U64, [new("A", 1)]);

    var innerX = new IrField("X", IrType.F32, false);
    var innerY = new IrField("Y", IrType.F64, false);
    var inner = new IrStructType("Inner", [innerX, innerY]);

    var outerPt = new IrField("Pt", IrType.Struct(inner), false);
    var outerTag = new IrField("Tag", IrType.I32, false);
    var outer = new IrStructType("Outer", [outerPt, outerTag]);

    var gArr = new IrGlobal("Shared", IrType.Array(IrType.I32), new IrNewArray(IrType.Array(IrType.I32), I(2)));
    var gInit = new IrGlobal("InitFlag", IrType.I32, null);

    var p1 = L("p1", IrType.Struct(outer));
    var p2 = L("p2", IrType.Struct(outer));
    var def = L("def", IrType.Struct(outer));
    var arr = L("arr", IrType.Array(IrType.Struct(outer)));
    var c = L("c", color.Type);

    var initFn = Function("ModuleInit", false, IrType.Void, [],
      new IrGlobalAssign(gInit, I(77)),
      new IrStore(new IrGlobalRef(gArr), I(0), I(5)));

    var mod = new IrModule("TypesMod", [
      initFn,
      Function("EnumVal", IrType.I32, [c],
        new IrReturn(new IrConvert(IrType.I32, Bin(IrBinaryOp.Or, R(c), C(color.Type, 4)), IrConversion.Wrap))),
      Function("StructOps", IrType.Bool, [],
        new IrDeclare(def, new IrStructDefault(IrType.Struct(outer))),
        new IrDeclare(p1, new IrStructNew(IrType.Struct(outer), [new IrStructNew(IrType.Struct(inner), [F32(float.NaN), F64(-0.0)]), I(10)])),
        new IrDeclare(p2, R(p1)),
        new IrFieldStore(new IrFieldLoad(R(p2), outerPt), innerY, F64(0.0)),
        new IrDeclare(arr, new IrNewArray(IrType.Array(IrType.Struct(outer)), I(1))),
        new IrStore(R(arr), I(0), R(p1)),
        new IrFieldStore(new IrArrayLoad(R(arr), I(0)), outerTag, I(20)),
        new IrReturn(Bin(IrBinaryOp.LogicalAnd,
          Bin(IrBinaryOp.Equal, new IrFieldLoad(R(def), outerTag), I(0)),
          Bin(IrBinaryOp.LogicalAnd,
            new IrStructEquals(R(p1), R(p2)),
            Bin(IrBinaryOp.LogicalAnd,
              new IrUnary(IrUnaryOp.LogicalNot, new IrStructEquals(R(p1), new IrArrayLoad(R(arr), I(0)))),
              Bin(IrBinaryOp.Equal, Bin(IrBinaryOp.Add, new IrGlobalRef(gInit), new IrArrayLoad(new IrGlobalRef(gArr), I(0))), I(82))))))),
      Function("MutateParamStruct", IrType.I32, [p1],
        new IrFieldStore(R(p1), outerTag, I(99)),
        new IrReturn(new IrFieldLoad(R(p1), outerTag)))
    ]) {
      Types = [color, smallEnum, signedByteEnum, i16Enum, u16Enum, u32Enum, i64Enum, u64Enum, inner, outer],
      Globals = [gArr, gInit],
      Initializer = "ModuleInit"
    };

    AssertMatchesInterpreter(mod, "StructOps");
    WithLoadedAssembly(mod, asm => {
      var colorType = asm.GetType("Color")!;
      Assert.That(colorType.IsEnum, Is.True);
      Assert.That(Enum.GetValues(colorType).Length, Is.EqualTo(3));

      var modType = asm.GetType("TypesMod")!;
      var red = Enum.ToObject(colorType, 1);
      Assert.That(modType.GetMethod("EnumVal")!.Invoke(null, [red]), Is.EqualTo(5));

      var outerType = asm.GetType("Outer")!;
      var instance = Activator.CreateInstance(outerType)!;
      Assert.That(modType.GetMethod("MutateParamStruct")!.Invoke(null, [instance]), Is.EqualTo(99));
    });
  }

  [Test]
  public void OptionalsAndNulls() {
    var opt = L("o", IrType.Optional(IrType.I32));
    var s = L("s", IrType.String);

    var mod = new IrModule("OptMod", [
      Function("MakeSome", IrType.Optional(IrType.I32), [], new IrReturn(new IrSome(I(42)))),
      Function("MakeNone", IrType.Optional(IrType.I32), [], new IrReturn(new IrNone(IrType.Optional(IrType.I32)))),
      Function("IsNone", IrType.Bool, [opt], new IrReturn(new IrIsNone(R(opt)))),
      Function("Unwrap", IrType.I32, [opt], new IrReturn(new IrUnwrap(R(opt)))),
      Function("CoalesceVal", IrType.I32, [opt], new IrReturn(new IrCoalesce(R(opt), I(99)))),
      Function("CoalesceOpt", IrType.Optional(IrType.I32), [opt], new IrReturn(new IrCoalesce(R(opt), new IrSome(I(7))))),
      Function("IsNullStr", IrType.Bool, [s], new IrReturn(new IrIsNull(R(s)))),
      Function("NullStr", IrType.String, [], new IrReturn(new IrNull(IrType.String)))
    ]);

    Assert.That(Invoke(mod, "MakeSome"), Is.EqualTo((int?)42));
    Assert.That(Invoke(mod, "MakeNone"), Is.Null);
    Assert.That(Invoke(mod, "IsNone", (int?)null), Is.True);
    Assert.That(Invoke(mod, "IsNone", (int?)5), Is.False);
    Assert.That(Invoke(mod, "Unwrap", (int?)5), Is.EqualTo(5));
    Assert.That(() => Invoke(mod, "Unwrap", (int?)null), Throws.TypeOf<InvalidOperationException>());
    Assert.That(Invoke(mod, "CoalesceVal", (int?)5), Is.EqualTo(5));
    Assert.That(Invoke(mod, "CoalesceVal", (int?)null), Is.EqualTo(99));
    Assert.That(Invoke(mod, "CoalesceOpt", (int?)5), Is.EqualTo((int?)5));
    Assert.That(Invoke(mod, "CoalesceOpt", (int?)null), Is.EqualTo((int?)7));
    Assert.That(Invoke(mod, "IsNullStr", (string?)null), Is.True);
    Assert.That(Invoke(mod, "IsNullStr", "hi"), Is.False);
    Assert.That(Invoke(mod, "NullStr"), Is.Null);
  }

  [Test]
  public void Exceptions_TryCatchFinallyFilterRethrowAndControlTransfer() {
    var mode = L("mode", IrType.I32);
    var res = L("res", IrType.I32);
    var ex = L("ex", IrExceptionClass.Reference);
    var ioe = L("ioe", IrType.Object(IrExceptionClass.InvalidOperationException));
    var inner = L("inner", IrExceptionClass.Reference);
    var i = L("i", IrType.I32);

    var mod = new IrModule("ExMod", [
      // Try / catch (typed + filter + catch-all) / finally + Message + InnerException
      Function("TryCatchFinally", IrType.I32, [mode],
        new IrDeclare(res, I(0)),
        new IrTry(
          Block(
            new IrIf(Bin(IrBinaryOp.Equal, R(mode), I(1)),
              Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.InvalidOperationException), IrExceptionClass.Constructor(IrExceptionClass.InvalidOperationException, 1), [new IrStrConst("boom")]))),
              null),
            new IrIf(Bin(IrBinaryOp.Equal, R(mode), I(2)),
              Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.KeyNotFoundException), IrExceptionClass.Constructor(IrExceptionClass.KeyNotFoundException, 0), []))),
              null),
            new IrIf(Bin(IrBinaryOp.Equal, R(mode), I(3)),
              Block(
                new IrDeclare(inner, new IrNew(IrType.Object(IrExceptionClass.AssertionException), IrExceptionClass.Constructor(IrExceptionClass.AssertionException, 1), [new IrStrConst("cause")])),
                new IrThrow(new IrNew(IrType.Object(IrExceptionClass.Exception), IrExceptionClass.Constructor(IrExceptionClass.Exception, 2), [new IrStrConst("outer"), R(inner)]))),
              null),
            new IrReturn(I(10))
          ),
          [
            new IrCatch(
              IrExceptionClass.InvalidOperationException,
              ioe,
              new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [new IrFieldLoad(R(ioe), IrExceptionClass.Message), new IrStrConst("boom")]),
              Block(new IrAssign(res, I(100)))),
            new IrCatch(
              IrExceptionClass.KeyNotFoundException,
              null,
              B(false),
              Block(new IrAssign(res, I(999)))),
            new IrCatch(
              IrExceptionClass.KeyNotFoundException,
              null,
              null,
              Block(new IrAssign(res, I(200)))),
            new IrCatch(
              IrExceptionClass.Exception,
              ex,
              null,
              Block(new IrAssign(res, new IrConditional(new IrIsNull(new IrFieldLoad(R(ex), IrExceptionClass.InnerException)), I(0), I(300)))))
          ],
          Block(new IrAssign(res, Bin(IrBinaryOp.Add, R(res), I(1))))),
        new IrReturn(R(res))),
      // Try / finally only + break & continue leaving try
      Function("TryInLoop", IrType.I32, [],
        new IrDeclare(res, I(0)),
        new IrDeclare(i, I(0)),
        new IrWhile(Bin(IrBinaryOp.Less, R(i), I(4)), Block(
          new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))),
          new IrTry(
            Block(
              new IrIf(Bin(IrBinaryOp.Equal, R(i), I(2)), Block(new IrContinue()), null),
              new IrIf(Bin(IrBinaryOp.Equal, R(i), I(3)), Block(new IrBreak()), null)),
            [],
            Block(new IrAssign(res, Bin(IrBinaryOp.Add, R(res), I(10)))))
        )),
        new IrReturn(R(res))),
      // Rethrow and ThrowExpression
      Function("Rethrow", IrType.Void, [],
        new IrTry(
          Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.FormatException), IrExceptionClass.Constructor(IrExceptionClass.FormatException, 0), []))),
          [new IrCatch(IrExceptionClass.FormatException, null, null, Block(new IrRethrow()))],
          null)),
      Function("ThrowExpr", IrType.I32, [mode],
        new IrReturn(new IrConditional(
          Bin(IrBinaryOp.Greater, R(mode), I(0)),
          R(mode),
          new IrThrowExpression(IrType.I32, new IrNew(IrType.Object(IrExceptionClass.ArgumentException), IrExceptionClass.Constructor(IrExceptionClass.ArgumentException, 1), [new IrStrConst("bad")])))))
    ]);

    AssertMatchesInterpreter(mod, "TryCatchFinally", 0);
    AssertMatchesInterpreter(mod, "TryCatchFinally", 1);
    AssertMatchesInterpreter(mod, "TryCatchFinally", 2);
    AssertMatchesInterpreter(mod, "TryCatchFinally", 3);
    AssertMatchesInterpreter(mod, "TryInLoop");
    Assert.That(() => Invoke(mod, "Rethrow"), Throws.TypeOf<FormatException>());
    Assert.That(Invoke(mod, "ThrowExpr", 5), Is.EqualTo(5));
    Assert.That(() => Invoke(mod, "ThrowExpr", 0), Throws.TypeOf<ArgumentException>());
  }

  [Test]
  public void Intrinsics_MathBitsEndianStringsAndLegalization() {
    var f = L("f", IrType.F64);
    var g = L("g", IrType.F64);
    var s = L("s", IrType.F32);
    var t = L("t", IrType.F32);
    var n = L("n", IrType.I32);
    var un = L("un", IrType.U32);
    var ul = L("ul", IrType.U64);
    var bytes = L("b", IrType.Array(IrType.U8));
    var str1 = L("a", IrType.String);
    var str2 = L("b", IrType.String);
    var ch = L("c", IrType.U16);
    var b8 = L("x", IrType.U8);

    var mod = new IrModule("Intrinsics", [
      // Float & math intrinsics (f64 + f32)
      Function("AbsI32", IrType.I32, [n], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.I32, [R(n)]))),
      Function("AbsU32", IrType.U32, [un], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.U32, [R(un)]))),
      Function("AbsF32", IrType.F32, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F32, [R(s)]))),
      Function("MinF64", IrType.F64, [f, g], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Min, IrType.F64, [R(f), R(g)]))),
      Function("MaxF32", IrType.F32, [s, t], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Max, IrType.F32, [R(s), R(t)]))),
      Function("SignI32", IrType.I32, [n], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(n)]))),
      Function("SignU32", IrType.I32, [un], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(un)]))),
      Function("SignU64", IrType.I32, [ul], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(ul)]))),
      Function("SignF32", IrType.I32, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(s)]))),
      Function("SqrtF64", IrType.F64, [f], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [R(f)]))),
      Function("FloorF32", IrType.F32, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Floor, IrType.F32, [R(s)]))),
      Function("CeilF64", IrType.F64, [f], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Ceiling, IrType.F64, [R(f)]))),
      Function("TruncF64", IrType.F64, [f], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Truncate, IrType.F64, [R(f)]))),
      Function("RoundEvenF64", IrType.F64, [f], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundEven, IrType.F64, [R(f)]))),
      Function("RoundEvenF32", IrType.F32, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundEven, IrType.F32, [R(s)]))),
      Function("RoundAwayF64", IrType.F64, [f], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundAway, IrType.F64, [R(f)]))),
      Function("RoundAwayF32", IrType.F32, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundAway, IrType.F32, [R(s)]))),
      Function("CopySignF64", IrType.F64, [f, g], new IrReturn(new IrIntrinsic(IrIntrinsicOp.CopySign, IrType.F64, [R(f), R(g)]))),
      Function("CopySignF32", IrType.F32, [s, t], new IrReturn(new IrIntrinsic(IrIntrinsicOp.CopySign, IrType.F32, [R(s), R(t)]))),
      Function("ClassifyF64", IrType.I32, [f], new IrReturn(
        Bin(IrBinaryOp.Add,
          new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, [R(f)]), I(1), I(0)),
          Bin(IrBinaryOp.Add,
            new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, [R(f)]), I(2), I(0)),
            Bin(IrBinaryOp.Add,
              new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsFinite, IrType.Bool, [R(f)]), I(4), I(0)),
              Bin(IrBinaryOp.Add,
                new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsNormal, IrType.Bool, [R(f)]), I(8), I(0)),
                Bin(IrBinaryOp.Add,
                  new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsSubnormal, IrType.Bool, [R(f)]), I(16), I(0)),
                  new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsNegative, IrType.Bool, [R(f)]), I(32), I(0))))))))),
      Function("IsNaNF32", IrType.Bool, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, [R(s)]))),
      Function("BitsRoundtrip32", IrType.F32, [s],
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F32, [new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I32, [R(s)])]))),
      Function("BitsRoundtrip64", IrType.F64, [f],
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F64, [new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I64, [R(f)])]))),
      // BitOperations (32 & 64 bit)
      Function("BitOps32", IrType.I32, [un, n],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.RotateLeft, IrType.U32, [R(un), R(n)]), IrConversion.Wrap),
          Bin(IrBinaryOp.Add,
            new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.RotateRight, IrType.U32, [R(un), R(n)]), IrConversion.Wrap),
            Bin(IrBinaryOp.Add,
              new IrIntrinsic(IrIntrinsicOp.LeadingZeroCount, IrType.I32, [R(un)]),
              Bin(IrBinaryOp.Add,
                new IrIntrinsic(IrIntrinsicOp.TrailingZeroCount, IrType.I32, [R(un)]),
                new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [R(un)]))))))),
      Function("BitOps64", IrType.I32, [ul, n],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.RotateLeft, IrType.U64, [R(ul), R(n)]), IrConversion.Wrap),
          Bin(IrBinaryOp.Add,
            new IrIntrinsic(IrIntrinsicOp.LeadingZeroCount, IrType.I32, [R(ul)]),
            Bin(IrBinaryOp.Add,
              new IrIntrinsic(IrIntrinsicOp.TrailingZeroCount, IrType.I32, [R(ul)]),
              new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [R(ul)])))))),
      // Endian loads & stores (i16, i32, i64, f32, f64, little & big)
      Function("EndianRoundtrip", IrType.F64, [bytes],
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(bytes), I(0), C(IrType.I16, -1234)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(bytes), I(2), F32(3.5f)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(bytes), I(6), F64(12.25)])),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConvert(IrType.F64, new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.I16, [R(bytes), I(0)]), IrConversion.Wrap),
          Bin(IrBinaryOp.Add,
            new IrConvert(IrType.F64, new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.F32, [R(bytes), I(2)]), IrConversion.Wrap),
            new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.F64, [R(bytes), I(6)]))))),
      Function("EndianBad", IrType.I32, [bytes, n],
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.I32, [R(bytes), R(n)]))),
      // Strings
      Function("StrOps", IrType.String, [str1, str2, ch],
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [
          new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [R(str1), I(1), I(3)]),
          new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [R(str2), I(1)]),
          new IrIntrinsic(IrIntrinsicOp.StrJoin, IrType.String, [new IrStrConst("-"), new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.String), [R(str1), R(str2)])])
        ]))),
      Function("StrQueries", IrType.I32, [str1, str2, ch],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [R(str1)]),
          Bin(IrBinaryOp.Add,
            new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [R(str1), I(0)]), IrConversion.Wrap),
            Bin(IrBinaryOp.Add,
              new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [R(str1), R(str2)]),
              Bin(IrBinaryOp.Add,
                new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [R(str1), R(str2)]),
                Bin(IrBinaryOp.Add,
                  new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [R(str1), R(str2), I(1)]),
                  Bin(IrBinaryOp.Add,
                    new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [R(str1), R(ch)]),
                    Bin(IrBinaryOp.Add,
                      new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [R(str1), R(ch), I(1)]),
                      Bin(IrBinaryOp.Add,
                        new IrConditional(new IrIntrinsic(IrIntrinsicOp.StrStartsWith, IrType.Bool, [R(str1), R(str2)]), I(1), I(0)),
                        Bin(IrBinaryOp.Add,
                          new IrConditional(new IrIntrinsic(IrIntrinsicOp.StrStartsWith, IrType.Bool, [R(str1), R(ch)]), I(2), I(0)),
                          Bin(IrBinaryOp.Add,
                            new IrConditional(new IrIntrinsic(IrIntrinsicOp.StrEndsWith, IrType.Bool, [R(str1), R(str2)]), I(4), I(0)),
                            Bin(IrBinaryOp.Add,
                              new IrConditional(new IrIntrinsic(IrIntrinsicOp.StrEndsWith, IrType.Bool, [R(str1), R(ch)]), I(8), I(0)),
                              Bin(IrBinaryOp.Add,
                                new IrConditional(new IrIntrinsic(IrIntrinsicOp.StrContains, IrType.Bool, [R(str1), R(str2)]), I(16), I(0)),
                                new IrConditional(new IrIntrinsic(IrIntrinsicOp.StrContains, IrType.Bool, [R(str1), R(ch)]), I(32), I(0)))))))))))))))),
      // Legalized intrinsics (portable float Sin + integer ByteSwap + narrow Lzc)
      Function("SinF64", IrType.F64, [f], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sin, IrType.F64, [R(f)]))),
      Function("Swap32", IrType.U32, [un], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.U32, [R(un)]))),
      Function("Lzc8", IrType.I32, [b8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.LeadingZeroCount, IrType.I32, [R(b8)])))
    ]);

    var legalized = Legalize(mod);
    AssertMatchesInterpreter(legalized, "AbsI32", -42);
    AssertMatchesInterpreter(legalized, "AbsU32", 42u);
    AssertMatchesInterpreter(legalized, "AbsF32", -3.5f);
    AssertMatchesInterpreter(legalized, "MinF64", -0.0, 0.0);
    AssertMatchesInterpreter(legalized, "MaxF32", 1.5f, 2.5f);
    AssertMatchesInterpreter(legalized, "SignI32", -9);
    AssertMatchesInterpreter(legalized, "SignU32", 5u);
    AssertMatchesInterpreter(legalized, "SignU64", 0UL);
    AssertMatchesInterpreter(legalized, "SignF32", 2.0f);
    AssertMatchesInterpreter(legalized, "SqrtF64", 9.0);
    AssertMatchesInterpreter(legalized, "FloorF32", 2.9f);
    AssertMatchesInterpreter(legalized, "CeilF64", 2.1);
    AssertMatchesInterpreter(legalized, "TruncF64", -2.9);
    AssertMatchesInterpreter(legalized, "RoundEvenF64", 2.5);
    AssertMatchesInterpreter(legalized, "RoundEvenF32", 3.5f);
    AssertMatchesInterpreter(legalized, "RoundAwayF64", 2.5);
    AssertMatchesInterpreter(legalized, "RoundAwayF32", 2.5f);
    AssertMatchesInterpreter(legalized, "CopySignF64", 3.0, -1.0);
    AssertMatchesInterpreter(legalized, "CopySignF32", 3.0f, -1.0f);
    AssertMatchesInterpreter(legalized, "ClassifyF64", -2.5);
    AssertMatchesInterpreter(legalized, "ClassifyF64", double.PositiveInfinity);
    AssertMatchesInterpreter(legalized, "ClassifyF64", double.Epsilon);
    AssertMatchesInterpreter(legalized, "ClassifyF64", double.NaN);
    AssertMatchesInterpreter(legalized, "IsNaNF32", float.NaN);
    AssertMatchesInterpreter(legalized, "BitsRoundtrip32", -1.25f);
    AssertMatchesInterpreter(legalized, "BitsRoundtrip64", -1.25);
    AssertMatchesInterpreter(legalized, "BitOps32", 0x1234_5678u, 5);
    AssertMatchesInterpreter(legalized, "BitOps64", 0x0123_4567_89AB_CDEFUL, 13);
    AssertMatchesInterpreter(legalized, "EndianRoundtrip", new byte[16]);
    Assert.That(() => Invoke(legalized, "EndianBad", new byte[4], -1), Throws.TypeOf<IndexOutOfRangeException>());
    Assert.That(() => Invoke(legalized, "EndianBad", new byte[4], 2), Throws.TypeOf<IndexOutOfRangeException>());
    AssertMatchesInterpreter(legalized, "StrOps", "abcdef", "xyz", (ushort)'a');
    AssertMatchesInterpreter(legalized, "StrQueries", "ababa", "ab", (ushort)'a');
    AssertMatchesInterpreter(legalized, "SinF64", 0.5);
    AssertMatchesInterpreter(legalized, "Swap32", 0x1234_5678u);
    AssertMatchesInterpreter(legalized, "Lzc8", (byte)0x08);
  }

}
