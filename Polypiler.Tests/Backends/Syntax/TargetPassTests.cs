using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.CSharp.Passes;
using Polypiler.Core.Backends.CSharp.Printing;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.JavaScript.Passes;
using Polypiler.Core.Backends.JavaScript.Printing;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Target;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Syntax;

/// <summary>
/// The target passes O0500 (helper inlining) and O0501 (unused declaration removal) of both backends,
/// on hand-built trees and on modules the emitters build, and the pipeline that runs them.
/// </summary>
[TestFixture]
public sealed class TargetPassTests {

  private static readonly JavaScriptProfile _es5 = JavaScriptProfile.Parse("es5", "cjs");

  #region JavaScript

  private static JsIdentifier Id(string name) => new(name);

  private static JsFunctionDeclaration Helper(string name, string[] parameters, JsExpression body) => new(name, parameters, new JsBlock([new JsReturn(body)])) { IsHelper = true };

  private static JsFunctionDeclaration Function(string name, params JsStatement[] body) => new(name, ["x"], new JsBlock(body));

  private static JsProgram Program(params JsStatement[] body) => new([], body, false);

  /// <summary>The program after the passes, printed, with what they reported.</summary>
  private static (string Text, List<string> Applied) Run(ITargetPass<JsProgram, JavaScriptProfile> pass, JsProgram program) {
    var applied = new List<string>();
    var result = pass.Run(program, _es5, applied.Add);
    return (JavaScriptPrinter.Print(result, _es5), applied);
  }

  /// <summary>A helper <c>$h(a, b)</c> that reads <c>a</c> twice and <c>b</c> once, called by <c>f</c> with the given arguments.</summary>
  private static string InlinedJavaScript(params JsExpression[] arguments) {
    var helper = Helper("$h", ["a", "b"], new JsBinary(new JsBinary(Id("a"), "+", Id("a")), "*", Id("b")));
    return Run(new JavaScriptHelperInlining(), Program(helper, Function("f", new JsReturn(new JsCall(Id("$h"), arguments))))).Text;
  }

  private static IEnumerable<TestCaseData> JavaScriptArguments() => [
    new TestCaseData((JsExpression)new JsNumber(3), (JsExpression)new JsNumber(4), "return (3 + 3) * 4;").SetName("JavaScript constant arguments are inlined"),
    new TestCaseData((JsExpression)Id("x"), (JsExpression)Id("y"), "return (x + x) * y;").SetName("JavaScript parameter and local arguments are inlined"),
    new TestCaseData((JsExpression)Id("x"), (JsExpression)new JsBinary(Id("x"), "|", new JsNumber(0)), "return (x + x) * (x | 0);").SetName("JavaScript pure argument read once is inlined"),
    new TestCaseData((JsExpression)new JsBinary(Id("x"), "|", new JsNumber(0)), (JsExpression)Id("x"), "return $h(x | 0, x);").SetName("JavaScript pure argument read twice is not inlined"),
    new TestCaseData((JsExpression)Id("x"), (JsExpression)new JsCall(Id("g"), []), "return $h(x, g());").SetName("JavaScript argument with an effect is not inlined"),
    new TestCaseData((JsExpression)Id("x"), (JsExpression)new JsUpdate("++", false, Id("x")), "return $h(x, x++);").SetName("JavaScript step argument is not inlined")
  ];

  [TestCaseSource(nameof(JavaScriptArguments))]
  public void JavaScriptInlining_DependsOnTheArgument(JsExpression first, JsExpression second, string expected)
    => Assert.That(InlinedJavaScript(first, second), Does.Contain(expected));

  [Test]
  public void JavaScriptInlining_DropsOnlyAnUnusedArgumentWithoutEffect() {
    var helper = Helper("$k", ["a", "b"], Id("a"));
    Assert.That(Run(new JavaScriptHelperInlining(), Program(helper, Function("f", new JsReturn(new JsCall(Id("$k"), [Id("x"), new JsBinary(Id("x"), "+", Id("x"))]))))).Text, Does.Contain("return x;"));
    Assert.That(Run(new JavaScriptHelperInlining(), Program(helper, Function("f", new JsReturn(new JsCall(Id("$k"), [Id("x"), new JsCall(Id("g"), [])]))))).Text, Does.Contain("return $k(x, g());"));
  }

