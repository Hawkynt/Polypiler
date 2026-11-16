using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// Generic structs, impls, methods and traits beside the <c>generics</c> fixture: annotations,
/// turbofish on types and methods, patterns, impl parameters named apart from the struct's, generic
/// operator impls with <c>Self::Output</c>, generic traits with provided methods and bounds, generic
/// trait methods, exported instances, and every error and refusal - each value and code the one
/// rustc 1.99 gave.
/// </summary>
[TestFixture]
public sealed class RustGenericTests {

  private static ImportResult Import(string source) => RustFrontend.Import("lib.rs", source, OverflowChecks.Panic);

  [TestCase("struct Pair<T> { a: T, b: T } pub fn f() -> u16 { let p: Pair<u16> = Pair { a: 1, b: 65534 }; let Pair { a, b } = p; a + b }", "65535")]
  [TestCase("struct Wrap<T>(T); pub fn f() -> u8 { let w = Wrap::<u8>(5); match w { Wrap(x) => x + 1 } }", "6")]
  [TestCase("#[derive(Clone, Copy)] struct P<T> { x: T } impl<U: Copy> P<U> { fn get(&self) -> U { self.x } fn with<V: Copy>(&self, v: V) -> (U, V) { (self.x, v) } fn again(&self) -> Self { Self::make(self.x) } fn make(x: U) -> Self { P { x } } } pub fn f() -> u64 { let p = P { x: 7u8 }; let t = p.with::<u64>(5); p.again().get() as u64 + t.0 as u64 + t.1 }", "19")]
  [TestCase("#[derive(Clone, Copy)] struct Pair<T> { a: T, b: T } fn first<T: Copy>(p: Pair<T>) -> T { p.a } struct Outer { p: Pair<u8> } pub fn f() -> i32 { let o = Outer { p: Pair { a: 3, b: 4 } }; first(o.p) as i32 + o.p.b as i32 }", "7")]
  [TestCase("#[derive(Clone, Copy)] struct V<T> { x: T } impl<T: Copy + std::ops::Mul<Output = T>> std::ops::Mul for V<T> { type Output = Self; fn mul(self, o: Self) -> Self::Output { V { x: self.x * o.x } } } pub fn f() -> i16 { (V { x: -3i16 } * V { x: 7 }).x }", "-21")]
  [TestCase("trait Conv<T> { fn conv(&self) -> T; fn plus(&self, t: T) -> T { t } } impl Conv<u32> for u8 { fn conv(&self) -> u32 { *self as u32 + 1 } } fn go<X: Conv<u32>>(x: X) -> u32 { x.conv() * 2 } pub fn f() -> u32 { go(4u8) + 3u8.plus(10) }", "20")]
  [TestCase("trait T { fn g<U: Copy>(&self, u: U) -> U { u } fn h(&self) -> u8; } struct S; impl T for S { fn h(&self) -> u8 { 2 } } pub fn f() -> u64 { S.g(5u64) + S.h() as u64 + S.g(1u8) as u64 }", "8")]
  [TestCase("pub trait A { fn a(&self) -> u8; fn b(&self) -> u8 { self.a() + 1 } } struct W<T> { v: T } impl<T: Copy> A for W<T> { fn a(&self) -> u8 { 4 } } pub fn f() -> u8 { let w = W { v: 1u16 }; w.b() + W { v: true }.b() }", "10")]
  [TestCase("trait A { fn a(&self) -> u8; } struct W<T>(T); impl<T> A for W<T> { fn a(&self) -> u8 { 1 } } pub fn f() -> u8 { let b: Box<dyn A> = Box::new(W(5)); b.a() }", "1")]
  [TestCase("use std::collections::{HashMap, HashSet}; fn size<T>(m: &HashMap<T, u8>, s: &HashSet<T>) -> usize { m.len() + s.len() } pub fn f() -> usize { let mut m = HashMap::new(); m.insert(1u8, 2u8); let mut s = HashSet::new(); s.insert(3u8); s.insert(4); size(&m, &s) }", "3")]
  [TestCase("trait Make { fn make() -> Self; } struct W<T> { v: T } impl<T: Default> Make for W<T> { fn make() -> Self { W { v: T::default() } } } fn build<X: Make>() -> X { X::make() } pub fn f() -> u8 { build::<W<u8>>().v + 1 }", "1")]
  public void Generic_HasTheValueRustGives(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    Assert.That(RustFixture.Interpreted(result.Module!, new PrototypeCall("f", [])), Is.EqualTo(expected));
  }

