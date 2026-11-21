using System.IO;
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
/// <c>@template T</c> with <c>@param {T[]} arr</c>, as the Cipher corpus's <c>OpCodes.ClearArray</c>
/// states it (#1411): the parameter takes the arrays the calls of the file pass, as one without JSDoc
/// does, and a function of an object of functions that the calls pass arrays of different kinds gets a
/// copy per kind, as a function of the file does. The expected values are node's on the same source.
/// </summary>
[TestFixture]
public sealed class JavaScriptTemplateArrayTests {

  /// <summary>The helper library, written as OpCodes is: a wrapper whose object literal is the export of a file the entry requires.</summary>
  private const string Clear = """
    (function(global) {
      'use strict';
      const Ops = {
        /**
         * Clear array (fill with zeros)
         * @template T
         * @param {T[]} arr - Array to clear (modified in place)
         */
        ClearArray: function(arr) {
          for (let i = 0; i < arr.length; ++i)
            arr[i] = 0;
        }
      };
      global.Ops = Ops;
      if (typeof module !== 'undefined' && module.exports) {
        module.exports = Ops;
      }
    })(typeof global !== 'undefined' ? global : this);
    """;

  private static readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "polypiler-template-arrays"));

  private const string Words = """
    /** @param {int} n */
    function words(n) {
      const w = new Uint32Array(n);
      for (let i = 0; i < n; ++i) w[i] = 0xFFFFFFFF - i;
      Ops.ClearArray(w);
      let s = 0;
      for (let i = 0; i < n; ++i) s += w[i];
      return s * 1000 + w.length;
    }
    """;

  private const string Bytes = """
    /** @param {int} n */
    function bytes(n) {
      const b = new Uint8Array(n);
      for (let i = 0; i < n; ++i) b[i] = 200 + i;
      Ops.ClearArray(b);
      let s = 0;
      for (let i = 0; i < n; ++i) s += b[i];
      return s * 1000 + b.length;
    }
    """;

  private const string Doubles = """
    /** @param {int} n */
    function doubles(n) {
      const d = new Array(n).fill(0.5);
      Ops.ClearArray(d);
      let s = 0;
      for (let i = 0; i < d.length; ++i) s += d[i];
      return s * 1000 + d.length;
    }
    """;

  /// <summary>Imports an entry that requires the helper library and exports the given functions of its own.</summary>
  private static IrModule Import(params string[] functions) {
    var entry = Path.Combine(_root, "main.js");
    var source = "const Ops = require('./ops');\n" + string.Concat(functions)
      + $"module.exports = {{ {string.Join(", ", functions.Select(function => function[(function.IndexOf("function ", System.StringComparison.Ordinal) + 9)..function.IndexOf('(')]))} }};\n";
    var result = JavaScriptFrontend.Import(entry, source, null, path => path == Path.Combine(_root, "ops.js") ? Clear : null);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>Runs a function in the interpreter and in C#, and checks the JavaScript translations are at their level.</summary>
  private static void AssertEverywhere(IrModule module, string function, double expected, params object[] arguments) {
    Assert.That(IrInterpreter.Call(module, function, arguments), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, arguments), Is.EqualTo(expected), text);
    foreach (var profile in new[] { JavaScriptProfile.Default, JavaScriptProfile.Parse("es5", "cjs") }) {
      var script = LegalizedJavaScript.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(script, profile), Is.Empty, script);
    }
  }

  /// <summary>Scenario: clear words and bytes - both arrays are zero as in node, each call calling a copy of its kind.</summary>
  [Test]
  public void ClearWordsAndBytes() {
    var module = Import(Words, Bytes);
    var copies = module.Functions.Where(function => function.Name.StartsWith("ClearArray", System.StringComparison.Ordinal)).Select(function => function.Parameters.Single().Type).ToList();
    Assert.That(copies, Is.EquivalentTo(new[] { IrType.Array(IrType.U32), IrType.Array(IrType.U8) }));
    AssertEverywhere(module, "words", 3.0, 3);
    AssertEverywhere(module, "bytes", 3.0, 3);
  }

  /// <summary>Test design 1 and 3: one kind, uint32 words, empty, one and three long (node: 0, 1 and 3).</summary>
  [TestCase(0, 0.0)]
  [TestCase(1, 1.0)]
  [TestCase(3, 3.0)]
  public void OneKind_IsCleared(int length, double expected) {
    var module = Import(Words);
    Assert.That(module.Functions.Single(function => function.Name == "ClearArray").Parameters.Single().Type, Is.EqualTo(IrType.Array(IrType.U32)));
    AssertEverywhere(module, "words", expected, length);
  }

  /// <summary>Test design 2: bytes, words and an Array of doubles, a copy each (node: the length, every element zero).</summary>
  [TestCase("bytes", 0, 0.0)]
  [TestCase("words", 1, 1.0)]
  [TestCase("doubles", 3, 3.0)]
  [TestCase("doubles", 0, 0.0)]
  public void EveryKind_GetsACopy(string function, int length, double expected) {
    var module = Import(Words, Bytes, Doubles);
    Assert.That(module.Functions.Count(candidate => candidate.Name.StartsWith("ClearArray", System.StringComparison.Ordinal)), Is.EqualTo(3));
    AssertEverywhere(module, function, expected, length);
  }

  /// <summary>Scenario: nothing decides T - ClearArray exported with no call in the file is refused with PLY1101 naming arr.</summary>
  [Test]
  public void NothingDecidesT_IsRefused() {
    var refusal = JavaScriptFrontend.Import("m.mjs", """
      /**
       * @template T
       * @param {T[]} arr
       */
      export function ClearArray(arr) { for (let i = 0; i < arr.length; ++i) arr[i] = 0; }
      """).Diagnostics.Single();
    Assert.That((refusal.Code, refusal.Message), Is.EqualTo(("PLY1101", "parameter 'arr' of 'ClearArray' is used as an array and needs a JSDoc type")));
  }

  /// <summary>T[] without a @template T, and a template that is no array, state no type.</summary>
  [TestCase("/** @param {T[]} arr */")]
  [TestCase("/** @template T @param {T} arr */")]
  [TestCase("/** @template U @param {T[]} arr */")]
  public void WhatNoTemplateStates_IsRefused(string comment) {
    var diagnostics = JavaScriptFrontend.Import("m.mjs", comment + " function clear(arr) { arr[0] = 0; } export function f() { const a = new Uint8Array(1); clear(a); return a[0]; }").Diagnostics;
    Assert.That(diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Does.Contain(("PLY1101", "parameter 'arr' of 'clear' has no supported JSDoc type")));
  }

}
