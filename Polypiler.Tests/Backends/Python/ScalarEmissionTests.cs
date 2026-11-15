using System;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Python.PythonIr;

namespace Polypiler.Tests.Backends.Python;

/// <summary>
/// The lowering table of the scalar issue (#447) as text: every integer operation masked or
/// sign-wrapped to its width, truncating division through its helpers, IEEE 754 float forms, binary32
/// rounding, conversions and bit casts. Whether the text computes the IR's result is what the
/// differential tests of the acceptance project run under CPython.
/// </summary>
[TestFixture]
public sealed class ScalarEmissionTests {

  private static readonly IrLocal _x = L("x", IrType.I32), _y = L("y", IrType.I32);
  private static readonly IrLocal _ux = L("x", IrType.U32), _uy = L("y", IrType.U32);
  private static readonly IrLocal _lx = L("x", IrType.I64), _ly = L("y", IrType.I64);
  private static readonly IrLocal _qx = L("x", IrType.U64), _qy = L("y", IrType.U64);
  private static readonly IrLocal _dx = L("x", IrType.F64), _dy = L("y", IrType.F64);
  private static readonly IrLocal _fx = L("x", IrType.F32), _fy = L("y", IrType.F32);
  private static readonly IrLocal _b = L("b", IrType.Bool), _c = L("c", IrType.Bool);

  private static string Body(IrType result, IrLocal[] parameters, IrExpr value) {
    var text = Emit(Returning(result, parameters, value));
    return text[text.IndexOf("def f(", StringComparison.Ordinal)..];
  }

  [Test]
  public void AddOfI32_WrapsWithAMaskAndASignWrap()
    => Assert.That(Body(IrType.I32, [_x, _y], Bin(IrBinaryOp.Add, R(_x), R(_y))), Does.Contain("return (((x + y) & 0xFFFFFFFF) ^ 0x80000000) - 0x80000000"));

  [Test]
  public void MulOfU32_IsMasked() => Assert.That(Body(IrType.U32, [_ux, _uy], Bin(IrBinaryOp.Multiply, R(_ux), R(_uy))), Does.Contain("return (x * y) & 0xFFFFFFFF"));

  [Test]
  public void SubOf64BitTypes_IsMaskedWithGroupedDigits() {
    Assert.That(Body(IrType.U64, [_qx, _qy], Bin(IrBinaryOp.Subtract, R(_qx), R(_qy))), Does.Contain("return (x - y) & 0xFFFF_FFFF_FFFF_FFFF"));
    Assert.That(Body(IrType.I64, [_lx, _ly], Bin(IrBinaryOp.Subtract, R(_lx), R(_ly))), Does.Contain("return (((x - y) & 0xFFFF_FFFF_FFFF_FFFF) ^ 0x8000_0000_0000_0000) - 0x8000_0000_0000_0000"));
  }

  [TestCase(IrBinaryOp.AddChecked, "x + y")]
  [TestCase(IrBinaryOp.SubtractChecked, "x - y")]
  [TestCase(IrBinaryOp.MultiplyChecked, "x * y")]
  public void CheckedArithmetic_TrapsOutsideTheType(IrBinaryOp op, string exact) {
    var text = Emit(Returning(IrType.I32, [_x, _y], Bin(op, R(_x), R(_y))));
    Assert.That(text, Does.Contain($"return _ply_checked({exact}, -2147483648, 2147483647)").And.Contain("def _ply_checked(value: int, low: int, high: int) -> int:"));
  }

