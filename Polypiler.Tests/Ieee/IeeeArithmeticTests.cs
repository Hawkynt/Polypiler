using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Tests.Ieee.Support;
using Polypiler.Tests.Precomputed.Support;

namespace Polypiler.Tests.Ieee;

/// <summary>
/// #116: the arithmetic of <c>binary32</c> and <c>binary64</c> in the five rounding modes, the fourteen
/// comparisons and the classification, as the IR states them on the node and the interpreter computes
/// them exactly (<see cref="IeeeOperations"/>, <see cref="IeeeFormat"/>). Every directed result is
/// compared with an oracle built on the host's own nearest-even arithmetic and an error-free
/// transformation (<see cref="IeeeOracle"/>).
/// </summary>
[TestFixture]
public sealed class IeeeArithmeticTests {

  private static readonly IeeeFormat _double = IeeeFormats.Binary64;
  private static readonly IeeeFormat _single = IeeeFormats.Binary32;

  /// <summary>Runs <c>op(a, b, mode)</c> through a module in the interpreter.</summary>
  private static object Interpret(IrIntrinsicOp op, object left, object right, RoundingMode mode) {
    var type = left is float ? IrType.F32 : IrType.F64;
    var a = new IrLocal("a", type);
    var b = new IrLocal("b", type);
    var node = IeeeOperations.Arithmetic(op, new IrLocalRef(a), new IrLocalRef(b), mode);
    var module = new IrModule("M", [new IrFunction("F", true, type, [a, b], [], new IrBlock([new IrReturn(node)]))]);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return IrInterpreter.Call(module, "F", [left, right])!;
  }

