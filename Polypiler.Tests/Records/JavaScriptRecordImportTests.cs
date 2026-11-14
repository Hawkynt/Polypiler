using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Records;

/// <summary>
/// Object literals of fixed shape imported as value records: one struct per shape, named after the
/// first variable that holds it, and every construct that could tell a value from a reference refused.
/// </summary>
[TestFixture]
public sealed class JavaScriptRecordImportTests {

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static Diagnostic Refusal(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics, Has.Count.EqualTo(1), string.Join("; ", result.Diagnostics));
    return result.Diagnostics[0];
  }

  private static string Shapes(IrModule module)
    => string.Join("; ", module.Types.Cast<IrStructType>().Select(type => $"{type.Name}({string.Join(", ", type.Fields.Select(field => $"{field.Name}: {field.Type}{(field.IsReadOnly ? "" : " mutable")}"))})"));

  /// <summary>Test design 5: a literal, shorthand, destructuring, a parameter, a result and an array of records each make one struct of their shape.</summary>
  [TestCase("export function f(a) { const point = { x: a, y: 2 }; return point.x + point.y; }", "Point(x: f64, y: i32)", 3.0)]
  [TestCase("export function f(a) { const x = a; const y = true; const flag = { x, y }; return flag.y ? flag.x : 0; }", "Flag(x: f64, y: bool)", 1.0)]
  [TestCase("export function f(a) { const p = { x: a, y: a + 1 }; const { x, y: why } = p; return x * why; }", "P(x: f64, y: f64)", 2.0)]
  [TestCase("function g(pair) { return pair.left - pair.right; } export function f(a) { return g({ left: a, right: 3 }); }", "Pair(left: f64, right: i32)", -2.0)]
  [TestCase("function g(a) { return { v: a * 2 }; } export function f(a) { const { v } = g(a); return v; }", "V(v: f64)", 2.0)]
  [TestCase("export function f(a) { const all = [{ k: 1 }, { k: 2 }]; return all[1].k + a; }", "All(k: i32)", 3.0)]
  [TestCase("export function f(a) { const box = { inner: { x: a }, n: 1 }; return box.inner.x + box.n; }", "Inner(x: f64); Box(inner: Inner, n: i32)", 2.0)]
  [TestCase("export function f(a) { return ({ x: a }).x; }", "X(x: f64)", 1.0)]
  public void Shape_BecomesOneStruct(string source, string shapes, double expected) {
    var module = Import(source);
    Assert.That(Shapes(module), Is.EqualTo(shapes));
    Assert.That(IrInterpreter.Call(module, "f", [1.0]), Is.EqualTo(expected));
  }

  /// <summary>Two variables of one shape share a struct; another shape gets a struct of its own, and a name that is taken a number.</summary>
  [Test]
  public void Shapes_AreNamedOnce_AndANameTakenGetsANumber() {
    var module = Import("export function f(a) { const p = { x: a }; const q = { x: a + 1 }; const r = { x: true }; const s = { y: 1 }; "
      + "return (r.x ? p.x + q.x : 0) + s.y; }");
    Assert.That(Shapes(module), Is.EqualTo("P(x: f64); R(x: bool); S(y: i32)"));
    var clash = Import("export function f(a) { const p = { x: a }; return p.x; } export function h() { const p = { y: true }; return p.y; }");
    Assert.That(Shapes(clash), Is.EqualTo("P(x: f64); P2(y: bool)"));
  }

  /// <summary>A literal of integers given where a record of doubles is held is converted key by key: a new literal, or a variable read once per key.</summary>
  [Test]
  public void Record_IsConvertedToTheShapeOfItsPlace() {
    var module = Import("function len(v) { return v.x + v.y; } export function f(a) { const ints = { x: 1, y: 2 }; return len(ints) + len({ x: a, y: 0.5 }) + len({ x: 3, y: 4 }); }");
    Assert.That(IrInterpreter.Call(module, "f", [1.0]), Is.EqualTo(11.5));
    Assert.That(Shapes(module), Is.EqualTo("V(x: f64, y: f64); Ints(x: i32, y: i32)"));
  }

  /// <summary>Test design 11: every construct that would let a reference be told from a value, by name.</summary>
  [TestCase("export function f() { const p = { x: 1, y: 2 }; const q = p; q.x = 5; return p.x; }", "an object literal written after it is built (q.x)")]
  [TestCase("export function f() { const p = { x: 1 }; p.z = 5; return p.x; }", "a new property of an object literal (p.z)")]
  [TestCase("export function f() { const p = { x: 1 }; p.x++; return p.x; }", "an object literal written after it is built (p.x)")]
  [TestCase("export function f() { const p = { x: 1 }; delete p.x; return 1; }", "delete p.x as a statement")]
  [TestCase("export function f(a) { const p = { x: a }; const q = { x: a }; return p === q; }", "an object literal compared by identity (===)")]
  [TestCase("export function f(a) { const p = { x: a }; return p != p; }", "an object literal compared by identity (!=)")]
  [TestCase("export function f(k) { const p = { [k]: 1 }; return 1; }", "a computed key in an object literal")]
  [TestCase("export function f() { const p = { x: 1 }; const q = { ...p }; return q.x; }", "a spread in an object literal (...)")]
  [TestCase("export function f() { const p = { m() { return 1; } }; return 1; }", "a method in an object literal")]
  [TestCase("export function f() { const p = { get x() { return 1; } }; return 1; }", "a getter in an object literal")]
  [TestCase("export function f() { const p = { set x(v) { } }; return 1; }", "a setter in an object literal")]
  [TestCase("export function f() { const p = { 'x': 1 }; return 1; }", "a key that is no name in an object literal")]
  [TestCase("export function f() { const p = { x: 1, x: 2 }; return 1; }", "the key x twice in an object literal")]
  [TestCase("export function f() { const p = { x: [1, 2] }; return 1; }", "an object literal with a int32[] value")]
  [TestCase("export function f() { const p = { x: 1 }; return p.y; }", "the property y, which { x } does not have")]
  [TestCase("export function f() { const { y } = { x: 1 }; return y; }", "the property y, which { x } does not have")]
  [TestCase("export function f() { const { x = 2 } = { x: 1 }; return x; }", "a default value or a nested pattern in a destructuring declaration")]
  [TestCase("export function f() { const { ...rest } = { x: 1 }; return 1; }", "a spread in a destructuring declaration (...)")]
  [TestCase("export function f(a) { const { x } = a; return x; }", "a destructuring of number")]
  [TestCase("export function f() { const { x$ } = { x$: 1 }; return x$; }", "the name x$")]
  [TestCase("export function f() { const x = 1; { const { x } = { x: 2 }; } return x; }", "a second declaration of x")]
  [TestCase("export function f() { const p = { x: 1 }; return p; } export function g() { return f() + 1; }", "+ on { x } and number")]
  public void Construct_IsRefusedByName(string source, string what) {
    var refusal = Refusal(source);
    Assert.That((refusal.Code, refusal.Message), Is.EqualTo((JavaScriptFrontend.Unsupported, $"'{what}' is not supported yet")));
  }

  /// <summary>Test design 11: one variable reached by two shapes is refused as two kinds.</summary>
  [TestCase("export function f(a) { let p = { x: a }; p = { y: a }; return 1; }", "'p' holds both { x } and { y }")]
  [TestCase("export function f(a) { let p = { x: a }; p = { x: true }; return 1; }", "'p' holds both { x } and { x }")]
  [TestCase("export function f(a) { let p = { x: a }; p = 1; return 1; }", "'p' holds both { x } and number")]
  public void TwoShapesInOneVariable_AreRefused(string source, string message) {
    var refusal = Refusal(source);
    Assert.That((refusal.Code, refusal.Message), Is.EqualTo((JavaScriptFrontend.MixedKinds, message)));
  }

  /// <summary>A record holds numbers and truth values: a BigInt in one is refused, bounded or not.</summary>
  [TestCase("2n * 3n")]
  [TestCase("BigInt.asIntN(64, 2n)")]
  public void BigIntInARecord_IsRefused(string value)
    => Assert.That(Refusal($"export function f(a) {{ const p = {{ x: {value} }}; return 1; }}").Message, Is.EqualTo("'an object literal with a bigint value' is not supported yet"));

}
