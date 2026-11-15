using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// The rarer forms of the phase-two constructs: every error rustc reports about them with its code,
/// every refusal with its stable code, and the forms the fixtures do not use with the values rustc
/// gives them.
/// </summary>
[TestFixture]
public sealed class RustRareFormTests {

  private static ImportResult Import(string source) => RustFrontend.Import("snippet.rs", source, OverflowChecks.Panic);

  private static string Run(string source, params object[] arguments) {
    var result = Import(source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return RustFixture.Interpreted(result.Module!, new PrototypeCall("f", arguments));
  }

  /// <summary>What the source is refused with: a code, or the text of a compiler error.</summary>
  private static void Refused(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message), Has.Some.Contains(expected));
  }

  // ---------------------------------------------------------------- paths, calls, references

  [TestCase("pub fn f() -> u32 { Box::new(1); 1 }", RustFrontend.SmartPointer)]
  [TestCase("struct T(u8); pub fn f() -> u32 { let g = T; 1 }", "constructor of 'T' as a value")]
  [TestCase("pub fn f() -> u32 { let m = std::cmp::max; 1 }", "'cmp::max' as a value")]
  [TestCase("pub fn f() -> u32 { crate::nothing::x }", "E0433")]
  [TestCase("pub fn f(v: &mut Vec<u8>) -> usize { let s = &mut v[..]; s.len() }", RustFrontend.UnsupportedElement)]
  [TestCase("pub fn f() -> u32 { let x; *x }", RustFrontend.UnresolvedType)]
  [TestCase("pub fn f() -> u32 { (|x: u32| x)(1) }", "call of a value")]
  [TestCase("pub fn f(x: u32) -> u32 { x(1) }", "E0618")]
  [TestCase("pub fn f() -> Option<u32> { Some(1, 2) }", "E0061")]
  [TestCase("pub fn f() -> u32 { std::cmp::min(1) }", "E0061")]
  [TestCase("pub fn f(a: u32, b: u32) { std::mem::swap(a, b); }", "E0308")]
  [TestCase("pub fn f(a: &mut u32, b: &mut u32) { std::mem::swap(a, b); }", "mem::swap of references")]
  [TestCase("#[derive(Clone, Copy, PartialEq)] struct P { x: u8 } pub fn f() -> u8 { std::cmp::min(P { x: 1 }, P { x: 2 }).x }", "cmp::min of")]
  [TestCase("pub fn f() -> usize { let v: Vec<u8> = Vec::new(1); v.len() }", "E0061")]
  [TestCase("pub fn f() -> u32 { u32::from_ne_bytes([1, 2, 3, 4]) }", RustFrontend.UnsupportedSliceMethod)]
  [TestCase("struct S; pub fn f() { let s = S::default(); }", "E0599")]
  [TestCase("pub fn f() -> u32 { let t = (1u32, 2u32); t.2 }", "E0609")]
  [TestCase("struct S { a: u32 } pub fn f() -> u32 { let s = S { a: 1 }; s.b }", "E0609")]
  [TestCase("mod m { pub struct S { a: u32 } pub fn make() -> S { S { a: 1 } } } pub fn f() -> u32 { m::make().a }", "E0616")]
  [TestCase("pub fn f() -> usize { let s = \"x\"; s.a; s[0]; s.len() }", RustFrontend.TextType)]
  [TestCase("pub fn f() -> u32 { let x; x.a }", RustFrontend.UnresolvedType)]
  [TestCase("pub fn f(a: &[u8]) -> usize { a[..=].len() }", "E0586")]
  [TestCase("pub fn f() -> usize { [].len() }", RustFrontend.UnresolvedType)]
  [TestCase("pub fn f(n: usize) -> u8 { let a = [0u8; n]; a[0] }", RustFrontend.UnsupportedElement)]
  [TestCase("pub fn f() -> usize { let v = vec![1u8]; let a = [v; 2]; 1 }", "E0277")]
  [TestCase("pub fn f() -> u32 { Missing { a: 1 }; 1 }", "E0422")]
  [TestCase("pub fn f() { let r = std::ops::Range { start: 1, end: 2 }; }", RustFrontend.ForeignPath)]
  [TestCase("struct S { a: u32 } pub fn f() { let s = S { a: 1, b: 2 }; }", "E0560")]
  [TestCase("struct S { a: u32 } pub fn f() { let s = S { a: 1, a: 2 }; }", "E0062")]
  [TestCase("mod m { pub struct S { a: u32 } } pub fn f() { let s = m::S { a: 1 }; }", "E0451")]
  [TestCase("pub fn f() -> Option<u32> { let s = \"x\"; s? }", RustFrontend.TextType)]
  [TestCase("fn g() -> Option<u32> { Some(1) } pub fn f() -> Result<u32, u8> { let v = g()?; Ok(v) }", "E0277")]
  [TestCase("fn g() -> Result<u32, u8> { Ok(1) } pub fn f() -> Option<u32> { let v = g()?; Some(v) }", "E0277")]
  [TestCase("pub fn f() -> Option<u32> { let x; x? }", RustFrontend.UnresolvedType)]
  [TestCase("pub fn f(x: u32) -> Option<u32> { x? }", "E0277")]
  [TestCase("pub fn f() -> usize { let v = vec![vec![1u8]; 2]; v.len() }", RustFrontend.UnsupportedElement)]
  [TestCase("pub fn f() { assert!(); }", "boolean expression")]
  [TestCase("pub fn f() { assert_eq!(1); }", "takes two values")]
  [TestCase("struct S; pub fn f() { assert_eq!(S, S); }", "E0369")]
  [TestCase("struct S; pub fn f() -> bool { S == S }", "E0369")]
  [TestCase("pub fn f(o: Option<Vec<u8>>) -> u32 { 1 }", RustFrontend.UnsupportedOptionMethod)]
  [TestCase("pub fn f(r: Result<(), ()>) -> Option<()> { r.ok() }", RustFrontend.UnsupportedOptionMethod)]
  public void PathOrCall_IsRefused(string source, string expected) => Refused(source, expected);

  // ---------------------------------------------------------------- items

  [TestCase("#[repr(u8)] enum E { A = 300 } pub fn f() -> u8 { 1 }", "overflowing_literals")]
  [TestCase("#[repr(u8)] enum E { A = 255, B } pub fn f() -> u8 { 1 }", "E0370")]
  [TestCase("enum E { A = 1, B = 1 } pub fn f() -> u8 { 1 }", "E0081")]
  [TestCase("#[derive(Default)] enum E { A } pub fn f() -> u8 { 1 }", "E0665")]
  [TestCase("enum E {} pub fn f() -> u8 { 1 }", "without variants")]
  [TestCase("#[repr(C)] enum E { A } pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedDerive)]
  [TestCase("enum E { A = X } pub fn f() -> u8 { 1 }", "discriminant of 'E::A'")]
  [TestCase("struct S { s: S } pub fn f() -> u8 { 1 }", "E0072")]
  [TestCase("struct A { b: B } struct B { a: A } pub fn f() -> u8 { 1 }", "E0072")]
  [TestCase("impl u32 { fn g(&self) {} } pub fn f() -> u8 { 1 }", "E0116")]
  [TestCase("impl Missing { } pub fn f() -> u8 { 1 }", "E0412")]
  [TestCase("struct S; impl S { fn a() {} } impl S { fn a() {} } pub fn f() -> u8 { 1 }", "E0592")]
  [TestCase("struct S; impl S { const C: u32 = true; } pub fn f() -> u8 { 1 }", "E0308")]
  [TestCase("struct S { v: Vec<u8> } pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedElement)]
  [TestCase("struct S { g: fn() } pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedElement)]
  [TestCase("struct S { m: Missing } pub fn f() -> u8 { 1 }", "E0412")]
  [TestCase("#[derive(Copy)] struct S; pub fn f() -> u8 { 1 }", "E0277")]
  [TestCase("struct S; #[derive(Clone, Copy)] struct T { s: S } pub fn f() -> u8 { 1 }", "E0204")]
  [TestCase("#[derive(Eq)] struct S; pub fn f() -> u8 { 1 }", "E0277")]
  [TestCase("fn g(self) {} pub fn f() -> u8 { 1 }", "E0424")]
  [TestCase("fn g(x: &mut u32) -> &mut u32 { x } pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedLifetime)]
  [TestCase("pub fn f(x: &'a u8) -> u8 { 1 }", RustFrontend.UnsupportedLifetime)]
  [TestCase("pub fn f(x: Option<'a>) -> u8 { 1 }", RustFrontend.UnsupportedLifetime)]
  [TestCase("struct S; pub fn f(x: S<u8>) -> u8 { 1 }", RustFrontend.UnsupportedGeneric)]
  [TestCase("enum E { A } pub fn f(x: E<u8>) -> u8 { 1 }", RustFrontend.UnsupportedGeneric)]
  [TestCase("pub fn f(x: [Vec<u8>; 2]) -> u8 { 1 }", RustFrontend.UnsupportedElement)]
  [TestCase("struct S { v: [[u8; 2]; 2] } pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedElement)]
  [TestCase("pub fn f(x: Result<u8, [u8; 2]>) -> u8 { 1 }", RustFrontend.UnsupportedOptionMethod)]
  [TestCase("pub fn f(x: (u8, (u8, Vec<u8>))) -> u8 { 1 }", RustFrontend.Unsupported)]
  public void Item_IsRefused(string source, string expected) => Refused(source, expected);

  // ---------------------------------------------------------------- methods and iterators

  [TestCase("pub fn f() -> u32 { let t = (1u32, 2u32); t.go() }", "E0599")]
  [TestCase("pub fn f() -> u32 { let x; x.go() }", RustFrontend.UnresolvedType)]
  [TestCase("struct S; pub fn f() { S.go(); }", "E0599")]
  [TestCase("struct S; impl S { fn new() -> S { S } } pub fn f() { S.new(); }", "associated function, not a method")]
  [TestCase("mod m { pub struct S; impl S { fn hidden(&self) {} } } pub fn f() { m::S.hidden(); }", "E0624")]
  [TestCase("struct S { a: u32 } impl S { fn bump(&mut self) { self.a += 1; } } pub fn f() { let s = S { a: 1 }; s.bump(); }", "E0596")]
  [TestCase("struct S { a: u32 } impl S { fn take(self) -> u32 { self.a } } pub fn f(s: &S) -> u32 { s.take() }", "E0507")]
  [TestCase("pub fn f(a: &mut [u8], r: std::ops::Range<usize>) { a.copy_within(r, 0); }", RustFrontend.ForeignPath)]
  [TestCase("pub fn f(a: &mut [u8]) { a.copy_within(1, 0); }", "copy_within with a range")]
  [TestCase("pub fn f(a: &[u8]) -> usize { let p = a.split_at(1); 1 }", "split_at other than")]
  [TestCase("pub fn f(a: &[u8]) -> usize { let c = a.clone(); 1 }", "E0599")]
  [TestCase("pub fn f(a: &[u8]) -> usize { a.get(0..1); 1 }", "get with a range")]
  [TestCase("struct S; pub fn f(a: &[S]) -> usize { 1 }", RustFrontend.UnsupportedElement)]
  [TestCase("#[derive(PartialEq)] struct S; pub fn f(a: [S; 1]) -> usize { a.to_vec().len() }", "E0277")]
  [TestCase("pub fn f() -> u32 { (1..).sum() }", RustFrontend.UnsupportedIterable)]
  [TestCase("pub fn f(a: &[u8]) -> usize { a.iter().take(1).count() }", RustFrontend.UnsupportedIteratorMethod)]
  [TestCase("pub fn f(a: &[u8]) -> usize { (0..3u8).zip(a).count() }", "zip of a range")]
  [TestCase("pub fn f(a: &[u8]) -> usize { a.iter().zip(3).count() }", "zip with anything but")]
  [TestCase("pub fn f(a: &[u8]) -> usize { a.iter().copied().copied().count() }", "E0271")]
  [TestCase("pub fn f() -> bool { (0..3u8).map(|x| x == 1).sum() }", "cannot be made by summing")]
  [TestCase("pub fn f(a: &[u8]) -> usize { a.iter().map(|x, y| x).count() }", "E0593")]
  [TestCase("pub fn f(a: &[u8]) -> usize { a.iter().map(if true { |x: &u8| *x } else { |x: &u8| *x }).count() }", "E0308")]
  [TestCase("pub fn f(a: &[u8]) -> usize { a.iter().map({ |x: &u8| *x }).count() }", RustFrontend.EscapingClosure)]
  [TestCase("pub fn f(a: &[u8]) -> usize { a.iter().map(3).count() }", "FnMut")]
  [TestCase("pub fn f(a: &[u8]) -> usize { a.iter().map(missing).count() }", "E0425")]
  [TestCase("pub fn f(x: bool) -> bool { x.go() }", RustFrontend.Unsupported)]
  public void MethodOrIterator_IsRefused(string source, string expected) => Refused(source, expected);

  // ---------------------------------------------------------------- patterns and loops

  [TestCase("pub fn f() -> u32 { let x; for i in x {} 1 }", RustFrontend.UnresolvedType)]
  [TestCase("pub fn f(x: u32) -> u32 { match x { .. => 1 } }", "not allowed here")]
  [TestCase("pub fn f(o: &mut Option<u32>) -> u32 { if let Some(v) = o { *v } else { 0 } }", "by mutable reference")]
  [TestCase("const A: bool = false; const B: bool = true; pub fn f(x: bool) -> u32 { match x { A..=B => 1 } }", "E0029")]
  [TestCase("pub fn f(x: &u32) -> u32 { let &mut y = x; y }", "E0308")]
  [TestCase("pub fn f() -> u32 { let x; let &y = x; 1 }", RustFrontend.UnresolvedType)]
  [TestCase("pub fn f(x: (u32, u32)) -> u32 { let (.., a, ..) = x; a }", "only be used once")]
  [TestCase("pub fn f(x: (u32, u32)) -> u32 { let (a, b, c, ..) = x; a }", "at least 3")]
  [TestCase("pub fn f(x: (u32, u32)) -> u32 { let (a, b, c) = x; a }", "E0308")]
  [TestCase("pub fn f(o: Option<u32>) -> u32 { match o { Some(a, b) => a, None => 0 } }", "E0023")]
  [TestCase("enum E { A } pub fn f(e: E) -> u32 { match e { E::A(x) => 1 } }", "E0532")]
  [TestCase("pub fn f(x: u32) -> u32 { match x { std::Thing(a) => 1, _ => 0 } }", RustFrontend.ForeignPath)]
  [TestCase("pub fn f(x: u32) -> u32 { match x { Missing(a) => 1, _ => 0 } }", "E0531")]
  [TestCase("struct S { a: u32 } pub fn f(s: S) -> u32 { let S { b } = s; 1 }", "E0026")]
  [TestCase("struct S { a: u32, b: u32 } pub fn f(s: S) -> u32 { let S { a } = s; a }", "E0027")]
  [TestCase("struct S { a: u32 } pub fn f(x: u32) -> u32 { match x { Missing { a } => 1, _ => 0 } }", "E0422")]
  [TestCase("pub fn f(x: u32, y: u32) -> u32 { match x { 1..=y => 1, _ => 0 } }", RustFrontend.UnsupportedPattern)]
  public void PatternOrLoop_IsRefused(string source, string expected) => Refused(source, expected);

  // ---------------------------------------------------------------- forms with their values

  [TestCase("use std::cmp; pub fn f() -> u32 { cmp::min(4, 2) + cmp::max(4, 2) }", "6")]
  [TestCase("use std::cmp::*; pub fn f() -> u32 { min(4, 2) }", "2")]
  [TestCase("use std::mem; pub fn f() -> u32 { let mut a = 1; let mut b = 2; mem::swap(&mut a, &mut b); a }", "2")]
  [TestCase("pub fn f() -> u32 { let x = 5u32; let r = &x; let rr = &r; **rr }", "5")]
  [TestCase("fn g(y: &mut u32) { *y += 2; } pub fn f() -> u32 { let mut x = 1; let r = &mut x; g(&mut *r); x }", "3")]
  [TestCase("pub fn f() -> u32 { let g = |x: Option<u32>| { let v = x?; Some(v + 1) }; g(Some(1)).unwrap() }", "2")]
  [TestCase("pub fn f() -> u32 { let g = |x: Result<u32, u8>| { let v = x?; Ok::<u32, u8>(v + 1) }; 1 }", "1")]
  [TestCase("pub fn f() -> u32 { panic!(\"a {{ b }}\") }", RustFixture.Panic)]
  [TestCase("pub fn f() -> u32 { let o: Option<u32> = Some(3); let a = [4u32]; o.clone().unwrap() + a.first().copied().unwrap() + a.last().cloned().unwrap() }", "11")]
  [TestCase("pub fn f() -> u32 { const K: u32 = 3; match 3u32 { K => 1, _ => 0 } }", "1")]
  [TestCase("pub fn f() -> u32 { let r: Result<u32, u8> = Ok(4); r.clone().unwrap_or(1) + r.unwrap_or(2) }", "8")]
  [TestCase("pub fn f() -> u32 { let x = 3u32; x.clone() + true.clone() as u32 }", "4")]
  [TestCase("pub fn f() -> u32 { let a = [1u32, 2]; let b = a.clone(); b[1] }", "2")]
  [TestCase("struct S { a: u32 } impl S { const K: u32 = 4; } pub fn f() -> u32 { let a = [0u8; S::K as usize]; a.len() as u32 }", "4")]
  [TestCase("pub fn f() -> u32 { const N: usize = 3; let a = [1u8; N]; a.len() as u32 }", "3")]
  [TestCase("const N: usize = 2; pub fn f() -> u32 { let a: [u8; N] = [1, 2]; a.len() as u32 }", "2")]
  [TestCase("pub fn f() -> u32 { let t = (1u32, (2u32, 3u32)); let (a, (b, c)) = t; a + b + c }", "6")]
  [TestCase("pub fn f() -> u32 { let t = (1u32, 2u32); match t { (1, x) | (x, 1) => x, _ => 0 } }", RustFrontend.UnsupportedPattern)]
  public void Form_HasTheValueRustGives(string source, string expected) {
    var result = Import(source);
    if (expected.StartsWith("PLY", System.StringComparison.Ordinal)) {
      Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain(expected));
      return;
    }

    Assert.That(Run(source), Is.EqualTo(expected));
  }

  /// <summary>A nested function is checked once, however often it is called.</summary>
  [Test]
  public void NestedFunction_IsOneFunction() {
    var result = Import("pub fn f() -> u32 { fn g() -> u32 { 1 } g() + g() }");
    Assert.That(result.Module!.Functions.Select(function => function.Name), Is.EqualTo(new[] { "f", "f_g" }));
  }

}
