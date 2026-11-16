using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// Trait objects beside the <c>objects</c> fixture: <c>&amp;dyn</c>, <c>Box&lt;dyn&gt;</c> and
/// <c>Rc&lt;dyn&gt;</c> by every path that names them, the coercion sites, methods with slices and
/// <c>&amp;mut self</c>, and every error and refusal - each value and code the one rustc 1.99 gave.
/// </summary>
[TestFixture]
public sealed class RustTraitObjectTests {

  private static ImportResult Import(string source) => RustFrontend.Import("lib.rs", source, OverflowChecks.Panic);

  [TestCase("trait A { fn a(&self) -> u8; } struct S(u8); impl A for S { fn a(&self) -> u8 { self.0 } } fn g(x: &dyn A) -> u8 { x.a() } pub fn f() -> u8 { let s = S(4); let r = &s; g(r) + g(&S(1)) }", "5")]
  [TestCase("use std::rc::Rc as Shared; trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { *self + 1 } } pub fn f() -> u8 { let x: Shared<dyn A> = Shared::new(4u8); let y = x.clone(); x.a() + y.a() }", "10")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for bool { fn a(&self) -> u8 { *self as u8 } } pub fn f() -> u8 { let b: std::boxed::Box<dyn A + Send> = std::boxed::Box::new(true); let c: std::rc::Rc<dyn A> = std::rc::Rc::new(false); b.a() * 2 + c.a() }", "2")]
  [TestCase("trait A { fn a(&self) -> u8; fn eat(self) -> u8; } impl A for u8 { fn a(&self) -> u8 { *self } fn eat(self) -> u8 { self } } pub fn f() -> u8 { let b: Box<dyn A> = Box::new(3u8); b.a() + 4u8.eat() }", "7")]
  [TestCase("trait A { fn s(&self, v: &[u8]) -> usize; } struct S { k: usize } impl A for S { fn s(&self, v: &[u8]) -> usize { v.len() * self.k } } pub fn f() -> usize { let b: Box<dyn A> = Box::new(S { k: 3 }); let v = vec![1u8, 2]; b.s(&v) + b.s(&[1, 2, 3]) }", "15")]
  [TestCase("trait C { fn inc(&mut self); fn get(&self) -> u8; } struct T { n: u8 } impl C for T { fn inc(&mut self) { self.n += 1; } fn get(&self) -> u8 { self.n } } fn bump(b: &mut Box<dyn C>) { b.inc(); } pub fn f() -> u8 { let mut b: Box<dyn C> = Box::new(T { n: 5 }); bump(&mut b); b.inc(); b.get() }", "7")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { *self } } impl A for i8 { fn a(&self) -> u8 { 9 } } pub fn f() -> u8 { let mut b: Box<dyn A> = Box::new(1u8); let x = b.a(); b = Box::new(2i8); x + b.a() }", "10")]
  [TestCase("use std::rc::Rc; trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { *self } } pub fn f() -> u8 { let v: Vec<Rc<dyn A>> = vec![Rc::new(1u8), Rc::new(4u8)]; let w = v.clone(); w[0].a() + v[1].a() + w.len() as u8 }", "7")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { *self } } impl A for i8 { fn a(&self) -> u8 { 9 } } pub fn f() -> u8 { let x = 1u8; let y = 2i8; let mut t = 0; for k in 0..3 { let r: &dyn A = match k { 0 => &x, _ => &y }; t += r.a(); } t }", "19")]
  [TestCase("trait A { fn a(&self) -> u8; } trait B: A { fn b(&self) -> u8 { self.a() + 1 } } trait C: A { fn c(&self) -> u8 { self.a() + 2 } } trait D: B + C { fn d(&self) -> u8 { self.b() * self.c() } } impl A for u8 { fn a(&self) -> u8 { *self } } impl B for u8 {} impl C for u8 {} impl D for u8 {} fn via(x: &dyn D) -> u8 { x.a() + x.d() } pub fn f() -> u8 { let x: &dyn D = &3u8; via(x) }", "23")]
  [TestCase("trait C { fn add(&mut self, k: u8) -> usize; } impl C for Vec<u8> { fn add(&mut self, k: u8) -> usize { self.push(k); self.len() } } pub fn f() -> usize { let mut b: Box<dyn C> = Box::new(vec![1u8]); b.add(2); b.add(3) }", "3")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { *self } } fn A_a() -> u8 { 5 } pub fn f() -> u8 { let b: Box<dyn A> = Box::new(2u8); b.a() + A_a() }", "7")]
  [TestCase("trait A { fn fill(&self, v: &mut [u8]); } impl A for u8 { fn fill(&self, v: &mut [u8]) { v[0] = *self; } } pub fn f() -> u8 { let b: Box<dyn A> = Box::new(7u8); let mut a = [0u8; 2]; b.fill(&mut a); a[0] + a[1] }", "7")]
  [TestCase("struct Box; impl Box { fn new() -> Box { Box } } pub fn f() -> u8 { let _b = Box::new(); 1 }", "1")]
  public void TraitObject_HasTheValueRustGives(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    Assert.That(RustFixture.Interpreted(result.Module!, new PrototypeCall("f", [])), Is.EqualTo(expected));
  }

  [Test]
  public void TraitObject_IsAnInterfaceAndAClassPerType() {
    var result = Import("pub trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { 1 } } impl A for i8 { fn a(&self) -> u8 { 2 } } pub fn f() -> u8 { let x: Box<dyn A> = Box::new(1u8); let y: Box<dyn A> = Box::new(2u8); let z: Box<dyn A> = Box::new(3i8); x.a() + y.a() + z.a() }");
    Assert.That(result.Diagnostics, Is.Empty);
    var types = result.Module!.Types;
    var shape = types.OfType<IrInterfaceType>().Single();
    Assert.That((shape.Name, shape.Visibility, shape.Methods.Single().Name), Is.EqualTo(("A", IrVisibility.Public, "a")));
    Assert.That(types.OfType<IrClassType>().Select(type => (type.Name, type.IsSealed, type.Interfaces.Single() == shape)), Is.EqualTo(new[] { ("A_for_u8", true, true), ("A_for_i8", true, true) }));
  }

  [TestCase("trait A { fn a(&self) -> u8; } struct S; pub fn f() -> u8 { let b: Box<dyn A> = Box::new(S); b.a() }", "E0277: the trait bound `S: A` is not satisfied")]
  [TestCase("trait A { fn new() -> Self; } fn g(x: &dyn A) {} pub fn f() -> u8 { 1 }", "E0038: the trait `A` is not dyn compatible: associated function `new` has no `self` parameter")]
  [TestCase("trait A { fn g<T>(&self, t: T); } fn h(x: &dyn A) {} pub fn f() -> u8 { 1 }", "E0038: the trait `A` is not dyn compatible: method `g` has generic type parameters")]
  [TestCase("trait A { fn same(&self, o: &Self) -> bool; } fn h(x: &dyn A) {} pub fn f() -> u8 { 1 }", "E0038: the trait `A` is not dyn compatible: method `same` references the `Self` type")]
  [TestCase("trait A { fn me(&self) -> Option<Self>; } fn h(x: &dyn A) {} pub fn f() -> u8 { 1 }", "E0038")]
  [TestCase("trait A { type Out; fn a(&self) -> u8; } fn h(x: &dyn A<Out = u8>) {} pub fn f() -> u8 { 1 }", "dyn A, whose trait A has the associated type Out")]
  [TestCase("trait A { fn a(&self) -> u8; } trait B { fn b(&self) -> u8; } fn h(x: Box<dyn A + B>) {} pub fn f() -> u8 { 1 }", "E0225: only auto traits can be used as additional traits in a trait object")]
  [TestCase("fn h(x: Box<dyn Send>) {} pub fn f() -> u8 { 1 }", "dyn Send without a trait of the crate")]
  [TestCase("fn h(x: &dyn std::fmt::Display) {} pub fn f() -> u8 { 1 }", "dyn std::fmt::Display, a trait object of a trait outside the crate")]
  [TestCase("trait A { fn v(&self) -> u8; } trait B: A { fn v(&self) -> u8; } fn h(x: &dyn B) {} pub fn f() -> u8 { 1 }", "dyn B, whose traits share the method name 'v'")]
  [TestCase("trait A { fn a(&self, x: &mut u8); } fn h(x: &dyn A) {} pub fn f() -> u8 { 1 }", "dyn A, whose method 'a' takes a &mut parameter")]
  [TestCase("trait A { fn a(&self, x: UnknownType); } fn h(x: &dyn A) {} pub fn f() -> u8 { 1 }", "E0412")]
  [TestCase("trait A { fn a(&self) -> u8; } pub fn f(x: &mut dyn A) -> u8 { x.a() }", "&mut dyn A, a trait object that writes through to a borrowed value")]
  [TestCase("pub fn f() -> u8 { let b: Box<u8> = Box::new(1); *b }", "PLY1523 smart pointer or interior-mutability type 'Box<u8>'")]
  [TestCase("pub fn f() -> u8 { let r = std::rc::Rc::new(5u8); 1 }", "std::rc::Rc::new of a value that becomes no trait object")]
  [TestCase("use std::rc::Rc; pub fn f() -> u8 { let r = Rc::clone(5); 1 }", "E0308: mismatched types: expected `&Rc<_>`, found `{integer}`")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { 1 } } pub fn f() -> u8 { let b: Box<dyn A> = Box::new(1u8); b.z() }", "E0599: no method named `z` found for `Box<dyn A>`")]
  [TestCase("trait A { fn a(&self) -> u8; fn eat(self) -> u8; } impl A for u8 { fn a(&self) -> u8 { 1 } fn eat(self) -> u8 { 2 } } pub fn f() -> u8 { let b: Box<dyn A> = Box::new(1u8); b.eat() }", "E0161: cannot move a value of type `dyn A`")]
  [TestCase("trait C { fn inc(&mut self); } impl C for u8 { fn inc(&mut self) { *self += 1; } } fn g(x: &dyn C) { x.inc(); } pub fn f() -> u8 { 1 }", "E0596: cannot borrow `*x` as mutable, as it is behind a `&` reference")]
  [TestCase("use std::rc::Rc; trait C { fn inc(&mut self); } impl C for u8 { fn inc(&mut self) { *self += 1; } } pub fn f() -> u8 { let r: Rc<dyn C> = Rc::new(1u8); r.inc(); 1 }", "E0596: cannot borrow data in an `Rc` as mutable")]
  [TestCase("trait C { fn inc(&mut self); } impl C for u8 { fn inc(&mut self) { *self += 1; } } fn g(b: &Box<dyn C>) { b.inc(); } pub fn f() -> u8 { 1 }", "E0596: cannot borrow `**b` as mutable, as it is behind a `&` reference")]
  [TestCase("trait C { fn inc(&mut self); } impl C for u8 { fn inc(&mut self) { *self += 1; } } pub fn f() -> u8 { let b: Box<dyn C> = Box::new(1u8); b.inc(); 1 }", "E0596: cannot borrow `b` as mutable, as it is not declared as mutable")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { 1 } } pub fn f() -> u8 { let b: Box<dyn A> = Box::new(1u8); let c = b.clone(); 1 }", "E0599: the method `clone` exists for struct `Box<dyn A>`, but its trait bounds were not satisfied")]
  [TestCase("trait A { fn a(&self) -> u8; } struct H { s: Box<dyn A> } pub fn f() -> u8 { 1 }", "field 's' holding the trait object 'Box<dyn A>'")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for str { fn a(&self) -> u8 { 1 } } pub fn f() -> u8 { let x: &dyn A = \"hi\"; x.a() }", "E0277: the size for values of type `str` cannot be known at compilation time")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { 1 } } fn g(x: &dyn A) -> u8 { x.a() } pub fn f() -> u8 { let b: Box<dyn A> = Box::new(1u8); g(&b) }", "E0277: the trait bound `Box<dyn A>: A` is not satisfied")]
  [TestCase("trait A { fn a(&self, k: u8) -> u8; } impl A for u8 { fn a(&self, k: u8) -> u8 { k } } pub fn f() -> u8 { let b: Box<dyn A> = Box::new(1u8); b.a() }", "E0061: this method takes 1 argument but 0 were supplied")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { 1 } } pub fn f() -> u8 { let b: Box<dyn A> = Box::new(None.unwrap()); b.a() }", RustFrontend.UnresolvedType)]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { 1 } } pub fn f() -> u8 { let b: Box<dyn A> = Box::new(Unknown); b.a() }", "E0425")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { 1 } } pub fn f() -> bool { let b: Box<dyn A> = Box::new(1u8); let c: Box<dyn A> = Box::new(2u8); b == c }", "E0369")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { *self } } fn g(b: &mut Box<dyn A>) { *b = Box::new(2u8); } pub fn f() -> u8 { 1 }", "assignment of a new trait object through &mut Box<dyn A>")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { *self } } pub fn f() -> u8 { let v: Vec<Box<dyn A>> = vec![Box::new(1u8)]; let w = v.clone(); w[0].a() }", "E0599: the method `clone` exists for struct `Vec<Box<dyn A>>`, but its trait bounds were not satisfied")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { *self } } pub fn f() -> usize { let v: Vec<Box<dyn A>> = vec![Box::new(1u8)]; v.to_vec().len() }", "E0277: the trait bound `Box<dyn A>: Clone` is not satisfied")]
  [TestCase("trait A { fn a(&self) -> u8; } pub fn f(a: [Box<dyn A>; 2]) {}", "array of Box<dyn A>")]
  [TestCase("trait A { fn a(&self) -> u8; } pub fn f(a: &[Box<dyn A>]) {}", "slice of Box<dyn A>")]
  [TestCase("trait A { fn a(&self) -> u8; } pub fn f(a: Option<Box<dyn A>>) {}", "Option<Box<dyn A>>")]
  [TestCase("use std::rc::Rc; pub fn f() -> u8 { let r = Rc::clone(&5u8); 1 }", "E0308: mismatched types: expected `&Rc<_>`, found `&u8`")]
  [TestCase("trait A { fn a(&self) -> u8; } pub fn f() -> u8 { let b: Box<dyn A> = Box::new(1u128); b.a() }", "PLY1501 type 'u128'")]
  [TestCase("trait A { fn a(&self) -> u8; } impl A for u8 { fn a(&self) -> u8 { 1 } } pub fn f() -> u8 { let b: Box<dyn A> = Box::from(1u8); b.a() }", "Box::from of a value that becomes no trait object")]
  public void TraitObject_IsRefused(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message), Has.Some.Contains(expected));
  }

}
