using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Python.Printing;
using Polypiler.Core.Backends.Python.Syntax;

namespace Polypiler.Tests.Backends.Python;

/// <summary>
/// The nodes of the syntax tree the object model, the exceptions, the closures and the sequences
/// need: classes with decorators and bases, <c>try</c>/<c>except</c>/<c>finally</c>, a bare
/// <c>raise</c>, <c>nonlocal</c>, lambdas, <c>yield</c>, dictionaries, slices, keyword and starred
/// arguments, parameters without a type or with a default, the comparisons <c>is</c>, <c>is not</c>
/// and <c>not in</c>, and a <c>for</c> over pairs - each printed, read back to the same shape and
/// printed again to the same text.
/// </summary>
[TestFixture]
public sealed class PythonObjectSyntaxTests {

  [TestCase("x is None", "x is None")]
  [TestCase("x is not None", "x is not None")]
  [TestCase("k not in d", "k not in d")]
  [TestCase("lambda a, b: a + b", "lambda a, b: a + b")]
  [TestCase("lambda: 1", "lambda: 1")]
  [TestCase("f(lambda v: v * 2)", "f(lambda v: v * 2)")]
  [TestCase("(lambda: 1)()", "(lambda: 1)()")]
  [TestCase("{}", "{}")]
  [TestCase("{1: \"a\", k: v}", "{1: \"a\", k: v}")]
  [TestCase("xs[1:3]", "xs[1:3]")]
  [TestCase("xs[:n]", "xs[:n]")]
  [TestCase("xs[n:]", "xs[n:]")]
  [TestCase("xs[:]", "xs[:]")]
  [TestCase("v.to_bytes(4, \"little\", signed=True)", "v.to_bytes(4, \"little\", signed=True)")]
  [TestCase("f(*args)", "f(*args)")]
  [TestCase("x = (yield v)", "x = (yield v)")]
  public void Expression_PrintsAndReadsBack(string source, string printed) {
    var tree = source.StartsWith("x = ", StringComparison.Ordinal)
      ? ((PyAssign)PythonSyntaxReader.ReadModule(source).Body[0]).Value
      : PythonSyntaxReader.ReadExpression(source);
    var text = PythonPrinter.Print(tree);
    Assert.That(source.StartsWith("x = ", StringComparison.Ordinal) ? "x = " + text : text, Is.EqualTo(printed));
  }

  private const string Module = """
    from __future__ import annotations


    @dataclass(eq=False, frozen=True)
    class Point:
        x: int = 0
        y: float = 0.0

        def __eq__(self, other: object) -> bool:
            return isinstance(other, Point) and self.x == other.x


    class Dog(Animal, Named):
        def __init__(self, *args: object) -> None:
            super().__init__()

        @property
        def legs(self) -> int:
            return 4

        @legs.setter
        def legs(self, value: int) -> None:
            pass

        @staticmethod
        def make(count: int = 1) -> Dog:
            return Dog()


    class Empty:
        pass


    def f(x: int) -> int:
        total = 0
        def step(v: int, cell=cell) -> None:
            nonlocal total
            total = total + v
        try:
            step(x)
        except (ValueError, IndexError) as error:
            raise
        except ZeroDivisionError:
            return -1
        except:
            pass
        finally:
            total = total + 1
        try:
            pass
        finally:
            pass
        for key, value in d.items():
            yield key
        yield from xs
        yield
        raise ValueError("negative")
    """;

  [Test]
  public void Module_OfClassesAndHandlers_ReadsBackToTheSameShapeAndText() {
    var tree = PythonSyntaxReader.ReadModule(Module);
    var printed = PythonPrinter.Print(tree);
    var again = PythonSyntaxReader.ReadModule(printed);
    Assert.That(PythonSyntaxRewriter.Shape(again), Is.EqualTo(PythonSyntaxRewriter.Shape(tree)));
    Assert.That(PythonPrinter.Print(again), Is.EqualTo(printed));
    Assert.That(printed, Does.Contain("class Dog(Animal, Named):").And.Contain("@legs.setter").And.Contain("except (ValueError, IndexError) as error:")
      .And.Contain("        raise\n").And.Contain("nonlocal total").And.Contain("def step(v: int, cell=cell) -> None:").And.Contain("for key, value in d.items():")
      .And.Contain("yield from xs").And.Contain("def make(count: int = 1) -> Dog:").And.Contain("class Empty:\n    pass"));
  }

  [Test]
  public void Rewriter_ReachesEveryChildOfTheNewNodes() {
    var tree = PythonSyntaxReader.ReadModule(Module);
    var names = PythonSyntaxRewriter.DescendantsAndSelf(tree).OfType<PyName>().Select(name => name.Name).ToHashSet();
    Assert.That(names, Is.SupersetOf(new[] { "dataclass", "Animal", "Named", "isinstance", "ValueError", "IndexError", "ZeroDivisionError", "step", "xs", "d", "cell", "Dog" }));

    // Renaming every name renames it everywhere, and a rewrite that changes nothing keeps the tree.
    var renamed = PythonSyntaxRewriter.Rewrite(tree, node => node is PyName { Name: "xs" } ? new PyName("ys") : node);
    Assert.That(PythonPrinter.Print(renamed), Does.Contain("yield from ys"));
    Assert.That(PythonSyntaxRewriter.Rewrite(tree, node => node), Is.SameAs(tree));
  }

  [Test]
  public void Try_WithoutHandlersOrFinally_IsRefused() {
    Assert.That(() => PythonSyntaxReader.ReadModule("try:\n    pass\nx = 1\n"), Throws.TypeOf<FormatException>());
  }

  [Test]
  public void Try_WithNeitherClauseNorFinallyInTheTree_PrintsAnEmptyFinally() {
    var printed = PythonPrinter.Print(new PyTry([new PyPass()], [], []));
    Assert.That(printed, Is.EqualTo("try:\n    pass\nfinally:\n    pass\n"));
  }

  [Test]
  public void Keyword_ThatIsAKeyword_StandsNowhereAnExpressionIsExpected() {
    Assert.That(() => PythonSyntaxReader.ReadExpression("yield + class"), Throws.TypeOf<FormatException>());
    Assert.That(() => PythonSyntaxReader.ReadExpression("{1 2}"), Throws.TypeOf<FormatException>());
    Assert.That(() => PythonSyntaxReader.ReadExpression("lambda a b: 1"), Throws.TypeOf<FormatException>());
  }

}
