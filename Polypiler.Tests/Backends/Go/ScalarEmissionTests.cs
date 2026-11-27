using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Go;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Go.GoIr;

namespace Polypiler.Tests.Backends.Go;

/// <summary>
/// The lowering table of the scalar issue (#692) as text: every integer operation on Go's sized type
/// of the IR's width, shifts with the count masked to the IR's width, the division through a helper
/// where Go wraps what the IR traps on, fusion barriers on products in floating-point sums, the
/// conversions from floating point through helpers, the intrinsics of <c>math/bits</c> and
/// <c>math</c>, constants folded with the IR's semantics. Whether the text computes the IR's result is
/// what the differential tests of the Go layer run.
/// </summary>
[TestFixture]
public sealed class ScalarEmissionTests {

  private static readonly IrLocal _a = L("a", IrType.I32), _b = L("b", IrType.I32);
  private static readonly IrLocal _ua = L("a", IrType.U32), _ub = L("b", IrType.U32);
  private static readonly IrLocal _la = L("a", IrType.I64), _lb = L("b", IrType.I64);
  private static readonly IrLocal _qa = L("a", IrType.U64), _qb = L("b", IrType.U64);
  private static readonly IrLocal _da = L("a", IrType.F64), _db = L("b", IrType.F64), _dc = L("c", IrType.F64);
  private static readonly IrLocal _fa = L("a", IrType.F32), _fb = L("b", IrType.F32);
  private static readonly IrLocal _n = L("n", IrType.I32);

  [TestCase(IrBinaryOp.Add, "return a + b")]
  [TestCase(IrBinaryOp.Subtract, "return a - b")]
  [TestCase(IrBinaryOp.Multiply, "return a * b")]
  [TestCase(IrBinaryOp.And, "return a & b")]
  [TestCase(IrBinaryOp.Or, "return a | b")]
  [TestCase(IrBinaryOp.Xor, "return a ^ b")]
  public void WrappingArithmetic_IsGosOperator(IrBinaryOp op, string expected) {
    Assert.That(Body(IrType.U32, [_ua, _ub], Bin(op, R(_ua), R(_ub))), Does.Contain(expected).And.Contain("func F(a, b uint32) uint32 {"));
    Assert.That(Body(IrType.I64, [_la, _lb], Bin(op, R(_la), R(_lb))), Does.Contain(expected));
  }

  [TestCase(IrBinaryOp.ShiftLeft, "return a << (n & 31)")]
  [TestCase(IrBinaryOp.ShiftRight, "return a >> (n & 31)")]
  [TestCase(IrBinaryOp.ShiftRightLogical, "return a >> (n & 31)")]
  public void ShiftOfAnUnsignedValue_MasksTheCountToTheWidthOfTheIr(IrBinaryOp op, string expected)
    => Assert.That(Body(IrType.U32, [_ua, _n], Bin(op, R(_ua), R(_n))), Does.Contain(expected));

  [Test]
  public void Shifts_MaskByTheWidth_LogicalShiftOfASignedValueGoesThroughItsUnsignedType() {
    Assert.That(Body(IrType.I64, [_la, _n], Bin(IrBinaryOp.ShiftLeft, R(_la), R(_n))), Does.Contain("return a << (n & 63)"));
    Assert.That(Body(IrType.I32, [_a, _n], Bin(IrBinaryOp.ShiftRightLogical, R(_a), R(_n))), Does.Contain("return int32(uint32(a) >> (n & 31))"));
    Assert.That(Body(IrType.I64, [_la, _n], Bin(IrBinaryOp.ShiftRightLogical, R(_la), R(_n))), Does.Contain("return int64(uint64(a) >> (n & 63))"));
    Assert.That(Body(IrType.U32, [_ua], Bin(IrBinaryOp.ShiftLeft, R(_ua), I(33))), Does.Contain("return a << 1"));
    Assert.That(Body(IrType.U32, [_n], Bin(IrBinaryOp.ShiftLeft, C(IrType.U32, 1), R(_n))), Does.Contain("return uint32(1) << (n & 31)"));
  }

