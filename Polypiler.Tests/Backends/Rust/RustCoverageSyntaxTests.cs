using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Passes;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The syntax tree of the Rust backend around the emitter: the reader on the forms the round trip of
/// the emitted crates does not meet, the rewriter on every kind of node, and the literal put in for
/// a parameter that is cast by the helper inlining - kept bare only where it is a value of the type.
/// </summary>
[TestFixture]
public sealed class RustCoverageSyntaxTests {

  private static RsPath P(string name) => new(name);

  // The reader.

  [TestCase("fn f() -> u32 {\n    g()?\n}\n", TestName = "Reader_ReadsATry")]
  [TestCase("fn f() {\n    if let Some(x) = a {\n        x;\n    }\n}\n", TestName = "Reader_ReadsALetCondition")]
  [TestCase("fn f() {\n    let a = &mut b;\n}\n", TestName = "Reader_ReadsAMutableBorrow")]
  [TestCase("fn f() {\n    *a = b;\n}\n", TestName = "Reader_ReadsAnAssignmentToADereference")]
  [TestCase("fn f() -> u32 {\n    -a as u32\n}\n", TestName = "Reader_ReadsACastOfANegation")]
  [TestCase("fn f() {\n    let v = vec![1, 2];\n}\n", TestName = "Reader_ReadsABracketedMacro")]
  [TestCase("fn f() -> char {\n    '\\n'\n}\n", TestName = "Reader_ReadsAnEscapedCharacter")]
  [TestCase("impl S {\n    fn f(mut self) {}\n}\n", TestName = "Reader_ReadsAMutableReceiver")]
  public void Reader_ReadsWhatThePrinterWritesBack(string text) => Assert.That(RustPrinter.Print(RustSyntaxReader.Read(text)), Is.EqualTo(text));

  // The rewriter.

  [Test]
  public void Rewriter_ReachesThePathsInsideEveryKindOfNode() {
    var function = new RsFunction("f", "", [], null, new RsBlock([
      new RsLet("s", false, null, new RsStructLiteral("S", [new RsFieldValue("a", P("x"))])),
      new RsLet("t", false, null, new RsTuple([P("x"), new RsField(P("x"), "y")])),
      new RsExpressionStatement(new RsTry(P("x"))),
      new RsExpressionStatement(new RsIf(new RsLetCondition("Some(v)", P("x")), RsBlock.Empty, null)),
      new RsItemStatement(new RsFunction("g", "", [], "u32", new RsBlock([], P("x"))))
    ], null));
    var file = new RsFile([], [], [function, new RsImpl(null, "S", [new RsConst("C", "", "u32", P("x"))])]);
    var rewritten = RustSyntaxRewriter.Rewrite(file, node => node is RsPath { Name: "x" } ? P("z") : node);
    Assert.That(RustPrinter.Print(rewritten), Is.EqualTo(
      "fn f() {\n    let s = S { a: z };\n    let t = (z, z.y);\n    z?;\n    if let Some(v) = z {}\n    fn g() -> u32 {\n        z\n    }\n}\n\nimpl S {\n    const C: u32 = z;\n}\n"));
    Assert.That(RustSyntaxRewriter.DescendantsAndSelf(rewritten).OfType<RsPath>().Count(path => path.Name == "x"), Is.Zero);
  }

  // The literals the helper inlining puts in for a cast parameter.

  private static string Inlined(string type, string literal) {
    var helper = new RsFunction("h", "", [new RsParameter("p0", "i64")], type, new RsBlock([], new RsCast(P("p0"), type))) { IsHelper = true };
    var caller = new RsFunction("f", "pub", [], type, new RsBlock([], new RsCall(P("h"), [new RsLiteral(literal)])));
    var crate = new RustCrate("p", "", new RsFile([], [], []), "m", new RsFile([], [], [helper, caller]));
    var inlined = new RustHelperInlining().Run(crate, RustProfile.Default, _ => { });
    return RustPrinter.Print(inlined.Module.Items.OfType<RsFunction>().Single(function => function.Name == "f").Body.Tail!);
  }

