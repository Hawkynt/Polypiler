using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// Forms whose lowering takes a path of its own - references passed on, items destructured through
/// references, closures that leave by return from inside loops, spilled arguments, the type forms
/// at the limits of what the IR holds - with the values rustc gives them or the codes they are
/// refused with.
/// </summary>
[TestFixture]
public sealed class RustLoweringFormTests {

  private static ImportResult Import(string source) => RustFrontend.Import("snippet.rs", source, OverflowChecks.Panic);

  private static string Run(string source) {
    var result = Import(source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return RustFixture.Interpreted(result.Module!, new PrototypeCall("f", []));
  }

  [TestCase("fn inc(x: &mut u32) { *x += 1; } fn twice(x: &mut u32) { inc(x); inc(x); } pub fn f() -> u32 { let mut v = 1; twice(&mut v); v }", "3")]
  [TestCase("pub fn f() -> u32 { let a = [3u32, 4]; let mut s = 0; for p in a.iter().enumerate() { s += p.0 as u32 * 10 + *p.1; } s }", "17")]
  [TestCase("pub fn f() -> u32 { let mut a = [3u32, 4]; let mut s = 0; for p in a.iter_mut().enumerate() { s += *p.1; } s }", "7")]
  [TestCase("pub fn f() -> u32 { let mut a = [3u32, 4]; for &mut x in a.iter_mut() { if x > 3 { return x; } } 0 }", "4")]
  [TestCase("pub fn f() -> u32 { let p = [(1u32, 2u32), (3, 4)]; let mut s = 0; for (a, b) in p.iter() { s += a * b; } s }", "14")]
  [TestCase("pub fn f() -> u32 { let mut p = [(1u32, 2u32), (3, 4)]; for (a, b) in p.iter_mut() { *a += *b; } p[1].0 }", "7")]
  [TestCase("pub fn f() -> u32 { let () = (); 1 }", "1")]
  [TestCase("pub fn f() -> u32 { let c = |n: u32| -> u32 { let mut i = 0; while i < 10 { if i == n { return i * 2; } i += 1; } loop { break; } for _ in 0..1 {} let _t = (1, 2); let _x = &n; let _a = [0u8; 2]; 99 }; c(3) + c(20) }", "105")]
  [TestCase("fn g(a: &mut u32, b: u32) { *a += b; } pub fn f() -> u32 { let mut x = 1; g(&mut x, { let t = 2; t * 3 }); x }", "7")]
  [TestCase("pub fn f() -> u32 { let a = [1u32, 2]; a.iter().map(|x| x).copied().sum() }", "3")]
  [TestCase("fn d(x: u32) -> u32 { x * 2 } pub fn f() -> u32 { let a = [1u32, 2]; a.iter().copied().for_each(d); 5 }", "E0308")]
  [TestCase("fn s(x: &u32) { let _ = x; } pub fn f() -> u32 { let g: fn(&u32) = s; let a = [1u32]; a.iter().for_each(g); 6 }", "6")]
  [TestCase("const T: [u8; 2] = [1, 2]; pub fn f() -> u32 { const N: usize = 2; let a: [u8; N] = T; let b: [u8; S::K] = [0; 3]; a.len() as u32 + b.len() as u32 } struct S; impl S { const K: usize = 3; }", "5")]
  [TestCase("#[derive(Clone, Copy, PartialEq, Default)] struct P { x: u8 } fn g(x: u8) -> Result<P, u8> { if x > 0 { Ok(P { x }) } else { Err(1) } } pub fn f() -> u32 { g(3).unwrap().x as u32 + g(0).is_err() as u32 }", "4")]
  [TestCase("pub fn f() -> u32 { let v: std::vec::Vec<u8> = vec![1]; let o: core::option::Option<u8> = Some(2); let r: std::result::Result<u8, u8> = Ok(3); v[0] as u32 + o.unwrap() as u32 + r.unwrap() as u32 }", "6")]
  [TestCase("pub fn f() -> u32 { let a = [1u32, 2, 3]; let b = &a; let c: &[u32] = b; c.len() as u32 }", "3")]
  [TestCase("pub fn f() -> u32 { let mut x = [0u8; 2]; let r = &mut x; r[1] = 8; x[1] as u32 }", "8")]
  public void Form_HasTheValueRustGives(string source, string expected) {
    if (expected.StartsWith('E')) {
      Assert.That(Import(source).Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(expected));
      return;
    }

    Assert.That(Run(source), Is.EqualTo(expected));
  }

  /// <summary>A place whose index never ends is no place: the assignment is the panic or return before it.</summary>
  [Test]
  public void DivergingIndex_EndsTheAssignment()
    => Assert.That(Import("pub fn f(a: &mut [u8]) { a[return] = 1; }").Diagnostics, Is.Empty);

  [TestCase("fn inc(x: &mut u32) {} pub fn f() { let g: fn(&mut u32) = inc; }", "with &mut parameters as a value")]
  [TestCase("pub fn f() -> u32 { let mut x = 1; let t = (&mut x, 1); 1 }", "'&mut' to a value")]
  [TestCase("fn bump(x: &mut u32) {} pub fn f() { let mut v = vec![1u32]; v.iter_mut().for_each(bump); }", "passed to an iterator method")]
  [TestCase("pub fn f(x: !) -> u8 { 1 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(x: (u8, u128)) -> u8 { 1 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(x: [u128; 2]) -> u8 { 1 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(x: [u8; -1]) -> u8 { 1 }", RustFrontend.UnsupportedElement)]
  [TestCase("pub fn f(x: &mut str) -> u8 { 1 }", RustFrontend.TextType)]
  [TestCase("pub fn f(x: &[u128]) -> u8 { 1 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(x: &impl Copy) -> u8 { 1 }", RustFrontend.UnsupportedFunction)]
  [TestCase("pub fn f(x: &u128) -> u8 { 1 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(x: fn(u128)) -> u8 { 1 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(x: Vec<u128>) -> u8 { 1 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(x: Option<u128>) -> u8 { 1 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(x: Result<u128, u8>) -> u8 { 1 }", RustFrontend.UnsupportedType)]
  [TestCase("pub fn f(x: Result<Vec<u8>, u8>) -> u8 { 1 }", RustFrontend.UnsupportedOptionMethod)]
  [TestCase("struct S { v: [u8; 1] } pub fn f(x: Result<S, u8>) -> u8 { 1 }", RustFrontend.UnsupportedOptionMethod)]
  public void Form_IsRefused(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message), Has.Some.Contains(expected));
  }

}
