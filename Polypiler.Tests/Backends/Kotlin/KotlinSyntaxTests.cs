using System;
using NUnit.Framework;
using Polypiler.Core.Backends.Kotlin.Passes;
using Polypiler.Core.Backends.Kotlin.Printing;
using Polypiler.Core.Backends.Kotlin.Syntax;

namespace Polypiler.Tests.Backends.Kotlin;

/// <summary>
/// The Kotlin printer and its reader: precedence and parentheses, every node the tree has, the layout
/// of declarations, and the text the reader reads back into a tree that prints the same.
/// </summary>
[TestFixture]
public sealed class KotlinSyntaxTests {

  private static KtName N(string name) => new(name);

  private static KtLiteral L(string text) => new(text);

  private static KtBinary B(KtExpression left, string op, KtExpression right) => new(left, op, right);

  private static KtNamedType T(string name) => new(name);

  [Test]
  public void Precedence_ParenthesizesWhereKotlinNeedsIt() {
    Assert.Multiple(() => {
      Assert.That(KotlinPrinter.Print(B(B(N("a"), "+", N("b")), "*", N("c"))), Is.EqualTo("(a + b) * c"));
      Assert.That(KotlinPrinter.Print(B(N("a"), "-", B(N("b"), "-", N("c")))), Is.EqualTo("a - (b - c)"));
      Assert.That(KotlinPrinter.Print(B(B(N("a"), "+", N("b")), "shl", L("2"))), Is.EqualTo("(a + b) shl 2"));
      Assert.That(KotlinPrinter.Print(B(B(N("a"), "and", N("b")), "or", N("c"))), Is.EqualTo("(a and b) or c"));
      Assert.That(KotlinPrinter.Print(B(B(N("a"), "or", N("b")), "or", N("c"))), Is.EqualTo("a or b or c"));
      Assert.That(KotlinPrinter.Print(B(N("a"), "xor", B(N("b"), "xor", N("c")))), Is.EqualTo("a xor (b xor c)"));
      Assert.That(KotlinPrinter.Print(B(B(N("a"), "<", N("b")), "==", N("c"))), Is.EqualTo("a < b == c"));
      Assert.That(KotlinPrinter.Print(B(B(N("a"), "&&", N("b")), "||", N("c"))), Is.EqualTo("a && b || c"));
      Assert.That(KotlinPrinter.Print(B(N("a"), "&&", B(N("b"), "||", N("c")))), Is.EqualTo("a && (b || c)"));
      Assert.That(KotlinPrinter.Print(new KtPrefix("-", new KtPrefix("-", N("a")))), Is.EqualTo("-(-a)"));
      Assert.That(KotlinPrinter.Print(new KtCall(new KtMember(new KtPrefix("-", L("5")), "toByte"), [])), Is.EqualTo("(-5).toByte()"));
      Assert.That(KotlinPrinter.Print(B(new KtIfExpression(N("c"), L("1"), L("2")), "+", L("3"))), Is.EqualTo("(if (c) 1 else 2) + 3"));
      Assert.That(KotlinPrinter.Print(new KtIfExpression(N("c"), new KtIfExpression(N("d"), L("1"), L("2")), new KtIfExpression(N("e"), L("3"), L("4")))), Is.EqualTo("if (c) (if (d) 1 else 2) else if (e) 3 else 4"));
      Assert.That(KotlinPrinter.Print(B(N("a"), "..", N("b"))), Is.EqualTo("a..b"));
      Assert.That(KotlinPrinter.Print(B(N("a"), "..<", N("b"))), Is.EqualTo("a..<b"));
      Assert.That(KotlinPrinter.Print(B(N("a"), "?:", new KtThrowExpression(new KtCall(N("E"), [])))), Is.EqualTo("a ?: (throw E())"));
      Assert.That(KotlinPrinter.Print(B(new KtAs(N("a"), T("Int"), false), "*", L("2"))), Is.EqualTo("a as Int * 2"));
      Assert.That(KotlinPrinter.Print(new KtAs(B(N("a"), "+", N("b")), T("Long"), true)), Is.EqualTo("(a + b) as? Long"));
      Assert.That(KotlinPrinter.Print(new KtIs(N("a"), T("String") with { Nullable = true }, true)), Is.EqualTo("a !is String?"));
      Assert.That(KotlinPrinter.Print(B(new KtIs(N("a"), T("Int"), false), "&&", N("b"))), Is.EqualTo("a is Int && b"));
      Assert.That(KotlinPrinter.Print(B(N("a"), "in", N("r"))), Is.EqualTo("a in r"));
      Assert.That(KotlinPrinter.Print(new KtPostfix(N("a"), "!!")), Is.EqualTo("a!!"));
      Assert.That(KotlinPrinter.Print(new KtMember(N("a"), "b") { Safe = true }), Is.EqualTo("a?.b"));
      Assert.That(KotlinPrinter.Print(new KtIndex(N("a"), B(N("i"), "+", L("1")))), Is.EqualTo("a[i + 1]"));
      Assert.That(KotlinPrinter.Print(new KtReference("f")), Is.EqualTo("::f"));
      Assert.That(KotlinPrinter.Print(new KtThis()), Is.EqualTo("this"));
      Assert.That(KotlinPrinter.Print(new KtCall(N("listOf"), [L("1")]) { TypeArguments = [T("Int")] }), Is.EqualTo("listOf<Int>(1)"));
      Assert.That(KotlinPrinter.Print(new KtLambda([new KtParameter("x", T("Int"))], [new KtExpressionStatement(N("x"))])), Is.EqualTo("{ x: Int -> x }"));
      Assert.That(KotlinPrinter.Print(B(new KtLambda([], [new KtExpressionStatement(N("x"))]), "==", N("y"))), Is.EqualTo("({ x }) == y"));
    });
  }

