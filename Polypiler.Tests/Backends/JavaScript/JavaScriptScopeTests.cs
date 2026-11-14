using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.JavaScript;

/// <summary>
/// Locals of one name whose scopes nest (#1343). A <c>let</c> hides every other variable of its name
/// from its whole block, the reads in front of it and its own initializer included; the IR does not.
/// Above ECMAScript 5 the emitter therefore renames a local declared while one of its name is visible
/// to <c>&lt;name&gt;_&lt;k&gt;</c>, the rule of the C# backend (#1312); ECMAScript 5 keeps its
/// <c>var</c> renaming. What the files compute in <c>node</c> is checked by the acceptance tests
/// (<c>tests/features/lang/js/be-js-scopes.feature</c>); here the names, the level of the text, the
/// interpreter and the C# compiler.
/// </summary>
[TestFixture]
public sealed class JavaScriptScopeTests {

  private static readonly bool[] _overflowChecking = [false, true];

  /// <summary>Every valid combination of level, module format and, at ECMAScript 5, typed arrays.</summary>
  private static readonly JavaScriptProfile[] _profiles = [
    .. from level in JavaScriptProfile.LevelNames
       from module in JavaScriptProfile.ModuleNames
       where level != "es5" || module != "esm"
       from typed in level == "es5" ? new[] { false, true } : [false]
       select JavaScriptProfile.Parse(level, module, module == "iife" ? "Generated" : null, typed)
  ];

  private static ShadowingModules.Case Case(string name) => ShadowingModules.All.Single(@case => @case.Name == name);

  /// <summary>The file of a module in a profile, after it parsed at the level of the profile.</summary>
  private static string Emitted(IrModule module, JavaScriptProfile profile) {
    var text = JavaScriptEmitter.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{profile}:\n{text}");
    return text;
  }

  /// <summary>The body of <c>F</c>, one line per statement without indentation.</summary>
  private static string[] Body(string text) {
    var lines = text.Split('\n');
    var start = Array.FindIndex(lines, line => line.Contains("function F("));
    return lines.Skip(start + 1).TakeWhile(line => line.TrimEnd() != "}").Select(line => line.Trim()).ToArray();
  }

  /// <summary>The names a file declares with <c>let</c> or <c>var</c>, in their order.</summary>
  private static string[] Declared(string text)
    => Regex.Matches(text, @"(?m)^\s*(?:let|var) ([\w$]+)").Select(match => match.Groups[1].Value).ToArray();

  /// <summary>
  /// Test design 1 to 4: every case is a verified module, and the C# compiler's build of it computes
  /// what the interpreter computes, without and with overflow checking.
  /// </summary>
  [TestCase("inner after outer")]
  [TestCase("outer read before inner")]
  [TestCase("parameter shadowed")]
  [TestCase("loop variable shadowed")]
  [TestCase("suffix taken")]
  [TestCase("siblings")]
  public void Case_InterpreterAndCSharpAgree(string name) {
    var @case = Case(name);
    Assert.That(IrVerifier.Verify(@case.Module), Is.Empty);
    var text = CSharpEmitter.Emit(@case.Module, CSharpProfile.Parse("net10.0", null)).Single().Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net10.0", "14"), Is.Empty, text);
    foreach (var (argument, result) in @case.Calls) {
      var call = new PrototypeCall("F", [argument]);
      Assert.That(PrototypeFixture.InterpretedOutcome(@case.Module, call), Is.EqualTo(result), $"interpreter {call}");
      foreach (var checking in _overflowChecking)
        Assert.That(GeneratedCSharp.Compile(text, checking).Outcome(call), Is.EqualTo(result), $"C# {call}, checked {checking}");
    }
  }

