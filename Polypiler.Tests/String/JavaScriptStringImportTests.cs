using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// The JavaScript constructs on strings the frontend imports, each interpreted against what node
/// printed for the same calls of the original function (recorded here; the acceptance tests run node
/// on the fixtures themselves), and the constructs it refuses by name: every row of
/// <see cref="JavaScriptStringApi"/>.
/// </summary>
[TestFixture]
public sealed class JavaScriptStringImportTests {

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Not.Null, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>One function per supported row of the table, with its calls and what node printed for each.</summary>
  private static readonly (string Row, string Source, (object[] Arguments, string Node)[] Calls)[] _cases = [
    ("string literal", "export function f() { return \"a\\u00e9\\\\\\\"\"; }", [(new object[] {  }, "a\u00E9\\\"")]),
    ("template literal", "/** @param {string} s */ export function f(s) { return `<${s}|${s}>`; }", [(new object[] { "x" }, "<x|x>")]),
    ("s.length", "/** @param {string} s */ export function f(s) { return s.length; }", [(new object[] { "a\U0001F600" }, "3"), (new object[] { "" }, "0")]),
    ("s.charCodeAt(i)", "/** @param {string} s @param {int32} i */ export function f(s, i) { return s.charCodeAt(i); }", [(new object[] { "abc", 1 }, "98"), (new object[] { "abc", 3 }, "NaN"), (new object[] { "abc", -1 }, "NaN")]),
    ("s[i]", "/** @param {string} s @param {int32} i */ export function f(s, i) { return s[i]; }", [(new object[] { "abc", 1 }, "b"), (new object[] { "abc", 0 }, "a")]),
    ("s.charAt(i)", "/** @param {string} s @param {int32} i */ export function f(s, i) { return s.charAt(i); }", [(new object[] { "abc", 1 }, "b"), (new object[] { "abc", 3 }, ""), (new object[] { "abc", -1 }, "")]),
    ("s.at(i)", "/** @param {string} s @param {int32} i */ export function f(s, i) { return s.at(i); }", [(new object[] { "abc", -1 }, "c"), (new object[] { "abc", 0 }, "a")]),
    ("a + b", "/** @param {string} a @param {string} b */ export function f(a, b) { return a + b + a; }", [(new object[] { "x", "y" }, "xyx")]),
    ("a += b", "/** @param {string} a @param {string} b */ export function f(a, b) { let r = a; r += b; return r; }", [(new object[] { "x", "y" }, "xy")]),
    ("s.concat(...)", "/** @param {string} a @param {string} b */ export function f(a, b) { return a.concat(b, a); }", [(new object[] { "x", "y" }, "xyx")]),
    ("===, !==, ==, !=", "/** @param {string} a @param {string} b */ export function f(a, b) { return a === b && !(a !== b) && a == b && !(a != b); }", [(new object[] { "x", "x" }, "true"), (new object[] { "x", "X" }, "false")]),
    ("<, <=, >, >=", "/** @param {string} a @param {string} b */ export function f(a, b) { return a < b ? 1 : a > b ? 2 : a <= b && a >= b ? 3 : 4; }", [(new object[] { "a", "B" }, "2"), (new object[] { "B", "a" }, "1"), (new object[] { "a", "a" }, "3"), (new object[] { "a", "ab" }, "1")]),
    ("s.substring(a[, b])", "/** @param {string} s @param {int32} a @param {int32} b */ export function f(s, a, b) { return s.substring(a, b) + \"|\" + s.substring(a); }", [(new object[] { "abcdef", 4, 1 }, "bcd|ef"), (new object[] { "abc", 2, 7 }, "c|c"), (new object[] { "abc", -3, 2 }, "ab|abc")]),
    ("s.slice(a[, b])", "/** @param {string} s @param {int32} a @param {int32} b */ export function f(s, a, b) { return s.slice(a, b) + \"|\" + s.slice(a); }", [(new object[] { "abcdef", -2, 6 }, "ef|ef"), (new object[] { "abcdef", 4, 1 }, "|ef"), (new object[] { "abcdef", 1, -1 }, "bcde|bcdef")]),
    ("s.indexOf(n[, from])", "/** @param {string} s @param {string} n @param {int32} i */ export function f(s, n, i) { return s.indexOf(n) * 100 + s.indexOf(n, i); }", [(new object[] { "abcabc", "bc", 2 }, "104"), (new object[] { "abc", "", 9 }, "3"), (new object[] { "abc", "x", 0 }, "-101")]),
    ("s.lastIndexOf(n[, from])", "/** @param {string} s @param {string} n @param {int32} i */ export function f(s, n, i) { return s.lastIndexOf(n) * 100 + s.lastIndexOf(n, i); }", [(new object[] { "abcabc", "bc", 3 }, "401"), (new object[] { "abc", "", 1 }, "301"), (new object[] { "abc", "x", -5 }, "-101")]),
    ("s.includes(n)", "/** @param {string} s @param {string} n */ export function f(s, n) { return s.includes(n); }", [(new object[] { "abc", "bc" }, "true"), (new object[] { "abc", "x" }, "false")]),
    ("s.startsWith(n)", "/** @param {string} s @param {string} n */ export function f(s, n) { return s.startsWith(n); }", [(new object[] { "abc", "ab" }, "true"), (new object[] { "e\u0301", "e" }, "true")]),
    ("s.endsWith(n)", "/** @param {string} s @param {string} n */ export function f(s, n) { return s.endsWith(n); }", [(new object[] { "abc", "bc" }, "true"), (new object[] { "abc", "b" }, "false")]),
    ("s.replace(string, string)", "/** @param {string} s */ export function f(s) { return s.replace(\"b\", \"[$&$$]\"); }", [(new object[] { "abcb" }, "a[b$]cb"), (new object[] { "xyz" }, "xyz")]),
    ("s.replaceAll(string, string)", "/** @param {string} s */ export function f(s) { return s.replaceAll(\"b\", \"XY\"); }", [(new object[] { "abcb" }, "aXYcXY")]),
    ("s.split(string[, limit])", "/** @param {string} s */ export function f(s) { return s.split(\",\").length * 10 + s.split(\",\", 2).length; }", [(new object[] { "a,b,c" }, "32"), (new object[] { "" }, "11")]),
    ("a.join(sep)", "/** @param {string} s */ export function f(s) { return s.split(\"\").join(\";\"); }", [(new object[] { "abc" }, "a;b;c"), (new object[] { "" }, "")]),
    ("s.trim()", "/** @param {string} s */ export function f(s) { return s.trim().length; }", [(new object[] { "\uFEFFa\uFEFF" }, "1"), (new object[] { "\u0085a\u0085" }, "3")]),
    ("s.trimStart()", "/** @param {string} s */ export function f(s) { return s.trimStart().length; }", [(new object[] { " a " }, "2")]),
    ("s.trimEnd()", "/** @param {string} s */ export function f(s) { return s.trimEnd().length; }", [(new object[] { " a " }, "2")]),
    ("s.padStart(n[, f])", "/** @param {string} s */ export function f(s) { return s.padStart(5, \"ab\"); }", [(new object[] { "x" }, "ababx"), (new object[] { "abcdef" }, "abcdef")]),
    ("s.padEnd(n[, f])", "/** @param {string} s */ export function f(s) { return s.padEnd(4) + \"|\"; }", [(new object[] { "x" }, "x   |")]),
    ("s.repeat(n)", "/** @param {string} s @param {int32} i */ export function f(s, i) { return s.repeat(i); }", [(new object[] { "ab", 3 }, "ababab"), (new object[] { "ab", 0 }, "")]),
    ("String.fromCharCode(...)", "/** @param {int32} c */ export function f(c) { return String.fromCharCode(c, c + 1); }", [(new object[] { 65 }, "AB"), (new object[] { 65601 }, "AB")]),
    ("s.codePointAt(i)", "/** @param {string} s @param {int32} i */ export function f(s, i) { return s.codePointAt(i); }", [(new object[] { "a\U0001F600", 1 }, "128512"), (new object[] { "a\U0001F600", 2 }, "56832")]),
    ("for (const ch of s)", "export function f(s) { let n = 0; for (const ch of s) { if (ch === \"b\") continue; n++; } return n; }", [(new object[] { "a\U0001F600b" }, "2"), (new object[] { "" }, "0")]),
    ("switch on a string", "/** @param {string} s */ export function f(s) { switch (s) { case \"a\": case \"b\": return 1; case \"c\": return 2; case \"a\": return 9; default: return 0; } }", [(new object[] { "a" }, "1"), (new object[] { "b" }, "1"), (new object[] { "c" }, "2"), (new object[] { "z" }, "0")])
  ];

