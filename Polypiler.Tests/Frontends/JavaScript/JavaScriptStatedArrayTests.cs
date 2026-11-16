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
/// JSDoc's <c>T[]</c> of numbers (#1374) says what the elements are, not whether the parameter is a
/// JavaScript <c>Array</c> or a typed array: the calls of the file decide - an Array, a list where it
/// grows, a typed array - and where none passes one, a function that tests <c>Array.isArray</c> of it
/// requires an Array. The Cipher corpus writes <c>@param {uint8[]} data</c> for Arrays of bytes. The
/// expected values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptStatedArrayTests {

  private const string Source = """
    /** @param {uint8[]} d */
    function length(d) { return Array.isArray(d) ? d.length : -1; }
    /** @param {uint8[]} d */
    function first(d) { return d[0]; }
    /** @param {uint8[]} d */
    function store(d) { d[0] = 255; return d[0]; }
    /** @param {uint8[]} d */
    function grow(d) { d.push(7); return d.length; }
    export function scenario() { return length([1, 2, 3]); }
    export function kinds() {
      const typed = new Uint8Array(2);
      typed[0] = 9;
      const list = [];
      list.push(4);
      return first(typed) * 1000 + store([1, 2]) + grow(list) * 10000;
    }
    /** @param {uint8[]} data */
    export function feed(data) { if (!Array.isArray(data)) throw new Error('not an array'); return data.length; }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static IrType ParameterOf(IrModule module, string function) => module.Functions.Single(item => item.Name == function).Parameters.Single().Type;

  /// <summary>Scenario: Array.isArray of a stated uint8[] that the file passes an Array to is true, and the length 3.</summary>
  [Test]
  public void StatedArrayPassedAnArray_IsAnArray() {
    var module = Import(Source);
    Assert.That(IrInterpreter.Call(module, "scenario", []), Is.EqualTo(3));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("scenario").Invoke(null, []), Is.EqualTo(3), text);
  }

  /// <summary>Test design 1-3: an Array, a typed array and a list passed; a store of 255; a function called from outside that requires an Array.</summary>
  [Test]
  public void EveryCaller_DecidesItsKind() {
    var module = Import(Source);
    Assert.That(IrInterpreter.Call(module, "kinds", []), Is.EqualTo(29255));
    Assert.That(ParameterOf(module, "grow"), Is.EqualTo(IrType.List(IrType.U8)));
    Assert.That(ParameterOf(module, "first"), Is.EqualTo(IrType.Array(IrType.U8)));
    Assert.That(IrInterpreter.Call(module, "feed", [new byte[] { 1, 2 }]), Is.EqualTo(2), "Feed requires an Array, which a caller from outside passes");
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("kinds").Invoke(null, []), Is.EqualTo(29255), text);
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>Test design 4: calls passing an Array and a typed array to one stated parameter are refused; an explicit Uint8Array stays a typed array.</summary>
  [Test]
  public void CallersOfBothKinds_AreRefused() {
    var result = JavaScriptFrontend.Import("m.mjs", "/** @param {uint8[]} d */ function f(d) { return d.length; } export function g() { return f([1]) + f(new Uint8Array(1)); }");
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.Some.EqualTo(JavaScriptFrontend.MixedKinds), string.Join("; ", result.Diagnostics));
    var typed = Import("/** @param {Uint8Array} d */ export function f(d) { return Array.isArray(d) ? 1 : 0; }");
    Assert.That(IrInterpreter.Call(typed, "f", [new byte[] { 1 }]), Is.EqualTo(0));
    var undecided = JavaScriptFrontend.Import("m.mjs", "/** @param {uint8[]} d */ export function f(d) { const e = d; return Array.isArray(e) ? 1 : 0; }");
    Assert.That(undecided.Diagnostics.Single().Message, Is.EqualTo("'Array.isArray of uint8[], which JSDoc may have meant as an Array' is not supported yet"));
  }

}