  private static IEnumerable<TestCaseData> JavaScriptRefusals() => [
    new TestCaseData(Helper("$h", ["a"], new JsCall(Id("$h"), [Id("a")]))).SetName("JavaScript recursive helper is not inlined"),
    new TestCaseData(new JsFunctionDeclaration("$h", ["a"], new JsBlock([new JsExpressionStatement(Id("a")), new JsReturn(Id("a"))])) { IsHelper = true }).SetName("JavaScript multi-statement helper is not inlined"),
    new TestCaseData(Helper("$h", ["a"], new JsAssignment(Id("a"), "=", new JsNumber(1)))).SetName("JavaScript assigning helper is not inlined"),
    new TestCaseData(Helper("$h", ["a"], new JsMember(Id("this"), "a"))).SetName("JavaScript helper naming this is not inlined"),
    new TestCaseData(Helper("$h", ["a"], new JsBinary(Id("a"), "+", Id("k")))).SetName("JavaScript helper capturing a local is not inlined"),
    new TestCaseData(new JsFunctionDeclaration("$h", ["a"], new JsBlock([new JsReturn(Id("a"))]))).SetName("JavaScript function of the program is not inlined"),
    new TestCaseData(Helper("$h", ["a"], Enumerable.Range(0, 9).Aggregate((JsExpression)Id("a"), (sum, _) => new JsBinary(sum, "+", Id("a"))))).SetName("JavaScript helper over the budget is not inlined")
  ];

  [TestCaseSource(nameof(JavaScriptRefusals))]
  public void JavaScriptInlining_RefusesWhatItMayNotChange(JsFunctionDeclaration helper) {
    var caller = new JsFunctionDeclaration("f", ["x"], new JsBlock([new JsVariableDeclaration("var", [new JsDeclarator("k", null)]), new JsReturn(new JsCall(Id("$h"), [Id("x")]))]));
    var (text, applied) = Run(new JavaScriptHelperInlining(), Program(helper, caller));
    Assert.That(text, Does.Contain("return $h(x);"));
    Assert.That(applied, Is.Empty);
  }

  [Test]
  public void JavaScriptInlining_ReachesExportsAndTheModuleLevelAndWrongArity() {
    var helper = Helper("$h", ["a"], new JsUnary("-", Id("a")));
    var program = Program(helper, new JsExport(Function("f", new JsReturn(new JsCall(Id("$h"), [Id("x")])))),
      new JsVariableDeclaration("const", [new JsDeclarator("c", new JsCall(Id("$h"), [new JsNumber(-1)]))]),
      Function("g", new JsReturn(new JsCall(Id("$h"), [Id("x"), Id("x")]))));
    var (text, applied) = Run(new JavaScriptHelperInlining(), program);
    Assert.That(text, Does.Contain("return -x;").And.Contain("const c = -(-1);").And.Contain("return $h(x, x);"));
    Assert.That(applied, Is.EqualTo(new[] { "'$h' into 'f'", "'$h' into 'the module'" }));
    Assert.That(Run(new JavaScriptHelperInlining(), Program(Function("f"))).Applied, Is.Empty);
  }

  [Test]
  public void JavaScriptRemoval_RemovesWhatNothingNamesAndNothingElse() {
    var program = Program(
      new JsExpressionStatement(new JsString("use strict")),
      new JsFunctionDeclaration("$a", ["v"], new JsBlock([new JsReturn(Id("T"))])) { IsHelper = true, Leading = new JsTrivia(2, []) },
      Helper("$b", ["v"], new JsCall(Id("$a"), [Id("v")])),
      new JsVariableDeclaration("const", [new JsDeclarator("T", new JsArray([]))]) { Leading = JsTrivia.Blank },
      new JsVariableDeclaration("const", [new JsDeclarator("U", new JsNumber(1))]),
      Function("f", new JsReturn(Id("U"))) with { Leading = JsTrivia.None },
      new JsFunctionDeclaration("g", [], new JsBlock([])),
      new JsExpressionStatement(new JsAssignment(new JsMember(Id("exports"), "f"), "=", Id("f"))));
    var (text, applied) = Run(new JavaScriptUnusedDeclarations(), program);
    Assert.That(applied, Is.EqualTo(new[] { "'$b'", "'$a'", "'T'" }));
    Assert.That(text, Is.EqualTo("\"use strict\";\n\n\nconst U = 1;\nfunction f(x) {\n  return U;\n}\nfunction g() {\n}\nexports.f = f;\n"));
  }

