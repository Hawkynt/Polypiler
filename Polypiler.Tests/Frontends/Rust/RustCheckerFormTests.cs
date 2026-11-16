using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// What the checker says at its edges - types that do not unify part by part, places that cannot
/// be assigned, constant operations rustc refuses at compile time, iterator sources and adapters,
/// item forms the parser skips - with rustc's error codes, or rustc's value where the form compiles.
/// </summary>
[TestFixture]
public sealed class RustCheckerFormTests {

  private static ImportResult Import(string source) => RustFrontend.Import("lib.rs", source, OverflowChecks.Panic);

  [TestCase("pub fn f() -> u8 { let t: (u8, u8) = (1u8, 2u8, 3u8); 1 }", "E0308: mismatched types: expected `(u8, u8)`, found `(u8, u8, u8)`")]
  [TestCase("pub fn f() -> u8 { let t: (u8, u16) = (1u8, 2u8); 1 }", "E0308: mismatched types: expected `(u8, u16)`, found `(u8, u8)`")]
  [TestCase("pub fn f() -> u8 { let v: Vec<u8> = vec![1u16]; 1 }", "E0308")]
  [TestCase("pub fn f() -> u8 { let a: [u8; 1] = [1u16]; 1 }", "E0308")]
  [TestCase("pub fn f() -> u8 { let mut v = Vec::new(); v.push(v); 1 }", "E0308")]
  [TestCase("fn g((1, b): (u8, u8)) -> u8 { b } pub fn f() -> u8 { 1 }", "E0005: refutable pattern in function argument")]
  [TestCase("fn h() {} pub fn f() -> bool { let g: fn() = h; g == g }", "E0369")]
  [TestCase("pub fn f() -> bool { let a = [1u8].iter(); a == a }", "E0369")]
  [TestCase("const C: u8 = 1; pub fn f() { C = 2; }", "E0070")]
  [TestCase("pub fn f() { let mut x = 1u8; *unsafe { &mut x } = 2; }", RustFrontend.UnsafeConstruct)]
  [TestCase("pub fn f() { let x = 1u8; x += 1; }", "E0384")]
  [TestCase("pub fn f() { let x = 1u8; let r = &x; *r = 2; }", "E0594: cannot assign to `*r`")]
  [TestCase("struct S { x: u8 } pub fn f() { let s = S { x: 1 }; s.x = 2; }", "E0594: cannot assign to `s.x`")]
  [TestCase("struct S { x: u8 } pub fn f() { S { x: 1 }.x = 2; }", "E0594")]
  [TestCase("pub fn f() { (1u8, 2u8).0 = 2; }", "E0594")]
  [TestCase("pub fn f(c: bool) -> u8 { if c { 1 } 2 }", "E0308: mismatched types: expected `()`")]
  [TestCase("struct S { x: u8 } pub fn f() -> u8 { let a = [S { x: 1 }]; a.iter().copied().count() as u8 }", "E0277: the trait bound `S: Copy` is not satisfied")]
  [TestCase("pub fn f() -> u32 { (..3u32).sum() }", "'..'")]
  [TestCase("pub fn f() -> u32 { (1u32..).sum() }", "'a..'")]
  [TestCase("pub fn f() -> u32 { [1u32].iter().map(|x, y| x).sum() }", "E0593: closure is expected to take 1 argument, but it takes 2")]
  [TestCase("struct S { x: u8 } pub fn f() -> u8 { let s = S { x: 1 }; s.nope(1 + 1) }", "E0599")]
  [TestCase("enum E { A } pub fn f() -> u8 { E::A.nope(1) }", "E0599")]
  [TestCase("pub fn f() -> u8 { let t = (1u8,); t.nope(2) }", "E0599: no method named `nope` found for `(u8,)`")]
  [TestCase("pub fn f() -> u8 { 1u8 << 8 }", "attempt to shift left by `8`")]
  [TestCase("pub fn f() -> u8 { 200u8 + 100 }", "arithmetic_overflow")]
  [TestCase("pub fn f() -> u8 { 2u8 - 3 }", "arithmetic_overflow")]
  [TestCase("pub fn f() -> u8 { 20u8 * 30 }", "arithmetic_overflow")]
  [TestCase("pub fn f() -> u8 { 1u8 / 0 }", "unconditional_panic")]
  [TestCase("pub fn f() -> i8 { -128i8 / -1 }", "arithmetic_overflow")]
  [TestCase("pub unsafe trait T {} pub fn f() -> u8 { 1 }", "unsafe trait")]
  [TestCase("pub fn f() -> u8 { 1 } unsafe impl Send for S {} struct S;", "unsafe impl")]
  [TestCase("pub fn f() -> u8 { let x: Option<Vec<u8>>= None; 1 }", "'Option<Vec<u8>>'")]
  [TestCase("pub fn f() -> u8 { let x: Vec<Vec<u8>>= Vec::new(); 1 }", "'Vec of Vec<u8>'")]
  [TestCase("pub fn f(x: ::foo::T) {}", "'::foo::T'")]
  [TestCase("pub fn f() -> u32 { ::m::g() } mod m { pub fn g() -> u32 { 1 } }", "'::m::g'")]
  [TestCase("pub fn f() -> u32 { #[cfg(test)] let x = 1; 2 }", "cfg(test)")]
  [TestCase("pub fn f() { std::println!(\"a\"); }", "'std::println!'")]
  [TestCase("pub fn f() { foo::bar!(); }", "'foo::bar!'")]
  [TestCase("pub fn f() -> u8 { let x = 1u8; let r = &raw const x; 1 }", "raw borrow")]
  public void Form_IsRefused(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message), Has.Some.Contains(expected));
  }

  [TestCase("pub fn f() { println!(", "unclosed delimiter '('")]
  [TestCase("pub fn f() { let c = |x", "expected '|', found end of file")]
  [TestCase("pub fn f(o: Option<u8>) -> u8 { if let Some(x) = o && x > 1", "expected '{'")]
  [TestCase("pub fn f() -> Vec<u8= { 1 }", "expected identifier")]
  public void Text_IsASyntaxError(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Single().Message, Does.Contain(expected));
  }

  /// <summary>A closure used other than called is one refusal, however often it is used.</summary>
  [Test]
  public void EscapingClosure_IsReportedOnce() {
    var result = Import("pub fn f() -> bool { let a = |x: u8| x; let b = a; true }");
    Assert.That(result.Diagnostics.Count(diagnostic => diagnostic.Code == RustFrontend.EscapingClosure), Is.EqualTo(1));
  }

  /// <summary>A refused binding inside a pattern is the one diagnostic: the pattern is not also called refutable.</summary>
  [Test]
  public void RefusedBinding_IsNotAlsoRefutable() {
    var result = Import("struct S { x: u8, y: u8 } pub fn f(s: S) -> u8 { let S { ref x, mut y } = s; y }");
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Is.EqualTo(new[] { "pattern 'ref binding' is not supported" }));
  }

  [TestCase("pub fn f() -> u32 { ;; 1 }", "1")]
  [TestCase("pub fn f() -> u32 { #[allow(unused)] let x = 1; 2 }", "2")]
  [TestCase("pub fn f() -> u32 { #[inline] let x = 1; 2 }", "2")]
  [TestCase("pub fn f() -> u8 { let t = ((1u8, 2u8), 3u8); t.0.1 }", "2")]
  [TestCase("pub fn f() -> u8 { let a = [1u8, 2,]; a[1] }", "2")]
  [TestCase("struct S(u8); pub fn f() -> u8 { let s = S { 0: 5 }; s.0 }", "5")]
  [TestCase("struct S { x: u8, y: u8 } pub fn f() -> u8 { let S { mut x, y } = S { x: 1, y: 2 }; x += 1; x + y }", "4")]
  [TestCase("struct S(u8, u8); pub fn f() -> u8 { let S { 0: a, 1: b } = S(3, 4); a + b }", "7")]
  [TestCase("pub fn f() -> u8 { let x = 1u8; let r = &&x; **r }", "1")]
  [TestCase("const _: u8 = 1; pub fn f() -> u8 { const _: u8 = 1; 1 }", "1")]
  [TestCase("pub fn f() -> u8 { let x: Option<u8>= None; x.unwrap_or(3) }", "3")]
  [TestCase("pub fn f() -> u8 { let v: Vec<u8>= vec![4]; v[0] }", "4")]
  [TestCase("pub fn f() -> u32 { let mut s = 0; for x in [1u32, 2].iter_mut().rev() { *x += 1; s += *x; } s }", "5")]
  [TestCase("pub fn f() -> u32 { let mut a = [1u32, 2]; a.iter_mut().for_each(|x| *x += 1); a[0] + a[1] }", "5")]
  [TestCase("pub fn f() -> u32 { let v = vec![1u32, 2]; v.clone().iter().sum() }", "3")]
  [TestCase("pub fn f() -> u32 { let a = [1u32, 2]; (&a).iter().sum() }", "3")]
  [TestCase("pub fn f() -> u32 { vec![1u32, 2].iter_mut().map(|x| *x).sum() }", "3")]
  [TestCase("pub fn f() -> u32 { let mut a = [1u32, 2]; (&mut a).iter_mut().map(|x| *x).sum() }", "3")]
  [TestCase("pub fn f() -> u32 { [1u32, 2].into_iter().sum() }", "3")]
  [TestCase("pub fn f() -> u64 { (0..=3u64).sum() }", "6")]
  [TestCase("pub fn f() -> u16 { (0..=3u16).sum() }", "6")]
  [TestCase("pub fn f() -> u8 { let _x = return 1; }", "1")]
  [TestCase("pub fn f() -> u32 { let mut r = 0; for x in (0..4u32).map(|x| x * 10).rev() { r = r * 100 + x; } r }", "30201000")]
  [TestCase("pub fn f() -> u32 { (1..4u32).map(|x| x * x).rev().fold(0, |a, x| a * 100 + x) }", "90401")]
  [TestCase("pub fn f() -> u32 { (0..10u32).filter(|x| x % 3 == 0).rev().map(|x| x + 1).fold(0, |a, x| a * 100 + x) }", "10070401")]
  public void Form_HasTheValueRustGives(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    Assert.That(RustFixture.Interpreted(result.Module!, new PrototypeCall("f", [])), Is.EqualTo(expected));
  }

}
