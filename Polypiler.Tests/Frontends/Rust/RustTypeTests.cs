using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// The Rust frontend on structs with inherent methods, field-less enums, <c>Option</c> and
/// <c>Result</c> with <c>?</c>: the scenarios of <c>fe-rs-types.feature</c> and the rows of the test
/// design of #467, in the interpreter. The acceptance tests hold the fixture <c>types.rs</c> to rustc.
/// </summary>
[TestFixture]
public sealed class RustTypeTests {

  private static ImportResult Import(string source, OverflowChecks mode = OverflowChecks.Panic) => RustFrontend.Import("snippet.rs", source, mode);

  private static IrModule Module(string source, OverflowChecks mode = OverflowChecks.Panic) {
    var result = Import(source, mode);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static string Run(string source, string function, params object[] arguments) => Run(source, OverflowChecks.Panic, function, arguments);

  private static string Run(string source, OverflowChecks mode, string function, params object[] arguments)
    => RustFixture.Interpreted(Module(source, mode), new PrototypeCall(function, arguments));

  private static string[] Codes(string source) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    return [.. result.Diagnostics.Select(diagnostic => diagnostic.Code)];
  }

  // ---------------------------------------------------------------- fe-rs-types.feature

  private const string Xorshift = "struct Xorshift32 { state: u32 } impl Xorshift32 { fn next(&mut self) -> u32 { let mut x = self.state; x ^= x << 13; x ^= x >> 17; x ^= x << 5; self.state = x; x } } pub fn two(seed: u32) -> (u32, u32) { let mut g = Xorshift32 { state: seed }; (g.next(), g.next()) }";

  /// <summary>Scenario: a state struct with a mutating method is imported.</summary>
  [Test]
  public void StateStruct_WithAMutatingMethod() {
    Assert.That(Run(Xorshift, OverflowChecks.Wrap, "two", 2463534242u), Is.EqualTo("{723471715,2497366906}"));
    var next = Module(Xorshift, OverflowChecks.Wrap).Functions.Single(function => function.Name == "Xorshift32_next");
    // The exclusive receiver is copied in and back out with the result.
    Assert.That(next.Parameters[0].Type.StructType!.Name, Is.EqualTo("Xorshift32"));
    Assert.That(next.ReturnType.StructType!.Fields.Select(field => field.Name), Is.EqualTo(new[] { "result", "receiver" }));
  }

  private const string Calculator = "#[derive(Clone, Copy, PartialEq)] enum E { DivZero, Neg } fn div(a: i32, b: i32) -> Result<i32, E> { if b == 0 { Err(E::DivZero) } else if a < 0 { Err(E::Neg) } else { Ok(a / b) } } fn calc(a: i32, b: i32, c: i32) -> Result<i32, E> { let q = div(a, b)?; div(q, c) } pub fn code(a: i32, b: i32, c: i32) -> i32 { match calc(a, b, c) { Ok(v) => v, Err(E::DivZero) => -1, Err(E::Neg) => -2 } }";

  /// <summary>Scenario outline: Result values and the ? operator return errors as values.</summary>
  [TestCase(100, 5, 2, "10")]
  [TestCase(1, 0, 2, "-1")]
  [TestCase(-1, 1, 1, "-2")]
  [TestCase(10, 2, 0, "-1")]
  public void ResultAndQuestionMark_ReturnErrorsAsValues(int a, int b, int c, string expected)
    => Assert.That(Run(Calculator, "code", a, b, c), Is.EqualTo(expected));

  /// <summary>Scenario: enum discriminants and struct equality follow the compiler.</summary>
  [Test]
  public void DiscriminantsAndEquality_FollowTheCompiler() {
    const string Source = "#[repr(u8)] enum Op { Add = 1, Sub = 5, Mul } #[derive(Clone, Copy, PartialEq)] struct P { x: i32, y: i32 } pub fn d() -> u8 { Op::Mul as u8 } pub fn eq(x: i32, y: i32) -> bool { P { x, y } == P { y: -4, x: 3 } }";
    Assert.That(Run(Source, "d"), Is.EqualTo("6"));
    Assert.That(Run(Source, "eq", 3, -4), Is.EqualTo("true"));
    Assert.That(Run(Source, "eq", 3, 4), Is.EqualTo("false"));
  }