  [Test]
  public void JavaScriptPasses_WorkInsideTheFunctionOfAScript() {
    var body = new JsBlock([Helper("$h", ["a"], new JsUnary("~", Id("a"))), Function("f", new JsReturn(new JsCall(Id("$h"), [Id("x")])))]);
    var script = Program(new JsVariableDeclaration("var", [new JsDeclarator("G", new JsCall(new JsParenthesized(new JsFunctionExpression(null, [], body)), []))]));
    var result = JavaScriptTargetPasses.Pipeline.Run(script, _es5, TargetPassOptions.Default);
    Assert.That(result.Applications.Select(application => application.ToString()), Is.EqualTo(new[] { "O0500 helper inlining: '$h' into 'f'", "O0501 unused declaration removal: '$h'" }));
    Assert.That(JavaScriptPrinter.Print(result.Tree, _es5), Is.EqualTo("var G = (function () {\n  function f(x) {\n    return ~x;\n  }\n})();\n"));
  }

  [Test]
  public void InliningScenario_OnTheEmittedModule() {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public static uint F(uint x) => System.Numerics.BitOperations.RotateLeft(x, 8);"));
    Assert.That(JavaScriptEmitter.Emit(module, _es5).Single().Content, Does.Contain("return $rotl(x, 8);"));
    var inlined = JavaScriptEmitter.Emit(module, _es5, FloatMode.Accurate, null, TargetPassOptions.Default).Files.Single().Content;
    Assert.That(inlined, Does.Not.Contain("$rotl").And.Contain("return ((x << 8) | (x >>> (-8 & 31))) >>> 0;"));
    var kept = JavaScriptEmitter.Emit(module, _es5, FloatMode.Accurate, null, new TargetPassOptions { Disabled = new HashSet<string> { "O0500" } });
    Assert.That(kept.Files.Single().Content, Does.Contain("function $rotl(").And.Contain("return $rotl(x, 8);"));
    Assert.That(kept.Applications, Is.Empty);
    var csharp = CSharpEmitter.Emit(module, CSharpProfile.Net20, FloatMode.Accurate, null, null, TargetPassOptions.Default).Files.Single().Content;
    Assert.That(csharp, Does.Contain("return (uint)((x << 8) | (x >> (32 - (8 & 31))));").And.Not.Contain("private static uint RotateLeft"));
  }

  #endregion

  #region C#

  private static CsName Name(string name) => new(name);

  private static CsMethod CsHelper(string name, string returns, (string Type, string Name)[] parameters, CsExpression body)
    => new(["private", "static"], new CsType(returns), name, [], [.. parameters.Select(parameter => new CsParameter(new CsType(parameter.Type), parameter.Name))], new CsBlock([new CsReturn(body)]), null) {
      IsHelper = true
    };

  private static CsMethod CsFunction(string name, CsExpression result, params CsStatement[] before)
    => new(["public", "static"], new CsType("int"), name, [], [new CsParameter(new CsType("int"), "x")], new CsBlock([.. before, new CsReturn(result)]), null);

  private static CsCompilationUnit Unit(params CsMember[] members)
    => new([], [new CsTypeDeclaration(["public", "static"], "class", "C", null, [], [.. members.Select(member => member with { Leading = CsTrivia.Blank })])]);

  private static (string Text, List<string> Applied) Run(ITargetPass<CsCompilationUnit, CSharpProfile> pass, CsCompilationUnit unit) {
    var applied = new List<string>();
    return (CSharpPrinter.Print(pass.Run(unit, CSharpProfile.Net20, applied.Add), CSharpProfile.Net20), applied);
  }

  private static CsName Typed(string name, Polypiler.Core.IR.IrType type) => new(name) { Type = type };