  [Test]
  public void Shifts_MaskTheirCount_AndALogicalShiftOfASignedValueReadsItsBits() {
    Assert.That(Body(IrType.I32, [_x, _y], Bin(IrBinaryOp.ShiftRight, R(_x), R(_y))), Does.Contain("return x >> (y & 31)"));
    Assert.That(Body(IrType.I32, [_x, _y], Bin(IrBinaryOp.ShiftLeft, R(_x), R(_y))), Does.Contain("return (((x << (y & 31)) & 0xFFFFFFFF) ^ 0x80000000) - 0x80000000"));
    Assert.That(Body(IrType.U64, [_qx, _y], Bin(IrBinaryOp.ShiftLeft, R(_qx), R(_y))), Does.Contain("return (x << (y & 63)) & 0xFFFF_FFFF_FFFF_FFFF"));
    Assert.That(Body(IrType.I32, [_x, _y], Bin(IrBinaryOp.ShiftRightLogical, R(_x), R(_y))), Does.Contain("return ((((x & 0xFFFFFFFF) >> (y & 31)) & 0xFFFFFFFF) ^ 0x80000000) - 0x80000000"));
    Assert.That(Body(IrType.U32, [_ux, _y], Bin(IrBinaryOp.ShiftRightLogical, R(_ux), R(_y))), Does.Contain("return x >> (y & 31)"));
  }

  [Test]
  public void ShiftByAConstant_FoldsTheMask_AndALogicalShiftByOneOrMoreNeedsNoSignWrap() {
    Assert.That(Body(IrType.I32, [_x], Bin(IrBinaryOp.ShiftRightLogical, R(_x), I(33))), Does.Contain("return (x & 0xFFFFFFFF) >> 1\n"));
    Assert.That(Body(IrType.I32, [_x], Bin(IrBinaryOp.ShiftRightLogical, R(_x), I(32))), Does.Contain("return ((((x & 0xFFFFFFFF) >> 0) & 0xFFFFFFFF) ^ 0x80000000) - 0x80000000"));
    Assert.That(Body(IrType.I64, [_lx], Bin(IrBinaryOp.ShiftRight, R(_lx), I(65))), Does.Contain("return x >> 1\n"));
  }

  [Test]
  public void SignedDivision_TruncatesThroughItsHelpers_UnsignedFloors() {
    var divide = Emit(Returning(IrType.I32, [_x, _y], Bin(IrBinaryOp.Divide, R(_x), R(_y))));
    Assert.That(divide, Does.Contain("return _ply_sdiv(x, y, -2147483648)").And.Contain("def _ply_sdiv(").And.Not.Contain("def _ply_srem("));
    Assert.That(Body(IrType.I64, [_lx, _ly], Bin(IrBinaryOp.Remainder, R(_lx), R(_ly))), Does.Contain("return _ply_srem(x, y, -9223372036854775808)"));
    Assert.That(Body(IrType.U32, [_ux, _uy], Bin(IrBinaryOp.Divide, R(_ux), R(_uy))), Does.Contain("return x // y"));
    Assert.That(Body(IrType.U64, [_qx, _qy], Bin(IrBinaryOp.Remainder, R(_qx), R(_qy))), Does.Contain("return x % y"));
    Assert.That(Body(IrType.I32, [_x], Bin(IrBinaryOp.Divide, I(7), I(2))), Does.Contain("return 7 // 2"));
    Assert.That(Body(IrType.I32, [_x], Bin(IrBinaryOp.Divide, I(-7), I(2))), Does.Contain("_ply_sdiv(-7, 2, -2147483648)"));
    Assert.That(Body(IrType.I32, [_x], Bin(IrBinaryOp.Divide, I(7), I(-2))), Does.Contain("_ply_sdiv(7, -2, -2147483648)"));
  }

  [Test]
  public void BitwiseOperatorsAndComparisons_AreAsTheyStand() {
    Assert.That(Body(IrType.I32, [_x, _y], Bin(IrBinaryOp.And, R(_x), R(_y))), Does.Contain("return x & y"));
    Assert.That(Body(IrType.I32, [_x, _y], Bin(IrBinaryOp.Or, R(_x), R(_y))), Does.Contain("return x | y"));
    Assert.That(Body(IrType.I32, [_x, _y], Bin(IrBinaryOp.Xor, R(_x), R(_y))), Does.Contain("return x ^ y"));
    foreach (var (op, text) in new[] { (IrBinaryOp.Equal, "=="), (IrBinaryOp.NotEqual, "!="), (IrBinaryOp.Less, "<"), (IrBinaryOp.LessOrEqual, "<="), (IrBinaryOp.Greater, ">"), (IrBinaryOp.GreaterOrEqual, ">=") })
      Assert.That(Body(IrType.Bool, [_dx, _dy], Bin(op, R(_dx), R(_dy))), Does.Contain($"return x {text} y"));

    Assert.That(Body(IrType.Bool, [_b, _c], Bin(IrBinaryOp.LogicalAnd, R(_b), Bin(IrBinaryOp.LogicalOr, R(_c), new IrUnary(IrUnaryOp.LogicalNot, R(_b))))),
      Does.Contain("return b and (c or not b)"));
  }

