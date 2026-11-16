using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Passes;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The parts of the Rust backend around the emitter, one rule at a time: the reader of the parse-back
/// sweep on the forms the emitter does not write, the liveness analysis behind the dead-store check,
/// the names and the version of a profile, the lints a crate allows, the rewriter, the inlining's
/// refusals, and the public entry points of the emitter on modules they refuse.
/// </summary>
[TestFixture]
public sealed class RustReaderAndAnalysisTests {

  private static RsPath P(string name) => new(name);

  private static RsLiteral L(string text) => new(text);

  // The reader.

  [TestCase("pub(crate) fn f() {}\n")]
  [TestCase("fn f() -> i32 {\n    -!a\n}\n")]
  [TestCase("fn f() {\n    loop {\n        continue;\n    }\n}\n")]
  [TestCase("fn f() {\n    'l: loop {\n        continue 'l;\n    }\n}\n")]
  [TestCase("fn f() {\n    return;\n}\n")]
  [TestCase("fn f() -> u32 {\n    match a {\n        _ => return,\n    }\n}\n")]
  [TestCase("fn f() -> bool {\n    a as u32 == b\n}\n")]
  [TestCase("fn f() {\n    a = b..c;\n}\n")]
  [TestCase("fn f() {\n    for i in 0..n as usize {}\n}\n")]
  [TestCase("fn f() -> u32 {\n    (a = b) as u32\n}\n")]
  [TestCase("fn f() -> i32 {\n    fn g(m: i32) -> i32 {\n        m\n    }\n    g(1)\n}\n")]
  [TestCase("fn f() -> i32 {\n    let g: Rc<dyn Fn(i32, i32) -> i32> = Rc::new(move |a: i32, b: i32| a + b);\n    g(1, 2)\n}\n")]
  [TestCase("fn f() -> i32 {\n    let g: Option<Rc<dyn Fn() -> i32>> = Some(Rc::new(|| 1));\n    ply_non_null(&g)() + h()() + t[0]() + (u)()\n}\n")]
  public void Reader_ReadsWhatThePrinterWritesBack(string text) => Assert.That(RustPrinter.Print(RustSyntaxReader.Read(text)), Is.EqualTo(text));

  [TestCase("pub(crate fn f() {}", "Expected ')'")]
  [TestCase("fn f() { ; }", "Unexpected ';'")]
  [TestCase("//! only a comment", null)]
  public void Reader_RefusesOrReadsTheEdges(string text, string? message) {
    if (message is null)
      Assert.That(RustSyntaxReader.Read(text).Items, Is.Empty);
    else
      Assert.That(() => RustSyntaxReader.Read(text), Throws.TypeOf<FormatException>().With.Message.Contains(message));
  }

  // The liveness analysis.

  private static RsFunction Function(params RsStatement[] statements) => new("f", "", [], null, new RsBlock(statements, null));

  [Test]
  public void Liveness_SeesThroughEveryExit() {
    // A declaration without a value, a break with a value out of a labelled loop, a continue, a bare return.
    var function = Function(
      new RsLet("a", true, "u32", null),
      new RsLet("b", true, null, L("1")),
      new RsExpressionStatement(new RsAssign(P("a"), "=", L("2"))),
      new RsLet("c", false, null, new RsLoop("l", new RsBlock([
        new RsExpressionStatement(new RsIf(P("a"), new RsBlock([new RsExpressionStatement(new RsContinue("l"))], null), null)),
        new RsExpressionStatement(new RsBreak("l", P("b")))
      ], null))),
      new RsExpressionStatement(new RsMacro("assert_eq", [P("c"), L("1")])),
      new RsExpressionStatement(new RsReturn(null)));
    Assert.That(RustLiveness.DeadStores(function), Is.Empty);
    var dead = Function(new RsLet("x", true, null, L("1")), new RsExpressionStatement(new RsAssign(P("x"), "=", L("2"))), new RsExpressionStatement(new RsReturn(P("x"))));
    Assert.That(RustLiveness.DeadStores(dead), Is.EqualTo(new[] { "x" }));
  }

  [Test]
  public void Liveness_CountsWhatAClosureNamesAsRead() {
    // The closure returns from itself, not from the function: the value it reads stays live.
    var closure = new RsClosure(true, [], new RsBlockExpression(new RsBlock([new RsExpressionStatement(new RsReturn(P("x")))], null)));
    var function = Function(new RsLet("x", true, null, L("1")), new RsLet("g", false, null, closure), new RsExpressionStatement(new RsCall(P("g"), [])));
    Assert.That(RustLiveness.DeadStores(function), Is.Empty);
  }

  [Test]
  public void Liveness_OfAnExitWithoutItsLoop_TakesWhatFollows() {
    // The reader accepts a break and a continue outside a loop; the analysis treats them as falling through.
    var function = Function(new RsLet("x", false, null, L("1")), new RsExpressionStatement(new RsBreak(null, null)), new RsExpressionStatement(new RsContinue(null)), new RsExpressionStatement(new RsMacro("assert_eq", [P("x"), L("1")])));
    Assert.That(RustLiveness.DeadStores(function), Is.Empty);
    Assert.That(() => RustLiveness.DeadStores(null!), Throws.ArgumentNullException);
  }

