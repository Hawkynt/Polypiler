using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.JavaScript;

/// <summary>
/// <c>typeof</c> of a value whose type is known (#1372): the constant of its kind, and of a value an
/// absent value may reach a test of absence; a comparison of two constants is decided when the module
/// is imported. The Cipher framework validates its arguments so. The expected values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptTypeofTests {

  private const string Kinds = """
    class Box { constructor() { this.text = null; this.count = null; this.items = null; } }
    function add(a, b) { return a + b; }
    /** @param {int32} which */
    export function kinds(which) {
      const box = new Box();
      if (which === 1) { box.text = 'x'; box.count = 3; box.items = [1]; }
      let code = 0;
      if (typeof 1.5 === 'number') code += 1;
      if (typeof 5n === 'bigint') code += 2;
      if (typeof 'a' === 'string') code += 4;
      if (typeof true === 'boolean') code += 8;
      if (typeof box === 'object') code += 16;
      if (typeof add === 'function') code += 32;
      if (typeof undefined === 'undefined') code += 64;
      if (typeof null === 'object') code += 128;
      if (typeof box.text === 'string') code += 256;
      if (typeof box.count === 'number') code += 512;
      if (typeof box.items === 'object') code += 1024;
      if (typeof box.text !== 'object') code += 2048;
      return code;
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>Scenario: typeof s === 'string' of a string parameter is 1, decided when the module is imported.</summary>
  [Test]
  public void TypeofOfAStringParameter_IsAConstant() {
    var module = Import("/** @param {string} s */ export function f(s) { return typeof s === 'string' ? 1 : 0; }");
    Assert.That(IrInterpreter.Call(module, "f", ["a"]), Is.EqualTo(1));
    Assert.That(IrTree.Nodes(((IrReturn)module.Functions.Single().Body.Statements.Single()).Value!).OfType<IrStrConst>(), Is.Empty, "the comparison is decided");
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("f").Invoke(null, ["a"]), Is.EqualTo(1), text);
  }

  /// <summary>Test design 1-3: every kind, a value null may reach, and typeof null.</summary>
  [TestCase(0, 1279)]
  [TestCase(1, 4095)]
  public void EveryKind_IsItsConstant(int which, double expected) {
    var module = Import(Kinds);
    Assert.That(IrInterpreter.Call(module, "kinds", [which]), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("kinds").Invoke(null, [which]), Is.EqualTo(expected), text);
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>An optional number undefined reaches is 'undefined' where it holds none.</summary>
  [Test]
  public void OptionalNumberUndefinedReaches_IsTested() {
    var module = Import("/** @param {int32} n */ export function f(n) { let x = undefined; if (n > 0) x = n; return typeof x === 'undefined' ? 1 : 2; }");
    Assert.That(IrInterpreter.Call(module, "f", [0]), Is.EqualTo(1));
    Assert.That(IrInterpreter.Call(module, "f", [3]), Is.EqualTo(2));
  }

  /// <summary>Test design 4: typeof of a value of two kinds, of both absent values, of a value of no known kind, and of an operand with an effect.</summary>
  [TestCase("/** @param {int32} n */ export function f(n) { let x = 1; if (n > 0) x = 'a'; return typeof x; }", JavaScriptFrontend.MixedKinds)]
  [TestCase("/** @param {int32} n */ export function f(n) { let x = 1; if (n > 0) x = null; if (n > 1) x = undefined; return typeof x === 'object' ? 1 : 0; }", JavaScriptFrontend.MixedKinds)]
  [TestCase("function g() { return 1; } export function f() { return typeof g() === 'number' ? 1 : 0; }", JavaScriptFrontend.Unsupported)]
  [TestCase("export function f() { return typeof missing === 'undefined' ? 1 : 0; }", JavaScriptFrontend.Unsupported)]
  public void UnknownKindOrEffect_IsRefused(string source, string code) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.Some.EqualTo(code), string.Join("; ", result.Diagnostics));
  }

}
