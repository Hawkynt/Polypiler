using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.Frontends.Python.AstDump;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// The edges of the import beside the scenarios: conversions between the number types, temporaries
/// that keep the order of evaluation, the loop and match forms that need a flag or a fresh name,
/// unreachable code, names the module takes from the helpers, and an interpreter that does not parse.
/// Every value is CPython's.
/// </summary>
[TestFixture]
public sealed class LoweringEdgeTests {

  /// <summary>A bool stored into an int or a float variable, constant or not, is converted; a bool operand of arithmetic counts as 0 or 1.</summary>
  [TestCase(true, "float 11.0")]
  [TestCase(false, "float 6.5")]
  public void BoolStoredAsANumber_IsConverted(bool c, string expected) {
    const string source = "def f(c: bool) -> float:\n    x = True\n    x = 2\n    y = 1.5\n    y = c\n    z = 0.5\n    z = False\n    w = 3\n    w = c\n"
      + "    return x + y + z + w + True * 2.5 + c * 2.5 + (True + 1)\n";
    Assert.That(PythonSnippet.Run(source, "f", c), Is.EqualTo(expected));
  }

  /// <summary>A left operand of 'or' that is no variable but raises nothing is kept in a temporary without a question of order.</summary>
  [TestCase(0.0, "float 2.0")]
  [TestCase(1.5, "float -1.5")]
  public void PureOperandUsedTwice_IsKept(double x, string expected)
    => Assert.That(PythonSnippet.Run("def f(x: float, y: float) -> float:\n    return -x or y\n", "f", x, 2.0), Is.EqualTo(expected));

  [TestCase(1.5, "bool True")]
  [TestCase(0.5, "bool False")]
  public void IntConstantComparedWithAFloat_ComparesAsTheFloat(double x, string expected)
    => Assert.That(PythonSnippet.Run("def f(x: float) -> bool:\n    return 1 < x\n", "f", x), Is.EqualTo(expected));

  /// <summary>The arguments of range that are neither constants nor stable variables are kept in temporaries named after their roles.</summary>
  [Test]
  public void RangeArguments_AreKeptByTheirRoles() {
    const string source = "def f(a: int, b: int, s: int) -> int:\n    c = 0\n    for i in range(a + 0, b * 1, s - 0):\n        c += i\n    return c\n";
    Assert.That(PythonSnippet.Run(source, "f", 0L, 10L, 3L), Is.EqualTo("int 18"));
    Assert.That(PythonSnippet.Module(source).Functions.Single().Locals.Select(local => local.Name), Is.SupersetOf(new[] { "start", "stop", "step" }));
  }

  /// <summary>A capture inside a loop keeps the loop variable a counter of its own; a literal case with a guard and a last capture with a guard fall through.</summary>
  [TestCase(5L, "int 107")]
  [TestCase(0L, "int 0")]
  public void CapturesAndGuards_InLoopsAndAtTheEnd(long n, string expected) {
    const string source = "def f(n: int) -> int:\n    s = 0\n    for i in range(n):\n        match i:\n            case 0:\n                s += 100\n            case k if k > 2:\n                s += k\n"
      + "    match s:\n        case 1 if n > 100:\n            return -1\n        case i if i > 1000:\n            return i\n    return s\n";
    Assert.That(PythonSnippet.Run(source, "f", n), Is.EqualTo(expected));
  }

  /// <summary>A result of an int on one path and a float on another is a float.</summary>
  [Test]
  public void ResultOfIntAndFloat_IsWidened() {
    const string source = "def f(c: bool) -> float:\n    if c:\n        return 1\n    return 2.5\n";
    // CPython gives int 1 for True; the value is the same, the type the wider one (py-number-widened).
    Assert.That(PythonSnippet.Run(source, "f", true), Is.EqualTo("float 1.0"));
    Assert.That(PythonSnippet.Import(source).Caveats.Select(caveat => caveat.Id), Does.Contain(PythonFrontend.Widened));
  }

  [Test]
  public void BoolLocalAndBareAnnotation_Import()
    => Assert.That(PythonSnippet.Run("def f(a: int) -> bool:\n    flag = a > 0\n    y: int\n    return flag\n", "f", 1L), Is.EqualTo("bool True"));

  /// <summary>A break inside a match inside a loop with an else clause sets the flag.</summary>
  [TestCase(2L, "int -1")]
  [TestCase(5L, "int 3")]
  public void BreakInsideMatch_SkipsTheElse(long n, string expected) {
    const string source = "def f(n: int) -> int:\n    r = 0\n    for i in range(n):\n        match i:\n            case 3:\n                break\n        r += 1\n    else:\n        r = -1\n    return r\n";
    Assert.That(PythonSnippet.Run(source, "f", n), Is.EqualTo(expected));
  }

