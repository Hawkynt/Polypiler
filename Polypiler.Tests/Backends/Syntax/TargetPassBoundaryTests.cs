using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.CSharp.Passes;
using Polypiler.Core.Backends.CSharp.Printing;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.JavaScript.Passes;
using Polypiler.Core.Backends.JavaScript.Printing;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Backends.Syntax;

/// <summary>
/// The classes of arguments O0500 tells apart, one case each: which operators an argument without an
/// effect may consist of, which literal converts to which parameter, and the operator tables of the
/// printers, known operators and an unknown one.
/// </summary>
[TestFixture]
public sealed class TargetPassBoundaryTests {

  private static CsName Typed(string name, IrType type) => new(name) { Type = type };

  /// <summary>Whether <c>$h(T a, int b) =&gt; a</c> is inlined into a call with the given first argument.</summary>
  private static bool CSharpInlined(string parameter, CsExpression argument) {
    var helper = new CsMethod(["private", "static"], new CsType(parameter), "$h", [], [new CsParameter(new CsType(parameter), "a"), new CsParameter(new CsType("int"), "b")],
      new CsBlock([new CsReturn(new CsName("a"))]), null) { IsHelper = true };
    var caller = new CsMethod(["public", "static"], new CsType(parameter), "F", [], [], new CsBlock([new CsReturn(new CsInvocation(new CsName("$h"), [argument, Typed("y", IrType.I32)]))]), null);
    var unit = new CsCompilationUnit([], [new CsTypeDeclaration(["public", "static"], "class", "C", null, [], [helper, caller])]);
    var applied = new List<string>();
    new CSharpHelperInlining().Run(unit, CSharpProfile.Net20, applied.Add);
    return applied.Count == 1;
  }

  private static IEnumerable<TestCaseData> CSharpOperators()
    => new[] { "&", "|", "^", "<<", ">>", "==", "!=", "<", ">", "<=", ">=", "&&", "||" }.Select(op => new TestCaseData(op, true).SetName($"CSharp pure operator {op}"))
      .Append(new TestCaseData("+", false).SetName("CSharp arithmetic that may trap is no pure argument"))
      .Append(new TestCaseData("/", false).SetName("CSharp division is no pure argument"));

  [TestCaseSource(nameof(CSharpOperators))]
  public void CSharpPureArgument_IsMadeOf(string op, bool pure)
    => Assert.That(CSharpInlined("int", new CsParenthesized(new CsBinary(Typed("x", IrType.I32), op, new CsIntegerLiteral(1)) { Type = IrType.I32 })), Is.EqualTo(pure));

  [Test]
  public void CSharpPureArgument_TakesNotAndComplementOnly() {
    Assert.That(CSharpInlined("int", new CsPrefixUnary("~", Typed("x", IrType.I32)) { Type = IrType.I32 }), Is.True);
    Assert.That(CSharpInlined("bool", new CsPrefixUnary("!", Typed("x", IrType.Bool)) { Type = IrType.Bool }), Is.True);
    Assert.That(CSharpInlined("int", new CsPrefixUnary("-", Typed("x", IrType.I32)) { Type = IrType.I32 }), Is.False);
    Assert.That(CSharpInlined("int", new CsName("global::System.X") { Type = IrType.I32 }), Is.False);
    Assert.That(CSharpInlined("int", new CsName("a.b") { Type = IrType.I32 }), Is.False);
    Assert.That(CSharpInlined("int", new CsName("x") { Type = IrType.Array(IrType.I32) }), Is.False);
  }