  private static bool Compare(FloatPredicate predicate, object left, object right) {
    var type = left is float ? IrType.F32 : IrType.F64;
    var a = new IrLocal("a", type);
    var b = new IrLocal("b", type);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Bool, [a, b], [], new IrBlock([
      new IrReturn(IeeeOperations.Compare(predicate, new IrLocalRef(a), new IrLocalRef(b)))
    ]))]);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return (bool)IrInterpreter.Call(module, "F", [left, right])!;
  }

  private static long Bits(object value) => (long)IeeeFormats.BitsOf(value);

  // ---------------------------------------------------------------- the scenarios of the issue

  [TestCase(IrIntrinsicOp.FloatAdd, RoundingMode.NearestEven, 0.1, 0.2, 0x3FD3333333333334L)]
  [TestCase(IrIntrinsicOp.FloatDivide, RoundingMode.NearestEven, 1.0, 3.0, 0x3FD5555555555555L)]
  [TestCase(IrIntrinsicOp.FloatDivide, RoundingMode.Up, 1.0, 3.0, 0x3FD5555555555556L)]
  [TestCase(IrIntrinsicOp.FloatDivide, RoundingMode.Down, -1.0, 3.0, unchecked((long)0xBFD5555555555556UL))]
  [TestCase(IrIntrinsicOp.FloatDivide, RoundingMode.TowardZero, -1.0, 3.0, unchecked((long)0xBFD5555555555555UL))]
  [TestCase(IrIntrinsicOp.FloatSubtract, RoundingMode.Up, 1.0, 1e-30, 0x3FF0000000000000L)]
  [TestCase(IrIntrinsicOp.FloatSubtract, RoundingMode.Down, 1.0, 1e-30, 0x3FEFFFFFFFFFFFFFL)]
  [TestCase(IrIntrinsicOp.FloatMultiply, RoundingMode.Up, 0.1, 0.1, 0x3F847AE147AE147CL)]
  public void Double_IsRoundedInTheStatedMode(IrIntrinsicOp op, RoundingMode mode, double left, double right, long bits)
    => Assert.That(Bits(Interpret(op, left, right, mode)), Is.EqualTo(bits));

  [Test]
  public void Single_IsRoundedInTheFormat()
    => Assert.That(Bits(Interpret(IrIntrinsicOp.FloatAdd, 0.1f, 0.2f, RoundingMode.NearestEven)), Is.EqualTo(0x3E99999AL));

  [Test]
  public void TwoToThe53PlusOne_InEachMode() {
    // 2^53 + 1 lies halfway between 2^53 and 2^53 + 2.
    var expected = new[] { 9007199254740992.0, 9007199254740992.0, 9007199254740994.0, 9007199254740992.0, 9007199254740994.0 };
    for (var mode = 0; mode < 5; ++mode)
      Assert.That((double)Interpret(IrIntrinsicOp.FloatAdd, 9007199254740992.0, 1.0, (RoundingMode)mode), Is.EqualTo(expected[mode]), ((RoundingMode)mode).ToString());
  }

  [Test]
  public void ExceptionalValues_FollowIeee754() {
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatDivide, 1.0, 0.0, RoundingMode.NearestEven)), Is.EqualTo(0x7FF0000000000000L));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatDivide, -1.0, 0.0, RoundingMode.NearestEven)), Is.EqualTo(unchecked((long)0xFFF0000000000000UL)));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatDivide, 0.0, 0.0, RoundingMode.NearestEven)), Is.EqualTo(0x7FF8000000000000L), "the canonical NaN");
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatAdd, double.PositiveInfinity, double.NegativeInfinity, RoundingMode.Up)), Is.EqualTo(0x7FF8000000000000L));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatMultiply, double.PositiveInfinity, 0.0, RoundingMode.Up)), Is.EqualTo(0x7FF8000000000000L));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatDivide, double.PositiveInfinity, double.NegativeInfinity, RoundingMode.Up)), Is.EqualTo(0x7FF8000000000000L));
    Assert.That((double)Interpret(IrIntrinsicOp.FloatDivide, double.PositiveInfinity, -2.0, RoundingMode.TowardZero), Is.EqualTo(double.NegativeInfinity));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatDivide, -3.0, double.PositiveInfinity, RoundingMode.Up)), Is.EqualTo(unchecked((long)0x8000000000000000UL)));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatMultiply, -3.0, double.PositiveInfinity, RoundingMode.Down)), Is.EqualTo(unchecked((long)0xFFF0000000000000UL)));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatAdd, double.NegativeInfinity, 5.0, RoundingMode.Up)), Is.EqualTo(unchecked((long)0xFFF0000000000000UL)));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatAdd, 5.0, double.PositiveInfinity, RoundingMode.Down)), Is.EqualTo(0x7FF0000000000000L));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatMultiply, BitConverter.Int64BitsToDouble(0x7FF0000000000005L), 1.0, RoundingMode.Up)), Is.EqualTo(0x7FF8000000000000L));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatDivide, 1.0, double.NaN, RoundingMode.Up)), Is.EqualTo(0x7FF8000000000000L));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatAdd, 1.0, double.NaN, RoundingMode.Up)), Is.EqualTo(0x7FF8000000000000L));
  }

  /// <summary>Test design 7: overflow to infinity or the largest finite number, by mode and sign.</summary>
  [TestCase(RoundingMode.NearestEven, 0x7F800000L, 0xFF800000L)]
  [TestCase(RoundingMode.NearestAway, 0x7F800000L, 0xFF800000L)]
  [TestCase(RoundingMode.TowardZero, 0x7F7FFFFFL, 0xFF7FFFFFL)]
  [TestCase(RoundingMode.Up, 0x7F800000L, 0xFF7FFFFFL)]
  [TestCase(RoundingMode.Down, 0x7F7FFFFFL, 0xFF800000L)]
  public void Overflow_IsInfinityOrTheLargestNumberByMode(RoundingMode mode, long positive, long negative) {
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatMultiply, float.MaxValue, 2.0f, mode)), Is.EqualTo(positive));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatMultiply, float.MaxValue, -2.0f, mode)), Is.EqualTo(negative));
  }

  /// <summary>Test design 8: underflow to a subnormal number and to zero.</summary>
  [Test]
  public void Underflow_GivesSubnormalsAndZeros() {
    var minNormal = BitConverter.Int64BitsToDouble(0x0010000000000000L);
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatDivide, minNormal, 2.0, RoundingMode.NearestEven)), Is.EqualTo(0x0008000000000000L));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatDivide, double.Epsilon, 2.0, RoundingMode.NearestEven)), Is.EqualTo(0L), "a tie to the even zero");
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatDivide, double.Epsilon, 2.0, RoundingMode.NearestAway)), Is.EqualTo(1L));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatDivide, double.Epsilon, 3.0, RoundingMode.Up)), Is.EqualTo(1L));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatDivide, -double.Epsilon, 3.0, RoundingMode.Up)), Is.EqualTo(unchecked((long)0x8000000000000000UL)), "a negative zero");
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatMultiply, double.Epsilon, 0.75, RoundingMode.NearestEven)), Is.EqualTo(1L));
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatMultiply, 3 * double.Epsilon, 0.5, RoundingMode.NearestEven)), Is.EqualTo(2L), "1.5 ulp ties to the even 2");
    Assert.That(Bits(Interpret(IrIntrinsicOp.FloatAdd, minNormal, -double.Epsilon, RoundingMode.NearestEven)), Is.EqualTo(0x000FFFFFFFFFFFFFL), "the largest subnormal");
  }

  /// <summary>An exact zero sum: <c>-0</c> in <c>rdn</c> or of two <c>-0</c>, <c>+0</c> otherwise (IEEE 754-2019 §6.3).</summary>
  [Test]
  public void ExactZeroSum_HasTheSignOfTheMode() {
    foreach (RoundingMode mode in Enum.GetValues<RoundingMode>()) {
      var negative = mode == RoundingMode.Down ? unchecked((long)0x8000000000000000UL) : 0L;
      Assert.That(Bits(Interpret(IrIntrinsicOp.FloatSubtract, 1.5, 1.5, mode)), Is.EqualTo(negative), mode + ": x - x");
      Assert.That(Bits(Interpret(IrIntrinsicOp.FloatAdd, 0.0, -0.0, mode)), Is.EqualTo(negative), mode + ": +0 + -0");
      Assert.That(Bits(Interpret(IrIntrinsicOp.FloatAdd, 0.0, 0.0, mode)), Is.EqualTo(0L), mode + ": +0 + +0");
      Assert.That(Bits(Interpret(IrIntrinsicOp.FloatAdd, -0.0, -0.0, mode)), Is.EqualTo(unchecked((long)0x8000000000000000UL)), mode + ": -0 + -0");
      Assert.That(Bits(Interpret(IrIntrinsicOp.FloatAdd, -0.0, 2.0, mode)), Is.EqualTo(Bits(2.0)), mode + ": a zero summand");
    }
  }

  // ---------------------------------------------------------------- against the oracle

  private static IEnumerable<(double, double)> DoublePairs(int seed, int low, int high) {
    var random = new Random(seed);
    for (var index = 0; index < 4000; ++index) {
      var left = (random.NextDouble() + 0.5) * Math.Pow(2, random.Next(low, high)) * (random.Next(2) == 0 ? 1 : -1);
      var right = (random.NextDouble() + 0.5) * Math.Pow(2, random.Next(low, high)) * (random.Next(2) == 0 ? 1 : -1);
      yield return (left, right);
    }

    // Ties and cancellation: neighbours, halves of an ulp, numbers that differ in one bit.
    yield return (1.0, Math.Pow(2, -53));
    yield return (1.0, -Math.Pow(2, -54));
    yield return (Math.BitIncrement(1.0), Math.Pow(2, -53));
    yield return (3.0, Math.Pow(2, -52));
    yield return (1.0, -Math.BitDecrement(1.0));
    yield return (-1.0, Math.Pow(2, -53) * 3);
  }

  [Test]
  public void DoubleArithmetic_MatchesTheOracleInEveryMode() {
    foreach (var (left, right) in DoublePairs(116, -40, 40)) {
      for (var mode = 0; mode < 5; ++mode) {
        var rounding = (RoundingMode)mode;
        var (a, b) = ((ulong)Bits(left), (ulong)Bits(right));
        Assert.That(_double.Add(a, b, rounding), Is.EqualTo(IeeeFormats.BitsOf(IeeeOracle.Add(left, right, mode))), $"{left:R} + {right:R} in {rounding}");
        Assert.That(_double.Subtract(a, b, rounding), Is.EqualTo(IeeeFormats.BitsOf(IeeeOracle.Add(left, -right, mode))), $"{left:R} - {right:R} in {rounding}");
        Assert.That(_double.Multiply(a, b, rounding), Is.EqualTo(IeeeFormats.BitsOf(IeeeOracle.Multiply(left, right, mode))), $"{left:R} * {right:R} in {rounding}");
        Assert.That(_double.Divide(a, b, rounding), Is.EqualTo(IeeeFormats.BitsOf(IeeeOracle.Divide(left, right, mode))), $"{left:R} / {right:R} in {rounding}");
      }
    }
  }

  [Test]
  public void SingleArithmetic_MatchesTheOracleInEveryMode() {
    var random = new Random(1160);
    for (var index = 0; index < 4000; ++index) {
      var left = (float)((random.NextDouble() + 0.5) * Math.Pow(2, random.Next(-12, 12)) * (random.Next(2) == 0 ? 1 : -1));
      var right = (float)((random.NextDouble() + 0.5) * Math.Pow(2, random.Next(-12, 12)) * (random.Next(2) == 0 ? 1 : -1));
      for (var mode = 0; mode < 5; ++mode) {
        var rounding = (RoundingMode)mode;
        var (a, b) = ((ulong)Bits(left), (ulong)Bits(right));
        Assert.That(_single.Add(a, b, rounding), Is.EqualTo(IeeeFormats.BitsOf(IeeeOracle.Add(left, right, mode))), $"{left:R} + {right:R} in {rounding}");
        Assert.That(_single.Multiply(a, b, rounding), Is.EqualTo(IeeeFormats.BitsOf(IeeeOracle.Multiply(left, right, mode))), $"{left:R} * {right:R} in {rounding}");
        Assert.That(_single.Divide(a, b, rounding), Is.EqualTo(IeeeFormats.BitsOf(IeeeOracle.Divide(left, right, mode))), $"{left:R} / {right:R} in {rounding}");
      }
    }
  }

  /// <summary>In <c>rne</c> every operation is the host's, also where the oracle's transformations would not be exact: subnormal results and overflow.</summary>
  [Test]
  public void NearestEven_IsTheHostsArithmetic() {
    foreach (var (left, right) in DoublePairs(1161, -1080, 1030)) {
      var (a, b) = ((ulong)Bits(left), (ulong)Bits(right));
      Assert.That(_double.Add(a, b, RoundingMode.NearestEven), Is.EqualTo(IeeeFormats.BitsOf(left + right)), $"{left:R} + {right:R}");
      Assert.That(_double.Multiply(a, b, RoundingMode.NearestEven), Is.EqualTo(IeeeFormats.BitsOf(left * right)), $"{left:R} * {right:R}");
      Assert.That(_double.Divide(a, b, RoundingMode.NearestEven), Is.EqualTo(IeeeFormats.BitsOf(left / right)), $"{left:R} / {right:R}");
      var (x, y) = ((float)left, (float)right);
      if (float.IsFinite(x) && float.IsFinite(y) && x != 0 && y != 0) {
        Assert.That(_single.Add(IeeeFormats.BitsOf(x), IeeeFormats.BitsOf(y), RoundingMode.NearestEven), Is.EqualTo(IeeeFormats.BitsOf(x + y)), $"{x:R} + {y:R}");
        Assert.That(_single.Multiply(IeeeFormats.BitsOf(x), IeeeFormats.BitsOf(y), RoundingMode.NearestEven), Is.EqualTo(IeeeFormats.BitsOf(x * y)), $"{x:R} * {y:R}");
        Assert.That(_single.Divide(IeeeFormats.BitsOf(x), IeeeFormats.BitsOf(y), RoundingMode.NearestEven), Is.EqualTo(IeeeFormats.BitsOf(x / y)), $"{x:R} / {y:R}");
      }
    }
  }

  // ---------------------------------------------------------------- comparisons

  /// <summary>Test design 4: each predicate on a pair that is less, equal, greater and unordered.</summary>
  [TestCase(FloatPredicate.OrderedEqual, false, true, false, false)]
  [TestCase(FloatPredicate.OrderedNotEqual, true, false, true, false)]
  [TestCase(FloatPredicate.OrderedLess, true, false, false, false)]
  [TestCase(FloatPredicate.OrderedLessOrEqual, true, true, false, false)]
  [TestCase(FloatPredicate.OrderedGreater, false, false, true, false)]
  [TestCase(FloatPredicate.OrderedGreaterOrEqual, false, true, true, false)]
  [TestCase(FloatPredicate.Ordered, true, true, true, false)]
  [TestCase(FloatPredicate.UnorderedEqual, false, true, false, true)]
  [TestCase(FloatPredicate.UnorderedNotEqual, true, false, true, true)]
  [TestCase(FloatPredicate.UnorderedLess, true, false, false, true)]
  [TestCase(FloatPredicate.UnorderedLessOrEqual, true, true, false, true)]
  [TestCase(FloatPredicate.UnorderedGreater, false, false, true, true)]
  [TestCase(FloatPredicate.UnorderedGreaterOrEqual, false, true, true, true)]
  [TestCase(FloatPredicate.Unordered, false, false, false, true)]
  public void Predicate_OnLessEqualGreaterAndUnordered(FloatPredicate predicate, bool less, bool equal, bool greater, bool unordered) {
    Assert.That(new[] { Compare(predicate, -2.0, 1.0), Compare(predicate, -0.0, 0.0), Compare(predicate, double.PositiveInfinity, 1.0), Compare(predicate, double.NaN, 1.0) },
      Is.EqualTo(new[] { less, equal, greater, unordered }), "f64");
    Assert.That(new[] { Compare(predicate, -2.0f, 1.0f), Compare(predicate, 3.5f, 3.5f), Compare(predicate, 1.0f, float.NegativeInfinity), Compare(predicate, 1.0f, float.NaN) },
      Is.EqualTo(new[] { less, equal, greater, unordered }), "f32");
    Assert.That(IeeeNames.TryParse(IeeeNames.Of(predicate), out FloatPredicate parsed) && parsed == predicate, Is.True);
  }

  [Test]
  public void ComparisonsWithNaN_AreAsTheIssueStatesThem() {
    Assert.That(Compare(FloatPredicate.OrderedEqual, double.NaN, double.NaN), Is.False);
    Assert.That(Compare(FloatPredicate.UnorderedNotEqual, double.NaN, double.NaN), Is.True);
    Assert.That(Compare(FloatPredicate.OrderedLess, double.NaN, 1.0), Is.False);
    Assert.That(Compare(FloatPredicate.Unordered, double.NaN, 1.0), Is.True);
    Assert.That(Compare(FloatPredicate.OrderedEqual, -0.0, 0.0), Is.True);
    Assert.That(Compare(FloatPredicate.OrderedLess, -1e-300, -0.0), Is.True, "a negative number below a negative zero");
    Assert.That(Compare(FloatPredicate.OrderedGreater, -1.0, -2.0), Is.True, "the sign reverses the order of the magnitudes");
  }

  [Test]
  public void AnUnknownPredicate_IsRefused() {
    Assert.That(() => _double.Compare(0, 0, (FloatPredicate)14), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(() => _double.Compare(_double.CanonicalNaN, 0, (FloatPredicate)(-1)), Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  // ---------------------------------------------------------------- classification

  [TestCase(0x0000000000000000L, FloatClass.Zero)]
  [TestCase(unchecked((long)0x8000000000000000UL), FloatClass.Zero | FloatClass.Negative)]
  [TestCase(0x0000000000000001L, FloatClass.Subnormal)]
  [TestCase(0x000FFFFFFFFFFFFFL, FloatClass.Subnormal)]
  [TestCase(0x0010000000000000L, FloatClass.Normal)]
  [TestCase(unchecked((long)0xBFF0000000000000UL), FloatClass.Normal | FloatClass.Negative)]
  [TestCase(0x7FEFFFFFFFFFFFFFL, FloatClass.Normal)]
  [TestCase(0x7FF0000000000000L, FloatClass.Infinite)]
  [TestCase(unchecked((long)0xFFF0000000000000UL), FloatClass.Infinite | FloatClass.Negative)]
  [TestCase(0x7FF8000000000000L, FloatClass.QuietNaN)]
  [TestCase(0x7FF0000000000001L, FloatClass.SignalingNaN)]
  [TestCase(unchecked((long)0xFFF8000000000001UL), FloatClass.QuietNaN | FloatClass.Negative)]
  public void Classify_Double(long bits, FloatClass expected) {
    var x = new IrLocal("x", IrType.F64);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.U8, [x], [], new IrBlock([new IrReturn(IeeeOperations.Classify(new IrLocalRef(x)))]))]);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That((FloatClass)Convert.ToInt32(IrInterpreter.Call(module, "F", [BitConverter.Int64BitsToDouble(bits)])), Is.EqualTo(expected));
  }

  [TestCase(0x00000000u, FloatClass.Zero)]
  [TestCase(0x00000001u, FloatClass.Subnormal)]
  [TestCase(0x00800000u, FloatClass.Normal)]
  [TestCase(0xFF800000u, FloatClass.Infinite | FloatClass.Negative)]
  [TestCase(0x7FC00000u, FloatClass.QuietNaN)]
  [TestCase(0x7F800001u, FloatClass.SignalingNaN)]
  public void Classify_Single(uint bits, FloatClass expected) => Assert.That(_single.Classify(bits), Is.EqualTo(expected));

  // ---------------------------------------------------------------- the rules of the verifier

  private static IReadOnlyList<Diagnostic> Verify(IrExpr node, params IrLocal[] parameters)
    => IrVerifier.Verify(new IrModule("M", [new IrFunction("F", true, node.Type, parameters, [], new IrBlock([new IrReturn(node)]))]));

  [Test]
  public void MixedFormats_AreRejected() {
    var a = new IrLocal("a", IrType.F64);
    var b = new IrLocal("b", IrType.F32);
    var node = new IrIntrinsic(IrIntrinsicOp.FloatAdd, IrType.F64, [new IrLocalRef(a), new IrLocalRef(b), new IrConst(IrType.I32, 0)]);
    Assert.That(Verify(node, a, b).Single().Message, Does.Contain("FloatAdd with the type f64 is applied to (f64, f32, i32)"));
  }

  [Test]
  public void AnIntegerOperand_IsRejected() {
    var a = new IrLocal("a", IrType.U32);
    var node = new IrIntrinsic(IrIntrinsicOp.FloatAdd, IrType.U32, [new IrLocalRef(a), new IrLocalRef(a), new IrConst(IrType.I32, 0)]);
    Assert.That(Verify(node, a), Has.Count.EqualTo(1));
    Assert.That(Verify(new IrIntrinsic(IrIntrinsicOp.FloatClassify, IrType.U8, []), []), Has.Count.EqualTo(1), "no operand");
  }

  [TestCase(IrIntrinsicOp.FloatAdd, 5, "unknown rounding mode 5 of FloatAdd")]
  [TestCase(IrIntrinsicOp.FloatDivide, -1, "unknown rounding mode -1 of FloatDivide")]
  [TestCase(IrIntrinsicOp.FloatCompare, 14, "unknown predicate 14 of FloatCompare")]
  public void AnUnknownModeOrPredicate_IsRejected(IrIntrinsicOp op, int value, string message) {
    var a = new IrLocal("a", IrType.F64);
    var type = op == IrIntrinsicOp.FloatCompare ? IrType.Bool : IrType.F64;
    var node = new IrIntrinsic(op, type, [new IrLocalRef(a), new IrLocalRef(a), new IrConst(IrType.I32, value)]);
    Assert.That(Verify(node, a).Single().Message, Does.Contain(message));
  }

  [Test]
  public void AModeThatIsNoConstant_IsRejected() {
    var a = new IrLocal("a", IrType.F64);
    var mode = new IrLocal("m", IrType.I32);
    var node = new IrIntrinsic(IrIntrinsicOp.FloatMultiply, IrType.F64, [new IrLocalRef(a), new IrLocalRef(a), new IrLocalRef(mode)]);
    Assert.That(Verify(node, a, mode).Single().Message, Does.Contain("the rounding mode of FloatMultiply is no constant"));
    var compare = new IrIntrinsic(IrIntrinsicOp.FloatCompare, IrType.Bool, [new IrLocalRef(a), new IrLocalRef(a), new IrLocalRef(mode)]);
    Assert.That(Verify(compare, a, mode).Single().Message, Does.Contain("the predicate of FloatCompare is no constant"));
  }

  [Test]
  public void TheWrongResultType_IsRejected() {
    var a = new IrLocal("a", IrType.F32);
    Assert.That(Verify(new IrIntrinsic(IrIntrinsicOp.FloatClassify, IrType.I32, [new IrLocalRef(a)]), a), Has.Count.EqualTo(1));
    Assert.That(Verify(new IrIntrinsic(IrIntrinsicOp.FloatCompare, IrType.F32, [new IrLocalRef(a), new IrLocalRef(a), new IrConst(IrType.I32, 0)]), a), Has.Count.EqualTo(1));
    Assert.That(Verify(new IrIntrinsic(IrIntrinsicOp.FloatAdd, IrType.F64, [new IrLocalRef(a), new IrLocalRef(a), new IrConst(IrType.I32, 0)]), a), Has.Count.EqualTo(1));
    Assert.That(IeeeOperations.Problem(new IrIntrinsic(IrIntrinsicOp.FloatAdd, IrType.F32, [new IrLocalRef(a)])), Is.Null, "the arity is the typing rule's to report");
    Assert.That(IeeeOperations.Problem(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F32, [new IrLocalRef(a)])), Is.Null);
  }

  [Test]
  public void TheBuilders_CheckTheirArguments() {
    var a = new IrConst(IrType.F64, 0);
    Assert.That(() => IeeeOperations.Arithmetic(IrIntrinsicOp.Abs, a, a, RoundingMode.Up), Throws.ArgumentException);
    Assert.That(() => IeeeOperations.Arithmetic(IrIntrinsicOp.FloatAdd, null!, a, RoundingMode.Up), Throws.ArgumentNullException);
    Assert.That(() => IeeeOperations.Arithmetic(IrIntrinsicOp.FloatAdd, a, null!, RoundingMode.Up), Throws.ArgumentNullException);
    Assert.That(() => IeeeOperations.Compare(FloatPredicate.Ordered, null!, a), Throws.ArgumentNullException);
    Assert.That(() => IeeeOperations.Compare(FloatPredicate.Ordered, a, null!), Throws.ArgumentNullException);
    Assert.That(() => IeeeOperations.Classify(null!), Throws.ArgumentNullException);
    Assert.That(() => IeeeOperations.Problem(null!), Throws.ArgumentNullException);
    Assert.That(() => IeeeOperations.Evaluate(null!, []), Throws.ArgumentNullException);
    Assert.That(() => IeeeOperations.Evaluate(IeeeOperations.Classify(a), null!), Throws.ArgumentNullException);
    Assert.That(IeeeOperations.ModeOf(IeeeOperations.Arithmetic(IrIntrinsicOp.FloatSubtract, a, a, RoundingMode.NearestAway)), Is.EqualTo(RoundingMode.NearestAway));
    Assert.That(IeeeOperations.Operations, Has.Count.EqualTo(6));
    Assert.That(IeeeOperations.IsArithmetic(IrIntrinsicOp.FloatCompare), Is.False);
  }

  [Test]
  public void TheModule_RoundTripsThroughTheBinaryFormat() {
    var a = new IrLocal("a", IrType.F64);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.F64, [a], [], new IrBlock([
      new IrReturn(IeeeOperations.Arithmetic(IrIntrinsicOp.FloatDivide, new IrLocalRef(a), new IrConst(IrType.F64, Bits(3.0)), RoundingMode.Up))
    ]))]);
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    Assert.That(IrGraph.Difference(read, module), Is.Null);
    Assert.That(Bits(IrInterpreter.Call(read, "F", [1.0])!), Is.EqualTo(0x3FD5555555555556L));
  }

}
