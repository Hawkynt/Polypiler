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
/// <c>++</c> and <c>--</c> of a field used as a value (#1407), as a stream cipher reads its keystream
/// with <c>this.keystreamBuffer[this.keystreamPosition++]</c>: the receiver evaluated once, the old
/// (postfix) or the new (prefix) value yielded, the field stored with the step in double arithmetic, as
/// <c>+= 1</c> stores it. The expected values are node's on the same source.
/// </summary>
[TestFixture]
public sealed class JavaScriptFieldStepTests {

  private const string Source = """
    class R {
      constructor() {
        this.p = 0;
      }
      next() { return [10, 20][this.p++]; }
    }
    export function scenario() {
      const r = new R();
      const a = r.next();
      const b = r.next();
      return a * 1000 + b * 10 + r.p;
    }
    class C {
      constructor() {
        this.n = 5;
      }
    }
    let made = 0;
    /** @param {C} c */
    function pick(c) { made++; return c; }
    export function forms() {
      const c = new C();
      const a = c.n++;
      const b = ++c.n;
      const d = c.n--;
      const e = --c.n;
      const f = pick(c).n++;
      return a * 100000 + b * 10000 + d * 1000 + e * 100 + f * 10 + c.n + made * 1000000;
    }
    class L {
      constructor() {
        this.v = 2147483647;
      }
    }
    export function limit() {
      const l = new L();
      const old = l.v++;
      const m = new L();
      m.v += 1;
      return (l.v - old) + (l.v === m.v ? 10 : 0) + (l.v === 2147483648 ? 100 : 0);
    }
    class B {
      constructor() {
        this.k = 1;
      }
    }
    class D extends B {
      /** @returns {number} */
      bump() { return ++this.k; }
    }
    export function inherited() {
      const d = new D();
      const b = new B();
      const x = d.bump();
      const y = b.k++;
      return x * 10 + y + b.k * 100;
    }
    class S {
      static count = 0;
    }
    export function statics() {
      const a = S.count++;
      const b = ++S.count;
      return a * 10 + b;
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics.Where(diagnostic => diagnostic.Severity == Core.DiagnosticSeverity.Error), Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>
  /// Scenario: the postfix read position yields 10, then 20, and leaves p at 2. Equivalence classes of
  /// the operators (<c>x++</c>, <c>++x</c>, <c>x--</c>, <c>--x</c>) with a receiver of an effect evaluated
  /// once; the boundary of a field at 2147483647, stepped as <c>+= 1</c> steps it; a field of a base
  /// class through <c>this</c> of a derived class and through an object of the base class; a static field.
  /// </summary>
  [TestCase("scenario", 10202)]
  [TestCase("forms", 1577556)]
  [TestCase("limit", 111)]
  [TestCase("inherited", 221)]
  [TestCase("statics", 2)]
  public void StepOfAField_YieldsWhatNodeYields(string function, double expected) {
    var module = Import(Source);
    Assert.That(IrInterpreter.Call(module, function, []), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, []), Is.EqualTo(expected), text);
  }

  /// <summary>The step is one function of the module per field and form, which the JavaScript backend writes as valid code at every level.</summary>
  [TestCase("es5")]
  [TestCase("es2022")]
  public void StepOfAField_IsAFunctionPerFieldAndForm(string level) {
    var module = Import(Source);
    Assert.That(module.Functions.Count(function => function.Name.StartsWith("$step")), Is.EqualTo(8));
    var profile = JavaScriptProfile.Parse(level);
    var script = LegalizedJavaScript.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, profile), Is.Empty, script);
  }

  /// <summary>Scenario and error paths: an accessor, a private field, a field null reaches as well, a property that is no field.</summary>
  [TestCase("class A { constructor() { this.x = 0; } get n() { return this.x; } set n(v) { this.x = v; } m() { return this.n++; } }", "a step of the accessor n")]
  [TestCase("class A { constructor() { this.x = 0; } get n() { return this.x; } m() { return this.n--; } }", "a step of the accessor n")]
  [TestCase("class A { constructor() { this.#q = 0; } #q; m() { return this.#q++; } }", "++ of the private field #q as a value")]
  [TestCase("class A { constructor() { this.p = null; } m() { this.p = 1; return this.p++; } }", "++ of the field p of the type f64?")]
  [TestCase("class A { constructor() { this.z = 0; } m() { return --this.y; } }", "-- of this.y, which is no field")]
  public void Construct_IsRefusedByName(string declaration, string named) {
    var result = JavaScriptFrontend.Import("m.mjs", declaration + " export function f() { return new A().m('z'); }");
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)),
      Has.Some.Matches<(string Code, string Message)>(diagnostic => diagnostic.Code == JavaScriptFrontend.Unsupported && diagnostic.Message.Contains(named)), string.Join("; ", result.Diagnostics));
  }

}
