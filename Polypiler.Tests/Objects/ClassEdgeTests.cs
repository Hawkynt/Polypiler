using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The edges of the construct tables of both frontends and of the lowering tables of both backends:
/// the refusals one by one, and the forms a fixture does not reach.
/// </summary>
[TestFixture]
public sealed class ClassEdgeTests {

  private static Diagnostic CSharpRefusal(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", "#nullable disable\n" + source);
    Assert.That(result.Module, Is.Null);
    return result.Diagnostics[0];
  }

  private static IrModule CSharp(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", "#nullable disable\n" + source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static Diagnostic JavaScriptRefusal(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Module, Is.Null);
    return result.Diagnostics[0];
  }

  private static IrModule JavaScript(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>Test design 10: the C# refusals the construct table names, one each.</summary>
  [TestCase("public sealed class D { } public static class C { public struct D { public int X; } public static int F() => 0; }", "a second type named D")]
  [TestCase("[System.Serializable] public sealed class D { } public static class C { public static int F() => 0; }", "the attribute System.Serializable")]
  [TestCase("public sealed record D(int A = 1); public static class C { public static int F() => 0; }", "a positional parameter with a modifier, an attribute or a default value")]
  [TestCase("public sealed class D { public volatile int V; } public static class C { public static int F() => 0; }", "a volatile field")]
  [TestCase("public sealed class D { public int P { get; set { field = value; } } } public static class C { public static int F() => 0; }", "a property with an automatic and a written accessor")]
  [TestCase("public sealed class D { public int P { get => 1; init { } } } public static class C { public static int F() => 0; }", "an init accessor that is not automatic")]
  [TestCase("public sealed class D { public D(int a) { } public D() : this(a: 1) { } } public static class C { public static int F() => 0; }", "a named argument")]
  [TestCase("public sealed class D { public int M(int x) => x; } public static class C { public static int F() => new D().M(x: 1); }", "a named or ref argument")]
  [TestCase("public sealed class D { public D(int a) { } } public static class C { public static int F() { new D(a: 1); return 0; } }", "a named argument")]
  [TestCase("public sealed class D { public int V; } public static class C { static int Use(D d) => d.V; public static int F() => Use(new D { V = 1 }); }", "an object initializer inside an expression")]
  [TestCase("public sealed class D { } public sealed class E { } public static class C { public static bool F() => ReferenceEquals(new D(), new E()); }", "a comparison of D with E")]
  [TestCase("public sealed record R(int A); public static class C { public static int F() => new R(1).GetHashCode(); }", "R.GetHashCode")]
  [TestCase("public sealed class D { public event System.Action A; } public static class C { public static int F() => 0; }", "an event")]
  public void CSharpConstruct_IsRefusedByName(string source, string what)
    => Assert.That(CSharpRefusal(source).Message, Is.EqualTo($"'{what}' is not supported yet"));

  /// <summary>An object initializer through a setter, a compound assignment of a computed property, null on the left and internal members.</summary>
  [Test]
  public void CSharpForms_ComputeWhatDotNetComputes() {
    var module = CSharp("""
      internal sealed class D {
        private int _v;
        internal int Twice { get => _v * 2; set => _v = value / 2; }
        public bool Same(D other) => null == other ? false : ReferenceEquals(this, other);
      }
      public sealed record R(int A) { public int B; }
      public static class C {
        public static int F() {
          var d = new D { Twice = 10 };
          d.Twice += 4;
          var r = new R(1) { B = 2 };
          var s = r;
          return d.Twice + (d.Same(d) ? 100 : 0) + (r == s ? 1000 : 0) + (r == new R(1) ? 10000 : 0);
        }
      }
      """);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(14 + 100 + 1000));
    foreach (var profile in new[] { CSharpProfile.Parse("net2.0"), CSharpProfile.Default }) {
      var text = LegalizedCSharp.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(text, Does.Contain("internal sealed class D").And.Contain("null == other"));
      Assert.That(GeneratedCSharp.Compile(text, false).Method("F").Invoke(null, []), Is.EqualTo(1114), text);
    }
  }

