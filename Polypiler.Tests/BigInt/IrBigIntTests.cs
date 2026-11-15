using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.BigInt;

/// <summary>
/// The arbitrary-precision integer of the IR on modules built by hand: the type, the rules of the
/// verifier, what the interpreter computes - exactly, with floor shifts, the infinite two's complement
/// of the bitwise operators and the conversions at their edges - and the module format.
/// </summary>
[TestFixture]
public sealed class IrBigIntTests {

  private static readonly IrLocal A = new("a", IrType.BigInt);
  private static readonly IrLocal B = new("b", IrType.BigInt);
  private static readonly IrLocal N = new("n", IrType.I32);

  private static IrBigConst Big(BigInteger value) => new(value);

  private static IrLocalRef R(IrLocal local) => new(local);

  private static IrModule Module(IrType result, IReadOnlyList<IrLocal> parameters, IrExpr value)
    => new("M", [new IrFunction("F", true, result, parameters, [], new IrBlock([new IrReturn(value)]))]);

  private static IReadOnlyList<string> Problems(IrType result, IReadOnlyList<IrLocal> parameters, IrExpr value)
    => [.. IrVerifier.Verify(Module(result, parameters, value)).Select(diagnostic => diagnostic.Message)];

  private static object? Run(IrExpr value, IReadOnlyList<IrLocal> parameters, params object[] arguments)
    => IrInterpreter.Call(Module(value.Type, parameters, value), "F", arguments);