  [Test]
  public void Unary_NegationWrapsOrTraps_TheComplementOfAnUnsignedValueIsAnXor() {
    Assert.That(Body(IrType.I32, [_x], new IrUnary(IrUnaryOp.Negate, R(_x))), Does.Contain("return ((-x & 0xFFFFFFFF) ^ 0x80000000) - 0x80000000"));
    Assert.That(Body(IrType.I64, [_lx], new IrUnary(IrUnaryOp.NegateChecked, R(_lx))), Does.Contain("return _ply_checked(-x, -9223372036854775808, 9223372036854775807)"));
    Assert.That(Body(IrType.I32, [_x], new IrUnary(IrUnaryOp.BitNot, R(_x))), Does.Contain("return ~x"));
    Assert.That(Body(IrType.U32, [_ux], new IrUnary(IrUnaryOp.BitNot, R(_ux))), Does.Contain("return x ^ 0xFFFFFFFF"));
    Assert.That(Body(IrType.F64, [_dx], new IrUnary(IrUnaryOp.Negate, R(_dx))), Does.Contain("return -x"));
  }

  [Test]
  public void NegationOfALiteral_IsTheLiteral() {
    Assert.That(Body(IrType.I32, [_x], new IrUnary(IrUnaryOp.Negate, I(int.MinValue))), Does.Contain("return -2147483648\n"));
    Assert.That(Body(IrType.F64, [_dx], new IrUnary(IrUnaryOp.Negate, C(IrType.F64, BitConverter.DoubleToInt64Bits(1.5)))), Does.Contain("return -1.5\n"));
  }

  [Test]
  public void FloatArithmetic_IsIeee754_AndBinary32IsRoundedAfterEveryOperation() {
    Assert.That(Body(IrType.F64, [_dx, _dy], Bin(IrBinaryOp.Add, R(_dx), R(_dy))), Does.Contain("return x + y"));
    var divide = Emit(Returning(IrType.F64, [_dx, _dy], Bin(IrBinaryOp.Divide, R(_dx), R(_dy))));
    Assert.That(divide, Does.Contain("return _ply_fdiv(x, y)").And.Contain("def _ply_fdiv(").And.Contain("import math"));
    Assert.That(divide[..divide.IndexOf("def ", StringComparison.Ordinal)], Does.Contain("__all__ = [\"f\"]"), "a helper is not exported");
    Assert.That(Body(IrType.F64, [_dx, _dy], Bin(IrBinaryOp.Remainder, R(_dx), R(_dy))), Does.Contain("return _ply_fmod(x, y)"));
    Assert.That(Body(IrType.F32, [_fx, _fy], Bin(IrBinaryOp.Add, R(_fx), R(_fy))), Does.Contain("return _ply_f32(x + y)"));
    Assert.That(Body(IrType.F32, [_fx, _fy], Bin(IrBinaryOp.Divide, R(_fx), R(_fy))), Does.Contain("return _ply_f32(_ply_fdiv(x, y))"));
    Assert.That(Body(IrType.F32, [_fx, _fy], Bin(IrBinaryOp.Remainder, R(_fx), R(_fy))), Does.Contain("return _ply_fmod(x, y)"));
    Assert.That(Emit(Returning(IrType.F32, [_fx, _fy], Bin(IrBinaryOp.Add, R(_fx), R(_fy)))), Does.Contain("import struct"));
  }