  [Test]
  public void SignedDivision_GoesThroughAHelper_WhereTheDivisorMayBeMinusOneOrZero() {
    var text = Emit(Returning(IrType.I32, [_a, _b], Bin(IrBinaryOp.Divide, R(_a), R(_b))));
    Assert.That(text, Does.Contain("return plyDivInt32(a, b)").And.Contain("func plyDivInt32(a, b int32) int32 {").And.Contain("if b == -1 && a == -2147483648 {"));
    Assert.That(Body(IrType.I32, [_a], Bin(IrBinaryOp.Divide, R(_a), I(2))), Does.Contain("return a / 2"));
    Assert.That(Body(IrType.I64, [_la], Bin(IrBinaryOp.Remainder, R(_la), C(IrType.I64, 7))), Does.Contain("return a % 7"));
    Assert.That(Body(IrType.I32, [_a], Bin(IrBinaryOp.Remainder, R(_a), I(-1))), Does.Contain("return plyRemInt32(a, -1)"));
    Assert.That(Body(IrType.U32, [_ua, _ub], Bin(IrBinaryOp.Divide, R(_ua), R(_ub))), Does.Contain("return a / b"));
    Assert.That(Body(IrType.U32, [_ua], Bin(IrBinaryOp.Divide, R(_ua), C(IrType.U32, 0))), Does.Contain("return plyDivUint32(a, 0)"));
  }

  [TestCase(IrBinaryOp.AddChecked, "plyAddCheckedInt32")]
  [TestCase(IrBinaryOp.SubtractChecked, "plySubCheckedInt32")]
  [TestCase(IrBinaryOp.MultiplyChecked, "plyMulCheckedInt32")]
  public void CheckedArithmetic_IsAHelperThatPanics(IrBinaryOp op, string helper) {
    var text = Emit(Returning(IrType.I32, [_a, _b], Bin(op, R(_a), R(_b))));
    Assert.That(text, Does.Contain($"return {helper}(a, b)").And.Contain($"func {helper}(a, b int32) int32 {{").And.Contain("panic(\"integer overflow\")"));
  }

  [Test]
  public void CheckedArithmetic_On64Bits_UsesMathBitsForUnsignedValues() {
    var text = Emit(new IrModule("M", [
      Function("f", IrType.U64, [_qa, _qb], new IrReturn(Bin(IrBinaryOp.AddChecked, R(_qa), R(_qb)))),
      Function("g", IrType.U64, [_qa, _qb], new IrReturn(Bin(IrBinaryOp.SubtractChecked, R(_qa), R(_qb)))),
      Function("h", IrType.U64, [_qa, _qb], new IrReturn(Bin(IrBinaryOp.MultiplyChecked, R(_qa), R(_qb)))),
      Function("i", IrType.I64, [_la, _lb], new IrReturn(Bin(IrBinaryOp.MultiplyChecked, R(_la), R(_lb)))),
      Function("j", IrType.I64, [_la], new IrReturn(new IrUnary(IrUnaryOp.NegateChecked, R(_la)))),
      Function("k", IrType.U32, [_ua, _ub], new IrReturn(Bin(IrBinaryOp.SubtractChecked, R(_ua), R(_ub))))
    ]));
    Assert.That(text, Does.Contain("bits.Add64(a, b, 0)").And.Contain("bits.Sub64(a, b, 0)").And.Contain("bits.Mul64(a, b)")
      .And.Contain("math.MinInt64").And.Contain("func plyNegCheckedInt64(a int64) int64 {").And.Contain("if b > a {")
      .And.Contain("import (\n\t\"math\"\n\t\"math/bits\"\n)"));
  }

  [Test]
  public void ProductInASum_IsConvertedSoThatGoCannotFuseIt() {
    var product = Bin(IrBinaryOp.Multiply, R(_da), R(_db));
    Assert.That(Body(IrType.F64, [_da, _db, _dc], Bin(IrBinaryOp.Add, product, R(_dc))), Does.Contain("return float64(a*b) + c"));
    Assert.That(Body(IrType.F64, [_da, _db, _dc], Bin(IrBinaryOp.Subtract, R(_dc), product)), Does.Contain("return c - float64(a*b)"));
    Assert.That(Body(IrType.F32, [_fa, _fb], Bin(IrBinaryOp.Add, Bin(IrBinaryOp.Multiply, R(_fa), R(_fb)), R(_fa))), Does.Contain("return float32(a*b) + a"));
    Assert.That(Body(IrType.F64, [_da, _db], Bin(IrBinaryOp.Multiply, R(_da), R(_db))), Does.Contain("return a * b"));
  }

