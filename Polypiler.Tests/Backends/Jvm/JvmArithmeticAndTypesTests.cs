using System;
using NUnit.Framework;
using Polypiler.Core.Backends.Jvm;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Jvm.JvmTestRunner;

namespace Polypiler.Tests.Backends.Jvm;

[TestFixture]
public sealed class JvmArithmeticAndTypesTests {

  [Test]
  public void Primitives_Unary_Binary_Unsigned_AndEnums_ExecuteCorrectly() {
    var colorEnum = new IrEnumType("Color", IrType.I32, [new("Red", 1), new("Green", 2), new("Blue", 4)]);
    var bigEnum = new IrEnumType("BigFlags", IrType.U64, [new("A", 1), new("B", unchecked((long)0x8000_0000_0000_0000UL))], isFlags: true);

    var a32 = L("a", IrType.I32);
    var b32 = L("b", IrType.I32);
    var ua32 = L("a", IrType.U32);
    var ub32 = L("b", IrType.U32);
    var a64 = L("a", IrType.I64);
    var b64 = L("b", IrType.I64);
    var ua64 = L("a", IrType.U64);
    var ub64 = L("b", IrType.U64);
    var fa = L("a", IrType.F32);
    var fb = L("b", IrType.F32);
    var da = L("a", IrType.F64);
    var db = L("b", IrType.F64);
    var u8p = L("b", IrType.U8);
    var i8p = L("b", IrType.I8);
    var i16p = L("s", IrType.I16);
    var u16p = L("c", IrType.U16);
    var bp = L("flag", IrType.Bool);
    var ep = L("c", colorEnum.Type);
    var bep = L("f", bigEnum.Type);

    var module = new IrModule("Arith", [
      // Narrow types & unsigned u8 normalization
      Function("U8ToI32", IrType.I32, [u8p], new IrReturn(new IrConvert(IrType.I32, R(u8p)))),
      Function("U8Echo", IrType.U8, [u8p], new IrReturn(R(u8p))),
      Function("U8CallEcho", IrType.I32, [u8p], new IrReturn(new IrConvert(IrType.I32, new IrCall("U8Echo", IrType.U8, [R(u8p)])))),
      Function("I8ToI32", IrType.I32, [i8p], new IrReturn(new IrConvert(IrType.I32, R(i8p)))),
      Function("I16ToI32", IrType.I32, [i16p], new IrReturn(new IrConvert(IrType.I32, R(i16p)))),
      Function("U16ToI32", IrType.I32, [u16p], new IrReturn(new IrConvert(IrType.I32, R(u16p)))),
      Function("BoolNot", IrType.Bool, [bp], new IrReturn(new IrUnary(IrUnaryOp.LogicalNot, R(bp)))),
      // Enums
      Function("EnumOps", IrType.Bool, [ep], new IrReturn(Bin(IrBinaryOp.Equal, Bin(IrBinaryOp.Or, R(ep), C(colorEnum.Type, 2)), C(colorEnum.Type, 3)))),
      Function("BigEnumBitNot", bigEnum.Type, [bep], new IrReturn(new IrUnary(IrUnaryOp.BitNot, R(bep)))),
      // Unary ops
      Function("Neg32", IrType.I32, [a32], new IrReturn(new IrUnary(IrUnaryOp.Negate, R(a32)))),
      Function("Neg64", IrType.I64, [a64], new IrReturn(new IrUnary(IrUnaryOp.Negate, R(a64)))),
      Function("NegF32", IrType.F32, [fa], new IrReturn(new IrUnary(IrUnaryOp.Negate, R(fa)))),
      Function("NegF64", IrType.F64, [da], new IrReturn(new IrUnary(IrUnaryOp.Negate, R(da)))),
      Function("NegChk32", IrType.I32, [a32], new IrReturn(new IrUnary(IrUnaryOp.NegateChecked, R(a32)))),
      Function("NegChk64", IrType.I64, [a64], new IrReturn(new IrUnary(IrUnaryOp.NegateChecked, R(a64)))),
      Function("Not32", IrType.I32, [a32], new IrReturn(new IrUnary(IrUnaryOp.BitNot, R(a32)))),
      Function("Not64", IrType.I64, [a64], new IrReturn(new IrUnary(IrUnaryOp.BitNot, R(a64)))),
      // Wrapping & float arithmetic
      Function("AddSubMul32", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.Multiply, Bin(IrBinaryOp.Add, R(a32), R(b32)), Bin(IrBinaryOp.Subtract, R(a32), R(b32))))),
      Function("AddSubMul64", IrType.I64, [a64, b64], new IrReturn(Bin(IrBinaryOp.Multiply, Bin(IrBinaryOp.Add, R(a64), R(b64)), Bin(IrBinaryOp.Subtract, R(a64), R(b64))))),
      Function("FloatOps32", IrType.F32, [fa, fb], new IrReturn(Bin(IrBinaryOp.Remainder, Bin(IrBinaryOp.Divide, Bin(IrBinaryOp.Multiply, Bin(IrBinaryOp.Add, R(fa), R(fb)), F32(2.0f)), Bin(IrBinaryOp.Subtract, R(fa), F32(1.0f))), F32(3.5f)))),
      Function("FloatOps64", IrType.F64, [da, db], new IrReturn(Bin(IrBinaryOp.Remainder, Bin(IrBinaryOp.Divide, Bin(IrBinaryOp.Multiply, Bin(IrBinaryOp.Add, R(da), R(db)), F64(1.0)), Bin(IrBinaryOp.Subtract, R(da), F64(-0.0))), F64(3.5)))),
      // Signed & unsigned div/rem
      Function("Div32", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.Divide, R(a32), R(b32)))),
      Function("Rem32", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.Remainder, R(a32), R(b32)))),
      Function("Div64", IrType.I64, [a64, b64], new IrReturn(Bin(IrBinaryOp.Divide, R(a64), R(b64)))),
      Function("Rem64", IrType.I64, [a64, b64], new IrReturn(Bin(IrBinaryOp.Remainder, R(a64), R(b64)))),
      Function("UDiv32", IrType.U32, [ua32, ub32], new IrReturn(Bin(IrBinaryOp.Divide, R(ua32), R(ub32)))),
      Function("URem32", IrType.U32, [ua32, ub32], new IrReturn(Bin(IrBinaryOp.Remainder, R(ua32), R(ub32)))),
      Function("UDiv64", IrType.U64, [ua64, ub64], new IrReturn(Bin(IrBinaryOp.Divide, R(ua64), R(ub64)))),
      Function("URem64", IrType.U64, [ua64, ub64], new IrReturn(Bin(IrBinaryOp.Remainder, R(ua64), R(ub64)))),
      // Bitwise & shifts
      Function("BitOps32", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.Xor, Bin(IrBinaryOp.Or, Bin(IrBinaryOp.And, R(a32), R(b32)), Bin(IrBinaryOp.ShiftLeft, R(a32), I(2))), Bin(IrBinaryOp.ShiftRightLogical, R(a32), I(1))))),
      Function("BitOps64", IrType.I64, [a64, b64], new IrReturn(Bin(IrBinaryOp.Xor, Bin(IrBinaryOp.Or, Bin(IrBinaryOp.And, R(a64), R(b64)), Bin(IrBinaryOp.ShiftLeft, R(a64), I(2))), Bin(IrBinaryOp.ShiftRightLogical, R(a64), I(1))))),
      Function("Sar32", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.ShiftRight, R(a32), R(b32)))),
      Function("ShrU32", IrType.U32, [ua32, b32], new IrReturn(Bin(IrBinaryOp.ShiftRight, R(ua32), R(b32)))),
      Function("Sar64", IrType.I64, [a64, b32], new IrReturn(Bin(IrBinaryOp.ShiftRight, R(a64), R(b32)))),
      Function("ShrU64", IrType.U64, [ua64, b32], new IrReturn(Bin(IrBinaryOp.ShiftRight, R(ua64), R(b32)))),
      // Comparisons (signed, unsigned, float including NaN)
      Function("CmpU32Lt", IrType.Bool, [ua32, ub32], new IrReturn(Bin(IrBinaryOp.Less, R(ua32), R(ub32)))),
      Function("CmpU32Ge", IrType.Bool, [ua32, ub32], new IrReturn(Bin(IrBinaryOp.GreaterOrEqual, R(ua32), R(ub32)))),
      Function("CmpU64Gt", IrType.Bool, [ua64, ub64], new IrReturn(Bin(IrBinaryOp.Greater, R(ua64), R(ub64)))),
      Function("CmpU64Le", IrType.Bool, [ua64, ub64], new IrReturn(Bin(IrBinaryOp.LessOrEqual, R(ua64), R(ub64)))),
      Function("CmpI64Eq", IrType.Bool, [a64, b64], new IrReturn(Bin(IrBinaryOp.Equal, R(a64), R(b64)))),
      Function("CmpI64Ne", IrType.Bool, [a64, b64], new IrReturn(Bin(IrBinaryOp.NotEqual, R(a64), R(b64)))),
      Function("CmpF32", IrType.I32, [fa, fb],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConditional(Bin(IrBinaryOp.Equal, R(fa), R(fb)), I(1), I(0)),
          Bin(IrBinaryOp.Add,
            new IrConditional(Bin(IrBinaryOp.NotEqual, R(fa), R(fb)), I(2), I(0)),
            Bin(IrBinaryOp.Add,
              new IrConditional(Bin(IrBinaryOp.Less, R(fa), R(fb)), I(4), I(0)),
              Bin(IrBinaryOp.Add,
                new IrConditional(Bin(IrBinaryOp.LessOrEqual, R(fa), R(fb)), I(8), I(0)),
                Bin(IrBinaryOp.Add,
                  new IrConditional(Bin(IrBinaryOp.Greater, R(fa), R(fb)), I(16), I(0)),
                  new IrConditional(Bin(IrBinaryOp.GreaterOrEqual, R(fa), R(fb)), I(32), I(0))))))))),
      Function("CmpF64", IrType.I32, [da, db],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConditional(Bin(IrBinaryOp.Less, R(da), R(db)), I(1), I(0)),
          new IrConditional(Bin(IrBinaryOp.Greater, R(da), R(db)), I(2), I(0))))),
      // Short-circuit logical && and || (rhs would divide by 0 if evaluated!)
      Function("ShortAnd", IrType.Bool, [bp, a32],
        new IrReturn(Bin(IrBinaryOp.LogicalAnd, R(bp), Bin(IrBinaryOp.Equal, Bin(IrBinaryOp.Divide, I(10), R(a32)), I(5))))),
      Function("ShortOr", IrType.Bool, [bp, a32],
        new IrReturn(Bin(IrBinaryOp.LogicalOr, R(bp), Bin(IrBinaryOp.Equal, Bin(IrBinaryOp.Divide, I(10), R(a32)), I(5))))),
      // Special float constants (+0, -0, 1, 2, other)
      Function("NegZeroF32Bits", IrType.I32, [], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I32, [F32(-0.0f)]))),
      Function("PosZeroF32Bits", IrType.I32, [], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I32, [F32(0.0f)]))),
      Function("OneF32", IrType.F32, [], new IrReturn(F32(1.0f))),
      Function("ZeroF64", IrType.F64, [], new IrReturn(F64(0.0)))
    ]) { Types = [colorEnum, bigEnum] };

    var files = JvmEmitter.Emit(module);
    var output = RunHarness(files, """
      public class Harness {
        static boolean throwsArith(Runnable r) {
          try { r.run(); return false; } catch (ArithmeticException e) { return true; }
        }
        public static void main(String[] args) {
          System.out.println(Arith.U8ToI32((byte)0xFF) == 255);
          System.out.println(Arith.U8CallEcho((byte)0xFE) == 254);
          System.out.println(Arith.I8ToI32((byte)-5) == -5);
          System.out.println(Arith.I16ToI32((short)-300) == -300);
          System.out.println(Arith.U16ToI32((char)65000) == 65000);
          System.out.println(Arith.BoolNot(true) == false && Arith.BoolNot(false) == true);
          System.out.println(Arith.EnumOps(1) && Arith.BigEnumBitNot(0L) == -1L);
          System.out.println(Arith.Neg32(7) == -7 && Arith.Neg64(7L) == -7L && Arith.NegF32(2.5f) == -2.5f && Arith.NegF64(2.5) == -2.5);
          System.out.println(Arith.NegChk32(5) == -5 && throwsArith(() -> Arith.NegChk32(Integer.MIN_VALUE)));
          System.out.println(Arith.NegChk64(5L) == -5L && throwsArith(() -> Arith.NegChk64(Long.MIN_VALUE)));
          System.out.println(Arith.Not32(0) == -1 && Arith.Not64(0L) == -1L);
          System.out.println(Arith.AddSubMul32(5, 3) == 16 && Arith.AddSubMul64(5L, 3L) == 16L);
          System.out.println(Math.abs(Arith.FloatOps32(3.0f, 1.0f) - 0.5f) < 1e-6 && Math.abs(Arith.FloatOps64(2.0, 3.0) - 2.5) < 1e-12);
          System.out.println(Arith.Div32(-7, 2) == -3 && Arith.Rem32(-7, 2) == -1);
          System.out.println(throwsArith(() -> Arith.Div32(Integer.MIN_VALUE, -1)) && throwsArith(() -> Arith.Div32(1, 0)));
          System.out.println(Arith.Div64(-7L, 2L) == -3L && Arith.Rem64(-7L, 2L) == -1L);
          System.out.println(throwsArith(() -> Arith.Div64(Long.MIN_VALUE, -1L)) && throwsArith(() -> Arith.Rem64(Long.MIN_VALUE, -1L)));
          // Unsigned 0xFFFFFFFF / 2 == 0x7FFFFFFF (2147483647), rem == 1
          System.out.println(Arith.UDiv32(-1, 2) == 0x7FFFFFFF && Arith.URem32(-1, 2) == 1);
          // Unsigned 0xFFFFFFFFFFFFFFFF / 2 == 0x7FFFFFFFFFFFFFFF, rem == 1
          System.out.println(Arith.UDiv64(-1L, 2L) == Long.MAX_VALUE && Arith.URem64(-1L, 2L) == 1L);
          System.out.println(Arith.Sar32(-8, 1) == -4 && Arith.ShrU32(-1, 1) == 0x7FFFFFFF);
          System.out.println(Arith.Sar64(-8L, 1) == -4L && Arith.ShrU64(-1L, 1) == Long.MAX_VALUE);
          System.out.println(Arith.BitOps32(6, 3) != 0 && Arith.BitOps64(6L, 3L) != 0L);
          System.out.println(Arith.CmpU32Lt(1, -1) && !Arith.CmpU32Lt(-1, 1) && Arith.CmpU32Ge(-1, 1));
          System.out.println(Arith.CmpU64Gt(-1L, 1L) && Arith.CmpU64Le(1L, -1L) && Arith.CmpI64Eq(5L, 5L) && Arith.CmpI64Ne(5L, 6L));
          // NaN only triggers NotEqual (bit 2)
          System.out.println(Arith.CmpF32(Float.NaN, 1.0f) == 2 && Arith.CmpF32(1.0f, 2.0f) == (2|4|8) && Arith.CmpF32(2.0f, 1.0f) == (2|16|32));
          System.out.println(Arith.CmpF64(Double.NaN, 1.0) == 0 && Arith.CmpF64(1.0, 2.0) == 1 && Arith.CmpF64(2.0, 1.0) == 2);
          System.out.println(!Arith.ShortAnd(false, 0) && Arith.ShortAnd(true, 2) && Arith.ShortOr(true, 0) && Arith.ShortOr(false, 2));
          System.out.println(Arith.NegZeroF32Bits() == 0x80000000 && Arith.PosZeroF32Bits() == 0 && Arith.OneF32() == 1.0f && Arith.ZeroF64() == 0.0);
        }
      }
      """);
    foreach (var line in output.Split('\n'))
      Assert.That(line, Is.EqualTo("true"), output);
  }

  [Test]
  public void CheckedArithmetic_AndAllConversions_ExecuteAndTrapCorrectly() {
    var a32 = L("a", IrType.I32);
    var b32 = L("b", IrType.I32);
    var ua32 = L("a", IrType.U32);
    var ub32 = L("b", IrType.U32);
    var a64 = L("a", IrType.I64);
    var b64 = L("b", IrType.I64);
    var ua64 = L("a", IrType.U64);
    var ub64 = L("b", IrType.U64);
    var f32 = L("f", IrType.F32);
    var f64 = L("d", IrType.F64);

    var module = new IrModule("ConvChk", [
      // Checked signed & unsigned
      Function("AddChkI32", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.AddChecked, R(a32), R(b32)))),
      Function("SubChkI32", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.SubtractChecked, R(a32), R(b32)))),
      Function("MulChkI32", IrType.I32, [a32, b32], new IrReturn(Bin(IrBinaryOp.MultiplyChecked, R(a32), R(b32)))),
      Function("AddChkI64", IrType.I64, [a64, b64], new IrReturn(Bin(IrBinaryOp.AddChecked, R(a64), R(b64)))),
      Function("AddChkU32", IrType.U32, [ua32, ub32], new IrReturn(Bin(IrBinaryOp.AddChecked, R(ua32), R(ub32)))),
      Function("SubChkU32", IrType.U32, [ua32, ub32], new IrReturn(Bin(IrBinaryOp.SubtractChecked, R(ua32), R(ub32)))),
      Function("MulChkU32", IrType.U32, [ua32, ub32], new IrReturn(Bin(IrBinaryOp.MultiplyChecked, R(ua32), R(ub32)))),
      Function("AddChkU64", IrType.U64, [ua64, ub64], new IrReturn(Bin(IrBinaryOp.AddChecked, R(ua64), R(ub64)))),
      Function("SubChkU64", IrType.U64, [ua64, ub64], new IrReturn(Bin(IrBinaryOp.SubtractChecked, R(ua64), R(ub64)))),
      Function("MulChkU64", IrType.U64, [ua64, ub64], new IrReturn(Bin(IrBinaryOp.MultiplyChecked, R(ua64), R(ub64)))),
      // Int <-> Int wrap & trap
      Function("WrapI64ToU8", IrType.U8, [a64], new IrReturn(new IrConvert(IrType.U8, R(a64)))),
      Function("WrapI64ToI8", IrType.I8, [a64], new IrReturn(new IrConvert(IrType.I8, R(a64)))),
      Function("WrapI64ToI16", IrType.I16, [a64], new IrReturn(new IrConvert(IrType.I16, R(a64)))),
      Function("WrapI64ToU16", IrType.U16, [a64], new IrReturn(new IrConvert(IrType.U16, R(a64)))),
      Function("WrapU32ToI64", IrType.I64, [ua32], new IrReturn(new IrConvert(IrType.I64, R(ua32)))),
      Function("WrapSame", IrType.I32, [a32], new IrReturn(new IrConvert(IrType.I32, R(a32)))),
      Function("TrapI32ToU8", IrType.U8, [a32], new IrReturn(new IrConvert(IrType.U8, R(a32), IrConversion.Trap))),
      Function("TrapI32ToI8", IrType.I8, [a32], new IrReturn(new IrConvert(IrType.I8, R(a32), IrConversion.Trap))),
      Function("TrapI32ToU32", IrType.U32, [a32], new IrReturn(new IrConvert(IrType.U32, R(a32), IrConversion.Trap))),
      Function("TrapU32ToI32", IrType.I32, [ua32], new IrReturn(new IrConvert(IrType.I32, R(ua32), IrConversion.Trap))),
      Function("TrapI64ToU64", IrType.U64, [a64], new IrReturn(new IrConvert(IrType.U64, R(a64), IrConversion.Trap))),
      Function("TrapU64ToI64", IrType.I64, [ua64], new IrReturn(new IrConvert(IrType.I64, R(ua64), IrConversion.Trap))),
      Function("TrapI64ToI32", IrType.I32, [a64], new IrReturn(new IrConvert(IrType.I32, R(a64), IrConversion.Trap))),
      // Int -> Float & Float <-> Float
      Function("U32ToF64", IrType.F64, [ua32], new IrReturn(new IrConvert(IrType.F64, R(ua32)))),
      Function("I64ToF32", IrType.F32, [a64], new IrReturn(new IrConvert(IrType.F32, R(a64)))),
      Function("I32ToF64", IrType.F64, [a32], new IrReturn(new IrConvert(IrType.F64, R(a32)))),
      Function("U64ToF64", IrType.F64, [ua64], new IrReturn(new IrConvert(IrType.F64, R(ua64)))),
      Function("U64ToF32", IrType.F32, [ua64], new IrReturn(new IrConvert(IrType.F32, R(ua64)))),
      Function("F32ToF64", IrType.F64, [f32], new IrReturn(new IrConvert(IrType.F64, R(f32)))),
      Function("F64ToF32", IrType.F32, [f64], new IrReturn(new IrConvert(IrType.F32, R(f64)))),
      // Float -> Int (Saturate & Trap)
      Function("SatF32ToI32", IrType.I32, [f32], new IrReturn(new IrConvert(IrType.I32, R(f32), IrConversion.Saturate))),
      Function("SatF64ToI64", IrType.I64, [f64], new IrReturn(new IrConvert(IrType.I64, R(f64), IrConversion.Saturate))),
      Function("SatF64ToU8", IrType.U8, [f64], new IrReturn(new IrConvert(IrType.U8, R(f64), IrConversion.Saturate))),
      Function("SatF64ToU64", IrType.U64, [f64], new IrReturn(new IrConvert(IrType.U64, R(f64), IrConversion.Saturate))),
      Function("TrapF64ToI32", IrType.I32, [f64], new IrReturn(new IrConvert(IrType.I32, R(f64), IrConversion.Trap))),
      Function("TrapF64ToI64", IrType.I64, [f64], new IrReturn(new IrConvert(IrType.I64, R(f64), IrConversion.Trap))),
      Function("TrapF64ToU64", IrType.U64, [f64], new IrReturn(new IrConvert(IrType.U64, R(f64), IrConversion.Trap)))
    ]);

    var files = JvmEmitter.Emit(module);
    var output = RunHarness(files, """
      public class Harness {
        static boolean throwsArith(Runnable r) {
          try { r.run(); return false; } catch (ArithmeticException e) { return true; }
        }
        public static void main(String[] args) {
          System.out.println(ConvChk.AddChkI32(10, 20) == 30 && throwsArith(() -> ConvChk.AddChkI32(Integer.MAX_VALUE, 1)));
          System.out.println(ConvChk.SubChkI32(10, 20) == -10 && throwsArith(() -> ConvChk.SubChkI32(Integer.MIN_VALUE, 1)));
          System.out.println(ConvChk.MulChkI32(6, 7) == 42 && throwsArith(() -> ConvChk.MulChkI32(Integer.MAX_VALUE, 2)));
          System.out.println(ConvChk.AddChkI64(10L, 20L) == 30L && throwsArith(() -> ConvChk.AddChkI64(Long.MAX_VALUE, 1L)));
          System.out.println(ConvChk.AddChkU32(10, 20) == 30 && throwsArith(() -> ConvChk.AddChkU32(-1, 1)));
          System.out.println(ConvChk.SubChkU32(20, 10) == 10 && throwsArith(() -> ConvChk.SubChkU32(5, 10)));
          System.out.println(ConvChk.MulChkU32(1000, 1000) == 1000000 && throwsArith(() -> ConvChk.MulChkU32(0x10000, 0x10000)));
          System.out.println(ConvChk.AddChkU64(10L, 20L) == 30L && throwsArith(() -> ConvChk.AddChkU64(-1L, 1L)));
          System.out.println(ConvChk.SubChkU64(20L, 10L) == 10L && throwsArith(() -> ConvChk.SubChkU64(5L, 10L)));
          System.out.println(ConvChk.MulChkU64(0L, -1L) == 0L && ConvChk.MulChkU64(10L, 20L) == 200L && throwsArith(() -> ConvChk.MulChkU64(0x100000000L, 0x100000000L)));
          System.out.println((ConvChk.WrapI64ToU8(0x1234L) & 0xFF) == 0x34 && ConvChk.WrapI64ToI8(255L) == -1);
          System.out.println(ConvChk.WrapI64ToI16(0xFFFFL) == -1 && ConvChk.WrapI64ToU16(0x12345L) == 0x2345);
          System.out.println(ConvChk.WrapU32ToI64(-1) == 0xFFFFFFFFL && ConvChk.WrapSame(99) == 99);
          System.out.println((ConvChk.TrapI32ToU8(200) & 0xFF) == 200 && throwsArith(() -> ConvChk.TrapI32ToU8(-1)) && throwsArith(() -> ConvChk.TrapI32ToU8(256)));
          System.out.println(ConvChk.TrapI32ToI8(-128) == -128 && throwsArith(() -> ConvChk.TrapI32ToI8(-129)) && throwsArith(() -> ConvChk.TrapI32ToI8(128)));
          System.out.println(ConvChk.TrapI32ToU32(5) == 5 && throwsArith(() -> ConvChk.TrapI32ToU32(-1)));
          System.out.println(ConvChk.TrapU32ToI32(5) == 5 && throwsArith(() -> ConvChk.TrapU32ToI32(-1)));
          System.out.println(ConvChk.TrapI64ToU64(5L) == 5L && throwsArith(() -> ConvChk.TrapI64ToU64(-1L)));
          System.out.println(ConvChk.TrapU64ToI64(5L) == 5L && throwsArith(() -> ConvChk.TrapU64ToI64(-1L)));
          System.out.println(ConvChk.TrapI64ToI32(5L) == 5 && throwsArith(() -> ConvChk.TrapI64ToI32(Long.MAX_VALUE)) && throwsArith(() -> ConvChk.TrapI64ToI32(Long.MIN_VALUE)));
          System.out.println(ConvChk.U32ToF64(-1) == 4294967295.0 && ConvChk.I64ToF32(100L) == 100.0f && ConvChk.I32ToF64(-5) == -5.0);
          System.out.println(ConvChk.U64ToF64(42L) == 42.0 && ConvChk.U64ToF64(-1L) == 18446744073709551616.0);
          System.out.println(ConvChk.U64ToF32(42L) == 42.0f && ConvChk.U64ToF32(-1L) == 18446744073709551616.0f);
          System.out.println(ConvChk.F32ToF64(1.5f) == 1.5 && ConvChk.F64ToF32(1.5) == 1.5f);
          System.out.println(ConvChk.SatF32ToI32(Float.NaN) == 0 && ConvChk.SatF32ToI32(1e20f) == Integer.MAX_VALUE);
          System.out.println(ConvChk.SatF64ToI64(-1e30) == Long.MIN_VALUE && (ConvChk.SatF64ToU8(500.0) & 0xFF) == 255 && (ConvChk.SatF64ToU8(-5.0) & 0xFF) == 0);
          System.out.println(ConvChk.SatF64ToU64(-1.0) == 0L && ConvChk.SatF64ToU64(Double.NaN) == 0L && ConvChk.SatF64ToU64(1e30) == -1L);
          System.out.println(ConvChk.SatF64ToU64(100.5) == 100L && ConvChk.SatF64ToU64(9223372036854775808.0) == Long.MIN_VALUE);
          System.out.println(ConvChk.TrapF64ToI32(-2147483648.9) == Integer.MIN_VALUE && throwsArith(() -> ConvChk.TrapF64ToI32(Double.NaN)) && throwsArith(() -> ConvChk.TrapF64ToI32(-2147483649.0)) && throwsArith(() -> ConvChk.TrapF64ToI32(2147483648.0)));
          System.out.println(ConvChk.TrapF64ToI64(-9223372036854775808.0) == Long.MIN_VALUE && throwsArith(() -> ConvChk.TrapF64ToI64(9223372036854775808.0)));
          System.out.println(ConvChk.TrapF64ToU64(0.0) == 0L && throwsArith(() -> ConvChk.TrapF64ToU64(-1.0)) && throwsArith(() -> ConvChk.TrapF64ToU64(18446744073709551616.0)));
        }
      }
      """);
    foreach (var line in output.Split('\n'))
      Assert.That(line, Is.EqualTo("true"), output);
  }

  [Test]
  public void AllPrimitiveArrays_AndAllOptionalBoxedTypes_ExecuteCorrectly() {
    var bArr = L("b", IrType.Array(IrType.I8));
    var sArr = L("s", IrType.Array(IrType.I16));
    var cArr = L("c", IrType.Array(IrType.U16));
    var lArr = L("l", IrType.Array(IrType.I64));
    var fArr = L("f", IrType.Array(IrType.F32));
    var dArr = L("d", IrType.Array(IrType.F64));
    var optZ = L("oz", IrType.Optional(IrType.Bool));
    var optB = L("ob", IrType.Optional(IrType.U8));
    var optS = L("os", IrType.Optional(IrType.I16));
    var optC = L("oc", IrType.Optional(IrType.U16));
    var optL = L("ol", IrType.Optional(IrType.I64));
    var optF = L("of", IrType.Optional(IrType.F32));
    var optD = L("od", IrType.Optional(IrType.F64));
    var ex = L("ex", IrType.Object(IrExceptionClass.ArithmeticException));
    var x = L("x", IrType.I32);

    var module = new IrModule("ArrOpt", [
      Function("AllArrays", IrType.F64, [],
        new IrDeclare(bArr, new IrNewArray(IrType.Array(IrType.I8), I(1))),
        new IrStore(R(bArr), I(0), C(IrType.I8, 1)),
        new IrDeclare(sArr, new IrNewArray(IrType.Array(IrType.I16), I(1))),
        new IrStore(R(sArr), I(0), C(IrType.I16, -5)),
        new IrDeclare(cArr, new IrNewArray(IrType.Array(IrType.U16), I(1))),
        new IrStore(R(cArr), I(0), C(IrType.U16, 10)),
        new IrDeclare(lArr, new IrNewArray(IrType.Array(IrType.I64), I(1))),
        new IrStore(R(lArr), I(0), I64(20)),
        new IrDeclare(fArr, new IrNewArray(IrType.Array(IrType.F32), I(1))),
        new IrStore(R(fArr), I(0), F32(2.5f)),
        new IrDeclare(dArr, new IrNewArray(IrType.Array(IrType.F64), I(1))),
        new IrStore(R(dArr), I(0), F64(3.5)),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConvert(IrType.F64, new IrArrayLoad(R(bArr), I(0))),
          Bin(IrBinaryOp.Add,
            new IrConvert(IrType.F64, new IrArrayLoad(R(sArr), I(0))),
            Bin(IrBinaryOp.Add,
              new IrConvert(IrType.F64, new IrArrayLoad(R(cArr), I(0))),
              Bin(IrBinaryOp.Add,
                new IrConvert(IrType.F64, new IrArrayLoad(R(lArr), I(0))),
                Bin(IrBinaryOp.Add,
                  new IrConvert(IrType.F64, new IrArrayLoad(R(fArr), I(0))),
                  new IrArrayLoad(R(dArr), I(0))))))))),
      Function("AllOptionals", IrType.F64, [optZ, optB, optS, optC, optL, optF, optD],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConditional(new IrUnwrap(R(optZ)), F64(1.0), F64(0.0)),
          Bin(IrBinaryOp.Add,
            new IrConvert(IrType.F64, new IrUnwrap(R(optB))),
            Bin(IrBinaryOp.Add,
              new IrConvert(IrType.F64, new IrUnwrap(R(optS))),
              Bin(IrBinaryOp.Add,
                new IrConvert(IrType.F64, new IrUnwrap(R(optC))),
                Bin(IrBinaryOp.Add,
                  new IrConvert(IrType.F64, new IrUnwrap(R(optL))),
                  Bin(IrBinaryOp.Add,
                    new IrConvert(IrType.F64, new IrUnwrap(R(optF))),
                    new IrUnwrap(R(optD)))))))))),
      Function("CatchFilterNoFinally", IrType.I32, [x],
        new IrTry(
          Block(new IrReturn(Bin(IrBinaryOp.Divide, I(10), R(x)))),
          [new IrCatch(IrExceptionClass.ArithmeticException, ex, Bin(IrBinaryOp.Equal, R(x), I(0)), Block(new IrReturn(I(88))))],
          null))
    ]);

    var emission = JvmEmitter.EmitTarget(module);
    Assert.That(emission.Applications, Is.Empty);
    var output = RunHarness(emission.Files, """
      public class Harness {
        public static void main(String[] args) {
          // 1 - 5 + 10 + 20 + 2.5 + 3.5 = 32.0
          System.out.println(ArrOpt.AllArrays() == 32.0);
          // 1 + 255 + (-5) + 10 + 20 + 2.5 + 3.5 = 287.0
          System.out.println(ArrOpt.AllOptionals(Boolean.TRUE, (byte)0xFF, (short)-5, (char)10, 20L, 2.5f, 3.5) == 287.0);
          boolean threwNullOpt = false;
          try { ArrOpt.AllOptionals(null, (byte)0, (short)0, (char)0, 0L, 0f, 0.0); } catch (IllegalStateException e) { threwNullOpt = true; }
          System.out.println(threwNullOpt);
          System.out.println(ArrOpt.CatchFilterNoFinally(0) == 88 && ArrOpt.CatchFilterNoFinally(2) == 5);
        }
      }
      """);
    foreach (var line in output.Split('\n'))
      Assert.That(line, Is.EqualTo("true"), output);
  }

}
