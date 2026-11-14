using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Globals;

/// <summary>
/// The variables of a JavaScript module the frontend imports as globals: typed like locals, from every
/// value that reaches them in any function and in their initializer; the construct table of #1316 row
/// by row; and the fixtures of <c>tests/fixtures/prototype/state</c> in the interpreter.
/// </summary>
[TestFixture]
public sealed class JavaScriptGlobalImportTests {

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static IReadOnlyList<string> Calls(string source, params string[] functions) {
    var instance = IrInterpreter.Instantiate(Import(source));
    return [.. functions.Select(function => PrototypeFixture.Show(instance.Call(function)))];
  }

  /// <summary>Test design 1: every JavaScript fixture computes in the interpreter, call after call in one instance, what the issue states.</summary>
  [TestCaseSource(typeof(StateFixtures), nameof(StateFixtures.All))]
  public void Fixture_RunsInTheInterpreter((string File, string Class, string Script) fixture) {
    var calls = StateFixtures.Sequence(fixture.File).Select(StateFixtures.Script);
    Assert.That(StateFixtures.Interpreted(StateFixtures.ImportedJavaScript(fixture.Script), calls), Is.EqualTo(StateFixtures.Expected(fixture.File)));
  }

  /// <summary>A variable of the module has the type of every value that reaches it, as a local has.</summary>
  [Test]
  public void TheVariablesOfTheFixtures_HaveTheTypesOfTheirValues() {
    var xorshift = StateFixtures.ImportedJavaScript("xorshift32");
    Assert.That(xorshift.Globals.Select(global => (global.Name, global.Type)), Is.EqualTo(new[] { ("state", IrType.U32), ("count", IrType.I32) }));
    Assert.That(StateFixtures.ImportedJavaScript("xorshift64").Globals.Single().Type, Is.EqualTo(IrType.U64));
    Assert.That(StateFixtures.ImportedJavaScript("lazy").Globals.Select(global => global.Type), Is.EqualTo(new[] { IrType.Array(IrType.U32), IrType.Bool }));
  }

  /// <summary>Construct table and test design 3: let, var and arrays of the module, with every kind of initializer, keep their values between calls.</summary>
  [TestCase("let x = 1; export function f() { x = (x + 1) | 0; return x; }", "2", "3")]
  [TestCase("var x = 1; export function f() { x = (x + 1) | 0; return x; }", "2", "3")]
  [TestCase("let x; export function f() { x = 5; return x; }", "5", "5")]
  [TestCase("let x = 0.5; export function f() { x *= 2; return x; }", "1", "2")]
  [TestCase("let x = 0; export function f() { x++; return x; }", "1", "2")]
  [TestCase("let x = 0; export function f() { return x++; }", "0", "1")]
  [TestCase("let x = 0; export function f() { return ++x; }", "1", "2")]
  [TestCase("let a = 2; let b = a * 3; export function f() { b = b + 1; return b; }", "7", "8")]
  [TestCase("const K = 4; let x = K + 1; export function f() { x = x + K; return x; }", "9", "13")]
  [TestCase("function twice(v) { return v * 2; } let x = twice(4); export function f() { x = twice(x); return x; }", "16", "32")]
  [TestCase("let on = !false; export function f() { on = !on; return on; }", "false", "true")]
  [TestCase("const t = new Uint8Array(2); export function f() { t[0] = t[0] + 255; return t[0]; }", "255", "254")]
  [TestCase("const t = [0, 0]; export function f() { t[1] = (t[1] + 2) | 0; return t[1]; }", "2", "4")]
  [TestCase("const t = new Int32Array([7, 8]); export function f() { t[0]++; return t[0]; }", "8", "9")]
  [TestCase("let s = 18446744073709551615n; export function f() { s = BigInt.asUintN(64, s + 1n); return s; }", "0", "1")]
  public void AVariableOfTheModule_KeepsItsValueBetweenCalls(string source, string first, string second)
    => Assert.That(Calls(source, "f", "f"), Is.EqualTo(new[] { first, second }));

  /// <summary>Test design 5: a local or a parameter of the name of a variable of the module hides it in its function, and only there.</summary>
  [Test]
  public void ALocalOfTheNameOfAVariable_HidesIt() {
    var module = Import("let x = 10; export function f() { let x = 1; return x; } export function g() { x = x + 1; return x; } /** @param {int32} x */ export function h(x) { return x; }");
    var instance = IrInterpreter.Instantiate(module);
    Assert.That((instance.Call("f"), instance.Call("g"), instance.Call("h", 3), instance.Call("g")), Is.EqualTo((1, 11, 3, 12)));
  }