  /// <summary>Code after a return is never read for definite assignment; a constant range of a zero step raises.</summary>
  [Test]
  public void UnreachableCodeAndConstantZeroStep() {
    Assert.That(PythonSnippet.Run("def f(n: int) -> int:\n    for i in range(3):\n        return i\n    return 0\n    x = 2\n", "f", 1L), Is.EqualTo("int 0"));
    Assert.That(PythonSnippet.Run("def f() -> int:\n    for i in range(1, 5, 0):\n        pass\n    return 0\n", "f"), Is.EqualTo("raise ValueError"));
    Assert.That(PythonSnippet.Run("def f() -> int:\n    c = 0\n    for i in range(+3):\n        c += 1\n    return c\n", "f"), Is.EqualTo("int 3"));
  }

  [Test]
  public void WhileOfATrueFloat_RunsUntilBreak()
    => Assert.That(PythonSnippet.Run("def f(n: int) -> int:\n    while 1.0:\n        x = n\n        break\n    return x\n", "f", 4L), Is.EqualTo("int 4"));

  [TestCase(3L, "int 1")]
  [TestCase(0L, "int 0")]
  public void ElseBranchThatReturns_LeavesTheThenBranchesAssignments(long n, string expected)
    => Assert.That(PythonSnippet.Run("def f(n: int) -> int:\n    if n:\n        x = 1\n    else:\n        return 0\n    return x\n", "f", n), Is.EqualTo(expected));

  /// <summary>Recursion through a unary operator and through 'or' is typed by the fixed point.</summary>
  [Test]
  public void RecursionThroughOperators_IsTyped() {
    const string source = "def f(n: int) -> int:\n    return -f(n - 1) if n > 0 else 0\n\ndef g(n: int) -> int:\n    if n > 0:\n        return g(n - 1) or n\n    return -n\n";
    Assert.That(PythonSnippet.Run(source, "f", 3L), Is.EqualTo("int 0"));
    Assert.That(PythonSnippet.Run(source, "g", 3L), Is.EqualTo("int 1"));
  }

