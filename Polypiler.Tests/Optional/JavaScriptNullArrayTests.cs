using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Optional;

/// <summary>
/// <c>null</c> stored into a place JSDoc states as an array (#1403): the values, not the JSDoc, decide
/// whether the place may be absent, and an array that grows - a list of the IR - is a reference that may
/// be <c>null</c> as an array is. The Cipher corpus starts a result with <c>let result = null</c> and a
/// field with <c>this.processedData = null</c> and stores a list there later. A read JavaScript makes of
/// <c>null</c> - <c>r.length</c> - is the IR's null-reference trap, as node throws a TypeError. The
/// expected values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptNullArrayTests {

  private const string Source = """
    export function scenario(n) { /** @type {uint8[]} */ let r = null; if (n > 0) r = [n]; return r === null ? -1 : r[0]; }
    export function local(n) {
      /** @type {uint8[]} */
      let r = null;
      if (n > 0) { r = []; for (let i = 0; i < n; i++) r.push(i); }
      return r === null ? -1 : r.length;
    }
    class Box {
      constructor() {
        /** @type {uint8[]|null} */
        this.data = null;
      }
      fill(n) { const out = []; for (let i = 0; i < n; i++) out.push(i + 1); this.data = out; }
      sum() { if (!this.data) return -1; let s = 0; for (let i = 0; i < this.data.length; i++) s += this.data[i]; return s; }
    }
    export function field(n) { const b = new Box(); const before = b.sum(); if (n > 0) b.fill(n); return before * 1000 + b.sum(); }
    function count(list) { return list === null ? -1 : list.length; }
    export function argument(n) { const l = []; for (let i = 0; i < n; i++) l.push(i); return count(n > 0 ? l : null) + count(null) * 100; }
    export function nullLength(n) { /** @type {uint8[]} */ let r = null; if (n > 0) { r = []; r.push(n); } return r.length; }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static double Number(object? value) => System.Convert.ToDouble(value);

  /// <summary>Scenario: assigned later - an array that null starts, run with 0 and 5, returns -1 and 5 as node does.</summary>
  [TestCase(0d, -1d)]
  [TestCase(5d, 5d)]
  public void AssignedLater_ReturnsWhatNodeReturns(double n, double expected) {
    var module = Import(Source);
    Assert.That(Number(IrInterpreter.Call(module, "scenario", [n])), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(Number(GeneratedCSharp.Compile(text, false).Method("scenario").Invoke(null, [n])), Is.EqualTo(expected), text);
  }

  /// <summary>Test design 2: a local, a field and an argument that null and a list reach are lists that may be null, in the interpreter and in C#.</summary>
  [TestCase("local", 0d, -1d)]
  [TestCase("local", 5d, 5d)]
  [TestCase("field", 0d, -1001d)]
  [TestCase("field", 5d, -985d)]
  [TestCase("argument", 0d, -101d)]
  [TestCase("argument", 5d, -95d)]
  public void NullAndAList_ReachOnePlace(string function, double n, double expected) {
    var module = Import(Source);
    Assert.That(module.Types.OfType<IrClassType>().Single().Fields.Single().Type, Is.EqualTo(IrType.List(IrType.F64)));
    Assert.That(Number(IrInterpreter.Call(module, function, [n])), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(Number(GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, [n])), Is.EqualTo(expected), text);
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>Test design 3: the length of a list that is null is the IR's null-reference trap, where node throws a TypeError; of one that is not, its length.</summary>
  [Test]
  public void LengthOfNull_IsTheNullReferenceTrap() {
    var module = Import(Source);
    Assert.That(Number(IrInterpreter.Call(module, "nullLength", [3d])), Is.EqualTo(1d));
    Assert.That(() => IrInterpreter.Call(module, "nullLength", [0d]), Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.NullReference));
  }

  /// <summary>Boundary: a list nothing absent reaches stays one that is never null - a test of it for null is false, its truth true.</summary>
  [Test]
  public void ListNoNullReaches_IsNeverNull() {
    var module = Import("""
      export function f(n) { const l = []; l.push(n); return (l === null ? 1 : 0) + (l ? 10 : 20) + (Array.isArray(l) ? 100 : 200); }
      """);
    Assert.That(Number(IrInterpreter.Call(module, "f", [1d])), Is.EqualTo(110d));
    Assert.That(IrTree.Nodes(module.Functions.Single().Body).OfType<IrIsNull>(), Is.Empty);
  }

  /// <summary>Scenario: null and undefined told apart - a local both reach and that is compared with both is refused with PLY1105.</summary>
  [Test]
  public void NullAndUndefinedToldApart_IsRefused() {
    var result = JavaScriptFrontend.Import("m.mjs", """
      export function f(n) {
        let r = null;
        if (n > 1) r = undefined;
        if (n > 0) { r = []; r.push(n); }
        return r === null ? 1 : r === undefined ? 2 : r.length;
      }
      """);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.Some.EqualTo(JavaScriptFrontend.MixedKinds), string.Join("; ", result.Diagnostics));
  }

}
