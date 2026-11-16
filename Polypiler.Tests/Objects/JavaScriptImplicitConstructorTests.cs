using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The constructor a derived class does not declare (#1369): JavaScript gives it
/// <c>constructor(...args) { super(...args); }</c>, so it takes the parameters of the nearest
/// constructor up its lineage and passes them on, as the instance classes of the Cipher corpus rely on
/// (<c>class XInstance extends IAlgorithmInstance { … }</c>, made by <c>new XInstance(this)</c>). The
/// expected values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptImplicitConstructorTests {

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static void Agrees(string source, double expected) {
    var module = Import(source);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("f").Invoke(null, []), Is.EqualTo(expected), text);
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>Scenario: new D(2, 3).sum() of a class D extends B {} is 5.</summary>
  [Test]
  public void ImplicitConstructor_PassesItsArgumentsOn() => Agrees("""
    class B { constructor(a, b) { this.a = a; this.b = b; } }
    class D extends B { sum() { return this.a + this.b; } }
    export function f() { return new D(2, 3).sum(); }
    """, 5);

  /// <summary>Test design 1 and 3: two levels without a constructor reach the nearest one, its default included; a class below declares its own again.</summary>
  [Test]
  public void NearestConstructor_IsTheOneTaken() => Agrees("""
    class A { constructor(a, b = 7) { this.a = a; this.b = b; } }
    class B extends A { }
    class D extends B { sum() { return this.a + this.b; } }
    class E extends D { constructor(x) { super(x, x); } }
    export function f() { return new D(2, 3).sum() * 100 + new D(1).sum() * 10 + new E(4).sum(); }
    """, 588);

  /// <summary>Test design 2: constructors of no, one and three parameters.</summary>
  [TestCase("constructor() { this.v = 4; }", "new D()", 4)]
  [TestCase("constructor(a) { this.v = a; }", "new D(5)", 5)]
  [TestCase("constructor(a, b, c) { this.v = a * 100 + b * 10 + c; }", "new D(1, 2, 3)", 123)]
  public void ParameterCount_IsPassedOn(string constructor, string creation, double expected)
    => Agrees($"class B {{ {constructor} }} class D extends B {{ get value() {{ return this.v; }} }} export function f() {{ return {creation}.value; }}", expected);

  /// <summary>The constructor of the IR takes the parameters of the inherited one and calls it with them.</summary>
  [Test]
  public void ImplicitConstructor_IsAFunctionOfTheInheritedParameters() {
    var module = Import("class B { constructor(a, b) { this.a = a; this.b = b; } } class D extends B { } export function f() { return new D(2, 3).a; }");
    var constructor = module.Functions.Single(function => function.Name == "D.ctor");
    Assert.That(constructor.Parameters.Select(parameter => parameter.Name), Is.EqualTo(new[] { "this", "a", "b" }));
    Assert.That(constructor.Body.Statements.Single(), Is.InstanceOf<IrExprStmt>());
  }

  /// <summary>Test design 4: a rest parameter of the inherited constructor stays refused.</summary>
  [Test]
  public void RestParameterOfTheBase_IsRefused() {
    var result = JavaScriptFrontend.Import("m.mjs", "class B { constructor(...values) { this.n = values.length; } } class D extends B { } export function f() { return new D(1, 2).n; }");
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.EqualTo("'a rest parameter' is not supported yet"));
  }

}