  private static BigInteger P(string digits) => BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);

  [Test]
  public void Type_IsAnExactIntegerOfItsOwn() {
    Assert.That(IrType.BigInt.ToString(), Is.EqualTo("bigint"));
    Assert.That(IrType.BigInt.IsBigInt && IrType.BigInt.IsStorable, Is.True);
    Assert.That(IrType.BigInt.IsInteger || IrType.BigInt.IsNumeric || IrType.BigInt.IsArithmetic, Is.False);
    Assert.That(IrType.I64.IsBigInt, Is.False);
    Assert.That(new IrBigConst(5).Type, Is.EqualTo(IrType.BigInt));
  }

  [TestCase(IrBinaryOp.Add, "18446744073709551615", "1", "18446744073709551616")]
  [TestCase(IrBinaryOp.Subtract, "0", "18446744073709551616", "-18446744073709551616")]
  [TestCase(IrBinaryOp.Multiply, "4294967296", "4294967296", "18446744073709551616")]
  [TestCase(IrBinaryOp.Divide, "-7", "2", "-3")]
  [TestCase(IrBinaryOp.Remainder, "-7", "2", "-1")]
  [TestCase(IrBinaryOp.Remainder, "7", "-2", "1")]
  [TestCase(IrBinaryOp.And, "-1", "255", "255")]
  [TestCase(IrBinaryOp.And, "-256", "-1", "-256")]
  [TestCase(IrBinaryOp.Or, "-256", "15", "-241")]
  [TestCase(IrBinaryOp.Xor, "-1", "18446744073709551615", "-18446744073709551616")]
  public void Arithmetic_IsExact(IrBinaryOp op, string left, string right, string expected)
    => Assert.That(Run(new IrBinary(op, R(A), R(B)), [A, B], P(left), P(right)), Is.EqualTo(P(expected)));

  [TestCase(IrBinaryOp.Equal, "18446744073709551616", "18446744073709551616", true)]
  [TestCase(IrBinaryOp.NotEqual, "1", "2", true)]
  [TestCase(IrBinaryOp.Less, "-18446744073709551616", "1", true)]
  [TestCase(IrBinaryOp.LessOrEqual, "2", "1", false)]
  [TestCase(IrBinaryOp.Greater, "18446744073709551616", "18446744073709551615", true)]
  [TestCase(IrBinaryOp.GreaterOrEqual, "1", "1", true)]
  public void Comparison_OrdersTheIntegers(IrBinaryOp op, string left, string right, bool expected)
    => Assert.That(Run(new IrBinary(op, R(A), R(B)), [A, B], P(left), P(right)), Is.EqualTo(expected));

  [TestCase(IrBinaryOp.Divide)]
  [TestCase(IrBinaryOp.Remainder)]
  public void Division_ByZero_Traps(IrBinaryOp op)
    => Assert.That(() => Run(new IrBinary(op, R(A), R(B)), [A, B], BigInteger.One, BigInteger.Zero),
      Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.DivideByZero));

  [TestCase(IrBinaryOp.ShiftLeft, "1", 64, "18446744073709551616")]
  [TestCase(IrBinaryOp.ShiftLeft, "-3", 1, "-6")]
  [TestCase(IrBinaryOp.ShiftLeft, "-8", -2, "-2")]
  [TestCase(IrBinaryOp.ShiftRight, "-1", 1, "-1")]
  [TestCase(IrBinaryOp.ShiftRight, "-5", 1, "-3")]
  [TestCase(IrBinaryOp.ShiftRight, "18446744073709551616", 64, "1")]
  [TestCase(IrBinaryOp.ShiftRight, "-18446744073709551616", 1000, "-1")]
  [TestCase(IrBinaryOp.ShiftRight, "18446744073709551616", 65, "0")]
  [TestCase(IrBinaryOp.ShiftRight, "18446744073709551616", 64000, "0")]
  [TestCase(IrBinaryOp.ShiftRight, "3", -2, "12")]
  public void Shift_IsUnmaskedAndFloors(IrBinaryOp op, string value, int count, string expected)
    => Assert.That(Run(new IrBinary(op, R(A), R(N)), [A, N], P(value), count), Is.EqualTo(P(expected)));

  [TestCase(IrUnaryOp.Negate, "-9223372036854775808", "9223372036854775808")]
  [TestCase(IrUnaryOp.BitNot, "5", "-6")]
  [TestCase(IrUnaryOp.BitNot, "-18446744073709551617", "18446744073709551616")]
  public void Unary_IsExact(IrUnaryOp op, string value, string expected)
    => Assert.That(Run(new IrUnary(op, R(A)), [A], P(value)), Is.EqualTo(P(expected)));

  /// <summary>Equivalence classes and the boundaries of the conversions to a fixed width: wrapping keeps the low bits, a trap fires just outside.</summary>
  [TestCase("I32", "4294967295", "Wrap", -1L)]
  [TestCase("U32", "-1", "Wrap", 4294967295L)]
  [TestCase("U8", "-1", "Wrap", 255L)]
  [TestCase("I8", "128", "Wrap", -128L)]
  [TestCase("I16", "-32769", "Wrap", 32767L)]
  [TestCase("U16", "65536", "Wrap", 0L)]
  [TestCase("I64", "18446744073709551615", "Wrap", -1L)]
  [TestCase("U64", "-1", "Wrap", -1L)]
  [TestCase("I64", "9223372036854775807", "Trap", long.MaxValue)]
  [TestCase("I32", "-2147483648", "Trap", int.MinValue)]
  [TestCase("U64", "18446744073709551615", "Trap", -1L)]
  public void ToFixedWidth_WrapsOrFits(string type, string value, string contract, long expected) {
    var target = Fixed(type);
    var result = Run(new IrConvert(target, R(A), Enum.Parse<IrConversion>(contract)), [A], P(value));
    Assert.That(System.Convert.ToInt64(result is ulong large ? (long)large : result), Is.EqualTo(expected));
  }

  [TestCase("I64", "9223372036854775808")]
  [TestCase("I32", "-2147483649")]
  [TestCase("U64", "-1")]
  [TestCase("U64", "18446744073709551616")]
  public void ToFixedWidth_JustOutside_Traps(string type, string value)
    => Assert.That(() => Run(new IrConvert(Fixed(type), R(A), IrConversion.Trap), [A], P(value)),
      Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.IntegerOverflow));

  [Test]
  public void FromFixedWidth_IsExact() {
    var u = new IrLocal("u", IrType.U64);
    Assert.That(Run(new IrConvert(IrType.BigInt, R(u)), [u], ulong.MaxValue), Is.EqualTo(P("18446744073709551615")));
    var i = new IrLocal("i", IrType.I8);
    Assert.That(Run(new IrConvert(IrType.BigInt, R(i), IrConversion.Trap), [i], (sbyte)-128), Is.EqualTo(new BigInteger(-128)));
  }

  /// <summary>To binary64: exact below 2^53, ties to even above, the sticky bits counted, an infinity beyond the largest finite number.</summary>
  [TestCase("9007199254740993", 9007199254740992.0)]
  [TestCase("9007199254740995", 9007199254740996.0)]
  [TestCase("-1467610831193486894665", -1.467610831193487e+21)]
  [TestCase("12551840078097276403302034493229823973055639407069474225443998522909850857583436", 1.2551840078097277e+79)]
  [TestCase("0", 0.0)]
  public void ToDouble_RoundsToNearestEven(string value, double expected)
    => Assert.That(Run(new IrConvert(IrType.F64, R(A)), [A], P(value)), Is.EqualTo(expected));

  [Test]
  public void ToDouble_BeyondTheLargestFinite_IsAnInfinity() {
    var largest = new BigInteger(double.MaxValue);
    var halfway = largest + (BigInteger.One << 970);
    Assert.That(IrBigInts.ToDouble(halfway - 1), Is.EqualTo(double.MaxValue));
    Assert.That(IrBigInts.ToDouble(halfway), Is.EqualTo(double.PositiveInfinity));
    Assert.That(IrBigInts.ToDouble(-halfway), Is.EqualTo(double.NegativeInfinity));
  }

  [TestCase(2.9, "2")]
  [TestCase(-2.9, "-2")]
  [TestCase(1e30, "1000000000000000019884624838656")]
  public void FromDouble_Truncates(double value, string expected) {
    var x = new IrLocal("x", IrType.F64);
    Assert.That(Run(new IrConvert(IrType.BigInt, R(x), IrConversion.Trap), [x], value), Is.EqualTo(P(expected)));
  }

  [TestCase(double.NaN)]
  [TestCase(double.PositiveInfinity)]
  public void FromDouble_NaNOrInfinity_Traps(double value) {
    var x = new IrLocal("x", IrType.F64);
    Assert.That(() => Run(new IrConvert(IrType.BigInt, R(x), IrConversion.Trap), [x], value),
      Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.IntegerOverflow));
    var f = new IrLocal("f", IrType.F32);
    Assert.That(Run(new IrConvert(IrType.BigInt, R(f), IrConversion.Trap), [f], 2.5f), Is.EqualTo(new BigInteger(2)));
  }

  [Test]
  public void Verifier_AcceptsTheOperationsOfTheType() {
    Assert.That(Problems(IrType.BigInt, [A, B], new IrBinary(IrBinaryOp.Multiply, R(A), R(B))), Is.Empty);
    Assert.That(Problems(IrType.BigInt, [A, N], new IrBinary(IrBinaryOp.ShiftLeft, R(A), R(N))), Is.Empty);
    Assert.That(Problems(IrType.Bool, [A, B], new IrBinary(IrBinaryOp.Less, R(A), R(B))), Is.Empty);
    Assert.That(Problems(IrType.BigInt, [A], new IrUnary(IrUnaryOp.BitNot, R(A))), Is.Empty);
    Assert.That(Problems(IrType.BigInt, [], Big(P("123456789012345678901234567890"))), Is.Empty);
  }

  [Test]
  public void Verifier_RefusesWhatTheTypeDoesNotHave() {
    var i = new IrLocal("i", IrType.I64);
    Assert.That(Problems(IrType.BigInt, [A, B], new IrBinary(IrBinaryOp.AddChecked, R(A), R(B))), Has.Some.Contains("AddChecked is applied to bigint and bigint"));
    Assert.That(Problems(IrType.BigInt, [A, B], new IrBinary(IrBinaryOp.ShiftRightLogical, R(A), R(N))), Is.Not.Empty);
    Assert.That(Problems(IrType.BigInt, [A, i], new IrBinary(IrBinaryOp.Add, R(A), R(i))), Is.Not.Empty);
    Assert.That(Problems(IrType.BigInt, [A, B], new IrBinary(IrBinaryOp.ShiftLeft, R(A), R(B))), Is.Not.Empty);
    Assert.That(Problems(IrType.Bool, [A, B], new IrBinary(IrBinaryOp.LogicalAnd, R(A), R(B))), Is.Not.Empty);
    Assert.That(Problems(IrType.BigInt, [A], new IrUnary(IrUnaryOp.NegateChecked, R(A))), Is.Not.Empty);
    Assert.That(Problems(IrType.BigInt, [A], new IrUnary(IrUnaryOp.LogicalNot, R(A))), Is.Not.Empty);
    Assert.That(Problems(IrType.BigInt, [], new IrConst(IrType.BigInt, 0)), Has.Some.Contains("is not a value of bigint"));
  }

  [Test]
  public void Verifier_ChecksTheContractsOfTheConversions() {
    var x = new IrLocal("x", IrType.F64);
    Assert.That(Problems(IrType.F64, [A], new IrConvert(IrType.F64, R(A))), Is.Empty);
    Assert.That(Problems(IrType.F32, [A], new IrConvert(IrType.F32, R(A))), Is.Not.Empty);
    Assert.That(Problems(IrType.F64, [A], new IrConvert(IrType.F64, R(A), IrConversion.Trap)), Is.Not.Empty);
    Assert.That(Problems(IrType.BigInt, [x], new IrConvert(IrType.BigInt, R(x), IrConversion.Trap)), Is.Empty);
    Assert.That(Problems(IrType.BigInt, [x], new IrConvert(IrType.BigInt, R(x), IrConversion.Saturate)), Is.Not.Empty);
    Assert.That(Problems(IrType.I32, [A], new IrConvert(IrType.I32, R(A), IrConversion.Saturate)), Is.Not.Empty);
    Assert.That(Problems(IrType.BigInt, [A], new IrConvert(IrType.BigInt, R(A))), Is.Not.Empty);
    var flag = new IrLocal("f", IrType.Bool);
    Assert.That(Problems(IrType.BigInt, [flag], new IrConvert(IrType.BigInt, R(flag))), Is.Not.Empty);
  }

  [Test]
  public void ModuleFormat_KeepsTheTypeAndItsConstants() {
    var value = P("-340282366920938463463374607431768211457");
    var module = Module(IrType.BigInt, [A], new IrBinary(IrBinaryOp.Add, R(A), Big(value)));
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    Assert.That(read.Functions[0].Parameters[0].Type, Is.EqualTo(IrType.BigInt));
    Assert.That(((IrBinary)((IrReturn)read.Functions[0].Body.Statements[0]).Value!).Right, Is.EqualTo(new IrBigConst(value)));
    Assert.That(IrInterpreter.Call(read, "F", [BigInteger.One]), Is.EqualTo(value + 1));
  }

  [Test]
  public void Boundary_TakesAndGivesBigIntegers()
    => Assert.That(() => IrInterpreter.Call(Module(IrType.BigInt, [A], R(A)), "F", [5L]), Throws.ArgumentException);

  private static IrType Fixed(string name) => name switch {
    "I8" => IrType.I8,
    "U8" => IrType.U8,
    "I16" => IrType.I16,
    "U16" => IrType.U16,
    "I32" => IrType.I32,
    "U32" => IrType.U32,
    "I64" => IrType.I64,
    _ => IrType.U64
  };

}
