using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Integer;

/// <summary>
/// Exact integers stored or passed where JSDoc states an integer type (#1416), as the Cipher
/// framework's <c>BlockAbsorber</c> keeps <c>/** @type {int} */ this._pending</c> and adds
/// <c>Math.min(blockSize - this._pending, total - offset)</c> to it: a sum, a difference and a product of
/// integers, and <c>Math.min</c>, <c>max</c> and <c>abs</c> of them, are integers a double computes as
/// JavaScript does; the stated place converts them, exactly where they fit and trapping with 'integer
/// overflow' where they do not (<c>js-stated-integer</c>). A result keeps its own type whatever
/// <c>@returns</c> states: the uncoerced sum is the double node returns. A value that may have a fraction
/// is refused where it is passed. The expected values are node's on the same source.
/// </summary>
[TestFixture]
public sealed class JavaScriptStatedIntegerTests {

  private const string Source = """
    class Pending {
      constructor() {
        /** @type {int} */
        this.p = 0;
      }
      /**
       * @param {int} b
       * @param {int} t
       */
      add(b, t) { this.p += Math.min(b - this.p, t); }
    }
    /**
     * @param {int} a
     * @returns {int}
     */
    function twice(a) { return a + a; }
    /**
     * @param {int} a
     * @param {int} d
     * @returns {int}
     */
    function shift(a, d) { return id(a + d); }
    /**
     * @param {int} v
     * @returns {int}
     */
    function id(v) { return v; }
    /**
     * @param {int} a
     * @param {int} b
     * @returns {int}
     */
    function ops(a, b) { return Math.max(a - b, 0) + Math.abs(a - b) * 2 - id(a * 3) + Math.min(a, b - 1); }
    /**
     * @param {int} n
     * @returns {number}
     */
    function local(n) {
      /** @type {int} */
      const m = n * n - 1;
      return m;
    }
    /**
     * @param {int} n
     * @returns {uint8[]}
     */
    function counts(n) {
      const out = [];
      out.push(n + 1, n * 1, n - 1);
      return out;
    }
    /**
     * @param {int} big
     * @returns {int}
     */
    function wide(big) {
      let x = big;
      for (let i = 0; i < 3; i++) x = x * 65536;
      return x - x + big;
    }
    export function pending() { const q = new Pending(); q.add(64, 10); q.add(64, 100); return q.p; }
    export function inRange() { return twice(1073741823); }
    export function largest() { return shift(2147483646, 1); }
    export function past() { return shift(2147483647, 1); }
    export function below() { return shift(-2147483648, -1); }
    export function operations() { return ops(7, 10) * 1000 + ops(10, 7); }
    export function quotients() { return id(Math.floor(-7 / 2)) * 100 + id(Math.ceil(7 / 2)); }
    export function square() { return local(1000); }
    export function bytes() { const b = counts(3); return b[0] * 100 + b[1] * 10 + b[2]; }
    export function huge() { return wide(5); }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static object? Run(string text, string function) {
    try {
      return GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, []);
    } catch (System.Reflection.TargetInvocationException invoked) when (invoked.InnerException is OverflowException) {
      return "integer overflow";
    }
  }

  /// <summary>
  /// Scenario: a pending count (64). Operations: <c>+</c>, <c>-</c>, <c>*</c>, <c>Math.min</c>, <c>max</c>,
  /// <c>abs</c>, the rounding of a quotient; places: a field, an argument, a local, the elements of
  /// a stated <c>uint8[]</c>; a product past 2^53 is still the integer JavaScript computes. Boundaries:
  /// 2147483647 is the value, 2147483648 and -2147483649 trap.
  /// </summary>
  [TestCase("pending", 64.0)]
  [TestCase("inRange", 2147483646.0)]
  [TestCase("largest", 2147483647.0)]
  [TestCase("operations", -8015.0)]
  [TestCase("quotients", -396.0)]
  [TestCase("square", 999999.0)]
  [TestCase("bytes", 432.0)]
  [TestCase("huge", 5.0)]
  public void ExactIntegers_HoldWhatNodeHolds(string function, double expected) {
    var module = Import(Source);
    Assert.That(Convert.ToDouble(IrInterpreter.Call(module, function, [])), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(Convert.ToDouble(Run(text, function)), Is.EqualTo(expected), text);
  }

  /// <summary>Scenario: leaving the stated range of a parameter traps, past the largest and below the smallest <c>int</c>, where node keeps 2147483648 and -2147483649.</summary>
  [TestCase("past")]
  [TestCase("below")]
  public void LeavingTheStatedRange_Traps(string function) {
    var module = Import(Source);
    Assert.That(() => IrInterpreter.Call(module, function, []), Throws.TypeOf<IrTrapException>().With.Message.Contains("integer overflow"));
    Assert.That(Run(LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content, function), Is.EqualTo("integer overflow"));
  }

  /// <summary>Happy path: the module announces the trap; the places are of the stated types; the JavaScript translation is well-formed.</summary>
  [Test]
  public void StatedPlaces_AreTheStatedIntegers() {
    var result = JavaScriptFrontend.Import("m.mjs", Source);
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Has.Member(JavaScriptFrontend.StatedInteger));
    var text = LegalizedCSharp.Emit(result.Module!, CSharpProfile.Default)[0].Content;
    Assert.That(text, Does.Contain("public int p;").And.Contain("static double twice(int a)").And.Contain("id(checked((int)").And.Contain("List<byte> counts(int n)"));
    var script = LegalizedJavaScript.Emit(result.Module!, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>A sum stored into a place that holds it is no trap: the module has no such caveat.</summary>
  [Test]
  public void SumThatFits_HasNoCaveat() {
    var result = JavaScriptFrontend.Import("m.mjs", "/** @param {uint8} a @returns {uint16} */ export function f(a) { return a + a; }");
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Has.No.Member(JavaScriptFrontend.StatedInteger));
    Assert.That(IrInterpreter.Call(result.Module!, "f", [(byte)255]), Is.EqualTo((ushort)510));
  }

  /// <summary>Exception: a value that may have a fraction - a quotient, a square root, the minimum of a fraction, the maximum of nothing - is refused where it is passed as a stated integer.</summary>
  [TestCase("return id(Math.sqrt(a));")]
  [TestCase("return id(a / 2);")]
  [TestCase("return id(Math.min(a, 0.5));")]
  [TestCase("return id(Math.abs(a / 2));")]
  [TestCase("return id(Math.max());")]
  public void Fraction_IsRefused(string body) {
    var result = JavaScriptFrontend.Import("m.mjs", $$"""
      /**
       * @param {int} v
       * @returns {int}
       */
      function id(v) { return v; }
      /**
       * @param {int} a
       * @returns {int}
       */
      export function f(a) { {{body}} }
      """);
    Assert.That(result.Module, Is.Null);
    var refusal = result.Diagnostics.Single();
    Assert.That((refusal.Code, refusal.Message), Is.EqualTo((JavaScriptFrontend.Unsupported, "'float64 where int32 is expected' is not supported yet")));
  }

  /// <summary>A result JSDoc states an integer of that may have a fraction stays the double it is, as the recorded fixture <c>contradictedResult</c> says.</summary>
  [Test]
  public void ContradictedResult_StaysADouble() {
    var module = Import("/** @param {int} a @returns {int} */ export function f(a) { return a / 2; }");
    Assert.That(module.Functions[0].ReturnType, Is.EqualTo(IrType.F64));
    Assert.That(IrInterpreter.Call(module, "f", [3]), Is.EqualTo(1.5));
  }

  /// <summary>A whole double that may be a NaN stays the double it is where a function states an integer result, as before.</summary>
  [Test]
  public void WholeThatMayBeNaN_StaysADouble() {
    var module = Import("/** @param {number} v @returns {int} */ export function f(v) { return Math.trunc(v) % 7; }");
    Assert.That(module.Functions[0].ReturnType, Is.EqualTo(IrType.F64));
    Assert.That(IrInterpreter.Call(module, "f", [-15.5]), Is.EqualTo(-1.0));
  }

  /// <summary>A loop over a list in a function value a method is passed, whose element is a local of the function value (#1416, sha256's Finish).</summary>
  [Test]
  public void LoopInAFunctionValue_IsALocalOfIt() {
    var module = Import("""
      class Holder {
        constructor() { /** @type {int} */ this.n = 3; }
        /**
         * @template T
         * @param {function(int):T} finalize
         * @returns {T}
         */
        Finish(finalize) { return finalize(this.n); }
      }
      /**
       * @param {int} n
       * @returns {int[]}
       */
      function parts(n) { const out = []; for (let i = 0; i < n; i++) out.push(i); return out; }
      class User {
        constructor() { /** @type {number} */ this.total = 0; }
        /** @param {number} v */
        add(v) { this.total += v; }
        run() {
          new Holder().Finish(n => {
            for (const p of parts(n))
              this.add(p);
          });
          return this.total;
        }
      }
      export function f() { return new User().run(); }
      """);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(3.0));
  }

  /// <summary>A variable of the module computed by a function that tests a regular expression starts after the expression's field (#1416, sha256's K).</summary>
  [Test]
  public void ModuleVariableOfARegexTest_StartsAfterTheRegex() {
    var module = Import("""
      /**
       * @param {string} s
       * @returns {int}
       */
      function count(s) { if (!/^[a-z]*$/.test(s)) throw new Error('x'); return s.length; }
      const N = count('abc');
      export function f() { return N; }
      """);
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(Convert.ToDouble(GeneratedCSharp.Compile(text, false).Method("f").Invoke(null, [])), Is.EqualTo(3.0), text);
  }

  private const string More = """
    /**
     * @param {int} v
     * @returns {int}
     */
    function id(v) { return v; }
    /**
     * @param {int} a
     * @param {int} b
     * @returns {int}
     */
    function gcd(a, b) {
      return id(a % (b & 7));
    }
    /**
     * @param {int} a
     * @returns {int}
     */
    function absolutes(a) {
      const big = Math.trunc(a * 1.5);
      return id(Math.abs(0 - a - 1)) + id(Math.abs(a + 1) - Math.abs(-5 + 1)) + id(Math.min(Math.abs(big), 7));
    }
    /**
     * @param {int} n
     * @returns {int}
     */
    function declared(n) {
      /** @type {string} */
      const text = 'x';
      /** @type {int} */
      let a = n + 1, b = n;
      /** @type {int} */
      const c = Math.ceil(n / 3) + Math.floor(-n / 3) + Math.round(n / 1);
      return a + b + c + text.length;
    }
    export function divisor() { return gcd(-47, 18) * 100 + gcd(7, 5); }
    export function zeroDivisor() { return gcd(5, 8); }
    export function abs() { return absolutes(4); }
    export function locals() { return declared(10); }
    """;

  /// <summary>
  /// Equivalence: a remainder by a computed divisor - a NaN where it is 0, else below it - <c>Math.abs</c>
  /// of a negative, a positive and a mixed range and of no bound, the roundings of quotients, stated locals
  /// of an integer (also a declaration of two names and a local of a string, which state nothing). Values: node's.
  /// </summary>
  [TestCase("divisor", -98.0)]
  [TestCase("abs", 12.0)]
  [TestCase("locals", 32.0)]
  public void MoreExactIntegers_HoldWhatNodeHolds(string function, double expected) {
    var module = Import(More);
    Assert.That(Convert.ToDouble(IrInterpreter.Call(module, function, [])), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(Convert.ToDouble(Run(text, function)), Is.EqualTo(expected), text);
  }

  /// <summary>Boundary: a remainder by 0 is a NaN, which node returns and the stated parameter traps on (caveat <c>js-nan-store</c>).</summary>
  [Test]
  public void RemainderByZero_Traps() {
    var result = JavaScriptFrontend.Import("m.mjs", More);
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Has.Member(JavaScriptFrontend.NaNStore));
    Assert.That(() => IrInterpreter.Call(result.Module!, "zeroDivisor", []), Throws.TypeOf<IrTrapException>().With.Message.Contains("integer overflow"));
  }

  /// <summary>Exception: a remainder by a divisor that is only ever 0, a rounding of a quotient by 0, by a fraction or of a fraction, and <c>Math.abs</c> of two arguments are no exact integers.</summary>
  [TestCase("id(a % 0)")]
  [TestCase("id(a % (a & 0 & 0) + 0 * Math.floor(a / 0))")]
  [TestCase("id(Math.floor(a / 0.5))")]
  [TestCase("id(Math.floor(a / 0))")]
  [TestCase("id(Math.floor((a + 0.5) / 2))")]
  [TestCase("id(Math.floor(a * 2))")]
  [TestCase("id(Math.abs(a + 1, 2))", "'Math.abs with 2 arguments' is not supported yet")]
  public void NoExactInteger_IsRefused(string value, string refusal = "'float64 where int32 is expected' is not supported yet") {
    var result = JavaScriptFrontend.Import("m.mjs", $$"""
      /**
       * @param {int} v
       * @returns {int}
       */
      function id(v) { return v; }
      /**
       * @param {int} a
       * @returns {number}
       */
      export function f(a) { return {{value}}; }
      """);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Member(refusal));
  }

  /// <summary>A catch clause in a function value a method is passed: its variable is a local of the function value.</summary>
  [Test]
  public void CatchInAFunctionValue_IsALocalOfIt() {
    var module = Import("""
      class Holder {
        /**
         * @param {function(int):void} run
         */
        Run(run) { run(1); }
      }
      class User {
        constructor() { /** @type {number} */ this.total = 0; }
        go() {
          new Holder().Run(n => {
            try { throw new Error('x'); } catch (e) { this.total = n + 1; }
          });
          return this.total;
        }
      }
      export function f() { return new User().go(); }
      """);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(2.0));
  }

}