  [Test]
  public void Literals_OfEveryKind() {
    Assert.That(Body(IrType.Bool, [], C(IrType.Bool, 1)), Does.Contain("return True"));
    Assert.That(Body(IrType.U64, [], C(IrType.U64, -1)), Does.Contain("return 18446744073709551615"));
    Assert.That(Body(IrType.F64, [], C(IrType.F64, BitConverter.DoubleToInt64Bits(double.PositiveInfinity))), Does.Contain("return math.inf"));
    Assert.That(Body(IrType.F64, [], C(IrType.F64, BitConverter.DoubleToInt64Bits(double.NegativeInfinity))), Does.Contain("return -math.inf"));
    Assert.That(Body(IrType.F64, [], C(IrType.F64, 0x7FF8000000000000)), Does.Contain("return math.nan"));
    Assert.That(Body(IrType.F64, [], C(IrType.F64, 0x7FF8000000000001)), Does.Contain("return struct.unpack(\"<d\", struct.pack(\"<Q\", 0x7FF8_0000_0000_0001))[0]"));
    Assert.That(Body(IrType.F32, [], C(IrType.F32, 0x7FC00000)), Does.Contain("return math.nan"));
    Assert.That(Body(IrType.F32, [], C(IrType.F32, 0x7FC00001)), Does.Contain("return struct.unpack(\"<f\", struct.pack(\"<I\", 0x7FC00001))[0]"));
    Assert.That(Body(IrType.F64, [], C(IrType.F64, BitConverter.DoubleToInt64Bits(-0.0))), Does.Contain("return -0.0"));
    Assert.That(Body(IrType.F32, [], C(IrType.F32, BitConverter.SingleToInt32Bits(0.1f))), Does.Contain("return 0.10000000149011612"));
  }

  [Test]
  public void IntegerConversions_WrapTrapOrSaturate_AndWideningWritesNothing() {
    var i8 = L("x", IrType.I8);
    Assert.That(Body(IrType.U8, [_x], new IrConvert(IrType.U8, R(_x))), Does.Contain("return x & 0xFF"));
    Assert.That(Body(IrType.I16, [_x], new IrConvert(IrType.I16, R(_x))), Does.Contain("return ((x & 0xFFFF) ^ 0x8000) - 0x8000"));
    Assert.That(Body(IrType.I64, [i8], new IrConvert(IrType.I64, R(i8))), Does.Contain("return x\n"));
    Assert.That(Body(IrType.U32, [_x], new IrConvert(IrType.U32, R(_x), IrConversion.Trap)), Does.Contain("return _ply_checked(x, 0, 4294967295)"));
    Assert.That(Body(IrType.U8, [], new IrConvert(IrType.U8, I(300))), Does.Contain("return 44\n"));
    Assert.That(Body(IrType.I32, [_x], new IrConvert(IrType.I32, R(_x))), Does.Contain("return x\n"));
  }

  [Test]
  public void FloatConversions_RoundOnceAndSaturateOrTrap() {
    var i16 = L("x", IrType.I16);
    Assert.That(Body(IrType.F64, [_lx], new IrConvert(IrType.F64, R(_lx))), Does.Contain("return float(x)"));
    Assert.That(Body(IrType.F32, [i16], new IrConvert(IrType.F32, R(i16))), Does.Contain("return float(x)"));
    Assert.That(Body(IrType.F32, [_x], new IrConvert(IrType.F32, R(_x))), Does.Contain("return _ply_f32_of_int(x)"));
    Assert.That(Body(IrType.F32, [_dx], new IrConvert(IrType.F32, R(_dx))), Does.Contain("return _ply_f32(x)"));
    Assert.That(Body(IrType.F64, [_fx], new IrConvert(IrType.F64, R(_fx))), Does.Contain("return x\n"));
    Assert.That(Body(IrType.I32, [_dx], new IrConvert(IrType.I32, R(_dx), IrConversion.Saturate)), Does.Contain("return _ply_f2i_sat(x, -2147483648, 2147483647)"));
    Assert.That(Body(IrType.U64, [_dx], new IrConvert(IrType.U64, R(_dx), IrConversion.Trap)), Does.Contain("return _ply_f2i_trap(x, 0, 18446744073709551615)"));
  }

