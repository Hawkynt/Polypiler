using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The Rust syntax tree, its printer, which writes what rustfmt writes, its reader and its rewriter:
/// the layout rules one by one, and the parse-back sweep over every file the backend writes for the
/// prototype fixtures.
/// </summary>
[TestFixture]
public sealed class RustSyntaxTests {

  private static RsPath P(string name) => new(name);

  private static RsLiteral L(string text) => new(text);

  private static string Function(RsBlock body, params RsParameter[] parameters) => RustPrinter.Print(new RsFunction("f", "pub", parameters, "u32", body));

  [Test]
  public void ShortCall_StaysOnOneLine()
    => Assert.That(Function(new RsBlock([], new RsCall(P("g"), [P("a"), P("b")]))), Is.EqualTo("pub fn f() -> u32 {\n    g(a, b)\n}\n"));

  [Test]
  public void CallWithArgumentsWiderThan60Columns_PutsOneOnEachLine() {
    var arguments = Enumerable.Range(0, 7).Select(index => (RsExpression)P("argument" + index)).ToList();
    Assert.That(Function(new RsBlock([], new RsCall(P("g"), arguments))),
      Is.EqualTo("pub fn f() -> u32 {\n    g(\n" + string.Concat(Enumerable.Range(0, 7).Select(index => $"        argument{index},\n")) + "    )\n}\n"));
  }

  [Test]
  public void ChainWiderThan60Columns_PutsOneCallOnEachLine() {
    RsExpression chain = P("value");
    foreach (var name in new[] { "wrapping_add", "wrapping_mul", "wrapping_sub", "rotate_left" })
      chain = new RsMethodCall(chain, name, [P("other")]);
    Assert.That(Function(new RsBlock([], chain)),
      Is.EqualTo("pub fn f() -> u32 {\n    value\n        .wrapping_add(other)\n        .wrapping_mul(other)\n        .wrapping_sub(other)\n        .rotate_left(other)\n}\n"));
  }

  [Test]
  public void ChainOfAShortRoot_KeepsItsFirstCall() {
    RsExpression chain = P("x");
    foreach (var name in new[] { "wrapping_add", "wrapping_mul", "wrapping_sub", "rotate_left" })
      chain = new RsMethodCall(chain, name, [P("other")]);
    Assert.That(Function(new RsBlock([], chain)), Does.StartWith("pub fn f() -> u32 {\n    x.wrapping_add(other)\n        .wrapping_mul(other)\n"));
  }

  [Test]
  public void ChainAfterABrokenRoot_StandsAtTheRootsIndentation() {
    var root = new RsCall(P("i8::try_from"), [.. Enumerable.Range(0, 7).Select(index => (RsExpression)P("argument" + index))]);
    var text = Function(new RsBlock([], new RsMethodCall(root, "expect", [new RsString("IntegerOverflow")])));
    Assert.That(text, Does.EndWith("        argument6,\n    )\n    .expect(\"IntegerOverflow\")\n}\n"));
  }

  [Test]
  public void LongRightHandSide_MovesToTheNextLine() {
    var value = new RsMethodCall(new RsCall(P("pop_count32"), [new RsCast(P("value"), "u32")]), "wrapping_add", [new RsCall(P("pop_count32"), [new RsCast(new RsMethodCall(P("value"), "wrapping_shr", [L("32")]), "u32")])]);
    var text = Function(new RsBlock([new RsLet("pop_count64", false, "i32", value)], P("pop_count64")));
    Assert.That(text, Does.Contain("    let pop_count64: i32 =\n        pop_count32(value as u32).wrapping_add(pop_count32(value.wrapping_shr(32) as u32));\n"));
  }

  [Test]
  public void LongChainOfOneOperator_PutsOneOperandOnEachLine() {
    RsExpression sum = P("first_operand");
    for (var index = 0; index < 8; ++index)
      sum = new RsBinary(sum, "+", P("operand_number_" + index));
    var text = Function(new RsBlock([], sum));
    Assert.That(text, Does.StartWith("pub fn f() -> u32 {\n    first_operand\n        + operand_number_0\n        + operand_number_1\n"));
  }