  [Test]
  public void FloatRemainder_IsMathMod_AnF32ThroughFloat64() {
    Assert.That(Body(IrType.F64, [_da, _db], Bin(IrBinaryOp.Remainder, R(_da), R(_db))), Does.Contain("return math.Mod(a, b)"));
    Assert.That(Body(IrType.F32, [_fa, _fb], Bin(IrBinaryOp.Remainder, R(_fa), R(_fb))), Does.Contain("return float32(math.Mod(float64(a), float64(b)))"));
  }

  [Test]
  public void Constants_AreTypedLiterals_FloatSpecialsAreCallsOfMath() {
    Assert.That(Body(IrType.U32, [], C(IrType.U32, 0x811C9DC5)), Does.Contain("return 0x811C9DC5"));
    Assert.That(Body(IrType.U64, [], C(IrType.U64, -1)), Does.Contain("return 0xFFFFFFFFFFFFFFFF"));
    Assert.That(Body(IrType.I32, [], I(-5)), Does.Contain("return -5"));
    Assert.That(Body(IrType.Bool, [], C(IrType.Bool, 1)), Does.Contain("return true"));
    Assert.That(Body(IrType.F64, [], F64(0.1)), Does.Contain("return 0.1"));
    Assert.That(Body(IrType.F64, [], F64(-2.5)), Does.Contain("return -2.5"));
    Assert.That(Body(IrType.F64, [], F64(1e20)), Does.Contain("return 1e+20"));
    Assert.That(Body(IrType.F32, [], F32(0.1f)), Does.Contain("return 0.1"));
    Assert.That(Body(IrType.F64, [], F64(double.PositiveInfinity)), Does.Contain("return math.Inf(1)"));
    Assert.That(Body(IrType.F32, [], F32(float.NegativeInfinity)), Does.Contain("return float32(math.Inf(-1))"));
    Assert.That(Body(IrType.F64, [], F64(-0.0)), Does.Contain("return math.Copysign(0, -1)"));
    Assert.That(Body(IrType.F64, [], F64(double.NaN)), Does.Contain("return math.Float64frombits(0x"));
    Assert.That(Body(IrType.F32, [], F32(float.NaN)), Does.Contain("return math.Float32frombits(0x"));
  }

  [Test]
  public void ConstantExpressions_AreFoldedWithTheSemanticsOfTheIr() {
    // Go would fold 0.1 + 0.2 exactly to 0.3 and refuse 200 + 100 of uint8 as an overflow.
    Assert.That(Body(IrType.F64, [], Bin(IrBinaryOp.Add, F64(0.1), F64(0.2))), Does.Contain("return 0.30000000000000004"));
    Assert.That(Body(IrType.U32, [], Bin(IrBinaryOp.Multiply, C(IrType.U32, 0x10000), C(IrType.U32, 0x10000))), Does.Contain("return 0\n"));
    Assert.That(Body(IrType.Bool, [], Bin(IrBinaryOp.Less, I(1), I(2))), Does.Contain("return true"));
    Assert.That(Body(IrType.I32, [], new IrUnary(IrUnaryOp.Negate, I(int.MinValue))), Does.Contain("return -2147483648"));
    Assert.That(Body(IrType.U8, [], new IrConvert(IrType.U8, I(300))), Does.Contain("return 44"));
    // A constant expression that traps is left to the helper, which traps when it runs.
    Assert.That(Body(IrType.I32, [], Bin(IrBinaryOp.Divide, I(1), I(0))), Does.Contain("return plyDivInt32(1, 0)"));
  }