  /// <summary>
  /// What becomes of a variable of the module that a function returns: a number is a global, an array
  /// one as well; one the file cannot type is refused as before.
  /// </summary>
  [TestCase("let C = 1;", "")]
  [TestCase("var C = 1;", "")]
  [TestCase("const C = new Uint8Array(4);", "")]
  [TestCase("const C = [];", "")]
  [TestCase("const C = [1, , 2];", "'an array literal with a hole or a spread element' is not supported yet")]
  [TestCase("const C = [1, x];", "'x' is not supported yet")]
  [TestCase("const C = [1, 2n];", "'int64 where float64 is expected' is not supported yet")]
  [TestCase("const C = new Uint8Array([1n]);", "'bigint as a number' is not supported yet")]
  [TestCase("const C = new BigInt64Array([1]);", "'number as an element of a int64 array' is not supported yet")]
  [TestCase("const T = [1, 2]; function g() { T[0] = 1; }", "")]
  [TestCase("const T = [1, 2]; function g() { T[0]++; }", "")]
  public void AVariableOfTheModule_ReturnedByAFunction(string declaration, string refused) {
    var result = JavaScriptFrontend.Import("snippet.js", declaration.Replace("T", "C") + " function f() { return C; }", ["f"]);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), refused.Length == 0 ? Is.Empty : Has.Some.EqualTo(refused));
    if (refused.Length == 0)
      Assert.That(result.Module!.Globals.Single().Name, Is.EqualTo("C"));
  }

  /// <summary>Test design 8: what the construct table refuses, each with its code and the name of what it is about.</summary>
  [TestCase("export let counter = 0; export function f() { return ++counter; }", JavaScriptFrontend.Unsupported, "'the exported variable counter' is not supported yet")]
  [TestCase("export var counter = 0; export function f() { return counter; }", JavaScriptFrontend.Unsupported, "'the exported variable counter' is not supported yet")]
  [TestCase("let counter = 0; export { counter }; export function f() { return counter; }", JavaScriptFrontend.Unsupported, "'the exported variable counter' is not supported yet")]
  [TestCase("const t = new Uint8Array(1); export default t; export function f() { return t[0]; }", JavaScriptFrontend.Unsupported, "'the exported variable t' is not supported yet")]
  [TestCase("let c = 0; c = 5; export function f() { return c; }", JavaScriptFrontend.Unsupported, "'a statement at the top level of the module' is not supported yet")]
  [TestCase("let c = 0; for (;;) { } export function f() { return c; }", JavaScriptFrontend.Unsupported, "'a statement at the top level of the module' is not supported yet")]
  [TestCase("let c = 0; if (c) { } export function f() { return c; }", JavaScriptFrontend.Unsupported, "'a statement at the top level of the module' is not supported yet")]
  [TestCase("let c = 0; f(); export function f() { return c; }", JavaScriptFrontend.Unsupported, "'a statement at the top level of the module' is not supported yet")]
  [TestCase("let a = b; let b = 1; export function f() { return a; }", JavaScriptFrontend.Unsupported, "'reading the variable b before its declaration' is not supported yet")]
  [TestCase("let y = 0; function g() { return y; } let x = g(); export function f() { return x; }", JavaScriptFrontend.Unsupported,
    "'an initializer that calls g, which reads or writes a variable of the module' is not supported yet")]
  [TestCase("let p = { x: 1 }; export function f() { return p.x; }", JavaScriptFrontend.Unsupported, "'an object in the variable p of the module' is not supported yet")]
  [TestCase("const t = new Uint8Array(2); export function f() { t = new Uint8Array(3); }", JavaScriptFrontend.Unsupported, "'an assignment to the constant t' is not supported yet")]
  [TestCase("const t = new Uint8Array(2); export function f() { t++; }", JavaScriptFrontend.MixedKinds, "'t' holds both number and uint8[]")]
  [TestCase("let x; export function f() { return x; }", JavaScriptFrontend.ReadBeforeAssignment, "'x' may be read before it is assigned")]
  [TestCase("let x; let y = x; export function f() { return y; }", JavaScriptFrontend.ReadBeforeAssignment, "'x' may be read before it is assigned")]
  [TestCase("let x = 1; export function f() { x = 1n; return 0; }", JavaScriptFrontend.MixedKinds, "'x' holds both bigint and number")]
  [TestCase("let s = 1n; export function f() { s = s + 1n; return 0; }", JavaScriptFrontend.NotBounded, "the result of 's + 1n' has no bounded type")]
  public void Construct_IsRefused(string source, string code, string message) {
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Has.Some.EqualTo((code, message)));
  }

  /// <summary>A module without variables that functions use may do what it likes at its top level, as before.</summary>
  [Test]
  public void AModuleWithoutState_IgnoresItsTopLevel() {
    var module = Import("let unused = 0; console.log(unused); export function f() { return 1; }");
    Assert.That(module.Globals, Is.Empty);
  }

}
