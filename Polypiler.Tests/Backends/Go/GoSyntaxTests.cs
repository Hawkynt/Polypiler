using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Go.Printing;
using Polypiler.Core.Backends.Go.Syntax;

namespace Polypiler.Tests.Backends.Go;

/// <summary>
/// The Go syntax tree, its printer and its reader: the spaces around binary operators as gofmt sets
/// them by precedence and depth, parentheses exactly where precedence needs them, literals, the
/// statements and declarations in gofmt's layout, and the round trip text → tree → text. Every text
/// here is gofmt's own output, which <see cref="GoCanonicalTexts"/> lists for the live check that runs
/// gofmt on them.
/// </summary>
[TestFixture]
public sealed class GoSyntaxTests {

  private static GoIdent N(string name) => new(name);

  private static GoBinary B(GoExpr left, string op, GoExpr right) => new(left, op, right);

  [TestCaseSource(typeof(GoCanonicalTexts), nameof(GoCanonicalTexts.Expressions))]
  public void Expression_ReadsAndPrintsBackUnchanged(string text)
    => Assert.That(GoPrinter.Print(GoSyntaxReader.ReadExpression(text)), Is.EqualTo(text));

  [TestCaseSource(typeof(GoCanonicalTexts), nameof(GoCanonicalTexts.Files))]
  public void File_ReadsAndPrintsBackUnchanged(string text) {
    var tree = GoSyntaxReader.ReadFile(text);
    Assert.That(GoPrinter.Print(tree), Is.EqualTo(text));
    Assert.That(GoSyntaxRewriter.Shape(GoSyntaxReader.ReadFile(GoPrinter.Print(tree))), Is.EqualTo(GoSyntaxRewriter.Shape(tree)));
  }

  [Test]
  public void Precedence_InsertsTheParenthesesATreeNeeds() {
    Assert.That(GoPrinter.Print(B(B(N("a"), "+", N("b")), "*", N("c"))), Is.EqualTo("(a + b) * c"));
    Assert.That(GoPrinter.Print(B(N("a"), "-", B(N("b"), "-", N("c")))), Is.EqualTo("a - (b - c)"));
    Assert.That(GoPrinter.Print(new GoUnary("-", B(N("a"), "+", N("b")))), Is.EqualTo("-(a + b)"));
    Assert.That(GoPrinter.Print(new GoSelector(new GoStar(N("p")), "x")), Is.EqualTo("(*p).x"));
    Assert.That(GoPrinter.Print(new GoUnary("-", new GoUnary("-", N("a")))), Is.EqualTo("- -a"));
    Assert.That(GoPrinter.Print(new GoUnary("&", new GoUnary("^", N("a")))), Is.EqualTo("& ^a"));
    Assert.That(GoPrinter.Print(new GoCall(new GoFuncType([], []), [N("f")])), Is.EqualTo("(func())(f)"));
  }

  [TestCase("*", 5), TestCase("&^", 5), TestCase("<<", 5), TestCase("|", 4), TestCase("^", 4), TestCase(">=", 3), TestCase("&&", 2), TestCase("||", 1)]
  public void Precedence_IsGos(string op, int expected) => Assert.That(GoPrinter.Precedence(op), Is.EqualTo(expected));

  [Test]
  public void Precedence_OfAnUnknownOperator_Throws() => Assert.That(() => GoPrinter.Precedence("**"), Throws.ArgumentException);

  [Test]
  public void Literals_AreSpelledAsGoReadsThem() {
    Assert.That(GoPrinter.Spell(new GoInt(255)), Is.EqualTo("255"));
    Assert.That(GoPrinter.Spell(new GoInt(0x811C9DC5, true)), Is.EqualTo("0x811C9DC5"));
    Assert.That(GoPrinter.Spell(new GoInt(0, true)), Is.EqualTo("0x0"));
    Assert.That(GoPrinter.Quote("a\"b\\c\n\t\a\b\f\r\v\u0001\u007Fé"), Is.EqualTo("\"a\\\"b\\\\c\\n\\t\\a\\b\\f\\r\\v\\x01\\x7fé\""));
    Assert.That(((GoString)GoSyntaxReader.ReadExpression("\"a\\\"b\\\\c\\n\\t\\a\\b\\f\\r\\v\\x01\\u00e9\\U0001F600\"")).Value, Is.EqualTo("a\"b\\c\n\t\a\b\f\r\v\u0001é😀"));
    Assert.That(((GoString)GoSyntaxReader.ReadExpression("`raw\\n`")).Value, Is.EqualTo("raw\\n"));
    Assert.That(GoSyntaxReader.ReadExpression("0b101"), Is.EqualTo(new GoInt(5)));
    Assert.That(GoSyntaxReader.ReadExpression("0o17"), Is.EqualTo(new GoInt(15)));
    Assert.That(GoSyntaxReader.ReadExpression("1_000"), Is.EqualTo(new GoInt(1000)));
    Assert.That(GoSyntaxReader.ReadExpression("1e+20"), Is.EqualTo(new GoFloat("1e+20")));
  }

