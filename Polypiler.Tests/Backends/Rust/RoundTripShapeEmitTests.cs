using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The shapes the Rust fixtures <c>types</c>, <c>edges</c> and <c>reassign</c> bring back through the
/// backend (#1393): a second variable of an array or a collection that borrows it, a choice of two
/// new collections, a loop that runs once, an empty option handed on with <c>?</c>, a struct literal
/// in a condition, and what rustfmt and clippy ask of them.
/// </summary>
[TestFixture]
public sealed class RoundTripShapeEmitTests {

  private static IrModule FromRust(string source) => RustFrontend.Import("snippet.rs", source, OverflowChecks.Panic).Module!;

  private static string Rust(string source, RustCapabilities? capabilities = null)
    => RustCrates.ModuleText(RustCrates.Emit(FromRust(source), level: OptimizationLevel.None, capabilities: capabilities));

  private static string CSharp(string methods) => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), level: OptimizationLevel.None));

  private static string[] Problems(string methods) => [.. RustCrates.Check(RustCrates.Import(methods)).Select(problem => problem.Message)];

  [Test]
  public void ListOfASecondVariable_BorrowsItExclusively_WhereItIsChangedThrough() {
    var text = CSharp("public static int F(byte k) { var v = new System.Collections.Generic.List<byte> { 9 }; var r = v; r.Clear(); r.Add(k); return v.Count; }");
    Assert.That(text, Does.Contain("let mut v: Vec<u8> = Vec::new();\n    v.push(9);\n    let r: &mut Vec<u8> = &mut v;\n    r.clear();\n    r.push(k);"));
  }

  [Test]
  public void ListOfASecondVariable_OnlyRead_IsASharedBorrow() {
    var text = CSharp("public static int F() { var v = new System.Collections.Generic.List<int> { 9 }; var r = v; var n = r.Count; v.Add(1); return n + v.Count; }");
    Assert.That(text, Does.Contain("let r: &Vec<i32> = &v;"));
    Assert.That(text, Does.Contain("let mut v: Vec<i32>"));
  }

  [Test]
  public void BorrowHandedOn_IsTheSameBorrow() {
    var text = Rust("pub fn f(k: u8) -> usize { let mut w = vec![1u8]; let first = &mut w; let again = &mut *first; *again = vec![k; 3]; w.len() }");
    Assert.That(text, Does.Contain("let first: &mut Vec<u8> = &mut w;"));
    Assert.That(text, Does.Contain("let again: &mut Vec<u8> = first;"));
  }

  [Test]
  public void ListBorrowedPassedOn_IsPassedAsTheBorrowItIs() {
    var text = CSharp("public static int F() { var v = new System.Collections.Generic.List<int> { 9 }; var r = v; Add(r); return v.Count; } static void Add(System.Collections.Generic.List<int> l) => l.Add(1);");
    Assert.That(text, Does.Contain("add(r);"));
  }

  [TestCase("public static int F() { var v = new System.Collections.Generic.List<int> { 9 }; var r = v; r.Add(1); v.Add(2); return r.Count; }", TestName = "ListReadWhileTheBorrowLives_IsShared")]
  [TestCase("public static int F() { var v = new System.Collections.Generic.List<int> { 9 }; var r = v; r = new System.Collections.Generic.List<int>(); r.Add(1); return v.Count; }", TestName = "BorrowGivenAnotherList_IsShared")]
  [TestCase("public static System.Func<int> F() { var v = new System.Collections.Generic.List<int> { 9 }; var r = v; System.Func<int> g = () => r.Count; v.Add(1); return g; }", TestName = "BorrowInALambda_IsShared")]
  public void ListHeldTwiceAtOnce_IsRefused(string methods)
    => Assert.That(Problems(methods), Is.Not.Empty);

  [Test]
  public void ChoiceOfTwoNewLists_IsAList()
    => Assert.That(Rust("pub fn f(c: bool) -> usize { let list = if c { vec![5u32] } else { vec![] }; list.len() }"), Does.Contain("let list: Vec<u32> = if c {"));

  [Test]
  public void ChoiceOfAListHeldElsewhere_IsRefused()
    => Assert.That(Problems("public static int F(bool c) { var a = new System.Collections.Generic.List<int>(); var b = c ? a : new System.Collections.Generic.List<int>(); b.Add(1); return a.Count; }"),
      Has.Some.Contain("'a collection held by two variables'"));

  [Test]
  public void LoopThatRunsOnce_IsABlock() {
    var text = Rust("pub fn f(x: i32) -> i32 { let r = loop { if x > 10 { return x; } break x * 2; }; r + 1 }");
    Assert.That(text, Does.Not.Contain("loop {"));
    Assert.That(text, Does.Contain("    {\n        if x > 10 {\n            return x;\n        }\n"));
  }

  [Test]
  public void LoopThatRunsOnce_LeftEarly_IsALabelledBlock() {
    var text = Rust("pub fn f(x: i32) -> i32 { let r = loop { if x > 10 { break 1; } if x < 0 { return x; } break 2; }; r + 1 }");
    Assert.That(text, Does.Not.Contain("loop {"));
    Assert.That(text, Does.Match(@"'b\w*: \{"));
    Assert.That(text, Does.Match(@"break 'b\w*;"));
  }

  [Test]
  public void LoopThatRunsOnce_WithoutLabelledBlocks_StaysALoop() {
    var without = RustCapabilities.Default.With(RustCapabilities.LabeledBlock, null);
    Assert.That(Rust("pub fn f(x: i32) -> i32 { let r = loop { if x > 10 { return x; } break x * 2; }; r + 1 }", without), Does.Contain("loop {"));
  }

  [Test]
  public void LoopThatMayRunAgain_StaysALoop()
    => Assert.That(CSharp("public static int F(int x) { while (true) { x++; if (x > 10) continue; break; } return x; }"), Does.Contain("loop {"));

  [Test]
  public void EmptyOptionHandedOn_IsTheQuestionMark() {
    var text = Rust("pub fn quarter(x: u32) -> Option<u32> { let h = if x % 2 == 0 { Some(x / 2) } else { None }?; if h % 2 == 0 { Some(h / 2) } else { None } }");
    Assert.That(text, Does.Contain("let h: u32 = tried?;"));
    Assert.That(text, Does.Not.Contain("is_none()"));
  }

  [Test]
  public void OptionReadAfterwards_KeepsTheTest() {
    var text = CSharp("public static int? F(int? a) { if (a == null) return null; var v = a.Value; return v + (a ?? 0); }");
    Assert.That(text, Does.Not.Contain("?;"));
  }

  [Test]
  public void StructLiteral_InEveryConditionPosition_IsParenthesized() {
    var literal = new RsStructLiteral("P", [new RsFieldValue("x", new RsLiteral("0"))]);
    RsExpression Field() => new RsField(literal, "x");
    var conditions = new RsExpression[] {
      new RsBinary(new RsPath("a"), "==", Field()),
      new RsUnary("!", new RsMethodCall(literal, "is_empty", [])),
      new RsBinary(new RsCast(Field(), "u8"), "==", new RsIndex(new RsField(literal, "s"), new RsLiteral("0"))),
      literal
    };
    foreach (var condition in conditions) {
      var printed = RustPrinter.Print(new RsFunction("f", "", [], null, new RsBlock([new RsExpressionStatement(new RsIf(condition, new RsBlock([], null), null))], null)));
      Assert.That(printed, Does.Contain("(P { x: 0 })"), printed);
      Assert.That(RustSyntaxReader.Read(printed).Items, Has.Count.EqualTo(1));
    }

    var call = RustPrinter.Print(new RsFunction("f", "", [], null, new RsBlock([new RsExpressionStatement(new RsIf(new RsCall(new RsPath("g"), [literal]), new RsBlock([], null), null))], null)));
    Assert.That(call, Does.Contain("g(P { x: 0 })"));
  }

  [Test]
  public void ConditionWhoseLastLineIsIndentedPastTheKeyword_PutsTheBraceOnItsOwnLine() {
    var text = Rust("pub fn f(a: Option<u32>, b: Option<u32>, r: Result<u32, u32>, s: Result<u32, u32>) -> u32 { "
      + "((a == b) as u32) * 1000 + ((r == s) as u32) * 100 }");
    Assert.That(text, Does.Not.Match(@"\n {12}\}\) \{"));
    Assert.That(text, Does.Match(@"\n {12}\}\)\n {8}\{\n"));
  }

  [Test]
  public void ElementsOfTwoArraysSwapped_AllowManualSwap() {
    var files = RustCrates.Emit(RustCrates.Import("public static int F() { var a = new int[] { 1, 2 }; var b = new int[] { 3, 4 }; var t = a[0]; a[0] = b[1]; b[1] = t; return a[0] + b[1]; }"), level: OptimizationLevel.None);
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.Contain("clippy::manual_swap"));
  }

  [Test]
  public void UnsignedZeroWithItsSuffix_AllowsTheExtremeComparison() {
    var lints = RustEmitter.Lints.Needed(new RsFile([], [], [new RsFunction("f", "", [], null, new RsBlock([], new RsBinary(new RsLiteral("0u64") { Type = IrType.U64 }, ">", new RsPath("n") { Type = IrType.U64 }) { Type = IrType.Bool }))]));
    Assert.That(lints, Does.Contain("clippy::absurd_extreme_comparisons").And.Contain("unused_comparisons"));
    var signed = RustEmitter.Lints.Needed(new RsFile([], [], [new RsFunction("f", "", [], null, new RsBlock([], new RsBinary(new RsLiteral("0i64") { Type = IrType.I64 }, ">", new RsPath("n") { Type = IrType.I64 }) { Type = IrType.Bool }))]));
    Assert.That(signed, Does.Not.Contain("clippy::absurd_extreme_comparisons"));
  }

  [Test]
  public void ParenthesizedStructLiteral_ReadsBackInAHeader() {
    var file = RustSyntaxReader.Read("fn f() { if a == (P { x: 0 }).x {} while (Q { y: 1 }).y > 0 {} }");
    Assert.That(file.Items, Has.Count.EqualTo(1));
    Assert.That(() => RustSyntaxReader.Read("fn f() { if a == P { x: 0 }.x {} }"), Throws.Exception);
  }

  private const string Buffer = "public struct Buf { public int[] D; public Buf(int[] d) { D = d; } } static int Use(Buf b) => b.D[0]; static int Sum(int[] a) => a[0]; ";

  [Test]
  public void ArrayOfAStructField_PassedOn_IsBorrowedFromTheStruct()
    => Assert.That(CSharp(Buffer + "public static int F() { var b = new Buf(new int[1]); return Sum(b.D); }"), Does.Contain("sum(&b.d)"));

  [Test]
  public void StructHoldingAnArray_MovedAtItsLastRead() {
    var text = CSharp(Buffer + "public static int F() { var b = new Buf(new int[1]); var c = b; return Use(c); }");
    Assert.That(text, Does.Contain("let c: Buf = b;"));
    Assert.That(text, Does.Contain("buf_use(c)").Or.Contain("use_(c)").Or.Contain("r#use(c)"));
  }

  [TestCase("public static int[] F(Buf b) => b.D;", "'the array of a struct field held by a second owner'", TestName = "ArrayOfAStructFieldGivenAway_IsRefused")]
  [TestCase("public static int F() { var b = new Buf(new int[1]); var c = b; return Use(c) + Use(b); }", "'a struct holding an array copied while its variable lives on'", TestName = "StructHoldingAnArrayCopiedWhileItLives_IsRefused")]
  [TestCase("public static int F() { var b = new Buf(new int[1]); while (Use(b) > 0) { } return 0; }", "'a struct holding an array copied while its variable lives on'", TestName = "StructHoldingAnArrayReadInALoopHeader_IsRefused")]
  [TestCase("public static int F() { System.Func<int> g = () => { var b = new Buf(new int[1]); var c = b; return Use(c); }; return g(); }", "'a struct holding an array copied while its variable lives on'", TestName = "StructHoldingAnArrayInALambda_IsRefused")]
  [TestCase("public static int F() { var a = new int[1]; var b = new Buf(a); return Use(b) + a[0]; }", "'an array a struct field shares with a variable'", TestName = "ArrayAStructFieldSharesWithAVariable_IsRefused")]
  public void StructHoldingAnArray_SharedOtherwise_IsRefused(string methods, string construct)
    => Assert.That(Problems(Buffer + methods), Has.Some.Contain(construct));

}