  [Test]
  public void PublicGenericFunction_IsExportedAsTheInstancesTheCrateMakes() {
    var result = Import("pub mod m { pub fn id<T>(x: T) -> T { x } } pub fn f() -> u8 { m::id(3u8) + m::id(1i64) as u8 }");
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(result.Module!.Functions.Where(function => function.IsPublic).Select(function => function.Name), Is.EquivalentTo(new[] { "f", "m_id_u8", "m_id_i64" }));
    Assert.That(result.Instances, Is.EquivalentTo(new[] { new System.Collections.Generic.KeyValuePair<string, string>("m_id_u8", "m::id::<u8>"), new("m_id_i64", "m::id::<i64>") }));
    Assert.That(RustFixture.Interpreted(result.Module!, new PrototypeCall("m_id_i64", [-5L])), Is.EqualTo("-5"));
  }

  [Test]
  public void GenericStruct_IsAStructPerListOfTypeArguments() {
    var result = Import("#[derive(Clone, Copy)] struct Pair<T> { a: T, b: T } pub fn f() -> u8 { let p = Pair { a: 1u8, b: 2 }; let q = Pair { a: 3i64, b: 4 }; let r = Pair { a: p, b: p }; p.a + q.b as u8 + r.b.a }");
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(result.Module!.Types.OfType<IrStructType>().Select(type => type.Name), Is.EquivalentTo(new[] { "Pair_u8", "Pair_i64", "Pair_Pair_u8" }));
  }

