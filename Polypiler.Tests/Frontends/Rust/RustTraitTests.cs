using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// Traits and generics beside the <c>traits</c> fixture: the operator traits <c>Neg</c> and
/// <c>AddAssign</c>, <c>where</c> clauses and <c>?Sized</c>, bounds on <c>Display</c>,
/// <c>PartialEq</c>, <c>Clone</c>, <c>Mul</c> and <c>Default</c>, traits of primitives, <c>&amp;mut self</c>
/// trait methods, and every error and refusal - each value the one rustc 1.99 printed.
/// </summary>
[TestFixture]
public sealed class RustTraitTests {

  private static ImportResult Import(string source) => RustFrontend.Import("lib.rs", source, OverflowChecks.Panic);

  [TestCase("#[derive(Clone, Copy)] struct V { x: i32 } impl std::ops::Neg for V { type Output = V; fn neg(self) -> V { V { x: -self.x } } } impl std::ops::AddAssign for V { fn add_assign(&mut self, o: V) { self.x += o.x; } } pub fn f() -> i32 { let mut v = V { x: 3 }; v += -V { x: 10 }; v += V { x: 1 }; v.x }", "-6")]
  [TestCase("fn first<T>(a: T, b: T) -> T where T: Copy { let _ = b; a } pub fn f() -> u8 { first(7u8, 9) }", "7")]
  [TestCase("fn show<T: std::fmt::Display>(x: T) -> String { format!(\"<{}>\", x) } pub fn f() -> String { show(5u8) + &show(\"é\") + &show(true) }", "<5><é><true>")]
  [TestCase("fn same<T: PartialEq>(a: T, b: T) -> bool { a == b } pub fn f() -> bool { same(String::from(\"x\"), \"x\".to_string()) && !same(1.5, 2.5) }", "true")]
  [TestCase("fn dup<T: Clone>(x: &T) -> T { x.clone() } pub fn f() -> usize { dup(&String::from(\"ab\")).len() + dup(&vec![1u8, 2]).len() }", "4")]
  [TestCase("trait Area { fn area(&self) -> u32; } struct S { s: u32 } impl Area for S { fn area(&self) -> u32 { self.s } } fn both<A: Area, B: Area>(a: &A, b: &B) -> u32 { a.area() * 10 + b.area() } pub fn f() -> u32 { both(&S { s: 2 }, &S { s: 3 }) }", "23")]
  [TestCase("trait T { fn v(&self) -> u8; fn w(&self) -> u8 { self.v() + 1 } } impl T for u8 { fn v(&self) -> u8 { *self } } impl T for bool { fn v(&self) -> u8 { *self as u8 } } pub fn f() -> u8 { 5u8.w() * 10 + true.w() }", "62")]
  [TestCase("fn id<T: ?Sized>(x: &T) -> &T { x } fn twice<T: Copy + std::ops::Mul<Output = T>>(x: T) -> T { x * x } pub fn f() -> i64 { twice(-3i64) + twice(2i64) }", "13")]
  [TestCase("#[derive(Clone, Copy, Default)] struct P { a: u8 } fn make<T: Default>() -> T { T::default() } pub fn f() -> u8 { make::<P>().a + make::<u8>() }", "0")]
  [TestCase("trait Count { fn count(&mut self) -> u32; } struct C { n: u32 } impl Count for C { fn count(&mut self) -> u32 { self.n += 1; self.n } } pub fn f() -> u32 { let mut c = C { n: 0 }; c.count(); c.count() + c.n }", "4")]
  [TestCase("fn first<T: Copy>(o: Option<T>, v: Vec<T>, a: [T; 2], s: &[T], t: (T, T), r: Result<T, u8>, g: fn(T) -> T) -> T { let _ = o; let _ = v; let _ = s; let _ = t; let _ = r; g(a[0]) } fn inc(x: u32) -> u32 { x + 1 } pub fn f() -> u32 { first(Some(1u32), vec![2], [3, 4], &[5], (6, 7), Ok(8), inc) }", "4")]
  [TestCase("fn dbg<T: std::fmt::Debug>(x: T) -> String { format!(\"{:?}\", x) } pub fn f() -> String { dbg(5i8) + &dbg(false) }", "5false")]
  [TestCase("fn bits<T: std::ops::BitAnd<Output = T> + std::ops::BitOr<Output = T> + std::ops::BitXor<Output = T> + Copy>(a: T, b: T) -> T { (a & b) | (a ^ b) } pub fn f() -> u8 { bits(12u8, 10) }", "14")]
  [TestCase("fn sh<T: std::ops::Shl<Output = T> + std::ops::Not<Output = T>>(a: T, b: T) -> T { !(a << b) } pub fn f() -> u8 { sh(1u8, 3) }", "247")]
  [TestCase("fn neg<T: std::ops::Neg<Output = T>>(a: T) -> T { -a } pub fn f() -> i32 { neg(5) }", "-5")]
  [TestCase("fn acc<T: std::ops::AddAssign + Copy>(mut a: T, b: T) -> T { a += b; a } pub fn f() -> u16 { acc(3u16, 4) }", "7")]
  [TestCase("fn ord<T: Ord>(a: T, b: T) -> bool { a < b } fn eqs<T: Eq>(a: T, b: T) -> bool { a == b } pub fn f() -> bool { ord('a', 'b') && eqs(\"x\", \"x\") && ord(String::from(\"a\"), String::from(\"b\")) }", "true")]
  [TestCase("fn cl<T: Clone>(x: &T) -> T { x.clone() } #[derive(Clone)] struct P { a: u8 } #[derive(Clone, Copy)] enum E { A, B } pub fn f() -> u8 { cl(&P { a: 4 }).a + cl(&E::B) as u8 + cl(&1u8) }", "6")]
  [TestCase("fn df<T: Default>() -> T { T::default() } #[derive(Clone, Copy, Default, PartialEq)] enum E { #[default] A, B } pub fn f() -> bool { df::<E>() == E::A && df::<String>().is_empty() && df::<char>() == '\\0' && df::<f64>() == 0.0 }", "true")]
  [TestCase("trait W { fn len(&self) -> u8; fn twice(&self) -> u8; } impl W for String { fn len(&self) -> u8 { 99 } fn twice(&self) -> u8 { 2 } } impl W for char { fn len(&self) -> u8 { 98 } fn twice(&self) -> u8 { 4 } } pub fn f() -> usize { let s = String::from(\"ab\"); s.len() + s.twice() as usize + 'x'.twice() as usize + 'x'.len_utf8() }", "9")]
  [TestCase("fn g<T: std::ops::Add + Copy + Send>(a: T) -> T where T: std::ops::Add<Output = T> { a + a } pub fn f() -> u32 { g(4u32) }", "8")]
  [TestCase("trait A: Sized { fn v(&self) -> u8; } struct S; impl A for S { fn v(&self) -> u8 { 3 } } fn g<T: A>(x: &T) -> u8 { T::v(x) } pub fn f() -> u8 { g(&S) }", "3")]
  [TestCase("#[derive(Clone, Copy, PartialEq)] enum E { A, B } pub fn f() -> bool { E::A != E::B }", "true")]
  [TestCase("fn g<T, U>(a: T, b: U) -> T where T: Copy, U: Copy, { let _ = b; a } pub fn f() -> u8 { g(5u8, 1u16) }", "5")]
  [TestCase("trait W { fn w(&self) -> u8; } impl W for (u8, u8) { fn w(&self) -> u8 { self.0 + self.1 } } pub fn f() -> u8 { (1u8, 2u8).w() }", "3")]
  [TestCase("pub fn f() -> usize { let s = \"ab\"; let t = s.clone(); t.len() }", "2")]
  [TestCase("trait T { #[inline] fn a(&self) -> u8 { 4 } } struct S; impl T for S {} pub fn f() -> u8 { S.a() }", "4")]
  public void Trait_HasTheValueRustGives(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    Assert.That(RustFixture.Interpreted(result.Module!, new PrototypeCall("f", [])), Is.EqualTo(expected));
  }

