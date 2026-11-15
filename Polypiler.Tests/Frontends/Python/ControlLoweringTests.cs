using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// The scenarios and the test design of #449: <c>if</c>/<c>elif</c>, <c>while</c> and <c>for</c> over
/// <c>range</c> with their <c>else</c> clauses, <c>match</c> on literals, <c>assert</c>, multiple and
/// tuple assignment, with the iteration counts, evaluation order and binding rules of CPython.
/// </summary>
[TestFixture]
public sealed class ControlLoweringTests {

  private static string Digits(string range) => $"def f(n: int) -> int:\n    s = 0\n    for i in {range}:\n        s = s * 10 + i\n    return s\n";

  /// <summary>Scenario outline: range loops visit exactly the values python visits.</summary>
  [TestCase("range(1, n + 1)", 4L, "int 1234")]
  [TestCase("range(n)", 4L, "int 123")]
  [TestCase("range(n)", 0L, "int 0")]
  [TestCase("range(n, 0, -1)", 4L, "int 4321")]
  [TestCase("range(0, n + 1, 3)", 10L, "int 369")]
  public void RangeLoop_VisitsThePythonValues(string range, long n, string expected)
    => Assert.That(PythonSnippet.Run(Digits(range), "f", n), Is.EqualTo(expected));

  /// <summary>Scenario outline: the else clause of a loop runs only when the loop was not left by break.</summary>
  [TestCase(7L, "int 1")]
  [TestCase(9L, "int 0")]
  [TestCase(2L, "int 1")]
  public void ForElse_RunsOnlyWithoutBreak(long n, string expected) {
    const string source = "def f(n: int) -> int:\n    for i in range(2, n):\n        if n % i == 0:\n            break\n    else:\n        return 1\n    return 0\n";
    Assert.That(PythonSnippet.Run(source, "f", n), Is.EqualTo(expected));
  }

  /// <summary>Scenario outline: match tries its cases in order and falls through a false guard.</summary>
  [TestCase(0L, "int 10")]
  [TestCase(2L, "int 20")]
  [TestCase(9L, "int 30")]
  [TestCase(10L, "int 40")]
  [TestCase(-1L, "int 40")]
  public void Match_TriesCasesInOrder(long x, string expected) {
    const string source = "def f(x: int) -> int:\n    match x:\n        case 0:\n            return 10\n        case 1 | 2:\n            return 20\n"
      + "        case _ if 3 <= x <= 9:\n            return 30\n        case _:\n            return 40\n";
    Assert.That(PythonSnippet.Run(source, "f", x), Is.EqualTo(expected));
  }

  /// <summary>Scenario: a range is evaluated once and its loop variable is an ordinary local.</summary>
  [Test]
  public void Range_IsEvaluatedOnceBeforeTheLoop() {
    const string source = "def f(n: int) -> int:\n    c = 0\n    i = -1\n    for i in range(n):\n        n = 100\n        i = i + 10\n        c += 1\n    return c * 100 + i\n";
    Assert.That(PythonSnippet.Run(source, "f", 3L), Is.EqualTo("int 312"));
    var body = PythonSnippet.Module(source).Functions.Single().Body;
    // The bound is kept in a local before the loop, so that assigning n in the body changes nothing.
    var loop = PythonSnippet.Statements(body).OfType<IrFor>().Single();
    Assert.That(((IrBinary)loop.Condition).Right, Is.TypeOf<IrLocalRef>().And.Property("Local").Property("Name").EqualTo("stop"));
  }

  /// <summary>Scenario: a zero step traps before the first iteration.</summary>
  [Test]
  public void ZeroStep_RaisesValueErrorBeforeTheFirstPass() {
    const string source = "def f(n: int, s: int) -> int:\n    c = 0\n    for i in range(0, n, s):\n        c += 1\n    return c\n";
    Assert.That(PythonSnippet.Run(source, "f", 10L, 0L), Is.EqualTo("raise ValueError"));
    Assert.That(PythonSnippet.Run(source, "f", 10L, 5L), Is.EqualTo("int 2"));
    Assert.That(PythonSnippet.Run("def f(n: int) -> int:\n    for i in range(0, n, 0):\n        return 1\n    return 0\n", "f", 10L), Is.EqualTo("raise ValueError"));
  }