  [Test]
  public void Conversions_WrapAsGosOwn_FromFloatingPointThroughHelpers() {
    Assert.That(Body(IrType.U8, [_a], new IrConvert(IrType.U8, R(_a))), Does.Contain("return uint8(a)"));
    Assert.That(Body(IrType.I64, [_ua], new IrConvert(IrType.I64, R(_ua))), Does.Contain("return int64(a)"));
    Assert.That(Body(IrType.F64, [_a], new IrConvert(IrType.F64, R(_a))), Does.Contain("return float64(a)"));
    Assert.That(Body(IrType.F32, [_da], new IrConvert(IrType.F32, R(_da))), Does.Contain("return float32(a)"));
    var saturated = Emit(Returning(IrType.I32, [_da], new IrConvert(IrType.I32, R(_da), IrConversion.Saturate)));
    Assert.That(saturated, Does.Contain("return plyFloatToInt32Sat(a)").And.Contain("if x != x {").And.Contain("if x >= 2147483648.0 {"));
    var trapping = Emit(Returning(IrType.U64, [_fa], new IrConvert(IrType.U64, R(_fa), IrConversion.Trap)));
    Assert.That(trapping, Does.Contain("return plyFloatToUint64Checked(float64(a))").And.Contain("if !(x > -1 && x < 18446744073709551616.0) {"));
    var signed = Emit(Returning(IrType.I64, [_da], new IrConvert(IrType.I64, R(_da), IrConversion.Trap)));
    Assert.That(signed, Does.Contain("x >= -9223372036854775808.0 && x < 9223372036854775808.0"));
    var unsigned = Emit(Returning(IrType.U16, [_da], new IrConvert(IrType.U16, R(_da), IrConversion.Saturate)));
    Assert.That(unsigned, Does.Contain("if x != x || x <= 0 {"));
    var narrowed = Emit(Returning(IrType.U8, [_a], new IrConvert(IrType.U8, R(_a), IrConversion.Trap)));
    Assert.That(narrowed, Does.Contain("return plyConvInt32ToUint8(a)").And.Contain("if v < 0 || v > 255 {"));
    Assert.That(Body(IrType.I64, [_a], new IrConvert(IrType.I64, R(_a), IrConversion.Trap)), Does.Contain("return int64(a)"));
  }

  [Test]
  public void NarrowArithmetic_IsTheOperatorOfTheNarrowType() {
    var a = L("a", IrType.U8);
    var b = L("b", IrType.U8);
    IrExpr Wide(IrLocal local) => new IrConvert(IrType.U32, R(local));
    Assert.That(Body(IrType.U8, [a, b], new IrConvert(IrType.U8, Bin(IrBinaryOp.Add, Wide(a), Wide(b)))), Does.Contain("return a + b"));
    Assert.That(Body(IrType.U8, [a], new IrConvert(IrType.U8, Bin(IrBinaryOp.Add, Wide(a), C(IrType.U32, 100)))), Does.Contain("return a + 100"));
    Assert.That(Body(IrType.U8, [a], new IrConvert(IrType.U8, Bin(IrBinaryOp.Add, Wide(a), C(IrType.U32, 300)))), Does.Contain("return uint8(uint32(a) + 300)"));
    Assert.That(Body(IrType.U8, [a], new IrConvert(IrType.U8, new IrUnary(IrUnaryOp.BitNot, Wide(a)))), Does.Contain("return ^a"));
  }

  [Test]
  public void Comparisons_OfWidenedValues_CompareTheValues_AndNegationReversesThem() {
    var a = L("a", IrType.U8);
    var b = L("b", IrType.U8);
    Assert.That(Body(IrType.Bool, [a, b], Bin(IrBinaryOp.Equal, new IrConvert(IrType.I32, R(a)), new IrConvert(IrType.I32, R(b)))), Does.Contain("return a == b"));
    Assert.That(Body(IrType.Bool, [_a, _b], new IrUnary(IrUnaryOp.LogicalNot, Bin(IrBinaryOp.Less, R(_a), R(_b)))), Does.Contain("return a >= b"));
    Assert.That(Body(IrType.Bool, [_da, _db], new IrUnary(IrUnaryOp.LogicalNot, Bin(IrBinaryOp.Less, R(_da), R(_db)))), Does.Contain("return !(a < b)"));
    var c = L("c", IrType.Bool);
    Assert.That(Body(IrType.Bool, [c], new IrUnary(IrUnaryOp.LogicalNot, new IrUnary(IrUnaryOp.LogicalNot, R(c)))), Does.Contain("return c\n"));
  }

