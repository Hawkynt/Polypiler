using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// The Rust frontend on functions, modules and the crate graph, closures, iterator closures, the
/// assertion and panic macros and <c>#[test]</c> functions: the scenarios of
/// <c>fe-rs-functions.feature</c> and the rows of the test design of #464, in the interpreter.
/// </summary>
[TestFixture]
public sealed class RustFunctionTests {

  private static ImportResult Import(string source, OverflowChecks mode = OverflowChecks.Panic, bool? debugAssertions = null)
    => RustFrontend.Import("snippet.rs", source, new RustImportOptions(mode) { DebugAssertions = debugAssertions });

  private static IrModule Module(string source, OverflowChecks mode = OverflowChecks.Panic, bool? debugAssertions = null) {
    var result = Import(source, mode, debugAssertions);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static string Run(string source, string function, params object[] arguments) => Run(source, OverflowChecks.Panic, function, arguments);

  private static string Run(string source, OverflowChecks mode, string function, params object[] arguments)
    => RustFixture.Interpreted(Module(source, mode), new PrototypeCall(function, arguments));

  private static string[] Codes(string source, bool? debugAssertions = null) {
    var result = Import(source, OverflowChecks.Panic, debugAssertions);
    Assert.That(result.Module, Is.Null);
    return [.. result.Diagnostics.Select(diagnostic => diagnostic.Code)];
  }

  // ---------------------------------------------------------------- fe-rs-functions.feature

  /// <summary>Scenario outline: imported functions and iterator chains compute what rustc computes.</summary>
  [TestCase("pub fn f(n: u32) -> u64 { if n < 2 { n as u64 } else { f(n - 1) + f(n - 2) } }", 10u, "55")]
  [TestCase("pub fn f(x: u32) -> u32 { fn dbl(x: u32) -> u32 { x * 2 } let g: fn(u32) -> u32 = dbl; g(g(x)) }", 5u, "20")]
  [TestCase("pub fn f(n: u32) -> u32 { (1..=n).map(|x| x * x).sum() }", 5u, "55")]
  [TestCase("pub fn f(n: u32) -> u32 { (1..=n).product() }", 5u, "120")]
  public void Function_ComputesWhatRustcComputes(string source, uint argument, string expected) => Assert.That(Run(source, "f", argument), Is.EqualTo(expected));

  [TestCase("pub fn f(a: &[u32]) -> u32 { a.iter().filter(|&&x| x % 2 == 0).map(|&x| x * x).sum() }", "56")]
  [TestCase("pub fn f(a: &[u32]) -> u32 { a.iter().fold(0u32, |h, &x| h.wrapping_mul(31).wrapping_add(x)) }", "31810")]
  public void IteratorChain_ComputesWhatRustcComputes(string source, string expected)
    => Assert.That(Run(source, "f", source.Contains("fold") ? new uint[] { 1, 2, 3, 4 } : new uint[] { 1, 2, 3, 4, 5, 6 }).Split(" |")[0], Is.EqualTo(expected));

  /// <summary>Scenario: a closure that mutates a captured variable updates it.</summary>
  [Test]
  public void MutatingClosure_UpdatesTheCapturedVariable()
    => Assert.That(Run("pub fn total() -> u32 { let mut c = 0u32; let mut inc = |d: u32| { c += d; }; inc(2); inc(5); c }", "total"), Is.EqualTo("7"));

  /// <summary>Scenario: adapters run element by element and any stops early.</summary>
  [Test]
  public void Adapters_RunElementByElement() {
    const string Source = "pub fn probe(a: &[u32], z: u32) -> bool { a.iter().map(|&x| x / z).any(|q| q > 1) }";
    Assert.That(Run(Source, "probe", new uint[] { 4, 0, 9 }, 2u), Is.EqualTo("true | [4,0,9]"));
    // Test design 13: a trap inside a closure in a lazy chain happens at the first element.
    Assert.That(Run(Source, "probe", new uint[] { 4, 0, 9 }, 0u), Is.EqualTo(RustFixture.DivideByZero));
    // The divisions and tests interleave: a counter sees one division before any stops.
    Assert.That(Run("pub fn f(a: &[u32]) -> u32 { let mut n = 0u32; let found = a.iter().map(|&x| { n += 1; x / 2 }).any(|q| q > 1); n * 10 + found as u32 }", "f", new uint[] { 4, 0, 9 }).Split(" |")[0], Is.EqualTo("11"));
  }

  /// <summary>Scenario: modules are imported in a deterministic order with the crate's export set; the source's tests are imported.</summary>
  [Test]
  public void ModuleGraph_IsOrderedAndExported() {
    var files = new Dictionary<string, string> {
      ["modgraph/src/lib.rs"] = "pub mod hash;\nmod util;\npub use util::bits::rotl;\n#[cfg(test)]\nmod tests {\n  use super::*;\n  #[test]\n  fn fnv1a_of_a() { assert_eq!(hash::fnv1a(b\"a\"), 0xE40C292C); }\n  #[test]\n  fn rotl_wraps() { assert!(rotl(0x80000000, 1) == 1); }\n  #[test]\n  fn wrong() { assert_eq!(hash::fnv1a(b\"a\"), 0); }\n}\n",
      ["modgraph/src/hash.rs"] = "use crate::util::bits;\npub fn fnv1a(data: &[u8]) -> u32 {\n  let mut h = 0x811C9DC5u32;\n  for &b in data { h = bits::mix(h, b); }\n  h\n}\n",
      ["modgraph/src/util/mod.rs"] = "pub mod bits;\n",
      ["modgraph/src/util/bits.rs"] = "pub fn rotl(x: u32, n: u32) -> u32 { x.rotate_left(n) }\npub(crate) fn mix(h: u32, b: u8) -> u32 { (h ^ b as u32).wrapping_mul(16777619) }\n"
    };
    ImportResult Package() => RustFrontend.Import("modgraph/src/lib.rs", files["modgraph/src/lib.rs"], new RustImportOptions(OverflowChecks.Panic) {
      ReadFile = path => files.GetValueOrDefault(path.Replace('\\', '/'))
    });

    var first = Package();
    var second = Package();
    Assert.That(first.Diagnostics, Is.Empty);
    Assert.That(first.ModuleOrder, Is.EqualTo(new[] { "crate::util::bits", "crate::hash", "crate::util", "crate", "crate::tests" }));
    Assert.That(second.ModuleOrder, Is.EqualTo(first.ModuleOrder));
    Assert.That(first.Module!.Functions.Select(function => function.Name), Is.EqualTo(second.Module!.Functions.Select(function => function.Name)));
    Assert.That(first.Module.Functions.Where(function => function.IsPublic).Select(function => function.Name), Is.EquivalentTo(new[] { "hash_fnv1a", "rotl" }));
    Assert.That(IrVerifier.Verify(first.Module), Is.Empty);
    Assert.That(IrInterpreter.Call(first.Module, "hash_fnv1a", ["a"u8.ToArray()]), Is.EqualTo(0xE40C292Cu));
    // The tests pass and fail as cargo test reports them.
    Assert.That(first.Tests, Is.EqualTo(new[] { "tests_fnv1a_of_a", "tests_rotl_wraps", "tests_wrong" }));
    // The module of each function outside the root, for a backend that writes modules (#459).
    Assert.That(first.Modules, Is.EquivalentTo(new Dictionary<string, string> {
      ["rotl"] = "util::bits", ["util_bits_mix"] = "util::bits", ["hash_fnv1a"] = "hash",
      ["tests_fnv1a_of_a"] = "tests", ["tests_rotl_wraps"] = "tests", ["tests_wrong"] = "tests"
    }));
    Assert.That(first.Tests.Select(test => RustFixture.Interpreted(first.Module, new PrototypeCall(test, []))), Is.EqualTo(new[] { "void", "void", RustFixture.Panic }));
  }

  /// <summary>Scenario: debug assertions need an explicit profile key.</summary>
  [Test]
  public void DebugAssertions_NeedAnExplicitProfileKey() {
    const string Source = "pub fn f(x: u32) -> u32 { debug_assert!(x > 0); x - 1 }";
    Assert.That(Codes(Source), Does.Contain(RustFrontend.MissingDebugAssertions));
    Assert.That(RustFixture.Interpreted(Module(Source, OverflowChecks.Wrap, false), new PrototypeCall("f", [0u])), Is.EqualTo("4294967295"));
    Assert.That(RustFixture.Interpreted(Module(Source, OverflowChecks.Wrap, true), new PrototypeCall("f", [0u])), Is.EqualTo(RustFixture.Panic));
  }

  /// <summary>Scenario: a function that returns a closure is rejected with a stable code.</summary>
  [Test]
  public void ReturnedClosure_IsRejected() {
    var result = Import("fn adder(k: u32) -> impl Fn(u32) -> u32 { move |x| x + k }");
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Line)), Does.Contain((RustFrontend.UnsupportedFunction, 1)));
  }

  // ---------------------------------------------------------------- test design

  /// <summary>Test design 3: each callable class.</summary>
  [TestCase("fn is_even(n: u32) -> bool { if n == 0 { true } else { is_odd(n - 1) } } fn is_odd(n: u32) -> bool { if n == 0 { false } else { is_even(n - 1) } } pub fn f() -> u32 { is_even(7) as u32 }", "0")]
  [TestCase("pub fn f() -> u32 { let k = 3u32; let add = move |x: u32| x + k; add(4) }", "7")]
  [TestCase("pub fn f() -> u32 { let mut k = 3u32; let add = move |x: u32| x + k; k = 10; add(4) + k }", "17")]
  [TestCase("pub fn f() -> u32 { let k = 3u32; let add = |x: u32| x + k; add(add(1)) }", "7")]
  [TestCase("pub fn f() -> u32 { let early = |x: u32| -> u32 { if x > 2 { return 1; } 0 }; early(3) * 10 + early(1) }", "10")]
  [TestCase("const fn sq(x: u32) -> u32 { x * x } pub fn f() -> u32 { sq(9) }", "81")]
  [TestCase("fn pair((a, b): (u32, u32), mut c: u32) -> u32 { c += 1; a * b + c } pub fn f() -> u32 { pair((3, 4), 1) }", "14")]
  [TestCase("fn apply(g: fn(u32) -> u32, x: u32) -> u32 { g(x) } fn inc(x: u32) -> u32 { x + 1 } pub fn f() -> u32 { apply(inc, 1) }", "2")]
  public void Callable_BehavesAsRust(string source, string expected) => Assert.That(Run(source, "f"), Is.EqualTo(expected));

  /// <summary>Test design 4, 5 and 9: each iterator method and source, and empty iterations.</summary>
  [TestCase("a.iter().any(|&x| x > 3) as u32", "1")]
  [TestCase("a.iter().all(|&x| x > 1) as u32", "0")]
  [TestCase("a.iter().filter(|x| **x % 2 == 0).count() as u32", "2")]
  [TestCase("let mut s = 0; a.iter().for_each(|x| s += x); s", "10")]
  [TestCase("a.iter().copied().map(|x| x * 10).sum()", "100")]
  [TestCase("a.iter().zip(a[1..].iter()).map(|(x, y)| x * y).sum()", "20")]
  [TestCase("a.iter().enumerate().map(|(i, x)| i as u32 * x).sum()", "20")]
  [TestCase("a.iter().map(|x| x * 2).filter(|x| *x > 4).enumerate().map(|(i, x)| i as u32 + x).sum()", "15")]
  [TestCase("(0..4u32).rev().map(|x| x * x).sum()", "14")]
  [TestCase("(0..10u32).step_by(3).filter(|x| x % 2 == 1).product()", "27")]
  [TestCase("e.iter().sum::<u32>()", "0")]
  [TestCase("e.iter().product::<u32>()", "1")]
  [TestCase("e.iter().count() as u32", "0")]
  [TestCase("e.iter().any(|_| true) as u32", "0")]
  [TestCase("e.iter().all(|_| false) as u32", "1")]
  [TestCase("e.iter().fold(7u32, |a, b| a + b)", "7")]
  [TestCase("let v = vec![1u32, 2]; v.iter().map(|x| x + 1).sum()", "5")]
  [TestCase("a.iter().map(double).sum()", "20")]
  public void IteratorMethod_BehavesAsRust(string expression, string expected)
    => Assert.That(Run($"fn double(x: &u32) -> u32 {{ *x * 2 }} pub fn f() -> u32 {{ let a = [1u32, 2, 3, 4]; let e: [u32; 0] = []; {expression} }}", "f"), Is.EqualTo(expected));

  /// <summary>Test design 6: each macro's failing condition.</summary>
  [TestCase("assert!(x > 5);")]
  [TestCase("assert!(x > 5, \"too small\");")]
  [TestCase("assert_eq!(x, 5);")]
  [TestCase("assert_ne!(x, 1);")]
  [TestCase("if x == 1 { panic!(\"one\"); }")]
  [TestCase("if x == 1 { unreachable!(); }")]
  [TestCase("if x == 1 { todo!(); }")]
  [TestCase("if x == 1 { unimplemented!(); }")]
  public void Macro_PanicsWhenItFails(string statement) {
    var source = $"pub fn f(x: u32) -> u32 {{ {statement} x }}";
    Assert.That(Run(source, "f", 1u), Is.EqualTo(RustFixture.Panic));
  }

  [Test]
  public void Assertion_Passes() {
    Assert.That(Run("pub fn f(x: u32) -> u32 { assert!(x > 0); assert_eq!(x, 1); assert_ne!(x, 2); x }", "f", 1u), Is.EqualTo("1"));
    Assert.That(Run("#[derive(PartialEq, Debug)] struct P { a: u8 } pub fn f() -> u32 { assert_eq!(P { a: 1 }, P { a: 1 }); 1 }", "f"), Is.EqualTo("1"));
  }

  /// <summary>Test design 10 and 11: recursion depth and sum at the type limit by mode.</summary>
  [TestCase(0u, "0")]
  [TestCase(1u, "1")]
  [TestCase(20u, "6765")]
  public void Recursion_AtItsDepths(uint n, string expected)
    => Assert.That(Run("pub fn fib(n: u32) -> u64 { if n < 2 { n as u64 } else { fib(n - 1) + fib(n - 2) } }", "fib", n), Is.EqualTo(expected));

  /// <summary>Test design 10: a deep recursion, 1.6 million calls in the interpreter.</summary>
  [Test, Category("Slow")]
  public void DeepRecursion_HasItsValue()
    => Assert.That(Run("pub fn fib(n: u32) -> u64 { if n < 2 { n as u64 } else { fib(n - 1) + fib(n - 2) } }", "fib", 30u), Is.EqualTo("832040"));

  [TestCase(OverflowChecks.Panic, RustFixture.Overflow)]
  [TestCase(OverflowChecks.Wrap, "44")]
  public void Sum_AtTheTypeLimit(OverflowChecks mode, string expected)
    => Assert.That(Run("pub fn f() -> u8 { [200u8, 100].iter().sum::<u8>() }", mode, "f"), Is.EqualTo(expected));

  /// <summary>Test design 12: a diamond of modules is ordered by its uses, then by path.</summary>
  [Test]
  public void Diamond_IsOrderedByPath() {
    const string Source = "mod c { pub fn v() -> u32 { 1 } } mod a { pub fn v() -> u32 { super::c::v() + 1 } } mod b { pub fn v() -> u32 { crate::c::v() + 2 } } pub fn f() -> u32 { a::v() + b::v() }";
    var result = Import(Source);
    Assert.That(result.ModuleOrder, Is.EqualTo(new[] { "crate::c", "crate::a", "crate::b", "crate" }));
    Assert.That(IrInterpreter.Call(result.Module!, "f", []), Is.EqualTo(5u));
  }

  /// <summary>Items and names: constants, statics, inline modules, use with renames, globs, nested groups and re-exports.</summary>
  [Test]
  public void Names_ResolveAcrossModules() {
    const string Source = """
      const K: u32 = 2;
      static S: u32 = 5;
      mod m {
        pub const SCALE: u32 = 10;
        pub mod inner { pub fn triple(x: u32) -> u32 { x * 3 } pub fn half(x: u32) -> u32 { x / 2 } }
        pub use self::inner::half;
      }
      use m::inner::{triple as thrice, self};
      use m::*;
      use std::cmp::{min, max};
      pub fn f() -> u32 { thrice(K) + inner::half(S * 2) + half(SCALE) + min(1, 2) + max(1, 2) + S }
      """;
    Assert.That(Run(Source, "f"), Is.EqualTo("24"));
  }

  /// <summary>Test design 14 to 18: generic and trait-based functions, macros, escaping closures, attributes, foreign paths, other iterator methods, compiler errors.</summary>
  [TestCase("pub fn id<T>(x: T) -> T { x } pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedGeneric)]
  [TestCase("fn g(x: impl Into<u32>) -> u32 { 1 } pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedFunction)]
  [TestCase("unsafe fn g() {} pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedFunction)]
  [TestCase("extern \"C\" fn g() {} pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedFunction)]
  [TestCase("pub fn f() -> u32 { println!(\"x\"); 1 }", RustFrontend.UnsupportedMacro)]
  [TestCase("pub fn f() -> u32 { println!(\"{}\", 1); 1 }", RustFrontend.UnsupportedMacro)]
  [TestCase("pub fn f(v: u32) -> u32 { assert!(v > 0, \"v = {}\", v); 1 }", RustFrontend.UnsupportedMacro)]
  [TestCase("pub fn f(v: u32) -> u32 { assert!(v > 0, \"v = {v}\"); 1 }", RustFrontend.UnsupportedMacro)]
  [TestCase("macro_rules! m { () => {} } pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedMacro)]
  [TestCase("pub fn f() -> u32 { m!(); 1 }", RustFrontend.UnsupportedMacro)]
  [TestCase("pub fn f() -> u32 { let g = |x: u32| x; let h = g; h(1) }", RustFrontend.EscapingClosure)]
  [TestCase("pub fn f() -> u32 { let b: Box<dyn Fn(u32) -> u32> = Box::new(|x| x); 1 }", RustFrontend.SmartPointer)]
  [TestCase("pub fn f() -> usize { let v = vec![1u8]; let c = move || v.len(); c() }", RustFrontend.EscapingClosure)]
  [TestCase("#[cfg(target_os = \"linux\")] pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedAttribute)]
  [TestCase("#[should_panic] #[test] fn t() {} pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedAttribute)]
  [TestCase("#[ignore] #[test] fn t() {} pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedAttribute)]
  [TestCase("#[no_mangle] pub fn f() -> u32 { 1 }", RustFrontend.UnsupportedAttribute)]
  [TestCase("pub fn f() -> u32 { std::fs::read(1); 1 }", RustFrontend.ForeignPath)]
  [TestCase("use std::collections::VecDeque; pub fn f() -> u32 { 1 }", RustFrontend.ForeignPath)]
  [TestCase("pub fn f(a: &[u32]) -> u32 { *a.iter().max().unwrap() }", RustFrontend.UnsupportedIteratorMethod)]
  [TestCase("pub fn f(a: &[u32]) -> usize { a.iter().peekable().count() }", RustFrontend.UnsupportedIteratorMethod)]
  [TestCase("pub fn f() -> u32 { (0..3u32).map(|x| x + 1).enumerate().rev().count() as u32 }", RustFrontend.UnsupportedIteratorMethod)]
  public void Construct_IsRefusedWithItsCode(string source, string code) => Assert.That(Codes(source), Does.Contain(code));

  [TestCase("mod m { fn hidden() -> u32 { 1 } } pub fn f() -> u32 { m::hidden() }", "E0603")]
  [TestCase("pub fn f() -> u32 { missing(1) }", "E0425")]
  [TestCase("pub fn f() -> u32 { g(1, 2) } fn g(x: u32) -> u32 { x }", "E0061")]
  [TestCase("mod m; pub fn f() -> u32 { 1 }", "E0583")]
  [TestCase("pub fn f() -> u32 { let v = vec![1u32]; let w = v; v.len() as u32 }", "E0382")]
  [TestCase("pub fn f() -> u32 { let v = vec![1u32]; for _ in 0..2 { let w = v; } 1 }", "E0382")]
  public void CompilerError_IsRejected(string source, string text)
    => Assert.That(Import(source).Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(text));

  /// <summary>A value moved in one branch and assigned again is usable; a move in a loop that breaks right after is no error.</summary>
  [TestCase("pub fn f(c: bool) -> u32 { let mut v = vec![1u32]; if c { let w = v; v = w; } v.len() as u32 }")]
  [TestCase("pub fn f() -> u32 { let v = vec![1u32]; loop { let w = v; break; } 1 }")]
  [TestCase("pub fn f(c: bool) -> u32 { let v = vec![1u32]; if c { let w = v; return 0; } v.len() as u32 }")]
  public void ValidMove_IsAccepted(string source) => Assert.That(Import(source).Diagnostics, Is.Empty);

}
