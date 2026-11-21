using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// Array literals with spread elements (#1405), as the Cipher instances copy the key they are given
/// with <c>[...keyBytes]</c>: <c>[...a]</c> is a new Array of the elements of <c>a</c>, distinct from
/// it, and <c>[x, ...a, ...b]</c> the elements of every part in order. A part spreads an Array, a list,
/// a typed array or a view; the copy of a typed array is an Array, whose elements no longer wrap. A
/// hole and a spread of anything else are refused by name. The expected values are node's on the same source.
/// </summary>
[TestFixture]
public sealed class JavaScriptArraySpreadTests {

  private const string Source = """
    function f(a) { const b = [...a]; b[0] = 9; return a[0] * 10 + b[0]; }
    export function scenario() { return f([1, 2]); }
    export function parts() {
      const a = [1, 2];
      const b = [3];
      const e = [];
      const joined = [0, ...a, ...e, 7, ...b, ...a];
      return joined.length * 10000000 + joined[0] * 1000000 + joined[1] * 100000 + joined[2] * 10000 + joined[3] * 1000 + joined[4] * 100 + joined[5] * 10 + joined[6];
    }
    export function fromTyped() {
      const t = new Uint8Array([200, 100]);
      const c = [...t];
      c[0] = c[0] + 100;
      t[1] = 1;
      return c[0] * 1000 + c[1] + t[0] * 1000000;
    }
    export function fromList() {
      const l = [];
      l.push(5);
      l.push(6);
      const c = [...l, 1.5];
      l.push(7);
      return c.length * 100 + c[0] * 10 + c[2];
    }
    export function fromView() {
      const t = new Uint8Array([1, 2, 3]);
      const c = [...t.subarray(1)];
      c[0] = c[0] + 300;
      return c[0] * 10 + c.length;
    }
    export function grown() {
      const t = new Int8Array([-3]);
      const g = [...t, 4];
      g.push(-1);
      return g.length * 100 + g[0] + g[1] * 10 + g[2];
    }
    export function empty() {
      const e = [];
      const c = [...e];
      const one = [...[8]];
      return c.length * 10 + one.length + one[0] * 100;
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>
  /// Scenario: a copy is distinct (19). Equivalence classes of the source - an Array, a typed array whose
  /// copy no longer wraps, a list, a view, a typed array spread into an Array that grows - and the
  /// boundaries of the parts: an empty source, one element, several spreads.
  /// </summary>
  [TestCase("scenario", 19)]
  [TestCase("parts", 70127312)]
  [TestCase("fromTyped", 200300100)]
  [TestCase("fromList", 351.5)]
  [TestCase("fromView", 3022)]
  [TestCase("grown", 336)]
  [TestCase("empty", 801)]
  public void Spread_YieldsWhatNodeYields(string function, double expected) {
    var module = Import(Source);
    Assert.That(IrInterpreter.Call(module, function, []), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, []), Is.EqualTo(expected), text);
  }

  /// <summary>The JavaScript round trip is valid at every level the backend writes.</summary>
  [TestCase("es5")]
  [TestCase("es2022")]
  public void Spread_WritesValidJavaScript(string level) {
    var profile = JavaScriptProfile.Parse(level);
    var script = LegalizedJavaScript.Emit(Import(Source), profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, profile), Is.Empty, script);
  }

  /// <summary>A copy of an Array of the same elements is one copy; one with other parts is the concat of them, with no copy before it.</summary>
  [Test]
  public void Spread_CopiesOnce() {
    var text = LegalizedCSharp.Emit(Import("export function f() { const a = [1, 2]; const b = [...a]; const c = [...a, ...b]; b[0] = 5; return a[0] + b[0] * 10 + c[2] * 100; }"), CSharpProfile.Default)[0].Content;
    Assert.That(text, Does.Contain("var b = global::System.MemoryExtensions.AsSpan(a).ToArray();"));
    Assert.That(text, Does.Contain("_concat0(a, b)"));
  }

  /// <summary>Scenario and error path: a hole, a spread of a string, of a Map, of a number, and a spread in what a typed array is made of.</summary>
  [TestCase("export function f() { return [1, , 2].length; }", "an array literal with a hole")]
  [TestCase("export function f() { return ['x', ...'ab'].length; }", "a spread of string")]
  [TestCase("export function f() { const m = new Map(); m.set(1, 2); return [...m].length; }", "a spread of a Map of")]
  [TestCase("export function f(n) { return [1, ...n].length; }", "a spread of")]
  [TestCase("export function f() { const a = [1]; return new Uint8Array([...a]).length; }", "a spread element in the elements of a typed array, a Map or a Set")]
  public void Construct_IsRefusedByName(string source, string named) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Has.Some.Matches<(string Code, string Message)>(diagnostic => diagnostic.Code == JavaScriptFrontend.Unsupported && diagnostic.Message.Contains(named)), string.Join("; ", result.Diagnostics));
  }

}
