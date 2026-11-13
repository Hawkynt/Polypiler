using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.JavaScript;

/// <summary>
/// The check every generated file is held to must be able to fail: it finds syntax that is newer
/// than the level, a global, a member of a global and a method the profile lacks, what is never to
/// be emitted, and a name that is in no table at all.
/// </summary>
[TestFixture]
public sealed class GeneratedJavaScriptTests {

  private static JavaScriptProfile Profile(string level, string? module = null, bool typedArrays = false)
    => JavaScriptProfile.Parse(level, module, module == "iife" ? "G" : null, typedArrays);

  /// <summary>Code that is at its level, with every kind of name the check must not take for a global.</summary>
  [TestCase("es5", "cjs", "\"use strict\";\nvar K = { hi: 1, lo: 2 };\nfunction F(a) { var b = a.lo + K.hi; return Math.floor(b) + NaN; }\nexports.F = F;")]
  [TestCase("es5", "iife", "var G = (function () { function F(a) { return a | 0; } return { F: F }; })();")]
  [TestCase("es2015", "esm", "const K = 1;\nexport function F(a) { let b = Math.imul(a, K); return new Uint8Array(b).fill(0); }")]
  [TestCase("es2017", "cjs", "function F(a) { return a.length; }\nexports.F = F;")]
  [TestCase("es2020", "esm", "export function F(a) { return BigInt.asIntN(64, a + 1n); }")]
  [TestCase("es2022", "esm", "const $view = new DataView(new ArrayBuffer(8));\nexport function F(a) { $view.setFloat64(0, a); return $view.getBigInt64(0); }")]
  public void CodeAtItsLevel_HasNoProblems(string level, string module, string code)
    => Assert.That(GeneratedJavaScript.Problems(code, Profile(level, module)), Is.Empty);

  /// <summary>Syntax the level does not have is a syntax error of the parser at that level.</summary>
  [TestCase("es5", "cjs", "let a = 1;")]
  [TestCase("es5", "cjs", "const a = 1;")]
  [TestCase("es5", "cjs", "var f = function () { return 1n; };")]
  [TestCase("es2017", "esm", "export const a = 1n;")]
  [TestCase("es5", "cjs", "var f = (a) => a;")]
  [TestCase("es2015", "esm", "export const a = 2 ** 3;")]
  [TestCase("es2020", "esm", "export const a = 1_000;")]
  [TestCase("es2015", "cjs", "export function F() { }")]
  [TestCase("es2022", "esm", "export function F( { }")]
  public void NewerSyntax_IsNotAtTheLevel(string level, string module, string code) {
    var problems = GeneratedJavaScript.Problems(code, Profile(level, module));
    Assert.That(problems, Has.Count.EqualTo(1));
    Assert.That(problems[0], Does.StartWith($"not {level}: "));
  }

  /// <summary>What parses but is not the profile's to use.</summary>
  [TestCase("es5", "cjs", false, "function F(a) { return Math.imul(a, a); }", "'Math.imul' is not available in es5, cjs")]
  [TestCase("es5", "cjs", false, "function F(a) { return new Uint8Array(a); }", "'Uint8Array' is not available in es5, cjs")]
  [TestCase("es5", "cjs", true, "function F(a) { return a.fill(0); }", "'.fill' is not available in es5 with typed arrays, cjs")]
  [TestCase("es5", "cjs", false, "function F(a, b) { a.set(b.subarray(0, 1), 0); }", "'.set' is not available in es5, cjs")]
  [TestCase("es2017", "esm", false, "export function F(a) { return BigInt(a); }", "'BigInt' is not available in es2017, esm")]
  [TestCase("es2017", "esm", false, "export function F(a) { return a.getBigInt64(0); }", "'.getBigInt64' is not available in es2017, esm")]
  [TestCase("es2022", "esm", false, "export function F(a) { return Math.cbrt(a); }", "'Math.cbrt' is never to be emitted")]
  [TestCase("es2022", "esm", false, "export function F(a) { return Number.isNaN(a); }", "'Number.isNaN' is never to be emitted")]
  [TestCase("es2022", "esm", false, "export const F = a => a;", "'=>' is never to be emitted")]
  [TestCase("es2022", "esm", false, "export function F(a) { return a ** 2; }", "'**' is never to be emitted")]
  [TestCase("es2022", "esm", false, "export function F() { return 1_000; }", "'numeric separators' is never to be emitted")]
  [TestCase("es2022", "esm", false, "export function F() { return globalThis; }", "'globalThis' is never to be emitted")]
  [TestCase("es2022", "esm", false, "export function F(a) { console.log(a); }", "'console' is not in the capability table")]
  [TestCase("es2022", "esm", false, "export function F(a) { return Math.sin(a); }", "'Math.sin' is not in the capability table")]
  [TestCase("es2022", "esm", false, "export function F(a) { return a.slice(1); }", "'.slice' is not in the capability table")]
  [TestCase("es2022", "esm", false, "function F() { }\nexports.F = F;", "'exports' is not in the capability table")]
  [TestCase("es2022", "esm", false, "export function F() { return { hi: hi }; }", "'hi' is not in the capability table")]
  public void WhatTheProfileLacks_IsFound(string level, string module, bool typedArrays, string code, string problem)
    => Assert.That(GeneratedJavaScript.Problems(code, Profile(level, module, typedArrays)), Does.Contain(problem));

  /// <summary>A problem is stated once however often the code repeats it, and a table of its own is asked instead of the standard one.</summary>
  [Test]
  public void Problems_AreStatedOnceAgainstTheTableGiven() {
    const string code = "function F(a) { return Math.imul(a, a) + Math.imul(a, 2); }";
    Assert.That(GeneratedJavaScript.Problems(code, Profile("es5")), Is.EqualTo(new[] { "'Math.imul' is not available in es5, cjs" }));
    Assert.That(GeneratedJavaScript.Problems(code, Profile("es5"), JavaScriptCapabilities.Standard.With(new("Math.imul", 5))), Is.Empty);
    Assert.That(GeneratedJavaScript.Problems(code, Profile("es2015", "cjs")), Is.Empty);
  }

}