  private static IEnumerable<TestCaseData> CSharpArguments() => [
    new TestCaseData((CsExpression)Typed("x", Polypiler.Core.IR.IrType.I32), (CsExpression)new CsIntegerLiteral(3), "return (int)(x + x * 3);").SetName("CSharp variable and constant arguments are inlined"),
    new TestCaseData((CsExpression)Typed("x", Polypiler.Core.IR.IrType.I32), (CsExpression)new CsIntegerLiteral(3, CsIntegerSuffix.U), "return $h(x, 3u);").SetName("CSharp argument the call converts to no other type is not inlined"),
    new TestCaseData((CsExpression)Typed("x", Polypiler.Core.IR.IrType.I16), (CsExpression)new CsBinary(Typed("x", Polypiler.Core.IR.IrType.I32), "&", new CsIntegerLiteral(1)) { Type = Polypiler.Core.IR.IrType.I32 }, "return (int)((int)x + (int)x * (x & 1));").SetName("CSharp narrow argument is cast and a pure one read once is inlined"),
    new TestCaseData((CsExpression)new CsBinary(Typed("x", Polypiler.Core.IR.IrType.I32), "&", new CsIntegerLiteral(1)) { Type = Polypiler.Core.IR.IrType.I32 }, (CsExpression)Typed("x", Polypiler.Core.IR.IrType.I32), "return $h(x & 1, x);").SetName("CSharp pure argument read twice is not inlined"),
    new TestCaseData((CsExpression)Typed("x", Polypiler.Core.IR.IrType.I32), (CsExpression)new CsInvocation(Name("G"), []) { Type = Polypiler.Core.IR.IrType.I32 }, "return $h(x, G());").SetName("CSharp argument with an effect is not inlined"),
    new TestCaseData((CsExpression)new CsIntegerLiteral(1), (CsExpression)new CsIntegerLiteral(2), "return $h(1, 2);").SetName("CSharp constant arguments only are not inlined"),
    new TestCaseData((CsExpression)Name("x"), (CsExpression)new CsIntegerLiteral(2), "return $h(x, 2);").SetName("CSharp argument of unknown type is not inlined"),
    new TestCaseData((CsExpression)Typed("x", Polypiler.Core.IR.IrType.I32), (CsExpression)new CsIntegerLiteral(1L << 40), "return $h(x, 1099511627776);").SetName("CSharp constant of a wider type is not inlined")
  ];

  /// <summary>A helper <c>$h(int a, int b)</c> returning <c>a + a * b</c>, called by <c>F</c> with the given arguments.</summary>
  [TestCaseSource(nameof(CSharpArguments))]
  public void CSharpInlining_DependsOnTheArgument(CsExpression first, CsExpression second, string expected) {
    var helper = CsHelper("$h", "int", [("int", "a"), ("int", "b")], new CsBinary(Name("a"), "+", new CsBinary(Name("a"), "*", Name("b"))));
    Assert.That(Run(new CSharpHelperInlining(), Unit(helper, CsFunction("F", new CsInvocation(Name("$h"), [first, second])))).Text, Does.Contain(expected));
  }

  private static IEnumerable<TestCaseData> CSharpRefusals() => [
    new TestCaseData(CsHelper("$h", "int", [("int", "a")], new CsInvocation(Name("$h"), [Name("a")]))).SetName("CSharp recursive helper is not inlined"),
    new TestCaseData(new CsMethod(["private", "static"], new CsType("int"), "$h", [], [new CsParameter(new CsType("int"), "a")],
      new CsBlock([new CsExpressionStatement(new CsAssignment(Name("a"), "=", new CsIntegerLiteral(1))), new CsReturn(Name("a"))]), null) { IsHelper = true }).SetName("CSharp multi-statement helper is not inlined"),
    new TestCaseData(CsHelper("$h", "int", [("int", "a")], new CsAssignment(Name("a"), "=", new CsIntegerLiteral(1)))).SetName("CSharp assigning helper is not inlined"),
    new TestCaseData(CsHelper("$h", "int", [("int", "a")], new CsBinary(Name("a"), "+", Name("k")))).SetName("CSharp helper capturing a local is not inlined"),
    new TestCaseData(CsHelper("$h", "int", [("int", "a")], new CsBinary(Name("a"), "/", new CsIntegerLiteral(2)))).SetName("CSharp dividing helper with a constant argument is not inlined"),
    new TestCaseData(CsHelper("$h", "void", [("int", "a")], Name("a"))).SetName("CSharp void helper is not inlined"),
    new TestCaseData(new CsMethod(["private", "static"], new CsType("int"), "$h", ["T"], [new CsParameter(new CsType("int"), "a")], null, Name("a")) { IsHelper = true }).SetName("CSharp generic helper is not inlined"),
    new TestCaseData(new CsMethod(["private", "static"], new CsType("int"), "$h", [], [new CsParameter(new CsType("int"), "a", "out")], null, Name("a")) { IsHelper = true }).SetName("CSharp helper with an out parameter is not inlined"),
    new TestCaseData(CsHelper("$h", "int", [("int", "a")], Enumerable.Range(0, 9).Aggregate((CsExpression)Name("a"), (sum, _) => new CsBinary(sum, "+", Name("a"))))).SetName("CSharp helper over the budget is not inlined")
  ];

