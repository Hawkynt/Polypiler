using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// The plain-function part of #450: module-level <c>def</c>s with type hints, calls between them,
/// recursion, the public surface of the module; and the module-level statements and function features
/// the import refuses until the rest of #450 brings them.
/// </summary>
[TestFixture]
public sealed class FunctionLoweringTests {

  [Test]
  public void Functions_CallEachOtherAndRecurse() {
    const string source = "def _twice(x):\n    return x * 2\n\ndef even(n: int) -> bool:\n    if n == 0:\n        return True\n    return odd(n - 1)\n\n"
      + "def odd(n: int) -> bool:\n    if n == 0:\n        return False\n    return even(n - 1)\n\ndef f(n: int) -> int:\n    return _twice(n) + _twice(1)\n";
    var module = PythonSnippet.Module(source);
    Assert.That(module.Name, Is.EqualTo("snippet"));
    Assert.That(module.Functions.Select(function => (function.Name, function.IsPublic)), Is.EqualTo(new[] { ("_twice", false), ("even", true), ("odd", true), ("f", true) }));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("even", [10L])), Is.EqualTo("bool True"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("odd", [10L])), Is.EqualTo("bool False"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("f", [5L])), Is.EqualTo("int 12"));
  }

  /// <summary>A function that returns nothing returns None, is called as a statement and has no result in the IR.</summary>
  [Test]
  public void FunctionWithoutResult_IsVoid() {
    const string source = "def g(n: int) -> None:\n    n += 1\n    return\n\ndef h(n: int):\n    n += 1\n\ndef f(n: int) -> int:\n    g(n)\n    h(n)\n    return n\n";
    var module = PythonSnippet.Module(source);
    Assert.That(module.Functions.Where(function => function.Name is "g" or "h").Select(function => function.ReturnType), Has.All.EqualTo(IrType.Void));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("g", [1L])), Is.EqualTo("None"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("f", [1L])), Is.EqualTo("int 1"));
  }

  /// <summary>A result only recursion reaches takes the type of its hint.</summary>
  [Test]
  public void ResultOnlyRecursionReaches_TakesItsHint() {
    const string source = "def f(n: int) -> int:\n    return f(n)\n";
    Assert.That(PythonSnippet.Module(source).Functions.Single().ReturnType, Is.EqualTo(IrType.BigInt));
    Assert.That(PythonSnippet.Refusals("def f(n: int):\n    return f(n)\n"), Has.Some.Contains("error PLY1801: the result type of 'f' is not known"));
  }

  /// <summary>An int argument for a float parameter is converted, as a caveat says.</summary>
  [Test]
  public void IntArgumentForAFloatParameter_IsConverted() {
    const string source = "def half(x: float) -> float:\n    return x / 2\n\ndef f(n: int) -> float:\n    return half(n)\n";
    var result = PythonSnippet.Import(source);
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Does.Contain(PythonFrontend.Widened));
    Assert.That(PythonOutcome.Interpreted(result.Module!, new PythonCall("f", [3L])), Is.EqualTo("float 1.5"));
  }

  [TestCase("x: list", "error PLY1801: the type hint 'list' of 'x' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], T | None and the classes of the module")]
  [TestCase("x: None", "error PLY1801: the type hint 'None' of 'x' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], T | None and the classes of the module")]
  [TestCase("x: 'float'", null)]
  public void ParameterHints_AreTheSupportedTypes(string parameter, string? diagnostic) {
    var source = $"def f({parameter}) -> float:\n    return x + 1\n";
    if (diagnostic is null)
      Assert.That(PythonSnippet.Run(source, "f", 1.5), Is.EqualTo("float 2.5"));
    else
      Assert.That(PythonSnippet.Refusals(source), Has.Some.Contains(diagnostic));
  }

  /// <summary>Every spelling of a hint the import knows: written as code, as a forward reference, through typing, and the ones it does not.</summary>
  [TestCase("'list[int]'", true)]
  [TestCase("' int | None '", true)]
  [TestCase("'typing.List[int]'", true)]
  [TestCase("'Optional[ValueError]'", true)]
  [TestCase("bytearray", true)]
  [TestCase("ValueError", true)]
  [TestCase("List[int]", true)]
  [TestCase("typing.List[int]", true)]
  [TestCase("tuple[int, float]", true)]
  [TestCase("''", false)]
  [TestCase("'list[int'", false)]
  [TestCase("'int float'", false)]
  [TestCase("'[int]'", false)]
  [TestCase("'int | str'", false)]
  [TestCase("math.pi", false)]
  [TestCase("3", false)]
  [TestCase("dict | None", false)]
  [TestCase("None | dict", false)]
  [TestCase("int | str", false)]
  [TestCase("tuple[int, None]", false)]
  [TestCase("tuple[int, ...]", false)]
  [TestCase("list[int, int]", false)]
  [TestCase("Union[int, str, float]", false)]
  [TestCase("Optional[None]", false)]
  [TestCase("Iterator[...]", false)]
  [TestCase("Final[int]", false)]
  [TestCase("Optional[list[int]]", false)]
  [TestCase("Iterator[int]", false)]
  public void HintSpellings_AreKnownOrRefused(string hint, bool known) {
    var diagnostics = PythonSnippet.Import($"import math\nimport typing\nfrom typing import Final, Iterator, List, Optional, Union\n\ndef f(x: {hint}) -> int:\n    return 0\n").Diagnostics.Select(diagnostic => diagnostic.ToString());
    if (known)
      Assert.That(diagnostics, Is.Empty);
    else
      Assert.That(diagnostics, Has.Some.Contains($"error PLY1801: the type hint '{hint}' of 'x' is not supported"));
  }

  [Test]
  public void UnsupportedReturnHint_IsRefused()
    => Assert.That(PythonSnippet.Refusals("def f(x: int) -> dict[str, int]:\n    return x\n"),
      Has.Some.Contains("error PLY1801: the return type hint 'dict[str, int]' of 'f' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], T | None and the classes of the module and None"));

  [Test]
  public void ParameterOfTwoKindsOfCalls_IsTheWiderOne() {
    const string source = "def _sq(x):\n    return x * x\n\ndef f(a: int, b: float) -> float:\n    return _sq(a) + _sq(b)\n";
    Assert.That(PythonSnippet.Module(source).Functions.Single(function => function.Name == "_sq").Parameters.Single().Type, Is.EqualTo(IrType.F64));
    Assert.That(PythonSnippet.Run(source, "f", 3L, 0.5), Is.EqualTo("float 9.25"));
  }

  [Test]
  public void LocalOfAStrAndANumber_IsRefused()
    => Assert.That(PythonSnippet.Refusals("def f(n: int) -> int:\n    x = n\n    x = 'a'\n    return 0\n"),
      Has.Some.Contains("error PLY1803: 'f.x' holds values of the types int and str"));

  /// <summary>A local of None and a number is an optional; the call whose None is stored still runs.</summary>
  [Test]
  public void LocalOfNoneAndANumber_IsAnOptional() {
    const string source = "def g(n: int) -> None:\n    assert n != 3\n\ndef f(n: int) -> int:\n    x = n\n    x = g(n)\n    return 0 if x is None else 1\n";
    Assert.That(PythonSnippet.Run(source, "f", 1L), Is.EqualTo("int 0"));
    Assert.That(PythonSnippet.Run(source, "f", 3L), Is.EqualTo("raise AssertionError"));
  }

  [Test]
  public void LocalWithoutAKnownValue_IsRefused()
    => Assert.That(PythonSnippet.Refusals("def g(n: int):\n    return g(n)\n\ndef f(n: int) -> int:\n    x = g(n)\n    return 0\n"),
      Has.Some.Contains("error PLY1801: the type of 'x' in 'f' is not known: no value of a known type reaches it"));

  /// <summary>The module-level statements: docstrings, __future__ imports and the main guard are left out, everything else is refused.</summary>
  [Test]
  public void ModuleLevelStatements_AreLeftOutOrRefused() {
    const string accepted = "\"\"\"A module.\"\"\"\nfrom __future__ import annotations\n\ndef f() -> int:\n    return 1\n\nif __name__ == \"__main__\":\n    print(f())\n";
    Assert.That(PythonSnippet.Module(accepted).Functions.Select(function => function.Name), Is.EqualTo(new[] { "f" }));
    Assert.That(PythonSnippet.Refusals("import math\nfrom os import path\nX = 1\nasync def g():\n    pass\n\ndef f() -> int:\n    return 1\nprint(X)\n"), Is.EqualTo(new[] {
      "snippet.py(2,1): error PLY1833: 'os' is neither part of this import nor in the Python call map",
      "snippet.py(4,1): error PLY1832: the function value 'g' escapes, or 'g' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported (coroutines: #752)",
      "snippet.py(9,1): error PLY1810: statement 'module-level print(X)' is not supported by the Python frontend"
    }));
    Assert.That(PythonSnippet.Refusals("\"\"\"Only a docstring.\"\"\"\n"), Is.EqualTo(new[] {
      "snippet.py(1,1): error PLY1810: statement 'a module without a function' is not supported by the Python frontend"
    }));
  }

  [TestCase("@staticmethod\ndef f(x: int) -> int:\n    return x\n", "'a decorator of f'")]
  [TestCase("def f(*x) -> int:\n    return 1\n", "'*x of f'")]
  [TestCase("def f(**x) -> int:\n    return 1\n", "'**x of f'")]
  [TestCase("def f[T](x: int) -> int:\n    return x\n", "'a type parameter of f'")]
  [TestCase("def f() -> int:\n    return 1\n\ndef f() -> int:\n    return 2\n", "'redefinition of f'")]
  public void FunctionFeatures_AreRefused(string source, string feature)
    => Assert.That(PythonSnippet.Refusals(source), Has.Some.Contains($"error PLY1830: {feature} is not supported for functions by the Python frontend"));

  [Test]
  public void EscapingLambdasAndUnboundNames_AreRefused() {
    Assert.That(PythonSnippet.Refusals("def f() -> int:\n    return (lambda: 1)\n"), Has.Some.Contains("error PLY1832: the function value 'lambda: 1' escapes"));
    Assert.That(PythonSnippet.Refusals("def f() -> int:\n    return math.floor(1.5)\n"), Has.Some.Contains("error PLY1833: 'math' is neither part of this import nor in the Python call map"));
    Assert.That(PythonSnippet.Refusals("def f() -> int:\n    return undefined_name\n"), Has.Some.Contains("error PLY1833: 'undefined_name' is neither part of this import nor in the Python call map"));
  }

}
