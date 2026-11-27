using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Kotlin;
using Polypiler.Core.Backends.Kotlin.Printing;
using Polypiler.Core.Backends.Kotlin.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Tests.Backends.Kotlin;

/// <summary>
/// What the Kotlin backend writes for the constructs of the IR, row by row of its lowering table, and
/// what it refuses, each with the code of its family.
/// </summary>
[TestFixture]
public sealed class KotlinEmissionTests {

  private static IrLocal L(string name, IrType type) => new(name, type);

  private static IrLocalRef R(IrLocal local) => new(local);

  private static IrConst C(IrType type, long value) => new(type, value);

  private static IrBinary Bin(IrBinaryOp op, IrExpr left, IrExpr right) => new(op, left, right);

  /// <summary>A public function <c>f</c> that returns one expression of its parameters.</summary>
  private static IrModule Returning(IrType result, IrLocal[] parameters, IrExpr value)
    => new("M", [new IrFunction("f", true, result, [.. parameters.Distinct()], [], new IrBlock([new IrReturn(value)]))]);

  private static IrModule Body(IrType result, IrLocal[] parameters, IrLocal[] locals, params IrStmt[] statements)
    => new("M", [new IrFunction("f", true, result, parameters, locals, new IrBlock(statements))]);

  private static string Emit(IrModule module, string? profile = null)
    => KotlinEmitter.Emit(module, KotlinProfile.Parse(profile), FloatMode.Accurate, null, TargetPassOptions.Default).Files.Single().Content;

  /// <summary>The expression body of <c>f</c>.</summary>
  private static string Value(IrModule module, string? profile = null) {
    var text = Emit(module, profile);
    var start = text.IndexOf("fun f(", StringComparison.Ordinal);
    var line = text[start..text.IndexOf('\n', start)];
    return line[(line.IndexOf(" = ", StringComparison.Ordinal) + 3)..];
  }

  private static IReadOnlyList<string> Problems(IrModule module, string? profile = null)
    => [.. KotlinEmitter.Check(module, KotlinProfile.Parse(profile)).Select(problem => problem.ToString())];

  private static readonly IrLocal _a = L("a", IrType.I32), _b = L("b", IrType.I32);

  [TestCase(IrBinaryOp.Add, IrTypeKind.I32, "a + b")]
  [TestCase(IrBinaryOp.Multiply, IrTypeKind.I64, "a * b")]
  [TestCase(IrBinaryOp.Subtract, IrTypeKind.U32, "a - b")]
  [TestCase(IrBinaryOp.AddChecked, IrTypeKind.I32, "Math.addExact(a, b)")]
  [TestCase(IrBinaryOp.SubtractChecked, IrTypeKind.I64, "Math.subtractExact(a, b)")]
  [TestCase(IrBinaryOp.MultiplyChecked, IrTypeKind.I32, "Math.multiplyExact(a, b)")]
  [TestCase(IrBinaryOp.AddChecked, IrTypeKind.U32, "plyAddCheckedUInt(a, b)")]
  [TestCase(IrBinaryOp.MultiplyChecked, IrTypeKind.U64, "plyMultiplyCheckedULong(a, b)")]
  [TestCase(IrBinaryOp.Divide, IrTypeKind.I32, "plyDivideInt(a, b)")]
  [TestCase(IrBinaryOp.Remainder, IrTypeKind.I64, "plyRemainderLong(a, b)")]
  [TestCase(IrBinaryOp.Divide, IrTypeKind.U64, "a / b")]
  [TestCase(IrBinaryOp.And, IrTypeKind.I32, "a and b")]
  [TestCase(IrBinaryOp.Or, IrTypeKind.U64, "a or b")]
  [TestCase(IrBinaryOp.Xor, IrTypeKind.I64, "a xor b")]
  [TestCase(IrBinaryOp.ShiftLeft, IrTypeKind.I32, "a shl b")]
  [TestCase(IrBinaryOp.ShiftRight, IrTypeKind.I32, "a shr b")]
  [TestCase(IrBinaryOp.ShiftRight, IrTypeKind.U32, "a shr b")]
  [TestCase(IrBinaryOp.ShiftRightLogical, IrTypeKind.I64, "a ushr b")]
  [TestCase(IrBinaryOp.Less, IrTypeKind.U32, "a < b")]
  [TestCase(IrBinaryOp.GreaterOrEqual, IrTypeKind.F64, "a >= b")]
  [TestCase(IrBinaryOp.Remainder, IrTypeKind.F32, "a % b")]
  public void Binary_IsTheOperatorOfKotlin(IrBinaryOp op, IrTypeKind kind, string expected) {
    var type = Type(kind);
    var (a, b) = (L("a", type), L("b", op is IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical ? IrType.I32 : type));
    Assert.That(Value(Returning(op >= IrBinaryOp.Equal ? IrType.Bool : type, [a, b], Bin(op, R(a), R(b)))), Is.EqualTo(expected));
  }

  private static IrType Type(IrTypeKind kind) => kind switch {
    IrTypeKind.I8 => IrType.I8,
    IrTypeKind.U8 => IrType.U8,
    IrTypeKind.I16 => IrType.I16,
    IrTypeKind.U16 => IrType.U16,
    IrTypeKind.I32 => IrType.I32,
    IrTypeKind.U32 => IrType.U32,
    IrTypeKind.I64 => IrType.I64,
    IrTypeKind.U64 => IrType.U64,
    IrTypeKind.F32 => IrType.F32,
    IrTypeKind.Bool => IrType.Bool,
    _ => IrType.F64
  };

