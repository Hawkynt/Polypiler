using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Optional;

/// <summary>
/// The JavaScript frontend on <c>null</c> and <c>undefined</c>: the fixture imported and run by the
/// interpreter against what node 22.23 printed for the same calls (captured once, written down here;
/// the acceptance tests run node itself), the spelling of an absent result, and what is refused.
/// </summary>
[TestFixture]
public sealed class JavaScriptOptionalImportTests {

  private static IrModule Imported(string file) {
    var result = JavaScriptFrontend.Import(file, File.ReadAllText(OptionalFixtures.PathOf(file)));
    return result.Module ?? throw new System.InvalidOperationException(string.Join("\n", result.Diagnostics));
  }

  private static IrFunction Function(string name) => Imported("find.js").Functions.Single(function => function.Name == name);

  [Test]
  public void Fixture_ImportsIntoWellTypedIr() {
    var module = Imported("find.js");
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(Function("find").ReturnType, Is.EqualTo(IrType.Optional(IrType.F64)));
    Assert.That(Function("coerced").Locals.Single(local => local.Name == "v").Type, Is.EqualTo(IrType.Optional(IrType.F64)));
  }

  /// <summary>Test design 1, 4 and 5: the interpreter computes what node computes; none crosses back as null where node has null or undefined.</summary>
  [TestCase("findOr", "6", "2")]
  [TestCase("findOr", "7", "-1")]
  [TestCase("next", null, "-1")]
  [TestCase("next", 2.0, "3")]
  [TestCase("isNull", null, "true")]
  [TestCase("isNull", 0.0, "false")]
  [TestCase("or5", 0.0, "0")]
  [TestCase("or5", null, "5")]
  [TestCase("assigned", "", "7")]
  [TestCase("length", "null", "-1")]
  [TestCase("length", "3", "3")]
  [TestCase("coerced", -3.0, "1")]
  [TestCase("coerced", 2.0, "3")]
  [TestCase("missing", -3.0, "NaN")]
  [TestCase("missing", 2.0, "3")]
  [TestCase("positive", 0.0, "void")]
  [TestCase("positive", 2.0, "2")]
  [TestCase("either", 0.0, "void")]
  [TestCase("nothing", 0.0, "void")]
  [TestCase("scaled", 2.0, "8")]
  [TestCase("truthy", -1.0, "0")]
  [TestCase("truthy", 3.0, "1")]
  [TestCase("truthy", 0.5, "1")]
  public void Interpreter_AgreesWithNode(string function, object? argument, string expected) {
    object?[] arguments = function switch {
      "findOr" => [new[] { 4, 5, 6 }, double.Parse((string)argument!, System.Globalization.CultureInfo.InvariantCulture)],
      "assigned" => [],
      "length" => [(string)argument! == "null" ? null : new int[3]],
      _ => [argument]
    };
    var outcome = PrototypeFixture.InterpretedOutcome(Imported("find.js"), new PrototypeCall(function, arguments!));
    Assert.That(outcome.Split(" | ")[0], Is.EqualTo(expected));
  }

  /// <summary>Test design 4: the spelling of the absent result is the one the source returns.</summary>
  [TestCase("find", IrAbsent.Null)]
  [TestCase("positive", IrAbsent.Undefined)]
  [TestCase("either", IrAbsent.Undefined)]
  [TestCase("nothing", IrAbsent.Undefined)]
  [TestCase("assigned", IrAbsent.Null)]
  public void AbsentResult_IsTheSpellingOfTheSource(string function, IrAbsent absent) => Assert.That(Function(function).Absent, Is.EqualTo(absent));

  /// <summary>Test design 4: both absent values told apart are refused with PLY1105.</summary>
  [Test]
  public void BothAbsentValuesToldApart_AreRefused() {
    var result = JavaScriptFrontend.Import("absent.js", File.ReadAllText(OptionalFixtures.PathOf("absent.js")));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()), Is.EqualTo(new[] { "absent.js(4,31): error PLY1105: 'x' holds both null and undefined, and the program tells them apart" }));
  }

  private static IReadOnlyList<string> Problems(string source) {
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    return result.Module is null ? [.. result.Diagnostics.Select(diagnostic => diagnostic.Message)] : [];
  }

  /// <summary>Test design 10: what stays outside the subset is refused, naming it.</summary>
  [TestCase("/** @param {Int32Array} o */ export function f(o) { return o?.[1] ?? 0; }", "obj?.[key]")]
  [TestCase("/** @param {Int32Array} a */ export function f(a) { return a?.length?.x; }", "?. on number")]
  [TestCase("export function f(x) { const y = x == null ? 1 : 2; return x + y; }", "which may be null or undefined")]
  [TestCase("export function f(n) { const s = n > 0 ? \"a\" : null; return 1; }", "a string that may be null")]
  [TestCase("export function f(n) { let x = n; x ??= 1; return 1; }", "")]
  [TestCase("export function f(n) { const o = { a: n }; o.a ??= 1; return 1; }", "??= of anything but a variable")]
  [TestCase("export function f(n) { return n === undefined ? 1 : n === null ? 2 : 3; }", "holds both null and undefined")]
  public void Construct_IsRefusedOrImported(string source, string named) {
    var problems = Problems(source);
    if (named.Length == 0)
      Assert.That(problems, Is.Empty);
    else
      Assert.That(problems, Has.Some.Contains(named), string.Join("\n", problems));
  }

  /// <summary>A test proves a variable present where it holds: behind it in a branch, an else, or behind &amp;&amp; and ||.</summary>
  [TestCase("export function f(x) { if (x != null) return x + 1; return 0; }", 2.0, "3")]
  [TestCase("export function f(x) { if (x == null) { return 0; } else { return x * 2; } }", 2.0, "4")]
  [TestCase("export function f(x) { return x != null && x > 1 ? 1 : 0; }", 2.0, "1")]
  [TestCase("export function f(x) { return x == null || x > 1 ? 1 : 0; }", null, "1")]
  [TestCase("export function f(x) { return x !== undefined ? x + 1 : -1; }", null, "-1")]
  [TestCase("export function f(x) { return x === null ? -1 : x + 1; }", 1.0, "2")]
  public void Narrowing_LetsAPresentValueCompute(string source, object? argument, string expected) {
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    Assert.That(result.Module, Is.Not.Null, string.Join("\n", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    Assert.That(PrototypeFixture.InterpretedOutcome(result.Module!, new PrototypeCall("f", [argument!])), Is.EqualTo(expected));
  }

  /// <summary>A missing argument is undefined; a test for null of a value that never is one is false.</summary>
  [Test]
  public void MissingArgument_IsUndefined() {
    var result = JavaScriptFrontend.Import("snippet.mjs", "function g(a, b) { return b === undefined ? a : a + b; } export function f(x) { return g(x) + g(x, 1); }");
    Assert.That(result.Module, Is.Not.Null, string.Join("\n", result.Diagnostics));
    Assert.That(PrototypeFixture.InterpretedOutcome(result.Module!, new PrototypeCall("f", [2.0])), Is.EqualTo("5"));
    var never = JavaScriptFrontend.Import("snippet.mjs", "/** @param {int32} a */ export function f(a) { return a === null ? 1 : 2; }");
    Assert.That(PrototypeFixture.InterpretedOutcome(never.Module!, new PrototypeCall("f", [3])), Is.EqualTo("2"));
    var tooFew = JavaScriptFrontend.Import("snippet.mjs", "/** @param {int32} b */ function g(a, b) { return b; } export function f(x) { return g(x); }");
    Assert.That(tooFew.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains("with 1 arguments for 2 parameters"));
  }

}