  /// <summary>Scenario: a loop variable that may be unbound after the loop is refused.</summary>
  [Test]
  public void PossiblyUnboundLoopVariable_IsRefused()
    => Assert.That(PythonSnippet.Refusals("def f(n: int) -> int:\n    for i in range(n):\n        pass\n    return i\n"),
      Is.EqualTo(new[] { "snippet.py(4,12): error PLY1811: 'i' may be unbound here; python would raise UnboundLocalError" }));

  /// <summary>Scenario: an unsupported match pattern is refused.</summary>
  [Test]
  public void SequencePattern_IsRefused()
    => Assert.That(PythonSnippet.Refusals("def f(x: int) -> int:\n    match x:\n        case [a, b]:\n            return a\n    return 0\n"),
      Is.EqualTo(new[] { "snippet.py(3,14): error PLY1812: match pattern '[a, b]' is not supported; supported are literals, members of an enum, '|', captures, '_' and guards" }));

  /// <summary>Test design 1 to 5: range forms, empty and one-element ranges, steps around the span, bounds near 64 bits.</summary>
  [TestCase(0L, 10L, 1L, "int 10")]
  [TestCase(5L, 5L, 1L, "int 0")]
  [TestCase(6L, 5L, 1L, "int 0")]
  [TestCase(5L, 6L, -1L, "int 0")]
  [TestCase(0L, 10L, 10L, "int 1")]
  [TestCase(0L, 10L, 11L, "int 1")]
  [TestCase(0L, 10L, 3L, "int 4")]
  [TestCase(0L, 10L, -1L, "int 0")]
  [TestCase(10L, 0L, -3L, "int 4")]
  [TestCase(long.MaxValue - 2, long.MaxValue, 1L, "int 2")]
  [TestCase(long.MaxValue - 5, long.MaxValue, 2L, "int 3")]
  [TestCase(long.MinValue + 5, long.MinValue, -2L, "int 3")]
  [TestCase(long.MinValue, long.MaxValue, long.MaxValue, "int 3")]
  [TestCase(long.MaxValue, long.MinValue, long.MinValue, "int 2")]
  public void RangeCount_IsPythons(long start, long stop, long step, string expected)
    => Assert.That(PythonSnippet.Run("def f(a: int, b: int, s: int) -> int:\n    c = 0\n    for _ in range(a, b, s):\n        c += 1\n    return c\n", "f", start, stop, step), Is.EqualTo(expected));

  [TestCase("range(3)", "int 3")]
  [TestCase("range(10, 0, -3)", "int 4")]
  [TestCase("range(0, 10, 4)", "int 3")]
  [TestCase("range(9223372036854775806, 9223372036854775807)", "int 1")]
  [TestCase("range(-9223372036854775807, -9223372036854775808, -1)", "int 1")]
  public void ConstantRange_CountsAsPython(string range, string expected)
    => Assert.That(PythonSnippet.Run($"def f() -> int:\n    c = 0\n    for _ in {range}:\n        c += 1\n    return c\n", "f"), Is.EqualTo(expected));

  /// <summary>Test design 5: a float argument of range raises TypeError, which the import refuses.</summary>
  [Test]
  public void FloatRange_IsRefused()
    => Assert.That(PythonSnippet.Refusals("def f(x: float) -> int:\n    c = 0\n    for _ in range(x):\n        c += 1\n    return c\n"),
      Is.EqualTo(new[] { "snippet.py(3,20): error PLY1813: iteration over 'range(x)' is not supported: a float argument raises TypeError" }));