  private static IEnumerable<TestCaseData> CSharpLiterals() => [
    new TestCaseData(new CsIntegerLiteral(5), "int", true),
    new TestCaseData(new CsIntegerLiteral(5), "byte", true),
    new TestCaseData(new CsIntegerLiteral(300), "byte", false),
    new TestCaseData(new CsIntegerLiteral(-1), "uint", false),
    new TestCaseData(new CsIntegerLiteral(5), "long", true),
    new TestCaseData(new CsIntegerLiteral(5), "double", true),
    new TestCaseData(new CsIntegerLiteral(5), "bool", false),
    new TestCaseData(new CsIntegerLiteral(3000000000), "uint", true),
    new TestCaseData(new CsIntegerLiteral(3000000000), "int", false),
    new TestCaseData(new CsIntegerLiteral(5000000000), "long", true),
    new TestCaseData(new CsIntegerLiteral(BigInteger.Parse("10000000000000000000")), "ulong", true),
    new TestCaseData(new CsIntegerLiteral(5, CsIntegerSuffix.U), "uint", true),
    new TestCaseData(new CsIntegerLiteral(5000000000, CsIntegerSuffix.U), "ulong", true),
    new TestCaseData(new CsIntegerLiteral(5, CsIntegerSuffix.L), "long", true),
    new TestCaseData(new CsIntegerLiteral(BigInteger.Parse("10000000000000000000"), CsIntegerSuffix.L), "ulong", true),
    new TestCaseData(new CsIntegerLiteral(5, CsIntegerSuffix.UL), "ulong", true),
    new TestCaseData(new CsRealLiteral(1.5), "double", true),
    new TestCaseData(new CsRealLiteral(1.5, true), "double", true),
    new TestCaseData(new CsRealLiteral(1.5), "float", false),
    new TestCaseData(new CsBooleanLiteral(true), "bool", true),
    new TestCaseData(new CsParenthesized(new CsIntegerLiteral(5)), "int", true)
  ];

  [TestCaseSource(nameof(CSharpLiterals))]
  public void CSharpLiteral_ConvertsAsTheCallConvertsIt(CsExpression literal, string parameter, bool inlined)
    => Assert.That(CSharpInlined(parameter, literal), Is.EqualTo(inlined));

  /// <summary>Whether <c>$h(a, b) =&gt; a</c> is inlined into a call with the given first argument, which it reads once.</summary>
  private static bool JavaScriptInlined(JsExpression argument) {
    var helper = new JsFunctionDeclaration("$h", ["a", "b"], new JsBlock([new JsReturn(new JsIdentifier("a"))])) { IsHelper = true };
    var caller = new JsFunctionDeclaration("f", ["x"], new JsBlock([new JsReturn(new JsCall(new JsIdentifier("$h"), [argument, new JsIdentifier("x")]))]));
    var applied = new List<string>();
    new JavaScriptHelperInlining().Run(new JsProgram([], [helper, caller], false), JavaScriptProfile.Default, applied.Add);
    return applied.Count == 1;
  }

  private static JsIdentifier X => new("x");

  private static IEnumerable<TestCaseData> JavaScriptArguments() => [
    new TestCaseData(new JsParenthesized(X), true).SetName("JavaScript parenthesized variable is pure"),
    new TestCaseData(new JsUnary("-", X), true).SetName("JavaScript negation is pure"),
    new TestCaseData(new JsUnary("typeof", X), false).SetName("JavaScript typeof is not taken"),
    new TestCaseData(new JsBinary(X, "+", X), true).SetName("JavaScript sum is pure"),
    new TestCaseData(new JsBinary(X, "in", X), false).SetName("JavaScript in is not taken"),
    new TestCaseData(new JsBinary(new JsCall(X, []), "+", X), false).SetName("JavaScript sum with a call is not pure"),
    new TestCaseData(new JsConditional(X, X, X), true).SetName("JavaScript conditional is pure"),
    new TestCaseData(new JsConditional(X, new JsCall(X, []), X), false).SetName("JavaScript conditional with a call is not pure"),
    new TestCaseData(new JsMember(X, "lo"), true).SetName("JavaScript member is pure"),
    new TestCaseData(new JsIndex(X, new JsNumber(0)), true).SetName("JavaScript element is pure"),
    new TestCaseData(new JsIndex(X, new JsCall(X, [])), false).SetName("JavaScript element with a call is not pure"),
    new TestCaseData(new JsObject([new JsProperty("hi", X)]), false).SetName("JavaScript object made anew is not taken"),
    new TestCaseData(new JsString("s"), true).SetName("JavaScript string is trivial"),
    new TestCaseData(new JsBigInt(1), true).SetName("JavaScript BigInt is trivial"),
    new TestCaseData(new JsBoolean(true), true).SetName("JavaScript Boolean is trivial")
  ];

  [TestCaseSource(nameof(JavaScriptArguments))]
  public void JavaScriptArgument_IsTakenWhenPure(JsExpression argument, bool inlined) => Assert.That(JavaScriptInlined(argument), Is.EqualTo(inlined));