  [Test]
  public void StructFields_AreAlignedAsGofmtAlignsThem() {
    var structure = new GoStructType([new GoField(["X"], N("int32")), new GoField(["Long", "Other"], N("float64")), new GoField([], N("Embedded"))]);
    Assert.That(GoPrinter.Print(structure), Is.EqualTo("struct {\n\tX           int32\n\tLong, Other float64\n\tEmbedded\n}"));
    Assert.That(GoPrinter.Print(new GoStructType([])), Is.EqualTo("struct{}"));
  }

  [Test]
  public void Statements_PrintAlone() {
    Assert.That(GoPrinter.Print(new GoLabeled("outer", new GoBranch("break", "outer")) { Leading = new GoTrivia(1, ["why", ""]) }), Is.EqualTo("// why\n//\nouter:\nbreak outer\n"));
    Assert.That(GoPrinter.Print(new GoDefer(new GoCall(N("f"), []))), Is.EqualTo("defer f()\n"));
    Assert.That(GoPrinter.Print(new GoFuncDecl("f", new GoFuncType([], [new GoField(["a"], N("int"))]), new GoBlock([new GoReturn([])])) { Leading = GoTrivia.Blank }),
      Is.EqualTo("func f() (a int) {\n\treturn\n}\n"));
  }

  [Test]
  public void ThePrinter_RefusesWhatItHasNoFormFor() {
    Assert.That(() => GoPrinter.Print(new Foreign()), Throws.ArgumentException);
    Assert.That(() => GoPrinter.Print(new ForeignStatement()), Throws.ArgumentException);
    Assert.That(() => GoPrinter.Print(new ForeignDeclaration()), Throws.ArgumentException);
    Assert.That(() => GoPrinter.Print(new GoIf(null, N("c"), new GoBlock([]), new GoExprStmt(N("x")))), Throws.ArgumentException);
  }

  [TestCase("/* block */", TestName = "Reader refuses a block comment")]
  [TestCase("\"open", TestName = "Reader refuses an unterminated string")]
  [TestCase("\"line\nbreak\"", TestName = "Reader refuses a string across lines")]
  [TestCase("`open", TestName = "Reader refuses an unterminated raw string")]
  [TestCase("'a", TestName = "Reader refuses an unterminated rune")]
  [TestCase("'a'", TestName = "Reader refuses a rune literal")]
  [TestCase("a @ b", TestName = "Reader refuses an unknown character")]
  [TestCase("\"\\q\"", TestName = "Reader refuses an unknown escape")]
  [TestCase("a b", TestName = "Reader refuses two expressions")]
  [TestCase(")", TestName = "Reader refuses a stray parenthesis")]
  public void Reader_RefusesWhatIsNoExpressionOfTheSubset(string text)
    => Assert.That(() => GoSyntaxReader.ReadExpression(text), Throws.TypeOf<FormatException>());

  [TestCase("package p\n\nfunc f(a, b) {\n}\n", TestName = "Reader reads a list of types")]
  public void Reader_ReadsParameterListsOfTypesAlone(string text)
    => Assert.That(GoSyntaxReader.ReadFile(text).Declarations.OfType<GoFuncDecl>().Single().Signature.Parameters, Has.Count.EqualTo(2));

  [TestCase("package p\n\nimport (\n\tx\n)\n", TestName = "Reader refuses an import that is no path")]
  [TestCase("package p\n\nimport x\n", TestName = "Reader refuses a single import that is no path")]
  [TestCase("package p\n\nx := 1\n", TestName = "Reader refuses a statement at the top")]
  [TestCase("package p\n\nfunc f(a int, b) {\n}\n", TestName = "Reader refuses a list ending in a name")]
  [TestCase("package p\n\nfunc f(a int, []int) {\n}\n", TestName = "Reader refuses a mixed list")]
  [TestCase("package p\n\nfunc f() {\n\tif x := 1 {\n\t}\n}\n", TestName = "Reader refuses an if without a condition")]
  [TestCase("package p\n\nfunc f() {\n\tswitch x := 1 {\n\t}\n}\n", TestName = "Reader refuses a switch tag that is a statement")]
  [TestCase("package p\n\nfunc f() {\n\tx := 1 y\n}\n", TestName = "Reader refuses a statement without its end")]
  [TestCase("package p\n\nfunc f() {\n\tfor x {\n\t\n}\n", TestName = "Reader refuses a block without its brace")]
  public void Reader_RefusesWhatIsNoFileOfTheSubset(string text)
    => Assert.That(() => GoSyntaxReader.ReadFile(text), Throws.TypeOf<FormatException>());

