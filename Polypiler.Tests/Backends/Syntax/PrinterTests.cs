using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Acornima;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.CSharp.Printing;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.JavaScript.Printing;
using Polypiler.Core.Backends.JavaScript.Syntax;

namespace Polypiler.Tests.Backends.Syntax;

/// <summary>
/// The printers on hand-built trees without parentheses: they add exactly the parentheses the
/// precedence and associativity of the language need, so that the text parses back to the tree, and
/// they spell every literal so that it reads back as its value.
/// </summary>
[TestFixture]
public sealed class PrinterTests {

  private static readonly string[] _csharpOperators = ["*", "/", "%", "+", "-", "<<", ">>", ">>>", "<", ">", "<=", ">=", "==", "!=", "&", "^", "|", "&&", "||", "??"];
  private static readonly string[] _javascriptOperators = ["**", "*", "/", "%", "+", "-", "<<", ">>", ">>>", "<", ">", "<=", ">=", "===", "!==", "&", "^", "|", "&&", "||"];

  private static CsName A => new("a");
  private static CsName B => new("b");
  private static CsName C => new("c");
  private static JsIdentifier X => new("x");
  private static JsIdentifier Y => new("y");
  private static JsIdentifier Z => new("z");

  private static IEnumerable<TestCaseData> CSharpPairs()
    => from outer in _csharpOperators from inner in _csharpOperators select new TestCaseData(outer, inner).SetName($"CSharp({inner} in {outer})");

  private static IEnumerable<TestCaseData> JavaScriptPairs()
    => from outer in _javascriptOperators from inner in _javascriptOperators
       where !(outer is "&&" or "||" && inner == "??" || inner is "&&" or "||" && outer == "??")
       select new TestCaseData(outer, inner).SetName($"JavaScript({inner} in {outer})");

  /// <summary>The C# expression as the printer writes it, read back by Roslyn.</summary>
  private static string CSharpRoundTrip(CsExpression expression) {
    var text = CSharpPrinter.Print(expression, CSharpProfile.Default);
    Assert.That(SyntaxShape.Of(CSharpSyntaxReader.Expression(text)), Is.EqualTo(SyntaxShape.Of(expression)), text);
    return text;
  }

  /// <summary>The JavaScript expression as the printer writes it, read back by Acornima as the statement it is.</summary>
  private static string JavaScriptRoundTrip(JsExpression expression) {
    var text = JavaScriptPrinter.Print(new JsExpressionStatement(expression));
    var parsed = JavaScriptSyntaxReader.Statements(text);
    Assert.That(SyntaxShape.Of(parsed[0]), Is.EqualTo(SyntaxShape.Of(new JsExpressionStatement(expression))), text);
    return text;
  }

  [TestCaseSource(nameof(CSharpPairs))]
  public void CSharpOperators_NestLeftAndRight(string outer, string inner) {
    CSharpRoundTrip(new CsBinary(new CsBinary(A, inner, B), outer, C));
    CSharpRoundTrip(new CsBinary(A, outer, new CsBinary(B, inner, C)));
  }

  [TestCaseSource(nameof(JavaScriptPairs))]
  public void JavaScriptOperators_NestLeftAndRight(string outer, string inner) {
    JavaScriptRoundTrip(new JsBinary(new JsBinary(X, inner, Y), outer, Z));
    JavaScriptRoundTrip(new JsBinary(X, outer, new JsBinary(Y, inner, Z)));
  }

