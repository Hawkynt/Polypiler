using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// The Rust frontend on snippets: the scenarios of <c>fe-rs-scalars.feature</c> and
/// <c>fe-rs-control.feature</c> in the interpreter, the IR the operators become in each overflow mode,
/// and every refusal with its stable code. That <c>rustc</c> computes the same values is the
/// acceptance tests' part; the values here are the ones the issues state, which those runs confirm.
/// </summary>
[TestFixture]
public sealed class RustFrontendTests {

  private static ImportResult Import(string source, OverflowChecks? mode = OverflowChecks.Panic) => RustFrontend.Import("snippet.rs", source, mode);

  private static IrModule Module(string source, OverflowChecks mode = OverflowChecks.Panic) {
    var result = Import(source, mode);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static string Run(string source, OverflowChecks mode, params object[] arguments)
    => RustFixture.Interpreted(Module(source, mode), new PrototypeCall("f", arguments));

  private static string[] Codes(string source, OverflowChecks? mode = OverflowChecks.Panic) {
    var result = Import(source, mode);
    Assert.That(result.Module, Is.Null);
    return [.. result.Diagnostics.Select(diagnostic => diagnostic.Code)];
  }

  // ---------------------------------------------------------------- fe-rs-scalars.feature

  /// <summary>Scenario outline: imported expressions compute what rustc computes in both overflow modes.</summary>
  [TestCase("a: u8, b: u8", "u8", "a.wrapping_add(b)", OverflowChecks.Panic, new object[] { (byte)200, (byte)100 }, "44")]
  [TestCase("a: u8, b: u8", "u8", "a + b", OverflowChecks.Wrap, new object[] { (byte)200, (byte)100 }, "44")]
  [TestCase("a: u8, b: u8", "u8", "a.saturating_add(b)", OverflowChecks.Wrap, new object[] { (byte)250, (byte)10 }, "255")]
  [TestCase("a: u8, b: u8", "u8", "a.checked_add(b).unwrap_or(7)", OverflowChecks.Panic, new object[] { (byte)200, (byte)100 }, "7")]
  [TestCase("a: i32, b: i32", "i32", "a % b", OverflowChecks.Panic, new object[] { -7, 3 }, "-1")]
  [TestCase("a: i32, b: i32", "i32", "a.rem_euclid(b)", OverflowChecks.Panic, new object[] { -7, 3 }, "2")]
  [TestCase("a: u32, n: u32", "u32", "a << n", OverflowChecks.Wrap, new object[] { 1u, 33u }, "2")]
  [TestCase("a: i32", "i32", "a >> 1", OverflowChecks.Panic, new object[] { -8 }, "-4")]
  [TestCase("a: i32", "u8", "a as u8", OverflowChecks.Panic, new object[] { 300 }, "44")]
  [TestCase("a: f64", "i32", "a as i32", OverflowChecks.Panic, new object[] { 3000000000.0 }, "2147483647")]
  [TestCase("a: f64", "i32", "a as i32", OverflowChecks.Panic, new object[] { double.NaN }, "0")]
  [TestCase("a: u32", "u32", "a.rotate_left(8)", OverflowChecks.Panic, new object[] { 0x12345678u }, "878082066")]
  [TestCase("a: i32, b: i32", "i32", "a.wrapping_div(b)", OverflowChecks.Panic, new object[] { int.MinValue, -1 }, "-2147483648")]
  [TestCase("a: i32, b: i32", "i32", "a.div_euclid(b)", OverflowChecks.Wrap, new object[] { -7, 3 }, "-3")]
  [TestCase("a: u32", "u32", "a.swap_bytes()", OverflowChecks.Wrap, new object[] { 0x12345678u }, "2018915346")]
  public void ImportedExpression_ComputesWhatRustcComputes(string parameters, string result, string expression, OverflowChecks mode, object[] arguments, string expected)
    => Assert.That(Run($"pub fn f({parameters}) -> {result} {{ {expression} }}", mode, arguments), Is.EqualTo(expected));

  /// <summary>Scenario: plain addition panics with overflow checks and wraps without.</summary>
  [Test]
  public void PlainAddition_PanicsWithOverflowChecksAndWrapsWithout() {
    const string Source = "pub fn f(a: i32, b: i32) -> i32 { a + b }";
    Assert.That(Run(Source, OverflowChecks.Panic, int.MaxValue, 1), Is.EqualTo(RustFixture.Overflow));
    Assert.That(Operators(Module(Source)), Does.Contain(IrBinaryOp.AddChecked));
    Assert.That(Operators(Module(Source, OverflowChecks.Wrap)), Does.Contain(IrBinaryOp.Add).And.Not.Contain(IrBinaryOp.AddChecked));
    Assert.That(Run(Source, OverflowChecks.Wrap, int.MaxValue, 1), Is.EqualTo("-2147483648"));
  }

  /// <summary>Scenario: division traps do not depend on the overflow mode.</summary>
  [Test]
  public void DivisionTraps_DoNotDependOnTheOverflowMode() {
    const string Source = "pub fn f(a: i32, b: i32, r: bool) -> i32 { if r { a % b } else { a / b } }";
    Assert.That(Operators(Module(Source, OverflowChecks.Wrap)), Does.Contain(IrBinaryOp.Divide).And.Contain(IrBinaryOp.Remainder));
    Assert.That(Run(Source, OverflowChecks.Wrap, 1, 0, false), Is.EqualTo(RustFixture.DivideByZero));
    Assert.That(Run(Source, OverflowChecks.Wrap, int.MinValue, -1, false), Is.EqualTo(RustFixture.Overflow));
    Assert.That(Run(Source, OverflowChecks.Wrap, int.MinValue, -1, true), Is.EqualTo(RustFixture.Overflow));
  }

  /// <summary>Scenario: a shift by the full width panics with overflow checks.</summary>
  [Test]
  public void ShiftByTheFullWidth_PanicsWithOverflowChecks() {
    const string Source = "pub fn f(a: u32, n: u32) -> u32 { a << n }";
    Assert.That(Run(Source, OverflowChecks.Panic, 1u, 32u), Is.EqualTo(RustFixture.Overflow));
    Assert.That(Operators(Module(Source)), Does.Contain(IrBinaryOp.ShiftLeft));
    Assert.That(Run(Source, OverflowChecks.Panic, 1u, 31u), Is.EqualTo("2147483648"));
  }

  /// <summary>Scenario: a profile that leaves the overflow mode open is refused.</summary>
  [Test]
  public void ProfileWithoutOverflowChecks_IsRefused()
    => Assert.That(Codes("pub fn f(a: i32, b: i32) -> i32 { a + b }", null), Is.EqualTo(new[] { RustFrontend.MissingOverflowChecks }));

  /// <summary>Scenario: an unsupported numeric type is rejected with a stable code at its position.</summary>
  [Test]
  public void UnsupportedNumericType_IsRejectedAtItsPosition() {
    var result = Import("pub fn f(a: u128) -> u128 { a + 1 }", OverflowChecks.Wrap);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics[0].ToString(), Is.EqualTo("snippet.rs(1,13): error PLY1501: type 'u128' is not supported by the Rust frontend"));
  }