  [Test]
  public void LongSignature_PutsOneParameterOnEachLine() {
    var parameters = Enumerable.Range(0, 6).Select(index => new RsParameter("parameter_number_" + index, "u32")).ToArray();
    Assert.That(Function(new RsBlock([], P("parameter_number_0")), parameters), Does.StartWith("pub fn f(\n    parameter_number_0: u32,\n"));
  }

  [Test]
  public void ShortIfElse_IsOneLine_AndALongOneIsABlock() {
    var shortChoice = new RsIf(P("c"), new RsBlock([], L("1")), new RsBlockExpression(new RsBlock([], L("2"))));
    Assert.That(Function(new RsBlock([], shortChoice)), Is.EqualTo("pub fn f() -> u32 {\n    if c { 1 } else { 2 }\n}\n"));
    var longChoice = new RsIf(P("condition_that_is_long"), new RsBlock([], P("value_when_it_holds")), new RsBlockExpression(new RsBlock([], P("value_otherwise"))));
    Assert.That(Function(new RsBlock([], longChoice)), Is.EqualTo("pub fn f() -> u32 {\n    if condition_that_is_long {\n        value_when_it_holds\n    } else {\n        value_otherwise\n    }\n}\n"));
  }

  [Test]
  public void LongAttribute_PutsOneArgumentOnEachLine() {
    var file = new RsFile([], ["allow(clippy::approx_constant, clippy::neg_cmp_op_on_partial_ord, clippy::nonminimal_bool)"], []);
    Assert.That(RustPrinter.Print(file), Is.EqualTo("#![allow(\n    clippy::approx_constant,\n    clippy::neg_cmp_op_on_partial_ord,\n    clippy::nonminimal_bool\n)]\n"));
  }

  [Test]
  public void Items_AreSeparatedByABlankLine_ModuleDeclarationsGrouped() {
    var file = new RsFile(["header"], ["forbid(unsafe_code)"], [new RsModuleDeclaration("a", "pub"), new RsModuleDeclaration("b", ""), new RsConst("C", "", "u32", L("1"))]);
    Assert.That(RustPrinter.Print(file), Is.EqualTo("//! header\n#![forbid(unsafe_code)]\n\npub mod a;\nmod b;\n\nconst C: u32 = 1;\n"));
  }

  [Test]
  public void EveryNode_PrintsAndReadsBack() {
    var body = new RsBlock([
      new RsLet("a", true, "u32", new RsCast(new RsUnary("-", P("x")), "u32")),
      new RsLet("b", false, null, null),
      new RsExpressionStatement(new RsAssign(P("a"), "^=", new RsIndex(P("TABLE"), L("3")))),
      new RsExpressionStatement(new RsWhile("l0", new RsBinary(P("a"), "!=", L("0")), new RsBlock([new RsExpressionStatement(new RsBreak("l0", null))], null))),
      new RsExpressionStatement(new RsLoop(null, new RsBlock([new RsExpressionStatement(new RsContinue(null))], null))),
      new RsExpressionStatement(new RsFor(null, "i", new RsRange(L("0"), P("n"), true), RsBlock.Empty)),
      new RsExpressionStatement(new RsLabeledBlock("s1", new RsBlock([new RsExpressionStatement(new RsMatch(P("a"), [
        new RsMatchArm(["1", "2..=4"], L("5")), new RsMatchArm(["_"], new RsBlockExpression(new RsBlock([new RsExpressionStatement(new RsBreak("s1", null))], null)))
      ]))], null))),
      new RsExpressionStatement(new RsMacro("panic", [new RsString("Integer\"Overflow")])),
      new RsExpressionStatement(new RsReturn(new RsParenthesized(new RsBinary(P("a"), "<<", L("2")))))
    ], null);
    var file = new RsFile([], [], [new RsUse("super::*"), new RsInlineModule("tests", [new RsFunction("g", "", [], null, body) { Attributes = ["test"], Docs = ["A test."] }]), new RsConst("TABLE", "pub", "[u32; 2]", new RsArray([L("1"), L("2")]))]);
    var text = RustPrinter.Print(file);
    Assert.That(RustPrinter.Print(RustSyntaxReader.Read(text)), Is.EqualTo(text));
  }

