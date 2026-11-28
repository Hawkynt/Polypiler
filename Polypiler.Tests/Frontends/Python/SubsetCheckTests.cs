using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>The subset check of the Python frontend: every construct outside the subset is refused with the diagnostic of its family before any type is inferred, and the constructs beside the refused ones import. Every value is CPython's.</summary>
[TestFixture]
public sealed class SubsetCheckTests {

  /// <summary>Imports a snippet beside a module <c>helper</c> it imports.</summary>
  private static PythonImportResult WithHelper(string source, string helper)
    => PythonSnippet.Import(source, new PythonImportOptions { Modules = name => name == "helper" ? ("helper.py", helper) : null });

  /// <summary>What the interpreter does for a call of a function of a snippet imported beside <c>helper</c>.</summary>
  private static string RunWithHelper(string source, string helper, string function, object[] arguments) {
    var module = PythonFixture.Module(WithHelper(source, helper));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return PythonOutcome.Interpreted(module, new PythonCall(function, arguments));
  }

  /// <summary>An exception class imported from another module is no class the subset check knows, so raising or catching it is refused; so is a plain class imported. Each is refused with exactly these diagnostics.</summary>
  [TestCase("from helper import Oops\n\ndef f(n: int) -> int:\n    try:\n        if n > 0:\n            raise Oops(\"x\")\n        return 0\n    except Oops:\n        return 1\n", "class Oops(Exception):\n    pass\n", "snippet.py(8,12): error PLY1843: exception construct 'except Oops, which names no exception class' is not supported by the Python frontend")]
  [TestCase("from helper import Plain\n\ndef f(n: int) -> int:\n    try:\n        return 0\n    except Plain:\n        return 1\n", "class Plain:\n    def __init__(self) -> None:\n        self.v = 1\n", "snippet.py(6,12): error PLY1843: exception construct 'except Plain, which names no exception class' is not supported by the Python frontend")]
  [TestCase("from helper import Plain\n\ndef f(n: int) -> int:\n    raise Plain()\n", "class Plain:\n    def __init__(self) -> None:\n        self.v = 1\n", "snippet.py(4,11): error PLY1843: exception construct 'raise of a value of the type Plain, which raises TypeError,' is not supported by the Python frontend")]
  public void Imported_AreRefused_WithHelper(string source, string helper, string expected) {
    var result = WithHelper(source, helper);
    Assert.That(result.Module, Is.Null);
    Assert.That(string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString())), Is.EqualTo(expected));
  }

  /// <summary>A loop variable that is no name - a parenthesized tuple holds names, an attribute does not - is refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    xs = [1]\n    for xs[0] in [1]:\n        pass\n    return 0\n", "snippet.py(3,9): error PLY1810: statement 'a loop variable other than names' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    t = [x for x[0] in [[1]]]\n    return 0\n", "snippet.py(2,10): error PLY1833: 'x' is neither part of this import nor in the Python call map\nsnippet.py(2,16): error PLY1810: statement 'a loop variable other than names' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    *a = [1]\n    return 0\n", "snippet.py(2,5): error PLY1800: SyntaxError: starred assignment target must be in a list or tuple")]
  [TestCase("def f(n: int) -> int:\n    for *a in [[1]]:\n        pass\n    return 0\n", "snippet.py(2,9): error PLY1800: SyntaxError: starred assignment target must be in a list or tuple")]
  [TestCase("def f(n: int) -> int:\n    return len([1 for *a in [[1]]])\n", "snippet.py(2,23): error PLY1800: SyntaxError: starred assignment target must be in a list or tuple")]
  [TestCase("def f(n: int) -> int:\n    for (a, b) in [(1, 2)]:\n        pass\n    for x.y in [1]:\n        pass\n    return 0\n", "snippet.py(4,9): error PLY1810: statement 'a loop variable other than names' is not supported by the Python frontend")]
  public void Targets_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A module-level call of a function or of a plain class, which would run code at import, is refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 1\n\nX = E(1)\nY = ValueError(\"x\")\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(7,5): error PLY1810: statement 'module-level ValueError(\"x\")' of the type ValueError is not supported by the Python frontend")]
  [TestCase("Y = ValueError(\"x\")\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(1,5): error PLY1810: statement 'module-level ValueError(\"x\")' of the type ValueError is not supported by the Python frontend")]
  [TestCase("def g(x: int) -> int:\n    return x\n\nX = g(1)\n", "snippet.py(4,5): error PLY1810: statement 'module-level call of g, which runs code of the module when it is imported,' is not supported by the Python frontend")]
  [TestCase("class D:\n    def __init__(self) -> None:\n        self.v = 1\n\nX = D()\n", "snippet.py(5,5): error PLY1810: statement 'module-level call of D, which runs code of the module when it is imported,' is not supported by the Python frontend")]
  public void ModuleCalls_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A module-level call of a function or of a plain class, which would run code at import, is refused. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 1\n\nX = E(1)\n\ndef f(n: int) -> int:\n    return X.value\n", "f", "int 1", 1L)]
  public void ModuleCalls_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>The expressions outside the subset are refused by their family: complex numbers, Ellipsis, lambdas outside the built-ins that take them, async comprehensions, f-strings, '@', yield and await as values; a set display beside them. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    x = 1j\n    return 0\n", "snippet.py(2,9): error PLY1802: '1j' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    x = ...\n    return 0\n", "snippet.py(2,9): error PLY1802: '...' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    x = lambda: 1\n    return 0\n", "snippet.py(2,9): error PLY1832: the function value 'lambda: 1' escapes, or 'lambda: 1' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported (escaping function values: #753)")]
  [TestCase("def f(n: int) -> int:\n    return [x async for x in n]\n", "snippet.py(2,12): error PLY1800: SyntaxError: asynchronous comprehension outside of an asynchronous function")]
  [TestCase("def f(n: int) -> str:\n    return f\"{n}\"\n", "snippet.py(2,12): error PLY1822: string operation 'f-string' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return n @ n\n", "snippet.py(2,12): error PLY1802: '@' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    n @= n\n    return n\n", "snippet.py(2,5): error PLY1802: '@' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    x = (yield n)\n    return 0\n", "snippet.py(2,10): error PLY1832: the function value 'f' escapes, or 'f' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported (a yield used as a value: #760)")]
  [TestCase("def f(n: int) -> int:\n    x = await n\n    return 0\n", "snippet.py(2,9): error PLY1800: SyntaxError: 'await' outside async function")]
  public void Expressions_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>The expressions outside the subset are refused by their family: complex numbers, Ellipsis, lambdas outside the built-ins that take them, async comprehensions, f-strings, '@', yield and await as values; a set display beside them. Each imports into IR that verifies.</summary>
  [TestCase("def f(n: int) -> int:\n    return {n}\n")]
  public void Expressions_Import(string source)
    => Assert.That(PythonSnippet.Module(source).Functions, Is.Not.Empty);

  /// <summary>The statements outside the subset are refused by their family, the ones beside them in the subset import: with, except*, imports and classes in a function, type, async for, del, global and nonlocal of unknown names, assert messages, leaving a finally, bare and chained raise, conditional and repeated nested functions, unpacking that does not match, slice and attribute stores, sequence and alias patterns. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    with n:\n        pass\n    return 0\n", "snippet.py(2,5): error PLY1843: exception construct 'with' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    try:\n        pass\n    except* ValueError:\n        pass\n    return 0\n", "snippet.py(2,5): error PLY1843: exception construct 'except*' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    import math\n    return 0\n", "snippet.py(2,5): error PLY1833: 'import math' is neither part of this import nor in the Python call map: an import inside a function")]
  [TestCase("def f(n: int) -> int:\n    class C:\n        pass\n    return 0\n", "snippet.py(2,5): error PLY1840: 'nested class C' is outside the first Python subset (dataclasses, simple final classes, enums)")]
  [TestCase("def f(n: int) -> int:\n    type T = int\n    return 0\n", "snippet.py(2,5): error PLY1810: statement 'type' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    async for x in n:\n        pass\n    return 0\n", "snippet.py(2,5): error PLY1800: SyntaxError: 'async for' outside async function")]
  [TestCase("def f(n: int) -> int:\n    del n.x\n    return 0\n", "snippet.py(2,9): error PLY1844: attribute 'x' of class 'n' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported (del)")]
  [TestCase("def f(n: int) -> int:\n    xs = [1]\n    del xs[0]\n    return 0\n", "snippet.py(3,9): error PLY1820: sequence operation 'del xs[0]' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    xs = [1]\n    del xs[0:1]\n    return 0\n", "snippet.py(3,9): error PLY1821: slice form 'del xs[0:1]' is not supported; slices are imported as values only")]
  [TestCase("def f(n: int) -> int:\n    del n\n    return 0\n", "snippet.py(2,9): error PLY1810: statement 'del' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    global Q\n    return 0\n", "snippet.py(2,5): error PLY1833: 'global Q' is neither part of this import nor in the Python call map: 'Q' is assigned at no module level")]
  [TestCase("def f(n: int) -> int:\n    nonlocal n\n    return 0\n", "snippet.py(2,5): error PLY1800: SyntaxError: name 'n' is parameter and nonlocal")]
  [TestCase("def f(n: int) -> int:\n    def g() -> int:\n        nonlocal q\n        return 0\n    return g()\n", "snippet.py(3,9): error PLY1800: SyntaxError: no binding for nonlocal 'q' found")]
  [TestCase("def f(n: int) -> int:\n    assert n, n\n    return 0\n", "snippet.py(2,15): error PLY1810: statement 'assert' with a message other than a string literal is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    try:\n        pass\n    finally:\n        return 1\n", "snippet.py(5,9): error PLY1843: exception construct 'return in finally' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    for i in range(n):\n        try:\n            pass\n        finally:\n            break\n    return 0\n", "snippet.py(6,13): error PLY1843: exception construct 'break in finally' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    for i in range(n):\n        try:\n            pass\n        finally:\n            continue\n    return 0\n", "snippet.py(6,13): error PLY1843: exception construct 'continue in finally' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    raise\n", "snippet.py(2,5): error PLY1843: exception construct 'raise outside an except clause' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    raise ValueError(\"x\") from None\n", "snippet.py(2,5): error PLY1843: exception construct 'raise … from' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    raise ValueError(\"x\", 2)\n", "snippet.py(2,11): error PLY1843: exception construct 'an exception made with other arguments than a message: ValueError(\"x\", 2)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    raise ValueError(msg=\"x\")\n", "snippet.py(2,11): error PLY1843: exception construct 'an exception made with other arguments than a message: ValueError(msg=\"x\")' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    if n:\n        def g() -> int:\n            return 1\n    return 0\n", "snippet.py(3,9): error PLY1830: 'def g inside a loop or conditional' is not supported for functions by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    def g() -> int:\n        return 1\n    def g() -> int:\n        return 2\n    return g()\n", "snippet.py(4,5): error PLY1830: 'redefinition of g' is not supported for functions by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    async def g() -> int:\n        return 1\n    return 0\n", "snippet.py(2,5): error PLY1832: the function value 'g' escapes, or 'g' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported (coroutines: #752)")]
  [TestCase("def f(n: int) -> int:\n    a, b = 1, 2, 3\n    return 0\n", "snippet.py(2,5): error PLY1810: statement 'tuple assignment whose right side is no tuple of as many values' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    a, *b = 1, 2, 3\n    return 0\n", "snippet.py(2,5): error PLY1810: statement 'tuple assignment whose right side is no tuple of as many values' is not supported by the Python frontend\nsnippet.py(2,8): error PLY1810: statement 'starred assignment target' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    a, (b, c) = 1, (2, 3)\n    return 0\n", "snippet.py(2,8): error PLY1810: statement 'nested tuple assignment' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    [1][0:1] = [2]\n    return 0\n", "snippet.py(2,5): error PLY1821: slice form '[1][0:1] = …' is not supported; slices are imported as values only")]
  [TestCase("def f(n: int) -> int:\n    f(n).x = 2\n    return 0\n", "snippet.py(2,5): error PLY1802: 'assignment to the attribute x of a value of the type int, which raises AttributeError,' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    match n:\n        case [a, b]:\n            return 1\n    return 0\n", "snippet.py(3,14): error PLY1812: match pattern '[a, b]' is not supported; supported are literals, members of an enum, '|', captures, '_' and guards")]
  [TestCase("def f(n: int) -> int:\n    match n:\n        case (1 | 2) as k:\n            return 1\n    return 0\n", "snippet.py(3,14): error PLY1812: match pattern '(1 | 2) as k' is not supported; supported are literals, members of an enum, '|', captures, '_' and guards")]
  [TestCase("def f(n: int) -> int:\n    match n:\n        case 1 | k:\n            return 1\n    return 0\n", "snippet.py(3,14): error PLY1800: SyntaxError: alternative patterns bind different names")]
  public void Statements_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>The statements outside the subset are refused by their family, the ones beside them in the subset import: with, except*, imports and classes in a function, type, async for, del, global and nonlocal of unknown names, assert messages, leaving a finally, bare and chained raise, conditional and repeated nested functions, unpacking that does not match, slice and attribute stores, sequence and alias patterns. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    assert n, \"no\"\n    return 0\n", "f", "int 0", 1L)]
  [TestCase("def f(n: int) -> int:\n    assert n, \"no\"\n    return 0\n", "f", "raise AssertionError", 0L)]
  public void Statements_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A call is refused where its arguments are unpacked, its callee is no name or a variable, its built-in takes other arguments, or its method or library name is not in the call map; a method of a class of the module shadows a refused method of the built-in types. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def g(x: int) -> int:\n    return x\n\ndef f(n: int) -> int:\n    return g(*[n])\n", "snippet.py(5,12): error PLY1830: '*args and **kwargs in g(*[n])' is not supported for functions by the Python frontend")]
  [TestCase("def g(x: int) -> int:\n    return x\n\ndef f(n: int) -> int:\n    return g(**{})\n", "snippet.py(5,12): error PLY1830: '*args and **kwargs in g(**{})' is not supported for functions by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return [1][0](n)\n", "snippet.py(2,12): error PLY1833: '[1][0]' is neither part of this import nor in the Python call map")]
  [TestCase("def f(n: int) -> int:\n    h = n\n    return h(1)\n", "snippet.py(3,12): error PLY1832: the function value 'h' escapes, or 'h' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported (a function value held by a variable: #753)")]
  [TestCase("def f(n: int) -> int:\n    return filter(n)\n", "snippet.py(2,12): error PLY1832: the function value 'filter' escapes, or 'filter' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported (map and filter take a lambda or a function of the module and one sequence)")]
  [TestCase("def f(n: int) -> int:\n    return len(sorted([1], cmp=1))\n", "snippet.py(2,28): error PLY1820: sequence operation 'sorted(cmp=…)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> str:\n    return str(n, n)\n", "snippet.py(2,12): error PLY1822: string operation 'str(n, n)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> bool:\n    return isinstance(n, (int, float)) and not isinstance(n, ValueError)\n", "snippet.py(2,48): error PLY1802: 'isinstance of a value of the type int against ValueError' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    for a, b in zip([1], [2], [3]):\n        pass\n    return 0\n", "snippet.py(2,17): error PLY1820: sequence operation 'zip of other than two sequences' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    for a, b in enumerate():\n        pass\n    return 0\n", "snippet.py(2,17): error PLY1813: iteration over 'enumerate()' is not supported")]
  [TestCase("def f(n: int) -> int:\n    return n.__class__\n", "snippet.py(2,12): error PLY1844: attribute '__class__' of class 'n' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported")]
  [TestCase("def f(n: int) -> int:\n    import math\n    return 0\n", "snippet.py(2,5): error PLY1833: 'import math' is neither part of this import nor in the Python call map: an import inside a function")]
  [TestCase("import math\n\ndef f(n: int) -> float:\n    return math.frexp(n)\n", "snippet.py(4,12): error PLY1833: 'math.frexp' is neither part of this import nor in the Python call map")]
  [TestCase("import math\n\ndef f(n: int) -> float:\n    return math.tan\n", "snippet.py(4,12): error PLY1833: 'math.tan' is neither part of this import nor in the Python call map")]
  [TestCase("def f(n: int) -> int:\n    xs = [3, 1]\n    xs.sort()\n    return 0\n", "snippet.py(3,5): error PLY1820: sequence operation 'sort' is not supported by the Python frontend")]
  [TestCase("def f(s: str) -> str:\n    return s.upper()\n", "snippet.py(2,12): error PLY1822: string operation 'upper' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    xs = [3, 1]\n    return xs.keys()\n", "snippet.py(3,12): error PLY1820: sequence operation 'keys' with these arguments is not supported by the Python frontend")]
  [TestCase("import struct\n\ndef f(n: int) -> bytes:\n    return struct.pack(\"<hh\", n)\n", "snippet.py(4,12): error PLY1823: struct format or array typecode '<hh' is not supported: it packs 2 values, the call gives 1, which raises struct.error")]
  [TestCase("import struct\n\ndef f(n: int) -> bytes:\n    return struct.pack(\"<q?\", n)\n", "snippet.py(4,12): error PLY1823: struct format or array typecode '<q?' is not supported: it packs 2 values, the call gives 1, which raises struct.error")]
  [TestCase("import struct\n\ndef f(n: int) -> int:\n    return struct.iter_unpack(\"<h\", b\"ab\")\n", "snippet.py(4,12): error PLY1833: 'struct.iter_unpack' is neither part of this import nor in the Python call map")]
  public void Calls_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A call is refused where its arguments are unpacked, its callee is no name or a variable, its built-in takes other arguments, or its method or library name is not in the call map; a method of a class of the module shadows a refused method of the built-in types. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def k(x: int) -> int:\n    return -x\n\ndef f(n: int) -> int:\n    return sorted([1, 3, 2], key=k, reverse=True)[0] + max([1, 2], default=0)\n", "f", "int 3", 1L)]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\n    def upper(self) -> int:\n        return 5\n\ndef f(n: int) -> int:\n    return C().upper()\n", "f", "int 5", 1L)]
  [TestCase("import struct\n\ndef f(n: int) -> int:\n    return struct.calcsize(\"<hq\")\n", "f", "int 10", 1L)]
  public void Calls_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A global of the module is named apart from a global of an imported module that would take the same name. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("import helper\n\nhelper_G = 1\n\ndef f(n: int) -> int:\n    global helper_G\n    helper_G += n\n    return helper_G + helper.g(n)\n", "G = 10\n\ndef g(n: int) -> int:\n    global G\n    G += n\n    return G\n", "f", "int 15", 2L)]
  public void Globals_RunAsCPython_WithHelper(string source, string helper, string function, string expected, params object[] arguments)
    => Assert.That(RunWithHelper(source, helper, function, arguments), Is.EqualTo(expected));

  /// <summary>A function defined in a try block is nested in the function that runs it. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    try:\n        def g(x: int) -> int:\n            return x + 1\n    finally:\n        pass\n    return g(n)\n", "f", "int 2", 1L)]
  public void Nested_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A raise of a class or an except clause naming one that is no exception, or naming no class at all, is refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\ndef f(n: int) -> int:\n    raise C()\n", "snippet.py(6,11): error PLY1843: exception construct 'raise C(), which is no exception' is not supported by the Python frontend")]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\ndef f(n: int) -> int:\n    try:\n        return 1\n    except C:\n        return 2\n", "snippet.py(8,12): error PLY1843: exception construct 'except C, which names no exception class' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    try:\n        return 1\n    except (ValueError, n):\n        return 2\n", "snippet.py(4,25): error PLY1843: exception construct 'except n, which names no exception class' is not supported by the Python frontend")]
  public void Handlers_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A match pattern that is no literal, no member of an enum of the module and no capture is refused: bytes, a negated float or string, an unknown member, a member of a plain class, a dotted path. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    match n:\n        case b\"x\":\n            return 1\n    return 0\n", "snippet.py(3,14): error PLY1812: match pattern 'b\"x\"' is not supported; supported are literals, members of an enum, '|', captures, '_' and guards")]
  [TestCase("def f(n: int) -> int:\n    match n:\n        case -1.5:\n            return 1\n        case -\"a\":\n            return 2\n    return 0\n", "snippet.py(5,15): error PLY1800: SyntaxError: invalid syntax")]
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 1\n\nclass D:\n    def __init__(self) -> None:\n        self.v = 1\n\ndef f(n: int) -> int:\n    match n:\n        case E.B:\n            return 1\n        case D.A:\n            return 2\n        case n.x:\n            return 3\n        case a.b.c:\n            return 4\n    return 0\n", "snippet.py(12,14): error PLY1812: match pattern 'E.B' is not supported; supported are literals, members of an enum, '|', captures, '_' and guards\nsnippet.py(14,14): error PLY1812: match pattern 'D.A' is not supported; supported are literals, members of an enum, '|', captures, '_' and guards\nsnippet.py(16,14): error PLY1812: match pattern 'n.x' is not supported; supported are literals, members of an enum, '|', captures, '_' and guards\nsnippet.py(18,14): error PLY1812: match pattern 'a.b.c' is not supported; supported are literals, members of an enum, '|', captures, '_' and guards")]
  public void Patterns_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>map takes a lambda or a function of the module and one sequence, without keywords. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    return len(map(n, [1]))\n", "snippet.py(2,16): error PLY1832: the function value 'map' escapes, or 'map' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported (map and filter take a lambda or a function of the module and one sequence)")]
  [TestCase("def f(n: int) -> int:\n    return len(list(map(lambda x: x, [1], key=1)))\n", "snippet.py(2,21): error PLY1832: the function value 'map' escapes, or 'map' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported (map and filter take a lambda or a function of the module and one sequence)")]
  public void FunctionValues_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A call of a built-in the import does not have is refused with the family of what it does: dynamic attributes, string formatting, iteration, super, or an unknown name. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    return getattr()\n", "snippet.py(2,12): error PLY1844: attribute 'getattr' of class '' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported")]
  [TestCase("def f(n: int) -> int:\n    return setattr(n, \"a\", 1)\n", "snippet.py(2,12): error PLY1844: attribute 'setattr' of class 'n' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported")]
  [TestCase("def f(n: int) -> int:\n    return hasattr(n, \"a\")\n", "snippet.py(2,12): error PLY1844: attribute 'hasattr' of class 'n' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported")]
  [TestCase("def f(n: int) -> int:\n    return delattr(n, \"a\")\n", "snippet.py(2,12): error PLY1844: attribute 'delattr' of class 'n' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported")]
  [TestCase("def f(n: int) -> int:\n    return vars(n)\n", "snippet.py(2,12): error PLY1844: attribute 'vars' of class 'n' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported")]
  [TestCase("def f(n: int) -> str:\n    return repr(n)\n", "snippet.py(2,12): error PLY1822: string operation 'repr(n)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> str:\n    return format(n)\n", "snippet.py(2,12): error PLY1822: string operation 'format(n)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> str:\n    return hex(n)\n", "snippet.py(2,12): error PLY1822: string operation 'hex(n)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> str:\n    return oct(n)\n", "snippet.py(2,12): error PLY1822: string operation 'oct(n)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> str:\n    return bin(n)\n", "snippet.py(2,12): error PLY1822: string operation 'bin(n)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> str:\n    return ascii(n)\n", "snippet.py(2,12): error PLY1822: string operation 'ascii(n)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return memoryview(n)\n", "snippet.py(2,12): error PLY1820: sequence operation 'memoryview' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return iter(n)\n", "snippet.py(2,12): error PLY1820: sequence operation 'iter' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return next(n)\n", "snippet.py(2,12): error PLY1820: sequence operation 'next' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return super()\n", "snippet.py(2,12): error PLY1840: 'super()' is outside the first Python subset (dataclasses, simple final classes, enums)")]
  [TestCase("def f(n: int) -> int:\n    return frobnicate(n)\n", "snippet.py(2,12): error PLY1833: 'frobnicate' is neither part of this import nor in the Python call map")]
  public void UnknownNames_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>The byte order of to_bytes and from_bytes is a constant "little" or "big", by position or by name. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> bytes:\n    return n.to_bytes(2, byteorder=n)\n", "snippet.py(2,36): error PLY1823: struct format or array typecode 'n' is not supported: the byte order is a constant \"little\" or \"big\"")]
  [TestCase("def f(n: int) -> bytes:\n    return n.to_bytes(2, n)\n", "snippet.py(2,26): error PLY1823: struct format or array typecode 'n' is not supported: the byte order is a constant \"little\" or \"big\"")]
  [TestCase("def f(n: int) -> int:\n    return int.from_bytes(b\"ab\", \"middle\")\n", "snippet.py(2,34): error PLY1823: struct format or array typecode '\"middle\"' is not supported: the byte order is a constant \"little\" or \"big\"")]
  [TestCase("def f(n: int) -> int:\n    return int.from_bytes(b\"ab\", 7)\n", "snippet.py(2,34): error PLY1823: struct format or array typecode '7' is not supported: the byte order is a constant \"little\" or \"big\"")]
  public void ByteOrder_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>The byte order of to_bytes and from_bytes is a constant "little" or "big", by position or by name. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    return len(n.to_bytes(2, byteorder=\"big\")) + int.from_bytes(n.to_bytes(2, \"little\"), byteorder=\"little\")\n", "f", "int 260", 258L)]
  public void ByteOrder_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>An unpacking star outside a call is refused as an expression and inside a subscript as a display element. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    x = *n\n    return 0\n", "snippet.py(2,9): error PLY1800: SyntaxError: can't use starred expression here")]
  [TestCase("def f(n: int) -> int:\n    return [1][*n]\n", "snippet.py(2,16): error PLY1820: sequence operation '*n in a display' is not supported by the Python frontend")]
  public void Starred_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A subscript of a tuple of a slice and an index is refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    return [1][1:2, 3]\n", "snippet.py(2,16): error PLY1821: slice form '[1][1:2, 3]' is not supported; slices are imported as values only")]
  public void Slices_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

}