  /// <summary>Scenario: code the Rust compiler rejects is rejected.</summary>
  [Test]
  public void CodeTheCompilerRejects_IsRejected() {
    var result = Import("pub fn f(a: u8, b: u32) -> u32 { a + b }", OverflowChecks.Wrap);
    Assert.That(result.Diagnostics.Single().Code, Is.EqualTo(RustFrontend.InvalidSource));
    Assert.That(result.Diagnostics.Single().Message, Does.Contain("E0308"));
  }

  /// <summary>Test design 1: the FNV-1a step.</summary>
  [Test]
  public void FnvStep_HasItsPublishedValue()
    => Assert.That(Run("pub fn f(h: u32, b: u32) -> u32 { (h ^ b).wrapping_mul(16777619) }", OverflowChecks.Panic, 2166136261u, 97u), Is.EqualTo("3826002220"));

  /// <summary>Test design 4, 6, 8, 12, 13, 14, 16a, 16b: the stated values of each method family.</summary>
  [TestCase("a: u8, b: u8", "bool", "a.overflowing_add(b).1", new object[] { (byte)255, (byte)1 }, "true")]
  [TestCase("a: u8, b: u8", "u8", "a.overflowing_add(b).0", new object[] { (byte)255, (byte)1 }, "0")]
  [TestCase("a: i8, b: i8", "i8", "a.saturating_sub(b)", new object[] { (sbyte)-100, (sbyte)100 }, "-128")]
  [TestCase("a: i32, b: i32", "i32", "a.saturating_div(b)", new object[] { int.MinValue, -1 }, "2147483647")]
  [TestCase("a: i8, b: u32", "i8", "a.saturating_pow(b)", new object[] { (sbyte)-2, 9u }, "-128")]
  [TestCase("a: i8, b: i8", "u8", "a.abs_diff(b)", new object[] { (sbyte)127, (sbyte)-128 }, "255")]
  [TestCase("a: u8", "u8", "a.overflowing_neg().0", new object[] { (byte)5 }, "251")]
  [TestCase("a: i8", "bool", "a.overflowing_abs().1", new object[] { sbyte.MinValue }, "true")]
  [TestCase("a: u8, b: u32", "bool", "a.overflowing_pow(b).1", new object[] { (byte)2, 9u }, "true")]
  [TestCase("a: u32, b: u32", "bool", "a.checked_shl(b).is_none()", new object[] { 1u, 32u }, "true")]
  [TestCase("a: i32", "i32", "a.clamp(1, 3)", new object[] { 5 }, "3")]
  [TestCase("a: i32", "i32", "a.clamp(5, 2)", new object[] { 1 }, RustFixture.Panic)]
  [TestCase("a: u8, b: u8", "u8", "a.checked_add(b).unwrap()", new object[] { (byte)200, (byte)100 }, RustFixture.Panic)]
  [TestCase("a: u8, b: u8", "u8", "a.checked_add(b).expect(\"boom\")", new object[] { (byte)200, (byte)100 }, RustFixture.Panic)]
  [TestCase("a: i32, b: i32", "bool", "a.checked_div(b).is_some()", new object[] { int.MinValue, -1 }, "false")]
  [TestCase("a: i32, b: i32", "i32", "a.wrapping_rem(b)", new object[] { int.MinValue, -1 }, "0")]
  [TestCase("a: i32, b: i32", "i32", "a.div_euclid(b)", new object[] { int.MinValue, -1 }, RustFixture.Overflow)]
  [TestCase("a: u32", "u32", "a.trailing_zeros()", new object[] { 0u }, "32")]
  [TestCase("a: u32", "u32", "a.leading_zeros()", new object[] { 1u }, "31")]
  [TestCase("a: u16", "u32", "a.count_ones()", new object[] { (ushort)0xF0F0 }, "8")]
  [TestCase("a: f64", "u64", "a.sqrt().to_bits()", new object[] { 2.0 }, "4609047870845172685")]
  [TestCase("a: f32", "u32", "a.sqrt().to_bits()", new object[] { 2.0f }, "1068827891")]
  [TestCase("a: f64", "bool", "a.sqrt().is_nan()", new object[] { -1.0 }, "true")]
  [TestCase("a: u32", "f32", "a as f32", new object[] { 16777217u }, "16777216")]
  [TestCase("a: f64", "u32", "(a as f32).to_bits()", new object[] { 1e40 }, "2139095040")]
  [TestCase("a: f64", "i64", "a as i64", new object[] { double.NegativeInfinity }, "-9223372036854775808")]
  [TestCase("a: f64", "u32", "a as u32", new object[] { -1e10 }, "0")]
  [TestCase("a: f32", "i16", "a as i16", new object[] { 1e10f }, "32767")]
  [TestCase("a: u32, n: i32", "u32", "a.wrapping_shl(n as u32)", new object[] { 1u, -1 }, "2147483648")]
  [TestCase("a: f64", "bool", "a != a", new object[] { double.NaN }, "true")]
  [TestCase("a: f64", "bool", "a == a", new object[] { double.NaN }, "false")]
  [TestCase("a: u8", "u32", "a as i8 as i32 as u32", new object[] { (byte)0xFF }, "4294967295")]
  public void Method_HasTheValueRustGives(string parameters, string result, string expression, object[] arguments, string expected)
    => Assert.That(Run($"pub fn f({parameters}) -> {result} {{ {expression} }}", OverflowChecks.Panic, arguments), Is.EqualTo(expected));

