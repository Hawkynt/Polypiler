using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Python.Printing;
using Polypiler.Core.Backends.Python.Syntax;

namespace Polypiler.Tests.Backends.Python;

/// <summary>
/// The Python syntax tree, its printer, its reader and its rewriter: parentheses exactly where
/// Python's precedence needs them (and around mixed bitwise operands), literals that read back as the
/// same value, the compound statements, and the round trip text → tree → text.
/// </summary>
[TestFixture]
public sealed class PythonSyntaxTests {

  private static PyName N(string name) => new(name);

  private static PyBinary B(PyExpression left, string op, PyExpression right) => new(left, op, right);

  private static string P(PyExpression expression) => PythonPrinter.Print(expression);

  [TestCase("a + b * c", "a + b * c")]
  [TestCase("(a + b) * c", "(a + b) * c")]
  [TestCase("a - (b - c)", "a - (b - c)")]
  [TestCase("a - b - c", "a - b - c")]
  [TestCase("(a < b) != (c < d)", "(a < b) != (c < d)")]
  [TestCase("a < b <= c", "a < b <= c")]
  [TestCase("not a == b", "not a == b")]
  [TestCase("(not a) == b", "(not a) == b")]
  [TestCase("-x ** 2", "-x ** 2")]
  [TestCase("(-x) ** 2", "(-x) ** 2")]
  [TestCase("2 ** -1", "2 ** -1")]
  [TestCase("a if b else c if d else e", "a if b else c if d else e")]
  [TestCase("(a if b else c) if d else e", "(a if b else c) if d else e")]
  [TestCase("a and b or c", "a and b or c")]
  [TestCase("a and (b or c)", "a and (b or c)")]
  [TestCase("a or (b or c)", "a or (b or c)")]
  [TestCase("x + y & 0xFF", "(x + y) & 0xFF")]
  [TestCase("x & 0xFF ^ 0x80", "(x & 0xFF) ^ 0x80")]
  [TestCase("x & y & z", "x & y & z")]
  [TestCase("x >> (y & 31)", "x >> (y & 31)")]
  [TestCase("-(-5)", "-(-5)")]
  [TestCase("~(-x)", "~(-x)")]
  [TestCase("-0.0", "-0.0")]
  [TestCase("(5).bit_count()", "(5).bit_count()")]
  [TestCase("(x, (x := x + 1))[0]", "(x, (x := x + 1))[0]")]
  [TestCase("(a,)", "(a,)")]
  [TestCase("()", "()")]
  [TestCase("[1, 2] * n", "[1, 2] * n")]
  [TestCase("x in (1, 2)", "x in (1, 2)")]
  [TestCase("f(a, b)[i].c", "f(a, b)[i].c")]
  [TestCase("a // b % c / d", "a // b % c / d")]
  [TestCase("x << 3 >> 1", "(x << 3) >> 1")]
  [TestCase("a | b ^ c", "a | (b ^ c)")]
  [TestCase("\"a\\\"b\\\\c\\n\\r\\t\\x01\\xe9\\u20ac\"", "\"a\\\"b\\\\c\\n\\r\\t\\x01\\xe9\\u20ac\"")]
  [TestCase("0xFFFF_FFFF_FFFF_FFFF", "0xFFFF_FFFF_FFFF_FFFF")]
  [TestCase("True and False or None", "True and False or None")]
  [TestCase("1e+300 + .5", "1e+300 + 0.5")]
  public void Expression_PrintsWithTheParenthesesItNeeds_AndReadsBack(string source, string printed) {
    var tree = PythonSyntaxReader.ReadExpression(source);
    Assert.That(P(tree), Is.EqualTo(printed));
    Assert.That(PythonSyntaxRewriter.Shape(PythonSyntaxReader.ReadExpression(printed)), Is.EqualTo(PythonSyntaxRewriter.Shape(tree)));
  }

  [Test]
  public void ComparisonOperand_ThatIsAComparison_IsParenthesized() {
    var inner = new PyCompare([N("a"), N("b")], ["<"]);
    Assert.That(P(new PyCompare([inner, N("c")], ["=="])), Is.EqualTo("(a < b) == c"));
  }

