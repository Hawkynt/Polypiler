using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.JavaScript;

/// <summary>
/// <c>||</c> and <c>&amp;&amp;</c> as a value whose left operand has an effect, as the Cipher framework's
/// <c>Find</c> writes <c>Algorithms.find(…) || null</c>: the left operand is evaluated once, into a
/// temporary that is tested and then yielded; the right one only where it is chosen. A value that
/// counts as true is never absent, so <c>a || b</c> is absent only where <c>b</c> may be. The expected
/// values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptLogicalValueTests {

  private const string Source = """
    class Algo { /** @param {string} n */ constructor(n) { this.name = n; } }
    const Algorithms = [];
    function find(name) { return Algorithms.find(a => a.name === name) || null; }
    let calls = 0;
    function next() { calls++; return calls > 1 ? 'x' : ''; }
    export function logical() {
      Algorithms.push(new Algo('a'));
      const found = find('a');
      const missing = find('z');
      const s = next() || 'fallback';
      const t = next() || 'fallback';
      const u = next() && 'and';
      return (found ? 1 : 0) + (missing ? 10 : 0) + s.length * 100 + t.length * 1000 + u.length * 10000 + calls * 100000;
    }
    """;

  /// <summary>Happy path and both operators: every left operand runs once, the right one where it is chosen.</summary>
  [Test]
  public void LeftOperandWithAnEffect_IsEvaluatedOnce() {
    var result = JavaScriptFrontend.Import("m.mjs", Source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrInterpreter.Call(result.Module!, "logical", []), Is.EqualTo(331801));
    var text = LegalizedCSharp.Emit(result.Module!, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("logical").Invoke(null, []), Is.EqualTo(331801), text);
  }

  /// <summary>Boundary: the type of a || b is the present left value joined with b - a string, not a string that may be null.</summary>
  [Test]
  public void OrOfAValueThatMayBeNull_IsPresentWhereTheRightOneIs() {
    var result = JavaScriptFrontend.Import("m.mjs", """
      class Box { constructor() { this.text = null; } name() { this.text = 'x'; } }
      export function f() { const box = new Box(); const s = box.text || 'none'; return s.length; }
      """);
    Assert.That(IrInterpreter.Call(result.Module!, "f", []), Is.EqualTo(4));
    Assert.That(result.Module!.Functions.Single(function => function.Name == "f").Locals.Single(local => local.Name == "s").Type, Is.EqualTo(IrType.String));
  }

  /// <summary>Error path: operands of two kinds stay refused.</summary>
  [Test]
  public void OperandsOfTwoKinds_AreRefused() {
    var result = JavaScriptFrontend.Import("m.mjs", "function g() { return 1; } export function f() { const v = g() || 'x'; return 0; }");
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains("|| on number and string"));
  }

}