  [TestCase("struct Pair<T> { a: T, b: T } pub fn f(p: Pair<u8, u8>) -> u8 { 1 }", "E0107: struct takes 1 generic argument but 2 generic arguments were supplied")]
  [TestCase("struct Pair<T> { a: T, b: T } pub fn f(p: Pair) -> u8 { 1 }", "E0107: struct takes 1 generic argument but 0 generic arguments were supplied")]
  [TestCase("struct Wrap<T>(T); pub fn f() -> u8 { Wrap::<u8, u8>(1).0 }", "E0107: struct takes 1 generic argument but 2 generic arguments were supplied")]
  [TestCase("struct Wrap(u8); pub fn f() -> u8 { Wrap::<u8>(1).0 }", "E0107: struct takes 0 generic arguments but 1 generic arguments were supplied")]
  [TestCase("trait Tr { fn t(&self) -> u8; } impl<T> Tr for Vec<T> { fn t(&self) -> u8 { 1 } } pub fn f() -> u8 { 1 }", "impl<T> for Vec<T>, which is no generic struct of the crate for all its type arguments in order")]
  [TestCase("struct S<A, B> { a: A, b: B } impl<A, B> S<B, A> { fn g(&self) -> u8 { 1 } } pub fn f() -> u8 { 1 }", "impl<A, B> for S<B, A>")]
  [TestCase("struct S<T> { a: T } impl<T> S<Vec<T>> { fn g(&self) -> u8 { 1 } } pub fn f() -> u8 { 1 }", "impl<T> for S<Vec<T>>")]
  [TestCase("struct S<T> { a: u8 } pub fn f() -> u8 { 1 }", "E0392: type parameter `T` is never used")]
  [TestCase("struct Pair<T> { a: T, b: T } pub fn f() -> usize { let p = Pair { a: String::new(), b: String::new() }; p.a.len() }", "field 'a' of type 'String' of Pair<String>")]
  [TestCase("trait A { fn a(&self) -> u8; } struct W<T> { a: T } pub fn f() -> u8 { let w = W { a: Box::new(1u8) as Box<dyn A> }; 1 }", "field 'a' of type 'Box<dyn A>' of W<Box<dyn A>>")]
  [TestCase("struct Pair<T> { a: T, b: T } impl<T: Copy + std::ops::Add<Output = T>> Pair<T> { fn sum(&self) -> T { self.a + self.b } } pub fn f() -> bool { Pair { a: true, b: false }.sum() }", "E0599: the method `sum` exists for struct `Pair<bool>`, but its trait bounds were not satisfied")]
  [TestCase("trait Conv<T> { fn conv(&self) -> T; } impl Conv for u8 { fn conv(&self) -> u32 { 1 } } pub fn f() -> u8 { 1 }", "E0107: trait takes 1 generic argument but 0 generic arguments were supplied")]
  [TestCase("trait Conv<T> { fn conv(&self) -> T; } impl Conv<u32> for u8 { fn conv(&self) -> u32 { 1 } } impl Conv<u16> for u8 { fn conv(&self) -> u16 { 2 } } pub fn f() -> u32 { 5u8.conv() }", "method 'conv' of the generic trait Conv, which 'u8' implements with several type arguments")]
  [TestCase("trait Conv<T> { fn conv(&self) -> T; } impl Conv<u32> for u8 { fn conv(&self) -> u32 { 1 } } impl Conv<u32> for u8 { fn conv(&self) -> u32 { 2 } } pub fn f() -> u8 { 1 }", "E0119: conflicting implementations of trait `Conv<u32>` for type `u8`")]
  [TestCase("trait Conv<T> { fn conv(&self) -> T; } impl Conv<Unknown> for u8 { fn conv(&self) -> u32 { 1 } } pub fn f() -> u8 { 1 }", "E0412")]
  [TestCase("trait Conv<T> { fn conv(&self) -> T; } impl Conv<u16> for u8 { fn conv(&self) -> u16 { 1 } } fn g<X: Conv<u32>>(x: X) -> u32 { x.conv() } pub fn f() -> u32 { g(1u8) }", "E0277: the trait bound `u8: Conv<u32>` is not satisfied")]
  [TestCase("trait Conv<T> { fn conv(&self) -> T; } trait A: Conv<u8> {} pub fn f() -> u8 { 1 }", "supertrait 'Conv<u8>' of the generic trait Conv")]
  [TestCase("pub fn id<T>(x: T) -> T { x } pub fn f() -> u8 { 1 }", "public generic fn id, which the crate instantiates nowhere")]
  [TestCase("struct S(u8); pub fn id<T>(x: T) -> T { x } pub fn f() -> u8 { id(S(4)).0 }", "public generic fn id<S>, whose exported instance names the private type 'S'")]
  [TestCase("struct S; impl S { fn g<T>(&self) -> u8 { 1 } } pub fn f() -> u8 { S.g() }", "E0282: type annotations needed: cannot infer the type of the type parameter of `g`")]
  [TestCase("struct S; impl S { fn g<T>(&self) -> u8 { 1 } } pub fn f() -> u8 { S.g::<u8, u8>() }", "E0107: method takes 1 generic argument but 2 were supplied")]
  [TestCase("struct Pair<T> { a: T, b: T } impl<T> Pair<T> { fn new(a: T, b: T) -> Self { Pair { a, b } } } pub fn f() -> u8 { Pair::<u8, u8>::new(1, 2).a }", "E0107: struct takes 1 generic argument but 2 were supplied")]
  [TestCase("fn g<T>(x: T) -> T { x } pub fn f() -> u8 { g::<u8, u8>(1) }", "E0107: function takes 1 generic argument but 2 were supplied")]
  public void Generic_IsRefused(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message), Has.Some.Contains(expected));
  }

}
