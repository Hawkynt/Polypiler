using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// <c>slice</c>, <c>fill</c> and <c>concat</c> of a JavaScript Array whose length never changes, which
/// is an array of the IR as a typed array is: <c>slice</c> copies, <c>fill</c> stores and yields the
/// array, <c>concat</c> makes a new Array of both. The Cipher framework writes
/// <c>new Array(n).fill(0)</c> and Adler-64 <c>Unpack32BE(b).concat(Unpack32BE(a))</c>. The expected
/// values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptArrayCopyTests {

  private const string Source = """
    export function arrays() {
      const a = [1, 2];
      const b = [3, 4, 5];
      const joined = a.concat(b, [6]);
      const part = joined.slice(1, 4);
      const filled = new Array(3).fill(7);
      filled.fill(9, 1);
      return joined.length * 1000 + part[0] * 100 + part[2] * 10 + filled[0] + filled[2] * 10000;
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>Happy path: two Arrays and a literal joined, a part copied, an Array filled whole and from a position.</summary>
  [Test]
  public void CopiesOfArrays_AreWhatNodeMakes() {
    var module = Import(Source);
    Assert.That(IrInterpreter.Call(module, "arrays", []), Is.EqualTo(96247));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("arrays").Invoke(null, []), Is.EqualTo(96247), text);
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>Boundary: concat with empty Arrays on either side, and of one argument only.</summary>
  [Test]
  public void EmptyArrays_JoinToTheOther() {
    var module = Import("export function f() { const w = [0]; const x = w.slice(1); const y = x.concat([5, 6]); const z = y.concat(x); return y.length * 10 + z.length + z[1] * 100; }");
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(622));
  }

  /// <summary>
  /// slice of a list, as the block ciphers cut their input buffer: positions clamped into the list, a
  /// negative one counted from its end, an empty part where the start passes the end; the copy grows apart.
  /// </summary>
  [Test]
  public void SliceOfAList_CopiesItsPart() {
    var module = Import("""
      export function f() {
        const l = [];
        for (let i = 0; i < 6; i++) l.push(i * 10);
        const a = l.slice(1, 4);
        const b = l.slice(-2);
        const c = l.slice(2, -1);
        const d = l.slice(5, 2);
        const e = l.slice();
        a.push(7);
        return a.length * 100000 + a[0] * 1000 + b[0] * 10 + c.length * 7 + d.length + e.length * 1000000 + a[3];
      }
      /** @param {int32} n */
      export function g(n) { const l = [1, 2, 3]; l.push(4); let at = 0; at += n; return l.slice(at, at + 1.5).length + l.slice(-1e12, 1e12).length * 10; }
      """);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(6410428));
    Assert.That(IrInterpreter.Call(module, "g", [1]), Is.EqualTo(41));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("f").Invoke(null, []), Is.EqualTo(6410428), text);
  }

  /// <summary>Error path: a list, a typed array and a part filled as a value stay refused by name.</summary>
  [TestCase("export function f() { const a = []; a.push(1); return a.splice(0).length; }", "a.splice(0)")]
  [TestCase("export function f() { const a = [1]; return a.concat(new Uint8Array(1)).length; }", "concat of")]
  [TestCase("export function f() { const a = new Array(3).fill(0, 1); return a.length; }", "fill of a part of an array as a value")]
  [TestCase("export function f() { const a = [1]; return a.subarray(0).length; }", "subarray on a plain Array")]
  public void OtherReceivers_AreRefused(string source, string named) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(named), string.Join("; ", result.Diagnostics));
  }

}
