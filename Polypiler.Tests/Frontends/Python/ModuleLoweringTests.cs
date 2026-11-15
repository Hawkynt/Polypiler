using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// #450: functions with defaults and keyword arguments, nested functions and closures, lambdas,
/// generators, module constants, tables and globals, imports between the modules of an import and the
/// standard library's call map - the scenarios of <c>fe-py-functions.feature</c>, the rows of the test
/// design the <c>modules</c> fixture does not already hold to CPython, and every refusal with its code.
/// </summary>
[TestFixture]
public sealed class ModuleLoweringTests {

  /// <summary>Imports a main module and the modules it imports from their sources, each from its recorded tree; a module with submodules is a package.</summary>
  private static PythonImportResult Import(string main, params (string Name, string Source)[] modules) {
    var sources = modules.ToDictionary(module => module.Name, module => module.Source);
    return PythonFixture.Import("main.py", main, new PythonImportOptions {
      Modules = name => {
        if (!sources.TryGetValue(name, out var source))
          return null;

        var package = modules.Any(module => module.Name.StartsWith(name + ".", System.StringComparison.Ordinal));
        var path = name.Replace('.', '/') + (package ? "/__init__.py" : ".py");
        return (path, source, PythonFixture.Dump(path, source));
      }
    });
  }

  private static string Run(PythonImportResult result, string function, params object[] arguments) {
    var module = PythonFixture.Module(result);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return PythonOutcome.Interpreted(module, new PythonCall(function, arguments));
  }

  [TestCase(10L, 7L, "int 6")]
  [TestCase(10L, 12L, "int 10")]
  public void NestedFunction_RebindsTheEnclosingVariableThroughNonlocal(long a, long b, string expected)
    => Assert.That(PythonSnippet.Run("def f(a: int, b: int) -> int:\n    best = a\n    def check(v: int) -> bool:\n        nonlocal best\n        if v >= best:\n            return False\n        best = v\n        return True\n"
      + "    if check(b):\n        check(b - 1)\n    return best\n", "f", a, b), Is.EqualTo(expected));

  [TestCase(true, "int -4")]
  [TestCase(false, "int -5")]
  public void Defaults_AreEvaluatedOnceAndKeywordArgumentsInCallSiteOrder(bool named, string expected)
    => Assert.That(PythonSnippet.Run("BASE = 5\ndef g(a: int, b: int = BASE * 2) -> int:\n    return a - b\nBASE = 100\ndef f(x: int, named: bool) -> int:\n    return g(b=x, a=1) if named else g(x)\n", "f", 5L, named),
      Is.EqualTo(expected));