  [Test]
  public void CSharpParentheses_AreMinimal() {
    Assert.That(CSharpRoundTrip(new CsBinary(A, "-", new CsBinary(B, "-", C))), Is.EqualTo("a - (b - c)"));
    Assert.That(CSharpRoundTrip(new CsBinary(new CsBinary(A, "-", B), "-", C)), Is.EqualTo("a - b - c"));
    Assert.That(CSharpRoundTrip(new CsBinary(A, "+", new CsBinary(B, "*", C))), Is.EqualTo("a + b * c"));
    Assert.That(CSharpRoundTrip(new CsPrefixUnary("-", new CsPrefixUnary("-", A))), Is.EqualTo("-(-a)"));
    Assert.That(CSharpRoundTrip(new CsBinary(new CsPrefixUnary("-", A), "-", B)), Is.EqualTo("-a - b"));
    Assert.That(CSharpRoundTrip(new CsPrefixUnary("-", new CsBinary(A, "-", B))), Is.EqualTo("-(a - b)"));
    Assert.That(CSharpRoundTrip(new CsCast(new CsType("int"), new CsBinary(A, "+", B))), Is.EqualTo("(int)(a + b)"));
    Assert.That(CSharpRoundTrip(new CsCast(new CsType("int"), new CsPrefixUnary("~", A))), Is.EqualTo("(int)~a"));
    Assert.That(CSharpRoundTrip(new CsMemberAccess(new CsBinary(A, "+", B), "Length")), Is.EqualTo("(a + b).Length"));
    Assert.That(CSharpRoundTrip(new CsConditional(A, new CsConditional(B, C, A), new CsConditional(C, A, B))), Is.EqualTo("a ? b ? c : a : c ? a : b"));
    Assert.That(CSharpRoundTrip(new CsConditional(new CsConditional(A, B, C), A, B)), Is.EqualTo("(a ? b : c) ? a : b"));
    Assert.That(CSharpRoundTrip(new CsAssignment(A, "=", new CsAssignment(B, "=", C))), Is.EqualTo("a = b = c"));
    Assert.That(CSharpRoundTrip(new CsIsType(new CsBinary(A, "+", B), new CsType("int"))), Is.EqualTo("a + b is int"));
    Assert.That(CSharpRoundTrip(new CsElementAccess(new CsPostfixUnary(A, "++"), B)), Is.EqualTo("a++[b]"));
  }

  [Test]
  public void JavaScriptParentheses_AreMinimal() {
    Assert.That(JavaScriptRoundTrip(new JsBinary(X, "-", new JsBinary(Y, "-", Z))), Is.EqualTo("x - (y - z);\n"));
    Assert.That(JavaScriptRoundTrip(new JsBinary(new JsBinary(X, ">>>", new JsNumber(0)), ">>", Y)), Is.EqualTo("x >>> 0 >> y;\n"));
    Assert.That(JavaScriptRoundTrip(new JsUnary("-", new JsUnary("-", X))), Is.EqualTo("-(-x);\n"));
    Assert.That(JavaScriptRoundTrip(new JsUnary("-", new JsNumber(-1))), Is.EqualTo("-(-1);\n"));
    Assert.That(JavaScriptRoundTrip(new JsUnary("-", new JsUpdate("--", true, X))), Is.EqualTo("-(--x);\n"));
    Assert.That(JavaScriptRoundTrip(new JsUnary("typeof", X)), Is.EqualTo("typeof x;\n"));
    Assert.That(JavaScriptRoundTrip(new JsMember(new JsNew(new JsIdentifier("DataView"), [X]), "byteLength")), Is.EqualTo("new DataView(x).byteLength;\n"));
    Assert.That(JavaScriptRoundTrip(new JsNew(new JsCall(X, []), [])), Is.EqualTo("new (x())();\n"));
    Assert.That(JavaScriptRoundTrip(new JsMember(new JsNumber(5), "toString")), Is.EqualTo("(5).toString;\n"));
    Assert.That(JavaScriptRoundTrip(new JsBinary(new JsBinary(X, "**", Y), "**", Z)), Is.EqualTo("(x ** y) ** z;\n"));
    Assert.That(JavaScriptRoundTrip(new JsConditional(X, new JsConditional(Y, Z, X), new JsConditional(Z, X, Y))), Is.EqualTo("x ? y ? z : x : z ? x : y;\n"));
    Assert.That(JavaScriptRoundTrip(new JsConditional(new JsAssignment(X, "=", Y), Y, Z)), Is.EqualTo("(x = y) ? y : z;\n"));
    Assert.That(JavaScriptRoundTrip(new JsCall(X, [new JsSequence([Y, Z])])), Is.EqualTo("x((y, z));\n"));
    Assert.That(JavaScriptRoundTrip(new JsSequence([new JsAssignment(X, "=", Y), new JsUpdate("++", false, Z)])), Is.EqualTo("x = y, z++;\n"));
    Assert.That(JavaScriptRoundTrip(new JsMember(new JsObject([new JsProperty("hi", X)]), "hi")), Is.EqualTo("({ hi: x }.hi);\n"));
    Assert.That(JavaScriptRoundTrip(new JsCall(new JsFunctionExpression(null, [], new JsBlock([])), [])), Is.EqualTo("(function () {\n}());\n"));
  }