  /// <summary>Test design 6: else on for and while - exhausted, empty, left by break, left by return, continue in the last pass.</summary>
  [TestCase(3L, 0L, "int 12")]
  [TestCase(0L, 0L, "int 10")]
  [TestCase(5L, 2L, "int 2")]
  [TestCase(5L, 100L, "int 14")]
  public void LoopElse_RunsOnTheExitsWithoutBreak(long n, long stopAt, string expected) {
    const string source = "def f(n: int, k: int) -> int:\n    r = 0\n    for i in range(n):\n        if i == k and k > 0:\n            r = i\n            break\n"
      + "        if i == n - 1:\n            continue\n        r += 1\n    else:\n        r += 10\n    return r\n";
    Assert.That(PythonSnippet.Run(source, "f", n, stopAt), Is.EqualTo(expected));
  }

  [TestCase(0L, "int 7")]
  [TestCase(3L, "int 7")]
  [TestCase(5L, "int -1")]
  [TestCase(9L, "int 9")]
  public void WhileElse_RunsWhenTheTestFails(long n, string expected) {
    const string source = "def f(n: int) -> int:\n    while n > 0:\n        if n == 5:\n            return -1\n        if n == 9:\n            break\n        n -= 1\n    else:\n        n = 7\n    return n\n";
    Assert.That(PythonSnippet.Run(source, "f", n), Is.EqualTo(expected));
  }

  /// <summary>Test design 8 and 9: if/elif/else on the truth of every type, while with break and continue, nested loops.</summary>
  [Test]
  public void Branches_TestTruth() {
    const string source = "def f(a: int, b: float, c: bool) -> int:\n    if a:\n        return 1\n    elif b:\n        return 2\n    elif c:\n        return 3\n    return 4\n";
    Assert.That(PythonSnippet.Run(source, "f", 5L, 0.0, false), Is.EqualTo("int 1"));
    Assert.That(PythonSnippet.Run(source, "f", 0L, double.NaN, false), Is.EqualTo("int 2"));
    Assert.That(PythonSnippet.Run(source, "f", 0L, -0.0, true), Is.EqualTo("int 3"));
    Assert.That(PythonSnippet.Run(source, "f", 0L, 0.0, false), Is.EqualTo("int 4"));
  }

  /// <summary>A while test that needs a temporary evaluates it on every pass.</summary>
  [Test]
  public void WhileTestWithATemporary_IsEvaluatedEveryPass() {
    const string source = "def g(x: int) -> int:\n    return x + 1\n\ndef f(n: int) -> int:\n    i = 0\n    while 0 <= g(i) < n:\n        i += 1\n    else:\n        i += 100\n    return i\n";
    Assert.That(PythonSnippet.Run(source, "f", 5L), Is.EqualTo("int 104"));
  }

  /// <summary>Test design 10 and 11: match forms - literals, a negative literal, alternatives, captures, guards, a bool and a float subject, no case matching.</summary>
  [TestCase(5L, "int 0")]
  [TestCase(-3L, "int 103")]
  [TestCase(150L, "int 50")]
  [TestCase(7L, "int 14")]
  public void MatchCapture_BindsTheSubject(long x, string expected) {
    const string source = "def f(x: int) -> int:\n    match x * 1:\n        case 5:\n            return 0\n        case -3:\n            return 103\n"
      + "        case n if n > 100:\n            return n - 100\n        case n:\n            return n * 2\n";
    Assert.That(PythonSnippet.Run(source, "f", x), Is.EqualTo(expected));
  }

  [TestCase(true, 1.0, "int 11")]
  [TestCase(false, 2.5, "int 22")]
  [TestCase(true, 3.0, "int 1")]
  public void MatchOnBoolAndFloat_ComparesAsPython(bool b, double x, string expected) {
    const string source = "def f(b: bool, x: float) -> int:\n    r = 0\n    match b:\n        case True:\n            r = 1\n        case False:\n            r = 2\n        case None:\n            r = 9\n"
      + "    match x:\n        case 1:\n            r += 10\n        case 2.5:\n            r += 20\n    return r\n";
    Assert.That(PythonSnippet.Run(source, "f", b, x), Is.EqualTo(expected));
  }

