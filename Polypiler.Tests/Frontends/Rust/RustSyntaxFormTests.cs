using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// The syntax and the names of the phase-two subset at their edges: item forms, attributes,
/// receivers, type forms, paths with a leading <c>::</c>, <c>use</c> trees, file modules and
/// assignments, each refused with its code or imported with rustc's value.
/// </summary>
[TestFixture]
public sealed class RustSyntaxFormTests {

  private static ImportResult Import(string source, IReadOnlyDictionary<string, string>? files = null)
    => RustFrontend.Import("lib.rs", source, new RustImportOptions(OverflowChecks.Panic) { ReadFile = files is null ? null : path => files.GetValueOrDefault(path.Replace('\\', '/')) });

  private static void Refused(string source, string expected, IReadOnlyDictionary<string, string>? files = null) {
    var result = Import(source, files);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message), Has.Some.Contains(expected));
  }

  private static string Run(string source, IReadOnlyDictionary<string, string>? files = null) {
    var result = Import(source, files);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return RustFixture.Interpreted(result.Module!, new PrototypeCall("f", []));
  }

  [TestCase("extern \"C\" { fn g(); } pub fn f() -> u8 { 1 }", "extern block")]
  [TestCase("struct S; impl S { fn g(&'a self) {} } pub fn f() -> u8 { 1 }", "lifetime of self")]
  [TestCase("struct S; impl S { fn g(self: Self) {} } pub fn f() -> u8 { 1 }", "typed self")]
  [TestCase("fn g(...) {} pub fn f() -> u8 { 1 }", "variadic fn")]
  [TestCase("fn g(); pub fn f() -> u8 { 1 }", "fn without a body")]
  [TestCase("struct S<T> where T: Copy { a: T } pub fn f() -> f32 { let s = S { a: vec![1u8] }; 1.0 }", "E0277: the trait bound `Vec<u8>: Copy` is not satisfied")]
  [TestCase("struct S where u8: Copy { a: u8 } pub fn f() -> u8 { 1 }", "where clause")]
  [TestCase("enum E<T> { A } pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedGeneric)]
  [TestCase("struct S; impl S where S: Copy { } pub fn f() -> u8 { 1 }", "where clause")]
  [TestCase("struct S; impl S { type T = u8; } pub fn f() -> u8 { 1 }", "E0658: inherent associated types are unstable")]
  [TestCase("struct S; impl S { struct T; } pub fn f() -> u8 { 1 }", "item in an impl block")]
  [TestCase("mod m { #![allow(dead_code)] #![no_std] pub fn g() {} } pub fn f() -> u8 { 1 }", "no_std")]
  [TestCase("#[cfg(all(test, unix))] fn g() {} pub fn f() -> u8 { 1 }", "cfg(all(test,unix))")]
  [TestCase("#[derive(Clone)] fn g() {} pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedDerive)]
  [TestCase("#[inline] const X: u8 = 1; pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedAttribute)]
  [TestCase("#[inline] static X: u8 = 1; pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedAttribute)]
  [TestCase("#[inline] mod m {} pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedAttribute)]
  [TestCase("#[inline] mod m; pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedAttribute)]
  [TestCase("#[inline] use std::cmp::min; pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedAttribute)]
  [TestCase("#[derive(Hash)] enum E { A } pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedDerive)]
  [TestCase("enum E { #[inline] A } pub fn f() -> u8 { 1 }", RustFrontend.UnsupportedAttribute)]
  [TestCase("pub fn f(p: *mut u8) -> u8 { 1 }", RustFrontend.UnsafeConstruct)]
  [TestCase("pub fn f() -> u8 { let x = 1u8; let p = &raw const x; 1 }", RustFrontend.UnsafeConstruct)]
  [TestCase("pub fn f(x: impl Copy + 'static) -> u8 { 1 }", RustFrontend.UnsupportedFunction)]
  [TestCase("pub fn f(x: ::std::primitive::u8) -> u8 { 1 }", RustFrontend.ForeignPath)]
  [TestCase("pub fn f() -> u8 { trait T {} 1 }", "nested item")]
  [TestCase("pub fn f() -> u8 { struct T; 1 }", "nested item")]
  [TestCase("use crate::missing; pub fn f() -> u8 { 1 }", "E0432")]
  [TestCase("use other::thing; pub fn f() -> u8 { 1 }", RustFrontend.ForeignPath)]
  [TestCase("use crate::m::*; fn m() {} pub fn f() -> u8 { 1 }", "E0432")]
  [TestCase("use std::fmt::*; pub fn f() -> u8 { 1 }", RustFrontend.ForeignPath)]
  [TestCase("pub fn f() -> u8 { let x = 1; x = 2; 1 }", "E0384")]
  [TestCase("const C: u8 = 1; pub fn f() -> u8 { C = 2; 1 }", "E0070")]
  [TestCase("fn g() {} pub fn f() -> u8 { g = 2; 1 }", "E0070")]
  [TestCase("pub fn f() -> u8 { 1 = 2; 1 }", "E0070")]
  [TestCase("pub fn f() -> u8 { \"a\" = 2; 1 }", "E0070")]
  [TestCase("pub fn f() -> u8 { let mut k = 1u8; let g = move || { k = 2; }; 1 }", RustFrontend.EscapingClosure)]
  [TestCase("pub fn f(x: &u8) -> u8 { *x = 2; 1 }", "E0594")]
  [TestCase("pub fn f() -> u8 { let x = Some(1u8); let Some(y) = x else { 0 }; y }", "does not diverge")]
  [TestCase("pub fn f() -> u8 { let Some(y) else { return 0 }; y }", RustFrontend.InvalidSource)]
  [TestCase("pub fn f() -> u8 { let x: u8 = 1u7; x }", "invalid suffix")]
  [TestCase("struct S; pub fn f() -> bool { Some(S) == None }", "E0369")]
  [TestCase("pub fn f() -> u8 { let g = |x: u8| x + 1; let h: fn(u8) -> u8 = g; h(1) }", RustFrontend.EscapingClosure)]
  [TestCase("pub fn f() -> u8 { let k = 1u8; let h: fn(u8) -> u8 = |x| x + k; h(1) }", "closures can only be coerced")]
  [TestCase("pub fn f(a: &mut [u8], b: &mut [u8]) { let c: &[u8] = b; }", "")]
  public void Form_IsRefused(string source, string expected) {
    if (expected.Length == 0) {
      Assert.That(Import(source).Diagnostics, Is.Empty);
      return;
    }

    Refused(source, expected);
  }

  [TestCase("struct S; impl S { fn g(self,) -> u8 { 3 } } pub fn f() -> u8 { S.g() }", "3")]
  [TestCase("pub fn f() -> u8 { const _: u8 = 1; 4 }", "4")]
  [TestCase("use std::cmp::min as _; pub fn f() -> u8 { 5 }", "5")]
  [TestCase("pub fn f() -> u8 { let x: (u8,) = (6,); x.0 }", "6")]
  [TestCase("fn g(h: fn(x: u32) -> u32, k: fn(u32, u32) -> u32) -> u32 { k(h(1), 2) } fn inc(x: u32) -> u32 { x + 1 } fn add(a: u32, b: u32) -> u32 { a + b } pub fn f() -> u32 { g(inc, add) }", "4")]
  [TestCase("pub fn f() -> u8 { #[allow(unused)] let x = 7; x }", "7")]
  [TestCase("pub fn f() -> u8 { static S: u8 = 8; S }", "8")]
  [TestCase("pub fn f() -> u8 { let t = ((1u8, 9u8), 3u8); t.0.1 }", "9")]
  [TestCase("pub fn f() -> u32 { ::std::cmp::min(10, 12) }", "10")]
  [TestCase("pub(in crate) fn g() -> u8 { 11 } pub fn f() -> u8 { g() }", "11")]
  [TestCase("pub fn f() -> u8 { let x = Some(5u8); if let Some(v) = x { v + 7 } else { 0 } }", "12")]
  [TestCase("pub fn f() -> u8 { let a = [1u8, 2]; let r: Result<[u8; 2], u8> = Ok(a); 13 }", RustFrontend.UnsupportedOptionMethod)]
  [TestCase("pub fn f() -> u8 { let mut x = 0u8; let r = &mut x; *r = 14; x }", "14")]
  [TestCase("pub fn f() -> u8 { let x = (1u8, 2u8); let y = x == (1, 2); (y as u8) * 15 }", "15")]
  [TestCase("pub fn f() -> u8 { let r: Result<u8, u8> = Err(3); let s: Result<u8, u8> = Err(3); (r == s) as u8 * 16 }", "16")]
  [TestCase("pub fn f() -> u8 { let mut v = [1u8, 2]; let w: &mut [u8] = &mut v; let r: &[u8] = w; r[1] + 15 }", "17")]
  [TestCase("pub fn f() -> u8 { let x = 3u8; let r: &u8 = &x; let s: &u8 = r; *s * 6 }", "18")]
  [TestCase("pub fn f() -> u8 { let mut x = 1u8; { let r: &mut u8 = &mut x; let s: &u8 = r; } 19 }", "19")]
  [TestCase("pub fn f() -> u8 { let g: fn(u8) -> u8 = |x| x + 1; g(19) }", RustFrontend.EscapingClosure)]
  [TestCase("pub fn f() -> u8 { let x = (1u8, 2u8) == (1u8, 2u8); let r: Result<u8, u8> = Ok(1); (r == Ok(1)) as u8 * 21 }", "21")]
  public void Form_Imports(string source, string expected) {
    if (expected.StartsWith("PLY", System.StringComparison.Ordinal)) {
      Assert.That(Import(source).Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain(expected));
      return;
    }

    Assert.That(Run(source), Is.EqualTo(expected));
  }

  /// <summary>Text that is no Rust in the new forms is a syntax error.</summary>
  [TestCase("mod m { pub fn g() {}")]
  [TestCase("trait T { fn g(")]
  [TestCase("struct S<'a")]
  [TestCase("pub fn f() -> u8 { |x")]
  [TestCase("struct S; impl S { fn g() {}")]
  [TestCase("pub fn f(g: unsafe")]
  public void Text_IsASyntaxError(string source) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.All.EqualTo(RustFrontend.InvalidSource));
  }

  /// <summary>File modules: a file beside the root, a folder module, a file that is no Rust, a missing file, nested modules of a non-root file.</summary>
  [Test]
  public void FileModules_AreFoundBesideTheirDeclarer() {
    var files = new Dictionary<string, string> {
      ["a.rs"] = "pub mod b; pub fn one() -> u8 { b::two() - 1 }",
      ["a/b.rs"] = "pub fn two() -> u8 { 2 }",
      ["c/mod.rs"] = "pub fn three() -> u8 { 3 }",
      ["bad.rs"] = "pub fn ( {"
    };
    Assert.That(Run("mod a; mod c; pub fn f() -> u8 { a::one() + c::three() }", files), Is.EqualTo("4"));
    Refused("mod bad; pub fn f() -> u8 { 1 }", "in bad.rs at 1:8", files);
    Refused("mod gone; pub fn f() -> u8 { 1 }", "E0583", files);
  }

  /// <summary>A refused signature leaves the body unchecked, whose own refusals are still reported, every node kind searched.</summary>
  [Test]
  public void RefusalsInAnUncheckedBody_AreFoundInEveryForm() {
    var result = Import("""
      pub fn f(a: &mut str) -> u32 {
        fn g() { unsafe {}; }
        let S { x: b"b", .. } = s;
        let &y = unsafe { 0 };
        let z = a[unsafe { 0 }];
        let w = [unsafe { 0 }; unsafe { 0 }];
        let v = S { x: unsafe { 0 }, ..unsafe { 0 } };
        let u = unsafe { 0 }..unsafe { 0 };
        if let b"k" = unsafe { 0 } { unsafe { 0 } } else { unsafe { 0 } };
        while let b"o" = unsafe { 0 } { }
        let t = unsafe { 0 }?;
        trait T {}
        1
      }
      """);
    Assert.That(result.Diagnostics.Count(diagnostic => diagnostic.Code == RustFrontend.TextType), Is.EqualTo(1));
    Assert.That(result.Diagnostics.Count(diagnostic => diagnostic.Code == RustFrontend.UnsafeConstruct), Is.EqualTo(14));
    Assert.That(result.Diagnostics.Count(diagnostic => diagnostic.Code == RustFrontend.UnsupportedPattern), Is.EqualTo(3));
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain(RustFrontend.UnsupportedFunction));
  }

}
