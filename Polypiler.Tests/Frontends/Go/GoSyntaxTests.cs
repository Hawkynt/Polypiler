using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Frontends.Go.Ast;
using Polypiler.Core.Frontends.Go.Syntax;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Go;

/// <summary>The scanner and parser of the Go frontend where a case file cannot reach: bytes that are no text, the ends of a file, every token.</summary>
[TestFixture]
public sealed class GoSyntaxTests {

  private static List<GoToken> Tokens(string text) {
    var scanner = new GoScanner(new GoSource(text));
    var tokens = new List<GoToken>();
    for (var token = scanner.Scan(); token.Kind != GoTokenKind.Eof; token = scanner.Scan())
      tokens.Add(token);

    return tokens;
  }

  private static string Error(byte[] bytes) => Assert.Throws<GoSyntaxException>(() => GoParser.ParseFile(new GoSource(bytes)))!.Message;

  private static string Error(string text) => Error(Encoding.UTF8.GetBytes(text));

  [Test]
  public void EveryOperator_IsItsToken() {
    const string operators = "+ - * / % & | ^ << >> &^ += -= *= /= %= &= |= ^= <<= >>= &^= && || <- ++ -- == < > = ! != <= >= := ... ( [ { , . ) ] } ; : ~";
    Assert.That(Tokens(operators).Select(token => token.Kind.Spelling()), Is.EqualTo(operators.Split(' ')));
    Assert.That(GoTokenKind.Mul.Precedence(), Is.EqualTo(5));
    Assert.That(GoTokenKind.Assign.Precedence(), Is.EqualTo(0));
    Assert.That(GoTokens.Lookup("fallthrough"), Is.EqualTo(GoTokenKind.Fallthrough));
    Assert.That(GoTokens.Lookup("x"), Is.EqualTo(GoTokenKind.Ident));
    Assert.That(GoTokenKind.Char.IsLiteral() && !GoTokenKind.Add.IsLiteral(), Is.True);
  }

  [Test]
  public void Semicolons_FollowTheTokensThatEndAStatement() {
    var tokens = Tokens("x\nbreak\ny++ /* a\nb */ z // c\nreturn");
    var semicolons = tokens.Where(token => token.Kind == GoTokenKind.Semicolon).Select(token => token.Literal).ToList();
    Assert.That(semicolons, Is.EqualTo(new[] { "\n", "\n", "\n", "\n", "\n" }));
    Assert.That(Tokens("a /* no newline */ b").Count(token => token.Kind == GoTokenKind.Semicolon), Is.EqualTo(1));
    Assert.That(Tokens("f(); g").Select(token => token.Literal), Does.Contain(";"));
  }

  [Test]
  public void Identifiers_AndNumbers_MayBeUnicodeAndOfEveryForm() {
    var tokens = Tokens("αβ x٣ .5 1. 0x_1p-2 0X1P2 0B10 0O7 017 1e+3 2i 'é' `raw`");
    Assert.That(tokens.Where(token => token.Kind != GoTokenKind.Semicolon).Select(token => (token.Kind, token.Literal)), Is.EqualTo(new[] {
      (GoTokenKind.Ident, "αβ"), (GoTokenKind.Ident, "x٣"), (GoTokenKind.Float, ".5"), (GoTokenKind.Float, "1."), (GoTokenKind.Float, "0x_1p-2"),
      (GoTokenKind.Float, "0X1P2"), (GoTokenKind.Int, "0B10"), (GoTokenKind.Int, "0O7"), (GoTokenKind.Int, "017"), (GoTokenKind.Float, "1e+3"),
      (GoTokenKind.Imag, "2i"), (GoTokenKind.Char, "'é'"), (GoTokenKind.String, "`raw`")
    }));
    Assert.That(Tokens("`a\r\nb`")[0].Literal, Is.EqualTo("`a\nb`"));
  }

  [Test]
  public void AByteOrderMark_IsSkippedAtTheStartAlone() {
    Assert.That(GoParser.ParseFile(new GoSource([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("package p\n")])).Node("Name")!.Text("Name"), Is.EqualTo("p"));
    Assert.That(Error("package p\n\uFEFF"), Is.EqualTo("illegal byte order mark"));
  }

