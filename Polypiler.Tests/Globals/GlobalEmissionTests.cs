using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using JavaScript = Polypiler.Core.Frontends.JavaScript;

namespace Polypiler.Tests.Globals;

/// <summary>
/// The forms both backends write a global in, per C# profile and per JavaScript level and module
/// format, as #1316 tables them: a private static field of the class, a variable of the module that
/// stays private to it in every format, a <c>BigInt</c> or a pair of halves for 64 bits.
/// </summary>
[TestFixture]
public sealed class GlobalEmissionTests {

  private static IrModule FromCSharp(string methods) {
    var result = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(methods));
    return result.Module ?? throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
  }

  private static IrModule FromJavaScript(string source) {
    var result = JavaScript.JavaScriptFrontend.Import("snippet.mjs", source);
    return result.Module ?? throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
  }

  private static string CSharp(IrModule module, string profile = "net10.0") => CSharpEmitter.Emit(module, CSharpProfile.Parse(profile)).Single().Content;

  private static string Script(IrModule module, string level, string module_ = "esm", bool typed = false) {
    var profile = JavaScriptProfile.Parse(level, module_, module_ == "iife" ? "Rng" : null, typed);
    var text = JavaScriptEmitter.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    return text;
  }

  /// <summary>C# backend: a global is a private static field initialised in its declaration, the same in every profile; an array one is readonly; no static constructor.</summary>
  [TestCase("net2.0")]
  [TestCase("net4.8")]
  [TestCase("net10.0")]
  public void CSharp_WritesAPrivateStaticField(string profile) {
    var xorshift = CSharp(StateFixtures.ImportedCSharp("Xorshift32"), profile);
    Assert.That(xorshift, Does.Contain("\n  private static uint _state = 2463534242;\n").And.Contain("\n  private static int _calls = 0;\n").And.Not.Contain("static Xorshift32()"));
    Assert.That(CSharp(StateFixtures.ImportedCSharp("Histogram"), profile), Does.Contain("\n  private static readonly uint[] _buckets = new uint[4];\n"));
    Assert.That(CSharp(StateFixtures.ImportedJavaScript("lazy"), profile), Does.Contain("\n  private static readonly uint[] table = new uint[256];\n").And.Contain("\n  private static bool filled = false;\n"));
  }

  /// <summary>An initializer that wraps around stands in an unchecked context, also where the project checks overflow.</summary>
  [Test]
  public void CSharp_WrapsAnInitializerThatWrapsAround() {
    var text = CSharp(FromCSharp("static int a = int.MaxValue; static int b = a + 1; public static int F() => b;"));
    Assert.That(text, Does.Contain("private static int b = unchecked(a + 1);"));
    Assert.That(GeneratedCSharp.Compile(text, true).Outcome(new PrototypeCall("F", [])), Is.EqualTo("-2147483648"));
  }

  /// <summary>A global named like a keyword of C# is escaped; a local named like a global is renamed and the global keeps its name.</summary>
  [Test]
  public void CSharp_EscapesAKeywordAndRenamesAHidingLocal() {
    var text = CSharp(FromJavaScript("let int = 3; let x = 10; export function f() { int = (int + 1) | 0; let x = 1; return (int + x) | 0; } export function g() { x = (x + 1) | 0; return x; }"));
    Assert.That(text, Does.Contain("private static int @int = 3;").And.Contain("private static int x = 10;").And.Contain("int x_ = 1;"));
    var build = GeneratedCSharp.Compile(text, false);
    Assert.That(new[] { build.Outcome(new("f", [])), build.Outcome(new("g", [])), build.Outcome(new("f", [])) }, Is.EqualTo(new[] { "5", "11", "6" }));
  }

  /// <summary>A global of every type without an initializer is written without one, and starts at zero in C# as in the IR.</summary>
  [Test]
  public void CSharp_LeavesAGlobalWithoutInitializerAtZero() {
    var globals = new[] { IrType.Bool, IrType.I8, IrType.U16, IrType.I64, IrType.U64, IrType.F32, IrType.F64 }.Select(type => new IrGlobal("g" + type, type, null)).ToList();
    var functions = globals.Select(global => new IrFunction("Get" + global.Type, true, global.Type, [], [], new IrBlock([new IrReturn(new IrGlobalRef(global))]))).ToList();
    var text = CSharp(new IrModule("Zeros", functions) { Globals = globals });
    var build = GeneratedCSharp.Compile(text, false);
    Assert.That(functions.Select(function => build.Outcome(new(function.Name, []))), Is.EqualTo(functions.Select(function => PrototypeFixture.Show(IrInterpreter.Call(new IrModule("Zeros", functions) { Globals = globals }, function.Name, [])))));
  }

  /// <summary>JavaScript backend, test design 11: the global stands after "use strict" and before the functions, in the module, in the CommonJS body or in the function of the script.</summary>
  [TestCase("es5", "cjs", "\"use strict\";\n\nvar _state = 2463534242;\nvar _calls = 0;\n")]
  [TestCase("es5", "iife", "  \"use strict\";\n\n  var _state = 2463534242;\n  var _calls = 0;\n")]
  [TestCase("es2015", "esm", "\nlet _state = 2463534242;\nlet _calls = 0;\n")]
  [TestCase("es2017", "cjs", "\"use strict\";\n\nlet _state = 2463534242;\nlet _calls = 0;\n")]
  [TestCase("es2022", "iife", "  \"use strict\";\n\n  let _state = 2463534242;\n  let _calls = 0;\n")]
  public void JavaScript_DeclaresTheGlobalInFrontOfTheFunctions(string level, string format, string declarations) {
    var text = Script(StateFixtures.ImportedCSharp("Xorshift32"), level, format);
    Assert.That(text, Does.Contain(declarations));
    Assert.That(text.IndexOf("_state = 2463534242", StringComparison.Ordinal), Is.LessThan(text.IndexOf("function Next", StringComparison.Ordinal)));
    Assert.That(text, Does.Not.Contain("exports._state").And.Not.Contain("_state: _state").And.Not.Contain("export let"));
  }

  /// <summary>Test design 4: 64 bits are a BigInt from ECMAScript 2020 and the object of two halves below.</summary>
  [TestCase("es5", "cjs", "var _state = { hi: 20529293, lo: 3418323524 };")]
  [TestCase("es2017", "esm", "let _state = { hi: 20529293, lo: 3418323524 };")]
  [TestCase("es2020", "esm", "let _state = 88172645463325252n;")]
  public void JavaScript_Writes64BitsAsTheLevelHasThem(string level, string format, string declaration)
    => Assert.That(Script(StateFixtures.ImportedCSharp("Xorshift64"), level, format), Does.Contain(declaration));

  /// <summary>An array global is a const where the level has one, a typed array or a plain one filled with zeros.</summary>
  [TestCase("es2015", false, "const _buckets = new Uint32Array($len(4));")]
  [TestCase("es5", true, "var _buckets = new Uint32Array($len(4));")]
  [TestCase("es5", false, "var _buckets = $new($len(4), 0);")]
  public void JavaScript_WritesAnArrayGlobalAsTheLevelHasArrays(string level, bool typed, string declaration)
    => Assert.That(Script(StateFixtures.ImportedCSharp("Histogram"), level, level == "es5" ? "cjs" : "esm", typed), Does.Contain(declaration));

  /// <summary>A global without an initializer starts at the zero of its type; one a parameter or a local hides gets a name of its own.</summary>
  [Test]
  public void JavaScript_ZeroesAndRenamesAsNeeded() {
    var globals = new[] { IrType.Bool, IrType.U64, IrType.F64 }.Select(type => new IrGlobal("g" + type, type, null)).ToList();
    var functions = globals.Select(global => new IrFunction("get" + global.Type, true, global.Type, [], [], new IrBlock([new IrReturn(new IrGlobalRef(global))]))).ToList();
    var zeros = new IrModule("zeros", functions) { Globals = globals };
    Assert.That(Script(zeros, "es2022"), Does.Contain("let gbool = false;\nlet gu64 = 0n;\nlet gf64 = 0;\n"));
    Assert.That(Script(zeros, "es2015"), Does.Contain("let gu64 = { hi: 0, lo: 0 };"));

    var hidden = Script(FromCSharp("static int x = 5; public static int F() { int x = 1; Snippet.x += x; return x + Snippet.x; } public static int G(int x) => x;"), "es2022");
    Assert.That(hidden, Does.Contain("let x$g = 5;").And.Contain("x$g = (x$g + x) | 0;").And.Contain("return (x + x$g) | 0;"));
  }

  /// <summary>Every fixture is written for every level and module format, and parses at its level.</summary>
  [TestCaseSource(typeof(StateFixtures), nameof(StateFixtures.All))]
  public void JavaScript_WritesEveryFixtureForEveryProfile((string File, string Class, string Script) fixture) {
    var modules = new[] { StateFixtures.ImportedCSharp(fixture.File), StateFixtures.ImportedJavaScript(fixture.Script) };
    foreach (var module in modules)
      foreach (var level in JavaScriptProfile.LevelNames)
        foreach (var format in JavaScriptProfile.ModuleNames.Where(format => level != "es5" || format != "esm"))
          Script(module, level, format, level == "es5");
  }

  /// <summary>
  /// An assignment to a global is the one statement of the step of a for, and a step of a global is a
  /// value: C# writes a real for and the operator on the field, JavaScript an assignment in the third
  /// part and the step as an assignment inside the expression.
  /// </summary>
  [Test]
  public void BothBackends_WriteAGlobalInTheStepOfAForAndAsAStep() {
    var total = new IrGlobal("total", IrType.I32, new IrConst(IrType.I32, 0));
    var add = new IrBinary(IrBinaryOp.Add, new IrGlobalRef(total), new IrConst(IrType.I32, 2));
    var loop = new IrFor(new IrBinary(IrBinaryOp.Less, new IrGlobalRef(total), new IrConst(IrType.I32, 5)), new IrBlock([new IrContinue()]), new IrBlock([new IrGlobalAssign(total, add)]));
    var module = new IrModule("Loop", [
      new IrFunction("F", true, IrType.I32, [], [], new IrBlock([loop, new IrReturn(new IrGlobalRef(total))])),
      new IrFunction("G", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrStep(new IrGlobalRef(total), true, true, false))]))
    ]) { Globals = [total] };
    var csharp = CSharp(module, "net2.0");
    Assert.That(csharp, Does.Contain("for (; total < 5; total = total + 2) {").And.Contain("return total++;"));
    var build = GeneratedCSharp.Compile(csharp, true);
    Assert.That(new[] { build.Outcome(new("F", [])), build.Outcome(new("G", [])), build.Outcome(new("G", [])) }, Is.EqualTo(new[] { "6", "6", "7" }));
    Assert.That(Script(module, "es2022"), Does.Contain("for (; total < 5; total = (total + 2) | 0) {").And.Contain("return ($p = total, total = ($p + 1) | 0, $p);"));
  }

  /// <summary>The checks of both backends find nothing to refuse in a module with globals.</summary>
  [Test]
  public void TheChecks_AcceptEveryNodeOfAGlobal() {
    var module = StateFixtures.ImportedCSharp("Xorshift32");
    Assert.That(CSharpEmitter.Check(module, CSharpProfile.Parse("net2.0")), Is.Empty);
    Assert.That(JavaScriptEmitter.Check(module, JavaScriptProfile.Parse("es5", "cjs")), Is.Empty);
    var saturating = new IrGlobal("s", IrType.I32, new IrConvert(IrType.I32, new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1e12)), IrConversion.Saturate));
    var get = new IrFunction("Get", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrGlobalRef(saturating))]));
    var withConversion = new IrModule("Saturating", [get]) { Globals = [saturating] };
    Assert.That(CSharpEmitter.Check(withConversion, CSharpProfile.Parse("net2.0")), Is.Empty);
    Assert.That(GeneratedCSharp.Compile(CSharp(withConversion, "net2.0"), false).Outcome(new("Get", [])), Is.EqualTo(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
  }

}
