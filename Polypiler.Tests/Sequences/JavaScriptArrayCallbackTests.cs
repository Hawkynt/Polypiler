using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// JavaScript's array callbacks imported (#1329): the interpreter computes of the fixtures what node
/// computes of them - the values below were measured with node and are compared in the acceptance
/// tests against node itself - every construct of the table is lowered or refused by name, and both
/// backends write the fixtures in every profile.
/// </summary>
[TestFixture]
internal sealed class JavaScriptArrayCallbackTests {

  private static ImportResult Import(string source) => JavaScriptFrontend.Import("snippet.js", source);

  private static IrModule Imported(string source) {
    var result = Import(source);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()), Is.Empty);
    return result.Module!;
  }

  private static string Result(IrModule module, string function, params object[] arguments) {
    var outcome = PrototypeFixture.InterpretedOutcome(module, new PrototypeCall(function, arguments));
    return outcome.Split(" | ")[0];
  }

  /// <summary>Test design 1, 3, 7, 13, 14 and 17: what node computes of the fixtures, call by call.</summary>
  [TestCase("pipes.mjs", "trace", new[] { 1.0, 2, 3 }, "[1,2,3,-2,-4,-6]")]
  [TestCase("pipes.mjs", "total", new[] { 1.0, 2, 3 }, "10")]
  [TestCase("pipes.mjs", "firstOver", new[] { 1.0, 2, 3, 4 }, "2")]
  [TestCase("pipes.mjs", "firstOver", new double[0], "-1")]
  [TestCase("pipes.mjs", "found", new[] { 1.0, 5, 3 }, "5")]
  [TestCase("pipes.mjs", "found", new[] { 1.0 }, "-1")]
  [TestCase("pipes.mjs", "stable", new[] { 2.0, 1, 2, 1 }, "[1,3,0,2]")]
  [TestCase("runLength.mjs", "encode", new[] { 1.0, 1, 2, 3, 3, 3 }, "[1,2,2,1,3,3]")]
  [TestCase("runLength.mjs", "encode", new double[0], "[]")]
  [TestCase("runLength.mjs", "counted", new[] { 4.0, 4, 4, 5 }, "4")]
  public void Fixture_ComputesWhatNodeComputes(string file, string function, double[] argument, string expected)
    => Assert.That(Result(LinqFixtures.ImportedScript(file), function, argument), Is.EqualTo(expected));

  [TestCase("eager", "1111222234"), TestCase("nanSearch", "9"), TestCase("flat", "[1,10,2,20,3,30]"), TestCase("emptyReduce", "-1"), TestCase("single", "5")]
  [TestCase("emptyTests", "-99"), TestCase("indexed", "a0b1"), TestCase("pushDuring", "36"), TestCase("eagerThrow", "3"), TestCase("right", "321")]
  [TestCase("each", "8"), TestCase("whole", "1"), TestCase("byName", "12")]
  public void Fixture_WithoutArguments_ComputesWhatNodeComputes(string function, string expected)
    => Assert.That(Result(LinqFixtures.ImportedScript("pipes.mjs"), function), Is.EqualTo(expected));

  /// <summary>A comparator that returns a double is announced; one that returns an integer is not.</summary>
  [Test]
  public void DoubleComparator_IsAnnounced() {
    Assert.That(Import("export function f() { const a = [2, 1]; a.sort((x, y) => x - y); return a[0]; }").Caveats.Select(caveat => caveat.Id), Does.Contain(JavaScriptFrontend.SortOrder));
    Assert.That(Import("export function f() { const a = [2, 1]; a.sort((x, y) => x < y ? -1 : 1); return a[0]; }").Caveats.Select(caveat => caveat.Id), Does.Not.Contain(JavaScriptFrontend.SortOrder));
  }

  /// <summary>Test design 16: a construct outside the table is refused by name, and nothing is imported.</summary>
  [TestCase("export function f() { const a = [2, 1]; a.sort(); return a[0]; }", "sort() without a comparator")]
  [TestCase("export function f() { let n = 0; const a = [2, 1]; a.sort((x, y) => { n++; return x - y; }); return n; }", "a sort comparator with effects")]
  [TestCase("export function f() { let n = 0; const a = [2, 1]; a.sort((x, y) => { n = n + 1; return x - y; }); return n; }", "a sort comparator with effects")]
  [TestCase("function g(x, y) { return x - y; } export function f() { const a = [2, 1]; a.sort((x, y) => g(x, y)); return a[0]; }", "a sort comparator with effects")]
  [TestCase("function g(x, y) { return x - y; } export function f() { const a = [2, 1]; a.sort(g); return a[0]; }", "a sort comparator with effects")]
  [TestCase("export function f() { const b = [1]; const a = [2, 1]; a.sort((x, y) => { b.push(x); return x - y; }); return a[0]; }", "a sort comparator with effects")]
  [TestCase("export function f() { const b = [1, 2]; const a = [2, 1]; a.sort((x, y) => { b[0] = x; return x - y; }); return a[0]; }", "a sort comparator with effects")]
  [TestCase("export function f() { const a = [2, 1]; a.sort((x, y) => { let t = x; t++; return t - y; }); return a[0]; }", null)]
  [TestCase("export function f() { const a = [2, 1]; return a.findLast((x) => x > 1); }", "findLast of an array, which ECMAScript 2023 added")]
  [TestCase("export function f() { const a = [2, 1]; return a.findLastIndex((x) => x > 1); }", "findLastIndex of an array")]
  [TestCase("export function f() { const a = [2, 1]; a.copyWithin(0, 1); return a[0]; }", "copyWithin of an array")]
  [TestCase("export function f() { return [\"1\", \"2\"].map(parseInt)[0]; }", "as the callback of map, which is no function of the file")]
  [TestCase("export function f() { return [1, 2].map((x, i, a, z) => x)[0]; }", "a callback of map with more than 3 parameters")]
  [TestCase("export function f() { return [1, 2].some((x) => x > 1, null) ? 1 : 0; }", "some with a thisArg")]
  [TestCase("export function f() { return [1, 2].some() ? 1 : 0; }", "some without a callback")]
  [TestCase("export function f() { return new Int32Array(2).map((x) => x / 2)[0]; }", "map of")]
  [TestCase("export function f() { return [1, 2].filter((x) => \"a\").length; }", "a callback of filter that returns")]
  [TestCase("export function f() { return [1, 2].flatMap((x) => x).length; }", "a callback of flatMap that returns")]
  [TestCase("export function f() { const s = \"ab\"; return [1, 2].map((x, i, a) => s)[0]; }", null)]
  [TestCase("/** @param {string} s */ function g(s) { return s.length; } export function f() { return [1].map(g)[0]; }", "a callback of map whose parameter 1 is string, not")]
  [TestCase("export function f() { let n = 0; const a = [2, 1]; a.sort((x, y) => x - y + (n++) * 0); return a[0]; }", "a sort comparator with effects")]
  [TestCase("export function f() { const b = [0]; const a = [2, 1]; a.sort((x, y) => x - y + (b[0]++) * 0); return a[0]; }", "a sort comparator with effects")]
  [TestCase("export function f() { const v = [\"a\"].find((x) => x === \"a\"); return v === undefined ? 0 : 1; }", "a string that may be undefined")]
  [TestCase("export function f() { [\"a\"].find((x) => x === \"a\"); return 0; }", "whose undefined the IR has no value for")]
  public void Construct_IsRefusedByName(string source, string? what) {
    var result = Import(source);
    if (what is null) {
      Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()), Is.Empty);
      return;
    }

    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.All.EqualTo(JavaScriptFrontend.Unsupported));
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(what));
  }

  /// <summary>
  /// A list - an array whose length changes - is read by index up to the length read before the first
  /// call, and an index it no longer has is skipped as HasProperty skips it.
  /// </summary>
  [TestCase("export function f() { const a = [1, 2, 3]; a.push(4); return a.map((x) => { if (a.length > 2) { a.pop(); } return x; }).length; }", "4")]
  [TestCase("export function f() { const a = [1, 2, 3]; a.push(4); return a.filter((x) => { a.pop(); return true; }).length; }", "2")]
  [TestCase("export function f() { const a = [3, 1, 2]; a.push(0); a.sort((x, y) => x - y); return a[0] * 1000 + a[1] * 100 + a[2] * 10 + a[3]; }", "123")]
  [TestCase("export function f() { const a = [1, 2]; a.push(3); return a.reduceRight((s, x) => s * 10 + x); }", "321")]
  [TestCase("export function f() { const a = [1, 2]; a.push(3); return a.reduce((s, x, i, all) => s + x * i + all.length, 0); }", "17")]
  [TestCase("export function f() { const a = [5, 6]; a.push(7); let s = 0; a.forEach((x) => { s += x; a.pop(); }); return s; }", "11")]
  [TestCase("export function f() { const a = [1, 2]; a.push(3); return a.flatMap((x) => { const r = [x]; r.push(x); return r; }).length; }", "6")]
  [TestCase("export function f() { const a = [1, 2]; a.push(3); return (a.some((x) => x > 2) ? 10 : 0) + (a.every((x) => x > 0) ? 1 : 0) + a.findIndex((x) => x === 2) * 100; }", "111")]
  [TestCase("export function f() { const a = [1]; a.pop(); return a.reduceRight((s, x) => s + x, 4); }", "4")]
  [TestCase("export function f() { try { const a = [1]; a.pop(); return a.reduceRight((s, x) => s + x); } catch (e) { return e instanceof TypeError ? -1 : -2; } }", "-1")]
  [TestCase("export function f() { const a = [1, 2]; const v = a.find((x) => x % 2); return v === undefined ? 0 : v; }", "1")]
  [TestCase("export function f() { return [1.5, 0, 0 / 0].filter((x) => x).length + [1, 0].filter((x) => x | 0).length * 10; }", "11")]
  [TestCase("export function f() { return [\"b\", \"a\"].map((x) => x + \"!\")[1]; }", "a!")]
  [TestCase("export function f() { const r = [1, 2].map((x) => x * 2); r.push(5); return r.length * 10 + r[1]; }", "34")]
  [TestCase("export function f() { const r = [\"a\", \"b\"].map((x) => x + x); r.push(\"c\"); return r[1] + r[2]; }", "bbc")]
  [TestCase("export function f() { const r = [1, 2].filter((x) => x > 1); r.push(5); return r.length * 10 + r[0]; }", "22")]
  [TestCase("export function f() { return [1, 2].map((x) => [3, 4].map((y) => y * x).reduce((s, v) => s + v, 0))[1]; }", "14")]
  [TestCase("export function f() { let s = 0; [1, 2].forEach((x) => [x].forEach((y) => { s += y; })); return s; }", "3")]
  [TestCase("export function f() { return [\"a\", \"b\"].reduce((s, x) => s + x); }", "ab")]
  [TestCase("class P { constructor(v) { this.v = v; } } export function f() { const a = []; a.push(new P(1)); a.push(new P(5)); const p = a.find((x) => x.v > 2); const q = a.find((x) => x.v > 9); return (p === undefined ? -1 : p.v) * 10 + (q === undefined ? -1 : q.v); }", "49")]
  [TestCase("export function f() { const a = [2, 1, 3]; a.sort((x, y) => { let t = 0; return x - y + (t++) * 0; }); return a[0] * 100 + a[1] * 10 + a[2]; }", "123")]
  public void ListsAndValues_AreTakenAsJavaScriptTakesThem(string source, string expected)
    => Assert.That(Result(Imported(source), "f"), Is.EqualTo(expected));

  [Test]
  public void Table_ListsWhatIsImportedAndWhatIsRefused() {
    Assert.That(JavaScriptArrayApi.Supported.Count() + JavaScriptArrayApi.Refused.Count(), Is.EqualTo(JavaScriptArrayApi.Members.Count));
    Assert.That(JavaScriptArrayApi.Refusal("map"), Is.Null);
    Assert.That(JavaScriptArrayApi.Refusal("findLast"), Does.Contain("ECMAScript 2023"));
  }

  /// <summary>Test design 1 and 19: every C# profile writes the JavaScript fixtures, which compile there.</summary>
  [TestCase("pipes.mjs"), TestCase("runLength.mjs")]
  [Category("Slow")]
  public void EveryCSharpProfile_WritesTheFixture(string file) {
    var module = LinqFixtures.ImportedScript(file);
    foreach (var profile in CSharpProfile.All) {
      var legalized = CSharpEmitter.Legalize(module, () => Polypiler.Cli.PrecomputedImplementations.Module, profile, FloatMode.Accurate);
      Assert.That(CSharpEmitter.Check(legalized, profile), Is.Empty, profile.Name);
      var text = CSharpEmitter.Emit(legalized, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 1 and 19: every JavaScript level writes the JavaScript fixtures at that level.</summary>
  [TestCase("pipes.mjs"), TestCase("runLength.mjs")]
  public void EveryJavaScriptLevel_WritesTheFixture(string file) {
    var module = LinqFixtures.ImportedScript(file);
    foreach (var level in JavaScriptProfile.LevelNames) {
      var profile = JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm");
      var legalized = JavaScriptEmitter.Legalize(module, () => Polypiler.Cli.PrecomputedImplementations.Module, profile, FloatMode.Accurate);
      var text = JavaScriptEmitter.Emit(legalized, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{level}\n{text}");
    }
  }

}