  /// <summary>
  /// Test design 1 to 4: the names every profile declares. Above ECMAScript 5 a local declared while
  /// one of its name is visible becomes <c>&lt;name&gt;_&lt;k&gt;</c> with the first free <c>k</c>; at
  /// ECMAScript 5 it keeps the <c>$</c> suffix of the <c>var</c> renaming; siblings keep their names.
  /// </summary>
  [TestCase("inner after outer", new[] { "t", "t_1" }, new[] { "t", "t$1" })]
  [TestCase("outer read before inner", new[] { "t", "s", "t_1" }, new[] { "t", "s", "t$1" })]
  [TestCase("parameter shadowed", new[] { "n_1" }, new[] { "n$1" })]
  [TestCase("loop variable shadowed", new[] { "s", "i", "i_1" }, new[] { "s", "i", "i$1" })]
  [TestCase("suffix taken", new[] { "t", "t_1", "t_2" }, new[] { "t", "t_1", "t$1" })]
  [TestCase("siblings", new[] { "s", "t", "t" }, new[] { "s", "t", "t" })]
  public void Case_DeclaresTheNamesOfItsProfile(string name, string[] block, string[] function) {
    var @case = Case(name);
    foreach (var profile in _profiles) {
      var body = Body(Emitted(@case.Module, profile));
      Assert.That(Declared(string.Join('\n', body)), Is.EqualTo(profile.Level == JavaScriptLevel.Es5 ? function : block), profile.ToString());
    }
  }

  /// <summary>Test design 1: the reproduction of the issue, where every read of the outer <c>t</c> in the block reads <c>t</c>.</summary>
  [Test]
  public void InnerAfterOuter_ReadsTheOuterLocalUnderItsOwnName() {
    var body = Body(Emitted(ShadowingModules.InnerAfterOuter().Module, JavaScriptProfile.Parse("es2022")));
    Assert.That(body, Is.EqualTo(new[] {
      "let t = 0.5;",
      "{",
      "let t_1 = Math.imul(n, 2);",
      "return t_1 + (t + t);",
      "}"
    }));
  }

  /// <summary>Test design 2: a block that reads the outer <c>t</c> in front of its own one no longer reads a <c>let</c> before its declaration.</summary>
  [Test]
  public void OuterReadBeforeInner_ReadsTheOuterLocalBeforeTheRenamedOne() {
    var body = Body(Emitted(ShadowingModules.OuterReadBeforeInner().Module, JavaScriptProfile.Parse("es2015")));
    Assert.That(body, Is.EqualTo(new[] {
      "let t = 0.5;",
      "let s = 0;",
      "{",
      "s = s + t;",
      "let t_1 = Math.imul(n, 2);",
      "s = s + t_1;",
      "}",
      "return s + t;"
    }));
  }

  /// <summary>Test design 2: the initializer of the renamed loop-body local reads the loop variable.</summary>
  [Test]
  public void LoopVariableShadowed_InitializerReadsTheLoopVariable() {
    var text = Emitted(ShadowingModules.LoopVariableShadowed().Module, JavaScriptProfile.Parse("es2022"));
    Assert.That(Body(text), Does.Contain("let i_1 = i * 0.5;").And.Contain("s = s + (i_1 + i);"));
  }

  /// <summary>
  /// A local named like a function of the module is renamed past it: a <c>let</c> of that name would
  /// hide the function from the block.
  /// </summary>
  [Test]
  public void RenamedLocal_DoesNotTakeTheNameOfAFunction() {
    var n = new IrLocal("n", IrType.I32);
    var outer = new IrLocal("t", IrType.F64);
    var inner = new IrLocal("t", IrType.I32);
    var body = new IrBlock([
      new IrDeclare(outer, new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(0.5))),
      new IrBlock([
        new IrDeclare(inner, new IrCall("t_1", IrType.I32, [new IrLocalRef(n)])),
        new IrReturn(new IrBinary(IrBinaryOp.Add, new IrConvert(IrType.F64, new IrLocalRef(inner)), new IrLocalRef(outer)))
      ])
    ]);
    var module = new IrModule("Scopes", [
      new IrFunction("F", true, IrType.F64, [n], [outer, inner], body),
      new IrFunction("t_1", false, IrType.I32, [new IrLocal("x", IrType.I32)], [], new IrBlock([new IrReturn(new IrLocalRef(new IrLocal("x", IrType.I32)))]))
    ]);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(Body(Emitted(module, JavaScriptProfile.Parse("es2022"))), Does.Contain("let t_2 = t_1(n);").And.Contain("return t_2 + t;"));
    Assert.That(IrInterpreter.Call(module, "F", [4]), Is.EqualTo(4.5));
  }

}
