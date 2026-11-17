using NUnit.Framework;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The layouts of the Rust printer the emitted crates rarely need, each on a hand-built tree: what
/// rustfmt writes for a trait with supertraits, an empty struct literal, and the forms that break
/// over lines - a tuple, a <c>?</c>, a <c>let</c> condition, a field, a mutable borrow, a range, an
/// index, a closure whose body ends in a block - each checked against rustfmt's own output.
/// </summary>
[TestFixture]
public sealed class RustCoveragePrinterTests {

  private static RsPath P(string name) => new(name);

  private static readonly string A30 = new('a', 30);

  private static readonly string B30 = new('b', 30);

  private static readonly string A35 = new('a', 35);

  private static readonly string B35 = new('b', 35);

  private static readonly string A40 = new('a', 40);

  private static readonly string B10 = new('b', 10);

  private static readonly string B20 = new('b', 20);

  private static readonly string C20 = new('c', 20);

  private static readonly string A50 = new('a', 50);

  private static readonly string B50 = new('b', 50);

  /// <summary>A name as long as given, made of one letter.</summary>
  private static RsPath Long(int length, char letter = 'a') => new(new string(letter, length));

  /// <summary>A call whose arguments are too wide for one line of a call's arguments.</summary>
  private static RsCall Wide() => new(P("compute"), [Long(30), Long(30, 'b')]);

  /// <summary>A function <c>f</c> without parameters of the statements given.</summary>
  private static string Function(params RsStatement[] statements) => RustPrinter.Print(new RsFunction("f", "", [], null, new RsBlock(statements, null)));

  /// <summary>A statement of an expression.</summary>
  private static RsStatement S(RsExpression expression) => new RsExpressionStatement(expression);

  [Test]
  public void Trait_WithSupertraits_ListsThem() {
    Assert.That(RustPrinter.Print(new RsTrait("T", "pub", ["A", "B"], [])), Is.EqualTo("pub trait T: A + B {}\n"));
    Assert.That(RustPrinter.Print(new RsTrait("T", "", ["A"], [new RsFunction("m", "", [new RsParameter("&self", "")], null, new RsBlock([], null) { IsDeclaration = true })])),
      Is.EqualTo("trait T: A {\n    fn m(&self);\n}\n"));
  }

  [Test]
  public void StructLiteral_WithoutFields_IsTheNameAndBraces()
    => Assert.That(Function(new RsLet("s", false, null, new RsStructLiteral("S", []))), Is.EqualTo("fn f() {\n    let s = S {};\n}\n"));

  [Test]
  public void Tuple_OfAWideCall_PutsOneElementOnEachLine()
    => Assert.That(Function(new RsLet("t", false, null, new RsTuple([Wide(), P("c")]))), Is.EqualTo($"fn f() {{\n    let t = (\n        compute(\n            {A30},\n            {B30},\n        ),\n        c,\n    );\n}}\n"));

  [Test]
  public void Try_OfAWideCall_BreaksTheCall()
    => Assert.That(Function(new RsLet("t", false, null, new RsTry(Wide()))), Is.EqualTo($"fn f() {{\n    let t = compute(\n        {A30},\n        {B30},\n    )?;\n}}\n"));

  [Test]
  public void LetCondition_OfAWideCall_BreaksTheCall()
    => Assert.That(Function(S(new RsIf(new RsLetCondition("Some(x)", Wide()), new RsBlock([S(P("x"))], null), null))), Is.EqualTo($"fn f() {{\n    if let Some(x) = compute(\n        {A30},\n        {B30},\n    ) {{\n        x;\n    }}\n}}\n"));

  [Test]
  public void Field_OfAWideCall_FollowsTheBrokenCall()
    => Assert.That(Function(new RsLet("t", false, null, new RsField(Wide(), "x"))), Is.EqualTo($"fn f() {{\n    let t = compute(\n        {A30},\n        {B30},\n    )\n    .x;\n}}\n"));

