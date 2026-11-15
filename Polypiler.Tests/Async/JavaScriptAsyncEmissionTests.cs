using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Async;

/// <summary>
/// The JavaScript backend writes async functions (#1330): <c>async function</c> from ECMAScript 2017, a
/// generator driven by <c>$async</c> at ECMAScript 2015, <c>$whenAll</c> for WhenAll, and a refusal with
/// PLY1152 at ECMAScript 5. That node computes what .NET computes is the acceptance tests' (AsyncTests).
/// </summary>
[TestFixture]
internal sealed class JavaScriptAsyncEmissionTests {

  private static string Emitted(IrModule module, string level) {
    var profile = JavaScriptProfile.Parse(level, "esm");
    Assert.That(LegalizedJavaScript.Check(module, profile), Is.Empty, level);
    var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{level}\n{text}");
    return text;
  }

  /// <summary>Test design 8: from ECMAScript 2017 on the native forms.</summary>
  [TestCase("es2017"), TestCase("es2020"), TestCase("es2022")]
  public void NativeLevel_WritesAsyncFunctions(string level) {
    var text = Emitted(AsyncFixtures.Imported(), level);
    Assert.That(text, Does.Contain("export async function Run(n) {"));
    Assert.That(text, Does.Contain("await Step(i)"));
    Assert.That(text, Does.Contain("await void 0;"));
    Assert.That(text, Does.Contain("await 5"));
    Assert.That(text, Does.Contain("new Int32Array(await $whenAll([Sq(2), Sq(3)]))"));
    Assert.That(text, Does.Not.Contain("$async").And.Not.Contain("function*"));
    Assert.That(text, Does.Contain(level == "es2017" ? "Promise.all(settled)" : "Promise.allSettled(promises)"));
    Assert.That(text, Does.Contain("Run(n: number): Promise<number>"));
  }

  /// <summary>Test design 8: ECMAScript 2015 drives a generator with $async.</summary>
  [Test]
  public void Es2015_DrivesAGenerator() {
    var text = Emitted(AsyncFixtures.Imported(), "es2015");
    Assert.That(text, Does.Contain("return $async(function* () {"));
    Assert.That(text, Does.Contain("(yield Step(i))"));
    Assert.That(text, Does.Contain("yield Promise.resolve();"));
    Assert.That(text, Does.Contain("yield Promise.resolve(5)"));
    Assert.That(text, Does.Contain("function $async(body) {"));
    Assert.That(text, Does.Not.Contain("async function").And.Not.Contain("await "));
  }

  /// <summary>Test design 8 and 14: ECMAScript 5 refuses the fixture with PLY1152, and with nothing else.</summary>
  [TestCase("cjs")]
  public void Es5_RefusesAsyncFunctions(string format) {
    var problems = LegalizedJavaScript.Check(AsyncFixtures.Imported(), JavaScriptProfile.Parse("es5", format));
    Assert.That(problems.Select(problem => problem.ToString()), Is.EqualTo(new[] { "error PLY1152: 'async function' cannot be expressed in JavaScript profile 'es5'" }));
  }

  /// <summary>Every helper of async functions has a text at the levels that have promises.</summary>
  [TestCase("es2015"), TestCase("es2020")]
  public void Helpers_HaveTheirText(string level) {
    var sources = JavaScriptEmitter.HelperSources(JavaScriptProfile.Parse(level, "esm")).ToDictionary(pair => pair.Name, pair => pair.Source);
    Assert.That(sources["$async"], Does.StartWith("function $async(body) {"));
    Assert.That(sources["$whenAll"], Does.Contain(level == "es2020" ? "Promise.allSettled" : "Promise.all(settled)"));
  }

  /// <summary>A WhenAll of tasks without a result awaits the helper and keeps no array.</summary>
  [Test]
  public void WhenAllWithoutResults_KeepsNoArray() {
    var text = Emitted(AsyncFixtures.Imported(), "es2022");
    Assert.That(text, Does.Contain("await $whenAll([Touch(), Touch()]);"));
  }

  [Test]
  public void Table_HasTheRowsOfAsync() {
    var table = JavaScriptCapabilities.Standard;
    Assert.That(table["Promise"].Since, Is.EqualTo(2015));
    Assert.That(table["Promise"].Replacement, Is.Null);
    Assert.That((table["async function"].Since, table["async function"].Replacement), Is.EqualTo((2017, "$async")));
    Assert.That((table["Promise.allSettled"].Since, table["Promise.allSettled"].Replacement), Is.EqualTo((2020, "Promise.all")));
  }

}