  private static IEnumerable<TestCaseData> CSharpLiterals() => [
    new TestCaseData(new CsIntegerLiteral(int.MinValue), "-2147483648"),
    new TestCaseData(new CsIntegerLiteral(long.MinValue, CsIntegerSuffix.L), "-9223372036854775808L"),
    new TestCaseData(new CsIntegerLiteral(ulong.MaxValue, CsIntegerSuffix.UL), "18446744073709551615UL"),
    new TestCaseData(new CsIntegerLiteral(0x80000000u, CsIntegerSuffix.U), "2147483648u"),
    new TestCaseData(new CsRealLiteral(-0.0), "-0.0"),
    new TestCaseData(new CsRealLiteral(double.Epsilon), "5E-324"),
    new TestCaseData(new CsRealLiteral(1.0), "1.0"),
    new TestCaseData(new CsRealLiteral(float.Epsilon, true), "1E-45f"),
    new TestCaseData(new CsRealLiteral(0.1f, true), "0.1f"),
    new TestCaseData(new CsIntegerLiteral(255) { Raw = "0xFF" }, "0xFF"),
    new TestCaseData(new CsIntegerLiteral(256) { Raw = "0xFF" }, "256"),
    new TestCaseData(new CsRealLiteral(0.5) { Raw = "5e-1" }, "5e-1")
  ];

  [TestCaseSource(nameof(CSharpLiterals))]
  public void CSharpLiteral_ReadsBackAsItsValue(CsExpression literal, string spelled)
    => Assert.That(CSharpRoundTrip(literal), Is.EqualTo(spelled));

  [Test]
  public void CSharpSpelling_FollowsTheLanguageVersion() {
    var separated = new CsIntegerLiteral(1000000) { Raw = "1_000_000" };
    var binary = new CsIntegerLiteral(5) { Raw = "0b101" };
    Assert.That(CSharpPrinter.Print(separated, CSharpProfile.Default), Is.EqualTo("1_000_000"));
    Assert.That(CSharpPrinter.Print(binary, CSharpProfile.Default), Is.EqualTo("0b101"));
    Assert.That(CSharpPrinter.Print(new CsField(["private", "const"], new CsType("int"), "X", separated), CSharpProfile.Net20), Is.EqualTo("private const int X = 1000000;\n"));
    Assert.That(CSharpPrinter.Print(new CsField(["private", "const"], new CsType("int"), "X", binary), CSharpProfile.Net20), Is.EqualTo("private const int X = 5;\n"));
  }

  private static IEnumerable<TestCaseData> JavaScriptLiterals() => [
    new TestCaseData(new JsNumber(-0.0), "-0"),
    new TestCaseData(new JsNumber(double.NaN), "NaN"),
    new TestCaseData(new JsNumber(double.PositiveInfinity), "Infinity"),
    new TestCaseData(new JsNumber(double.NegativeInfinity), "-Infinity"),
    new TestCaseData(new JsNumber(double.Epsilon), "5E-324"),
    new TestCaseData(new JsNumber(2147483648) { Raw = "0x80000000" }, "0x80000000"),
    new TestCaseData(new JsNumber(9223372036854775808.0) { Raw = "9223372036854775808" }, "9223372036854775808"),
    new TestCaseData(new JsNumber(3) { Raw = "0x80000000" }, "3"),
    new TestCaseData(new JsBigInt(BigInteger.Pow(2, 63)), "9223372036854775808n"),
    new TestCaseData(new JsBigInt(-BigInteger.Pow(2, 63)), "-9223372036854775808n"),
    new TestCaseData(new JsBigInt(255) { Raw = "0xFFn" }, "0xFFn"),
    new TestCaseData(new JsString("a\"b\\c\nd"), "\"a\\\"b\\\\c\\nd\""),
    new TestCaseData(new JsBoolean(false), "false")
  ];