  /// <summary>Scenario: Option-returning accessors do not trap, unwrap does.</summary>
  [Test]
  public void OptionAccessors_DoNotTrap() {
    const string Source = "pub fn third(a: &[u8]) -> u8 { match a.get(2) { Some(&v) => v, None => 255 } } pub fn must(a: &[u8]) -> u8 { *a.get(2).unwrap() }";
    Assert.That(Run(Source, "third", new byte[] { 1, 2, 3 }), Is.EqualTo("3 | [1,2,3]"));
    Assert.That(Run(Source, "third", new byte[] { 1, 2 }), Is.EqualTo("255 | [1,2]"));
    Assert.That(Run(Source, "must", new byte[] { 1, 2 }), Is.EqualTo(RustFixture.Panic));
  }

  /// <summary>Scenario: while let and let-else over Option are imported.</summary>
  [Test]
  public void WhileLetAndLetElse_OverOption() {
    const string Source = "pub fn popsum(n: u32) -> u32 { let mut v = Vec::new(); for i in 1..=n { v.push(i); } let mut s = 0; while let Some(x) = v.pop() { s = s * 10 + x; } s } pub fn le(a: &[u32]) -> u32 { let Some(&v) = a.first() else { return 99 }; v + 1 }";
    Assert.That(Run(Source, "popsum", 3u), Is.EqualTo("321"));
    Assert.That(Run(Source, "le", new uint[] { 4 }), Is.EqualTo("5 | [4]"));
    Assert.That(Run(Source, "le", new uint[0]), Is.EqualTo("99 | []"));
  }

  /// <summary>Scenario: a trait implementation is rejected with a stable code and a pointer to the object model.</summary>
  [Test]
  public void TraitImplementation_IsRejected() {
    var result = Import("struct V(i32); impl std::ops::Add for V { type Output = V; fn add(self, o: V) -> V { V(self.0 + o.0) } }");
    Assert.That(result.Module, Is.Null);
    var diagnostic = result.Diagnostics.Single();
    Assert.That((diagnostic.Code, diagnostic.Line), Is.EqualTo((RustFrontend.UnsupportedTrait, 1)));
    Assert.That(diagnostic.Message, Does.Contain("#473"));
  }

  // ---------------------------------------------------------------- test design

  private const string Point = """
    #[derive(Clone, Copy, PartialEq, Eq, Default, Debug)]
    pub struct P { x: i32, y: i32 }
    impl P {
      pub const ORIGIN: P = P { x: 0, y: 0 };
      pub fn new(x: i32, y: i32) -> Self { Self { x, y } }
      pub fn manhattan(self) -> i32 { self.x.abs() + self.y.abs() }
      pub fn shift(&mut self, d: i32) { self.x += d; }
      pub fn sum(&self) -> i32 { self.x + self.y }
    }
    #[derive(Clone, Copy)] struct Pair(u8, u8);
    struct Unit;
    struct Nested { p: P, tag: u8 }
    #[repr(C)] struct Raw { a: u32, b: [u8; 2] }
    #[derive(Clone, Copy, PartialEq, Debug)] struct F { v: f64 }
    pub fn f(n: i32) -> i32 {
      match n {
        0 => P::new(3, -4).manhattan(),
        1 => { let mut p = P::new(3, 0); p.shift(2); p.x }
        2 => (P::default() == P::ORIGIN) as i32,
        3 => { let p = P { x: 1, y: 2 }; let mut q = p.clone(); q.x = 9; p.x * 10 + q.x }
        4 => { let p = P { x: 5, y: -4 }; let q = P { y: 7, ..p }; q.x * 10 + q.y }
        5 => { let t = Pair(3, 4); (t.0 * t.1) as i32 }
        6 => { let _u = Unit; let n = Nested { p: P::new(1, 2), tag: 3 }; n.p.sum() * 10 + n.tag as i32 }
        7 => { let mut r = Raw { a: 1, b: [2, 3] }; r.b[1] = 7; r.a as i32 + r.b[1] as i32 }
        8 => { let x = F { v: f64::NAN }; (x == x) as i32 }
        9 => { let P { x, y } = P::new(6, 7); x * y }
        10 => { let mut a = [P::new(1, 1), P::new(2, 2)]; a[1].shift(5); a[1].x }
        11 => { let mut n = Nested { p: P::new(1, 2), tag: 0 }; n.p.shift(10); n.p.x }
        _ => match P::new(n, 0) { P { x: 12, .. } => 120, P { x, y: 0 } => x, _ => -1 },
      }
    }
    """;

