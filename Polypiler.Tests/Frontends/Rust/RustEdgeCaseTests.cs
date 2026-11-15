using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// The boundaries of the Rust frontend's lexer, parser, checker and lowering: literals and comments
/// at the end of the file, every type syntax the parser has to skip, the rarer statement and
/// expression forms, and the paths of the lowering a fixture of plain algorithms does not take.
/// </summary>
[TestFixture]
public sealed class RustEdgeCaseTests {

  private static ImportResult Import(string source) => RustFrontend.Import("edge.rs", source, OverflowChecks.Panic);

  private static string Run(string source, params object[] arguments) {
    var result = Import(source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return RustFixture.Interpreted(result.Module!, new PrototypeCall("f", arguments));
  }

  private static string[] Codes(string source) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    return [.. result.Diagnostics.Select(diagnostic => diagnostic.Code)];
  }

  /// <summary>Text at the end of the file, and lexical forms the fixtures do not use.</summary>
  [TestCase("﻿pub fn f() -> u32 { 1 }", "1")]
  [TestCase("pub fn f() -> u32 { 1 } // a comment at the end", "1")]
  [TestCase("pub fn f() -> f64 { 1.5E+3 + 1_000.000_1 + 2e-1 }", "f:40a388667381d7dc")]
  [TestCase("pub fn f() -> u32 { let s = \"a\\\n   b\nc😀\"; 1 }", null)]
  [TestCase("pub fn f() -> u32 { let s = r##\"a\"#\nb\"##; 1 }", null)]
  [TestCase("pub fn f() -> u8 { 0xff_u8 }", "255")]
  [TestCase("pub fn f() -> f64 { 7f64 }", "7")]
  public void LexicalForm_Imports(string source, string? expected) {
    var result = Import(source);
    if (expected is null)
      Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.All.EqualTo(RustFrontend.TextType));
    else
      Assert.That(RustFixture.Interpreted(result.Module!, new PrototypeCall("f", [])), Is.EqualTo(expected));
  }

  /// <summary>Text cut off or malformed at the end of the file is a syntax error, never a crash.</summary>
  [TestCase("pub fn")]
  [TestCase("pub fn f() -> u32 { 1 } 'a")]
  [TestCase("pub fn f() -> u32 { 'ab' }")]
  [TestCase("pub fn f() -> u32 { b'")]
  [TestCase("pub fn f() -> u32 { \"\\")]
  [TestCase("pub fn f() -> u32 { r##x }")]
  [TestCase("pub fn f() -> f64 { 1e }")]
  [TestCase("pub fn f() -> u32 { 1 + ")]
  [TestCase("pub fn f() { return")]
  [TestCase("pub fn f() -> u32 { 1.")]
  [TestCase("pub fn f() -> u32 { 1")]
  [TestCase("pub const unsafe async extern fn f() {}")]
  [TestCase("pub fn f() -> u32 { match 1 { 1..= => 1 } }")]
  public void CutOffText_IsASyntaxError(string source) => Assert.That(Codes(source), Has.All.EqualTo(RustFrontend.InvalidSource));

  /// <summary>Types the parser reads to name them in a refusal.</summary>
  [TestCase("pub fn f() -> u32 { let x: u128 = 1; 1 }")]
  [TestCase("pub fn f() -> u32 { 1 as u128 as u32 }")]
  [TestCase("const C: u128 = 1; pub fn f() -> u32 { 1 }")]
  [TestCase("pub fn f() -> u32 { const C: u128 = 1; 1 }")]
  public void TypeOutsideTheSubset_IsNamed(string source) => Assert.That(Codes(source), Does.Contain(RustFrontend.UnsupportedType));

  /// <summary>Every other form of type outside the subset is refused with the code of what it is.</summary>
  [TestCase("pub fn f(a: [u8]) -> u32 { 1 }", RustFrontend.InvalidSource)]
  [TestCase("pub fn f(a: *const u8) -> u32 { 1 }", RustFrontend.UnsafeConstruct)]
  [TestCase("pub fn f(a: unsafe extern \"C\" fn()) -> u32 { 1 }", RustFrontend.UnsafeConstruct)]
  [TestCase("pub fn f(a: impl Fn() + Copy) -> u32 { 1 }", RustFrontend.UnsupportedFunction)]
  [TestCase("pub fn f(a: &dyn Copy) -> u32 { 1 }", RustFrontend.UnsupportedTrait)]
  [TestCase("pub fn f(a: dyn Copy) -> u32 { 1 }", RustFrontend.UnsupportedTrait)]
  [TestCase("pub fn f(a: std::num::Wrapping<Vec<u32>>) -> u32 { 1 }", RustFrontend.ForeignPath)]
  [TestCase("pub fn f(a: Unknown) -> u32 { 1 }", RustFrontend.InvalidSource)]
  [TestCase("pub fn f(a: m::Unknown) -> u32 { 1 }", RustFrontend.ForeignPath)]
  [TestCase("pub fn f(a: &&u32) -> u32 { 1 }", RustFrontend.UnsupportedElement)]
  [TestCase("pub fn f(a: (u32, Vec<u8>)) -> u32 { 1 }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(a: Option<Vec<u8>>) -> u32 { 1 }", RustFrontend.UnsupportedOptionMethod)]
  [TestCase("pub fn f(a: Vec<Vec<u8>>) -> u32 { 1 }", RustFrontend.UnsupportedElement)]
  [TestCase("pub fn f(a: [u8; N]) -> u32 { 1 }", RustFrontend.UnsupportedElement)]
  [TestCase("pub fn f() -> Self { 1 }", RustFrontend.InvalidSource)]
  [TestCase("pub fn f(a: Vec<u8, u8>) -> u32 { 1 }", RustFrontend.InvalidSource)]
  public void OtherType_IsRefusedWithItsCode(string source, string code) => Assert.That(Codes(source), Does.Contain(code));

  /// <summary>Types that once were refused and now import: tuples, arrays, function pointers, references.</summary>
  [TestCase("pub fn f(a: (u32, u32)) -> u32 { a.0 + a.1 }")]
  [TestCase("pub fn f(a: [u8; 4]) -> u32 { a[3] as u32 }")]
  [TestCase("pub fn f(a: &'static mut u32) -> u32 { *a += 1; *a }")]
  [TestCase("pub fn f(a: fn(u32) -> u32) -> u32 { a(1) }")]
  [TestCase("pub fn f(a: &u32) -> u32 { *a }")]
  public void FormerlyRefusedType_Imports(string source) => Assert.That(Import(source).Diagnostics, Is.Empty);

  /// <summary>The body of a function whose signature is refused is still searched for refused constructs.</summary>
  [Test]
  public void RefusalsInAnUncheckedBody_AreReported() {
    var codes = Codes("""
      pub fn f(a: &mut str) -> u32 {
        let [x] = [1];
        let y = -!(1 as u8).count_ones() + g(|| 1, (2, 3)) + { 0..1; 5 } + match a { Some(v) | [v] if v.await => 1, _ => 2 };
        z = (*a).field;
        if async { 1 } { while unsafe { true } { break [1]; } loop { continue; } } else { return &1; }
        for (i, j) in vec![1] { }
        const K: u32 = println!();
        f
      }
      """);
    // The parameter type; [x]; [v]; .await; async; unsafe; println!.
    Assert.That(codes, Is.EquivalentTo(new[] {
      RustFrontend.TextType, RustFrontend.UnsupportedElement, RustFrontend.UnsupportedElement, RustFrontend.Coroutine, RustFrontend.Coroutine,
      RustFrontend.UnsafeConstruct, RustFrontend.UnsupportedMacro
    }));
  }

  /// <summary>Statement and expression forms the fixtures do not use, with the values rustc gives them.</summary>
  [TestCase("pub fn f(a: bool) -> bool { !true || a == (a != false) }", new object[] { true }, "true")]
  [TestCase("pub fn f(a: f32) -> f32 { -a }", new object[] { 1.5f }, "f:bff8000000000000")]
  [TestCase("pub fn f(a: u32) -> bool { let mut c = 0; let r = a > 5 || { c += 1; a > 2 }; r && c == 1 }", new object[] { 3u }, "true")]
  [TestCase("pub fn f(a: u32) -> u32 { let x: u32 = return a + 1; x }", new object[] { 3u }, "4")]
  [TestCase("pub fn f(a: u32) -> u32 { (return a) + 2 }", new object[] { 3u }, "3")]
  [TestCase("pub fn f(a: u32, b: u32) -> bool { let (_, o) = a.overflowing_sub(b); o }", new object[] { 1u, 2u }, "true")]
  [TestCase("pub fn f(a: u32, b: u32) -> u32 { let _ = a / b; let _ = 1; a / b; g(a); a; 7 } fn g(x: u32) -> u32 { x }", new object[] { 1u, 0u }, RustFixture.DivideByZero)]
  [TestCase("pub fn f(a: u32) -> u32 { let y = if a > 3 { a } else { return 0 }; y + 1 }", new object[] { 2u }, "0")]
  [TestCase("pub fn f(a: u32) -> u32 { let y = if a > 3 { let t = a * 2; t } else { 1 }; y }", new object[] { 4u }, "8")]
  [TestCase("pub fn f(a: u32) -> u32 { if a > 3 { return 1; } 2 }", new object[] { 4u }, "1")]
  [TestCase("pub fn f(a: u32) -> u32 { while a > 3 { return 1; } 2 }", new object[] { 2u }, "2")]
  [TestCase("pub fn f(a: u32) { return; }", new object[] { 2u }, "void")]
  [TestCase("pub fn f(a: u32) -> u32 { 'a: { if a > 1 { break 'a; } } 'b: { 5 } }", new object[] { 2u }, "5")]
  [TestCase("pub fn f(a: u32) -> u32 { let mut c = 0; 'a: loop { loop { loop { c += 1; if c > a { break 'a; } continue 'a; } } } c }", new object[] { 3u }, "4")]
  [TestCase("pub fn f(a: u32) -> u32 { let mut c = 0; 'w: while c < a { 'b: { loop { c += 1; continue 'w; } } } c }", new object[] { 3u }, "3")]
  [TestCase("pub fn f(a: u32) -> u32 { let mut c = 0; loop { 'b: { c += 1; if c > a { break; } } } c }", new object[] { 3u }, "4")]
  [TestCase("pub fn f(x: u32, y: bool) -> u32 { match (x, y,) { _ if x > 5 => 1, (0, _) => 2, (1, true) | _ => 3 } }", new object[] { 1u, false }, "3")]
  [TestCase("pub fn f(x: u32) -> u32 { match x { 5.. => 1, (0) => 2, _ => 3 } }", new object[] { 7u }, "1")]
  [TestCase("pub fn f(x: u32) { let mut c = 0; match x { n if { c += 1; n > 5 } => {} _ => {} } }", new object[] { 7u }, "void")]
  [TestCase("const L: u32 = 4; pub fn f(x: u32) -> u32 { match x { 0..L => 1, L..=u32::MAX => 2 } }", new object[] { 4u }, "2")]
  [TestCase("const K: i32 = ((7 - 1) * 4 / 2 % 5) & 7 | 8 ^ 1 >> 0 << 0; pub fn f(x: i32) -> i32 { match x { K => 1, _ => 0 } }", new object[] { 11 }, "1")]
  [TestCase("const C: i8 = 200u8 as i8; const B: bool = 1 < 2; pub fn f(x: i8, b: bool) -> i8 { match (x, b) { (C, B) => 1, _ => 0 } }", new object[] { (sbyte)-56, true }, "1")]
  [TestCase("pub fn f(x: i32) -> i32 { -x + 1 }", new object[] { 3 }, "-2")]
  [TestCase("pub fn f(x: u32) -> u32 { let mut y = x; y &= 3; y }", new object[] { 7u }, "3")]
  [TestCase("pub fn f(x: u32) -> u32 { x << -0 }", new object[] { 7u }, "7")]
  [TestCase("pub fn f() -> f32 { 1.5 as f32 + -1.5 as f32 }", new object[0], "0")]
  [TestCase("pub fn f() -> f64 { 5 as f64 }", new object[0], "5")]
  [TestCase("pub fn f() -> i32 { i32::MIN / 2 + 2 - 3 & 1 }", new object[0], "1")]
  [TestCase("fn g() -> () {} pub fn f() -> u32 { g(); 1 }", new object[0], "1")]
  [TestCase("pub fn f(x: u32) -> u32 { let y; y = 5 + x; y }", new object[] { 1u }, "6")]
  [TestCase("pub fn f(x: u32) -> u32 { { 1u32 }.count_ones(); x + 1 }", new object[] { 1u }, "2")]
  [TestCase("pub fn f(x: u16) -> u32 { let mut r = 0; for i in (0..=x).step_by(2).rev() { r += i as u32; } for i in (0..5usize).step_by(2).rev() { r += i as u32; } r }", new object[] { (ushort)4 }, "12")]
  [TestCase("fn g() {} pub fn f() -> bool { g() == g() }", new object[0], "true")]
  public void Form_HasTheValueRustGives(string source, object[] arguments, string expected) => Assert.That(Run(source, arguments), Is.EqualTo(expected));

  /// <summary>Refusals and compiler errors of the rarer forms.</summary>
  [TestCase("pub fn f() -> u32 { Foo::BAR }", RustFrontend.InvalidSource)]
  [TestCase("pub fn f() -> u32 { foo::BAR }", RustFrontend.ForeignPath)]
  [TestCase("pub fn f() -> f64 { f64::DIGITS }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(x: u32) -> u32 { x.abs() }", RustFrontend.Unsupported)]
  [TestCase("pub fn f(x: u32) -> u32 { let [a] = x; 1 }", RustFrontend.UnsupportedElement)]
  [TestCase("pub fn f(x: u32, y: u32) -> u32 { match (x, y) { [a] => 1, _ => 0 } }", RustFrontend.UnsupportedElement)]
  [TestCase("#[allow(a[b])] #[1] pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedAttribute)]
  [TestCase("pub fn f() -> u32 { #[allow(unused)] let x = 1; foo!; x }", RustFrontend.UnsupportedMacro)]
  [TestCase("pub fn f<A: Into<Vec<u8>>>() -> u32 { 1 }", RustFrontend.UnsupportedFunction)]
  [TestCase("pub fn f() -> u32 { async move { 1 }; 1 }", RustFrontend.Coroutine)]
  [TestCase("pub fn f() -> u32 { for x in .. { } 1 }", RustFrontend.UnsupportedIterable)]
  [TestCase("pub fn f(x: u32) -> u32 { match x { \"a\" => 1, _ => 0 } }", RustFrontend.InvalidSource)]
  [TestCase("pub fn f(x: f64) -> u32 { match x { 1.0..=2.0 => 1, _ => 0 } }", RustFrontend.UnsupportedPattern)]
  [TestCase("pub fn f(x: u32) -> u32 { match x { 'a'..='z' => 1, _ => 0 } }", RustFrontend.InvalidSource)]
  [TestCase("pub fn f(x: u32) -> u32 { match x { [[a]] => 1, _ => 0 } }", RustFrontend.UnsupportedElement)]
  public void RareConstruct_IsRefused(string source, string code) => Assert.That(Codes(source), Does.Contain(code));

  [TestCase("pub fn f() -> f32 { 1 + 1.5 }", "E0308")]
  [TestCase("pub fn f() -> u32 { let x: u32 = 1.5; x }", "E0308")]
  [TestCase("pub fn f(a: u32, b: u32) -> u32 { let (v, o): u32 = a.overflowing_add(b); v }", "E0308")]
  [TestCase("pub fn f(a: u32) -> u32 { match (a, a) { 1 => 1, _ => 2 } }", "E0308")]
  [TestCase("pub fn f(x: u32) -> u32 { x << -1 }", "arithmetic_overflow")]
  [TestCase("pub fn f(x: u32) -> u32 { x >> 40 }", "shift right")]
  [TestCase("pub fn f() -> i32 { i32::MIN / -1 }", "arithmetic_overflow")]
  [TestCase("pub fn f() -> i32 { i32::MIN % -1 }", "arithmetic_overflow")]
  [TestCase("pub fn f(x: u32) -> u32 { x.wrapping_add(1).0 }", "E0610")]
  [TestCase("pub fn f(x: u32) -> u32 { x.pow(1, 2) }", "takes 1 argument but")]
  [TestCase("pub fn f(x: u32) -> u32 { x.checked_add(1).unwrap_or() }", "takes 1 argument but")]
  [TestCase("fn g(a: u32) -> u32 { a } pub fn f() -> u32 { g(1, 2) }", "takes 1 argument but")]
  [TestCase("pub fn f() -> u32 { for i in (0..=9usize).step_by(2).rev() {} 1 }", "E0277")]
  [TestCase("pub fn f(x: u32) -> u32 { match x {} }", "E0004")]
  [TestCase("const C: u32 = true; pub fn f() -> u32 { C }", "E0308")]
  public void RareError_IsRejected(string source, string text)
    => Assert.That(Import(source).Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(text));

  /// <summary>A receiver nothing decides the type of is named by its method when it is no variable.</summary>
  [Test]
  public void UndecidedReceiver_IsReported()
    => Assert.That(Codes("pub fn f() -> u32 { let x; (if true { x } else { x }).count_ones() }"), Does.Contain(RustFrontend.UnresolvedType));

  /// <summary>Mixing an integer literal with a variable nothing else decides makes it an integer.</summary>
  [Test]
  public void LiteralDecidesAnUntypedVariable()
    => Assert.That(Run("pub fn f() -> i64 { let y; y = 2; let z = 5 + y; z as i64 }"), Is.EqualTo("7"));

}