  private static IEnumerable<TestCaseData> Cases() => _cases.Select(entry => new TestCaseData(entry.Source, entry.Calls).SetName("Imports: " + entry.Row));

  /// <summary>Test design 2: every supported row computes in the interpreter what node computed.</summary>
  [TestCaseSource(nameof(Cases))]
  public void Row_IsImported_AndComputesWhatNodeComputes(string source, (object[] Arguments, string Node)[] calls) {
    var module = Import(source);
    foreach (var (arguments, node) in calls)
      Assert.That(PrototypeFixture.Show(IrInterpreter.Call(module, "f", arguments)), Is.EqualTo(node), string.Join(", ", arguments.Select(PrototypeFixture.Show)));
  }

  [Test]
  public void EverySupportedRow_HasACase()
    => Assert.That(_cases.Select(entry => entry.Row), Is.EquivalentTo(JavaScriptStringApi.Supported.Select(member => member.Construct)));

  [Test]
  public void EveryRefusedRow_HasAReason() {
    Assert.That(JavaScriptStringApi.Refused.Select(member => member.Refusal), Has.All.Not.Empty);
    Assert.That(JavaScriptStringApi.Members.Select(member => member.Construct), Is.Unique);
  }

  /// <summary>A parameter without JSDoc that calls a method only strings have, or that a for...of goes over, is a string.</summary>
  [TestCase("export function f(s) { return s.trim(); }")]
  [TestCase("export function f(s) { let n = 0; for (const ch of s) n++; return n; }")]
  public void Parameter_UsedAsAString_IsAString(string source)
    => Assert.That(Import(source).Functions[0].Parameters[0].Type, Is.EqualTo(IrType.String));

