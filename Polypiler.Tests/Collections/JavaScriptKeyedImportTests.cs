using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Collections;

/// <summary>
/// The JavaScript frontend on <c>Map</c> and <c>Set</c> (#1328): the fixture run by the interpreter
/// against what node 22.23 printed for the same calls (captured once, written down here; the
/// acceptance tests run node itself), the types, and what is refused.
/// </summary>
[TestFixture]
public sealed class JavaScriptKeyedImportTests {

  private static IReadOnlyList<string> Problems(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    return result.Module is null ? [.. result.Diagnostics.Select(diagnostic => diagnostic.Message)] : [];
  }

  private static string Outcome(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Not.Null, string.Join("\n", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return PrototypeFixture.InterpretedOutcome(result.Module!, new PrototypeCall("f", []));
  }

  /// <summary>Test design 1, 2, 3 and 4: the interpreter computes what node computes of the fixture.</summary>
  [TestCase("mapOrder", "2456"), TestCase("setOrder", "134"), TestCase("firstZeroKey", "f:7ff0000000000000"), TestCase("nanKey", "12"), TestCase("missing", "void")]
  [TestCase("liveMap", "134"), TestCase("clearedSet", "2098"), TestCase("hasDelete", "11"), TestCase("wordSet", "ba")]
  public void Fixture_ComputesWhatNodeComputes(string function, string outcome)
    => Assert.That(PrototypeFixture.InterpretedOutcome(CollectionFixtures.ImportedScript("collections.js"), new PrototypeCall(function, [])), Is.EqualTo(outcome));

  [Test]
  public void Count_IsTheFrequencyTableInInsertionOrder()
    => Assert.That(PrototypeFixture.InterpretedOutcome(CollectionFixtures.ImportedScript("collections.js"), new PrototypeCall("count", [new[] { 3.0, 1, 3, 2, 3 }])), Is.EqualTo("331121 | [3,1,3,2,3]"));

  [Test]
  public void MapsAndSets_AreOfTheInsertionOrder() {
    var module = CollectionFixtures.ImportedScript("collections.js");
    var locals = module.Functions.Single(function => function.Name == "mapOrder").Locals;
    Assert.That(locals.Single(local => local.Name == "m").Type, Is.EqualTo(IrType.Map(IrType.I32, IrType.I32, MapOrder.Insertion)));
    Assert.That(module.Functions.Single(function => function.Name == "wordSet").Locals.Single(local => local.Name == "s").Type, Is.EqualTo(IrType.Set(IrType.String, MapOrder.Insertion)));
    Assert.That(module.Functions.Single(function => function.Name == "missing").ReturnType, Is.EqualTo(IrType.Optional(IrType.F64)));
  }

  /// <summary>Entries, keys and values of a map and of a set, a set of an array or of a list, sets and maps of objects.</summary>
  [TestCase("export function f() { const m = new Map([[1, 2], [3, 4]]); let s = 0; for (const [k, v] of m.entries()) { s = s * 100 + k * 10 + v; } return s; }", "1234")]
  [TestCase("export function f() { const m = new Map([[1, 2]]); let s = 0; for (const v of m.values()) { s += v; } for (const k of m.keys()) { s += k * 10; } return s; }", "12")]
  [TestCase("export function f() { const s = new Set([1, 2]); let t = 0; for (const k of s.keys()) { t = t * 10 + k; } return t; }", "12")]
  [TestCase("export function f() { const a = new Int32Array(3); a[1] = 5; const s = new Set(a); return s.size * 10 + (s.has(5) ? 1 : 0); }", "21")]
  [TestCase("export function f() { const a = []; a.push(4); a.push(4); const s = new Set(a); return s.size; }", "1")]
  [TestCase("class K { constructor() { this.v = 1; } } export function f() { const k = new K(); const s = new Set(); s.add(k); s.add(new K()); s.add(k); return s.size; }", "2")]
  [TestCase("class K { constructor(v) { this.v = v; } } export function f() { const m = new Map(); m.set(1, new K(5)); const k = m.get(1); return k === null ? 0 : k.v; }", "5")]
  [TestCase("export function f() { const m = new Map(); m.set(1, 0.5); let s = 0; for (const [k, v] of m) { s = v; } return s * 10; }", "5")]
  [TestCase("export function f() { const s = new Set(); s.add(1); s.clear(); s.delete(1); return s.size; }", "0")]
  public void Construct_ComputesWhatNodeComputes(string source, string outcome) => Assert.That(Outcome(source), Is.EqualTo(outcome));

  /// <summary>Test design 11: what maps and sets do not take is refused by name.</summary>
  [TestCase("export function f() { const m = new Map(); m.set({ x: 1 }, 1); return m.size; }", "'a Map keyed by objects of an object literal' is not supported yet")]
  [TestCase("export function f() { const s = new Set(); s.add({ x: 1 }); return s.size; }", "a Set by objects of an object literal")]
  [TestCase("export function f() { const m = new Map(); m.set(1, \"a\"); return m.get(1); }", "a string that may be undefined")]
  [TestCase("export function f() { const m = new Map(); return m.set(1, 2).size; }", "m.set(1, 2).size")]
  [TestCase("export function f() { const m = new Map(); m.set(1, 2); m.forEach(x => x); return 0; }", "m.forEach of a Map")]
  [TestCase("export function f() { const m = new Map(); m.set(1, 2); for (const e of m) { } return 0; }", "a for...of over a Map that does not destructure its entry into [key, value]")]
  [TestCase("export function f() { const s = new Set(); s.add(1); for (const [a, b] of s) { } return 0; }", "a for...of that does not declare one variable")]
  [TestCase("export function f() { return new Map([[1, 2]]).size; }", "new Map with elements other than as the value of a variable")]
  [TestCase("export function f() { const m = new Map([1, 2]); return m.size; }", "an entry of a Map other than [key, value]")]
  [TestCase("export function f() { const s = new Set(\"ab\"); return s.size; }", "new Set of string")]
  [TestCase("export function f() { const m = new WeakMap(); return 0; }", "new WeakMap")]
  public void Construct_IsRefusedByName(string source, string named)
    => Assert.That(Problems(source), Has.Some.Contains(named));

}