  [Test]
  public void NegativeLiterals_BindAsAUnaryMinus() {
    Assert.That(P(B(new PyInt(-5), "**", new PyInt(2))), Is.EqualTo("(-5) ** 2"));
    Assert.That(P(B(N("x"), "-", new PyInt(-5))), Is.EqualTo("x - -5"));
    Assert.That(P(new PyUnary("-", new PyFloat(-1.5))), Is.EqualTo("-(-1.5)"));
    Assert.That(P(new PyAttribute(new PyFloat(1.5), "hex")), Is.EqualTo("(1.5).hex"));
    Assert.That(P(new PyAttribute(new PyInt(-1), "bit_count")), Is.EqualTo("(-1).bit_count"));
  }

  [TestCase(1.0, "1.0")]
  [TestCase(0.1, "0.1")]
  [TestCase(1e16, "10000000000000000.0")]
  [TestCase(1e300, "1e+300")]
  [TestCase(1e-5, "1e-05")]
  [TestCase(-2.5, "-2.5")]
  [TestCase(double.MaxValue, "1.7976931348623157e+308")]
  [TestCase(double.Epsilon, "5e-324")]
  public void Float_IsTheShortestTextThatReadsBack(double value, string text) {
    Assert.That(PythonPrinter.Float(value), Is.EqualTo(text));
    Assert.That(((PyFloat)PythonSyntaxReader.ReadExpression(text)).Value, Is.EqualTo(value));
  }

  [Test]
  public void Float_NegativeZero_KeepsItsSign() {
    Assert.That(PythonPrinter.Float(-0.0), Is.EqualTo("-0.0"));
    Assert.That(double.IsNegative(((PyFloat)PythonSyntaxReader.ReadExpression("-0.0")).Value), Is.True);
  }

  [TestCase(double.NaN)]
  [TestCase(double.PositiveInfinity)]
  [TestCase(double.NegativeInfinity)]
  public void Float_WithoutALiteral_IsRefused(double value) => Assert.That(() => PythonPrinter.Float(value), Throws.ArgumentException);

  [Test]
  public void Integer_KeepsItsSpelling_OnlyWhileItDenotesTheValue() {
    Assert.That(P(new PyInt(255) { Raw = "0xFF" }), Is.EqualTo("0xFF"));
    Assert.That(P(new PyInt(256) { Raw = "0xFF" }), Is.EqualTo("256"));
    Assert.That(P(new PyInt(5) { Raw = "05" }), Is.EqualTo("5"));
  }

  [TestCase("0", 0)]
  [TestCase("00", 0)]
  [TestCase("1_000", 1000)]
  [TestCase("0x_FF", 255)]
  [TestCase("0xFF", 255)]
  [TestCase("0o17", 15)]
  [TestCase("0b101", 5)]
  [TestCase("0x", null)]
  [TestCase("", null)]
  [TestCase("_1", null)]
  [TestCase("1_", null)]
  [TestCase("1__0", null)]
  [TestCase("012", null)]
  [TestCase("0b2", null)]
  [TestCase("12a", null)]
  public void ParseInteger_ReadsPythonsIntegerLiterals(string text, int? value)
    => Assert.That(PythonLiterals.ParseInteger(text), Is.EqualTo(value is null ? null : (System.Numerics.BigInteger?)value.Value));

  [Test]
  public void Module_PrintsAndReadsBackEveryStatement() {
    const string source = """
      # header line
      #
      # third

      import math
      from typing import List

      __all__ = ["f"]


      def f(a: int, b: List[int]) -> int:
          global _g
          x: int
          y: int = a
          if a < 0:
              return -1
          elif a == 0:
              pass
          else:
              raise ValueError("no")
          while True:
              break
          for i in range(a):
              continue
          match a:
              case 1 | -2:
                  pass
              case _ if 3 <= a <= 9:
                  y = 3
              case _:
                  b[0] = 1
          f(a, b)
          return


      _TABLE = (
          1, 2, 3, 4, 5, 6, 7, 8,
          9,
      )
      """;
    var module = PythonSyntaxReader.ReadModule(source);
    Assert.That(module.Header, Is.EqualTo(new[] { "header line", "", "third" }));
    var printed = PythonPrinter.Print(module);
    var again = PythonSyntaxReader.ReadModule(printed);
    Assert.That(PythonSyntaxRewriter.Shape(again), Is.EqualTo(PythonSyntaxRewriter.Shape(module)));
    Assert.That(PythonPrinter.Print(again), Is.EqualTo(printed), "the reader keeps no layout, so a text it read prints the same again");
    Assert.That(printed, Does.Contain("    elif a == 0:\n        pass\n    else:\n").And.Contain("        case 1 | -2:\n            pass\n"));
  }