  [TestCaseSource(nameof(JavaScriptLiterals))]
  public void JavaScriptLiteral_ReadsBackAsItsValue(JsExpression literal, string spelled)
    => Assert.That(JavaScriptRoundTrip(literal), Is.EqualTo(spelled + ";\n"));

  [Test]
  public void Es5_ParsesTheHexadecimalLiteralOfTheHighBit() {
    var text = JavaScriptPrinter.Print(new JsProgram([], [new JsExpressionStatement(new JsNumber(2147483648) { Raw = "0x80000000" })], false), JavaScriptProfile.Parse("es5", "cjs"));
    Assert.That(JavaScriptSyntaxReader.Program(text, EcmaVersion.ES5, false, out var problem), Is.Not.Null, problem);
  }

  [Test]
  public void CSharpStatements_PrintAndParseBack() {
    CsStatement[] statements = [
      new CsIf(A, new CsReturn(B), new CsReturn(C)),
      new CsIf(A, new CsBlock([new CsBreak()]), new CsIf(B, new CsBlock([new CsContinue()]), new CsBlock([new CsThrow(new CsObjectCreation(new CsType("global::System.Exception"), [new CsStringLiteral("x")]))]))),
      new CsWhile(A, new CsExpressionStatement(new CsPostfixUnary(B, "++"))),
      new CsDo(new CsExpressionStatement(new CsPrefixUnary("--", B)), A),
      new CsDo(new CsBlock([]), A),
      new CsFor(new CsLocalDeclaration(new CsType("int"), "i", new CsIntegerLiteral(0)), new CsBinary(new CsName("i"), "<", A), [new CsPrefixUnary("++", new CsName("i"))], new CsBlock([]) { Inline = true }),
      new CsFor(null, null, [], new CsBlock([])),
      new CsSwitch(A, [new CsSwitchSection([new CsIntegerLiteral(1), null], [new CsBlock([new CsBreak()])]), new CsSwitchSection([new CsIntegerLiteral(2)], [new CsReturn(null)])]),
      new CsCheckedStatement(true, new CsBlock([new CsLocalDeclaration(CsType.Var, "x", new CsCheckedExpression(false, new CsArrayCreation(new CsType("int"), A, null)))])),
      new CsExpressionStatement(new CsAssignment(new CsName("_"), "=", new CsInvocation(new CsName("F"), [new CsOutArgument(new CsName("_")), new CsDefaultLiteral(), new CsDefaultExpression(new CsType("int"))]))),
      new CsBlock([new CsLocalDeclaration(new CsType("int[]"), "t", new CsArrayCreation(new CsType("int"), null, new CsArrayInitializer([new CsIntegerLiteral(1)]))) { Leading = new CsTrivia(true, ["note", ""]) }])
    ];
    foreach (var statement in statements) {
      var text = CSharpPrinter.Print(new CsMethod(["static"], CsType.Void, "M", [], [], new CsBlock([statement]), null), CSharpProfile.Default);
      var parsed = CSharpSyntaxReader.Member(text);
      Assert.That(SyntaxShape.Of(parsed), Is.EqualTo(SyntaxShape.Of(new CsMethod(["static"], CsType.Void, "M", [], [], new CsBlock([statement]), null))), text);
      Assert.That(CSharpPrinter.Print(parsed, CSharpProfile.Default), Is.EqualTo(text));
    }
  }