  [Test]
  public void BytesThatAreNoText_AreErrors() {
    Assert.That(Error([.. Encoding.UTF8.GetBytes("package p\nvar x = \""), 0xFF, (byte)'"']), Is.EqualTo("illegal UTF-8 encoding"));
    Assert.That(Error("package p\nvar x = \"\0\""), Is.EqualTo("illegal character NUL"));
    Assert.That(Error("package p\nvar x = '\\"), Is.EqualTo("escape sequence not terminated"));
    Assert.That(Error("package p\nvar x = '\\x4"), Is.EqualTo("escape sequence not terminated"));
    Assert.That(Error("package p\nvar x = \"\\x4\n\""), Does.StartWith("illegal character U+000A in escape"));
    Assert.That(Error("package p\nvar x = 'a"), Is.EqualTo("rune literal not terminated"));
    Assert.That(Error("package p\nvar x = \u0001"), Is.EqualTo("illegal character U+0001"));
  }

  [TestCase("1_000", -1)]
  [TestCase("0x_ff", -1)]
  [TestCase("0x_", 2)]
  [TestCase("1__0", 2)]
  [TestCase("_1", 0)]
  [TestCase("0b_1_", 4)]
  [TestCase("1_.5", 1)]
  public void Separators_StandBetweenDigits(string literal, int index) => Assert.That(GoScanner.InvalidSeparator(literal), Is.EqualTo(index));

  [Test]
  public void CodePoints_AreWrittenAsGoWritesThem() {
    Assert.That(GoScanner.CodePoint('A'), Is.EqualTo("U+0041 'A'"));
    Assert.That(GoScanner.CodePoint('\n'), Is.EqualTo("U+000A"));
    Assert.That(GoScanner.CodePoint(' '), Is.EqualTo("U+0020"));
  }

  [Test]
  public void Positions_AreLinesAndByteColumns() {
    var source = new GoSource("a\né b");
    Assert.That((source.Position(0), source.Position(5)), Is.EqualTo(((1, 1), (2, 4))));
  }

  [Test]
  public void TheTree_CarriesTheFieldsOfGoAst() {
    var file = GoParser.ParseFile(new GoSource("package p\n\nfunc (t *T) M[](a, b int, c ...string) (r int) { return }\n".Replace("[]", "")));
    var function = file.Nodes("Decls")[0];
    Assert.That((function.Kind, function.Line, function.Column, function.Is("FuncDecl")), Is.EqualTo(("FuncDecl", 3, 1, true)));
    Assert.That(function.Node("Type")!.Node("Params")!.Nodes("List").Select(field => field.Nodes("Names").Count), Is.EqualTo(new[] { 2, 1 }));
    Assert.That(function.Node("Missing"), Is.Null);
    Assert.That(function.Nodes("Missing"), Is.Empty);
    Assert.That((function.Text("Missing"), function.Flag("Missing")), Is.EqualTo(((string?)null, false)));
    Assert.That((function.Type, function.Underlying, function.Mode, function.Constant, function.Object, function.Declaration, function.Package), Is.EqualTo(("", "", "", (string?)null, (string?)null, "", (string?)null)));
    Assert.That(function.ToString(), Is.EqualTo("FuncDecl(3,1)"));
  }

  [Test]
  public void TheParser_FollowsNoDeeperThanItsLimit() {
    var deep = "package p\nvar x = " + string.Concat(Enumerable.Repeat("- ", GoParser.MaxNesting + 10)) + "1\n";
    Assert.That(() => GoParser.ParseFile(new GoSource(deep)), Throws.TypeOf<GoNestingException>().With.Property(nameof(GoNestingException.Offset)).GreaterThan(0));
  }

  [TestCase(null, null)]
  [TestCase("linux", null)]
  [TestCase("go1.22", "22")]
  [TestCase("go1.20 && linux", "20")]
  [TestCase("linux && go1.23", "23")]
  [TestCase("go1.20 && go1.22", "22")]
  [TestCase("go1.20 || go1.22", "20")]
  [TestCase("go1.20 || linux", null)]
  [TestCase("!go1.22", null)]
  [TestCase("(go1.21 || go1.24) && amd64", "21")]
  [TestCase("(go1.21", "21")]
  [TestCase("go1.x", null)]
  [TestCase("", null)]
  [TestCase("go1.21&", "21")]
  [TestCase("go1.21 |", "21")]
  public void BuildConstraints_ImplyTheMinimumVersion(string? expression, string? minor) => Assert.That(GoBuildConstraint.MinimumMinor(expression), Is.EqualTo(minor));

  [Test]
  public void OnlyAConstraintBeforeThePackageClause_Counts() {
    static string? Version(string text) => GoParser.ParseFile(new GoSource(text)).Text("GoVersion");
    Assert.That(Version("// x\n//go:build go1.22\n\npackage p\n"), Is.EqualTo("22"));
    Assert.That(Version("package p\n//go:build go1.22\n"), Is.Null);
    Assert.That(Version("//go:buildx go1.22\npackage p\n"), Is.Null);
    Assert.That(Version("//go:build\tgo1.23\npackage p\n"), Is.EqualTo("23"));
  }

