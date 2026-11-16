using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The forms of the Rust backend that the prototype fixtures do not reach: IR the optimizer would have
/// folded (constant conditions, conversions of a constant, a conversion to the same type), the float
/// classifications and the helpers bound for the floating-point operations Rust has no method for,
/// the negations and comparisons of truth values clippy asks to simplify, and steps of floating-point
/// counters. Hand-built IR goes through the same pipeline as an import, without the optimizer.
/// </summary>
[TestFixture]
public sealed class RustEmitEdgeTests {

  private static string Emitted(string methods) => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), level: OptimizationLevel.None));

  private static readonly IrLocal _a = new("a", IrType.I32);
  private static readonly IrLocal _c = new("c", IrType.Bool);
  private static readonly IrLocal _x = new("x", IrType.F64);

  /// <summary>One public function <c>f</c> of the parameters and locals given, as the module's text.</summary>
  private static string Emitted(IrType returns, IrLocal[] parameters, IrLocal[] locals, params IrStmt[] body) {
    var module = new IrModule("edge", [new IrFunction("F", true, returns, parameters, locals, new IrBlock(body))]);
    return RustCrates.ModuleText(RustCrates.Emit(module, level: OptimizationLevel.None));
  }

  private static string Returned(IrExpr value, params IrLocal[] parameters) => Emitted(value.Type, parameters, [], new IrReturn(value));

  private static IrLocalRef Ref(IrLocal local) => new(local);

  [TestCase("public static bool F(bool c) => !!c;", "pub fn f(c: bool) -> bool {\n    c\n}")]
  [TestCase("public static bool F(bool c) => c ? true : false;", "pub fn f(c: bool) -> bool {\n    c\n}")]
  [TestCase("public static bool F(bool c) => c ? false : true;", "pub fn f(c: bool) -> bool {\n    !c\n}")]
  [TestCase("public static bool F(int a, int b) => a < b ? false : true;", "pub fn f(a: i32, b: i32) -> bool {\n    a >= b\n}")]
  [TestCase("public static double F(double x) => -(-x);", "-(-x)")]
  [TestCase("public static bool F(uint a) => System.Numerics.BitOperations.IsPow2(a);", "a.is_power_of_two()")]
  [TestCase("public static bool F(double x) => double.IsInfinity(x);", "x.is_infinite()")]
  [TestCase("public static bool F(double x) => double.IsFinite(x);", "x.is_finite()")]
  [TestCase("public static bool F(double x) => double.IsNegative(x);", "x.is_sign_negative()")]
  [TestCase("public static bool F(double x) => double.IsNormal(x);", "x.is_normal()")]
  [TestCase("public static bool F(double x) => double.IsSubnormal(x);", "x.is_subnormal()")]
  [TestCase("public static double F(long a) => System.BitConverter.Int64BitsToDouble(a);", "f64::from_bits(a as u64)")]
  [TestCase("public static float F(int a) => System.BitConverter.Int32BitsToSingle(a);", "f32::from_bits(a as u32)")]
  [TestCase("public static double F(double x, double lo, double hi) => System.Math.Clamp(x, lo, hi);", "fn float_clamp(")]
  [TestCase("public static float F(float x, float lo, float hi) => System.Math.Clamp(x, lo, hi);", "fn float_clamp_f(")]
  [TestCase("public static double F(double x) => System.Math.Round(x, 2);", "fn float_round_digits(")]
  [TestCase("public static float F(float x) => System.MathF.Round(x, 2);", "fn float_round_digits_f(")]
  public void Form_IsItsRust(string method, string expected) => Assert.That(Emitted(method), Does.Contain(expected));

  [Test]
  public void ConstantConditions_AreTheirValues() {
    var negated = Returned(new IrUnary(IrUnaryOp.LogicalNot, new IrConst(IrType.Bool, 1)));
    Assert.That(negated, Does.Contain("pub fn f() -> bool {\n    false\n}"));
    var equal = Returned(new IrBinary(IrBinaryOp.Equal, new IrConst(IrType.Bool, 1), Ref(_c)), _c);
    Assert.That(equal, Does.Contain("pub fn f(c: bool) -> bool {\n    c\n}"));
    var unequal = Returned(new IrBinary(IrBinaryOp.NotEqual, new IrConst(IrType.Bool, 1), Ref(_c)), _c);
    Assert.That(unequal, Does.Contain("pub fn f(c: bool) -> bool {\n    !c\n}"));
  }

  [Test]
  public void ConversionOfAConstantOrToItsOwnType_IsNoCast() {
    Assert.That(Returned(new IrConvert(IrType.I32, Ref(_a), IrConversion.Wrap), _a), Does.Contain("pub fn f(a: i32) -> i32 {\n    a\n}"));
    Assert.That(Returned(new IrConvert(IrType.U8, new IrConst(IrType.I32, 300), IrConversion.Wrap)), Does.Contain("    44\n"));
    // A constant that fits cannot trap: it is the literal; one that does not keeps the conversion,
    // whose TryFrom is generic over its argument: an unsuffixed literal would be an i32, which 5000000000 is not.
    Assert.That(Returned(new IrConvert(IrType.U8, new IrConst(IrType.I32, 30), IrConversion.Trap)), Does.Contain("    30\n"));
    Assert.That(Returned(new IrConvert(IrType.U32, new IrConst(IrType.I64, 5000000000), IrConversion.Trap)), Does.Contain("u32::try_from(5000000000i64).expect(\"IntegerOverflow\")"));
  }

  [Test]
  public void ToInt32AndToUint32_AreTheHelpersOfECMAScript() {
    Assert.That(Returned(new IrIntrinsic(IrIntrinsicOp.ToInt32, IrType.I32, [Ref(_x)]), _x), Does.Contain("fn to_int32("));
    Assert.That(Returned(new IrIntrinsic(IrIntrinsicOp.ToUint32, IrType.U32, [Ref(_x)]), _x), Does.Contain("fn to_uint32("));
  }

  [Test]
  public void StepOfAFloatingPointCounter_AddsOne() {
    var text = Emitted(IrType.F64, [_x], [], new IrExprStmt(new IrStep(Ref(_x), true, false, false)), new IrExprStmt(new IrStep(Ref(_x), false, true, false)), new IrReturn(Ref(_x)));
    Assert.That(text, Does.Contain("x += 1.0;\n    x -= 1.0;"));
  }

}
