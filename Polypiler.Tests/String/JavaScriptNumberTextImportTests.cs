using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// The JavaScript constructs that convert numbers and truth values to text and back (#1315), each
/// interpreted against what node computes for the same calls (written here; the acceptance tests run
/// node on the fixtures themselves), and the constructs the frontend refuses by name: every row of
/// <see cref="JavaScriptNumberTextApi"/>.
/// </summary>
[TestFixture]
public sealed class JavaScriptNumberTextImportTests {

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Not.Null, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>One function per supported row of the table, with its calls and what node returns for each.</summary>
  private static readonly (string Row, string Source, (object[] Arguments, object Node)[] Calls)[] _cases = [
    ("\"…\" + n, n + \"…\", template literal hole, String(n), n.toString(), n.toString(10)",
      "/** @param {number} x @param {int32} n @param {uint64} b */ export function f(x, n, b) { return \"v=\" + x + \"|\" + n + `|${b}|` + String(x) + x.toString() + n.toString(10) + (x + \"!\"); }",
      [(new object[] { 1e21, -3, ulong.MaxValue }, "v=1e+21|-3|18446744073709551615|1e+211e+21-31e+21!"), (new object[] { -0.0, 0, 0UL }, "v=0|0|0|0000!"),
       (new object[] { 1.5e-6, 7, 1UL }, "v=0.0000015|7|1|0.00000150.000001570.0000015!")]),
    ("\"…\" + b, String(b), b.toString()", "/** @param {boolean} b */ export function f(b) { return \"b=\" + b + String(b) + b.toString(); }",
      [(new object[] { true }, "b=truetruetrue"), (new object[] { false }, "b=falsefalsefalse")]),
    ("n.toString(radix) of a proven integer or a BigInt, radix a literal from 2 to 36",
      "/** @param {int32} n @param {int64} b */ export function f(n, b) { return n.toString(16) + \"|\" + n.toString(2) + \"|\" + b.toString(36) + \"|\" + (255).toString(16); }",
      [(new object[] { -1, long.MinValue }, "-1|-1|-1y2p0ij32e8e8|ff"), (new object[] { 255, 35L }, "ff|11111111|z|ff")]),
    ("n.toFixed(d), d a literal from 0 to 100", "/** @param {number} x */ export function f(x) { return x.toFixed(2) + \"|\" + x.toFixed(0) + \"|\" + x.toFixed(); }",
      [(new object[] { 0.125 }, "0.13|0|0"), (new object[] { 2.5 }, "2.50|3|3"), (new object[] { -0.0 }, "0.00|0|0"), (new object[] { 1e21 }, "1e+21|1e+21|1e+21")]),
    ("Number(s), +s", "/** @param {string} s */ export function f(s) { return Number(s) + +s; }",
      [(new object[] { "0x10" }, 32.0), (new object[] { "" }, 0.0), (new object[] { "1,000" }, double.NaN), (new object[] { " 2.5 " }, 5.0)]),
    ("parseFloat(s)", "export function f(s) { return parseFloat(s); }", [(new object[] { "12px" }, 12.0), (new object[] { "-.5e1x" }, -5.0), (new object[] { "x" }, double.NaN)]),
    ("parseInt(s[, radix]), radix a literal", "/** @param {string} s */ export function f(s) { return parseInt(s) + parseInt(s, 16) * 1000; }",
      [(new object[] { "0x1A" }, 26026.0), (new object[] { "10" }, 16010.0), (new object[] { "z" }, double.NaN)])
  ];

  private static IEnumerable<TestCaseData> Cases() => _cases.Select(entry => new TestCaseData(entry.Source, entry.Calls).SetName("Imports: " + entry.Row));

  /// <summary>Test design 1: every supported row computes in the interpreter what node computes.</summary>
  [TestCaseSource(nameof(Cases))]
  public void Row_IsImported_AndComputesWhatNodeComputes(string source, (object[] Arguments, object Node)[] calls) {
    var module = Import(source);
    foreach (var (arguments, node) in calls)
      Assert.That(PrototypeFixture.Show(IrInterpreter.Call(module, "f", arguments)), Is.EqualTo(PrototypeFixture.Show(node)), string.Join(", ", arguments.Select(PrototypeFixture.Show)));
  }