  [TestCase("trait T { fn a(&self); fn b(&self); } struct S; impl T for S { fn a(&self) {} } pub fn f() {}", "E0046: not all trait items implemented, missing: `b`")]
  [TestCase("trait T { fn a(&self); } struct S; impl T for S { fn a(&self) {} fn c(&self) {} } pub fn f() {}", "E0407: method `c` is not a member of trait `T`")]
  [TestCase("trait T { fn a(&self) {} } struct S; impl T for S {} impl T for S {} pub fn f() {}", "E0119: conflicting implementations of trait `T`")]
  [TestCase("struct S; impl std::fmt::Display for S { } pub fn f() {}", "impl std::fmt::Display for S")]
  [TestCase("struct S<T> { a: T } impl S<u8> { fn g(&self) {} } pub fn f() {}", "impl S<u8>, an inherent impl of one instance of a generic struct")]
  [TestCase("trait T<A> { fn a(&self) -> A; } fn g(x: &dyn T<u8>) {} pub fn f() {}", "dyn T<u8>, a trait object of a generic trait")]
  [TestCase("trait T { const C: u8; } pub fn f() {}", "trait item other than a method")]
  [TestCase("trait T: std::fmt::Debug { } pub fn f() {}", "supertrait 'std::fmt::Debug'")]
  [TestCase("#[derive(Clone, Copy)] struct P { a: u8 } fn larger<T: PartialOrd + Copy>(a: T, b: T) -> T { if a > b { a } else { b } } pub fn f() -> u8 { larger(P { a: 1 }, P { a: 2 }).a }", "E0277: the trait bound `P: PartialOrd` is not satisfied")]
  [TestCase("fn zero<T: Default>() -> T { T::default() } pub fn f() { let x = zero(); }", RustFrontend.UnresolvedType)]
  [TestCase("fn g<T>(x: T) -> T { x } pub fn f() -> u8 { g::<u8, u8>(1) }", "E0107")]
  [TestCase("fn g<T: std::hash::Hash>(x: T) -> T { x } pub fn f() -> u8 { g(1u8) }", "bound 'std::hash::Hash'")]
  [TestCase("fn g<'a>(x: &'a u8) -> u8 { *x } pub fn f() {}", RustFrontend.UnsupportedLifetime)]
  [TestCase("fn g<const N: usize>() -> usize { N } pub fn f() {}", "const generic parameter of fn g")]
  [TestCase("fn g<T = u8>(x: T) -> T { x } pub fn f() {}", "default type parameter")]
  [TestCase("fn g<T>(x: T) -> T where u8: Copy { x } pub fn f() {}", "where clause on a type that is no type parameter")]
  [TestCase("struct S; impl S { fn g<const N: usize>(&self) -> usize { N } } pub fn f() -> usize { 1 }", RustFrontend.UnsupportedGeneric)]
  [TestCase("trait A { fn m(&self) -> u8; } trait B { fn m(&self) -> u8; } struct S; impl A for S { fn m(&self) -> u8 { 1 } } impl B for S { fn m(&self) -> u8 { 2 } } pub fn f() -> u8 { S.m() }", "E0034: multiple applicable items in scope")]
  [TestCase("struct S; impl std::ops::Add for S { type Output; fn add(self, o: S) -> S { o } } pub fn f() {}", "associated type Output")]
  [TestCase("struct S; impl std::ops::Neg<u8> for S { type Output = S; fn neg(self) -> S { self } } pub fn f() {}", RustFrontend.UnsupportedGeneric)]
  [TestCase("#[derive(Clone, Copy)] struct V { x: i32 } impl std::ops::Add for V { type Output = V; fn add(self, o: V) -> V { o } } pub fn f() { let mut v = V { x: 1 }; v += V { x: 2 }; }", "E0368")]
  [TestCase("trait T { type X<A>; } pub fn f() {}", "associated type X with generics or bounds")]
  [TestCase("impl !Send for u8 {} pub fn f() {}", "negative impl")]
  [TestCase("fn g<T: for<'a> Fn(&'a u8)>(x: T) {} pub fn f() {}", "higher-ranked bound")]
  [TestCase("fn g<T: 'static>(x: T) {} pub fn f() {}", RustFrontend.UnsupportedLifetime)]
  [TestCase("fn g<'a, T>(x: &'a T) {} pub fn f() {}", RustFrontend.UnsupportedLifetime)]
  [TestCase("fn g<T: std::ops::Add<Output = T>>(a: T) -> T::Output { a + a } pub fn f() -> u8 { g(1u8) }", "associated type T::Output of a type parameter")]
  [TestCase("pub fn f(x: &dyn std::fmt::Debug) {}", RustFrontend.UnsupportedTrait)]
  [TestCase("fn g<T>() -> T { T::nothing() } pub fn f() -> u8 { g::<u8>() }", "E0599: no function or associated item named `nothing`")]
  [TestCase("fn g<T: Copy>(x: T) -> T { x } pub fn f() -> usize { g(String::new()).len() }", "E0277: the trait bound `String: Copy`")]
  [TestCase("struct P; fn g<T: Clone>(x: &T) {} pub fn f() { g(&P); }", "`P: Clone`")]
  [TestCase("fn g<T: Eq>(x: T) {} pub fn f() { g(1.5); }", "`f64: Eq`")]
  [TestCase("fn g<T: Ord>(x: T) {} pub fn f() { g(1.5); }", "`f64: Ord`")]
  [TestCase("struct P; fn g<T: Default>() -> T { T::default() } pub fn f() { let p: P = g(); }", "`P: Default`")]
  [TestCase("fn g<T: std::fmt::Display>(x: T) {} pub fn f() { g(1.5); }", "`f64: std::fmt::Display`")]
  [TestCase("fn g<T: std::fmt::Debug>(x: T) {} pub fn f() { g('a'); }", "`char: std::fmt::Debug`")]
  [TestCase("fn g<T: std::ops::Add<Output = u8>>(x: T) {} pub fn f() { g(1u16); }", "`u16: std::ops::Add<Output = u8>`")]
  [TestCase("fn g<T: std::ops::BitAnd<Output = T>>(x: T) {} pub fn f() { g(1.5); }", "BitAnd")]
  [TestCase("fn g<T: std::ops::Shl<Output = T>>(x: T) {} pub fn f() { g(true); }", "Shl")]
  [TestCase("fn g<T: std::ops::Neg<Output = T>>(x: T) {} pub fn f() { g(1u8); }", "Neg")]
  [TestCase("fn g<T: std::ops::AddAssign>(x: T) {} pub fn f() { g(true); }", "AddAssign")]
  [TestCase("trait N { fn n(&self) -> u8; } fn g<T: N>(x: T) -> u8 { x.n() } pub fn f() -> u8 { g(1u8) }", "`u8: N`")]
  [TestCase("#[derive(Clone, Copy)] enum E { A } trait N { fn n(&self) -> u8 { 1 } } impl N for E {} pub fn f() -> u8 { E::A.n() + E::A.missing() }", "E0599")]
  [TestCase("fn g<T>(x: T) -> T { x } pub fn f() { let y = g(missing); }", "E0425")]
  [TestCase("fn g<T>() -> u8 { 1 } pub fn f() -> u8 { g() }", "E0282")]
  [TestCase("fn g<T: std::ops::Add<u8>>(a: T) {} pub fn f() { g(1u8); }", "E0277")]
  [TestCase("fn g<T>() -> u8 { T::nothing(1) } pub fn f() -> u8 { g::<u8>() }", "E0599")]
  [TestCase("trait T { fn d(&self) -> u128 { 0 } } struct S; impl T for S {} pub fn f() {}", RustFrontend.UnsupportedType)]
  [TestCase("struct S; impl Default<u8> for S { fn default() -> S { S } } pub fn f() {}", RustFrontend.UnsupportedGeneric)]
  [TestCase("#[derive(Clone, Copy)] struct S; pub fn f() { let x = S + S; }", "E0369")]
  [TestCase("#[derive(Clone, Copy)] struct S; pub fn f() { let x = -S; }", "E0600")]
  [TestCase("fn g<T, 'a: 'static>(x: T) {} pub fn f() {}", RustFrontend.UnsupportedLifetime)]
  [TestCase("pub fn f() -> u8 { (1u8, 2u8).nope(1) }", "E0599")]
  [TestCase("pub fn f() -> u32 { (0..3u32).rev().map(|x| x).rev().sum() }", "rev after rev")]
  [TestCase("trait T where Self: Sized { fn a(&self); } pub fn f() {}", "where clause of a trait")]
  [TestCase("trait T { #[test] fn a(&self) {} } pub fn f() {}", "test")]
  [TestCase("pub fn f() { static mut X: u8 = 0; }", "static mut")]
  public void Trait_IsRefused(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message), Has.Some.Contains(expected));
  }

  /// <summary>A trait that never ends is a syntax error.</summary>
  [Test]
  public void UnclosedTrait_IsASyntaxError()
    => Assert.That(Import("trait T { fn a(&self);").Diagnostics.Single().Message, Does.Contain("unclosed delimiter"));

  /// <summary>A generic function is one IR function for each list of type arguments it is called with, the template none.</summary>
  [Test]
  public void GenericFunction_HasAnInstancePerTypeArguments() {
    var result = Import("fn g<T: Copy>(x: T) -> T { x } pub fn f() -> u32 { g(1u8) as u32 + g(2u32) + g(3u8) as u32 }");
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(result.Module!.Functions.Select(function => function.Name), Is.EquivalentTo(new[] { "f", "g_u8", "g_u32" }));
    Assert.That(RustFixture.Interpreted(result.Module!, new PrototypeCall("f", [])), Is.EqualTo("6"));
  }

}