  /// <summary>Every kind of node, each holding a name too long for one line, so that each is broken; the text reads back to itself, and a rename reaches every node.</summary>
  [Test]
  public void EveryNodeTooLongForALine_IsBrokenReadBackAndRewritten() {
    var x = P("x_" + new string('a', 96));
    RsExpression[] expressions = [
      new RsUnary("-", x), new RsUnary("!", new RsParenthesized(new RsBinary(x, "<", x))), new RsCast(x, "u32"), new RsParenthesized(new RsBinary(x, "+", x)),
      new RsIndex(P("TABLE"), new RsCast(x, "usize")), new RsArray([x, L("1")]), new RsRange(x, x, true),
      new RsMacro("assert_eq", [x, x]), new RsCall(P("g"), [L("1"), new RsIf(x, new RsBlock([], L("1")), new RsBlockExpression(new RsBlock([], L("2"))))]),
      new RsMethodCall(new RsMethodCall(P("y"), "min", [x]), "max", [x]), new RsMethodCall(new RsCall(P("g"), [x]), "abs", []),
      new RsBlockExpression(new RsBlock([new RsLet("z", false, null, x)], P("z")))
    ];
    var statements = new List<RsStatement>();
    statements.AddRange(expressions.Select((expression, index) => (RsStatement)new RsLet("v" + index, false, null, expression)));
    statements.Add(new RsExpressionStatement(new RsAssign(P("v0"), "+=", new RsBinary(x, "*", x))));
    statements.Add(new RsExpressionStatement(new RsWhile("l0", new RsBinary(x, "!=", x), new RsBlock([new RsExpressionStatement(new RsBreak("l0", x))], null))));
    statements.Add(new RsExpressionStatement(new RsFor(null, "i", new RsRange(L("0"), x, false), new RsBlock([new RsExpressionStatement(new RsContinue(null))], null))));
    statements.Add(new RsExpressionStatement(new RsLoop("l1", new RsBlock([new RsExpressionStatement(new RsBreak("l1", null))], null))));
    statements.Add(new RsExpressionStatement(new RsLabeledBlock("s2", new RsBlock([new RsExpressionStatement(new RsMatch(x, [new RsMatchArm(["_"], x)]))], null))));
    statements.Add(new RsExpressionStatement(new RsReturn(new RsBinary(x, "-", x))));
    var function = new RsFunction("f", "pub", [new RsParameter("y", "u32")], "u32", new RsBlock(statements, null));
    var file = new RsFile([], [], [new RsConst("C", "", "u32", new RsBinary(x, "|", x)), function]);
    var text = RustPrinter.Print(file);
    Assert.That(RustPrinter.Print(RustSyntaxReader.Read(text)), Is.EqualTo(text));
    var renamed = RustSyntaxRewriter.Rewrite(file, node => node == x ? P("w") : node);
    Assert.That(RustPrinter.Print(renamed), Does.Not.Contain(x.Name));
    Assert.That(RustSyntaxRewriter.DescendantsAndSelf(renamed).OfType<RsPath>().Count(path => path.Name == "w"), Is.EqualTo(RustSyntaxRewriter.DescendantsAndSelf(file).Count(node => node == x)));
    Assert.That(RustPrinter.Print(new RsUse("a::b")), Is.EqualTo("use a::b;\n"));
    Assert.That(RustPrinter.Print(new RsInlineModule("m", [])), Is.EqualTo("mod m {\n}\n"));
  }

  [TestCase("fn f( {}")]
  [TestCase("union U {}")]
  [TestCase("fn f() { a b }")]
  [TestCase("fn f() { @ }")]
  public void TextOutsideTheSubset_IsRefused(string text) => Assert.Throws<FormatException>(() => RustSyntaxReader.Read(text));

  [Test]
  public void Rewriter_KeepsATreeItDoesNotChange() {
    var tree = new RsBlock([new RsLet("a", false, "u32", new RsBinary(P("x"), "+", L("1")))], P("a"));
    Assert.That(RustSyntaxRewriter.Rewrite(tree, node => node), Is.SameAs(tree));
    var renamed = RustSyntaxRewriter.Rewrite(tree, node => node is RsPath { Name: "x" } ? P("y") : node);
    Assert.That(RustSyntaxRewriter.DescendantsAndSelf(renamed).OfType<RsPath>().Select(path => path.Name), Is.EqualTo(new[] { "y", "a" }));
  }