  [Test]
  public void Types_PrintWithArgumentsAndNullability() {
    Assert.Multiple(() => {
      Assert.That(KotlinPrinter.Print(new KtNamedType("Array", [T("Int")]) { Nullable = true }), Is.EqualTo("Array<Int>?"));
      Assert.That(KotlinPrinter.Print(new KtFunctionType([T("Int"), T("Long")], T("Unit"))), Is.EqualTo("(Int, Long) -> Unit"));
      Assert.That(KotlinPrinter.Print(new KtFunctionType([], T("Int")) { Nullable = true }), Is.EqualTo("(() -> Int)?"));
    });
  }

  [Test]
  public void Strings_EscapeWhatKotlinNeedsAndEverythingOutsideAscii() {
    Assert.That(KotlinPrinter.Quoted("a\"b\\c$d\n\r\t\bé\u0001\ud800"), Is.EqualTo("\"a\\\"b\\\\c\\$d\\n\\r\\t\\b\\u00e9\\u0001\\ud800\""));
    Assert.That(KotlinSyntaxReader.ReadExpression(KotlinPrinter.Quoted("a\"b\\c$d\n\r\t\bé\u0001")), Is.EqualTo(new KtString("a\"b\\c$d\n\r\t\bé\u0001")));
  }

  /// <summary>A file with every declaration and statement the tree has, which prints and reads back to the same text.</summary>
  private static KtFile Everything() {
    var block = new KtBlock([
      new KtVariable(false, "a", T("Int"), L("1")),
      new KtVariable(true, "b", null, null),
      new KtAssignment(N("b"), "+=", N("a")),
      new KtExpressionStatement(new KtPostfix(N("b"), "++")),
      new KtIf(B(N("a"), ">", L("0")), new KtBlock([new KtBreak(null)]), new KtIf(N("c"), new KtBlock([]), new KtBlock([new KtContinue("loop")]))),
      new KtWhile("loop", L("true"), new KtBlock([new KtBreak("loop")])),
      new KtDoWhile(null, new KtBlock([new KtExpressionStatement(new KtCall(N("f"), []))]), L("false")),
      new KtFor("outer", "i", B(L("0"), "until", N("n")), new KtBlock([])),
      new KtWhen(N("a"), [new KtWhenEntry([L("1"), L("2")], new KtBlock([new KtReturn(null)])), new KtWhenEntry([], new KtBlock([]))]),
      new KtWhen(null, [new KtWhenEntry([N("c")], new KtBlock([new KtThrow(new KtCall(N("E"), [new KtString("x")]))]))]),
      new KtTry(new KtBlock([new KtExpressionStatement(new KtCall(N("g"), [L("1"), L("2")]))]), [new KtCatch("e", T("Exception"), new KtBlock([]))], new KtBlock([new KtReturn(L("3"))])),
      new KtLocalFunction(new KtFunction("inner", [], T("Int"), null) { Expression = L("4") }),
      new KtExpressionStatement(new KtCall(new KtMember(N("x"), "also"), []) { Trailing = new KtLambda([], [new KtAssignment(N("y"), "=", N("it")), new KtExpressionStatement(N("y"))]) }),
      new KtExpressionStatement(new KtCall(N("Array"), [L("3")]) { Trailing = new KtLambda([new KtParameter("i", null)], [new KtExpressionStatement(N("i"))]) }),
      new KtReturn(new KtCall(N("table"), [L("1"), L("2"), L("3"), L("4"), L("5"), L("6"), L("7"), L("8"), L("9"), L("10")]))
    ]);
    return new KtFile(["header", ""], "demo.pkg", ["kotlin.math.abs"], [
      new KtFunction("f", [new KtParameter("n", T("Int")) { Default = L("2") }], T("Int"), block) { Modifiers = ["private"], Documentation = ["Does things.", ""] },
      new KtFunction("g", [], null, null),
      new KtProperty(false, "LIMIT", T("Int"), L("5")) { Modifiers = ["private", "const"] },
      new KtProperty(true, "state", null, null),
      new KtClass("class", "Point", [new KtParameter("x", T("Int")) { Property = "val" }, new KtParameter("y", T("Int")) { Property = "var" }], [], []) { Modifiers = ["data"] },
      new KtClass("class", "Empty", null, [new KtSuperType(T("Base"), [L("1")]), new KtSuperType(T("Face"), null)], []),
      new KtClass("interface", "Face", null, [], [new KtFunction("m", [], T("Int"), null) { Modifiers = ["abstract"] }]),
      new KtClass("enum class", "Color", [new KtParameter("code", T("Int")) { Property = "val" }], [], [new KtFunction("twice", [], T("Int"), null) { Expression = B(N("code"), "*", L("2")) }]) {
        Entries = [new KtEnumEntry("RED", [L("1")]), new KtEnumEntry("BLUE", [L("2")])]
      },
      new KtClass("enum class", "Plain", null, [], []) { Entries = [new KtEnumEntry("A", []), new KtEnumEntry("B", [])] },
      new KtClass("object", "Holder", null, [], [
        new KtConstructor([new KtParameter("v", T("Int"))], "this", [N("v")], new KtBlock([])),
        new KtConstructor([], null, [], new KtBlock([new KtExpressionStatement(new KtCall(N("f"), []))])),
        new KtInitializer(new KtBlock([new KtAssignment(new KtIndex(N("a"), L("0")), "=", L("1"))]))
      ])
    ]) { Suppressions = ["NAME_SHADOWING", "UNUSED"] };
  }