  /// <summary>A function of the module named like a helper keeps its name; the helper takes another.</summary>
  [Test]
  public void FunctionNamedLikeAHelper_KeepsItsName() {
    var module = PythonSnippet.Module("def PyAbsInt(x: int) -> int:\n    return abs(x) + 1\n");
    Assert.That(module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "PyAbsInt", "PyAbsInt_1" }));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("PyAbsInt", [-3L])), Is.EqualTo("int 4"));
  }

  /// <summary>An integer literal below 64 bits is refused as one above them; a multi-line construct is quoted by its first line.</summary>
  [Test]
  public void NegativeHugeLiteralAndMultiLineConstruct_AreRefused() {
    Assert.That(PythonSnippet.Refusals("def f() -> int:\n    return -18446744073709551616\n"), Has.Some.Contains("error PLY1804: the integer '-18446744073709551616' needs more than 64 bits"));
    Assert.That(PythonSnippet.Refusals("def f() -> int:\n    return {1:\n            2}\n"), Has.Some.Contains("error PLY1842: dict or set operation '{1:…' is not supported by the Python frontend"));
  }

  /// <summary>A conditional expression of a number and a call that returns None is refused: the call could not run before its None is given.</summary>
  [Test]
  public void ConditionalOfNoneAndANumber_IsRefused()
    => Assert.That(PythonSnippet.Refusals("def g() -> None:\n    pass\n\ndef f(a: int) -> int:\n    return a if a else g()\n"),
      Has.Some.Contains("error PLY1802: 'the value None of g()' is not supported by the Python frontend"));

  [TestCase("    try:\n        pass\n    except* ValueError:\n        pass\n", "exception construct 'except*'")]
  [TestCase("    from math import floor\n", "an import inside a function")]
  [TestCase("    class C:\n        pass\n", "error PLY1840: 'nested class C' is outside the first Python subset")]
  [TestCase("    type T = int\n", "statement 'type'")]
  [TestCase("    nonlocal x\n", "error PLY1833: 'nonlocal x' is neither part of this import nor in the Python call map: no enclosing function binds 'x'")]
  [TestCase("    match x:\n        case [z] as y:\n            pass\n", "match pattern '[z] as y' is not supported")]
  public void MoreUnsupportedStatements_AreRefused(string body, string diagnostic)
    => Assert.That(PythonSnippet.Refusals("def f(x: int) -> int:\n" + body + "    return 0\n"), Has.Some.Contains(diagnostic));

  /// <summary>Only <c>if __name__ == "__main__":</c> is left out of the module; every other module-level if is refused.</summary>
  [TestCase("if __name__ != \"__main__\":\n    pass\n")]
  [TestCase("if name == \"__main__\":\n    pass\n")]
  [TestCase("if __name__ == \"main\":\n    pass\n")]
  [TestCase("if __name__ == 1:\n    pass\n")]
  [TestCase("if True:\n    pass\n")]
  public void OtherModuleLevelIf_IsRefused(string statement)
    => Assert.That(PythonSnippet.Refusals("def f() -> int:\n    return 1\n\n" + statement), Has.Some.Contains("error PLY1810: statement 'module-level if"));

  /// <summary>An interpreter that starts but does not run the helper is a refusal that carries what it said.</summary>
  [Test]
  public void InterpreterThatFails_IsRefusedWithPLY1800() {
    var result = PythonFrontend.Import("f.py", "def f() -> int:\n    return 1\n", new PythonImportOptions { Interpreter = "dotnet" });
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Single().Code, Is.EqualTo(PythonFrontend.SyntaxError));
    Assert.That(result.Diagnostics.Single().Message, Does.StartWith("dotnet exited with "));
    Assert.That(PythonFrontend.Dump("f.py", "", "dotnet").Failure, Does.StartWith("dotnet exited with "));
  }

  /// <summary>The environment variable names the interpreter tried after the one the options name.</summary>
  [Test, NonParallelizable]
  public void EnvironmentVariable_NamesAnInterpreter() {
    var saved = Environment.GetEnvironmentVariable(AstHelper.InterpreterVariable);
    try {
      Environment.SetEnvironmentVariable(AstHelper.InterpreterVariable, Path.Combine("opt", "python"));
      Assert.That(AstHelper.Candidates(null), Is.EqualTo(new[] { Path.Combine("opt", "python"), "python3", "python" }));
    } finally {
      Environment.SetEnvironmentVariable(AstHelper.InterpreterVariable, saved);
    }
  }

  /// <summary>The caveats describe what differs.</summary>
  [Test]
  public void Caveats_AreDescribed() {
    Assert.That(PythonFrontend.Describe(PythonFrontend.Int64), Does.Contain("OverflowError"));
    Assert.That(PythonFrontend.Describe(PythonFrontend.ComplexPower), Does.Contain("complex"));
    var result = PythonSnippet.Import("def f(a: float, b: float) -> float:\n    return a ** b\n");
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Is.EqualTo(new[] { PythonFrontend.ComplexPower }));
    Assert.That(result.Module!.Caveats, Is.EqualTo(new[] { PythonFrontend.ComplexPower }));
  }

  /// <summary>The forms the parser accepts in a plain function but the subset does not: coroutine syntax, unpacking, slices in a tuple, odd loop targets, raising what is no exception.</summary>
  [TestCase("await x", "error PLY1832: the function value 'f' escapes, or 'f' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported (coroutines: #752)")]
  [TestCase("async for y in a:\n        pass", "error PLY1810: statement 'async for' is not supported")]
  [TestCase("b = [y async for y in a]", "(coroutines: #752)")]
  [TestCase("b = [*a]", "error PLY1820: sequence operation '*a in a display' is not supported")]
  [TestCase("b = a[1:2, 0]", "error PLY1821: slice form 'a[1:2, 0]' is not supported")]
  [TestCase("for a[0] in [1]:\n        pass", "error PLY1810: statement 'a loop variable other than names' is not supported")]
  [TestCase("b = x.to_bytes(2, order)", "error PLY1823: struct format or array typecode 'order' is not supported: the byte order is a constant \"little\" or \"big\"")]
  [TestCase("def g() -> int:\n        return 1\n    def g() -> int:\n        return 2", "error PLY1830: 'redefinition of g' is not supported for functions")]
  [TestCase("raise C()", "error PLY1843: exception construct 'raise C(), which is no exception' is not supported")]
  [TestCase("raise ValueError(msg='x')", "error PLY1843: exception construct 'an exception made with other arguments than a message: ValueError(msg='x')'")]
  [TestCase("try:\n        pass\n    except C:\n        pass", "error PLY1843: exception construct 'except C, which names no exception class'")]
  public void ParsedFormsOutsideTheSubset_AreRefused(string body, string diagnostic)
    => Assert.That(PythonSnippet.Refusals($"class C:\n    pass\n\ndef f(a: list[int], x: int, order: str) -> int:\n    {body}\n    return 0\n"), Has.Some.Contains(diagnostic));

}