  /// <summary>Two private names that drop to one, a write-only property and a struct field, in JavaScript.</summary>
  [Test]
  public void JavaScriptNames_StayApart() {
    var module = CSharp("""
      public struct P { public int X; }
      public sealed class D {
        private int _x; private int x; public P Point;
        public int Sink { set => _x = value; }
        public int Sum() => _x + x + Point.X;
      }
      public static class C { public static int F() { var d = new D(); d.Sink = 3; return d.Sum(); } }
      """);
    var text = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(text, Does.Contain("#x = 0;").And.Contain("#x$1 = 0;").And.Contain("Point = { X: 0 };").And.Contain("set Sink(value) {"));
    Assert.That(GeneratedJavaScript.Problems(text, JavaScriptProfile.Default), Is.Empty, text);
  }

  /// <summary>Test design 11: the JavaScript refusals the construct table names, one each.</summary>
  [TestCase("export class P { constructor() { this.a = 1; } } export function f(a) { return new P(...a).a; }", "a spread argument")]
  [TestCase("export class P { constructor() { this.a = 1; } } export function f() { return new P().nope(); }", "the method nope of P, which it does not have")]
  [TestCase("export class P { m() { return 1; } } export function f() { return new P().m(1, 2); }", "a call with 2 arguments for 0 parameters")]
  [TestCase("export class P { m() { return 1; } } export function f() { const p = new P(); p.m = 1; return 1; }", "an assignment to the method or getter m")]
  [TestCase("export class P { m() { return 1; } } export function f() { const p = new P(); p.m++; return 1; }", "++ of p.m, which is no field")]
  [TestCase("export class P { static get x() { return 1; } } export function f() { return 1; }", "a static accessor")]
  [TestCase("export class P { } P.prototype.m = function () { return 1; }; export function f() { return 1; }", "prototype mutation")]
  [TestCase("function F() { this.a = 1; } Object.defineProperty(F.prototype, \"x\", { value: 1 }); export function f() { return new F().a; }", "a property descriptor other than get, set, configurable and enumerable")]
  [TestCase("function F() { this.a = 1; } Object.defineProperty(F.prototype, \"x\", descriptor()); function descriptor() { return 1; } export function f() { return new F().a; }", "prototype mutation")]
  [TestCase("export class P { x = 1; } export function f() { return new P().x; }", "a class field with a value in a class without a constructor")]
  [TestCase("export class P { constructor() { this.s = \"a\"; } } export function f() { return 1; }", "a field of the type string")]
  [TestCase("export class P { constructor() { this.a = 1; } } export function f() { return new P().a.b; }", "new P().a.b")]
  public void JavaScriptConstruct_IsRefusedByName(string source, string what)
    => Assert.That(JavaScriptRefusal(source).Message, Is.EqualTo($"'{what}' is not supported yet"));

  /// <summary>
  /// An exported constructor function with static methods and fields, a class without a constructor,
  /// a write-only property, compound assignments through a setter and of a static field.
  /// </summary>
  [Test]
  public void JavaScriptForms_ComputeWhatNodeComputes() {
    var module = JavaScript("""
      export function F(a) { this.a = a; }
      F.made = 0;
      F.make = function (a) { F.made = F.made + 1; F.made += 1; F.made++; return new F(a); };
      export class Q {
        m() { return 5; }
      }
      export class S {
        constructor() { this.v = 1; }
        get w() { return this.v; }
        set w(x) { this.v = x; }
        set only(x) { this.v = x * 2; }
      }
      export function g() {
        const s = new S();
        s.w += 3;
        s.only = 4;
        return F.make(2).a + new Q().m() + s.w + F.made;
      }
      """);
    Assert.That(IrInterpreter.Instantiate(module).Call("g"), Is.EqualTo(2.0 + 5 + 8 + 3));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net10.0", "14"), Is.Empty, text);
    Assert.That(GeneratedCSharp.Compile(text, false).Method("g").Invoke(null, []), Is.EqualTo(18.0), text);
  }

}