  [Test]
  public void Unary_IsGosOperator() {
    Assert.That(Body(IrType.I32, [_a], new IrUnary(IrUnaryOp.Negate, R(_a))), Does.Contain("return -a"));
    Assert.That(Body(IrType.U32, [_ua], new IrUnary(IrUnaryOp.BitNot, R(_ua))), Does.Contain("return ^a"));
    Assert.That(Body(IrType.F64, [_da], new IrUnary(IrUnaryOp.Negate, R(_da))), Does.Contain("return -a"));
    Assert.That(Body(IrType.U32, [_ua, _ub], Bin(IrBinaryOp.And, R(_ua), new IrUnary(IrUnaryOp.BitNot, R(_ub)))), Does.Contain("return a &^ b"));
    Assert.That(Body(IrType.I32, [_a], new IrUnary(IrUnaryOp.NegateChecked, R(_a))), Does.Contain("return plyNegCheckedInt32(a)"));
  }

  [Test]
  public void Intrinsics_AreMathBits_ConvertedToTheResultType() {
    IrExpr Op(IrIntrinsicOp op, IrType type, params IrExpr[] arguments) => new IrIntrinsic(op, type, arguments);
    Assert.That(Body(IrType.U32, [_ua, _n], Op(IrIntrinsicOp.RotateLeft, IrType.U32, R(_ua), R(_n))), Does.Contain("return bits.RotateLeft32(a, int(n))"));
    Assert.That(Body(IrType.U32, [_ua, _n], Op(IrIntrinsicOp.RotateRight, IrType.U32, R(_ua), R(_n))), Does.Contain("return bits.RotateLeft32(a, -int(n))"));
    Assert.That(Body(IrType.I32, [_a, _n], Op(IrIntrinsicOp.RotateLeft, IrType.I32, R(_a), R(_n))), Does.Contain("return int32(bits.RotateLeft32(uint32(a), int(n)))"));
    Assert.That(Body(IrType.I32, [_qa], Op(IrIntrinsicOp.PopCount, IrType.I32, R(_qa))), Does.Contain("return int32(bits.OnesCount64(a))"));
    Assert.That(Body(IrType.I32, [_a], Op(IrIntrinsicOp.LeadingZeroCount, IrType.I32, R(_a))), Does.Contain("return int32(bits.LeadingZeros32(uint32(a)))"));
    Assert.That(Body(IrType.I32, [_ua], Op(IrIntrinsicOp.TrailingZeroCount, IrType.I32, R(_ua))), Does.Contain("return int32(bits.TrailingZeros32(a))"));
    Assert.That(Body(IrType.U32, [_ua], Op(IrIntrinsicOp.ByteSwap, IrType.U32, R(_ua))), Does.Contain("return bits.ReverseBytes32(a)"));
    Assert.That(Body(IrType.I64, [_la], Op(IrIntrinsicOp.BitReverse, IrType.I64, R(_la))), Does.Contain("return int64(bits.Reverse64(uint64(a)))"));
    Assert.That(Body(IrType.U32, [_la], Op(IrIntrinsicOp.HighWord, IrType.U32, R(_la))), Does.Contain("return uint32(uint64(a) >> 32)"));
    Assert.That(Body(IrType.I64, [_ua, _ub], Op(IrIntrinsicOp.JoinWords, IrType.I64, R(_ua), R(_ub))), Does.Contain("return int64(uint64(a)<<32 | uint64(b))"));
    var carry = L("c", IrType.Bool);
    Assert.That(Body(IrType.Bool, [_ua, _ub, carry], Op(IrIntrinsicOp.CarryOut, IrType.Bool, R(_ua), R(_ub), R(carry))), Does.Contain("return uint64(a)+uint64(b)+t > 4294967295"));
    Assert.That(Body(IrType.Bool, [_ua, _ub, carry], Op(IrIntrinsicOp.BorrowOut, IrType.Bool, R(_ua), R(_ub), R(carry))), Does.Contain("return uint64(a) < uint64(b)+t"));
    var u8 = L("a", IrType.U8);
    Assert.That(Body(IrType.U8, [u8], Op(IrIntrinsicOp.ByteSwap, IrType.U8, R(u8))), Does.Contain("return a\n"));
  }