  [Test]
  public void Table_IsWrittenEightToALine_AndAShortOneOnOneLine() {
    var table = new PyTuple([.. Enumerable.Range(1, 9).Select(value => (PyExpression)new PyInt(value))]) { Tabulated = true };
    Assert.That(PythonPrinter.Print(new PyAssign(N("t"), table)), Is.EqualTo("t = (\n    1, 2, 3, 4, 5, 6, 7, 8,\n    9,\n)\n"));
    Assert.That(PythonPrinter.Print(new PyAssign(N("t"), new PyList([new PyInt(1)]) { Tabulated = true })), Is.EqualTo("t = [1]\n"));
  }

  [Test]
  public void LongSignature_IsWrittenOneParameterPerLine() {
    var parameters = Enumerable.Range(0, 20).Select(index => new PyParameter($"parameter{index}", N("int"))).ToList();
    var function = new PyFunctionDef("f", parameters, N("int"), [new PyReturn(new PyInt(0))]);
    var text = PythonPrinter.Print(function);
    Assert.That(text, Does.StartWith("def f(\n    parameter0: int,\n").And.Contain("    parameter19: int,\n) -> int:\n    return 0\n"));
    Assert.That(PythonSyntaxRewriter.Shape(PythonSyntaxReader.ReadModule(text)), Is.EqualTo(PythonSyntaxRewriter.Shape(new PyModule([], [function]))));
  }

  [Test]
  public void EmptyBodies_ArePass_AndAnElseOfOneIfIsElif() {
    var chained = new PyIf(N("a"), [], [new PyIf(N("b"), [new PyPass()], [])]);
    Assert.That(PythonPrinter.Print(chained), Is.EqualTo("if a:\n    pass\nelif b:\n    pass\n"));
    var commented = new PyIf(N("a"), [new PyBreak()], [new PyIf(N("b"), [new PyContinue()], []) { Leading = new PyTrivia(0, ["why"]) }]);
    Assert.That(PythonPrinter.Print(commented), Is.EqualTo("if a:\n    break\nelse:\n    # why\n    if b:\n        continue\n"));
  }

  [Test]
  public void PrintingAStatement_DropsTheLinesInFrontOfIt()
    => Assert.That(PythonPrinter.Print(new PyPass { Leading = PyTrivia.TwoBlank }), Is.EqualTo("pass\n"));

  [Test]
  public void Printer_RefusesWhatHasNoForm() {
    Assert.That(() => PythonPrinter.Print(new PyCompare([N("a")], [])), Throws.ArgumentException);
    Assert.That(() => PythonPrinter.Print(B(N("a"), "@", N("b"))), Throws.ArgumentException);
    Assert.That(() => PythonPrinter.Print(new Unknown()), Throws.ArgumentException);
    Assert.That(() => PythonPrinter.Print(new UnknownStatement()), Throws.ArgumentException);
  }

  private sealed record Unknown : PyExpression;

  private sealed record UnknownStatement : PyStatement;