  private static IEnumerable<TestCaseData> Fixtures() => Directory.EnumerateFiles(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype"), "*.cs", SearchOption.AllDirectories)
    .Order(StringComparer.Ordinal)
    .Select(path => new TestCaseData(Path.GetRelativePath(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype"), path).Replace('\\', '/')));

  /// <summary>The parse-back sweep: every file the backend writes for a prototype fixture reads back into a tree that prints the same text.</summary>
  [TestCaseSource(nameof(Fixtures))]
  public void GeneratedFile_ReadsBackToItsText(string fixture) {
    var source = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", fixture));
    var imported = Polypiler.Core.Frontends.CSharp.CSharpFrontend.Import(Path.GetFileName(fixture), source);
    if (imported.Module is not { } module || RustCrates.Check(module).Count > 0) {
      Assert.Pass("the fixture is not written as Rust yet");
      return;
    }

    foreach (var file in RustCrates.Emit(module).Where(file => file.Path.EndsWith(".rs", StringComparison.Ordinal)))
      Assert.That(RustPrinter.Print(RustSyntaxReader.Read(file.Content)), Is.EqualTo(file.Content), file.Path);
  }

  private static IEnumerable<TestCaseData> RustFixtures()
    => RustFixture.Names.Except(RustFixture.BackendPending).SelectMany(fixture => RustFixture.Modes.Select(mode => new TestCaseData(fixture, mode).SetArgDisplayNames(fixture, RustFixture.NameOf(mode))));

  /// <summary>
  /// The parse-back sweep over the Rust fixtures, Rust to IR to Rust: every one the backend writes
  /// (all but <see cref="RustFixture.BackendPending"/>, as in the round trip) is written in both
  /// overflow modes, and every file reads back into a tree that prints the same text. That the crates
  /// compute what rustc computed is the round trip of the acceptance tests.
  /// </summary>
  [TestCaseSource(nameof(RustFixtures))]
  [Category("Slow")]
  public void GeneratedFileOfARustFixture_ReadsBackToItsText(string fixture, Polypiler.Core.Frontends.Rust.OverflowChecks mode) {
    var module = RustFixture.Import(fixture, mode);
    Assert.That(RustCrates.Check(module), Is.Empty);
    foreach (var file in RustCrates.Emit(module).Where(file => file.Path.EndsWith(".rs", StringComparison.Ordinal)))
      Assert.That(RustPrinter.Print(RustSyntaxReader.Read(file.Content)), Is.EqualTo(file.Content), file.Path);
  }

  [Test]
  public void Precedence_RanksEveryKind() {
    Assert.That(RustEmitter.Precedence(L("-1")), Is.EqualTo(13));
    Assert.That(RustEmitter.Precedence(new RsCast(P("a"), "u8")), Is.EqualTo(12));
    Assert.That(RustEmitter.Precedence(new RsRange(L("0"), L("1"), false)), Is.EqualTo(2));
    Assert.That(RustEmitter.Precedence(new RsAssign(P("a"), "=", L("1"))), Is.EqualTo(1));
    Assert.That(RustEmitter.Precedence(new RsIf(P("c"), RsBlock.Empty, null)), Is.Zero);
    Assert.That(RustPrinter.Precedence("|"), Is.EqualTo(6));
  }

  [Test]
  public void Literal_OfEveryScalar() {
    Assert.That(RustPrinter.Print(RustEmitter.Literal(new IrConst(IrType.Bool, 1))), Is.EqualTo("true"));
    Assert.That(RustPrinter.Print(RustEmitter.Literal(new IrConst(IrType.U64, -1))), Is.EqualTo("18446744073709551615"));
    Assert.That(RustPrinter.Print(RustEmitter.Literal(new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, 0.1)))), Is.EqualTo("0.1"));
    Assert.That(RustPrinter.Print(RustEmitter.Literal(new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1.7976931348623157e308)))), Is.EqualTo("1.7976931348623157e308"));
  }

}