  [Test]
  public void JavaScriptInlining_RefusesTheWrongNumberOfArguments_AndAnArgumentsHelper() {
    var helper = new JsFunctionDeclaration("$h", ["a"], new JsBlock([new JsReturn(new JsIdentifier("arguments"))])) { IsHelper = true };
    var caller = new JsFunctionDeclaration("f", ["x"], new JsBlock([new JsReturn(new JsCall(new JsIdentifier("$h"), [X]))]));
    var applied = new List<string>();
    new JavaScriptHelperInlining().Run(new JsProgram([], [helper, caller], false), JavaScriptProfile.Default, applied.Add);
    Assert.That(applied, Is.Empty);
  }

  private static readonly string[] _csharpOperators = ["*", "/", "%", "+", "-", "<<", ">>", ">>>", "<", ">", "<=", ">=", "==", "!=", "&", "^", "|", "&&", "||", "??"];

  [Test]
  public void CSharpPrinter_KnowsEveryOperatorOfItsTable() {
    foreach (var op in _csharpOperators)
      Assert.That(CSharpPrinter.Print(new CsBinary(new CsName("a"), op, new CsName("b")), CSharpProfile.Default), Is.EqualTo($"a {op} b"));

    Assert.Throws<ArgumentException>(() => CSharpPrinter.Print(new CsBinary(new CsName("a"), "<>", new CsName("b")), CSharpProfile.Default));
    Assert.That(CSharpPrinter.Precedence(new CsRealLiteral(-1.5)), Is.EqualTo(CSharpPrinter.Precedence(new CsPrefixUnary("-", new CsName("a")))));
    Assert.That(CSharpPrinter.Precedence(new CsRealLiteral(double.NaN)), Is.EqualTo(CSharpPrinter.Precedence(new CsName("a"))));
  }

  [Test]
  public void JavaScriptPrinter_KnowsEveryOperatorOfItsTable() {
    foreach (var op in new[] { "**", "*", "/", "%", "+", "-", "<<", ">>", ">>>", "<", ">", "<=", ">=", "in", "instanceof", "==", "!=", "===", "!==", "&", "^", "|", "&&", "||", "??" })
      Assert.That(JavaScriptPrinter.Print(new JsBinary(X, op, new JsIdentifier("y"))), Is.EqualTo($"x {op} y"));

    Assert.Throws<ArgumentException>(() => JavaScriptPrinter.Print(new JsBinary(X, "<>", X)));
    Assert.That(JavaScriptPrinter.Precedence(new JsBigInt(-1)), Is.EqualTo(JavaScriptPrinter.Precedence(new JsUnary("-", X))));
    Assert.That(JavaScriptPrinter.Precedence(new JsUpdate("++", false, X)), Is.GreaterThan(JavaScriptPrinter.Precedence(new JsUpdate("++", true, X))));
    Assert.That(JavaScriptPrinter.Print(new JsUnary("-", new JsBigInt(-1))), Is.EqualTo("-(-1n)"));
    Assert.That(JavaScriptPrinter.Print(new JsUnary("+", new JsUnary("+", X))), Is.EqualTo("+(+x)"));
    Assert.That(JavaScriptPrinter.Print(new JsUnary("!", new JsUnary("-", X))), Is.EqualTo("!-x"));
  }

  [Test]
  public void TargetPasses_LeaveATreeWithoutTheirSubjectAlone() {
    var empty = new JsProgram([], [], true);
    var applied = new List<string>();
    Assert.That(JavaScriptTargetPasses.Pipeline.Run(empty, JavaScriptProfile.Default, Polypiler.Core.Optimize.Target.TargetPassOptions.Default).Tree, Is.SameAs(empty));
    var unit = new CsCompilationUnit([], [new CsTypeDeclaration(["public"], "struct", "S", null, [], null)]);
    Assert.That(CSharpTargetPasses.Pipeline.Run(unit, CSharpProfile.Default, Polypiler.Core.Optimize.Target.TargetPassOptions.Default).Tree, Is.SameAs(unit));
    Assert.That(CSharpTargetPasses.ModuleClass(unit), Is.Null);
  }

}