  [TestCase("x = (", "brackets")]
  [TestCase("x = )", "closes no bracket")]
  [TestCase("x = $", "unexpected character")]
  [TestCase("if a:\n\tpass", "tabs")]
  [TestCase("if a:\n    pass\n  pass", "indentation")]
  [TestCase("x = \"abc", "not closed")]
  [TestCase("x = \"a\\", "not closed")]
  [TestCase("x = \"\\q\"", "escape")]
  [TestCase("x = \"\\x4\"", "broken")]
  [TestCase("x = 12a", "no number")]
  [TestCase("x = 1e999", "no number")]
  [TestCase("f(a b)", "expected ','")]
  [TestCase("[a b]", "expected ','")]
  [TestCase("def f(a: int b: int) -> int:\n    pass", "expected ','")]
  [TestCase("def f() int:\n    pass", "'->'")]
  [TestCase("f(x)[0]: int", "only a name")]
  [TestCase("x = if", "keyword")]
  [TestCase("x = ,", "unexpected")]
  public void Reader_NamesTheLineOfWhatIsNotTheSubset(string source, string message)
    => Assert.That(() => PythonSyntaxReader.ReadModule(source), Throws.TypeOf<FormatException>().With.Message.Contains(message));

  [Test]
  public void ReadExpression_TakesExactlyOneExpression() {
    Assert.That(() => PythonSyntaxReader.ReadExpression("a b"), Throws.TypeOf<FormatException>());
    Assert.That(PythonSyntaxReader.ReadExpression("(a)"), Is.EqualTo(N("a")));
  }

  [Test]
  public void Reader_ReadsMatchAsANameWhereItIsOne() {
    var module = PythonSyntaxReader.ReadModule("match = 1\nmatch.x\nmatch(1)\nmatch\n");
    Assert.That(module.Body.Select(statement => statement.GetType().Name), Is.EqualTo(new[] { "PyAssign", "PyExpressionStatement", "PyExpressionStatement", "PyExpressionStatement" }));
  }

  [Test]
  public void Rewrite_ThatChangesNothing_ReturnsTheSameTree() {
    var module = PythonSyntaxReader.ReadModule("def f(a: int) -> int:\n    x: int\n    y: int = (a := 1)\n    while a:\n        a = [a][0]\n    return a if a else -a\n");
    Assert.That(PythonSyntaxRewriter.Rewrite(module, node => node), Is.SameAs(module));
    var renamed = PythonSyntaxRewriter.Rewrite(module, node => node is PyName { Name: "a" } ? N("b") : node);
    Assert.That(PythonPrinter.Print(renamed), Does.Contain("return b if b else -b").And.Contain("while b:"));
  }

  [Test]
  public void DescendantsAndSelf_ListsEveryNodeInSourceOrder() {
    var tree = PythonSyntaxReader.ReadExpression("f(a, b.c)[d]");
    Assert.That(PythonSyntaxRewriter.DescendantsAndSelf(tree).OfType<PyName>().Select(name => name.Name), Is.EqualTo(new[] { "f", "a", "b", "d" }));
  }

  [Test]
  public void Shape_IgnoresLayoutAndTypes_ButNotMeaning() {
    var plain = new PyAssign(N("t"), new PyTuple([new PyInt(255)]));
    var dressed = new PyAssign(N("t"), new PyTuple([new PyInt(255) { Raw = "0xFF", Type = Polypiler.Core.IR.IrType.I32 }]) { Tabulated = true }) { Leading = PyTrivia.TwoBlank };
    Assert.That(PythonSyntaxRewriter.Shape(dressed), Is.EqualTo(PythonSyntaxRewriter.Shape(plain)));
    Assert.That(PythonSyntaxRewriter.Shape(new PyAssign(N("t"), new PyInt(254))), Is.Not.EqualTo(PythonSyntaxRewriter.Shape(new PyAssign(N("t"), new PyInt(255)))));
  }

  [Test]
  public void Shape_WritesEveryKindOfNode() {
    var nodes = new List<PyNode> {
      new PyBool(true), new PyNone(), new PyString("s\"\\"), new PyFloat(1.5), new PyGlobal(["a", "b"]), new PyImport("math"),
      new PyImportFrom("typing", ["List"]), new PyAnnotation("x", N("int")), new PyRaise(N("e")), new PyReturn(null),
      new PyNamedExpression("x", new PyInt(1)), new PyBoolOp(N("a"), "or", N("b")), new PyUnary("~", N("a")), new PyList([])
    };
    Assert.That(nodes.Select(PythonSyntaxRewriter.Shape).Distinct().Count(), Is.EqualTo(nodes.Count));
  }

}