  [Test]
  public void Division_ByAConstant_IsTheOperatorAndTrapsThroughAHelperOtherwise() {
    Assert.Multiple(() => {
      Assert.That(Value(Returning(IrType.I32, [_a], Bin(IrBinaryOp.Divide, R(_a), C(IrType.I32, 7)))), Is.EqualTo("a / 7"));
      Assert.That(Value(Returning(IrType.I32, [_a], Bin(IrBinaryOp.Divide, R(_a), C(IrType.I32, -1)))), Is.EqualTo("plyDivideInt(a, -1)"));
      Assert.That(Value(Returning(IrType.U32, [L("a", IrType.U32)], Bin(IrBinaryOp.Remainder, R(L("a", IrType.U32)), C(IrType.U32, 0)))), Is.EqualTo("a % UInt.MIN_VALUE"));
      Assert.That(Emit(Returning(IrType.I32, [_a, _b], Bin(IrBinaryOp.Divide, R(_a), R(_b)))), Does.Contain("private fun plyDivideInt(a: Int, b: Int): Int {\n    if (b == -1 && a == Int.MIN_VALUE) {\n        throw ArithmeticException(\"integer overflow\")\n    }\n    return a / b\n}\n"));
    });
  }

  [TestCase(IrTypeKind.I32, 5, "5")]
  [TestCase(IrTypeKind.I32, -5, "-5")]
  [TestCase(IrTypeKind.I32, int.MinValue, "Int.MIN_VALUE")]
  [TestCase(IrTypeKind.I64, long.MinValue, "Long.MIN_VALUE")]
  [TestCase(IrTypeKind.I64, 5, "5L")]
  [TestCase(IrTypeKind.U32, 4294967295, "4294967295u")]
  [TestCase(IrTypeKind.U64, -1, "18446744073709551615uL")]
  [TestCase(IrTypeKind.I8, -5, "(-5).toByte()")]
  [TestCase(IrTypeKind.U16, 7, "7u.toUShort()")]
  [TestCase(IrTypeKind.Bool, 1, "true")]
  public void Literal_HasTheSuffixOfItsType(IrTypeKind kind, long value, string expected)
    => Assert.That(Value(Returning(Type(kind), [], C(Type(kind), value))), Is.EqualTo(expected));

  [TestCase(1.5, "1.5")]
  [TestCase(-0.0, "-0.0")]
  [TestCase(1e300, "1E300")]
  [TestCase(double.NaN, "Double.NaN")]
  [TestCase(double.PositiveInfinity, "Double.POSITIVE_INFINITY")]
  [TestCase(double.NegativeInfinity, "Double.NEGATIVE_INFINITY")]
  public void Double_IsItsShortestDigitsOrItsName(double value, string expected)
    => Assert.That(Value(Returning(IrType.F64, [], C(IrType.F64, BitConverter.DoubleToInt64Bits(value)))), Is.EqualTo(expected));

  [Test]
  public void Float_AndPayloads_KeepTheirBits() {
    Assert.Multiple(() => {
      Assert.That(Value(Returning(IrType.F64, [], C(IrType.F64, 0x7FF8000000000001))), Is.EqualTo("Double.fromBits(9221120237041090561L)"));
      Assert.That(Value(Returning(IrType.F32, [], C(IrType.F32, BitConverter.SingleToInt32Bits(0.5f)))), Is.EqualTo("0.5f"));
      Assert.That(Value(Returning(IrType.F32, [], C(IrType.F32, BitConverter.SingleToInt32Bits(-2f)))), Is.EqualTo("-2.0f"));
      Assert.That(Value(Returning(IrType.F32, [], C(IrType.F32, 0x7FC00000))), Is.EqualTo("Float.NaN"));
      Assert.That(Value(Returning(IrType.F32, [], C(IrType.F32, 0x7FC00001))), Is.EqualTo("Float.fromBits(2143289345)"));
      Assert.That(Value(Returning(IrType.F32, [], C(IrType.F32, BitConverter.SingleToInt32Bits(float.NegativeInfinity)))), Is.EqualTo("Float.NEGATIVE_INFINITY"));
    });
  }

  [TestCase(IrTypeKind.I32, IrTypeKind.I8, IrConversion.Wrap, "a.toByte()")]
  [TestCase(IrTypeKind.I32, IrTypeKind.U64, IrConversion.Wrap, "a.toULong()")]
  [TestCase(IrTypeKind.I64, IrTypeKind.I32, IrConversion.Trap, "plyToIntCheckedFromLong(a)")]
  [TestCase(IrTypeKind.U32, IrTypeKind.I32, IrConversion.Trap, "plyToIntCheckedFromUInt(a)")]
  [TestCase(IrTypeKind.I32, IrTypeKind.I64, IrConversion.Trap, "a.toLong()")]
  [TestCase(IrTypeKind.F64, IrTypeKind.I32, IrConversion.Saturate, "a.toInt()")]
  [TestCase(IrTypeKind.F64, IrTypeKind.I8, IrConversion.Saturate, "a.toInt().coerceIn(-128, 127).toByte()")]
  [TestCase(IrTypeKind.F32, IrTypeKind.U16, IrConversion.Saturate, "a.toUInt().coerceAtMost(65535u).toUShort()")]
  [TestCase(IrTypeKind.F64, IrTypeKind.U32, IrConversion.Trap, "plyToUIntCheckedFromDouble(a)")]
  [TestCase(IrTypeKind.F32, IrTypeKind.I64, IrConversion.Trap, "plyToLongCheckedFromFloat(a)")]
  [TestCase(IrTypeKind.U64, IrTypeKind.F32, IrConversion.Wrap, "plyULongToFloat(a)")]
  [TestCase(IrTypeKind.I32, IrTypeKind.F64, IrConversion.Wrap, "a.toDouble()")]
  [TestCase(IrTypeKind.F32, IrTypeKind.F64, IrConversion.Wrap, "a.toDouble()")]
  public void Conversion_HasKotlinsMeaning(IrTypeKind from, IrTypeKind to, IrConversion contract, string expected) {
    var a = L("a", Type(from));
    Assert.That(Value(Returning(Type(to), [a], new IrConvert(Type(to), R(a), contract))), Is.EqualTo(expected));
  }