  [Test]
  public void Everything_PrintsAndReadsBack() {
    var text = KotlinPrinter.Print(Everything());
    Assert.Multiple(() => {
      Assert.That(text, Does.StartWith("// header\n//\n\n@file:Suppress(\"NAME_SHADOWING\", \"UNUSED\")\n\npackage demo.pkg\n\nimport kotlin.math.abs\n\n/**\n * Does things.\n *\n */\nprivate fun f(n: Int = 2): Int {\n"));
      Assert.That(text, Does.Contain("    return table(\n        1, 2, 3, 4, 5, 6, 7, 8,\n        9, 10\n    )\n"));
      Assert.That(text, Does.Contain("data class Point(val x: Int, var y: Int)\n"));
      Assert.That(text, Does.Contain("enum class Color(val code: Int) {\n    RED(1),\n    BLUE(2);\n\n    fun twice(): Int = code * 2\n}\n"));
      Assert.That(text, Does.Contain("enum class Plain {\n    A,\n    B\n}\n"));
      Assert.That(text, Does.Contain("class Empty : Base(1), Face {}\n"));
      Assert.That(text, Does.Contain("    if (a > 0) {\n        break\n    } else if (c) {} else {\n        continue@loop\n    }\n"));
      Assert.That(text, Does.Contain("    loop@ while (true) {\n        break@loop\n    }\n"));
      Assert.That(text, Does.Contain("    try {\n        g(1, 2)\n    } catch (e: Exception) {} finally {\n        return 3\n    }\n"));
      Assert.That(KotlinPrinter.Print(KotlinSyntaxReader.Read(text)), Is.EqualTo(text));
    });
  }