  [Test]
  public void Field_OfALongPath_StaysWithItsPath()
    => Assert.That(Function(new RsLet("t", false, null, new RsBinary(new RsField(Long(50), "x"), "+", new RsField(Long(50, 'b'), "y")))), Is.EqualTo($"fn f() {{\n    let t = {A50}.x\n        + {B50}.y;\n}}\n"));

  [Test]
  public void MutableBorrow_OfAWideCall_BreaksTheCall()
    => Assert.That(Function(new RsLet("t", false, null, new RsReference(true, Wide()))), Is.EqualTo($"fn f() {{\n    let t = &mut compute(\n        {A30},\n        {B30},\n    );\n}}\n"));

  [Test]
  public void Range_OfWideBounds_BreaksThem()
    => Assert.That(Function(new RsLet("t", false, null, new RsRange(Wide(), null, false)), new RsLet("u", false, null, new RsRange(null, Wide(), true))), Is.EqualTo($"fn f() {{\n    let t = compute(\n        {A30},\n        {B30},\n    )..;\n    let u = ..=compute(\n        {A30},\n        {B30},\n    );\n}}\n"));

  [Test]
  public void Index_OfABrokenChain_FollowsItsLastLink()
    => Assert.That(Function(new RsLet("t", false, null, new RsIndex(new RsMethodCall(new RsMethodCall(Long(40), "first", []), "second", [Long(10, 'b')]), P("i")))), Is.EqualTo($"fn f() {{\n    let t = {A40}\n        .first()\n        .second({B10})[i];\n}}\n"));

  [Test]
  public void ChainEndingInAField_BreaksBeforeEachLink()
    => Assert.That(Function(new RsLet("t", false, null, new RsField(new RsMethodCall(new RsMethodCall(Long(40), "first", [Long(20, 'b')]), "second", [Long(20, 'c')]), "x"))), Is.EqualTo($"fn f() {{\n    let t = {A40}\n        .first({B20})\n        .second({C20})\n        .x;\n}}\n"));

  [TestCase("&", "&match x", "", TestName = "Closure_WhoseBodyIsTheBorrowOfAMatch_KeepsTheBodyBare")]
  [TestCase("!", "!match x", "", TestName = "Closure_WhoseBodyIsTheNegationOfAMatch_KeepsTheBodyBare")]
  [TestCase("as", "match x", " as u32", TestName = "Closure_WhoseBodyIsTheCastOfAMatch_KeepsTheBodyBare")]
  public void Closure_WhoseBodyEndsInAMatch_KeepsTheBodyBare(string form, string head, string tail) {
    var choice = new RsMatch(P("x"), [new RsMatchArm(["0"], Long(30)), new RsMatchArm(["_"], Long(30, 'b'))]);
    RsExpression body = form switch { "&" => new RsReference(false, choice), "!" => new RsUnary("!", choice), _ => new RsCast(choice, "u32") };
    Assert.That(Function(new RsLet("g", false, null, new RsClosure(false, [new RsParameter("x", "u32")], body))),
      Is.EqualTo($"fn f() {{\n    let g = |x: u32| {head} {{\n        0 => {A30},\n        _ => {B30},\n    }}{tail};\n}}\n"));
  }

  [Test]
  public void Argument_ThatIsAnIfWithAWideCondition_DoesNotOverflow()
    => Assert.That(Function(S(new RsCall(P("g"), [P("a"), new RsIf(Wide(), new RsBlock([], P("b")), new RsBlockExpression(new RsBlock([], P("c"))))]))), Is.EqualTo($"fn f() {{\n    g(\n        a,\n        if compute(\n            {A30},\n            {B30},\n        ) {{\n            b\n        }} else {{\n            c\n        }},\n    );\n}}\n"));

  [Test]
  public void NestedCall_UnderAMutableBorrow_IsOpened()
    => Assert.That(Function(S(new RsCall(P("g"), [new RsReference(true, new RsCall(P("h"), [Long(35), Long(35, 'b')]))]))), Is.EqualTo($"fn f() {{\n    g(&mut h(\n        {A35},\n        {B35},\n    ));\n}}\n"));

}
