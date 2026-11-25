using System;
using NUnit.Framework;
using Polypiler.Core.Backends.Cpp;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Cpp.CppTestHarness;

namespace Polypiler.Tests.Backends.Cpp;

/// <summary>
/// Live g++ (-std=c++17 / -std=c++20 / -std=c++23 -Wall -Wextra -Werror) execution tests verifying
/// exact parity with <see cref="IrInterpreter.Call"/> for wrapping &amp; checked arithmetic, division,
/// shifts, floats, conversions, bit ops, steps, and legalized math/integer intrinsics.
/// </summary>
[TestFixture]
public sealed class CppScalarAndBitOpsTests {

  [Test]
  public void SignedAndUnsignedArithmetic_WrappingCheckedDivAndShifts_ParityAcrossAllStandards() {
    var a = L("a", IrType.I32);
    var b = L("b", IrType.I32);
    var a64 = L("a", IrType.I64);
    var b64 = L("b", IrType.I64);
    var ua = L("a", IrType.U32);
    var ub = L("b", IrType.U32);
    var u8a = L("a", IrType.U8);
    var u8b = L("b", IrType.U8);

    var mod = new IrModule("Arith", [
      Function("wrapAdd", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.Add, R(a), R(b)))),
      Function("wrapSub", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.Subtract, R(a), R(b)))),
      Function("wrapMul", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.Multiply, R(a), R(b)))),
      Function("wrapNeg", IrType.I32, [a], new IrReturn(new IrUnary(IrUnaryOp.Negate, R(a)))),
      Function("wrapAdd64", IrType.I64, [a64, b64], new IrReturn(Bin(IrBinaryOp.Add, R(a64), R(b64)))),
      Function("wrapNeg64", IrType.I64, [a64], new IrReturn(new IrUnary(IrUnaryOp.Negate, R(a64)))),
      Function("chkAdd", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.AddChecked, R(a), R(b)))),
      Function("chkSub", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.SubtractChecked, R(a), R(b)))),
      Function("chkMul", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.MultiplyChecked, R(a), R(b)))),
      Function("chkNeg", IrType.I32, [a], new IrReturn(new IrUnary(IrUnaryOp.NegateChecked, R(a)))),
      Function("chkUAdd", IrType.U32, [ua, ub], new IrReturn(Bin(IrBinaryOp.AddChecked, R(ua), R(ub)))),
      Function("chkUSub", IrType.U32, [ua, ub], new IrReturn(Bin(IrBinaryOp.SubtractChecked, R(ua), R(ub)))),
      Function("chkUMul", IrType.U32, [ua, ub], new IrReturn(Bin(IrBinaryOp.MultiplyChecked, R(ua), R(ub)))),
      Function("sdiv", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.Divide, R(a), R(b)))),
      Function("srem", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.Remainder, R(a), R(b)))),
      Function("udiv", IrType.U32, [ua, ub], new IrReturn(Bin(IrBinaryOp.Divide, R(ua), R(ub)))),
      Function("shl", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.ShiftLeft, R(a), R(b)))),
      Function("ashr", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.ShiftRight, R(a), R(b)))),
      Function("lshr", IrType.I32, [a, b], new IrReturn(Bin(IrBinaryOp.ShiftRightLogical, R(a), R(b)))),
      Function("bitops", IrType.I32, [a, b], new IrReturn(
        Bin(IrBinaryOp.Xor, Bin(IrBinaryOp.Or, Bin(IrBinaryOp.And, R(a), R(b)), new IrUnary(IrUnaryOp.BitNot, R(a))), R(b)))),
      Function("narrowWrap", IrType.U8, [u8a, u8b], new IrReturn(new IrConvert(IrType.U8, Bin(IrBinaryOp.Add, new IrConvert(IrType.I32, R(u8a)), new IrConvert(IrType.I32, R(u8b)))))),
      Function("logic", IrType.Bool, [a, b], new IrReturn(
        Bin(IrBinaryOp.LogicalOr, Bin(IrBinaryOp.LogicalAnd, Bin(IrBinaryOp.Less, R(a), R(b)), new IrUnary(IrUnaryOp.LogicalNot, Bin(IrBinaryOp.Equal, R(a), I(0)))), Bin(IrBinaryOp.GreaterOrEqual, R(a), R(b)))))
    ]);

    // Check IR interpreter reference values
    Assert.That(Eval(mod, "wrapAdd", int.MaxValue, 1), Is.EqualTo(int.MinValue));
    Assert.That(Eval(mod, "wrapSub", int.MinValue, 1), Is.EqualTo(int.MaxValue));
    Assert.That(Eval(mod, "wrapMul", int.MaxValue, 2), Is.EqualTo(-2));
    Assert.That(Eval(mod, "wrapNeg", int.MinValue), Is.EqualTo(int.MinValue));
    Assert.That(Eval(mod, "wrapAdd64", long.MaxValue, 1L), Is.EqualTo(long.MinValue));
    Assert.That(Eval(mod, "wrapNeg64", long.MinValue), Is.EqualTo(long.MinValue));
    Assert.That(Eval(mod, "sdiv", -7, 2), Is.EqualTo(-3));
    Assert.That(Eval(mod, "srem", -7, 2), Is.EqualTo(-1));
    Assert.That(Eval(mod, "shl", -1, 33), Is.EqualTo(-2));
    Assert.That(Eval(mod, "ashr", -8, 1), Is.EqualTo(-4));
    Assert.That(Eval(mod, "lshr", -1, 24), Is.EqualTo(255));
    Assert.That(Eval(mod, "bitops", 0x0F, 0x33), Is.EqualTo(((0x0F & 0x33) | ~0x0F) ^ 0x33));

    var driver = """
      #include "arith.hpp"
      #include <cassert>
      #include <climits>
      #include <stdexcept>
      using namespace arith;
      template <typename Ex, typename Fn>
      void expect_throw(Fn fn) {
        bool caught = false;
        try { (void)fn(); } catch (const Ex&) { caught = true; }
        assert(caught);
      }
      int main() {
        assert(wrap_add(INT32_MAX, 1) == INT32_MIN);
        assert(wrap_sub(INT32_MIN, 1) == INT32_MAX);
        assert(wrap_mul(INT32_MAX, 2) == -2);
        assert(wrap_neg(INT32_MIN) == INT32_MIN);
        assert(wrap_add64(INT64_MAX, 1) == INT64_MIN);
        assert(wrap_neg64(INT64_MIN) == INT64_MIN);

        assert(chk_add(10, 20) == 30);
        expect_throw<std::overflow_error>([] { return chk_add(INT32_MAX, 1); });
        expect_throw<std::overflow_error>([] { return chk_add(INT32_MIN, -1); });
        assert(chk_sub(10, 20) == -10);
        expect_throw<std::overflow_error>([] { return chk_sub(INT32_MIN, 1); });
        expect_throw<std::overflow_error>([] { return chk_sub(INT32_MAX, -1); });
        assert(chk_mul(-3, 4) == -12);
        assert(chk_mul(-3, -4) == 12);
        expect_throw<std::overflow_error>([] { return chk_mul(INT32_MAX, 2); });
        expect_throw<std::overflow_error>([] { return chk_mul(INT32_MIN, -1); });
        assert(chk_neg(5) == -5);
        expect_throw<std::overflow_error>([] { return chk_neg(INT32_MIN); });

        assert(chk_u_add(1u, 2u) == 3u);
        expect_throw<std::overflow_error>([] { return chk_u_add(UINT32_MAX, 1u); });
        assert(chk_u_sub(5u, 3u) == 2u);
        expect_throw<std::overflow_error>([] { return chk_u_sub(3u, 5u); });
        assert(chk_u_mul(10u, 20u) == 200u);
        expect_throw<std::overflow_error>([] { return chk_u_mul(UINT32_MAX, 2u); });

        assert(sdiv(-7, 2) == -3);
        assert(srem(-7, 2) == -1);
        assert(udiv(10u, 3u) == 3u);
        expect_throw<std::domain_error>([] { return sdiv(1, 0); });
        expect_throw<std::overflow_error>([] { return sdiv(INT32_MIN, -1); });
        expect_throw<std::domain_error>([] { return srem(1, 0); });
        expect_throw<std::overflow_error>([] { return srem(INT32_MIN, -1); });

        assert(shl(-1, 33) == -2);
        assert(ashr(-8, 1) == -4);
        assert(ashr(-8, 0) == -8);
        assert(ashr(8, 1) == 4);
        assert(lshr(-1, 24) == 255);
        assert(bitops(0x0F, 0x33) == (((0x0F & 0x33) | ~0x0F) ^ 0x33));
        assert(narrow_wrap(200, 100) == 44);
        assert(logic(1, 2) == true);
        assert(logic(0, 2) == false);
        assert(logic(3, 2) == true);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void FloatingPointConversionsIntrinsicsAndBitOps_ParityAcrossAllStandards() {
    var x = L("x", IrType.F64);
    var y = L("y", IrType.F64);
    var fx = L("x", IrType.F32);
    var u32 = L("u", IrType.U32);
    var i32 = L("i", IrType.I32);
    var i64 = L("l", IrType.I64);
    var u16 = L("s", IrType.U16);
    var u8 = L("b", IrType.U8);
    var hi = L("hi", IrType.U32);
    var lo = L("lo", IrType.U32);

    var mod = new IrModule("MathBits", [
      Function("fops", IrType.F64, [x, y], new IrReturn(
        Bin(IrBinaryOp.Add, Bin(IrBinaryOp.Divide, Bin(IrBinaryOp.Multiply, R(x), R(y)), Bin(IrBinaryOp.Subtract, R(x), F64(1.0))), Bin(IrBinaryOp.Remainder, R(x), R(y))))),
      Function("fneg", IrType.F64, [x], new IrReturn(new IrUnary(IrUnaryOp.Negate, R(x)))),
      Function("fmin", IrType.F64, [x, y], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Min, IrType.F64, [R(x), R(y)]))),
      Function("fmax", IrType.F64, [x, y], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Max, IrType.F64, [R(x), R(y)]))),
      Function("imin", IrType.I32, [i32, L("j", IrType.I32)], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Min, IrType.I32, [R(i32), R(L("j", IrType.I32))]))),
      Function("imax", IrType.I32, [i32, L("j", IrType.I32)], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Max, IrType.I32, [R(i32), R(L("j", IrType.I32))]))),
      Function("iabs", IrType.I32, [i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.I32, [R(i32)]))),
      Function("uabs", IrType.U32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.U32, [R(u32)]))),
      Function("fabs", IrType.F64, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F64, [R(x)]))),
      Function("fsign", IrType.I32, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(x)]))),
      Function("isign", IrType.I32, [i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(i32)]))),
      Function("usign", IrType.I32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(u32)]))),
      Function("fsqrt", IrType.F64, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [R(x)]))),
      Function("fround", IrType.F64, [x], new IrReturn(
        Bin(IrBinaryOp.Add,
          Bin(IrBinaryOp.Add, new IrIntrinsic(IrIntrinsicOp.Floor, IrType.F64, [R(x)]), new IrIntrinsic(IrIntrinsicOp.Ceiling, IrType.F64, [R(x)])),
          Bin(IrBinaryOp.Add, new IrIntrinsic(IrIntrinsicOp.Truncate, IrType.F64, [R(x)]),
            Bin(IrBinaryOp.Add, new IrIntrinsic(IrIntrinsicOp.RoundEven, IrType.F64, [R(x)]), new IrIntrinsic(IrIntrinsicOp.RoundAway, IrType.F64, [R(x)])))))),
      Function("fcopysign", IrType.F64, [x, y], new IrReturn(new IrIntrinsic(IrIntrinsicOp.CopySign, IrType.F64, [R(x), R(y)]))),
      Function("fclassify", IrType.I32, [x], new IrReturn(
        Bin(IrBinaryOp.Add, new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, [R(x)]), I(1), I(0)),
        Bin(IrBinaryOp.Add, new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, [R(x)]), I(2), I(0)),
        Bin(IrBinaryOp.Add, new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsFinite, IrType.Bool, [R(x)]), I(4), I(0)),
        Bin(IrBinaryOp.Add, new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsNegative, IrType.Bool, [R(x)]), I(8), I(0)),
        Bin(IrBinaryOp.Add, new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsNormal, IrType.Bool, [R(x)]), I(16), I(0)),
        Bin(IrBinaryOp.Add, new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsSubnormal, IrType.Bool, [R(x)]), I(32), I(0)),
          new IrConditional(new IrIntrinsic(IrIntrinsicOp.IsInteger, IrType.Bool, [R(x)]), I(64), I(0)))))))))),
      Function("f32Bits", IrType.I32, [fx], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I32, [R(fx)]))),
      Function("bitsF32", IrType.F32, [i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F32, [R(i32)]))),
      Function("f64Bits", IrType.I64, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I64, [R(x)]))),
      Function("bitsF64", IrType.F64, [i64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F64, [R(i64)]))),
      Function("specialF32", IrType.F32, [i32],
        new IrIf(Bin(IrBinaryOp.Equal, R(i32), I(0)), Block(new IrReturn(F32(float.NaN))),
        Block(new IrIf(Bin(IrBinaryOp.Equal, R(i32), I(1)), Block(new IrReturn(F32(float.PositiveInfinity))),
        Block(new IrIf(Bin(IrBinaryOp.Equal, R(i32), I(2)), Block(new IrReturn(F32(float.NegativeInfinity))),
        Block(new IrIf(Bin(IrBinaryOp.Equal, R(i32), I(3)), Block(new IrReturn(F32(-0.0f))),
        Block(new IrReturn(F32(2.0f))))))))))),
      Function("specialF64", IrType.F64, [i32],
        new IrIf(Bin(IrBinaryOp.Equal, R(i32), I(0)), Block(new IrReturn(F64(double.NaN))),
        Block(new IrIf(Bin(IrBinaryOp.Equal, R(i32), I(1)), Block(new IrReturn(F64(double.PositiveInfinity))),
        Block(new IrIf(Bin(IrBinaryOp.Equal, R(i32), I(2)), Block(new IrReturn(F64(double.NegativeInfinity))),
        Block(new IrIf(Bin(IrBinaryOp.Equal, R(i32), I(3)), Block(new IrReturn(F64(-0.0))),
        Block(new IrReturn(F64(3.0))))))))))),
      Function("f2iSat", IrType.I32, [x], new IrReturn(new IrConvert(IrType.I32, R(x), IrConversion.Saturate))),
      Function("f2iTrap", IrType.I32, [x], new IrReturn(new IrConvert(IrType.I32, R(x), IrConversion.Trap))),
      Function("i2uTrap", IrType.U32, [i32], new IrReturn(new IrConvert(IrType.U32, R(i32), IrConversion.Trap))),
      Function("u2iTrap", IrType.I32, [u32], new IrReturn(new IrConvert(IrType.I32, R(u32), IrConversion.Trap))),
      Function("i64ToI32Trap", IrType.I32, [i64], new IrReturn(new IrConvert(IrType.I32, R(i64), IrConversion.Trap))),
      Function("i2f", IrType.F64, [i32], new IrReturn(new IrConvert(IrType.F64, R(i32)))),
      Function("rotl", IrType.U32, [u32, i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RotateLeft, IrType.U32, [R(u32), R(i32)]))),
      Function("rotr", IrType.U32, [u32, i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RotateRight, IrType.U32, [R(u32), R(i32)]))),
      Function("clz", IrType.I32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.LeadingZeroCount, IrType.I32, [R(u32)]))),
      Function("ctz", IrType.I32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.TrailingZeroCount, IrType.I32, [R(u32)]))),
      Function("popcnt", IrType.I32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [R(u32)]))),
      Function("bswap32", IrType.U32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.U32, [R(u32)]))),
      Function("bswap16", IrType.U16, [u16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.U16, [R(u16)]))),
      Function("bswap8", IrType.U8, [u8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.U8, [R(u8)]))),
      Function("hiWord", IrType.U32, [i64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.HighWord, IrType.U32, [R(i64)]))),
      Function("joinWords", IrType.I64, [hi, lo], new IrReturn(new IrIntrinsic(IrIntrinsicOp.JoinWords, IrType.I64, [R(hi), R(lo)]))),
      // Legalized intrinsics (portable float + integer libraries)
      Function("sinVal", IrType.F64, [x], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sin, IrType.F64, [R(x)]))),
      Function("log2Val", IrType.I32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Log2, IrType.I32, [R(u32)]))),
      Function("addSat", IrType.I32, [i32, L("k", IrType.I32)], new IrReturn(new IrIntrinsic(IrIntrinsicOp.AddSaturate, IrType.I32, [R(i32), R(L("k", IrType.I32))])))
    ]);

    var expectedSin = (double)Eval(mod, "sinVal", 1.0)!;
    var expectedRotl = (uint)Eval(mod, "rotl", 0x12345678u, 8)!;
    var expectedRotr = (uint)Eval(mod, "rotr", 0x12345678u, 8)!;
    var expectedBswap = (uint)Eval(mod, "bswap32", 0x12345678u)!;
    var expectedLog2 = (int)Eval(mod, "log2Val", 0x100u)!;
    var expectedAddSat = (int)Eval(mod, "addSat", int.MaxValue, 10)!;
    Assert.That(expectedRotl, Is.EqualTo(0x34567812u));
    Assert.That(expectedRotr, Is.EqualTo(0x78123456u));
    Assert.That(expectedBswap, Is.EqualTo(0x78563412u));
    Assert.That(expectedLog2, Is.EqualTo(8));
    Assert.That(expectedAddSat, Is.EqualTo(int.MaxValue));

    var driver = $$"""
      #include "math_bits.hpp"
      #include <cassert>
      #include <climits>
      #include <cmath>
      #include <limits>
      #include <stdexcept>
      using namespace math_bits;
      template <typename Ex, typename Fn>
      void expect_throw(Fn fn) {
        bool caught = false;
        try { (void)fn(); } catch (const Ex&) { caught = true; }
        assert(caught);
      }
      int main() {
        assert(std::fabs(fops(5.0, 2.0) - 3.5) < 1e-12);
        assert(fneg(2.5) == -2.5);
        assert(std::isnan(math_bits::fmin(std::numeric_limits<double>::quiet_NaN(), 1.0)));
        assert(std::isnan(math_bits::fmax(1.0, std::numeric_limits<double>::quiet_NaN())));
        assert(std::signbit(math_bits::fmin(-0.0, 0.0)));
        assert(!std::signbit(math_bits::fmax(-0.0, 0.0)));
        assert(imin(3, 5) == 3 && imax(3, 5) == 5);
        assert(iabs(-7) == 7 && uabs(7u) == 7u && math_bits::fabs(-3.5) == 3.5);
        expect_throw<std::overflow_error>([] { return iabs(INT32_MIN); });
        assert(fsign(-2.0) == -1 && fsign(0.0) == 0 && fsign(2.0) == 1);
        expect_throw<std::domain_error>([] { return fsign(std::numeric_limits<double>::quiet_NaN()); });
        assert(isign(-5) == -1 && isign(0) == 0 && isign(5) == 1);
        assert(usign(0u) == 0 && usign(5u) == 1);
        assert(math_bits::fsqrt(9.0) == 3.0);
        assert(fround(2.5) == (2.0 + 3.0 + 2.0 + 2.0 + 3.0));
        assert(fcopysign(3.0, -1.0) == -3.0);

        assert((fclassify(std::numeric_limits<double>::quiet_NaN()) & 1) != 0);
        assert((fclassify(std::numeric_limits<double>::infinity()) & 2) != 0);
        assert(fclassify(-4.0) == (4 + 8 + 16 + 64));
        assert((fclassify(std::numeric_limits<double>::denorm_min()) & 32) != 0);

        assert(f32_bits(1.0f) == 0x3F800000);
        assert(bits_f32(0x3F800000) == 1.0f);
        assert(f64_bits(1.0) == 0x3FF0000000000000LL);
        assert(bits_f64(0x3FF0000000000000LL) == 1.0);
        assert(std::isnan(special_f32(0)) && std::isinf(special_f32(1)) && special_f32(2) < 0 && std::signbit(special_f32(3)) && special_f32(4) == 2.0f);
        assert(std::isnan(special_f64(0)) && std::isinf(special_f64(1)) && special_f64(2) < 0 && std::signbit(special_f64(3)) && special_f64(4) == 3.0);

        assert(f2i_sat(std::numeric_limits<double>::quiet_NaN()) == 0);
        assert(f2i_sat(1e20) == INT32_MAX);
        assert(f2i_sat(-1e20) == INT32_MIN);
        assert(f2i_sat(-3.9) == -3);
        assert(f2i_trap(42.9) == 42);
        expect_throw<std::overflow_error>([] { return f2i_trap(std::numeric_limits<double>::quiet_NaN()); });
        expect_throw<std::overflow_error>([] { return f2i_trap(1e20); });
        expect_throw<std::overflow_error>([] { return f2i_trap(-1e20); });
        assert(i2u_trap(5) == 5u);
        expect_throw<std::overflow_error>([] { return i2u_trap(-1); });
        assert(u2i_trap(5u) == 5);
        expect_throw<std::overflow_error>([] { return u2i_trap(0x80000000u); });
        assert(i64_to_i32_trap(100) == 100);
        expect_throw<std::overflow_error>([] { return i64_to_i32_trap(1LL << 40); });
        assert(i2f(7) == 7.0);

        assert(rotl(0x12345678u, 8) == 0x34567812u);
        assert(rotl(0x12345678u, 0) == 0x12345678u);
        assert(rotr(0x12345678u, 8) == 0x78123456u);
        assert(clz(0u) == 32 && clz(1u) == 31 && clz(0x80000000u) == 0);
        assert(ctz(0u) == 32 && ctz(1u) == 0 && ctz(0x80000000u) == 31);
        assert(popcnt(0u) == 0 && popcnt(0xF0F0F0F0u) == 16);
        assert(bswap32(0x12345678u) == 0x78563412u);
        assert(bswap16(static_cast<std::uint16_t>(0x1234u)) == 0x3412u);
        assert(bswap8(static_cast<std::uint8_t>(0xABu)) == 0xABu);
        assert(hi_word(0x1122334455667788LL) == 0x11223344u);
        assert(join_words(0x11223344u, 0x55667788u) == 0x1122334455667788LL);

        assert(std::fabs(sin_val(1.0) - {{expectedSin.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}}) < 1e-14);
        assert(log2_val(0x100u) == 8);
        assert(add_sat(INT32_MAX, 10) == INT32_MAX);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void StepAndBindExpressions_ParityAcrossAllStandards() {
    var x = L("x", IrType.I32);
    var fx = L("fx", IrType.F64);
    var arr = L("arr", IrType.Array(IrType.I32));
    var t = L("t", IrType.I32);

    var mod = new IrModule("Steps", [
      Function("stepLocal", IrType.I32, [x],
        new IrDeclare(t, new IrStep(R(x), true, true, false)),
        new IrExprStmt(new IrStep(R(x), true, false, false)),
        new IrReturn(Bin(IrBinaryOp.Add, R(t), new IrBind(t, new IrStep(R(x), false, false, true))))),
      Function("stepFloat", IrType.F64, [fx],
        new IrExprStmt(new IrStep(R(fx), true, false, false)),
        new IrReturn(new IrStep(R(fx), false, true, false))),
      Function("stepArr", IrType.I32, [arr],
        new IrDeclare(t, new IrStep(new IrArrayLoad(R(arr), I(0)), true, true, false)),
        new IrReturn(Bin(IrBinaryOp.Add, R(t), new IrStep(new IrArrayLoad(R(arr), I(0)), true, false, false)))),
      Function("stepTrap", IrType.I32, [x],
        new IrReturn(new IrStep(R(x), true, false, true)))
    ]);

    Assert.That(Eval(mod, "stepLocal", 10), Is.EqualTo(21));
    Assert.That(Eval(mod, "stepFloat", 2.5), Is.EqualTo(3.5));

    var driver = """
      #include "steps.hpp"
      #include <cassert>
      #include <climits>
      #include <stdexcept>
      #include <vector>
      using namespace steps;
      int main() {
        assert(step_local(10) == 21);
        assert(step_float(2.5) == 3.5);
        std::vector<std::int32_t> v{5};
        assert(step_arr(v) == 12 && v[0] == 7);
        bool caught = false;
        try { (void)step_trap(INT32_MAX); } catch (const std::overflow_error&) { caught = true; }
        assert(caught);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

}