  [Test]
  public void Conversion_OfAConstant_IsTheConstant() {
    Assert.That(Value(Returning(IrType.I8, [], new IrConvert(IrType.I8, C(IrType.I32, 300)))), Is.EqualTo("44.toByte()"));
    Assert.That(Problems(Returning(IrType.I32, [L("a", IrType.F64)], new IrConvert(IrType.I32, R(L("a", IrType.F64)), IrConversion.Wrap))), Has.Some.Contains("PLY0100"));
  }

  [Test]
  public void Narrow_IsConvertedToItsTypeOfKotlin() {
    var (a, b) = (L("a", IrType.I8), L("b", IrType.I8));
    var u = L("u", IrType.U8);
    var sum = new IrConvert(IrType.I8, Bin(IrBinaryOp.Add, new IrConvert(IrType.I32, R(a)), new IrConvert(IrType.I32, R(b))));
    Assert.Multiple(() => {
      Assert.That(Value(Returning(IrType.I8, [a, b], sum)), Is.EqualTo("(a.toInt() + b.toInt()).toByte()"));
      Assert.That(Value(Returning(IrType.U32, [u], new IrConvert(IrType.U32, R(u)))), Is.EqualTo("u.toUInt()"));
      Assert.That(Value(Returning(IrType.I64, [L("l", IrType.I64)], new IrUnary(IrUnaryOp.NegateChecked, R(L("l", IrType.I64))))), Is.EqualTo("Math.negateExact(l)"));
      Assert.That(Value(Returning(IrType.I32, [_a], new IrUnary(IrUnaryOp.Negate, R(_a)))), Is.EqualTo("-a"));
      Assert.That(Value(Returning(IrType.U64, [L("w", IrType.U64)], new IrUnary(IrUnaryOp.BitNot, R(L("w", IrType.U64))))), Is.EqualTo("w.inv()"));
      Assert.That(Value(Returning(IrType.Bool, [L("c", IrType.Bool)], new IrUnary(IrUnaryOp.LogicalNot, R(L("c", IrType.Bool))))), Is.EqualTo("!c"));
      Assert.That(Value(Returning(IrType.I32, [L("c", IrType.Bool)], new IrConditional(R(L("c", IrType.Bool)), C(IrType.I32, 1), C(IrType.I32, 2)))), Is.EqualTo("if (c) 1 else 2"));
      Assert.That(Value(Returning(IrType.I32, [_a], Bin(IrBinaryOp.ShiftLeft, R(_a), C(IrType.I32, 33)))), Is.EqualTo("a shl 1"));
      Assert.That(Value(Returning(IrType.Bool, [L("c", IrType.Bool), L("d", IrType.Bool)], Bin(IrBinaryOp.LogicalAnd, R(L("c", IrType.Bool)), R(L("d", IrType.Bool))))), Is.EqualTo("c && d"));
    });
  }