  [Test]
  public void ModuleLevelTable_IsBuiltOnceByTheModuleInitialiser() {
    var module = PythonSnippet.Module("TABLE = [i * i for i in range(4)]\ndef f(i: int) -> int:\n    return TABLE[i] + len(TABLE)\n");
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("f", [3L])), Is.EqualTo("int 13"));
    Assert.That(module.Globals.Select(global => (global.Name, global.Type)), Is.EqualTo(new[] { ("TABLE", IrType.Array(IrType.I64)) }));
    var instance = IrInterpreter.Instantiate(module);
    Assert.That(instance.Call("f", [3L]), Is.EqualTo(13L));
    Assert.That(instance.Call("f", [0L]), Is.EqualTo(4L));
  }

  [TestCase("a: float", "math.floor(a)", -2.5, "int -3")]
  [TestCase("a: float", "math.trunc(a)", -2.5, "int -2")]
  [TestCase("a: float", "math.ceil(a)", -2.5, "int -2")]
  [TestCase("a: float", "math.sqrt(a)", -1.0, "raise ValueError")]
  [TestCase("a: int", "math.floor(a) + math.isqrt(a)", 10L, "int 13")]
  [TestCase("a: int", "math.isqrt(a)", -1L, "raise ValueError")]
  [TestCase("a: int", "math.gcd(a, -4)", -6L, "int 2")]
  [TestCase("a: float", "math.tau / math.pi + math.e - math.e", 0.0, "float 1.9999999999999996")]
  [TestCase("a: float", "math.inf if math.isinf(a + math.inf) else 0.0", 0.0, "float inf")]
  [TestCase("a: float", "math.copysign(1.0, math.nan)", 0.0, "float 1.0")]
  [TestCase("a: float", "math.isnan(a)", 0.0, "bool False")]
  [TestCase("a: float", "math.isfinite(a / 1e-308 * 1e308)", 2.0, "bool False")]
  public void MappedLibraryCalls_KeepTheirResultTypesAndEdgeBehaviour(string parameters, string expression, object argument, string expected)
    => Assert.That(PythonSnippet.Run($"import math\n\ndef f({parameters}):\n    return {expression}\n", "f", argument), Is.EqualTo(expected));

  [Test]
  public void MaxOfSeveralArguments_ComparesExactly()
    => Assert.That(PythonSnippet.Run("def f(a: int, b: int) -> int:\n    return max(a, b) - min(a, b)\n", "f", 3L, 9L), Is.EqualTo("int 6"));

  [Test]
  public void ShadowedBuiltIn_IsTheUsersFunction()
    => Assert.That(PythonSnippet.Run("def max(a: int, b: int) -> int:\n    return a if a < b else b\n\ndef f(a: int, b: int) -> int:\n    return max(a, b)\n", "f", 3L, 9L), Is.EqualTo("int 3"));

  [Test]
  public void ImportedNamesFromMath_AreBoundByTheirImport()
    => Assert.That(PythonSnippet.Run("from math import sqrt as root, pi\n\ndef f(a: float) -> float:\n    return root(a) + pi - pi\n", "f", 9.0), Is.EqualTo("float 3.0"));

  /// <summary>Test design 3 and 4: every parameter kind, a call omitting some, all or none of the defaults, and twenty parameters.</summary>
  [TestCase("g(1)", "int 1203")]
  [TestCase("g(1, 5)", "int 1503")]
  [TestCase("g(1, c=9)", "int 1209")]
  [TestCase("g(1, b=0, c=4)", "int 1004")]
  [TestCase("h(*[1])", null)]
  public void ParameterKinds_BindAsPythonBinds(string call, string? expected) {
    var source = $"def g(a: int, /, b: int = 2, *, c: int = 3) -> int:\n    return 1000 * a + 100 * b + c\n\ndef h(a: int) -> int:\n    return a\n\ndef f() -> int:\n    return {call}\n";
    if (expected is null)
      Assert.That(PythonSnippet.Refusals(source), Has.Some.Contains("error PLY1830"));
    else
      Assert.That(PythonSnippet.Run(source, "f"), Is.EqualTo(expected));
  }

  [Test]
  public void TwentyParameters_AreBoundInOrder() {
    var names = Enumerable.Range(0, 20).Select(index => "p" + index).ToList();
    var source = $"def g({string.Join(", ", names.Select(name => name + ": int"))}) -> int:\n    return {string.Join(" * 2 + ", names)}\n\ndef f() -> int:\n    return g({string.Join(", ", Enumerable.Range(0, 20).Select(index => (index % 2).ToString(System.Globalization.CultureInfo.InvariantCulture)))})\n";
    Assert.That(PythonSnippet.Run(source, "f"), Is.EqualTo("int 19"));
  }

  [Test]
  public void ParameterWithoutAHint_TakesTheTypeOfItsDefaultAndOfKeywordArguments() {
    var module = PythonSnippet.Module("def g(a, b=1.5):\n    return a + b\n\ndef f() -> float:\n    return g(a=2)\n");
    Assert.That(module.Functions.Single(function => function.Name == "g").Parameters.Select(parameter => parameter.Type), Is.EqualTo(new[] { IrType.I64, IrType.F64 }));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("f", [])), Is.EqualTo("float 3.5"));
  }

  /// <summary>Test design 5: two closures share one variable and see each other's writes.</summary>
  [Test]
  public void TwoClosures_ShareOneVariable()
    => Assert.That(PythonSnippet.Run("def f(n: int) -> int:\n    count = 0\n    def up() -> None:\n        nonlocal count\n        count += n\n    def read() -> int:\n        return count\n    up()\n    up()\n    return read()\n", "f", 3L),
      Is.EqualTo("int 6"));

  [Test]
  public void GlobalCounter_IsAVariableOfTheModule() {
    var module = PythonSnippet.Module("TOTAL = 0\n\ndef add(n: int) -> int:\n    global TOTAL\n    TOTAL += n\n    return TOTAL\n");
    var instance = IrInterpreter.Instantiate(module);
    Assert.That(instance.Call("add", [2L]), Is.EqualTo(2L));
    Assert.That(instance.Call("add", [3L]), Is.EqualTo(5L));
  }

  [Test]
  public void FloatGlobal_TakesTheWiderTypeOfWhatFunctionsAssign() {
    var module = PythonSnippet.Module("LAST = 0\n\ndef set_last(x: float) -> float:\n    global LAST\n    LAST = x\n    return LAST\n");
    Assert.That(module.Globals.Single().Type, Is.EqualTo(IrType.F64));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("set_last", [2.5])), Is.EqualTo("float 2.5"));
  }

  [Test]
  public void ModuleConstants_AreFoldedInSourceOrder() {
    const string source = "A = 2\nB = A * 3\nA += 1\nC, D = A, 'x'\nWEIGHTS = [0.5, 1.5]\nRAW = b'\\x01\\x02'\nPAIR = (1, 2.5)\n\n"
      + "def f(i: int) -> float:\n    return A + B + len(D) + WEIGHTS[i] + RAW[1] + PAIR[1] + C\n";
    Assert.That(PythonSnippet.Run(source, "f", 0L), Is.EqualTo("float 18.0"));
    Assert.That(PythonSnippet.Refusals("FLAGS = [True]\n\ndef f() -> int:\n    return 0\n"), Has.Some.Contains("a module-level list holds ints, floats or members of an enum"));
  }

  [Test]
  public void GeneratorFunctions_YieldLazilyAndCloseEarly() {
    const string source = "from typing import Iterator\n\ndef count(n: int) -> Iterator[int]:\n    i = 0\n    while i < n:\n        yield i\n        i += 1\n    return\n\n"
      + "def f(n: int) -> int:\n    total = 0\n    for v in count(n):\n        if v == 3:\n            break\n        total += v\n    return total + sum(count(n)) + len(list(count(2)))\n";
    Assert.That(PythonSnippet.Run(source, "f", 5L), Is.EqualTo("int 15"));
  }

  [Test]
  public void GeneratorWithoutAHint_YieldsTheTypeOfItsValues()
    => Assert.That(PythonSnippet.Run("def g(n: int):\n    yield n\n    yield n * 2.5\n\ndef f(n: int) -> float:\n    return sum(1 for _ in g(n)) + max(g(n))\n", "f", 2L), Is.EqualTo("float 7.0"));

  /// <summary>Test design 8: the import forms between the modules of an import.</summary>
  [Test]
  public void ImportForms_ResolveTheModulesOfTheImport() {
    var result = Import("import helper\nimport helper as h\nfrom helper import twice as double, OFFSET\nfrom pkg import sub\nfrom pkg.sub import third\n\n"
      + "def f(n: int) -> int:\n    return helper.twice(n) + h.twice(1) + double(n) + OFFSET + sub.third(9) + third(3) + helper.Box(n).v\n",
      ("helper", "OFFSET = 100\n\ndef twice(x: int) -> int:\n    return x * 2\n\nclass Box:\n    def __init__(self, v: int):\n        self.v = v\n"),
      ("pkg", "from . import sub\n"),
      ("pkg.sub", "from .tools import scale\n\ndef third(x: int) -> int:\n    return scale(x) // 3\n"),
      ("pkg.tools", "def scale(x: int) -> int:\n    return x\n"));
    Assert.That(Run(result, "f", 5L), Is.EqualTo("int 131"));
    var module = result.Module!;
    Assert.That(module.Functions.Where(function => function.IsPublic).Select(function => function.Name), Is.EqualTo(new[] { "f" }));
    Assert.That(module.Functions.Select(function => function.Name), Does.Contain("helper_twice").And.Contain("pkg_sub_third"));
  }

  [Test]
  public void AllOfAModule_IsItsExportSet() {
    var module = PythonFixture.Module(PythonSnippet.Import("__all__ = ['g']\n\ndef f() -> int:\n    return 1\n\ndef g() -> int:\n    return f()\n"));
    Assert.That(module.Functions.Where(function => function.IsPublic).Select(function => function.Name), Is.EqualTo(new[] { "g" }));
  }

  [TestCase("import missing\n", "error PLY1833: 'missing' is neither part of this import nor in the Python call map")]
  [TestCase("from helper import missing\n", "error PLY1833: 'helper.missing' is neither part of this import nor in the Python call map: 'helper' has no function or class 'missing'")]
  [TestCase("from helper import *\n", "error PLY1833: 'from helper import *' is neither part of this import nor in the Python call map: a star import")]
  [TestCase("import pkg.sub\n", "error PLY1833: 'pkg.sub' is neither part of this import nor in the Python call map: import it with 'import pkg.sub as …'")]
  [TestCase("import collections.abc\n", "error PLY1833: 'collections.abc' is neither part of this import nor in the Python call map: import it with 'from collections.abc import …'")]
  [TestCase("from math import sin\n", "error PLY1833: 'math.sin' is neither part of this import nor in the Python call map")]
  [TestCase("import numpy\n", "error PLY1833: 'numpy' is neither part of this import nor in the Python call map")]
  [TestCase("import cycle\n", "error PLY1833: 'main' is neither part of this import nor in the Python call map: the modules import each other in a cycle")]
  public void UnresolvedImport_IsRefusedWithPLY1833(string import, string diagnostic) {
    var result = Import(import + "\ndef f() -> int:\n    return 0\n",
      ("helper", "def twice(x: int) -> int:\n    return x * 2\n"), ("pkg", "X = 1\n"), ("pkg.sub", "Y = 1\n"), ("cycle", "import main\n"), ("main", "def g() -> int:\n    return 0\n"));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(found => found.ToString()), Has.Some.Contains(diagnostic));
  }

  [Test]
  public void ModuleOfASyntaxError_IsRefusedWithItsMessage() {
    var result = Import("import broken\n\ndef f() -> int:\n    return 0\n", ("broken", "def f(:\n"));
    Assert.That(result.Diagnostics.Select(found => found.ToString()), Has.Some.Contains("broken.py").And.Some.Contains("error PLY1800"));
  }

  [Test]
  public void ModulesBesideASourceFile_AreFoundOnDisk() {
    var directory = System.IO.Directory.CreateTempSubdirectory("polypiler-python-modules-").FullName;
    try {
      System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "helper.py"), "def twice(x: int) -> int:\n    return x * 2\n");
      System.IO.Directory.CreateDirectory(System.IO.Path.Combine(directory, "pkg"));
      System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "pkg", "__init__.py"), "def one() -> int:\n    return 1\n");
      var path = System.IO.Path.Combine(directory, "main.py");
      const string source = "from helper import twice\nfrom pkg import one\n\ndef f(n: int) -> int:\n    return twice(n) + one()\n";
      var result = PythonFrontend.ImportDump(path, source, PythonFixture.Dump("main.py", source), new PythonImportOptions {
        Modules = null,
        Interpreter = null
      });
      // The modules on disk are parsed by the interpreter; without one the import names why it has none.
      if (result.Module is not null)
        Assert.That(PythonOutcome.Interpreted(result.Module, new PythonCall("f", [4L])), Is.EqualTo("int 9"));
      else
        Assert.That(result.Diagnostics.Select(found => found.Code), Has.All.EqualTo(PythonFrontend.SyntaxError));
    } finally {
      System.IO.Directory.Delete(directory, true);
    }
  }

  [TestCase("def f(x: int, acc: list[int] = []) -> int:\n    return x\n", "error PLY1834: the default of parameter 'acc' is a mutable object shared across calls")]
  [TestCase("def f(x: int, acc: bytearray = bytearray(2)) -> int:\n    return x\n", "error PLY1834: the default of parameter 'acc' is a mutable object shared across calls")]
  [TestCase("def f(*args: int) -> int:\n    return 0\n", "error PLY1830: '*args of f' is not supported for functions")]
  [TestCase("def f(**kw: int) -> int:\n    return 0\n", "error PLY1830: '**kw of f' is not supported for functions")]
  [TestCase("def f(x: int):\n    return lambda y: x + y\n", "error PLY1832: the function value 'lambda y: x + y' escapes")]
  [TestCase("import numpy\ndef f() -> int:\n    return 1\n", "error PLY1833: 'numpy' is neither part of this import nor in the Python call map")]
  [TestCase("def g() -> int:\n    return 1\n\ndef f() -> int:\n    h = g\n    return h()\n", "error PLY1832: the function value 'g' escapes")]
  [TestCase("def g(k: int) -> int:\n    return k\n\ndef f() -> int:\n    return g(g)\n", "error PLY1832: the function value 'g' escapes")]
  [TestCase("def f() -> int:\n    def g() -> int:\n        return 1\n    return g\n", "error PLY1832: the function value 'g' escapes")]
  [TestCase("def f() -> int:\n    for i in range(3):\n        def g() -> int:\n            return i\n    return 0\n", "error PLY1830: 'def g inside a loop or conditional'")]
  [TestCase("def f() -> int:\n    def g() -> int:\n        yield 1\n    return 0\n", "error PLY1832: the function value 'g' escapes, or 'g' is a generator or coroutine")]
  [TestCase("def f() -> int:\n    def g() -> int:\n        return 1\n    def g() -> int:\n        return 2\n    return g()\n", "error PLY1830: 'redefinition of g'")]
  [TestCase("def f() -> int:\n    @staticmethod\n    def g() -> int:\n        return 1\n    return g()\n", "error PLY1830: 'a decorator of g'")]
  [TestCase("async def f() -> int:\n    return 0\n", "error PLY1832: the function value 'f' escapes, or 'f' is a generator or coroutine")]
  [TestCase("def f() -> int:\n    async def g() -> int:\n        return 0\n    return 0\n", "error PLY1832: the function value 'g' escapes")]
  [TestCase("def f() -> int:\n    x = (yield 1)\n    return 0\n", "error PLY1832")]
  [TestCase("def g(a: int, b: int) -> int:\n    return a\n\ndef f() -> int:\n    return g(1)\n", "error PLY1830: 'no argument for the parameter b of g in g(1), which raises TypeError,'")]
  [TestCase("def g(a: int) -> int:\n    return a\n\ndef f() -> int:\n    return g(1, a=2)\n", "error PLY1830: 'a second value for a in g(1, a=2), which raises TypeError,'")]
  [TestCase("def g(a: int, /) -> int:\n    return a\n\ndef f() -> int:\n    return g(a=1)\n", "error PLY1830: 'the keyword argument a, which g has no parameter for")]
  [TestCase("def g(*, a: int) -> int:\n    return a\n\ndef f() -> int:\n    return g(1)\n", "error PLY1830: 'more positional arguments than g takes")]
  [TestCase("def f() -> int:\n    return g()\n    def g() -> int:\n        return 1\n", "error PLY1811: 'g' may be unbound here")]
  [TestCase("def f() -> int:\n    def g() -> int:\n        return x\n    x = 1\n    return g()\n", "error PLY1811: 'x' may be unbound where 'g' is defined")]
  [TestCase("def f() -> int:\n    print(1)\n    return 0\n", "error PLY1833: 'print' is neither part of this import nor in the Python call map")]
  [TestCase("import math\ndef f() -> float:\n    return math.sin(1.0)\n", "error PLY1833: 'math.sin' is neither part of this import nor in the Python call map")]
  [TestCase("import math\ndef f() -> float:\n    return math.tan\n", "error PLY1833: 'math.tan' is neither part of this import nor in the Python call map")]
  [TestCase("import math\ndef f():\n    return math.pi()\n", "error PLY1801: the result type of 'f' is not known")]
  [TestCase("from math import pi\ndef f():\n    return pi()\n", "error PLY1801: the result type of 'f' is not known")]
  [TestCase("import math\ndef f() -> float:\n    return math.floor\n", "error PLY1832: the function value 'math.floor' escapes")]
  [TestCase("import math\ndef f() -> float:\n    return math.sqrt(x=1.0)\n", "error PLY1833: 'math.sqrt' is neither part of this import nor in the Python call map")]
  [TestCase("import math\ndef f() -> float:\n    return math.sqrt('a')\n", "error PLY1802: 'math.sqrt of these arguments, which raises TypeError,'")]
  [TestCase("import math\ndef f() -> int:\n    return math.gcd(1.5, 2)\n", "error PLY1802: 'math.gcd of a float, which raises TypeError,'")]
  [TestCase("from typing import Optional\ndef f() -> int:\n    return Optional(1)\n", "error PLY1833: 'typing.Optional' is neither part of this import nor in the Python call map")]
  [TestCase("def f() -> int:\n    return undefined(1)\n", "error PLY1833: 'undefined' is neither part of this import nor in the Python call map")]
  [TestCase("def f() -> int:\n    return (f)(1)\n", "error PLY1830: 'more positional arguments than f takes")]
  [TestCase("def f() -> int:\n    return [f][0]()\n", "error PLY1833: '[f][0]' is neither part of this import nor in the Python call map")]
  [TestCase("def f() -> int:\n    return map(len, [[1]])\n", "error PLY1832: the function value 'map' escapes")]
  [TestCase("def f(x: int) -> int:\n    return list(map(lambda a, b: a, [x]))[0]\n", "error PLY1832: the function value 'lambda a, b: a' escapes")]
  [TestCase("def f(x: int) -> int:\n    return sorted([x], key=lambda a=1: a)[0]\n", "error PLY1832")]
  [TestCase("def f(x: int) -> int:\n    return divmod(x)\n", "error PLY1802: 'divmod(x)' is not supported")]
  [TestCase("def f(x: int) -> int:\n    return getattr(x, 'y')\n", "error PLY1844: attribute 'getattr' of class 'x' is created dynamically")]
  [TestCase("def f(x: int) -> int:\n    return super().f()\n", "error PLY1840: 'super()'")]
  public void FunctionFeatureOutsideTheSubset_IsRefusedWithItsCode(string source, string diagnostic)
    => Assert.That(PythonSnippet.Refusals(source), Has.Some.Contains(diagnostic));

  [TestCase("X = f()\n\ndef f() -> int:\n    return 1\n", "error PLY1810: statement 'module-level call of f, which runs code of the module when it is imported,' is not supported")]
  [TestCase("class C:\n    pass\n\nX = C()\n\ndef f() -> int:\n    return 1\n", "error PLY1810: statement 'module-level call of C, which runs code of the module when it is imported,' is not supported")]
  [TestCase("X = Y\nY = 1\n\ndef f() -> int:\n    return X\n", "error PLY1833: 'Y' is neither part of this import nor in the Python call map: it is used before the module assigns it, which raises NameError")]
  [TestCase("X = 1 // 0\n\ndef f() -> int:\n    return X\n", "error PLY1810: statement 'module-level 1 // 0, which raises ZeroDivisionError when the module is imported,' is not supported")]
  [TestCase("X = 'a'\n\ndef f() -> int:\n    global X\n    X = 'b'\n    return 0\n", "error PLY1810: statement 'module variable X of the type str' is not supported")]
  [TestCase("X, Y = [1, 2]\n\ndef f() -> int:\n    return X\n", "error PLY1810: statement 'module-level unpacking of [1, 2]' is not supported")]
  [TestCase("X = ['a']\n\ndef f() -> int:\n    return len(X)\n", "error PLY1810: statement 'module-level ['a']' of the type list[str] is not supported by the Python frontend: a module-level list holds ints, floats or members of an enum")]
  [TestCase("X = None\n\ndef f() -> int:\n    return 0\n", "error PLY1802: 'the value None of None' is not supported")]
  [TestCase("for i in range(3):\n    pass\n\ndef f() -> int:\n    return 0\n", "error PLY1810: statement 'module-level for i in range(3):…' is not supported")]
  [TestCase("X = [1]\n\ndef f() -> int:\n    X.append(2)\n    return 0\n", "error PLY1820: sequence operation 'append' of a value of the type tuple[int, ...] is not supported")]
  [TestCase("X: int\n\ndef f() -> int:\n    return 0\n", "error PLY1810: statement 'module-level X: int' is not supported")]
  public void ModuleStatementOutsideTheSubset_IsRefused(string source, string diagnostic)
    => Assert.That(PythonSnippet.Refusals(source), Has.Some.Contains(diagnostic));

  /// <summary>Module-level values the interpreter folds when the module is imported: of every kind of constant, a table assigned twice and one copied.</summary>
  [TestCase("S = 'ab' * 2\n\ndef f() -> int:\n    return len(S)\n", "int 4")]
  [TestCase("B = bytes([1, 2]) + b'c'\n\ndef f() -> int:\n    return B[2]\n", "int 99")]
  [TestCase("P = (1, 2 * 3)\n\ndef f() -> int:\n    return P[1]\n", "int 6")]
  [TestCase("Q = 1 < 2\n\ndef f() -> bool:\n    return Q\n", "bool True")]
  [TestCase("H = 7.0 / 2\n\ndef f() -> float:\n    return H\n", "float 3.5")]
  [TestCase("from enum import Enum\n\nclass C(Enum):\n    A = 1\n    B = 2\n\nE = max([C.A.value, C.B.value])\n\ndef f() -> int:\n    return E\n", "int 2")]
  [TestCase("T = [1, 2]\nT = [3, 4]\n\ndef f() -> int:\n    return T[0]\n", "int 3")]
  [TestCase("T = [1, 2]\nU = T\n\ndef f() -> int:\n    return U[1]\n", "int 2")]
  public void ModuleLevelValues_AreFoldedWhenTheModuleIsImported(string source, string expected)
    => Assert.That(PythonSnippet.Run(source, "f"), Is.EqualTo(expected));

  [TestCase("def f() -> int:\n    def g(a: int = [1][0], b: list[int] = sorted([1])) -> int:\n        return a\n    return g()\n", "the default of parameter 'b' is a mutable object shared across calls")]
  [TestCase("T = [1, 2]\n\ndef f(a: list[int] = T) -> int:\n    return 0\n", "the default of parameter 'a' is a mutable object shared across calls")]
  [TestCase("G = (i for i in range(3))\n\ndef f() -> int:\n    return 0\n", "error PLY1810: statement 'module-level (i for i in range(3))' of the type Iterator[int] is not supported")]
  public void ModuleLevelValuesOutsideTheSubset_AreRefused(string source, string diagnostic)
    => Assert.That(PythonSnippet.Refusals(source), Has.Some.Contains(diagnostic));

  /// <summary>A nested function reads its parameters' defaults where its def ran; a lambda's parameter takes the type of the elements it is applied to.</summary>
  [Test]
  public void NestedDefaultsAndLambdaParameters_AreTyped()
    => Assert.That(PythonSnippet.Run("def f(n: int) -> int:\n    def scale(x: int, k: int = 3) -> int:\n        return x * k\n    return scale(n) + sorted([3, 1, 2], key=lambda v: -v)[0]\n", "f", 2L), Is.EqualTo("int 9"));

  [Test]
  public void Walrus_BindsInTheFunction()
    => Assert.That(PythonSnippet.Run("def f(n: int) -> int:\n    if (m := n * 2) > 3:\n        return m\n    return [y := 5, y][1] + m\n", "f", 1L), Is.EqualTo("int 7"));

}