  /// <summary>Test design 6: the overflow mode decides <c>+ - *</c>, unary <c>-</c>, <c>abs</c>, <c>pow</c> and the shifts.</summary>
  [TestCase("a: i32", "i32", "a + 1", new object[] { int.MaxValue }, "-2147483648")]
  [TestCase("a: u32", "u32", "a - 1", new object[] { 0u }, "4294967295")]
  [TestCase("a: u32", "u32", "a * a", new object[] { 65536u }, "0")]
  [TestCase("a: i32", "i32", "-a", new object[] { int.MinValue }, "-2147483648")]
  [TestCase("a: i8", "i8", "-a", new object[] { sbyte.MinValue }, "-128")]
  [TestCase("a: i32", "i32", "a.abs()", new object[] { int.MinValue }, "-2147483648")]
  [TestCase("a: i32, b: u32", "i32", "a.pow(b)", new object[] { 2, 40u }, "0")]
  [TestCase("a: u32, n: i32", "u32", "a << n", new object[] { 1u, -1 }, "2147483648")]
  [TestCase("a: i64, n: u8", "i64", "a >> n", new object[] { -1L, (byte)65 }, "-1")]
  public void OverflowMode_DecidesTheOperator(string parameters, string result, string expression, object[] arguments, string wrapped) {
    var source = $"pub fn f({parameters}) -> {result} {{ {expression} }}";
    Assert.That(Run(source, OverflowChecks.Panic, arguments), Is.EqualTo(RustFixture.Overflow));
    Assert.That(Run(source, OverflowChecks.Wrap, arguments), Is.EqualTo(wrapped));
  }

  /// <summary>Test design 19: with two trapping operands the left one's trap is the one observed.</summary>
  [Test]
  public void TwoTrappingOperands_TrapOnTheLeftOne() {
    Assert.That(Run("pub fn f(a: i32, b: i32, c: i32, d: i32) -> i32 { (a / b) + (c / d) }", OverflowChecks.Panic, 1, 0, int.MinValue, -1), Is.EqualTo(RustFixture.DivideByZero));
    Assert.That(Run("pub fn f(a: i32, b: i32, c: i32, d: i32) -> i32 { (a / b) + (c / d) }", OverflowChecks.Panic, int.MinValue, -1, 1, 0), Is.EqualTo(RustFixture.Overflow));
  }

  /// <summary>Literals of every base and form, shadowing, local constants and inferred widths.</summary>
  [TestCase("u64", "0xFF_u64 + 0o17 + 0b1010_1010 + 1_000_000 + b'A' as u64", "1000505")]
  [TestCase("u8", "b'\\n' + b'\\x41' + b'\\\\'", "167")]
  [TestCase("i64", "let a = 7; let a = a as i64 * 3; a", "21")]
  [TestCase("u32", "const K: u32 = 1 << 4; K * 2", "32")]
  [TestCase("i8", "-128", "-128")]
  [TestCase("i32", "i32::MIN.wrapping_abs()", "-2147483648")]
  [TestCase("u32", "u8::BITS + u64::BITS", "72")]
  [TestCase("u8", "let x; x = 5; x", "5")]
  [TestCase("f64", "f64::INFINITY - f64::MAX", "f:7ff0000000000000")]
  [TestCase("bool", "f32::NAN != f32::NAN && f64::NEG_INFINITY < f64::MIN", "true")]
  [TestCase("f32", "(-1.5f32).abs() + 2. + 1e1 - 0.5e1", "f:4021000000000000")]
  [TestCase("u8", "true as u8 + (false as u8)", "1")]
  [TestCase("u64", "let v = { let x = 3u64; x * x } + if true { 1 } else { 2 }; v", "10")]
  public void ConstantExpression_HasItsValue(string result, string body, string expected)
    => Assert.That(Run($"pub fn f() -> {result} {{ {body} }}", OverflowChecks.Panic), Is.EqualTo(expected));