  [Test]
  public void TrueAndFalsePatterns_NeverMatchAnInt() {
    const string source = "def f(x: int) -> int:\n    match x:\n        case True:\n            return 1\n        case 1:\n            return 2\n    return 0\n";
    Assert.That(PythonSnippet.Run(source, "f", 1L), Is.EqualTo("int 2"));
  }

  [Test]
  public void ManyLiteralCases_FirstMatchWins() {
    var cases = string.Concat(Enumerable.Range(0, 300).Select(value => $"        case {value}:\n            return {value * 2}\n"));
    var source = "def f(x: int) -> int:\n    match x:\n" + cases + "        case 7:\n            return -1\n    return -2\n";
    Assert.That(PythonSnippet.Run(source, "f", 7L), Is.EqualTo("int 14"));
    Assert.That(PythonSnippet.Run(source, "f", 299L), Is.EqualTo("int 598"));
    Assert.That(PythonSnippet.Run(source, "f", 300L), Is.EqualTo("int -2"));
  }

  /// <summary>Test design 12: assignment forms - chained, swap, rotation, a right side with effects.</summary>
  [Test]
  public void Assignments_EvaluateTheRightSideFirst() {
    const string source = "def g(x: int) -> int:\n    return 10 // x\n\n"
      + "def swap(a: int, b: int) -> int:\n    a, b = b, a\n    return a * 10 + b\n\n"
      + "def rotate(a: int, b: int, c: int) -> int:\n    a, b, c = b, c, a\n    return a * 100 + b * 10 + c\n\n"
      + "def chained(x: int) -> int:\n    a = b = g(x)\n    return a + b\n\n"
      + "def effects(x: int) -> int:\n    a = 1\n    a, b = g(x), a\n    return a * 100 + b\n";
    var module = PythonSnippet.Module(source);
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("swap", [1L, 2L])), Is.EqualTo("int 21"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("rotate", [1L, 2L, 3L])), Is.EqualTo("int 231"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("chained", [2L])), Is.EqualTo("int 10"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("effects", [5L])), Is.EqualTo("int 201"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("effects", [0L])), Is.EqualTo("raise ZeroDivisionError"));
  }

  /// <summary>Test design 13: assert kept and stripped.</summary>
  [Test]
  public void Assert_IsKeptOrStripped() {
    const string source = "def f(x: int) -> int:\n    assert x >= 0, \"negative\"\n    assert x != 3\n    return x\n";
    Assert.That(PythonSnippet.Run(source, "f", -1L), Is.EqualTo("raise AssertionError"));
    Assert.That(PythonSnippet.Run(source, "f", 3L), Is.EqualTo("raise AssertionError"));
    var stripped = PythonFixture.Module(PythonSnippet.Import(source, new PythonImportOptions { StripAssertions = true }));
    Assert.That(PythonOutcome.Interpreted(stripped, new PythonCall("f", [-1L])), Is.EqualTo("int -1"));
    Assert.That(PythonSnippet.Refusals("def f(x: int) -> int:\n    assert x, x\n    return x\n"),
      Is.EqualTo(new[] { "snippet.py(2,15): error PLY1810: statement 'assert' with a message other than a string literal is not supported by the Python frontend" }));
  }

  /// <summary>Test design 15 and 16: unsupported patterns, iterables and statements.</summary>
  [TestCase("    match x:\n        case {1: a}:\n            return 1\n", "error PLY1812: match pattern '{1: a}' is not supported")]
  [TestCase("    match x:\n        case int():\n            return 1\n", "error PLY1812: match pattern 'int()' is not supported")]
  [TestCase("    match x:\n        case 1 as y:\n            return y\n", "error PLY1812: match pattern '1 as y' is not supported")]
  [TestCase("    match x:\n        case 1 | y:\n            return 1\n", "error PLY1812: match pattern 'y' is not supported")]
  [TestCase("    for a in 3:\n        pass\n", "error PLY1813: iteration over '3' of the type int is not supported")]
  [TestCase("    for a in range(1, 2, 3, 4):\n        pass\n", "error PLY1813: iteration over 'range(1, 2, 3, 4)' is not supported")]
  [TestCase("    for a, b in range(3):\n        pass\n", "error PLY1810: statement 'unpacking a value of the type int into 2 names, which raises TypeError or ValueError,' is not supported")]
  [TestCase("    with x:\n        pass\n", "error PLY1843: exception construct 'with' is not supported")]
  [TestCase("    del x\n", "error PLY1810: statement 'del' is not supported")]
  [TestCase("    raise ValueError() from None\n", "error PLY1843: exception construct 'raise … from' is not supported")]
  [TestCase("    import math\n", "error PLY1833: 'import math' is neither part of this import nor in the Python call map: an import inside a function")]
  [TestCase("    *a, b = 1, 2\n", "error PLY1810: statement 'starred assignment target' is not supported")]
  [TestCase("    a, b = 1, 2, 3\n", "error PLY1810: statement 'tuple assignment whose right side is no tuple of as many values' is not supported")]
  [TestCase("    (a, b), c = (1, 2), 3\n", "error PLY1810: statement 'nested tuple assignment' is not supported")]
  [TestCase("    x[0] = 1\n", "error PLY1802: 'assignment to an element of a value of the type int, which raises TypeError,' is not supported")]
  [TestCase("    x.y = 1\n", "error PLY1802: 'assignment to the attribute y of a value of the type int, which raises AttributeError,' is not supported")]
  [TestCase("    global x\n", "error PLY1833: 'global x' is neither part of this import nor in the Python call map: 'x' is assigned at no module level")]
  [TestCase("    if x:\n        def g() -> int:\n            return 1\n", "error PLY1830: 'def g inside a loop or conditional' is not supported for functions by the Python frontend")]
  public void UnsupportedStatement_IsRefused(string body, string diagnostic)
    => Assert.That(PythonSnippet.Refusals("def f(x: int) -> int:\n" + body + "    return 0\n"), Has.Some.Contains(diagnostic));

  /// <summary>Test design 17: a trap inside a loop raises after the earlier passes ran.</summary>
  [Test]
  public void TrapInsideALoop_KeepsItsPosition() {
    const string source = "def f(n: int) -> int:\n    s = 0\n    for i in range(n):\n        s += 10 // (2 - i)\n    return s\n";
    Assert.That(PythonSnippet.Run(source, "f", 2L), Is.EqualTo("int 15"));
    Assert.That(PythonSnippet.Run(source, "f", 3L), Is.EqualTo("raise ZeroDivisionError"));
  }

  /// <summary>A loop variable the body does not assign and nothing reads after the loop is the counter itself.</summary>
  [Test]
  public void UnusedLoopVariable_IsTheCounter() {
    var function = PythonSnippet.Module("def f(n: int) -> int:\n    s = 0\n    for i in range(n):\n        s += i\n    return s\n").Functions.Single();
    Assert.That(function.Locals.Select(local => local.Name), Is.EqualTo(new[] { "s", "i" }));
  }

  /// <summary>Expression statements evaluate for their effects; a docstring does nothing.</summary>
  [Test]
  public void ExpressionStatements_AreEvaluated() {
    const string source = "def f(a: int, b: int) -> int:\n    \"\"\"Divides.\"\"\"\n    a // b\n    return a\n";
    Assert.That(PythonSnippet.Run(source, "f", 1L, 0L), Is.EqualTo("raise ZeroDivisionError"));
    Assert.That(PythonSnippet.Run(source, "f", 1L, 2L), Is.EqualTo("int 1"));
  }

  /// <summary>An operand used twice inside a condition, or after another operation that may raise, cannot be moved into a temporary.</summary>
  [TestCase("a if a else (g(a) or 1)")]
  [TestCase("g(a) + (g(b) or 1)")]
  [TestCase("a < b < g(a) < 3")]
  public void TemporaryThatWouldReorder_IsRefused(string expression)
    => Assert.That(PythonSnippet.Refusals($"def g(x: int) -> int:\n    return x\n\ndef f(a: int, b: int) -> int:\n    return {expression}\n"),
      Has.One.Contains("used twice where it cannot be evaluated first' is not supported by the Python frontend"));

}