  /// <summary>Test design 3 to 6, 11: each struct kind, receiver kind, derive and the struct update syntax.</summary>
  [TestCase(0, "7")]
  [TestCase(1, "5")]
  [TestCase(2, "1")]
  [TestCase(3, "19")]
  [TestCase(4, "57")]
  [TestCase(5, "12")]
  [TestCase(6, "33")]
  [TestCase(7, "8")]
  [TestCase(8, "0")]
  [TestCase(9, "42")]
  [TestCase(10, "7")]
  [TestCase(11, "11")]
  [TestCase(12, "120")]
  [TestCase(13, "13")]
  public void StructForm_BehavesAsRust(int n, string expected) => Assert.That(Run(Point, "f", n), Is.EqualTo(expected));

  private const string Options = """
    #[derive(Clone, Copy, PartialEq, Debug)] pub enum E { A, B }
    fn half(x: u32) -> Option<u32> { if x % 2 == 0 { Some(x / 2) } else { None } }
    fn quarter(x: u32) -> Option<u32> { let h = half(x)?; half(h) }
    fn check(x: i32) -> Result<i32, E> { if x < 0 { Err(E::B) } else { Ok(x) } }
    pub fn f(n: u32) -> i64 {
      match n {
        0 => if let Some(v) = half(8) { v as i64 } else { -1 },
        1 => half(3).unwrap_or(7) as i64,
        2 => half(4).is_some() as i64 * 10 + half(3).is_none() as i64,
        3 => quarter(12).unwrap_or(0) as i64 * 10 + quarter(6).unwrap_or(9) as i64,
        4 => check(4).is_ok() as i64 * 10 + check(-1).is_err() as i64,
        5 => check(5).unwrap() as i64,
        6 => half(3).ok_or(E::A).is_err() as i64,
        7 => check(-3).ok().unwrap_or(-7) as i64,
        8 => half(1).expect("odd") as i64,
        9 => check(-1).unwrap() as i64,
        10 => 5u8.checked_add(250).map_or(-1, |v| v as i64),
        11 => match 200u8.checked_add(100) { Some(v) => v as i64, None => -2 },
        12 => { let c = 7u32.checked_sub(9); c.unwrap_or(1) as i64 }
        13 => { let v = vec![1u32, 2, 3]; v.last().copied().unwrap_or(0) as i64 * 10 + v.first().copied().unwrap_or(0) as i64 }
        14 => { let e: [u32; 0] = []; e.first().is_none() as i64 }
        15 => { let mut v: Vec<u32> = Vec::new(); v.pop().is_none() as i64 }
        16 => { let x = 3u32.overflowing_sub(5); x.0 as i64 * 10 + x.1 as i64 }
        _ => check(-1).expect("negative") as i64,
      }
    }
    """;

  /// <summary>Test design 7 to 9 and 12: each Option and Result consumer and source, present and absent, and their panics.</summary>
  [TestCase(0u, "4")]
  [TestCase(1u, "7")]
  [TestCase(2u, "11")]
  [TestCase(3u, "39")]
  [TestCase(4u, "11")]
  [TestCase(5u, "5")]
  [TestCase(6u, "1")]
  [TestCase(7u, "-7")]
  [TestCase(8u, RustFixture.Panic)]
  [TestCase(9u, RustFixture.Panic)]
  [TestCase(11u, "-2")]
  [TestCase(12u, "1")]
  [TestCase(13u, "31")]
  [TestCase(14u, "1")]
  [TestCase(15u, "1")]
  [TestCase(16u, "42949672941")]
  [TestCase(17u, RustFixture.Panic)]
  public void OptionAndResult_BehaveAsRust(uint n, string expected)
    => Assert.That(Run(Options.Replace("10 => 5u8.checked_add(250).map_or(-1, |v| v as i64),", ""), "f", n), Is.EqualTo(expected));