  [Test]
  public void Intrinsics_AreTheMethodsOfKotlinAndJavaLang() {
    var u = L("u", IrType.U32);
    var l = L("l", IrType.I64);
    var d = L("d", IrType.F64);
    var s = L("s", IrType.F32);
    IrIntrinsic I(IrIntrinsicOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);
    Assert.Multiple(() => {
      Assert.That(Value(Returning(IrType.I32, [u], I(IrIntrinsicOp.PopCount, IrType.I32, R(u)))), Is.EqualTo("u.countOneBits()"));
      Assert.That(Value(Returning(IrType.I32, [l], I(IrIntrinsicOp.LeadingZeroCount, IrType.I32, R(l)))), Is.EqualTo("l.countLeadingZeroBits()"));
      Assert.That(Value(Returning(IrType.I32, [l], I(IrIntrinsicOp.TrailingZeroCount, IrType.I32, R(l)))), Is.EqualTo("l.countTrailingZeroBits()"));
      Assert.That(Value(Returning(IrType.U32, [u, _a], I(IrIntrinsicOp.RotateLeft, IrType.U32, R(u), R(_a)))), Is.EqualTo("u.rotateLeft(a)"));
      Assert.That(Value(Returning(IrType.U32, [u, _a], I(IrIntrinsicOp.RotateRight, IrType.U32, R(u), R(_a)))), Is.EqualTo("u.rotateRight(a)"));
      Assert.That(Value(Returning(IrType.U32, [u], I(IrIntrinsicOp.ByteSwap, IrType.U32, R(u)))), Is.EqualTo("Integer.reverseBytes(u.toInt()).toUInt()"));
      Assert.That(Value(Returning(IrType.I64, [l], I(IrIntrinsicOp.BitReverse, IrType.I64, R(l)))), Is.EqualTo("java.lang.Long.reverse(l)"));
      Assert.That(Value(Returning(IrType.I16, [L("h", IrType.I16)], I(IrIntrinsicOp.ByteSwap, IrType.I16, R(L("h", IrType.I16))))), Is.EqualTo("java.lang.Short.reverseBytes(h)"));
      Assert.That(Value(Returning(IrType.U32, [l], I(IrIntrinsicOp.HighWord, IrType.U32, R(l)))), Is.EqualTo("(l ushr 32).toUInt()"));
      Assert.That(Value(Returning(IrType.I64, [u, u], I(IrIntrinsicOp.JoinWords, IrType.I64, R(u), R(u)))), Is.EqualTo("(u.toLong() shl 32) or u.toLong()"));
      Assert.That(Value(Returning(IrType.I64, [l, l], I(IrIntrinsicOp.MulHigh, IrType.I64, R(l), R(l)))), Is.EqualTo("Math.multiplyHigh(l, l)"));
      Assert.That(Value(Returning(IrType.U64, [L("q", IrType.U64), L("r", IrType.U64)], I(IrIntrinsicOp.MulHigh, IrType.U64, R(L("q", IrType.U64)), R(L("r", IrType.U64))))), Is.EqualTo("Math.unsignedMultiplyHigh(q.toLong(), r.toLong()).toULong()"));
      Assert.That(Value(Returning(IrType.U32, [u, u], I(IrIntrinsicOp.ParallelBitExtract, IrType.U32, R(u), R(u)))), Is.EqualTo("Integer.compress(u.toInt(), u.toInt()).toUInt()"));
      Assert.That(Value(Returning(IrType.I64, [l, l], I(IrIntrinsicOp.ParallelBitDeposit, IrType.I64, R(l), R(l)))), Is.EqualTo("java.lang.Long.expand(l, l)"));
      Assert.That(Value(Returning(IrType.F64, [d], I(IrIntrinsicOp.Sqrt, IrType.F64, R(d)))), Is.EqualTo("Math.sqrt(d)"));
      Assert.That(Value(Returning(IrType.F32, [s], I(IrIntrinsicOp.Sqrt, IrType.F32, R(s)))), Is.EqualTo("Math.sqrt(s.toDouble()).toFloat()"));
      Assert.That(Value(Returning(IrType.F64, [d], I(IrIntrinsicOp.Floor, IrType.F64, R(d)))), Is.EqualTo("Math.floor(d)"));
      Assert.That(Value(Returning(IrType.F32, [s], I(IrIntrinsicOp.Ceiling, IrType.F32, R(s)))), Is.EqualTo("Math.ceil(s.toDouble()).toFloat()"));
      Assert.That(Value(Returning(IrType.F64, [d], I(IrIntrinsicOp.RoundEven, IrType.F64, R(d)))), Is.EqualTo("Math.rint(d)"));
      Assert.That(Value(Returning(IrType.F64, [d], I(IrIntrinsicOp.Truncate, IrType.F64, R(d)))), Is.EqualTo("kotlin.math.truncate(d)"));
      Assert.That(Value(Returning(IrType.F64, [d], I(IrIntrinsicOp.Abs, IrType.F64, R(d)))), Is.EqualTo("Math.abs(d)"));
      Assert.That(Value(Returning(IrType.F64, [d], I(IrIntrinsicOp.FusedMultiplyAdd, IrType.F64, R(d), R(d), R(d)))), Is.EqualTo("Math.fma(d, d, d)"));
      Assert.That(Value(Returning(IrType.I64, [d], I(IrIntrinsicOp.FloatBits, IrType.I64, R(d)))), Is.EqualTo("d.toRawBits()"));
      Assert.That(Value(Returning(IrType.F32, [_a], I(IrIntrinsicOp.BitsFloat, IrType.F32, R(_a)))), Is.EqualTo("Float.fromBits(a)"));
      Assert.That(Value(Returning(IrType.F64, [d, d], I(IrIntrinsicOp.Min, IrType.F64, R(d), R(d)))), Is.EqualTo("minOf(d, d)"));
      Assert.That(Value(Returning(IrType.U32, [u, u], I(IrIntrinsicOp.Max, IrType.U32, R(u), R(u)))), Is.EqualTo("maxOf(u, u)"));
      Assert.That(Value(Returning(IrType.I32, [d], I(IrIntrinsicOp.Sign, IrType.I32, R(d)))), Is.EqualTo("plySignDouble(d)"));
      Assert.That(Value(Returning(IrType.F32, [s], I(IrIntrinsicOp.RoundAway, IrType.F32, R(s)))), Is.EqualTo("plyRoundAwayFloat(s)"));
      Assert.That(Value(Returning(IrType.Bool, [u, u, L("c", IrType.Bool)], I(IrIntrinsicOp.CarryOut, IrType.Bool, R(u), R(u), R(L("c", IrType.Bool))))), Is.EqualTo("u.toLong() + u.toLong() + (if (c) 1L else 0L) > 4294967295L"));
      Assert.That(Value(Returning(IrType.Bool, [u, u, L("c", IrType.Bool)], I(IrIntrinsicOp.BorrowOut, IrType.Bool, R(u), R(u), R(L("c", IrType.Bool))))), Is.EqualTo("u.toLong() - u.toLong() - (if (c) 1L else 0L) < 0L"));
    });
  }