  [Test]
  public void Printer_RefusesWhatKotlinCannotSay() {
    Assert.Multiple(() => {
      Assert.Throws<ArgumentException>(() => KotlinPrinter.Print(new KtBlock([]) as KtStatement));
      Assert.Throws<ArgumentException>(() => KotlinPrinter.Print(new KtIf(N("c"), new KtBlock([]), new KtReturn(null))));
      Assert.Throws<ArgumentException>(() => KotlinPrinter.Precedence("<=>"));
      Assert.Throws<ArgumentNullException>(() => KotlinPrinter.Print((KtFile)null!));
      Assert.Throws<ArgumentNullException>(() => KotlinPrinter.Quoted(null!));
    });
  }

  [TestCase("fun f() {\n    val s = \"abc\n}\n", "unterminated string")]
  [TestCase("/** open", "unterminated comment")]
  [TestCase("fun f() = 'a\n", "unterminated character")]
  [TestCase("fun f() = a # b\n", "unexpected character")]
  [TestCase("fun f(\n", "expected")]
  [TestCase("val = 3\n", "a name expected")]
  [TestCase("stuff\n", "a declaration expected")]
  public void Reader_NamesTheLineOfWhatItCannotRead(string text, string message)
    => Assert.That(Assert.Throws<FormatException>(() => KotlinSyntaxReader.Read(text))!.Message, Does.Contain(message).And.StartWith("line "));

  [Test]
  public void Reader_ReadsLiteralsTypesAndLambdas() {
    Assert.Multiple(() => {
      Assert.That(KotlinSyntaxReader.ReadExpression("1.5e3f"), Is.EqualTo(new KtLiteral("1.5e3f")));
      Assert.That(KotlinSyntaxReader.ReadExpression("'a'"), Is.EqualTo(new KtLiteral("'a'")));
      Assert.That(KotlinSyntaxReader.ReadExpression("null"), Is.EqualTo(new KtLiteral("null")));
      Assert.That(KotlinPrinter.Print(KotlinSyntaxReader.ReadExpression("a as? List<Int>")), Is.EqualTo("a as? List<Int>"));
      Assert.That(KotlinPrinter.Print(KotlinSyntaxReader.ReadExpression("a !in b && c !is D")), Is.EqualTo("a !in b && c !is D"));
      Assert.That(KotlinPrinter.Print(KotlinSyntaxReader.ReadExpression("f<Int>(1) { x -> x }")), Is.EqualTo("f<Int>(1) { x -> x }"));
      Assert.That(KotlinPrinter.Print(KotlinSyntaxReader.ReadExpression("a < b")), Is.EqualTo("a < b"));
      Assert.That(KotlinPrinter.Print(KotlinSyntaxReader.ReadExpression("a?.b!!.c")), Is.EqualTo("a?.b!!.c"));
      Assert.That(KotlinPrinter.Print(KotlinSyntaxReader.ReadExpression("{ f() }")), Is.EqualTo("{ f() }"));
      Assert.That(KotlinPrinter.Print(KotlinSyntaxReader.Read("val f: (Int) -> Long = g\nval h: (Int)? = null\n")), Is.EqualTo("val f: (Int) -> Long = g\n\nval h: Int? = null\n"));
      Assert.Throws<FormatException>(() => KotlinSyntaxReader.ReadExpression("a b"));
      Assert.Throws<FormatException>(() => KotlinSyntaxReader.ReadExpression(")"));
    });
  }

  [Test]
  public void Walker_VisitsEveryNode() {
    var file = Everything();
    var count = 0;
    foreach (var _ in KotlinSyntaxWalker.DescendantsAndSelf(file))
      ++count;

    Assert.That(count, Is.GreaterThan(100));
    Assert.That(KotlinTargetPasses.Names(file), Has.Member("table").And.Member("f"));
    Assert.That(KotlinSyntaxWalker.Children(new KtThis()), Is.Empty);
  }

}