  /// <summary>Test design 10: discriminants implicit from 0, after a gap, at the limit of the representation, negative.</summary>
  [TestCase("enum E { A, B, C } pub fn f() -> i64 { E::C as i64 }", "2")]
  [TestCase("enum E { A = 1, B = 5, C } pub fn f() -> i64 { E::B as i64 * 10 + E::C as i64 }", "56")]
  [TestCase("#[repr(u8)] enum E { A = 254, B } pub fn f() -> i64 { E::B as i64 }", "255")]
  [TestCase("#[repr(i8)] enum E { A = -1, B } pub fn f() -> i64 { E::A as i64 * 10 + E::B as i64 }", "-10")]
  [TestCase("#[derive(PartialEq)] enum E { A, B } pub fn f() -> i64 { (E::A == E::B) as i64 * 10 + (E::B != E::A) as i64 }", "1")]
  [TestCase("#[derive(Default, Clone, Copy)] enum E { A, #[default] B } pub fn f() -> i64 { E::default() as i64 }", "1")]
  [TestCase("enum E { A, B, C } fn g(e: E) -> i64 { match e { E::A => 1, E::B | E::C => 2 } } pub fn f() -> i64 { g(E::C) }", "2")]
  public void Discriminant_IsTheCompilers(string source, string expected) => Assert.That(Run(source, "f"), Is.EqualTo(expected));

  /// <summary>Test design 13: the fields of a struct expression are evaluated in the order written.</summary>
  [Test]
  public void StructFields_AreEvaluatedInTheOrderWritten()
    => Assert.That(Run("struct P { x: u8, y: u8 } pub fn f(a: &[u8], z: u8) -> u8 { let p = P { y: 1 / z, x: a[9] }; p.x + p.y }", "f", new byte[3], (byte)0), Is.EqualTo(RustFixture.DivideByZero));

  /// <summary>Test design 14 to 17: traits, generics, lifetimes, data enums, attributes, Option methods, destructors and compiler errors.</summary>
  [TestCase("trait T {} pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedTrait)]
  [TestCase("struct P; impl std::fmt::Display for P { } pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedTrait)]
  [TestCase("pub fn f(t: &dyn Copy) -> u32 { 1 }", RustFrontend.UnsupportedTrait)]
  [TestCase("struct W<T>(T); pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedGeneric)]
  [TestCase("struct S; impl<const N: usize> S { } pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedGeneric)]
  [TestCase("struct R<'a> { d: &'a [u8] } pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedLifetime)]
  [TestCase("struct R { d: &'static [u8] } pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedLifetime)]
  [TestCase("enum Sh { C(f64), R { w: f64 } } pub fn f() -> u32 { 1 }", RustFrontend.DataEnum)]
  [TestCase("#[derive(Hash)] struct S; pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedDerive)]
  [TestCase("#[repr(packed)] struct S { a: u8 } pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedDerive)]
  [TestCase("pub fn f(o: Option<u32>) -> Option<u32> { o.map(|x| x + 1) }", RustFrontend.UnsupportedOptionMethod)]
  [TestCase("pub fn f(r: Result<u32, u8>) -> u32 { r.unwrap_or_else(|e| e as u32) }", RustFrontend.UnsupportedOptionMethod)]
  [TestCase("pub fn f(r: Result<u32, Vec<u8>>) -> u32 { 1 }", RustFrontend.UnsupportedOptionMethod)]
  [TestCase("struct S; impl Drop for S { fn drop(&mut self) {} } pub fn f() -> u32 { 1 }", RustFrontend.Destructor)]
  public void Construct_IsRefusedWithItsCode(string source, string code) => Assert.That(Codes(source), Does.Contain(code));

  [TestCase("fn g(x: u32) -> Option<u32> { Some(x) } pub fn f(x: u32) -> u32 { g(x)? }", "E0277")]
  [TestCase("struct S { a: u32 } fn take(s: S) -> u32 { s.a } pub fn f() -> u32 { let s = S { a: 1 }; take(s) + take(s) }", "E0382")]
  [TestCase("#[derive(Clone, Copy, PartialEq)] enum A { X } #[derive(Clone, Copy, PartialEq)] enum B { Y } fn g() -> Result<u32, A> { Ok(1) } fn h() -> Result<u32, B> { let v = g()?; Ok(v) } pub fn f() -> u32 { 1 }", "E0277")]
  [TestCase("struct S { a: u32 } pub fn f() -> u32 { let s = S { }; 1 }", "E0063")]
  [TestCase("struct S { a: u32 } pub fn f() -> bool { S { a: 1 } == S { a: 1 } }", "E0369")]
  [TestCase("enum E { A } pub fn f() -> u32 { match E::A { } }", "E0004")]
  [TestCase("pub fn f(o: Option<u32>) -> u32 { match o { Some(v) => v } }", "E0004")]
  [TestCase("struct P { x: u32 } impl P { fn get(&self) -> u32 { self.x } } pub fn f() -> u32 { P::get() }", "E0061")]
  public void CompilerError_IsRejected(string source, string text)
    => Assert.That(Import(source).Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(text));

}