  [Test]
  public void MinAndMax_AreTheBuiltinsFromGo121_AndHelpersBefore() {
    var call = new IrIntrinsic(IrIntrinsicOp.Min, IrType.I32, [R(_a), R(_b)]);
    Assert.That(Body(IrType.I32, [_a, _b], call, "go1.21"), Does.Contain("return min(a, b)"));
    var before = Emit(Returning(IrType.I32, [_a, _b], call), "go1.18");
    Assert.That(before, Does.Contain("return plyMinInt32(a, b)").And.Contain("func plyMinInt32(a, b int32) int32 {"));
    Assert.That(Emit(Returning(IrType.U64, [_qa, _qb], new IrIntrinsic(IrIntrinsicOp.Max, IrType.U64, [R(_qa), R(_qb)])), "go1.20"), Does.Contain("func plyMaxUint64(a, b uint64) uint64 {"));
    Assert.That(Body(IrType.I32, [_a], new IrIntrinsic(IrIntrinsicOp.Max, IrType.I32, [I(3), R(_a)])), Does.Contain("return max(int32(3), a)"));
    Assert.That(Body(IrType.F64, [_da, _db], new IrIntrinsic(IrIntrinsicOp.Min, IrType.F64, [R(_da), R(_db)])), Does.Contain("return min(a, b)"));
    Assert.That(Body(IrType.F32, [_fa, _fb], new IrIntrinsic(IrIntrinsicOp.Max, IrType.F32, [R(_fa), R(_fb)])), Does.Contain("return max(a, b)"));
    var floats = Emit(Returning(IrType.F32, [_fa, _fb], new IrIntrinsic(IrIntrinsicOp.Max, IrType.F32, [R(_fa), R(_fb)])), "go1.20");
    Assert.That(floats, Does.Contain("return float32(plyMaxFloat64(float64(a), float64(b)))").And.Contain("if a != a {").And.Contain("return math.Max(a, b)"));
    Assert.That(Emit(Returning(IrType.F64, [_da, _db], new IrIntrinsic(IrIntrinsicOp.Min, IrType.F64, [R(_da), R(_db)])), "go1.20"), Does.Contain("return plyMinFloat64(a, b)"));
    var clamp = new IrIntrinsic(IrIntrinsicOp.Clamp, IrType.I64, [R(_la), C(IrType.I64, -1), C(IrType.I64, 10)]);
    Assert.That(Body(IrType.I64, [_la], clamp), Does.Contain("return min(max(a, -1), 10)"));
    Assert.That(Emit(Returning(IrType.I64, [_la], clamp), "go1.18"), Does.Contain("return plyMinInt64(plyMaxInt64(a, -1), 10)"));
  }

  [TestCase(IrIntrinsicOp.Sqrt, "math.Sqrt")]
  [TestCase(IrIntrinsicOp.Abs, "math.Abs")]
  [TestCase(IrIntrinsicOp.Floor, "math.Floor")]
  [TestCase(IrIntrinsicOp.Ceiling, "math.Ceil")]
  [TestCase(IrIntrinsicOp.Truncate, "math.Trunc")]
  [TestCase(IrIntrinsicOp.RoundEven, "math.RoundToEven")]
  [TestCase(IrIntrinsicOp.RoundAway, "math.Round")]
  [TestCase(IrIntrinsicOp.RoundHalfUp, "plyRoundHalfUp")]
  public void ExactFloatingPointOperations_AreMath_AnF32ThroughFloat64(IrIntrinsicOp op, string function) {
    Assert.That(Body(IrType.F64, [_da], new IrIntrinsic(op, IrType.F64, [R(_da)])), Does.Contain($"return {function}(a)"));
    Assert.That(Body(IrType.F32, [_fa], new IrIntrinsic(op, IrType.F32, [R(_fa)])), Does.Contain($"return float32({function}(float64(a)))"));
  }