  // Names and profiles.

  [TestCase("HTTPServer", "http_server")]
  [TestCase("Utf8Text", "utf8_text")]
  [TestCase("ABC", "abc")]
  public void Snake_SplitsAcronyms(string name, string expected) => Assert.That(RustNames.Snake(name, 2024), Is.EqualTo(expected));

  [TestCase("1.")]
  [TestCase("1.x")]
  [TestCase("1.2.3.4")]
  public void RustVersion_ThatIsNoVersion_IsRefused(string version) {
    Assert.That(RustProfile.TryParse("rust2024", version, out var profile, out var error), Is.False);
    Assert.That(profile, Is.Null);
    Assert.That(error, Is.Not.Null);
  }

  // The rewriter.

  [Test]
  public void Rewriter_ReachesLoopsAndInlineModules() {
    var file = new RsFile([], [], [new RsInlineModule("m", [new RsFunction("f", "", [], null, new RsBlock([new RsExpressionStatement(new RsLoop(null, new RsBlock([new RsExpressionStatement(new RsAssign(P("a"), "=", L("1")))], null)))], null))])]);
    var rewritten = RustSyntaxRewriter.Rewrite(file, node => node is RsPath { Name: "a" } ? P("b") : node);
    Assert.That(RustPrinter.Print(rewritten), Does.Contain("b = 1;"));
  }

  // The inlining's refusals.

  private static RustCrate Crate(params RsItem[] items) => new("p", "", new RsFile([], [], []), "m", new RsFile([], [], items));

  private static RsFunction Helper(string name, RsExpression body) => new(name, "", [new RsParameter("p", "i32")], "i32", new RsBlock([], body)) { IsHelper = true };

  private static RsFunction Caller(params RsExpression[] arguments) => new("f", "pub", [new RsParameter("a", "i32")], "i32", new RsBlock([], new RsCall(P("h"), arguments)));

  [Test]
  public void Inlining_KeepsAHelperThatAssignsBreaksOrCallsItself() {
    RsExpression[] bodies = [
      new RsBlockExpression(new RsBlock([], new RsAssign(P("p"), "=", L("1")))),
      new RsLoop(null, new RsBlock([new RsExpressionStatement(new RsBreak(null, P("p")))], null)),
      new RsLoop(null, new RsBlock([new RsExpressionStatement(new RsContinue(null))], null)),
      new RsBlockExpression(new RsBlock([], new RsReturn(P("p")))),
      new RsCall(P("h"), [P("p")])
    ];
    foreach (var body in bodies) {
      var crate = Crate(Helper("h", body), Caller(P("a")));
      Assert.That(new RustHelperInlining().Run(crate, RustProfile.Default, _ => { }), Is.SameAs(crate), RustPrinter.Print(body));
    }
  }

  [Test]
  public void Inlining_OfANegativeLiteral_ParenthesizesIt() {
    var inlined = new RustHelperInlining().Run(Crate(Helper("h", new RsMethodCall(P("p"), "abs", [])), Caller(L("-1"))), RustProfile.Default, _ => { });
    Assert.That(RustPrinter.Print(inlined.Module.Items.OfType<RsFunction>().Single(function => function.Name == "f")), Does.Contain("(-1).abs()"));
  }

  // The entry points of the emitter.

  private static IrModule Invalid() {
    var a = new IrLocal("a", IrType.I32);
    return new IrModule("bad", [new IrFunction("F", true, IrType.I32, [a], [], new IrBlock([new IrReturn(new IrConst(IrType.Bool, 1))]))]);
  }

  [Test]
  public void EntryPoints_RefuseAModuleTheVerifierRefuses() {
    Assert.That(RustEmitter.Check(Invalid(), RustProfile.Default), Is.Not.Empty);
    Assert.That(() => RustEmitter.Emit(Invalid(), RustProfile.Default), Throws.ArgumentException.With.Message.Contains("cannot be emitted"));
    Assert.That(() => RustEmitter.Build(Invalid(), RustProfile.Default, null, TargetPassOptions.Default, null), Throws.ArgumentException);
    Assert.That(() => RustEmitter.Files(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void EntryPoints_WriteTheSameCrate() {
    var module = RustCrates.Prepared(RustCrates.Import("public static int F(int a) => a + 1;"));
    var files = RustEmitter.Emit(module, RustProfile.Default);
    var built = RustEmitter.Build(module, RustProfile.Default, null, TargetPassOptions.None, null).Tree;
    Assert.That(RustEmitter.Files(built).Select(file => file.Content), Is.EqualTo(files.Select(file => file.Content)));
  }

  // The lints a crate allows.

  private static string Lib(string methods) => RustCrates.Text(RustCrates.Emit(RustCrates.Import(methods), level: OptimizationLevel.None), "src/lib.rs");

  [TestCase("public static double F(double x) => x * 0.0;", "clippy::erasing_op")]
  [TestCase("public static double F(double x) => x - x;", "clippy::eq_op")]
  [TestCase("public static void F() { while (true) { } }", "clippy::empty_loop")]
  public void Lint_ThatTheCodeNeeds_IsAllowed(string method, string lint) => Assert.That(Lib(method), Does.Contain(lint));

}
