using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The scalar operations as Rust (#461): the emitted text per row of the lowering table, for IR the
/// C# frontend imports from one method. That the text computes what the IR does is shown by the
/// differential tests of the acceptance project, which run it.
/// </summary>
[TestFixture]
public sealed class RustScalarEmitTests {

  private static string Emitted(string methods, OptimizationLevel level = OptimizationLevel.None, RustProfile? profile = null)
    => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), profile, level: level));

  [TestCase("public static int F(int a, int b) => a + b;", "a.wrapping_add(b)")]
  [TestCase("public static uint F(uint a, uint b) => a * b;", "a.wrapping_mul(b)")]
  [TestCase("public static long F(long a, long b) => a - b;", "a.wrapping_sub(b)")]
  [TestCase("public static int F(int a, int b) => checked(a + b);", "a.checked_add(b).expect(\"IntegerOverflow\")")]
  [TestCase("public static long F(long a, long b) => checked(a * b);", "a.checked_mul(b).expect(\"IntegerOverflow\")")]
  [TestCase("public static int F(int a) => -a;", "a.wrapping_neg()")]
  [TestCase("public static int F(int a) => checked(-a);", "a.checked_neg().expect(\"IntegerOverflow\")")]
  [TestCase("public static int F(int a, int b) => a / b;", "ply_div_i32(a, b)")]
  [TestCase("public static int F(int a) => a / 7;", "a / 7")]
  [TestCase("public static uint F(uint a, uint b) => a % b;", "ply_rem_u32(a, b)")]
  [TestCase("public static uint F(uint a, int n) => a << n;", "a.wrapping_shl(n as u32)")]
  [TestCase("public static int F(int a, int n) => a >> n;", "a.wrapping_shr(n as u32)")]
  [TestCase("public static int F(int a, int n) => a >>> n;", "(a as u32).wrapping_shr(n as u32) as i32")]
  [TestCase("public static ulong F(ulong a) => a << 3;", "a.wrapping_shl(3)")]
  [TestCase("public static uint F(uint a, uint b) => a & b | a ^ ~b;", "a & b | a ^ !b")]
  [TestCase("public static double F(double a, double b) => a * b + a / b - a % b;", "a * b + a / b - a % b")]
  [TestCase("public static float F(float a) => -a;", "-a")]
  [TestCase("public static bool F(int a, int b) => a < b && a != b || !(a >= b);", "a < b && a != b || a < b")]
  [TestCase("public static bool F(double a, double b) => !(a < b);", "!(a < b)")]
  [TestCase("public static bool F(double a, double b) => !(a == b);", "a != b")]
  [TestCase("public static int F(bool c, int a, int b) => c ? a : b;", "if c { a } else { b }")]
  public void Operator_IsItsRustForm(string method, string expected)
    => Assert.That(Emitted(method), Does.Contain(expected));

  /// <summary>Test design 1: the FNV-1a step is one expression.</summary>
  [Test]
  public void FnvStep_IsOneExpression()
    => Assert.That(Emitted("public static uint F(uint h, byte b) => (h ^ b) * 16777619u;"), Does.Contain("(h ^ u32::from(b)).wrapping_mul(16777619)"));

  [TestCase("public static long F(int a) => a;", "i64::from(a)")]
  [TestCase("public static int F(uint a) => (int)a;", "a as i32")]
  [TestCase("public static byte F(int a) => (byte)a;", "a as u8")]
  [TestCase("public static byte F(int a) => checked((byte)a);", "u8::try_from(a).expect(\"IntegerOverflow\")")]
  [TestCase("public static double F(int a) => a;", "a as f64")]
  [TestCase("public static double F(float a) => a;", "f64::from(a)")]
  [TestCase("public static float F(double a) => (float)a;", "a as f32")]
  [TestCase("public static int F(double a) => (int)a;", "a as i32")]
  [TestCase("public static int F(double a) => checked((int)a);", "ply_f2i_trap_i32(a)")]
  [TestCase("public static long F(float a) => checked((long)a);", "ply_f2i_trap_i64(f64::from(a))")]
  public void Conversion_IsItsRustForm(string method, string expected)
    => Assert.That(Emitted(method), Does.Contain(expected));

  [Test]
  public void TrappingFloatConversion_TestsTheRangeInItsSpellingFunction() {
    var text = Emitted("public static int F(double a) => checked((int)a); public static ulong G(double a) => checked((ulong)a); public static long H(double a) => checked((long)a);");
    Assert.That(text, Does.Contain("fn ply_f2i_trap_i32(x: f64) -> i32 {"));
    Assert.That(text, Does.Contain("x > -2147483649.0 && x < 2147483648.0"));
    Assert.That(text, Does.Contain("x > -1.0 && x < 1.8446744073709552e19"));
    Assert.That(text, Does.Contain("x >= -9.223372036854776e18 && x < 9.223372036854776e18"));
    Assert.That(text, Does.Contain("panic!(\"IntegerOverflow\")"));
  }

  /// <summary>Below Rust 1.34 there is no TryFrom: the narrowing tests the range through i128.</summary>
  [Test]
  public void CheckedNarrowing_BeforeTryFrom_IsARangeTest() {
    var text = Emitted("public static byte F(int a) => checked((byte)a);", profile: RustProfile.Rust2018);
    Assert.That(text, Does.Contain("ply_i2i_trap_i32_u8(a)"));
    Assert.That(text, Does.Contain("x as i128 >= 0 && (x as i128) <= 255"));
  }

  [TestCase("public static int F() => int.MinValue;", "i32::MIN")]
  [TestCase("public static long F() => long.MinValue;", "i64::MIN")]
  [TestCase("public static ulong F() => ulong.MaxValue;", "18446744073709551615")]
  [TestCase("public static double F() => 0.1;", "0.1")]
  [TestCase("public static double F() => 1e300;", "1.0e300")]
  [TestCase("public static double F() => -0.0;", "-0.0")]
  [TestCase("public static double F() => double.NaN;", "f64::from_bits(0xfff8000000000000)")]
  [TestCase("public static float F() => float.PositiveInfinity;", "f32::INFINITY")]
  [TestCase("public static double F() => double.NegativeInfinity;", "f64::NEG_INFINITY")]
  [TestCase("public static double F() => 5e-324;", "5.0e-324")]
  [TestCase("public static double F() => 3;", "3.0")]
  public void Constant_IsItsRustLiteral(string method, string expected)
    => Assert.That(Emitted(method), Does.Contain(expected));

  /// <summary>Test design 3: the division family names its traps; an unsigned quotient cannot overflow.</summary>
  [Test]
  public void Division_NamesItsTraps() {
    var text = Emitted("public static int F(int a, int b) => a / b; public static uint G(uint a, uint b) => a % b; public static long H(long a, long b) { a %= b; return a; }");
    Assert.That(text, Does.Contain("fn ply_div_i32(a: i32, b: i32) -> i32 {\n    if b == 0 {\n        panic!(\"DivideByZero\");\n    }\n    a.checked_div(b).expect(\"IntegerOverflow\")\n}"));
    Assert.That(text, Does.Contain("fn ply_rem_u32(a: u32, b: u32) -> u32 {\n    if b == 0 {\n        panic!(\"DivideByZero\");\n    }\n    a % b\n}"));
    Assert.That(text, Does.Contain("a = ply_rem_i64(a, b);"));
  }

  [Test]
  public void NegativeLiteralAsReceiver_HasItsTypeAndParentheses()
    => Assert.That(Emitted("public static long F() => System.BitConverter.DoubleToInt64Bits(-0d);"), Does.Contain("(-0.0f64).to_bits() as i64"));

  [Test]
  public void ChoiceAsTheOnlyArgument_OverflowsTheCall()
    => Assert.That(Emitted("public static long F(long x) => x < 0 ? -1 : x > 0 ? 1 : 0;", level: OptimizationLevel.Default),
      Does.Contain("i64::from(if x < 0 {\n        -1i32\n    } else if x > 0 {\n        1i32\n    } else {\n        0i32\n    })"));

  /// <summary>
  /// <c>T::from</c> is generic over its argument: an unsuffixed literal in it would be an <c>i32</c>,
  /// which <c>u32</c> and <c>u64</c> do not convert from, so the literals of a choice carry their type.
  /// </summary>
  [TestCase("public static ulong F(bool c) => c ? 1u : 0u;", "u64::from(if c { 1u32 } else { 0u32 })")]
  [TestCase("public static uint F(bool c, bool d) => c ? (ushort)1 : d ? (ushort)2 : (ushort)0;", "u32::from(if c {\n        1u16\n    } else if d {\n        2u16\n    } else {\n        0u16\n    })")]
  public void LiteralsInAWideningConversion_CarryTheirType(string method, string expected)
    => Assert.That(Emitted(method), Does.Contain(expected));

  /// <summary>
  /// The overflow flags are Rust's own <c>checked_*</c> methods at every width: the bounds test of the
  /// IR library is a comparison clippy denies as absurd for a type of 32 bits.
  /// </summary>
  [TestCase("i32", "add", "a.checked_add(b).is_none()")]
  [TestCase("u8", "sub", "a.checked_sub(b).is_none()")]
  [TestCase("i64", "mul", "a.checked_mul(b).is_none()")]
  [TestCase("u64", "add", "a.checked_add(b).is_none()")]
  public void OverflowFlag_IsTheCheckedMethod(string type, string op, string expected) {
    var imported = Polypiler.Core.Frontends.Rust.RustFrontend.Import("flags.rs", $"pub fn f(a: {type}, b: {type}) -> bool {{ a.overflowing_{op}(b).1 }}", Polypiler.Core.Frontends.Rust.OverflowChecks.Panic);
    var text = RustCrates.ModuleText(RustCrates.Emit(imported.Module!));
    Assert.That(text, Does.Contain(expected).And.Not.Contain("::MIN ||"));
  }

  [Test]
  public void CanonicalNaN_IsTheConstant() {
    Assert.That(Polypiler.Core.Backends.Rust.Printing.RustPrinter.Print(RustEmitter.Literal(new Polypiler.Core.IR.IrConst(Polypiler.Core.IR.IrType.F64, 0x7FF8000000000000))), Is.EqualTo("f64::NAN"));
    Assert.That(Polypiler.Core.Backends.Rust.Printing.RustPrinter.Print(RustEmitter.Literal(new Polypiler.Core.IR.IrConst(Polypiler.Core.IR.IrType.F32, 0x7FC00000))), Is.EqualTo("f32::NAN"));
  }

  [Test]
  public void NaNWithPayload_IsItsBits() {
    var literal = RustEmitter.Literal(new Polypiler.Core.IR.IrConst(Polypiler.Core.IR.IrType.F64, 0x7FF8000000000001));
    Assert.That(Polypiler.Core.Backends.Rust.Printing.RustPrinter.Print(literal), Is.EqualTo("f64::from_bits(0x7ff8000000000001)"));
    var single = RustEmitter.Literal(new Polypiler.Core.IR.IrConst(Polypiler.Core.IR.IrType.F32, unchecked((int)0xFFC00001)));
    Assert.That(Polypiler.Core.Backends.Rust.Printing.RustPrinter.Print(single), Is.EqualTo("f32::from_bits(0xffc00001)"));
  }

  [TestCase("public static uint F(uint a, int n) => System.Numerics.BitOperations.RotateLeft(a, n);", "a.rotate_left(n as u32)")]
  [TestCase("public static ulong F(ulong a, int n) => System.Numerics.BitOperations.RotateRight(a, n);", "a.rotate_right(n as u32)")]
  [TestCase("public static int F(uint a) => System.Numerics.BitOperations.PopCount(a);", "a.count_ones() as i32")]
  [TestCase("public static int F(ulong a) => System.Numerics.BitOperations.LeadingZeroCount(a);", "a.leading_zeros() as i32")]
  [TestCase("public static int F(uint a) => System.Numerics.BitOperations.TrailingZeroCount(a);", "a.trailing_zeros() as i32")]
  [TestCase("public static uint F(uint a) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(a);", "a.swap_bytes()")]
  [TestCase("public static int F(int a, int b) => System.Math.Min(a, b);", "a.min(b)")]
  [TestCase("public static uint F(uint a, uint b) => System.Math.Max(a, b);", "a.max(b)")]
  [TestCase("public static int F(int a) => System.Math.Abs(a);", "a.checked_abs().expect(\"IntegerOverflow\")")]
  [TestCase("public static double F(double a) => System.Math.Sqrt(a);", "a.sqrt()")]
  [TestCase("public static double F(double a) => System.Math.Floor(a);", "a.floor()")]
  [TestCase("public static double F(double a) => System.Math.Ceiling(a);", "a.ceil()")]
  [TestCase("public static double F(double a) => System.Math.Truncate(a);", "a.trunc()")]
  [TestCase("public static double F(double a) => System.Math.Round(a, System.MidpointRounding.AwayFromZero);", "a.round()")]
  [TestCase("public static double F(double a) => System.Math.Round(a);", "a.round_ties_even()")]
  [TestCase("public static double F(double a) => System.Math.Abs(a);", "a.abs()")]
  [TestCase("public static double F(double a, double b, double c) => System.Math.FusedMultiplyAdd(a, b, c);", "a.mul_add(b, c)")]
  [TestCase("public static double F(double a, double b) => System.Math.CopySign(a, b);", "a.copysign(b)")]
  [TestCase("public static bool F(double a) => double.IsNaN(a);", "a.is_nan()")]
  [TestCase("public static long F(double a) => System.BitConverter.DoubleToInt64Bits(a);", "a.to_bits() as i64")]
  [TestCase("public static double F(long a) => System.BitConverter.Int64BitsToDouble(a);", "f64::from_bits(a as u64)")]
  public void Intrinsic_IsItsMethod(string method, string expected)
    => Assert.That(Emitted(method), Does.Contain(expected));

  /// <summary>An operation Rust has no method of the IR's meaning for is the helper of the library, copied into the crate.</summary>
  [TestCase("public static double F(double a, double b) => System.Math.Min(a, b);", "fn minimum_double(")]
  [TestCase("public static int F(double a) => System.Math.Sign(a);", "fn sign_of(")]
  [TestCase("public static double F(double a) => System.Math.BitIncrement(a);", "fn float_bit_increment(")]
  public void OperationWithoutAMethod_IsItsHelper(string method, string helper) {
    var files = RustCrates.Emit(RustCrates.Import(method), level: OptimizationLevel.None);
    Assert.That(RustCrates.ModuleText(files), Does.Contain(helper));
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.Contain("Helpers added by polypiler: MIT"));
    Assert.That(files.Single(file => file.Path == "src/snippet.rs").Helpers, Is.Not.Empty);
  }

  /// <summary>The MSRV row: round_ties_even came with Rust 1.77; below it the rounding to even is the helper of the library.</summary>
  [Test]
  public void RoundingToEven_FollowsTheRustVersion() {
    const string method = "public static double F(double a) => System.Math.Round(a);";
    Assert.That(Emitted(method, profile: RustProfile.Rust2021), Does.Not.Contain("round_ties_even").And.Contain("fn round_even("));
    Assert.That(Emitted(method, profile: RustProfile.Rust2021.WithRustVersion(new Version(1, 77))), Does.Contain("a.round_ties_even()"));
  }

  [Test]
  public void Crate_HasItsManifestAndRoot() {
    var files = RustCrates.Emit(RustCrates.Import("public static int F(int a) => a;"));
    Assert.That(files.Select(file => file.Path), Is.EqualTo(new[] { "Cargo.toml", "src/lib.rs", "src/snippet.rs" }));
    Assert.That(RustCrates.Text(files, "Cargo.toml"), Is.EqualTo("[package]\nname = \"snippet\"\nversion = \"0.1.0\"\nedition = \"2024\"\nrust-version = \"1.85\"\n"));
    var root = RustCrates.Text(files, "src/lib.rs");
    Assert.That(root, Does.StartWith("//! snippet: generated by polypiler. Change the source, not this file.\n//! Rust profile: rust2024 (Rust 1.85).\n#![forbid(unsafe_code)]\n"));
    Assert.That(root, Does.EndWith("\npub mod snippet;\n"));
    Assert.That(string.Concat(files.Select(file => file.Content)), Does.Not.Contain("unsafe {").And.Not.Contain("unsafe fn"));
  }

  [Test]
  public void Rust2018_HasNoRustVersionKey() {
    var files = RustCrates.Emit(RustCrates.Import("public static int F(int a) => a;"), RustProfile.Rust2018);
    Assert.That(RustCrates.Text(files, "Cargo.toml"), Does.Contain("edition = \"2018\"").And.Not.Contain("rust-version"));
  }

  [Test]
  public void PackageName_IsAnOption() {
    var files = RustCrates.Emit(RustCrates.Import("public static int F(int a) => a;"), options: new RustEmitOptions { PackageName = "checksum", PackageVersion = "2.0.1" });
    Assert.That(RustCrates.Text(files, "Cargo.toml"), Does.Contain("name = \"checksum\"\nversion = \"2.0.1\""));
  }

  /// <summary>Test design 15: a faithful shape that triggers a lint allows exactly that lint.</summary>
  [TestCase("public static uint F(uint x) => x | 0;", "clippy::identity_op")]
  [TestCase("public static bool F(uint x) => x >= 0;", "unused_comparisons")]
  [TestCase("public static double F() => 3.141592653589793;", "clippy::approx_constant")]
  [TestCase("public static bool F(double a, double b) => !(a < b);", "clippy::neg_cmp_op_on_partial_ord")]
  [TestCase("public static int F(int a, int b, int c, int d, int e, int f, int g, int h) => a;", "clippy::too_many_arguments")]
  [TestCase("public static int F() { int s = 0; for (int i = 3; i < 3; i++) s += i; return s; }", "clippy::reversed_empty_ranges")]
  [TestCase("public static int F() { int s = 0; for (int i = 5; i <= 3; i++) s += i; return s; }", "clippy::reversed_empty_ranges")]
  public void LintTriggeringShape_AllowsItsLint(string method, string lint)
    => Assert.That(RustCrates.Text(RustCrates.Emit(RustCrates.Import(method), level: OptimizationLevel.None), "src/lib.rs"), Does.Contain(lint).And.Contain("#![allow("));

  /// <summary>A range of constants that holds a value - one value inclusively, several exclusively - needs no lint.</summary>
  [TestCase("public static int F() { int s = 0; for (int i = 3; i <= 3; i++) s += i; return s; }")]
  [TestCase("public static int F() { int s = 0; for (int i = 3; i < 4; i++) s += i; return s; }")]
  public void RangeOfConstantsThatHoldsAValue_AllowsNoLint(string method)
    => Assert.That(RustCrates.Text(RustCrates.Emit(RustCrates.Import(method), level: OptimizationLevel.None), "src/lib.rs"), Does.Not.Contain("reversed_empty_ranges"));

  [Test]
  public void CleanCode_AllowsNoLint()
    => Assert.That(RustCrates.Text(RustCrates.Emit(RustCrates.Import("public static int F(int a) => a + 1;")), "src/lib.rs"), Does.Not.Contain("allow"));

  private static readonly IrLocal _x = new("x", IrType.F64);
  private static readonly IrLocal _a = new("a", IrType.U8);
  private static readonly IrLocal _b = new("b", IrType.U8);

  /// <summary>One public function <c>f</c> returning a value of its parameters, as the crate's files.</summary>
  private static System.Collections.Generic.IReadOnlyList<Polypiler.Core.GeneratedFile> Returning(IrExpr value, params IrLocal[] parameters)
    => RustCrates.Emit(new IrModule("edge", [new IrFunction("F", true, value.Type, parameters, [], new IrBlock([new IrReturn(value)]))]), level: OptimizationLevel.None);

  /// <summary>Scenario: only the helpers that are used are emitted; the wrapping conversion from floating point is its spelling function, once.</summary>
  [Test]
  public void WrappingFloatToInteger_IsItsSpellingFunctionOnce() {
    var files = Returning(new IrBinary(IrBinaryOp.Add, new IrIntrinsic(IrIntrinsicOp.ToInt32, IrType.I32, [new IrLocalRef(_x)]), new IrIntrinsic(IrIntrinsicOp.ToInt32, IrType.I32, [new IrLocalRef(_x)])), _x);
    var text = RustCrates.ModuleText(files);
    Assert.That(text, Does.Contain("fn ply_rs_f2i_wrap_i32(x: f64) -> i32 {\n    (x.trunc() % 4294967296.0) as i64 as i32\n}"));
    Assert.That(text.Split("fn ply_rs_f2i_wrap_i32").Length, Is.EqualTo(2));
    Assert.That(text, Does.Contain("ply_rs_f2i_wrap_i32(x).wrapping_add(ply_rs_f2i_wrap_i32(x))").And.Not.Contain("ply_f2i_trap"));
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.StartWith("//!").And.Contain("\n#![forbid(unsafe_code)]\n"));
  }

  [Test]
  public void WrappingFloatToUnsigned_IsItsSpellingFunction()
    => Assert.That(RustCrates.ModuleText(Returning(new IrIntrinsic(IrIntrinsicOp.ToUint32, IrType.U32, [new IrLocalRef(_x)]), _x)),
      Does.Contain("fn ply_rs_f2i_wrap_u32(x: f64) -> u32 {\n    (x.trunc() % 4294967296.0) as i64 as u32\n}").And.Contain("ply_rs_f2i_wrap_u32(x)\n"));

  /// <summary>The scenario's saturating addition: Rust's own method, which saturates in every build mode.</summary>
  [Test]
  public void SaturatingAddition_IsItsMethod()
    => Assert.That(RustCrates.ModuleText(Returning(new IrIntrinsic(IrIntrinsicOp.AddSaturate, IrType.U8, [new IrLocalRef(_a), new IrLocalRef(_b)]), _a, _b)),
      Does.Contain("pub fn f(a: u8, b: u8) -> u8 {\n    a.saturating_add(b)\n}"));

  /// <summary>
  /// Scenario: an operation the profile's table writes without the emitter having a spelling for it in
  /// the profile emitted for is refused with PLY1550 naming it, and no file is written. The IR has no
  /// <c>u24</c>; the override here is a table that legalization took as writing <c>reverse_bits</c>
  /// and the profile emitted for has no row of it.
  /// </summary>
  [Test]
  public void OperationTheTableOverrides_IsRefused() {
    var bitReverse = new IrModule("edge", [new IrFunction("F", true, IrType.U32, [new("a", IrType.U32)], [], new IrBlock([new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.U32, [new IrLocalRef(new("a", IrType.U32))]))]))]);
    var without = RustCapabilities.Default.With(RustCapabilities.ReverseBits, null);
    var problems = RustEmitter.Check(bitReverse, RustProfile.Default, without);
    Assert.That(problems.Select(problem => problem.Code), Is.EqualTo(new[] { RustEmitter.Inexpressible }));
    Assert.That(problems[0].Message, Does.Contain("'BitReverse'"));
    Assert.That(() => RustEmitter.Emit(bitReverse, RustProfile.Default, without, Polypiler.Core.Optimize.Target.TargetPassOptions.None, null), Throws.ArgumentException.With.Message.Contains("BitReverse"));
  }

}