  [Test]
  public void EverySupportedRow_HasACase()
    => Assert.That(_cases.Select(entry => entry.Row), Is.EquivalentTo(JavaScriptNumberTextApi.Supported.Select(member => member.Construct)));

  [Test]
  public void EveryRefusedRow_HasAReason() {
    Assert.That(JavaScriptNumberTextApi.Refused.Select(member => member.Refusal), Has.All.Not.Empty);
    Assert.That(JavaScriptNumberTextApi.Members.Select(member => member.Construct), Is.Unique);
  }

  /// <summary>A parameter parsed by Number, parseFloat or parseInt is text, and a local of the name hides the global.</summary>
  [Test]
  public void Parser_MakesItsArgumentText() {
    Assert.That(Import("export function f(s) { return Number(s); }").Functions[0].Parameters[0].Type, Is.EqualTo(IrType.String));
    Assert.That(Import("export function f(s) { return parseInt(s, 2); }").Functions[0].Parameters[0].Type, Is.EqualTo(IrType.String));
    var hidden = Import("function parseFloat(x) { return x * 2; } export function f(s) { return parseFloat(s); }");
    Assert.That(hidden.Functions.Single(function => function.Name == "f").Parameters[0].Type, Is.EqualTo(IrType.F64));
  }

  /// <summary>Test design 10: every refused construct, by name, with its reason.</summary>
  [TestCase("/** @param {number} n */ export function f(n) { return n.toString(16); }", "toString with a radix of a number with a fraction")]
  [TestCase("/** @param {int32} n @param {int32} r */ export function f(n, r) { return n.toString(r); }", "Number.prototype.toString with a radix that is no integer literal from 2 to 36")]
  [TestCase("/** @param {int32} n */ export function f(n) { return n.toString(37); }", "Number.prototype.toString with a radix that is no integer literal from 2 to 36")]
  [TestCase("/** @param {number} n @param {int32} d */ export function f(n, d) { return n.toFixed(d); }", "toFixed with digits that are no integer literal from 0 to 100")]
  [TestCase("/** @param {number} n */ export function f(n) { return n.toFixed(101); }", "toFixed with digits that are no integer literal from 0 to 100")]
  [TestCase("/** @param {string} s @param {int32} r */ export function f(s, r) { return parseInt(s, r); }", "parseInt with a radix that is no integer literal")]
  [TestCase("/** @param {string} s */ export function f(s) { return parseInt(s, 16, 2); }", "parseInt with 3 arguments")]
  [TestCase("/** @param {number} n */ export function f(n) { return parseInt(n); }", "parseInt of number")]
  [TestCase("/** @param {number} n */ export function f(n) { return parseFloat(n); }", "parseFloat of number")]
  [TestCase("/** @param {string} s */ export function f(s) { return parseFloat(s, 2); }", "parseFloat with 2 arguments")]
  [TestCase("export function f() { return String(); }", "String with 0 arguments")]
  [TestCase("/** @param {number} n */ export function f(n) { return n.toPrecision(3); }", "toPrecision, which is a format of its own")]
  [TestCase("/** @param {number} n */ export function f(n) { return n.toExponential(3); }", "toExponential, which is a format of its own")]
  [TestCase("/** @param {number} n */ export function f(n) { return n.toLocaleString(); }", "toLocaleString, which is a format of its own")]
  [TestCase("/** @param {boolean} b */ export function f(b) { return b.toString(2); }", "toString with 1 arguments")]
  [TestCase("/** @param {string} s */ export function f(s) { return s.indexOf(1); }", "number as an argument of indexOf, which JavaScript converts to text first: pass String(n)")]
  public void Construct_IsRefusedByName(string source, string what) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Has.Some.EqualTo((JavaScriptFrontend.Unsupported, $"'{what}' is not supported yet")));
  }

}