  [TestCase("i8", "127", TestName = "Inlining_TheLargestI8_IsTheBareLiteral")]
  [TestCase("u8", "255", TestName = "Inlining_TheLargestU8_IsTheBareLiteral")]
  [TestCase("i16", "32767", TestName = "Inlining_TheLargestI16_IsTheBareLiteral")]
  [TestCase("u16", "65535", TestName = "Inlining_TheLargestU16_IsTheBareLiteral")]
  [TestCase("i32", "2147483647", TestName = "Inlining_TheLargestI32_IsTheBareLiteral")]
  [TestCase("u32", "4294967295", TestName = "Inlining_TheLargestU32_IsTheBareLiteral")]
  [TestCase("i64", "9223372036854775807", TestName = "Inlining_TheLargestI64_IsTheBareLiteral")]
  [TestCase("u64", "18446744073709551615", TestName = "Inlining_TheLargestU64_IsTheBareLiteral")]
  [TestCase("usize", "0", TestName = "Inlining_TheSmallestUsize_IsTheBareLiteral")]
  public void Inlining_ALiteralThatIsAValueOfTheCastsType_IsTheBareLiteral(string type, string literal) => Assert.That(Inlined(type, literal), Is.EqualTo(literal));

  [TestCase("i8", "128", TestName = "Inlining_AboveTheLargestI8_KeepsTheCast")]
  [TestCase("u8", "256", TestName = "Inlining_AboveTheLargestU8_KeepsTheCast")]
  [TestCase("u64", "18446744073709551616", TestName = "Inlining_AboveTheLargestU64_KeepsTheCast")]
  [TestCase("f32", "1", TestName = "Inlining_ALiteralCastToAFloat_KeepsTheCast")]
  [TestCase("u32", "1.5", TestName = "Inlining_ALiteralThatIsNoInteger_KeepsTheCast")]
  public void Inlining_ALiteralThatIsNoValueOfTheCastsType_KeepsTheCast(string type, string literal) => Assert.That(Inlined(type, literal), Is.EqualTo($"{literal} as {type}"));

  [TestCase("i8", "-128", TestName = "Inlining_ANegativeLiteral_KeepsTheCastOfItsParentheses")]
  [TestCase("u16", "-1", TestName = "Inlining_ANegativeLiteralOfAnUnsignedType_KeepsTheCastOfItsParentheses")]
  public void Inlining_ANegativeLiteral_KeepsTheCastOfItsParentheses(string type, string literal) => Assert.That(Inlined(type, literal), Is.EqualTo($"({literal}) as {type}"));

  // The lints a crate allows.

  private static RsExpression Operand(string text) => char.IsAsciiDigit(text[0]) ? new RsLiteral(text) : P(text);

  /// <summary>The lints a file of one function needs allowed.</summary>
  private static IReadOnlyList<string> Needed(params RsItem[] items) => RustEmitter.Lints.Needed(new RsFile([], [], items));

  private static RsFunction Returning(RsExpression value, string returns = "u32", params RsParameter[] parameters) => new("f", "", parameters, returns, new RsBlock([], value));

  [TestCase("1", "*", "a", "clippy::identity_op", TestName = "Lints_AProductWithALeadingOne_IsAnIdentity")]
  [TestCase("a", "/", "1", "clippy::identity_op", TestName = "Lints_AQuotientByOne_IsAnIdentity")]
  [TestCase("a", "-", "0", "clippy::identity_op", TestName = "Lints_ADifferenceWithZero_IsAnIdentity")]
  [TestCase("0", "|", "a", "clippy::identity_op", TestName = "Lints_AnOrWithALeadingZero_IsAnIdentity")]
  [TestCase("0.0", "+", "a", "clippy::identity_op", TestName = "Lints_ASumWithALeadingFloatZero_IsAnIdentity")]
  [TestCase("a", "<<", "0", "clippy::identity_op", TestName = "Lints_AShiftByZero_IsAnIdentity")]
  [TestCase("0", "&", "a", "clippy::erasing_op", TestName = "Lints_AnAndWithZero_Erases")]
  [TestCase("a", "*", "0.0", "clippy::erasing_op", TestName = "Lints_AProductWithAFloatZero_Erases")]
  [TestCase("a", "^", "a", "clippy::eq_op", TestName = "Lints_AnXorOfOneOperand_IsAnEqualOperation")]
  public void Lints_AnOperationOfAnIdentityOrAZero_IsAllowed(string left, string op, string right, string lint)
    => Assert.That(Needed(Returning(new RsBinary(Operand(left), op, Operand(right)))), Does.Contain(lint));