  [TestCaseSource(nameof(CSharpRefusals))]
  public void CSharpInlining_RefusesWhatItMayNotChange(CsMethod helper) {
    var caller = CsFunction("F", new CsInvocation(Name("$h"), [new CsIntegerLiteral(5)]), new CsLocalDeclaration(new CsType("int"), "k", null), new CsLocalDeclaration(new CsType("int"), "y", Typed("x", Polypiler.Core.IR.IrType.I32)));
    var (text, applied) = Run(new CSharpHelperInlining(), Unit(helper, caller));
    Assert.That(applied, Is.Empty, text);
  }

  [Test]
  public void CSharpInlining_TellsOverloadsApart() {
    var narrow = CsHelper("R", "uint", [("uint", "v")], new CsCast(new CsType("uint"), new CsBinary(Name("v"), "<<", new CsIntegerLiteral(1))));
    var wide = CsHelper("R", "ulong", [("ulong", "v")], new CsBinary(Name("v"), "<<", new CsIntegerLiteral(1)));
    var caller = CsFunction("F", new CsBinary(
      new CsInvocation(Name("R"), [Typed("x", Polypiler.Core.IR.IrType.U32)]), "+", new CsBinary(
        new CsInvocation(Name("R"), [Typed("y", Polypiler.Core.IR.IrType.U64)]), "+", new CsInvocation(Name("R"), [Typed("z", Polypiler.Core.IR.IrType.U16)]))));
    var (text, applied) = Run(new CSharpHelperInlining(), Unit(narrow, wide, caller));
    Assert.That(text, Does.Contain("return (uint)(x << 1) + ((ulong)(y << 1) + R(z));"));
    Assert.That(applied, Has.Count.EqualTo(2));
    Assert.That(Run(new CSharpHelperInlining(), new CsCompilationUnit([], [])).Applied, Is.Empty);
    Assert.That(Run(new CSharpHelperInlining(), Unit(CsFunction("F", Name("x")))).Applied, Is.Empty);
  }

  [Test]
  public void CSharpRemoval_RemovesWhatNothingNamesAndNothingElse() {
    var unit = Unit(
      new CsField(["private", "const"], new CsType("int"), "T", new CsIntegerLiteral(1)),
      new CsField(["private", "const"], new CsType("int"), "U", new CsIntegerLiteral(2)),
      new CsField(["public", "const"], new CsType("int"), "V", new CsIntegerLiteral(3)),
      CsHelper("A", "int", [("int", "a")], Name("T")),
      CsHelper("B", "int", [("int", "a")], new CsInvocation(Name("A"), [Name("a")])),
      CsFunction("F", Name("U")),
      new CsMethod(["private", "static"], CsType.Void, "G", [], [], new CsBlock([]), null));
    var inNamespace = unit with { Members = [new CsNamespace("N", true, unit.Members)] };
    var (text, applied) = Run(new CSharpUnusedDeclarations(), inNamespace);
    Assert.That(applied, Is.EqualTo(new[] { "'B'", "'A'", "'T'" }));
    Assert.That(text, Does.Not.Contain(" T = ").And.Not.Contain(" A(").And.Not.Contain(" B(").And.Contain(" U = ").And.Contain(" V = ").And.Contain(" G()"));
    Assert.That(Run(new CSharpUnusedDeclarations(), new CsCompilationUnit([], [])).Applied, Is.Empty);
  }

  [Test]
  public void CSharpRemoval_KeepsTheBlankLineOfWhatItRemoves() {
    var unit = new CsCompilationUnit([], [new CsTypeDeclaration(["public", "static"], "class", "C", null, [], [
      CsHelper("A", "int", [], new CsIntegerLiteral(1)) with { Leading = CsTrivia.Blank },
      CsFunction("F", Name("x"))
    ])]);
    Assert.That(Run(new CSharpUnusedDeclarations(), unit).Text, Is.EqualTo("public static class C {\n\n  public static int F(int x) {\n    return x;\n  }\n\n}\n"));
  }