  [Test]
  public void CSharpDeclarations_PrintAndParseBack() {
    var record = new CsTypeDeclaration(["public", "readonly"], "record struct", "P", [new CsParameter(new CsType("int"), "X")], [], null);
    var type = new CsTypeDeclaration(["public"], "struct", "S", null, [new CsType("global::System.IEquatable<S>")], [
      new CsField(["public"], new CsType("int"), "X", null),
      new CsConstructor(["public"], "S", [new CsParameter(new CsType("int"), "x")], new CsBlock([])) { Leading = CsTrivia.Blank },
      new CsMethod(["public", "static"], new CsType("bool"), "operator ==", [], [new CsParameter(new CsType("S"), "l"), new CsParameter(new CsType("S"), "r")], null, new CsBooleanLiteral(true)) { Leading = CsTrivia.Blank },
      new CsMethod(["private", "static"], CsType.Void, "G", ["T"], [new CsParameter(new CsType("T"), "v", "out")], new CsBlock([]), null) { Leading = CsTrivia.Blank }
    ]) { OpenBlank = true };
    foreach (var (version, fileScoped) in new[] { ("14", true), ("2", false) }) {
      var unit = new CsCompilationUnit(["header"], [new CsNamespace("N.M", fileScoped, [record, type])]);
      var text = CSharpPrinter.Print(unit, CSharpProfile.Default);
      Assert.That(CSharpSyntaxReader.CompilationUnit(text, "14", out var problems) is { } parsed ? SyntaxShape.Of(parsed) : string.Join("; ", problems), Is.EqualTo(SyntaxShape.Of(unit)), text);
    }

    Assert.That(CSharpSyntaxReader.CompilationUnit("namespace N;", "9", out var refused), Is.Null);
    Assert.That(refused, Is.Not.Empty);
  }

  [Test]
  public void JavaScriptStatements_PrintAndParseBack() {
    JsStatement[] statements = [
      new JsIf(X, new JsReturn(Y), new JsReturn(Z)),
      new JsIf(X, new JsReturn(Y), new JsReturn(Z)) { ElseInline = true },
      new JsIf(X, new JsBlock([new JsBreak()]), new JsIf(Y, new JsBlock([new JsContinue()]), new JsBlock([new JsThrow(new JsNew(new JsIdentifier("RangeError"), [new JsString("x")]))]))),
      new JsWhile(X, new JsBlock([new JsExpressionStatement(new JsUpdate("++", false, Y))]) { Inline = true }),
      new JsDoWhile(new JsExpressionStatement(new JsUpdate("--", true, Y)), X),
      new JsDoWhile(new JsBlock([]), X),
      new JsFor(new JsVariableDeclaration("let", [new JsDeclarator("i", new JsNumber(0)), new JsDeclarator("j", null)]), X, new JsSequence([new JsUpdate("++", true, Y), Z]), new JsBlock([])),
      new JsFor(null, null, null, new JsBlock([new JsBreak()])),
      new JsSwitch(X, [new JsSwitchCase(new JsNumber(1), []), new JsSwitchCase(null, [new JsBlock([new JsBreak()])]), new JsSwitchCase(new JsString("2:3"), [new JsReturn(null)])]),
      new JsExpressionStatement(new JsObject([new JsProperty("x", X, true), new JsProperty("y", new JsArray([new JsNumber(1)]))]) { Multiline = true }) { Leading = new JsTrivia(2, ["note", ""]) },
      new JsFunctionDeclaration("f", ["a"], new JsBlock([new JsReturn(new JsIndex(new JsIdentifier("a"), new JsNumber(0)))]))
    ];
    foreach (var statement in statements) {
      var function = new JsFunctionDeclaration("m", [], new JsBlock([statement]));
      var text = JavaScriptPrinter.Print(function);
      var parsed = JavaScriptSyntaxReader.Statements(text).Single();
      Assert.That(SyntaxShape.Of(parsed), Is.EqualTo(SyntaxShape.Of(function)), text);
      Assert.That(JavaScriptPrinter.Print(parsed), Is.EqualTo(text));
    }

    var module = new JsProgram([], [new JsExport(new JsFunctionDeclaration("f", [], new JsBlock([])))], true);
    var printed = JavaScriptPrinter.Print(module, JavaScriptProfile.Default);
    Assert.That(SyntaxShape.Of(JavaScriptSyntaxReader.Program(printed, EcmaVersion.ES2022, true, out _)!), Is.EqualTo(SyntaxShape.Of(module)));
    Assert.That(JavaScriptSyntaxReader.Program("let x = 1;", EcmaVersion.ES5, false, out var problem), Is.Null);
    Assert.That(problem, Is.Not.Null);
  }