  [TestCase("1", "-", "a", TestName = "Lints_ADifferenceFromOne_NeedsNoLint")]
  [TestCase("0", "-", "a", TestName = "Lints_ADifferenceFromZero_NeedsNoLint")]
  [TestCase("1", "/", "a", TestName = "Lints_AQuotientOfOne_NeedsNoLint")]
  [TestCase("a", "*", "b", TestName = "Lints_AProductOfTwoVariables_NeedsNoLint")]
  public void Lints_AnOperationOfOrdinaryOperands_NeedsNoLint(string left, string op, string right)
    => Assert.That(Needed(Returning(new RsBinary(Operand(left), op, Operand(right)))), Is.Empty);

  [Test]
  public void Lints_ATypeOfAFunctionValueOfFunctionValues_IsTooComplex() {
    const string Complex = "Rc<dyn Fn() -> Rc<dyn Fn() -> i32>>";
    Assert.That(Needed(Returning(P("a"), Complex)), Is.EqualTo(new[] { "clippy::type_complexity" }));
    Assert.That(Needed(Returning(P("a"), "u32", new RsParameter("g", Complex))), Is.EqualTo(new[] { "clippy::type_complexity" }));
    Assert.That(Needed(Returning(new RsClosure(false, [new RsParameter("g", Complex)], P("a")), "u32")), Is.EqualTo(new[] { "clippy::type_complexity" }));
    Assert.That(Needed(Returning(new RsClosure(false, [new RsParameter("g", "Rc<dyn Fn() -> i32>")], P("a")), "u32")), Is.Empty);
  }

  [TestCase("0", "9", true, TestName = "Lints_AClampBetweenTwoLiterals_IsAMinMax")]
  [TestCase("LOW", "HIGH", true, TestName = "Lints_AClampBetweenTwoConstants_IsAMinMax")]
  [TestCase("0", "b", false, TestName = "Lints_AClampToAVariable_IsNoMinMax")]
  [TestCase("b", "9", false, TestName = "Lints_AClampFromAVariable_IsNoMinMax")]
  public void Lints_AMinimumOfAMaximum_IsAManualClamp(string low, string high, bool minMax) {
    var needed = Needed(Returning(new RsMethodCall(new RsMethodCall(P("a"), "max", [Operand(low)]), "min", [Operand(high)])));
    Assert.That(needed, Does.Contain("clippy::manual_clamp"));
    Assert.That(needed.Contains("clippy::min_max"), Is.EqualTo(minMax));
  }

  private static RsStatement Search() => new RsExpressionStatement(new RsFor(null, "&x", P("a"), new RsBlock([
    new RsExpressionStatement(new RsIf(P("c"), new RsBlock([new RsExpressionStatement(new RsReturn(new RsCall(P("Some"), [P("x")])))], null), null))
  ], null)));

  [Test]
  public void Lints_ASearchLoopFollowedByTheReturnOfNone_IsAManualFind() {
    var returned = new RsFunction("f", "", [], "Option<u32>", new RsBlock([Search(), new RsExpressionStatement(new RsReturn(P("None")))], null));
    Assert.That(Needed(returned), Is.EqualTo(new[] { "clippy::manual_find" }));
    var bound = new RsFunction("f", "", [], "Option<u32>", new RsBlock([Search(), new RsLet("y", false, null, P("None"))], P("y")));
    Assert.That(Needed(bound), Is.Empty);
  }

}