  #endregion

  #region Pipeline and command line

  private sealed class Pass(string id) : ITargetPass<string, int> {
    public string Id => id;
    public string Name => "append";
    public string Run(string tree, int profile, Action<string> applied) {
      applied(id);
      return tree + id;
    }
  }

  [Test]
  public void Pipeline_RunsThePassesInOrder_UnlessSwitchedOff() {
    var pipeline = new TargetPipeline<string, int>([new Pass("O0001"), new Pass("O0002")]);
    Assert.That(pipeline.Ids, Is.EqualTo(new[] { "O0001", "O0002" }));
    Assert.That(pipeline.Run("", 0, TargetPassOptions.Default).Tree, Is.EqualTo("O0001O0002"));
    Assert.That(pipeline.Run("", 0, TargetPassOptions.None), Has.Property("Tree").EqualTo("").And.Property("Applications").Empty);
    var disabled = pipeline.Run("", 0, new TargetPassOptions { Disabled = new HashSet<string> { "O0001" } });
    Assert.That(disabled.Tree, Is.EqualTo("O0002"));
    Assert.That(disabled.Applications.Single().ToString(), Is.EqualTo("O0002 append: O0002"));
    Assert.Throws<ArgumentNullException>(() => pipeline.Run("", 0, null!));
  }

  [Test]
  public void Pipeline_RefusesABadOrRepeatedNumber() {
    Assert.Throws<ArgumentException>(() => _ = new TargetPipeline<string, int>([new Pass("O1")]));
    Assert.Throws<ArgumentException>(() => _ = new TargetPipeline<string, int>([new Pass("O0001"), new Pass("O0001")]));
    Assert.That(CSharpTargetPasses.Pipeline.Ids.Concat(JavaScriptTargetPasses.Pipeline.Ids).Distinct(), Is.EqualTo(new[] { "O0500", "O0501" }));
    Assert.That(Polypiler.Core.Optimize.RuleTable.Default.Ids, Does.Not.Contain("O0500").And.Not.Contain("O0501"));
  }

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-passes-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private (int ExitCode, string Error, string Text) Translate(params string[] options) {
    var source = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(source, PrototypeFixture.Wrap("public static uint F(uint x) => System.Numerics.BitOperations.RotateLeft(x, 8);"));
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    using var printed = new StringWriter();
    using var error = new StringWriter();
    var exitCode = TranslateCommand.Run([source, "--from", "csharp", "--to", "javascript", "--profile", "es5", "--output", output, .. options], printed, error);
    var text = Directory.Exists(output) ? File.ReadAllText(Directory.GetFiles(output).Single(path => Path.GetFileName(path) != Manifest.FileName)) : "";
    return (exitCode, error.ToString(), text);
  }

  [TestCase("O9998", "unknown optimization 'O9998'; the pipeline has O0100")]
  [TestCase("O0500x", "'O0500x' is not an optimization number of the form O####")]
  public void UnknownTargetPass_IsAUsageError(string id, string message) {
    var (exitCode, error, _) = this.Translate("--disable-opt", id);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(error, Does.Contain("PLY0003: " + message));
  }

  [Test]
  public void TargetPasses_FollowTheSwitchesOfTheCommandLine() {
    var (exitCode, error, text) = this.Translate("--explain-opt");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success));
    Assert.That(error, Does.Contain("opt O0500 helper inlining: '$rotl' into 'F'").And.Contain("opt O0501 unused declaration removal: '$rotl'"));
    Assert.That(text, Does.Not.Contain("$rotl"));
    Assert.That(this.Translate("--disable-opt", "O0500").Text, Does.Contain("return $rotl(x, 8);"));
    Assert.That(this.Translate("--opt", "none").Text, Does.Contain("return $rotl(x, 8);"));
    Assert.That(this.Translate("--disable-opt", "O0501").Text, Does.Contain("function $rotl(").And.Not.Contain("return $rotl("));
    Assert.That(this.Translate().Error, Does.Not.Contain("O0500"));
  }

  #endregion

}