  // ---------------------------------------------------------------- fe-rs-control.feature

  /// <summary>Scenario outline: imported control flow takes the path rustc takes.</summary>
  [TestCase("pub fn f(mut a: u32, mut b: u32) -> u32 { while b != 0 { let t = a % b; a = b; b = t; } a }", new object[] { 48u, 18u }, "6")]
  [TestCase("pub fn f(mut n: u64) -> u32 { let mut s = 0; while n != 1 { n = if n % 2 == 0 { n / 2 } else { 3 * n + 1 }; s += 1; } s }", new object[] { 27UL }, "111")]
  [TestCase("pub fn f(n: u32) -> u32 { let mut p = 1u32; loop { if p >= n { break p; } p <<= 1; } }", new object[] { 100u }, "128")]
  [TestCase("pub fn f(n: u32) -> u32 { let mut s = 0; for i in 0..=n { s += i; } s }", new object[] { 100u }, "5050")]
  [TestCase("pub fn f() -> u32 { let mut s = 0; for i in 0..10u32 { if i % 2 == 0 { continue; } s += i; } s }", new object[0], "25")]
  [TestCase("pub fn f(x: i32) -> i32 { let y = if x < 0 { return -1 } else { x * 2 }; y + 1 }", new object[] { -5 }, "-1")]
  [TestCase("pub fn f(x: i32) -> i32 { let y = if x < 0 { return -1 } else { x * 2 }; y + 1 }", new object[] { 4 }, "9")]
  public void ControlFlow_TakesThePathRustcTakes(string source, object[] arguments, string expected)
    => Assert.That(Run(source, OverflowChecks.Panic, arguments), Is.EqualTo(expected));

  /// <summary>Scenario outline: match selects the first arm whose pattern and guard hold.</summary>
  [TestCase(-5, "0")]
  [TestCase(0, "1")]
  [TestCase(2, "2")]
  [TestCase(8, "3")]
  [TestCase(7, "4")]
  public void Match_SelectsTheFirstArmWhosePatternAndGuardHold(int x, string arm)
    => Assert.That(Run("pub fn f(x: i32) -> u8 { match x { i32::MIN..=-1 => 0, 0 => 1, 1 | 2 => 2, n if n % 2 == 0 => 3, _ => 4 } }", OverflowChecks.Panic, x), Is.EqualTo(arm));

  /// <summary>Scenario: a labelled break leaves both loops.</summary>
  [TestCase(12u, "26")]
  [TestCase(11u, "0")]
  public void LabelledBreak_LeavesBothLoops(uint t, string expected)
    => Assert.That(Run("pub fn f(t: u32) -> u32 { let mut r = 0; 'outer: for i in 1..=9u32 { for j in 1..=9u32 { if i * j == t { r = i * 10 + j; break 'outer; } } } r }", OverflowChecks.Panic, t), Is.EqualTo(expected));

  /// <summary>Scenario: an inclusive range ending at the type maximum terminates, without a trapping counter.</summary>
  [Test]
  public void InclusiveRangeToTheMaximum_Terminates() {
    const string Source = "pub fn f() -> u32 { let mut n = 0; for _ in 0..=u8::MAX { n += 1; } n }";
    Assert.That(Run(Source, OverflowChecks.Panic), Is.EqualTo("256"));
    Assert.That(IrTree.Nodes(Module(Source).Functions[0].Body).OfType<IrConvert>().Where(convert => convert.Type == IrType.U8), Has.All.Property(nameof(IrConvert.Contract)).EqualTo(IrConversion.Wrap));
  }

  /// <summary>Scenario: rev and step_by are applied in source order.</summary>
  [Test]
  public void RevAndStepBy_AreAppliedInSourceOrder() {
    Assert.That(Run("pub fn f() -> u32 { let mut r = 0; for i in (0..11u32).step_by(3).rev() { r = r * 16 + i; } r }", OverflowChecks.Panic), Is.EqualTo("38448"));
    Assert.That(Run("pub fn f() -> u32 { let mut r = 0; for i in (0..11u32).rev().step_by(3) { r = r * 16 + i; } r }", OverflowChecks.Panic), Is.EqualTo("42817"));
  }

  /// <summary>Scenario: a step of zero panics before the first iteration.</summary>
  [Test]
  public void StepOfZero_PanicsBeforeTheFirstIteration() {
    const string Source = "pub fn f(n: usize) -> u32 { let mut c = 0; for _ in (0..10u32).step_by(n) { c += 1; } c }";
    Assert.That(Run(Source, OverflowChecks.Panic, 0UL), Is.EqualTo(RustFixture.Panic));
    Assert.That(Run(Source, OverflowChecks.Panic, 3UL), Is.EqualTo("4"));
  }