  [TestCase("x.y", "x.y")]
  [TestCase("a[i]", "a[i]")]
  [TestCase("a[1:2]", "a[1:2]")]
  [TestCase("a[1:2:3]", "a[1:2:3]")]
  [TestCase("x.(int)", "x.(int)")]
  [TestCase("f(a, b...)", "f(a, b...)")]
  [TestCase("*p", "*p")]
  [TestCase("-x + 1", "-x + 1")]
  [TestCase("(x)", "(x)")]
  [TestCase("T{a: 1}", "T{…}")]
  [TestCase("struct{}{}", "struct{…}{…}")]
  [TestCase("func() {}", "func literal")]
  [TestCase("[]int(nil)", "[]int(nil)")]
  [TestCase("[2]int{}", "[2]int{…}")]
  [TestCase("(func())(nil)", "(func(…))(nil)")]
  [TestCase("(interface{})(nil)", "(interface{})(nil)")]
  [TestCase("(interface{ M() })(nil)", "(interface{…})(nil)")]
  [TestCase("map[int]string(nil)", "map[int]string(nil)")]
  [TestCase("(chan int)(nil)", "(chan int)(nil)")]
  [TestCase("(chan<- int)(nil)", "(chan<- int)(nil)")]
  [TestCase("(<-chan int)(nil)", "(<-chan int)(nil)")]
  [TestCase("G[int, string]", "G[int, string]")]
  [TestCase("'a'", "'a'")]
  public void Expressions_AreWrittenForMessages(string expression, string text) {
    var file = GoParser.ParseFile(new GoSource($"package p\n\nvar _ = {expression}\n"));
    Assert.That(Polypiler.Core.Frontends.Go.Checking.GoNodeText.Of(file.Nodes("Decls")[0].Nodes("Specs")[0].Nodes("Values")[0]), Is.EqualTo(text));
  }

  [Test]
  public void TheTextOfNoNode_IsEmpty() {
    Assert.That(Polypiler.Core.Frontends.Go.Checking.GoNodeText.Of(null), Is.Empty);
    Assert.That(Polypiler.Core.Frontends.Go.Checking.GoNodeText.Of(new GoNode("Ellipsis", 0, 1, 1).With("Elt", new GoNode("Ident", 0, 1, 1).With("Name", "T"))), Is.EqualTo("...T"));
    Assert.That(Polypiler.Core.Frontends.Go.Checking.GoNodeText.Of(new GoNode("KeyValueExpr", 0, 1, 1)), Is.EqualTo(": "));
    Assert.That(Polypiler.Core.Frontends.Go.Checking.GoNodeText.Of(new GoNode("EmptyStmt", 0, 1, 1)), Is.EqualTo("EmptyStmt"));
    Assert.That(Polypiler.Core.Frontends.Go.Checking.GoNodeText.Of(new GoNode("TypeAssertExpr", 0, 1, 1)), Is.EqualTo(".(type)"));
  }

  [Test]
  public void ANodeHoldsWhatItIsGiven() {
    var node = new GoNode("Ident", 0, 1, 1).With("List", new List<GoNode> { new("A", 0, 1, 1) }).With("Name", "x").With("Flag", true);
    Assert.That((node.Nodes("List").Count, node.Text("Name"), node.Flag("Flag"), node.Node("Name")), Is.EqualTo((1, "x", true, (GoNode?)null)));
  }

  /// <summary>The file of every production of the grammar, which the Go layer has go/parser accept as well.</summary>
  public static string Grammar { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "go", "syntax", "grammar.go");

  [Test]
  public void EveryProductionOfTheGrammar_Parses() {
    var file = GoParser.ParseFile(new GoSource(File.ReadAllText(Grammar)));
    Assert.That((file.Text("GoVersion"), file.Nodes("Decls").Count), Is.EqualTo(("24", 15)));
    var types = file.Nodes("Decls")[3].Nodes("Specs");
    Assert.That(types.Select(spec => spec.Node("Type")!.Kind).Take(10), Is.EqualTo(new[] { "ArrayType", "ArrayType", "ArrayType", "StarExpr", "FuncType", "FuncType", "ChanType", "ChanType", "MapType", "Ident" }));
    // L, L2 and L3 are arrays, as go/parser reads [P *int], [P *int | *string] and [P (int)] without a constraint that is plainly a type.
    Assert.That(types.Count(spec => spec.Node("TypeParams") is not null), Is.EqualTo(14));
  }

}