  [Test]
  public void ModuleConstants_OfStringsAreStrings() {
    var module = Import("const A = \"x\"; const T = [\"p\", \"q\"]; /** @param {int32} i */ export function f(i) { return A + T[i]; }");
    Assert.That(module.Constants.Select(constant => (constant.Name, constant.Type.ToString(), string.Join(",", constant.Strings))), Is.EqualTo(new[] { ("A", "str", "x"), ("T", "str[]", "p,q") }));
    Assert.That(IrInterpreter.Call(module, "f", [1]), Is.EqualTo("xq"));
  }

  /// <summary>Test design 9: every refused construct, by name, with its reason; nothing is written.</summary>
  [TestCase("export function f(s) { return s.toUpperCase(); }", "toUpperCase, which is case mapping")]
  [TestCase("export function f(s) { return s.toLowerCase(); }", "toLowerCase, which is case mapping")]
  [TestCase("/** @param {string} s */ export function f(s) { return s.localeCompare(\"a\"); }", "localeCompare, which depends on the locale")]
  [TestCase("/** @param {string} s */ export function f(s) { return s.normalize(); }", "normalize")]
  [TestCase("/** @param {string} s */ export function f(s) { return String.raw`a${s}`; }", "the tagged template String.raw")]
  [TestCase("/** @param {string} s */ export function f(s) { return s.replace(/b/g, \"x\"); }", "replace with a regular expression")]
  [TestCase("/** @param {string} s */ export function f(s) { return s.replace(\"b\", \"$`\"); }", "replace with the pattern $` in its replacement")]
  [TestCase("/** @param {string} s */ export function f(s) { return s.replace(\"b\", \"$1\"); }", "replace with the pattern $1 in its replacement")]
  [TestCase("/** @param {string} s */ export function f(s) { return s.replace(\"b\", s); }", "replace with a replacement that is no constant, which may hold a $ pattern")]
  [TestCase("/** @param {string} s */ export function f(s) { return s.replaceAll(s, \"x\"); }", "replaceAll with a needle that may be empty")]
  [TestCase("/** @param {string} s */ export function f(s) { return s.replaceAll(\"\", \"x\"); }", "replaceAll with a needle that may be empty")]
  [TestCase("/** @param {int32} c */ export function f(c) { return String.fromCodePoint(c); }", "String.fromCodePoint")]
  [TestCase("export function f() { return new String(\"x\"); }", "new String")]
  [TestCase("/** @param {string} s */ export function f(s) { return s + 1; }", "+ on string and number")]
  [TestCase("/** @param {number} n */ export function f(n) { return `${n}`; }", "number as a template hole, which converts a number to text, not supported until number-to-text conversion")]
  [TestCase("/** @param {string} s */ export function f(s) { return s.match(\"a\"); }", "match")]
  [TestCase("/** @param {string} s */ export function f(s) { return s.lastIndexOf(\"a\", 0.5); }", "lastIndexOf from number, which is no proven integer")]
  [TestCase("/** @param {string} s */ export function f(s) { return s.includes(\"a\", 1); }", "includes with 2 arguments")]
  public void Construct_IsRefusedByName(string source, string what) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Has.Some.EqualTo((JavaScriptFrontend.Unsupported, $"'{what}' is not supported yet")));
  }

}