  /// <summary>Scenario: an unsupported loop form is rejected with a stable code.</summary>
  [Test]
  public void UnsupportedLoopForm_IsRejected() {
    var result = Import("pub fn f(mut o: Option<u32>) -> u32 { let mut s = 0; while let Some(v) = o { s += v; o = None; } s }");
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Line)), Does.Contain((RustFrontend.ConditionalBinding, 1)));
  }

  /// <summary>Test design (#463) 6, 7, 10, 12, 13, 20: labelled blocks, trip counts, range pattern ends, traps in bodies and guards, an endless loop.</summary>
  [TestCase("pub fn f(x: i32) -> i32 { 'b: { if x > 10 { break 'b 1; } if x > 5 { break 'b 2; } 3 } }", new object[] { 6 }, "2")]
  [TestCase("pub fn f() -> u32 { let mut c = 0; for _ in 5..3u8 { c += 1; } for _ in 3..=3u8 { c += 10; } for _ in 4..=3u8 { c += 100; } c }", new object[0], "10")]
  [TestCase("pub fn f(x: i32) -> u8 { match x { ..=-1 => 1, 1..=3 => 2, _ => 3 } }", new object[] { -1 }, "1")]
  [TestCase("pub fn f(x: i32) -> u8 { match x { ..=-1 => 1, 1..=3 => 2, _ => 3 } }", new object[] { 4 }, "3")]
  [TestCase("pub fn f(n: u32) -> u32 { let mut s = 0; for i in 0..=n { s += i; } s }", new object[] { 100000u }, RustFixture.Overflow)]
  [TestCase("pub fn f(x: i32) -> i32 { match x { n if 10 / n > 1 => 1, _ => 0 } }", new object[] { 0 }, RustFixture.DivideByZero)]
  [TestCase("pub fn f(a: i32, b: i32) -> i32 { g(a / b, b / a) } fn g(x: i32, y: i32) -> i32 { x + y }", new object[] { 0, 0 }, RustFixture.DivideByZero)]
  public void ControlForm_HasTheValueRustGives(string source, object[] arguments, string expected)
    => Assert.That(Run(source, OverflowChecks.Panic, arguments), Is.EqualTo(expected));

  /// <summary>Test design (#463) 20: a function of type <c>!</c> imports; its loop has no exit.</summary>
  [Test]
  public void EndlessLoop_Imports() {
    var module = Module("pub fn f() -> ! { loop {} }");
    Assert.That(module.Functions[0].Body.Statements.Single(), Is.InstanceOf<IrWhile>());
  }

  // ---------------------------------------------------------------- refusals

  /// <summary>Every construct outside the subset is refused with its code, and nothing is imported.</summary>
  [TestCase("pub fn f(a: i128) -> i128 { a }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(a: f16) -> f16 { a }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(a: char) -> u32 { a as u32 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f() -> u32 { 'a' as u32 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f() -> u128 { 1u128 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f() -> u32 { let s = \"x\"; 1 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f() -> u32 { u128::MAX as u32 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(a: &u32) -> u32 { 1 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(a: f64) -> f64 { a.floor() }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: f64, b: f64, c: f64) -> f64 { a.mul_add(b, c) }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { a.to_be() }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32, b: u32) -> u32 { let o = a.checked_add(b); 1 }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32, b: u32) -> u32 { a.overflowing_add(b); 1 }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { a.checked_add(1).unwrap_or_default() }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { a.checked_add(1).expect(MESSAGE) }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: bool) -> bool { a.then_some() }", RustFrontend.Unsupported)]
  [TestCase("pub fn f() -> u32 { let t = (1, 2); 1 }", RustFrontend.Unsupported)]
  [TestCase("pub fn f() -> u32 { let r = 0..3; 1 }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { a.0 }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { let c = |x: u32| x; a }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { let c = move || 1; a }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { let c = || 1; a }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { let c = |x: u32| -> u32 { x }; a }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { *a }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { let b = &mut a; 1 }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { [a][0] }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { g(a)? }", RustFrontend.Unsupported)]
  [TestCase("pub fn f() -> u32 { unsafe { 1 } }", RustFrontend.Unsupported)]
  [TestCase("pub fn f() -> u32 { core::cmp::min(1, 2) }", RustFrontend.Unsupported)]
  [TestCase("pub fn f() -> u32 { Vec::<u32>::new() }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { a.into::<u64>() }", RustFrontend.Unsupported)]
  [TestCase("pub fn f() -> u32 { f }", RustFrontend.Unsupported)]
  [TestCase("pub fn f() -> u32 { let x = (); 1 }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { let mut x = 1; x.y = 2; x }", RustFrontend.Unsupported)]
  [TestCase("pub fn f() -> u32 { (1)() }", RustFrontend.Unsupported)]
  [TestCase("struct S; pub fn f() -> u32 { 1 }", RustFrontend.Unsupported)]
  [TestCase("use std::cmp; pub fn f() -> u32 { 1 }", RustFrontend.Unsupported)]
  [TestCase("mod m { pub fn g() {} } pub fn f() -> u32 { 1 }", RustFrontend.Unsupported)]
  [TestCase("static S: u32 = 1; pub fn f() -> u32 { 1 }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: u32) -> u32 { if let 1 = a { 1 } else { 2 } }", RustFrontend.ConditionalBinding)]
  [TestCase("pub fn f(a: u32) -> u32 { if let 1 = a { 1 } else if a > 2 { 2 } else { 3 } }", RustFrontend.ConditionalBinding)]
  [TestCase("pub fn f(a: u32) -> u32 { let 1 = a else { return 0 }; 1 }", RustFrontend.ConditionalBinding)]
  [TestCase("pub fn f(a: u32) -> u32 { match a { n @ 1..=5 => n, _ => 0 } }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32) -> u32 { match a { [x, y] => 1, _ => 0 } }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32) -> u32 { match a { Point { x, y } => 1, _ => 0 } }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32) -> u32 { match a { Some(x) => 1, _ => 0 } }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32) -> u32 { match a { &x => 1 } }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32) -> u32 { match a { ref x => 1 } }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32) -> u32 { match a { const { 1 } => 1, _ => 0 } }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32) -> u32 { match a { 'x' => 1, _ => 0 } }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(a: u32, b: u32) -> u32 { match a { x | 1 => 1, _ => 0 } }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32, b: u32) -> u32 { match a { b..=5 => 1, _ => 0 } }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: f64) -> u32 { match a { 1.0 => 1, _ => 0 } }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32) -> u32 { let (x, y) = (a, a); x }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32) -> u32 { let 1 = a; 1 }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32) -> u32 { for (i, j) in 0..a {} 1 }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32) -> u32 { for [i] in 0..a {} 1 }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32) -> u32 { let (.., x) = a.overflowing_add(1); 1 }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(a: u32) -> u32 { for x in v.iter().map(g) {} 1 }", RustFrontend.UnsupportedIterable)]
  [TestCase("pub fn f(a: u32) -> u32 { for x in 0.. {} 1 }", RustFrontend.UnsupportedIterable)]
  [TestCase("pub fn f(a: u32) -> u32 { for x in ..a {} 1 }", RustFrontend.UnsupportedIterable)]
  [TestCase("pub fn f(a: u32) -> u32 { for x in a {} 1 }", RustFrontend.UnsupportedIterable)]
  [TestCase("pub fn f(a: u32) -> u32 { for x in (0..a).rev().rev() {} 1 }", RustFrontend.UnsupportedIterable)]
  [TestCase("pub fn f(a: u32) -> u32 { for x in (0..a).step_by() {} 1 }", RustFrontend.UnsupportedIterable)]
  [TestCase("pub fn f(a: u32) -> u32 { for x in g(a) {} 1 }", RustFrontend.UnsupportedIterable)]
  [TestCase("pub async fn f() -> u32 { 1 }", RustFrontend.Coroutine)]
  [TestCase("pub fn f() -> u32 { g().await }", RustFrontend.Coroutine)]
  [TestCase("pub fn f() -> u32 { async { 1 }; 1 }", RustFrontend.Coroutine)]
  [TestCase("pub fn f() -> u32 { try { 1 }; 1 }", RustFrontend.Coroutine)]
  [TestCase("pub fn f() -> u32 { yield 1; 1 }", RustFrontend.Coroutine)]
  [TestCase("pub fn f() -> u32 { yield; 1 }", RustFrontend.Coroutine)]
  [TestCase("pub fn f<T>(a: T) -> u32 { 1 }", RustFrontend.UnsupportedFunction)]
  [TestCase("pub unsafe fn f() -> u32 { 1 }", RustFrontend.UnsupportedFunction)]
  [TestCase("pub extern \"C\" fn f() -> u32 { 1 }", RustFrontend.UnsupportedFunction)]
  [TestCase("pub fn f(a: u32) -> u32 where u32: Copy { 1 }", RustFrontend.UnsupportedFunction)]
  [TestCase("pub fn f((a, b): (u32, u32)) -> u32 { 1 }", RustFrontend.UnsupportedFunction)]
  [TestCase("pub fn f() -> u32 { fn g() {} 1 }", RustFrontend.UnsupportedFunction)]
  [TestCase("pub fn f() -> u32 { println!(\"{}\", 1); 1 }", RustFrontend.UnsupportedMacro)]
  [TestCase("pub fn f() -> u32 { panic!() }", RustFrontend.UnsupportedMacro)]
  [TestCase("#[test] pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedAttribute)]
  [TestCase("#![no_std] pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedAttribute)]
  [TestCase("pub fn f() -> u32 { #[cfg(test)] let x = 1; 1 }", RustFrontend.UnsupportedAttribute)]
  public void Construct_IsRefusedWithItsCode(string source, string code)
    => Assert.That(Codes(source), Does.Contain(code));

  /// <summary>What the Rust compiler rejects is rejected with its error code or lint.</summary>
  [TestCase("pub fn f() -> u8 { 255u8 + 1 }", "arithmetic_overflow")]
  [TestCase("pub fn f(x: u32) -> u32 { x << 32 }", "arithmetic_overflow")]
  [TestCase("pub fn f(x: u32) -> u32 { x / 0 }", "unconditional_panic")]
  [TestCase("pub fn f(x: i32) -> i32 { x % 0 }", "unconditional_panic")]
  [TestCase("pub fn f(x: i32) -> i32 { -i32::MIN + x }", "arithmetic_overflow")]
  [TestCase("pub fn f() -> i32 { i32::MIN - 1 }", "arithmetic_overflow")]
  [TestCase("pub fn f() -> u8 { 256 }", "overflowing_literals")]
  [TestCase("pub fn f() -> u8 { 300 as u8 }", "overflowing_literals")]
  [TestCase("pub fn f(x: u32) -> u32 { -x }", "E0600")]
  [TestCase("pub fn f() -> u32 { -1 as u32 }", "E0600")]
  [TestCase("pub fn f(x: bool) -> bool { -x }", "E0600")]
  [TestCase("pub fn f(x: f64) -> f64 { !x }", "E0600")]
  [TestCase("pub fn f(x: u8, y: u32) -> u32 { x + y }", "E0308")]
  [TestCase("pub fn f() -> u32 { true }", "E0308")]
  [TestCase("pub fn f(x: f64) -> f64 { let y: f64 = 1; y }", "E0308")]
  [TestCase("pub fn f() -> i32 { let x = 5; x.pow(2) }", "E0689")]
  [TestCase("pub fn f(x: u32) -> u32 { match x { 0 => 1 } }", "E0004")]
  [TestCase("pub fn f(x: bool) -> u32 { match x { true => 1 } }", "E0004")]
  [TestCase("pub fn f(x: u8, y: bool) -> u32 { match (x, y) { (0..=255, true) => 1 } }", "E0004")]
  [TestCase("pub fn f(x: u32) -> u32 { break; 1 }", "E0268")]
  [TestCase("pub fn f(x: u32) -> u32 { continue; 1 }", "E0268")]
  [TestCase("pub fn f(x: u32) -> u32 { loop { break 'a; } }", "E0426")]
  [TestCase("pub fn f(x: u32) -> u32 { 'a: { loop { continue 'a; } } }", "E0696")]
  [TestCase("pub fn f(x: u32) -> u32 { while x > 0 { break 1; } 1 }", "E0571")]
  [TestCase("pub fn f() -> u32 { y }", "E0425")]
  [TestCase("pub fn f() -> u32 { y = 1; 1 }", "E0425")]
  [TestCase("pub fn f() -> u32 { g() }", "E0425")]
  [TestCase("pub fn f(x: u32) -> u32 { let y = 1; y = 2; y }", "E0384")]
  [TestCase("pub fn f(x: u32) -> u32 { x += 1; x }", "E0384")]
  [TestCase("pub fn f(x: u32) -> u32 { g(1) } fn g() -> u32 { 1 }", "E0061")]
  [TestCase("pub fn f(x: u32) -> u32 { x.pow() }", "E0061")]
  [TestCase("pub fn f(x: u32) -> u32 { x.checked_add(1).unwrap(1) }", "E0061")]
  [TestCase("pub fn f() -> u32 { 1 } pub fn f() -> u32 { 2 }", "E0428")]
  [TestCase("pub fn f(x: f64) -> f64 { x as bool as f64 }", "E0054")]
  [TestCase("pub fn f(x: bool) -> bool { x + x }", "E0369")]
  [TestCase("pub fn f(x: f64) -> f64 { x << 1 }", "E0369")]
  [TestCase("pub fn f(x: f64) -> f64 { x & x }", "E0369")]
  [TestCase("pub fn f(x: f64) -> f64 { let mut y = x; y |= x; y }", "E0369")]
  [TestCase("pub fn f(x: f64) -> f64 { let mut y = x; y <<= 1; y }", "E0369")]
  [TestCase("pub fn f(x: f64) -> u32 { for i in 0.0..1.0 {} 1 }", "E0277")]
  [TestCase("pub fn f() -> u32 { for i in (0..=9u32).step_by(2).rev() {} 1 }", "E0277")]
  [TestCase("pub fn f() -> u32 { for i in (0..9u64).step_by(2).rev() {} 1 }", "E0277")]
  [TestCase("pub fn f(x: u32) -> u32 { match (x, x) { (1, 2, 3) => 1, _ => 2 } }", "E0308")]
  [TestCase("pub fn f(x: u32) -> u32 { match x { (1, 2) => 1, _ => 2 } }", "E0308")]
  [TestCase("pub fn f() -> u32 { 1u7 }", "invalid suffix")]
  public void CompilerError_IsRejected(string source, string text) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Has.Some.Matches<(string Code, string Message)>(item => item.Code == RustFrontend.InvalidSource && item.Message.Contains(text, System.StringComparison.Ordinal)));
  }

  /// <summary>Test design 18: a value whose type nothing decides.</summary>
  [TestCase("pub fn f() -> u32 { let x; 1 }")]
  [TestCase("pub fn f() -> u32 { let x; x.count_ones() }")]
  public void UndecidedType_IsReported(string source)
    => Assert.That(Codes(source), Does.Contain(RustFrontend.UnresolvedType));

  /// <summary>Text that is no Rust is a syntax error with its position.</summary>
  [TestCase("pub fn f( -> u32 { 1 }")]
  [TestCase("pub fn f() -> u32 { 1 + }")]
  [TestCase("pub fn f() -> u32 { a < b < c }")]
  [TestCase("pub fn f() -> u32 { 1 2 }")]
  [TestCase("pub fn f() -> u32 { /* unterminated }")]
  [TestCase("pub fn f() -> u32 { \"unterminated }")]
  [TestCase("pub fn f() -> u32 { 'a }")]
  [TestCase("pub fn f() -> u32 { b'é' as u32 }")]
  [TestCase("pub fn f() -> u32 { b'ab' as u32 }")]
  [TestCase("pub fn f() -> u32 { 0b102 }")]
  [TestCase("pub fn f() -> u32 { 0x }")]
  [TestCase("pub fn f() -> f64 { 1.5u8 }")]
  [TestCase("pub fn f() -> u32 { \"\\q\" }")]
  [TestCase("pub fn f() -> u32 { \"\\xZZ\" }")]
  [TestCase("pub fn f() -> u32 { \"\\u0041\" }")]
  [TestCase("pub fn f() -> u32 { r\"unterminated }")]
  [TestCase("pub fn f() -> u32 { r#x }")]
  [TestCase("pub fn f() -> u32 { € }")]
  [TestCase("pub fn f() -> u32 { 'a: 1 }")]
  [TestCase("pub fn f() -> u32 { match 1 { 1 => 1 2 => 2 } }")]
  [TestCase("pub fn f() -> u32 { match 1 { ..= => 1 } }")]
  [TestCase("pub fn f() -> u32 { match 1 { +1 => 1 } }")]
  [TestCase("pub fn f() -> u32 { let x = let y = 1; 1 }")]
  [TestCase("pub fn f() -> u32 { 1 }}")]
  [TestCase("pub fn f() -> u32 { (1 }")]
  [TestCase("pub fn f() -> u32 { if true { 1 }")]
  [TestCase("pub fn f() -> [u32; 4 { 1 }")]
  [TestCase("pub fn f() -> fn( { 1 }")]
  [TestCase("pub fn f() -> Vec<u32 { 1 }")]
  [TestCase("#[inline pub fn f() -> u32 { 1 }")]
  [TestCase("pub fn f() -> u32 { for x in 0..1 }")]
  [TestCase("pub fn f() -> u32 { |x }")]
  [TestCase("pub fn f() -> u32 { match 1 { [1, 2 => 1 } }")]
  [TestCase("pub fn f() -> u32 { if let x = 1 }")]
  [TestCase("pub fn f() -> u32 { mod m { }")]
  public void TextThatIsNoRust_IsASyntaxError(string source) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Single().Code, Is.EqualTo(RustFrontend.InvalidSource));
    Assert.That(result.Diagnostics.Single().Line, Is.EqualTo(1));
  }

  /// <summary>The forms the parser accepts besides the subset's own: lint attributes, raw identifiers, comments, escapes, unusual types.</summary>
  [Test]
  public void AcceptedNoise_ImportsUnchanged() {
    const string Source = """
      #![allow(dead_code)]
      //! A crate comment.
      /* A block comment /* nested */ still a comment. */
      /// A documented function.
      #[inline]
      #[must_use]
      pub(crate) fn r#f(r#type: u32) -> u32 {
        let _unused = "text\n\t\r\0\\\'\"\x41\u{1F600}";
        let _raw = r#"raw "string""#;
        let _bytes = b"bytes";
        let _raw_bytes = br"raw bytes";
        r#type + g()
      }
      const fn g() -> u32 { 1 }
      """;
    var result = Import(Source);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()), Has.All.Contains("PLY1501"));
  }

  /// <summary>Functions call each other, recursively and mutually; <c>pub</c> makes a function public; a function of unit result is a statement.</summary>
  [Test]
  public void Functions_CallEachOther() {
    var module = Module("""
      pub fn fib(n: u32) -> u64 { if n < 2 { n as u64 } else { fib(n - 1) + fib(n - 2) } }
      pub fn even(n: u32) -> bool { if n == 0 { true } else { odd(n - 1) } }
      fn odd(n: u32) -> bool { if n == 0 { false } else { even(n - 1) } }
      fn nothing(x: u32) {}
      pub fn calls(n: u32) -> u32 { nothing(n); n }
      pub fn unit() { nothing(1); }
      """);
    Assert.That(module.Functions.Select(function => (function.Name, function.IsPublic)), Is.EqualTo(new[] {
      ("fib", true), ("even", true), ("odd", false), ("nothing", false), ("calls", true), ("unit", true)
    }));
    Assert.That(IrInterpreter.Call(module, "fib", [20u]), Is.EqualTo(6765UL));
    Assert.That(IrInterpreter.Call(module, "even", [7u]), Is.EqualTo(false));
    Assert.That(IrInterpreter.Call(module, "calls", [7u]), Is.EqualTo(7u));
    Assert.That(module.Functions.Single(function => function.Name == "nothing").ReturnType, Is.EqualTo(IrType.Void));
  }

  /// <summary>The module is named after the file.</summary>
  [Test]
  public void Module_IsNamedAfterTheFile()
    => Assert.That(RustFrontend.Import("dir/hash.rs", "pub fn f() -> u32 { 1 }", OverflowChecks.Wrap).Module!.Name, Is.EqualTo("hash"));

  /// <summary>A local never takes the name of a function, which a backend would shadow.</summary>
  [Test]
  public void Local_NeverTakesTheNameOfAFunction() {
    var module = Module("pub fn f(g: u32) -> u32 { let f = g; h(f) } fn h(x: u32) -> u32 { x }");
    Assert.That(module.Functions[0].Parameters.Concat(module.Functions[0].Locals).Select(local => local.Name), Has.None.EqualTo("f").And.None.EqualTo("h"));
    Assert.That(IrInterpreter.Call(module, "f", [3u]), Is.EqualTo(3u));
  }

  /// <summary>The power helpers are lowered once per type and kind, beside a function of the same name.</summary>
  [Test]
  public void PowerHelpers_AreAddedOnce() {
    var module = Module("pub fn f(a: u8, n: u32) -> u8 { a.pow(n) ^ a.wrapping_pow(n) ^ a.saturating_pow(n) } pub fn rust_pow_wrapping_u8() -> u8 { 1 }");
    Assert.That(module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "f", "rust_pow_wrapping_u8", "rust_pow_overflows_u8", "_rust_pow_wrapping_u8" }));
  }

  private static IrBinaryOp[] Operators(IrModule module)
    => [.. module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrBinary>().Select(binary => binary.Op)];

}