  [Test]
  public void Rewriter_WalksEveryNode_AndTheShapeIgnoresLayout() {
    var file = GoSyntaxReader.ReadFile(GoCanonicalTexts.Files().Last());
    var nodes = GoSyntaxRewriter.DescendantsAndSelf(file).ToList();
    Assert.That(nodes[0], Is.SameAs(file));
    Assert.That(GoSyntaxRewriter.Names(file), Does.Contain("math"));
    var relaid = file with { Declarations = [.. file.Declarations.Select(declaration => declaration with { Leading = GoTrivia.None })] };
    Assert.That(GoSyntaxRewriter.Shape(relaid), Is.EqualTo(GoSyntaxRewriter.Shape(file)));
    Assert.That(GoSyntaxRewriter.Shape(new GoComposite(null, [new GoInt(1)]) { PerLine = 4 }), Is.EqualTo(GoSyntaxRewriter.Shape(new GoComposite(null, [new GoInt(1)]))));
    Assert.That(GoSyntaxRewriter.Children(new Foreign()), Is.Empty);
  }

  [Test]
  public void ReadDeclarations_ReadsAFileWithoutPackageClause() {
    var declarations = GoSyntaxReader.ReadDeclarations("func a() {\n}\n\nvar b = 1\n\nconst c int = 2\n\ntype d struct{}\n");
    Assert.That(declarations.Select(declaration => declaration.GetType().Name), Is.EqualTo(new[] { "GoFuncDecl", "GoVarDecl", "GoConstDecl", "GoTypeDecl" }));
  }

  private sealed record Foreign : GoExpr;

  private sealed record ForeignStatement : GoStmt;

  private sealed record ForeignDeclaration : GoDecl;

}

/// <summary>Texts in gofmt's layout: what the printer writes and the reader reads back, and what gofmt itself leaves unchanged.</summary>
public static class GoCanonicalTexts {

  /// <summary>Expressions, each as gofmt prints it alone.</summary>
  public static IEnumerable<string> Expressions() => [
    "a*b + c", "a + b*c", "(a + b) * c", "a - (b - c)", "x[i+1]", "x[i*2+1]", "a << (n & 31)", "a &^ b", "-a", "^a", "!c",
    "a - -b", "a + +b", "a & ^b", "a / *p", "a && &b == nil", "f(a, b+c)", "f(a + b)", "float64(a*b) + c", "uint32(a + b)",
    "s[1:n]", "s[a+1 : b]", "s[:n]", "s[n:]", "s[:]", "s[a:b:c]", "s[i+1:]", "T{X: 1, Y: 2}", "[]uint32{1, 2, 3}", "[]uint32{}",
    "a == b || c && d", "(a || b) && c", "x.y.z", "math.Sqrt(x)", "*p", "&x", "- -x", "f(xs...)", "[4]int{}", "map[string]int{}",
    "0x811C9DC5", "1.5", "1e-07", "\"text\"", "f()(1)", "a[i][j]", "(*p)[i]", "struct{}{}", "[]func(int32) int32{}",
    "func(a int32) (int32, bool) {\n\treturn a, true\n}", "func() {\n}()", "a.b(c).d"
  ];

  /// <summary>Files, each as gofmt prints it.</summary>
  public static IEnumerable<string> Files() => [
    "package p\n",
    "// Code generated by polypiler. DO NOT EDIT.\n//\n// Header.\n\npackage demo\n\nimport \"math/bits\"\n\nconst k uint32 = 0x1000193\n\nvar table = []uint32{\n\t0x0, 0x77073096, 0xEE0E612C, 0x990951BA,\n\t0x76DC419, 0x706AF48F,\n}\n\ntype Point struct {\n\tX    int32\n\tYyyy float64\n}\n\n// F rotates.\nfunc F(a, b uint32) uint32 {\n\treturn bits.RotateLeft32(a, int(b)) * k\n}\n",
    "package demo\n\nimport (\n\t\"fmt\"\n\t\"math\"\n)\n\nfunc G(a int32, n int32) (int32, error) {\nouter:\n\tfor i := int32(0); i < n; i++ {\n\t\tswitch a {\n\t\tcase 1, 7:\n\t\t\tbreak outer\n\t\tcase 2:\n\t\tdefault:\n\t\t\ta = a*2 + 1\n\t\t\tcontinue\n\t\t}\n\t}\n\tfor a > 0 {\n\t\ta--\n\t}\n\tfor {\n\t\tbreak\n\t}\n\tfor ; a < 3; a++ {\n\t}\n\tfor i := 0; ; {\n\t\t_ = i\n\t\tbreak\n\t}\n\tfor i, v := range []int{1} {\n\t\t_, _ = i, v\n\t}\n\tfor i = range 10 {\n\t}\n\tfor range 3 {\n\t}\n\tif x := a; x > 0 {\n\t\ta += x\n\t} else if a < -1 {\n\t\ta <<= 2\n\t} else {\n\t\t{\n\t\t\tvar y int32 = 3\n\t\t\ta = y\n\t\t}\n\t}\n\tswitch {\n\tcase a > 1:\n\t\tfmt.Println(math.Pi)\n\t}\n\tswitch x := a; x {\n\t}\n\tvar z, w int\n\tz, w = w, z\n\tdefer f(z)\n\treturn a, nil\n}\n"
  ];

}