  [Test]
  public void Strings_AreKotlinsStrings() {
    var (s, t) = (L("s", IrType.String), L("t", IrType.String));
    IrIntrinsic I(IrIntrinsicOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);
    Assert.Multiple(() => {
      Assert.That(Value(Returning(IrType.I32, [s], I(IrIntrinsicOp.StrLength, IrType.I32, R(s)))), Is.EqualTo("s.length"));
      Assert.That(Value(Returning(IrType.U16, [s, _a], I(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, R(s), R(_a)))), Is.EqualTo("s[a].code.toUShort()"));
      Assert.That(Value(Returning(IrType.String, [s, t], I(IrIntrinsicOp.StrConcat, IrType.String, R(s), R(t), new IrStrConst("$x")))), Is.EqualTo("s + t + \"\\$x\""));
      Assert.That(Value(Returning(IrType.String, [L("c", IrType.U16)], I(IrIntrinsicOp.StrFromCodeUnit, IrType.String, R(L("c", IrType.U16))))), Is.EqualTo("Char(c.toInt()).toString()"));
      Assert.That(Value(Returning(IrType.Bool, [s, t], I(IrIntrinsicOp.StrEquals, IrType.Bool, R(s), R(t)))), Is.EqualTo("s == t"));
      Assert.That(Value(Returning(IrType.I32, [s, t], I(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, R(s), R(t)))), Is.EqualTo("Integer.signum(s.compareTo(t))"));
      Assert.That(Value(Returning(IrType.Bool, [s, t], I(IrIntrinsicOp.StrStartsWith, IrType.Bool, R(s), R(t)))), Is.EqualTo("s.startsWith(t)"));
      Assert.That(Value(Returning(IrType.Bool, [s], I(IrIntrinsicOp.StrEndsWith, IrType.Bool, R(s), C(IrType.U16, 65)))), Is.EqualTo("s.endsWith(Char(65))"));
      Assert.That(Value(Returning(IrType.Bool, [s, t], I(IrIntrinsicOp.StrContains, IrType.Bool, R(s), R(t)))), Is.EqualTo("s.contains(t)"));
      Assert.That(Problems(Returning(IrType.String, [s], I(IrIntrinsicOp.StrTrim, IrType.String, R(s), C(IrType.I32, 0)))), Has.Some.Contains("PLY1651"));
    });
  }