  [Test]
  public void Readers_RefuseWhatTheTreeDoesNotHave() {
    Assert.Throws<System.ArgumentException>(() => CSharpSyntaxReader.Expression("x => x"));
    Assert.Throws<System.ArgumentException>(() => CSharpSyntaxReader.Expression("a +"));
    Assert.Throws<System.ArgumentException>(() => CSharpSyntaxReader.Member("int X { get; }"));
    Assert.Throws<System.ArgumentException>(() => CSharpSyntaxReader.Member("void M( {"));
    Assert.Throws<System.ArgumentException>(() => CSharpSyntaxReader.CompilationUnit("", "no version", out _));
    Assert.Throws<System.ArgumentException>(() => JavaScriptSyntaxReader.Statements("class A {}"));
    Assert.Throws<System.ArgumentException>(() => JavaScriptSyntaxReader.Statements("let = ;"));
    Assert.Throws<System.ArgumentException>(() => JavaScriptSyntaxReader.Statements("x = { get y() { return 1; } };"));
  }

  [Test]
  public void LiteralReaders_RefuseWhatIsNoLiteral() {
    Assert.That(Polypiler.Core.Backends.CSharp.Printing.CSharpLiterals.ParseInteger("12a"), Is.Null);
    Assert.That(Polypiler.Core.Backends.CSharp.Printing.CSharpLiterals.ParseInteger("0b"), Is.Null);
    Assert.That(Polypiler.Core.Backends.CSharp.Printing.CSharpLiterals.ParseInteger("0xZZ"), Is.Null);
    Assert.That(Polypiler.Core.Backends.CSharp.Printing.CSharpLiterals.ParseInteger("1lu"), Is.EqualTo(((BigInteger)1, CsIntegerSuffix.UL)));
    Assert.That(Polypiler.Core.Backends.CSharp.Printing.CSharpLiterals.ParseReal("1"), Is.Null);
    Assert.That(Polypiler.Core.Backends.CSharp.Printing.CSharpLiterals.ParseReal("1.5x"), Is.Null);
    Assert.That(Polypiler.Core.Backends.CSharp.Printing.CSharpLiterals.ParseReal("2d"), Is.Null);
    Assert.That(Polypiler.Core.Backends.CSharp.Printing.CSharpLiterals.ParseReal("2.5d"), Is.EqualTo((2.5, false)));
    Assert.That(Polypiler.Core.Backends.JavaScript.Printing.JavaScriptLiterals.ParseNumber("0o17"), Is.EqualTo(15));
    Assert.That(Polypiler.Core.Backends.JavaScript.Printing.JavaScriptLiterals.ParseNumber("0b102"), Is.Null);
    Assert.That(Polypiler.Core.Backends.JavaScript.Printing.JavaScriptLiterals.ParseNumber("x"), Is.Null);
    Assert.That(Polypiler.Core.Backends.JavaScript.Printing.JavaScriptLiterals.ParseNumber("-NaN"), Is.NaN);
    Assert.That(Polypiler.Core.Backends.JavaScript.Printing.JavaScriptLiterals.ParseBigInt("12"), Is.Null);
    Assert.That(Polypiler.Core.Backends.JavaScript.Printing.JavaScriptLiterals.ParseBigInt("1xn"), Is.Null);
    Assert.That(Polypiler.Core.Backends.JavaScript.Printing.JavaScriptLiterals.ParseBigInt("0xZn"), Is.Null);
  }

}
