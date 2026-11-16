using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Globals;

/// <summary>
/// A top-level <c>const</c> the module computes when it is loaded (#1410), as SHA-256's round constants
/// <c>const K = OpCodes.Hex32ToDWords('428a2f98' + …)</c>: a read-only global whose initializer computes
/// it once, in source order, and a refusal by name where the module's initializer would read it before.
/// What the interpreter computes, what C# compiles and runs and the JavaScript written back; the expected
/// values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptComputedConstantTests {

  private const string Words = """
    /** @param {string} hex @returns {uint32[]} */
    function words(hex) {
      const out = [];
      for (let i = 0; i < hex.length; i += 8) out.push(parseInt(hex.substring(i, i + 8), 16) >>> 0);
      return out;
    }

    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static Diagnostic Refusal(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics, Is.Not.Empty);
    return result.Diagnostics[0];
  }

  private static object? RunCSharp(IrModule module, string function, params object[] arguments) {
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    return GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, arguments);
  }

  private static void WritesJavaScript(IrModule module) {
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>Scenario: round constants - const K = words('0000000a00000014') and f(1) returning K[1] is 20, as in node.</summary>
  [TestCase(1, 20u)]
  [TestCase(0, 10u)]
  public void RoundConstants_AreComputedOnce(int index, uint expected) {
    var module = Import(Words + """
      const K = words('0000000a' + '00000014');
      /** @param {int32} i */
      export function f(i) { return K[i]; }
      """);
    var global = module.Globals.Single();
    Assert.That(global.Name, Is.EqualTo("K"));
    Assert.That(global.Type, Is.EqualTo(IrType.List(IrType.U32)));
    Assert.That(IrInterpreter.Call(module, "f", [index]), Is.EqualTo(expected));
    Assert.That(RunCSharp(module, "f", index), Is.EqualTo(expected));
    WritesJavaScript(module);
  }

  /// <summary>Test design 2: a concatenation of literals, an operation on a computed constant, a call and an array literal of them are each computed once.</summary>
  [Test]
  public void Initializers_OfEveryForm_AreComputed() {
    var module = Import(Words + """
      const S = 'ab' + 'cde';
      const N = S.length * 2;
      const W = words('00000003');
      const T = [N, 2];
      export function f() { return S.length + N * 10 + W[0] * 100 + T[1] * 1000 + T.length * 10000; }
      """);
    Assert.That(module.Globals.Select(global => global.Name), Is.EqualTo(new[] { "S", "N", "W", "T" }));
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(22405.0));
    Assert.That(RunCSharp(module, "f"), Is.EqualTo(22405.0));
    WritesJavaScript(module);
  }

  /// <summary>Equivalence: an operation of literals, which was no constant, is a variable of the module the function reads.</summary>
  [Test]
  public void OperationOfLiterals_IsComputed() {
    var module = Import("const C = 1 + 1; function f() { return C; }");
    Assert.That(module.Globals.Single().Name, Is.EqualTo("C"));
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(2.0));
  }

  /// <summary>Boundary: a computed constant only the top level reads stays a local of the module's initializer; a literal one a constant.</summary>
  [Test]
  public void ConstantNoFunctionReads_StaysWhatItWas() {
    var module = Import(Words + """
      let count = 0;
      const Z = 5;
      const once = words('00000004');
      count = once[0];
      export function f() { return count + Z; }
      """);
    Assert.That(module.Globals.Select(global => global.Name), Is.EqualTo(new[] { "count" }));
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(9.0));
  }

  /// <summary>Boundary and scenario: the initializer reads K after the module computes it - the value - and before - refused naming K.</summary>
  [Test]
  public void ReadAfterTheComputation_IsTheValue() {
    var module = Import(Words + """
      let seen = 0;
      const K = words('00000007');
      /** @returns {number} */
      function g() { return K[0]; }
      seen = g();
      export function f() { return seen; }
      """);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(7.0));
    Assert.That(RunCSharp(module, "f"), Is.EqualTo(7.0));
  }

  /// <summary>Scenario and test design 4: the module's initializer calls g(), which reads K, before const K = h(): refused with PLY1102 naming K.</summary>
  [TestCase("seen = g();")]
  [TestCase("seen = K[0];")]
  [TestCase("seen = new Box().get();")]
  public void ReadBeforeTheComputation_IsRefusedByName(string early) {
    var refusal = Refusal(Words + $$"""
      let seen = 0;
      class Box { /** @returns {number} */ get() { return g(); } }
      /** @returns {number} */
      function g() { return K[0]; }
      {{early}}
      const K = words('00000007');
      export function f() { return seen + K[0]; }
      """);
    Assert.That(refusal.Code, Is.EqualTo("PLY1102"));
    Assert.That(refusal.Message, Does.Contain("K, read before the module computes it"));
  }

  /// <summary>Error path: a computed constant whose initializer calls a function that reads or writes the module's variables stays refused.</summary>
  [Test]
  public void ImpureInitializer_IsRefused() {
    var refusal = Refusal("""
      let calls = 0;
      /** @returns {number} */
      function h() { calls = calls + 1; return calls; }
      const K = h();
      export function f() { return K; }
      """);
    Assert.That(refusal.Message, Does.Contain("an initializer that calls h, which reads or writes a variable of the module"));
  }

  /// <summary>Error path: a computed constant is read-only, and an initializer that reads a later one is refused.</summary>
  [Test]
  public void LaterConstantInAnInitializer_IsRefused() {
    var refusal = Refusal("""
      const A = B + 1;
      const B = Math.floor(2.5);
      export function f() { return A; }
      """);
    Assert.That(refusal.Code, Is.EqualTo("PLY1102"));
  }

  private static readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "polypiler-computed"));

  private static string At(string name) => Path.Combine(_root, name);

  /// <summary>Test design 1: the call of a required file, as SHA-256 calls OpCodes.Hex32ToDWords from its UMD factory.</summary>
  [Test]
  public void CallOfARequiredFile_IsTheInitializer() {
    var files = new Dictionary<string, string> {
      [At("sha.js")] = """
        (function (root, factory) {
          if (typeof module === 'object' && module.exports) {
            module.exports = factory(require('./ops'));
          } else {
            root.Sha = factory(root.Ops);
          }
        }((function() {
          if (typeof globalThis !== 'undefined') return globalThis;
          throw new Error('Unable to locate global object');
        })(), function (Ops) {
          const K = Ops.Hex32ToDWords('428a2f98' + '71374491');
          /** @param {int32} i @returns {uint32} */
          function round(i) { return K[i]; }
          return { round };
        }));
        """.ReplaceLineEndings("\n"),
      [At("ops.js")] = """
        (function(global) {
          'use strict';
          const Ops = {
            /** @param {string} hexString @returns {uint32[]} */
            Hex32ToDWords: function(hexString) {
              const words = [];
              for (let i = 0; i < hexString.length; i += 8)
                words.push(parseInt(hexString.substring(i, i + 8), 16) >>> 0);
              return words;
            }
          };
          if (typeof module !== 'undefined' && module.exports) {
            module.exports = Ops;
          }
        })(typeof global !== 'undefined' ? global : this);
        """.ReplaceLineEndings("\n")
    };
    var result = JavaScriptFrontend.Import(At("sha.js"), files[At("sha.js")], null, path => files.GetValueOrDefault(path));
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    var module = result.Module!;
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "round", [1]), Is.EqualTo(0x71374491u));
    Assert.That(RunCSharp(module, "round", 0), Is.EqualTo(0x428a2f98u));
  }

}