  [Test]
  public void Arrays_ArePrimitiveArraysOfTheSignedTypeOfTheWidth() {
    var bytes = L("bytes", IrType.Array(IrType.U8));
    var words = L("words", IrType.Array(IrType.U32));
    var values = L("values", IrType.Array(IrType.I32));
    var i = L("i", IrType.I32);
    var text = Emit(Body(IrType.U32, [bytes, words, values, i], [],
      new IrStore(R(bytes), R(i), C(IrType.U8, 200)),
      new IrStore(R(words), R(i), new IrConvert(IrType.U32, new IrArrayLoad(R(bytes), R(i)))),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [R(values), C(IrType.I32, 0), R(values), C(IrType.I32, 1), C(IrType.I32, 2)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [R(words), C(IrType.U32, 7)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayClear, IrType.Void, [R(values)])),
      new IrExprStmt(new IrStep(new IrArrayLoad(R(values), R(i)), true, true, false)),
      new IrReturn(Bin(IrBinaryOp.Add, new IrArrayLoad(R(words), C(IrType.I32, 0)), new IrConvert(IrType.U32, new IrArrayLength(R(bytes)))))));
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("fun f(bytes: ByteArray, words: IntArray, values: IntArray, i: Int): UInt {\n"));
      Assert.That(text, Does.Contain("    bytes[i] = (-56).toByte()\n"));
      Assert.That(text, Does.Contain("    words[i] = bytes[i].toUByte().toUInt().toInt()\n"));
      Assert.That(text, Does.Contain("    plyCopyInt(values, 0, values, 1, 2)\n"));
      Assert.That(text, Does.Contain("    words.fill(7)\n"));
      Assert.That(text, Does.Contain("    values.fill(0)\n"));
      Assert.That(text, Does.Contain("    values[i]++\n"));
      Assert.That(text, Does.Contain("    return words[0].toUInt() + bytes.size.toUInt()\n"));
      Assert.That(text, Does.Contain("private fun plyCopyInt(source: IntArray, sourceIndex: Int, target: IntArray, targetIndex: Int, length: Int) {\n"));
    });
  }

  [Test]
  public void NewArrays_AndTables() {
    var n = L("n", IrType.I32);
    var table = new IrModuleConst("Table", IrType.Array(IrType.U32), [1, 4294967295]);
    var limit = new IrModuleConst("MaxValue", IrType.I64, [5]);
    var nan = new IrModuleConst("Nothing", IrType.F64, [BitConverter.DoubleToInt64Bits(double.NaN)]);
    var name = IrModuleConst.OfStrings("Name", IrType.String, ["a$b"]);
    var names = IrModuleConst.OfStrings("Names", IrType.Array(IrType.String), ["x", "y"]);
    var module = new IrModule("M", [new IrFunction("f", true, IrType.Array(IrType.I64), [n], [L("first", IrType.U32)], new IrBlock([
      new IrDeclare(L("first", IrType.U32), new IrArrayLoad(new IrConstRef(table), C(IrType.I32, 0))),
      new IrReturn(new IrNewArray(IrType.Array(IrType.I64), R(n)))
    ])), new IrFunction("g", false, IrType.Array(IrType.F64), [], [], new IrBlock([
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.F64), [C(IrType.F64, BitConverter.DoubleToInt64Bits(1.5)), C(IrType.F64, 0)]))
    ]))], [table, limit, nan, name, names]);
    var text = Emit(module);
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("private val TABLE: IntArray = intArrayOf(1, -1)\n"));
      Assert.That(text, Does.Contain("private const val MAX_VALUE: Long = 5L\n"));
      Assert.That(text, Does.Contain("private const val NOTHING: Double = Double.NaN\n"));
      Assert.That(text, Does.Contain("private const val NAME: String = \"a\\$b\"\n"));
      Assert.That(text, Does.Contain("private val NAMES: Array<String> = arrayOf(\"x\", \"y\")\n"));
      Assert.That(text, Does.Contain("    return LongArray(n)\n"));
      Assert.That(text, Does.Contain("private fun g(): DoubleArray = doubleArrayOf(1.5, 0.0)\n"));
    });
  }

  [Test]
  public void Statements_AreStructuredKotlin() {
    var i = L("i", IrType.I32);
    var total = L("total", IrType.I64);
    var x = L("x", IrType.I32);
    var c = L("c", IrType.Bool);
    var text = Emit(Body(IrType.I64, [_a, c], [i, total, x],
      new IrDeclare(total, null),
      new IrDeclare(i, C(IrType.I32, 0)),
      new IrFor(Bin(IrBinaryOp.Less, R(i), R(_a)), new IrBlock([
        new IrIf(Bin(IrBinaryOp.Equal, R(i), C(IrType.I32, 3)), new IrBlock([new IrContinue()]), null),
        new IrSwitch(R(i), [
          new IrSwitchSection([1, 2], false, new IrBlock([new IrAssign(total, Bin(IrBinaryOp.Add, R(total), C(IrType.I64, 1))), new IrBreak()])),
          new IrSwitchSection([5], false, new IrBlock([new IrIf(R(c), new IrBlock([new IrBreak()]), null), new IrContinue()])),
          new IrSwitchSection([], true, new IrBlock([new IrAssign(total, Bin(IrBinaryOp.Subtract, R(total), C(IrType.I64, 2)))]))
        ]),
        new IrIf(R(c), new IrBlock([new IrBreak()]), new IrBlock([new IrIf(Bin(IrBinaryOp.Greater, R(i), C(IrType.I32, 9)), new IrBlock([new IrReturn(R(total))]), null)]))
      ]), new IrBlock([new IrExprStmt(new IrStep(R(i), true, true, false))])),
      new IrDeclare(x, null),
      new IrDoWhile(new IrBlock([new IrAssign(x, Bin(IrBinaryOp.Add, R(x), C(IrType.I32, 1)))]), Bin(IrBinaryOp.Less, R(x), C(IrType.I32, 3))),
      new IrWhile(R(c), new IrBlock([new IrBreak()])),
      new IrWriteLine(new IrStrConst("done")),
      new IrReturn(R(total))));
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("    var total: Long = 0L\n    var i: Int = 0\n    loop@ while (i < a) {\n        if (i == 3) {\n            i++\n            continue\n        }\n"));
      Assert.That(text, Does.Contain("        switch@ do {\n            when (i) {\n                1, 2 -> {\n                    total += 1L\n                }\n                5 -> {\n                    if (c) {\n                        break@switch\n                    }\n                    i++\n                    continue@loop\n                }\n                else -> {\n                    total -= 2L\n                }\n            }\n        } while (false)\n"));
      Assert.That(text, Does.Contain("        if (c) {\n            break\n        } else if (i > 9) {\n            return total\n        }\n        i++\n    }\n"));
      Assert.That(text, Does.Contain("    do {\n        x += 1\n    } while (x < 3)\n    while (c) {\n        break\n    }\n    println(\"done\")\n"));
    });
  }

  [Test]
  public void Parameters_TheBodyAssigns_AreRebound() {
    var text = Emit(Body(IrType.I32, [_a], [], new IrAssign(_a, Bin(IrBinaryOp.Multiply, R(_a), C(IrType.I32, 2))), new IrReturn(R(_a))));
    Assert.That(text, Does.Contain("@file:Suppress(\"NAME_SHADOWING\")").And.Contain("    var a = a\n    a *= 2\n    return a\n"));
  }

  [Test]
  public void CheckedSteps_AreExactArithmetic() {
    var bytes = L("bytes", IrType.Array(IrType.I8));
    var text = Emit(Body(IrType.I32, [_a, bytes], [],
      new IrExprStmt(new IrStep(R(_a), true, true, true)),
      new IrExprStmt(new IrStep(new IrArrayLoad(R(bytes), C(IrType.I32, 0)), false, false, true)),
      new IrReturn(Bin(IrBinaryOp.Add, new IrStep(R(_a), true, true, true), new IrStep(R(_a), false, false, true)))));
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("    a = Math.addExact(a, 1)\n"));
      Assert.That(text, Does.Contain("    bytes[0] = plySubtractCheckedByte(bytes[0], 1.toByte())\n"));
      Assert.That(text, Does.Contain("return a.also {\n        a = Math.addExact(a, 1)\n    } + Math.subtractExact(a, 1).also {\n        a = it\n    }\n"));
    });
  }

  [Test]
  public void Exceptions_OfTheLibrary_AreTheJvmsOrDeclared() {
    IrExpr New(IrClassType type, params IrExpr[] arguments) => new IrNew(IrType.Object(type), IrExceptionClass.Constructor(type, arguments.Length), arguments);
    var text = Emit(Body(IrType.I32, [_a], [],
      new IrIf(Bin(IrBinaryOp.Equal, R(_a), C(IrType.I32, 1)), new IrBlock([new IrThrow(New(IrExceptionClass.DivideByZeroException))]), null),
      new IrIf(Bin(IrBinaryOp.Equal, R(_a), C(IrType.I32, 2)), new IrBlock([new IrThrow(New(IrExceptionClass.IndexOutOfRangeException, new IrStrConst("m")))]), null),
      new IrIf(Bin(IrBinaryOp.Equal, R(_a), C(IrType.I32, 3)), new IrBlock([new IrThrow(New(IrExceptionClass.InvalidOperationException, new IrStrConst("m"), New(IrExceptionClass.Exception)))]), null),
      new IrReturn(new IrThrowExpression(IrType.I32, New(IrExceptionClass.ArgumentException, new IrStrConst("x"))))));
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("/**\n * The DivideByZeroException of the IR's library, which the JVM has no class of; it derives from ArithmeticException.\n */\nprivate class DivideByZeroException(message: String? = null) : ArithmeticException(message)\n"));
      Assert.That(text, Does.Contain("throw DivideByZeroException(\"Attempted to divide by zero.\")"));
      Assert.That(text, Does.Contain("throw IndexOutOfBoundsException(\"m\")"));
      Assert.That(text, Does.Contain("throw IllegalStateException(\"m\").also { it.initCause(Exception(\"Exception of type 'System.Exception' was thrown.\")) }"));
      Assert.That(text, Does.Contain("return throw IllegalArgumentException(\"x\")"));
    });
  }

  [Test]
  public void ReadOnlyStructs_AreDataClasses() {
    var point = new IrStructType("point", [new IrField("X", IrType.I32, true), new IrField("Y", IrType.F64, true)]);
    var p = L("p", IrType.Struct(point));
    var q = L("q", IrType.Struct(point));
    var module = new IrModule("M", [
      new IrFunction("make", true, IrType.Struct(point), [_a], [], new IrBlock([new IrReturn(new IrStructNew(IrType.Struct(point), [R(_a), C(IrType.F64, 0)]))])),
      new IrFunction("zero", true, IrType.Struct(point), [], [], new IrBlock([new IrReturn(new IrStructDefault(IrType.Struct(point)))])),
      new IrFunction("x", true, IrType.I32, [p], [], new IrBlock([new IrReturn(new IrFieldLoad(R(p), point.Fields[0]))])),
      new IrFunction("same", true, IrType.Bool, [p, q], [], new IrBlock([new IrReturn(new IrStructEquals(R(p), R(q)))])),
      new IrFunction("many", true, IrType.Array(IrType.Struct(point)), [_a], [], new IrBlock([new IrReturn(new IrNewArray(IrType.Array(IrType.Struct(point)), R(_a)))]))
    ]) { Types = [point] };
    var text = Emit(module);
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("data class Point(val x: Int = 0, val y: Double = 0.0)\n"));
      Assert.That(text, Does.Contain("fun make(a: Int): Point = Point(a, 0.0)\n"));
      Assert.That(text, Does.Contain("fun zero(): Point = Point()\n"));
      Assert.That(text, Does.Contain("fun x(p: Point): Int = p.x\n"));
      Assert.That(text, Does.Contain("fun same(p: Point, q: Point): Boolean = p == q\n"));
      Assert.That(text, Does.Contain("fun many(a: Int): Array<Point> = Array(a) { Point() }\n"));
    });
  }

  [Test]
  public void Refusals_NameTheirFamily() {
    var point = new IrStructType("Point", [new IrField("X", IrType.I32, false)]);
    var p = L("p", IrType.Struct(point));
    var classes = Polypiler.Core.Frontends.CSharp.CSharpFrontend.Import("A.cs", "public sealed class Box { public int V; } public static class A { public static int F(int x) { var b = new Box(); b.V = x; return b.V; } }").Module!;
    var laid = new IrStructType("Laid", [new IrField("X", IrType.I32, true)]) { Layout = new IrStructLayout(IrLayoutKind.Sequential, 0, 0, []) };
    Assert.Multiple(() => {
      Assert.That(Problems(Returning(IrType.BigInt, [], new IrBigConst(1))), Has.Some.StartsWith("error PLY1650"));
      Assert.That(Problems(new IrModule("M", [new IrFunction("f", true, IrType.Void, [p], [], new IrBlock([new IrFieldStore(R(p), point.Fields[0], C(IrType.I32, 1))]))]) { Types = [point] }),
        Has.Some.StartsWith("error PLY1682").And.Some.StartsWith("error PLY1658"));
      Assert.That(Problems(new IrModule("M", [new IrFunction("Ab", true, IrType.Void, [], [], new IrBlock([])), new IrFunction("ab", true, IrType.Void, [], [], new IrBlock([]))])), Has.Some.StartsWith("error PLY1674"));
      Assert.That(Problems(new IrModule("M", [new IrFunction("_", true, IrType.Void, [], [], new IrBlock([]))])), Has.Some.StartsWith("error PLY1674"));
      Assert.That(Problems(classes), Has.Some.StartsWith("error PLY1682"));
      Assert.That(Problems(new IrModule("M", []) { Types = [laid] }), Has.Some.StartsWith("error PLY1666"));
      Assert.That(Problems(Body(IrType.Void, [], [], new IrBreak())), Has.Some.StartsWith("error PLY0100"));
    });
  }

  [Test]
  public void Header_NamesTheProfileTheLicenceAndTheFloatingPoint() {
    var d = L("d", IrType.F64);
    var text = Emit(Returning(IrType.I32, [d], new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(d)])), "kotlin2.1-jvm17");
    Assert.Multiple(() => {
      Assert.That(text, Does.StartWith("// M: generated by polypiler. Change the source, not this file.\n// Kotlin profile: kotlin2.1, JVM target 17.\n// Helpers added by polypiler: MIT"));
      Assert.That(text, Does.Contain("\npackage m\n"));
      Assert.That(KotlinEmitter.FileName(new IrModule("crc32", [])), Is.EqualTo("Crc32.kt"));
      Assert.That(KotlinEmitter.FileClass(new IrModule("crc32", [])), Is.EqualTo("Crc32Kt"));
      Assert.That(KotlinEmitter.FileClass(new IrModule("__", [])), Is.EqualTo("GeneratedKt"));
      Assert.That(KotlinEmitter.PackageOf(new IrModule("Crc32", [])), Is.EqualTo("crc32"));
    });
  }

  [Test]
  public void TargetPasses_RemoveHelpersNothingCalls() {
    var module = Returning(IrType.I32, [_a, _b], Bin(IrBinaryOp.Divide, R(_a), R(_b)));
    var tree = KotlinEmitter.Build(module, KotlinProfile.Default, FloatMode.Accurate, null, TargetPassOptions.Default).Tree;
    var unused = tree with { Declarations = [.. tree.Declarations, KotlinHelpers.Declaration(KotlinHelpers.UnsignedToSingle)] };
    var details = new List<string>();
    var cleaned = Polypiler.Core.Backends.Kotlin.Passes.KotlinTargetPasses.Pipeline.Passes[0].Run(unused, KotlinProfile.Default, details.Add);
    Assert.Multiple(() => {
      Assert.That(details, Is.EqualTo(new[] { "removed the helper plyULongToFloat, which nothing calls" }));
      Assert.That(cleaned.Declarations, Has.Count.EqualTo(tree.Declarations.Count));
      Assert.That(Polypiler.Core.Backends.Kotlin.Passes.KotlinTargetPasses.Pipeline.Passes[0].Run(tree, KotlinProfile.Default, details.Add), Is.SameAs(tree));
    });
  }

  [Test]
  public void EveryHelper_PrintsAndReadsBack() {
    foreach (var name in KotlinHelpers.Names) {
      var text = KotlinPrinter.Print(KotlinHelpers.Declaration(name));
      Assert.That(KotlinPrinter.Print(KotlinSyntaxReader.Read(text)), Is.EqualTo(text), name);
      Assert.That(KotlinEmitter.HelperOrigins[name].Licence, Is.EqualTo("MIT"));
    }

    Assert.Throws<ArgumentException>(() => KotlinHelpers.Declaration("plyNothing"));
    Assert.That(KotlinHelpers.Number(1e21), Is.EqualTo("1E21"));
    Assert.That(KotlinHelpers.Number(3f), Is.EqualTo("3.0f"));
  }

  [Test]
  public void Legalize_BindsTheLibrariesHelpers() {
    var u = L("u", IrType.U32);
    var module = Returning(IrType.Bool, [u], new IrIntrinsic(IrIntrinsicOp.IsPow2, IrType.Bool, [R(u)]));
    var legalized = KotlinEmitter.Legalize(module, () => PrecomputedImplementations.Module, KotlinProfile.Default, FloatMode.Accurate);
    Assert.That(legalized.Functions.Count, Is.GreaterThan(1));
    Assert.That(KotlinEmitter.Check(legalized, KotlinProfile.Default), Is.Empty);
    Assert.That(KotlinEmitter.FunctionNames(legalized)["f"], Is.EqualTo("f"));
    var d = L("d", IrType.F64);
    var fma = Returning(IrType.F64, [d], new IrIntrinsic(IrIntrinsicOp.FusedMultiplyAdd, IrType.F64, [R(d), R(d), R(d)]));
    Assert.That(KotlinEmitter.Legalize(fma, () => PrecomputedImplementations.Module, KotlinProfile.Parse("kotlin2.4-jvm11"), FloatMode.Accurate).Functions, Has.Count.EqualTo(1));
    Assert.That(KotlinEmitter.Legalize(fma, () => PrecomputedImplementations.Module, KotlinProfile.Default, FloatMode.Accurate).Functions.Count, Is.GreaterThan(1));
  }

  [Test]
  public void Emit_RefusesAModuleItCannotWrite() {
    Assert.Throws<ArgumentException>(() => KotlinEmitter.Emit(Body(IrType.Void, [], [], new IrBreak())));
    Assert.Throws<ArgumentException>(() => KotlinEmitter.Emit(new IrModule("M", [new IrFunction("f", true, IrType.I32, [], [], new IrBlock([new IrReturn(C(IrType.I64, 1))]))])));
    Assert.That(KotlinEmitter.Check(new IrModule("M", [new IrFunction("f", true, IrType.I32, [], [], new IrBlock([new IrReturn(C(IrType.I64, 1))]))]), KotlinProfile.Default), Has.Some.Property("Code").EqualTo("PLY0100"));
  }

}