  [Test]
  public void BitCasts_GoThroughStruct() {
    Assert.That(Body(IrType.I64, [_dx], new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I64, [R(_dx)])), Does.Contain("return struct.unpack(\"<q\", struct.pack(\"<d\", x))[0]"));
    Assert.That(Body(IrType.I32, [_fx], new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I32, [R(_fx)])), Does.Contain("return struct.unpack(\"<i\", struct.pack(\"<f\", x))[0]"));
    Assert.That(Body(IrType.F32, [_x], new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F32, [R(_x)])), Does.Contain("return struct.unpack(\"<f\", struct.pack(\"<i\", x))[0]"));
    Assert.That(Body(IrType.F64, [_lx], new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F64, [R(_lx)])), Does.Contain("return struct.unpack(\"<d\", struct.pack(\"<q\", x))[0]"));
  }

  [Test]
  public void Conditional_IsAConditionalExpression()
    => Assert.That(Body(IrType.I32, [_b, _x], new IrConditional(R(_b), R(_x), I(0))), Does.Contain("return x if b else 0"));

  [Test]
  public void Intrinsics_PythonHasAFormOf() {
    Assert.That(Body(IrType.F64, [_dx], new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [R(_dx)])), Does.Contain("return _ply_sqrt(x)"));
    Assert.That(Body(IrType.F32, [_fx], new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F32, [R(_fx)])), Does.Contain("return _ply_f32(_ply_sqrt(x))"));
    Assert.That(Body(IrType.I32, [_x], new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [R(_x)])), Does.Contain("return (x & 0xFFFFFFFF).bit_count()"));
    Assert.That(Body(IrType.I32, [_ux], new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [R(_ux)])), Does.Contain("return x.bit_count()"));
    Assert.That(Body(IrType.I32, [_x, _y], new IrIntrinsic(IrIntrinsicOp.Min, IrType.I32, [R(_x), R(_y)])), Does.Contain("return min(x, y)"));
    Assert.That(Body(IrType.I32, [_x, _y], new IrIntrinsic(IrIntrinsicOp.Max, IrType.I32, [R(_x), R(_y)])), Does.Contain("return max(x, y)"));
    Assert.That(Body(IrType.F64, [_dx], new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F64, [R(_dx)])), Does.Contain("return abs(x)"));
    var carry = L("c", IrType.Bool);
    Assert.That(Body(IrType.Bool, [_ux, _uy, carry], new IrIntrinsic(IrIntrinsicOp.CarryOut, IrType.Bool, [R(_ux), R(_uy), R(carry)])), Does.Contain("return x + y + int(c) > 4294967295"));
    Assert.That(Body(IrType.Bool, [_ux, _uy, carry], new IrIntrinsic(IrIntrinsicOp.BorrowOut, IrType.Bool, [R(_ux), R(_uy), R(carry)])), Does.Contain("return x - y - int(c) < 0"));
  }

  [TestCase(IrIntrinsicOp.Min, "_ply_fmin(x, y)")]
  [TestCase(IrIntrinsicOp.Max, "_ply_fmax(x, y)")]
  public void FloatMinimumAndMaximum_AreIeee754(IrIntrinsicOp op, string call)
    => Assert.That(Body(IrType.F64, [_dx, _dy], new IrIntrinsic(op, IrType.F64, [R(_dx), R(_dy)])), Does.Contain("return " + call));

  [TestCase(IrIntrinsicOp.Floor, "_ply_floor")]
  [TestCase(IrIntrinsicOp.Ceiling, "_ply_ceil")]
  [TestCase(IrIntrinsicOp.Truncate, "_ply_trunc")]
  [TestCase(IrIntrinsicOp.RoundEven, "_ply_round_even")]
  [TestCase(IrIntrinsicOp.RoundAway, "_ply_round_away")]
  public void FloatRoundings_AreHelpers(IrIntrinsicOp op, string helper) {
    var text = Emit(Returning(IrType.F32, [_fx], new IrIntrinsic(op, IrType.F32, [R(_fx)])));
    Assert.That(text, Does.Contain($"return {helper}(x)").And.Contain($"def {helper}(value: float) -> float:"));
  }

  [Test]
  public void FloatSign_IsAnIntegerThatTrapsOnANaN()
    => Assert.That(Emit(Returning(IrType.I32, [_dx], new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(_dx)]))),
      Does.Contain("return _ply_fsign(x)").And.Contain("raise ArithmeticError(\"invalid operation\")"));

  [Test]
  public void PopCount_BeforePython310_IsTheLibrarysHelper() {
    var module = Returning(IrType.I32, [_x], new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [R(_x)]));
    Assert.That(Legalized(module, "py3.9"), Does.Not.Contain("bit_count").And.Contain("return _pop_count32("));
    Assert.That(Legalized(module, "py3.10"), Does.Contain("bit_count()"));
  }

  [Test]
  public void ALibraryHelper_IsAPrivateFunctionOfTheModule_ItsOriginInTheHeader() {
    var module = Returning(IrType.I32, [_x, _y], new IrIntrinsic(IrIntrinsicOp.RotateLeft, IrType.I32, [R(_x), R(_y)]));
    var lowered = PythonEmitter.Legalize(module, () => Polypiler.Cli.PrecomputedImplementations.Module, PythonProfile.Default, Polypiler.Core.IR.FloatMode.Accurate);
    var file = PythonEmitter.Emit(lowered, PythonProfile.Default).Single();
    Assert.That(file.Content, Does.Match(@"def _rotate_left32\(").And.Contain("Helpers added by polypiler"));
    Assert.That(file.Helpers.Select(helper => (helper.Symbol, helper.Origin)), Does.Contain(("_rotate_left32", "Hawkynt/Polypiler:Polypiler.Core.IR/Helpers/Operations/Rotate.cs")));
  }

  [Test]
  public void AnOperationWithoutAPythonForm_IsRefusedByName() {
    var array = L("a", IrType.Array(IrType.I32));
    var module = new IrModule("M", [Function("f", IrType.Void, [array], new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayClear, IrType.Void, [R(array)])), new IrReturn(null))]);
    Assert.That(Problems(module), Is.EqualTo(new[] { "error PLY1851: 'ArrayClear' cannot be expressed in Python profile 'py3.14' (function 'f')" }));
    Assert.That(() => PythonEmitter.Emit(module), Throws.ArgumentException.With.Message.Contains("ArrayClear"));
  }

  [Test]
  public void ATypeWithoutAPythonRepresentation_IsRefused() {
    var text = L("s", IrType.String);
    var module = new IrModule("M", [Function("f", IrType.I32, [text], new IrReturn(I(0)))]);
    Assert.That(Problems(module), Is.EqualTo(new[] { "error PLY1850: type 'str' has no Python representation in profile 'py3.14' (function 'f')" }));
  }

  [Test]
  public void HelpersAreDeclaredOnlyWhereUsed_AndEveryImportIsOfTheStandardLibrary() {
    var divides = Emit(Returning(IrType.I32, [_x, _y], Bin(IrBinaryOp.Divide, R(_x), R(_y))));
    var adds = Emit(Returning(IrType.U32, [_ux, _uy], Bin(IrBinaryOp.Add, R(_ux), R(_uy))));
    Assert.That(divides, Does.Contain("def _ply_sdiv("));
    Assert.That(adds, Does.Not.Contain("def _ply"));
    foreach (var text in new[] { divides, adds, Emit(Returning(IrType.F32, [_fx], new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F32, [R(_fx)]))) })
      Assert.That(Regex.Matches(text, @"^(?:import|from) (\w+)", RegexOptions.Multiline).Select(match => match.Groups[1].Value), Is.SubsetOf(new[] { "math", "struct", "typing" }));
  }

  [Test]
  public void Emission_IsDeterministic() {
    var module = Returning(IrType.I32, [_x, _y], Bin(IrBinaryOp.Divide, R(_x), R(_y)));
    var first = Emit(module);
    Assert.That(Emit(module with { }), Is.EqualTo(first));
  }

}
