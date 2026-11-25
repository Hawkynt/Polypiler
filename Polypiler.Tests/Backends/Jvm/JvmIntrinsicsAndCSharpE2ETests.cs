using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Jvm;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Jvm.JvmTestRunner;

namespace Polypiler.Tests.Backends.Jvm;

[TestFixture]
public sealed class JvmIntrinsicsAndCSharpE2ETests {

  [Test]
  public void Intrinsics_Math_Bits_Endian_Arrays_AndStrings_ExecuteCorrectly() {
    var i32 = L("x", IrType.I32);
    var y32 = L("y", IrType.I32);
    var u32 = L("u", IrType.U32);
    var v32 = L("v", IrType.U32);
    var i64 = L("x", IrType.I64);
    var y64 = L("y", IrType.I64);
    var u64 = L("u", IrType.U64);
    var v64 = L("v", IrType.U64);
    var u8 = L("b", IrType.U8);
    var i16 = L("s", IrType.I16);
    var u16 = L("c", IrType.U16);
    var f32 = L("f", IrType.F32);
    var g32 = L("g", IrType.F32);
    var f64 = L("d", IrType.F64);
    var e64 = L("e", IrType.F64);
    var buf = L("buf", IrType.Array(IrType.U8));
    var idx = L("idx", IrType.I32);
    var s1 = L("s1", IrType.String);
    var s2 = L("s2", IrType.String);

    var module = new IrModule("Intr", [
      // Abs
      Function("AbsI32", IrType.I32, [i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.I32, [R(i32)]))),
      Function("AbsI64", IrType.I64, [i64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.I64, [R(i64)]))),
      Function("AbsU32", IrType.U32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.U32, [R(u32)]))),
      Function("AbsF32", IrType.F32, [f32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F32, [R(f32)]))),
      Function("AbsF64", IrType.F64, [f64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F64, [R(f64)]))),
      // Min / Max
      Function("MinI32", IrType.I32, [i32, y32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Min, IrType.I32, [R(i32), R(y32)]))),
      Function("MaxI64", IrType.I64, [i64, y64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Max, IrType.I64, [R(i64), R(y64)]))),
      Function("MinU32", IrType.U32, [u32, v32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Min, IrType.U32, [R(u32), R(v32)]))),
      Function("MaxU64", IrType.U64, [u64, v64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Max, IrType.U64, [R(u64), R(v64)]))),
      Function("MinF32", IrType.F32, [f32, g32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Min, IrType.F32, [R(f32), R(g32)]))),
      Function("MaxF64", IrType.F64, [f64, e64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Max, IrType.F64, [R(f64), R(e64)]))),
      // Sign, Sqrt, Floor, Ceiling, Truncate, RoundEven, RoundAway, CopySign, FloatBits/BitsFloat
      Function("SignF32", IrType.I32, [f32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(f32)]))),
      Function("SignF64", IrType.I32, [f64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(f64)]))),
      Function("SqrtF32", IrType.F32, [f32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F32, [R(f32)]))),
      Function("SqrtF64", IrType.F64, [f64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [R(f64)]))),
      Function("FloorF64", IrType.F64, [f64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Floor, IrType.F64, [R(f64)]))),
      Function("CeilF64", IrType.F64, [f64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Ceiling, IrType.F64, [R(f64)]))),
      Function("TruncF32", IrType.F32, [f32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Truncate, IrType.F32, [R(f32)]))),
      Function("TruncF64", IrType.F64, [f64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Truncate, IrType.F64, [R(f64)]))),
      Function("RoundEvenF64", IrType.F64, [f64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundEven, IrType.F64, [R(f64)]))),
      Function("RoundAwayF32", IrType.F32, [f32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundAway, IrType.F32, [R(f32)]))),
      Function("RoundAwayF64", IrType.F64, [f64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundAway, IrType.F64, [R(f64)]))),
      Function("CopySignF32", IrType.F32, [f32, g32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.CopySign, IrType.F32, [R(f32), R(g32)]))),
      Function("CopySignF64", IrType.F64, [f64, e64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.CopySign, IrType.F64, [R(f64), R(e64)]))),
      Function("BitsRoundTripF32", IrType.F32, [f32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F32, [new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I32, [R(f32)])]))),
      Function("BitsRoundTripF64", IrType.F64, [f64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F64, [new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I64, [R(f64)])]))),
      // Float predicates (f32 and f64)
      Function("PredF32", IrType.I32, [f32],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, [R(f32)]), I(1), I(0)),
          Bin(IrBinaryOp.Add,
            new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, [R(f32)]), I(2), I(0)),
            Bin(IrBinaryOp.Add,
              new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsFinite, IrType.Bool, [R(f32)]), I(4), I(0)),
              Bin(IrBinaryOp.Add,
                new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsNormal, IrType.Bool, [R(f32)]), I(8), I(0)),
                Bin(IrBinaryOp.Add,
                  new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsSubnormal, IrType.Bool, [R(f32)]), I(16), I(0)),
                  new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsNegative, IrType.Bool, [R(f32)]), I(32), I(0))))))))),
      Function("PredF64", IrType.I32, [f64],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, [R(f64)]), I(1), I(0)),
          Bin(IrBinaryOp.Add,
            new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, [R(f64)]), I(2), I(0)),
            Bin(IrBinaryOp.Add,
              new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsFinite, IrType.Bool, [R(f64)]), I(4), I(0)),
              Bin(IrBinaryOp.Add,
                new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsNormal, IrType.Bool, [R(f64)]), I(8), I(0)),
                Bin(IrBinaryOp.Add,
                  new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsSubnormal, IrType.Bool, [R(f64)]), I(16), I(0)),
                  new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsNegative, IrType.Bool, [R(f64)]), I(32), I(0))))))))),
      // Bit intrinsics (wide + narrow)
      Function("Rotl32", IrType.U32, [u32, y32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RotateLeft, IrType.U32, [R(u32), R(y32)]))),
      Function("Rotr64", IrType.U64, [u64, y32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RotateRight, IrType.U64, [R(u64), R(y32)]))),
      Function("RotlU8", IrType.U8, [u8, y32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RotateLeft, IrType.U8, [R(u8), R(y32)]))),
      Function("RotrU16", IrType.U16, [u16, y32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RotateRight, IrType.U16, [R(u16), R(y32)]))),
      Function("Lzcnt32", IrType.I32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.LeadingZeroCount, IrType.I32, [R(u32)]))),
      Function("Lzcnt64", IrType.I32, [u64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.LeadingZeroCount, IrType.I32, [R(u64)]))),
      Function("LzcntU8", IrType.I32, [u8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.LeadingZeroCount, IrType.I32, [R(u8)]))),
      Function("Tzcnt32", IrType.I32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.TrailingZeroCount, IrType.I32, [R(u32)]))),
      Function("Tzcnt64", IrType.I32, [u64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.TrailingZeroCount, IrType.I32, [R(u64)]))),
      Function("TzcntU8", IrType.I32, [u8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.TrailingZeroCount, IrType.I32, [R(u8)]))),
      Function("Pop32", IrType.I32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [R(u32)]))),
      Function("Pop64", IrType.I32, [u64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [R(u64)]))),
      Function("PopU8", IrType.I32, [u8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [R(u8)]))),
      Function("SwapU8", IrType.U8, [u8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.U8, [R(u8)]))),
      Function("SwapI16", IrType.I16, [i16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.I16, [R(i16)]))),
      Function("SwapU16", IrType.U16, [u16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.U16, [R(u16)]))),
      Function("SwapI32", IrType.I32, [i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.I32, [R(i32)]))),
      Function("SwapI64", IrType.I64, [i64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.I64, [R(i64)]))),
      Function("HighAndJoin", IrType.I64, [i64],
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.JoinWords, IrType.I64, [
          new IrIntrinsic(IrIntrinsicOp.HighWord, IrType.U32, [R(i64)]),
          new IrConvert(IrType.U32, R(i64))
        ]))),
      // Endian loads & stores (16/32/64/f32/f64)
      Function("StoreLoadLeI16", IrType.I16, [buf, idx, i16],
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(buf), R(idx), R(i16)])),
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.I16, [R(buf), R(idx)]))),
      Function("StoreLoadBeI32", IrType.I32, [buf, idx, i32],
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(buf), R(idx), R(i32)])),
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.I32, [R(buf), R(idx)]))),
      Function("StoreLoadLeF32", IrType.F32, [buf, idx, f32],
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(buf), R(idx), R(f32)])),
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.F32, [R(buf), R(idx)]))),
      Function("StoreLoadBeF64", IrType.F64, [buf, idx, f64],
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(buf), R(idx), R(f64)])),
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.F64, [R(buf), R(idx)]))),
      Function("StoreLoadLeI64", IrType.I64, [buf, idx, i64],
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(buf), R(idx), R(i64)])),
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.I64, [R(buf), R(idx)]))),
      // Arrays: NewArray, ArrayOf, ArrayCopy, ArrayFill, ArrayClear (numeric & string)
      Function("ArrayOps", IrType.I32, [i32],
        new IrDeclare(L("src", IrType.Array(IrType.I32)), new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(10), I(20), I(30)])),
        new IrDeclare(L("dst", IrType.Array(IrType.I32)), new IrNewArray(IrType.Array(IrType.I32), R(i32))),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [new IrLocalRef(L("dst", IrType.Array(IrType.I32))), I(7)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayClear, IrType.Void, [new IrLocalRef(L("dst", IrType.Array(IrType.I32)))])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [new IrLocalRef(L("src", IrType.Array(IrType.I32))), I(1), new IrLocalRef(L("dst", IrType.Array(IrType.I32))), I(0), I(2)])),
        new IrReturn(Bin(IrBinaryOp.Add, new IrArrayLoad(new IrLocalRef(L("dst", IrType.Array(IrType.I32))), I(0)), new IrArrayLoad(new IrLocalRef(L("dst", IrType.Array(IrType.I32))), I(1))))),
      Function("StrArrayOps", IrType.String, [],
        new IrDeclare(L("sa", IrType.Array(IrType.String)), new IrNewArray(IrType.Array(IrType.String), I(2))),
        new IrStore(new IrLocalRef(L("sa", IrType.Array(IrType.String))), I(0), new IrStrConst("hello")),
        new IrReturn(new IrArrayLoad(new IrLocalRef(L("sa", IrType.Array(IrType.String))), I(0)))),
      // Strings: StrLength, StrConcat, StrEquals, StrCodeUnitAt, StrSubstring (2 & 3), StrCompareOrdinal
      Function("StrOps", IrType.I32, [s1, s2],
        new IrDeclare(L("joined", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [R(s1), new IrStrConst("-"), R(s2)])),
        new IrDeclare(L("sub1", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [new IrLocalRef(L("joined", IrType.String)), I(2)])),
        new IrDeclare(L("sub2", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [new IrLocalRef(L("joined", IrType.String)), I(0), I(3)])),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrLocalRef(L("sub1", IrType.String))]),
          Bin(IrBinaryOp.Add,
            new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [new IrLocalRef(L("sub2", IrType.String)), I(0)])),
            Bin(IrBinaryOp.Add,
              new IrConditional(new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [R(s1), R(s2)]), I(1000), I(0)),
              new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [R(s1), R(s2)]))))))
    ]);

    // Fix local reference identity in ArrayOps/StrArrayOps/StrOps
    var src = L("src", IrType.Array(IrType.I32));
    var dst = L("dst", IrType.Array(IrType.I32));
    var sa = L("sa", IrType.Array(IrType.String));
    var joined = L("joined", IrType.String);
    var sub1 = L("sub1", IrType.String);
    var sub2 = L("sub2", IrType.String);
    var funcs = module.Functions.ToList();
    funcs[^3] = Function("ArrayOps", IrType.I32, [i32],
      new IrDeclare(src, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(10), I(20), I(30)])),
      new IrDeclare(dst, new IrNewArray(IrType.Array(IrType.I32), R(i32))),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [R(dst), I(7)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayClear, IrType.Void, [R(dst)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [R(src), I(1), R(dst), I(0), I(2)])),
      new IrReturn(Bin(IrBinaryOp.Add, new IrArrayLoad(R(dst), I(0)), new IrArrayLoad(R(dst), I(1)))));
    funcs[^2] = Function("StrArrayOps", IrType.String, [],
      new IrDeclare(sa, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.String), [new IrStrConst("a"), new IrStrConst("b")])),
      new IrStore(R(sa), I(0), new IrStrConst("hello")),
      new IrReturn(new IrArrayLoad(R(sa), I(0))));
    funcs[^1] = Function("StrOps", IrType.I32, [s1, s2],
      new IrDeclare(joined, new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [R(s1), new IrStrConst("-"), R(s2)])),
      new IrDeclare(sub1, new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [R(joined), I(2)])),
      new IrDeclare(sub2, new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [R(joined), I(0), I(3)])),
      new IrReturn(Bin(IrBinaryOp.Add,
        new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [R(sub1)]),
        Bin(IrBinaryOp.Add,
          new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [R(sub2), I(0)])),
          Bin(IrBinaryOp.Add,
            new IrConditional(new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [R(s1), R(s2)]), I(1000), I(0)),
            new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [R(s1), R(s2)]))))));
    module = module with { Functions = funcs };

    var files = JvmEmitter.Emit(module);
    var output = RunHarness(files, """
      public class Harness {
        static boolean throwsArith(Runnable r) {
          try { r.run(); return false; } catch (ArithmeticException e) { return true; }
        }
        static boolean throwsOob(Runnable r) {
          try { r.run(); return false; } catch (IndexOutOfBoundsException e) { return true; }
        }
        public static void main(String[] args) {
          System.out.println(Intr.AbsI32(-42) == 42 && throwsArith(() -> Intr.AbsI32(Integer.MIN_VALUE)));
          System.out.println(Intr.AbsI64(-42L) == 42L && throwsArith(() -> Intr.AbsI64(Long.MIN_VALUE)));
          System.out.println(Intr.AbsU32(-1) == -1 && Intr.AbsF32(-3.5f) == 3.5f && Intr.AbsF64(-3.5) == 3.5);
          System.out.println(Intr.MinI32(3, -1) == -1 && Intr.MaxI64(3L, 9L) == 9L);
          System.out.println(Intr.MinU32(1, -1) == 1 && Intr.MaxU64(1L, -1L) == -1L);
          System.out.println(Intr.MinF32(1.0f, 2.0f) == 1.0f && Intr.MaxF64(1.0, 2.0) == 2.0);
          System.out.println(Intr.SignF32(-2.0f) == -1 && Intr.SignF32(0.0f) == 0 && Intr.SignF32(2.0f) == 1 && throwsArith(() -> Intr.SignF64(Double.NaN)));
          System.out.println(Intr.SqrtF32(9.0f) == 3.0f && Intr.SqrtF64(16.0) == 4.0);
          System.out.println(Intr.FloorF64(2.9) == 2.0 && Intr.CeilF64(2.1) == 3.0);
          System.out.println(Intr.TruncF32(-2.9f) == -2.0f && Intr.TruncF64(2.9) == 2.0 && Intr.TruncF64(-2.9) == -2.0);
          System.out.println(Intr.RoundEvenF64(2.5) == 2.0 && Intr.RoundEvenF64(3.5) == 4.0);
          System.out.println(Intr.RoundAwayF32(2.5f) == 3.0f && Intr.RoundAwayF64(-2.5) == -3.0 && Intr.RoundAwayF64(2.4) == 2.0 && Intr.RoundAwayF64(-2.4) == -2.0);
          System.out.println(Intr.CopySignF32(5.0f, -1.0f) == -5.0f && Intr.CopySignF64(-5.0, 1.0) == 5.0);
          System.out.println(Intr.BitsRoundTripF32(1.25f) == 1.25f && Intr.BitsRoundTripF64(1.25) == 1.25);
          // Predicates: 1=NaN, 2=Inf, 4=Finite, 8=Normal, 16=Subnormal, 32=Negative
          System.out.println(Intr.PredF32(Float.NaN) == 1 && Intr.PredF32(Float.NEGATIVE_INFINITY) == (2|32) && Intr.PredF32(1.0f) == (4|8) && Intr.PredF32(Float.MIN_VALUE) == (4|16) && Intr.PredF32(-0.0f) == (4|32));
          System.out.println(Intr.PredF64(Double.NaN) == 1 && Intr.PredF64(Double.POSITIVE_INFINITY) == 2 && Intr.PredF64(-2.0) == (4|8|32) && Intr.PredF64(Double.MIN_VALUE) == (4|16) && Intr.PredF64(0.0) == 4);
          System.out.println(Intr.Rotl32(0x80000001, 1) == 3 && Intr.Rotr64(1L, 1) == Long.MIN_VALUE);
          System.out.println((Intr.RotlU8((byte)0x81, 1) & 0xFF) == 0x03 && Intr.RotrU16((char)0x0001, 1) == 0x8000);
          System.out.println(Intr.Lzcnt32(1) == 31 && Intr.Lzcnt64(1L) == 63 && Intr.LzcntU8((byte)1) == 7);
          System.out.println(Intr.Tzcnt32(8) == 3 && Intr.Tzcnt64(8L) == 3 && Intr.TzcntU8((byte)0) == 8);
          System.out.println(Intr.Pop32(0xF0F0) == 8 && Intr.Pop64(0xFF00FFL) == 16 && Intr.PopU8((byte)0xFF) == 8);
          System.out.println((Intr.SwapU8((byte)0xAB) & 0xFF) == 0xAB && Intr.SwapI16((short)0x1234) == 0x3412 && Intr.SwapU16((char)0x1234) == 0x3412);
          System.out.println(Intr.SwapI32(0x12345678) == 0x78563412 && Intr.SwapI64(0x0102030405060708L) == 0x0807060504030201L);
          System.out.println(Intr.HighAndJoin(0x123456789ABCDEF0L) == 0x123456789ABCDEF0L);
          byte[] b = new byte[16];
          System.out.println(Intr.StoreLoadLeI16(b, 0, (short)-12345) == -12345 && Intr.StoreLoadBeI32(b, 2, 0x12345678) == 0x12345678);
          System.out.println(Intr.StoreLoadLeF32(b, 0, 3.25f) == 3.25f && Intr.StoreLoadBeF64(b, 4, -12.5) == -12.5 && Intr.StoreLoadLeI64(b, 8, 0x0102030405060708L) == 0x0102030405060708L);
          System.out.println(throwsOob(() -> Intr.StoreLoadLeI16(b, -1, (short)0)) && throwsOob(() -> Intr.StoreLoadBeF64(b, 12, 0.0)));
          System.out.println(Intr.ArrayOps(2) == 50 && throwsArith(() -> Intr.ArrayOps(-1)) && Intr.StrArrayOps().equals("hello"));
          // "abc-abc" -> sub1="c-abc"(5), sub2="abc"('a'=97), eq=true(1000), cmp=0 => 1102
          System.out.println(Intr.StrOps("abc", "abc") == 1102 && Intr.StrOps("abc", "def") == (5 + 97 - 1));
        }
      }
      """);
    foreach (var line in output.Split('\n'))
      Assert.That(line, Is.EqualTo("true"), output);
  }

  [Test]
  public void Legalize_AndCSharpFrontendToJvmJar_ExecuteLiveUnderJavaVerifyAll() {
    const string csharpSource = """
      using System;
      public static class DemoAlgorithms {
        public static int Factorial(int n) {
          int acc = 1;
          for (int i = 2; i <= n; i++)
            acc *= i;
          return acc;
        }

        public static uint Adler32(byte[] data) {
          uint a = 1;
          uint b = 0;
          for (int i = 0; i < data.Length; i++) {
            a = (a + data[i]) % 65521u;
            b = (b + a) % 65521u;
          }
          return (b << 16) | a;
        }

        public static double TrigAndClamp(double x) {
          return Math.Sin(x) + Math.Cos(x);
        }

        public static int SafeDivide(int a, int b) {
          try {
            return a / b;
          } catch (DivideByZeroException) {
            return -999;
          }
        }
      }
      """;

    var imported = CSharpFrontend.Import("DemoAlgorithms.cs", csharpSource);
    Assert.That(imported.Diagnostics, Is.Empty);

    var legalizedAccurate = JvmEmitter.Legalize(imported.Module!, () => PrecomputedImplementations.Module, JvmProfile.Java21, FloatMode.Accurate);
    var legalizedStrict = JvmEmitter.Legalize(imported.Module!, () => PrecomputedImplementations.Module, JvmProfile.Java8, FloatMode.Strict);
    Assert.That(JvmEmitter.Check(legalizedAccurate), Is.Empty);
    Assert.That(JvmEmitter.Check(legalizedStrict, JvmProfile.Java8), Is.Empty);

    // Also test integer library legalization (e.g., BitReverse + DivFloor)
    var x = L("x", IrType.I32);
    var y = L("y", IrType.I32);
    var intLibMod = new IrModule("IntLib", [
      Function("RevBits", IrType.I32, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.I32, [R(x)]))),
      Function("FloorDiv", IrType.I32, [x, y], new IrReturn(new IrIntrinsic(IrIntrinsicOp.DivFloor, IrType.I32, [R(x), R(y)])))
    ]);
    var legalizedInt = JvmEmitter.Legalize(intLibMod, () => PrecomputedImplementations.Module);

    var jarFiles = JvmEmitter.Emit(legalizedAccurate);
    var intFiles = JvmEmitter.Emit(legalizedInt, JvmProfile.Java17);
    var allFiles = jarFiles.Concat(intFiles).ToList();

    var output = RunHarness(allFiles, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(DemoAlgorithms.Factorial(10) == 3628800);
          byte[] msg = "Wikipedia".getBytes(java.nio.charset.StandardCharsets.US_ASCII);
          System.out.println(DemoAlgorithms.Adler32(msg) == 0x11E60398);
          double t = DemoAlgorithms.TrigAndClamp(0.0);
          System.out.println(Math.abs(t - 1.0) < 1e-12);
          System.out.println(DemoAlgorithms.SafeDivide(20, 4) == 5 && DemoAlgorithms.SafeDivide(20, 0) == -999);
          System.out.println(IntLib.RevBits(0x80000000) == 1 && IntLib.FloorDiv(-7, 2) == -4);
        }
      }
      """);
    foreach (var line in output.Split('\n'))
      Assert.That(line, Is.EqualTo("true"), output);
  }

  [Test]
  public void CheckAndEmit_RefuseUnsupportedTypesConstructsIntrinsicsAndInvalidIr() {
    // 1. Null guards
    Assert.That(() => JvmEmitter.Check(null!), Throws.ArgumentNullException);
    Assert.That(() => JvmEmitter.Emit(null!), Throws.ArgumentNullException);
    Assert.That(() => JvmEmitter.Legalize(null!, () => PrecomputedImplementations.Module), Throws.ArgumentNullException);
    Assert.That(() => JvmEmitter.Legalize(new IrModule("M", []), null!), Throws.ArgumentNullException);

    // 2. Ill-typed IR fails verifier in both Check and Emit
    var illTyped = new IrModule("Bad", [
      Function("F", IrType.I32, [], new IrReturn(B(true)))
    ]);
    Assert.That(JvmEmitter.Check(illTyped), Is.Not.Empty);
    Assert.That(() => JvmEmitter.Emit(illTyped), Throws.ArgumentException);

    // 3. Unsupported declared struct type + foreign function + unsupported statement (IrYield) + unsupported expression (IrStructDefault) + unsupported intrinsic (RegexTest & LoadLittle on view)
    var st = new IrStructType("Point");
    st.Define([new IrField("X", IrType.I32, false)]);
    var vLoc = L("v", IrType.Span(IrType.U8));

    var unsupportedMod = new IrModule("Unsupported", [
      Function("UseStruct", IrType.Struct(st), [], new IrReturn(new IrStructDefault(IrType.Struct(st)))),
      Function("UseYield", IrType.Sequence(IrType.I32, true), [], new IrYield(I(1))),
      Function("UseRegex", IrType.Bool, [], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RegexTest, IrType.Bool, [new IrStrConst("a"), new IrStrConst("a"), new IrStrConst("")]))),
      Function("UseViewEndian", IrType.I32, [vLoc], new IrReturn(new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.I32, [R(vLoc), I(0)])))
    ]) {
      Types = [st],
      ForeignFunctions = [new IrForeignFunction("ext_fn", true, "lib", IrType.Void, [])]
    };

    var diagnostics = JvmEmitter.Check(unsupportedMod);
    Assert.That(diagnostics.Select(d => d.Code), Has.Member(JvmEmitter.UnsupportedTypeCode));
    Assert.That(diagnostics.Select(d => d.Code), Has.Member(JvmEmitter.UnsupportedConstructCode));
    Assert.That(diagnostics.Select(d => d.Code), Has.Member(JvmEmitter.UnsupportedIntrinsicCode));
    Assert.That(() => JvmEmitter.Emit(unsupportedMod), Throws.ArgumentException);
  }

}