  [Test]
  public void BitCastsAndSign_AreMathAndAHelper() {
    Assert.That(Body(IrType.I64, [_da], new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I64, [R(_da)])), Does.Contain("return int64(math.Float64bits(a))"));
    Assert.That(Body(IrType.I32, [_fa], new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I32, [R(_fa)])), Does.Contain("return int32(math.Float32bits(a))"));
    Assert.That(Body(IrType.F64, [_la], new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F64, [R(_la)])), Does.Contain("return math.Float64frombits(uint64(a))"));
    Assert.That(Body(IrType.F32, [_a], new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F32, [R(_a)])), Does.Contain("return math.Float32frombits(uint32(a))"));
    var sign = Emit(Returning(IrType.I32, [_fa], new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(_fa)])));
    Assert.That(sign, Does.Contain("return plySign(float64(a))").And.Contain("panic(\"invalid operation\")"));
  }

  [Test]
  public void StringsAndText_AreGoStrings() {
    var s = L("s", IrType.String);
    var t = L("t", IrType.String);
    Assert.That(Body(IrType.String, [s, t], new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [R(s), R(t)])), Does.Contain("return s + t"));
    Assert.That(Body(IrType.Bool, [s, t], new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [R(s), R(t)])), Does.Contain("return s == t"));
    Assert.That(Body(IrType.String, [_a], new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [R(_a)])), Does.Contain("return strconv.FormatInt(int64(a), 10)"));
    Assert.That(Body(IrType.String, [_ua], new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [R(_ua)])), Does.Contain("return strconv.FormatUint(uint64(a), 10)"));
    var c = L("c", IrType.Bool);
    Assert.That(Body(IrType.String, [c], new IrIntrinsic(IrIntrinsicOp.FormatBoolDotNet, IrType.String, [R(c)])), Does.Contain("t := \"False\"\n\tif c {\n\t\tt = \"True\"\n\t}"));
    Assert.That(Body(IrType.String, [], new IrStrConst("a\"b")), Does.Contain("return \"a\\\"b\""));
    Assert.That(Problems(Returning(IrType.String, [], new IrStrConst("\uD800"))), Has.Some.Contains("PLY2151").And.Some.Contains("lone surrogate"));
    Assert.That(Body(IrType.String, [], new IrStrConst("😀")), Does.Contain("😀"));
  }

  [Test]
  public void TypesAndOperationsWithoutAGoForm_AreRefused() {
    var big = L("a", IrType.BigInt);
    Assert.That(Problems(Returning(IrType.BigInt, [big], R(big))), Has.Some.Contains("PLY2150"));
    Assert.That(Problems(Returning(IrType.I32, [_a], new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrStrConst("x")]))), Has.Some.Contains("PLY2151"));
    var other = L("b", IrType.BigInt);
    Assert.That(Problems(Returning(IrType.Bool, [big, other], Bin(IrBinaryOp.Less, R(big), R(other)))), Has.Some.Contains("PLY2151"));
    Assert.That(() => GoEmitter.Emit(Returning(IrType.BigInt, [big], R(big))), Throws.ArgumentException.With.Message.Contains("PLY2150").Or.Message.Contains("no Go representation"));
  }

  [Test]
  public void Writes_OnlyTheFloatOperationsWithoutPortableImplementation() {
    Assert.That(GoEmitter.Writes(new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [F64(2)])), Is.True);
    Assert.That(GoEmitter.Writes(new IrIntrinsic(IrIntrinsicOp.Sin, IrType.F64, [F64(2)])), Is.False);
  }

  [Test]
  public void Legalization_BindsTheLibraryForWhatGoLacks_AndPortableFloats() {
    var parity = Returning(IrType.I32, [_ua], new IrIntrinsic(IrIntrinsicOp.Parity, IrType.I32, [R(_ua)]));
    var files = Legalized(parity);
    Assert.That(files.Select(file => file.Path), Is.EquivalentTo(new[] { "go.mod", "m.go", GoEmitter.RuntimeFile }));
    Assert.That(files.Single(file => file.Path == "m.go").Content, Does.Contain("return popCount32(a) & 1"));
    var sine = Legalized(Returning(IrType.F64, [_da], new IrIntrinsic(IrIntrinsicOp.Sin, IrType.F64, [R(_da)])));
    Assert.That(sine.Select(file => file.Path), Does.Contain(GoEmitter.RuntimeFile));
    Assert.That(sine.Single(file => file.Path == GoEmitter.RuntimeFile).Helpers, Is.Not.Empty);
  }

}
