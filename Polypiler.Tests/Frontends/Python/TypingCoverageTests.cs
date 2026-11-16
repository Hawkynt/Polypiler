using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>The types the Python frontend infers for expressions and the diagnostics where it can infer none: optionals, tuples, enums, the standard library, built-ins, methods, sequence operators, module tuples, defaults, generators, handlers, unpacking, containers and key functions. Every value is CPython's.</summary>
[TestFixture]
public sealed class TypingCoverageTests {

  /// <summary>Imports a snippet beside a module <c>helper</c> it imports, both from their recorded trees.</summary>
  private static PythonImportResult WithHelper(string source, string helper)
    => PythonSnippet.Import(source, new PythonImportOptions { Modules = name => name == "helper" ? ("helper.py", helper, PythonFixture.Dump("helper.py", helper)) : null });

  /// <summary>What the interpreter does for a call of a function of a snippet imported beside <c>helper</c>.</summary>
  private static string RunWithHelper(string source, string helper, string function, object[] arguments) {
    var module = PythonFixture.Module(WithHelper(source, helper));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return PythonOutcome.Interpreted(module, new PythonCall(function, arguments));
  }

  /// <summary>A module-level list is a table: it is indexed, sliced, iterated and summed as a list of its elements; an enum is iterated over its members. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("T = [1, 2, 3]\n\ndef f(n: int) -> int:\n    t = 0\n    for x in T:\n        t += x\n    return t + len(T) + T[n] + T[-1]\n", "f", "int 14", 1L)]
  [TestCase("T = [1, 2, 3]\n\ndef f(n: int) -> int:\n    t = 0\n    for x in T:\n        t += x\n    return t + len(T) + T[n] + T[-1]\n", "f", "int 13", 0L)]
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 1\n    B = 2\n\ndef f(n: int) -> int:\n    t = 0\n    for e in E:\n        t += e.value\n    return t\n", "f", "int 3", 1L)]
  public void Tables_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A module-level list is a table: it is indexed, sliced, iterated and summed as a list of its elements; an enum is iterated over its members. Each is refused with exactly these diagnostics.</summary>
  [TestCase("T = [1.5, 2.5]\nU = T\n\ndef f(n: int) -> float:\n    return sum(U) + max(T)\n", "snippet.py(5,12): error PLY1820: sequence operation 'sum of float values', which CPython adds with compensation, is not supported by the Python frontend")]
  public void Tables_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>Names imported from math and struct are typed as their modules' functions; struct.unpack takes a constant format and a buffer, and other modules give no values. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("import struct\n\ndef f(n: int) -> int:\n    a, b = struct.unpack(\"<hh\", struct.pack(\"<hh\", n, -n))\n    return a - b + struct.unpack_from(\"<h\", b\"\\x01\\x02\\x03\", 1)[0]\n", "f", "int 772", 1L)]
  [TestCase("from struct import unpack_from, calcsize\n\ndef f(n: int) -> int:\n    return unpack_from(\"<b\", b\"\\xff\")[0] + calcsize(\"<i\")\n", "f", "int 3", 1L)]
  [TestCase("from math import sqrt, floor\n\ndef f(n: int) -> float:\n    return sqrt(n) + floor(2.5)\n", "f", "float 4.0", 4L)]
  [TestCase("from struct import pack, unpack\n\ndef f(n: int) -> int:\n    return unpack(\"<h\", pack(\"<h\", n))[0]\n", "f", "int 1", 1L)]
  public void Library_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>Names imported from math and struct are typed as their modules' functions; struct.unpack takes a constant format and a buffer, and other modules give no values. Each is refused with exactly these diagnostics.</summary>
  [TestCase("import enum\n\ndef f(n: int) -> int:\n    x = enum.Enum\n    return 0\n", "snippet.py(4,5): error PLY1801: the type of 'x' in 'f' is not known: no value of a known type reaches it")]
  [TestCase("import struct\n\ndef f(b: bytes) -> int:\n    return struct.unpack(\"<q\")[0]\n", "snippet.py(4,12): error PLY1802: 'struct.unpack with 1 arguments' is not supported by the Python frontend")]
  [TestCase("import struct\n\ndef f(b: bytes, s: str) -> int:\n    return struct.unpack(s, b)[0]\n", "snippet.py(4,26): error PLY1823: struct format or array typecode 's' is not supported: the format is not a constant")]
  [TestCase("import dataclasses\n\ndef f(n: int) -> int:\n    x = dataclasses.field()\n    return 0\n", "snippet.py(4,5): error PLY1801: the type of 'x' in 'f' is not known: no value of a known type reaches it")]
  public void Library_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A function, a constant, a class, a dataclass and an enum of an imported module are typed through the module and through names imported from it; a name the module lacks is refused. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("import helper\n\ndef f(n: int) -> int:\n    return helper.g(n) + helper.K + helper.B(n).v\n", "K = 5\n\nclass B:\n    def __init__(self, v: int) -> None:\n        self.v = v\n\ndef g(x: int) -> int:\n    return x * 2\n", "f", "int 14", 3L)]
  [TestCase("import helper\n\ndef f(n: int) -> int:\n    return helper.g(n)\n", "def g(x):\n    return x * 3\n", "f", "int 6", 2L)]
  [TestCase("from helper import P\n\ndef f(n: int) -> int:\n    return P(n).v\n", "from dataclasses import dataclass\n\n@dataclass\nclass P:\n    v: int\n", "f", "int 2", 2L)]
  [TestCase("import helper\n\ndef f(n: int) -> int:\n    return helper.P(n).v\n", "from dataclasses import dataclass\n\n@dataclass\nclass P:\n    v: int\n", "f", "int 2", 2L)]
  [TestCase("from helper import K\n\ndef f(n: int) -> int:\n    return K + n\n", "K = 40\n", "f", "int 42", 2L)]
  public void Imports_RunAsCPython_WithHelper(string source, string helper, string function, string expected, params object[] arguments)
    => Assert.That(RunWithHelper(source, helper, function, arguments), Is.EqualTo(expected));

  /// <summary>A function, a constant, a class, a dataclass and an enum of an imported module are typed through the module and through names imported from it; a name the module lacks is refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("import helper\n\ndef f(n: int) -> int:\n    return helper.nope(n)\n", "def g(x: int) -> int:\n    return x * 2\n", "snippet.py(4,12): error PLY1833: 'helper.nope' is neither part of this import nor in the Python call map")]
  [TestCase("import helper\nfrom helper import E\n\ndef f(n: int) -> int:\n    e = helper.E(1)\n    return e.value + E.B.value\n", "from enum import Enum\n\nclass E(Enum):\n    A = 1\n    B = 2\n", "snippet.py(6,22): error PLY1832: the function value 'E' escapes, or 'E' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported (escaping function values: #753)")]
  public void Imports_AreRefused_WithHelper(string source, string helper, string expected) {
    var result = WithHelper(source, helper);
    Assert.That(result.Module, Is.Null);
    Assert.That(string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString())), Is.EqualTo(expected));
  }

  /// <summary>A static method is called through its class and through a variable of it, not through a value that has to be evaluated. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\n    @staticmethod\n    def s(x: int) -> int:\n        return x + 1\n\ndef f(n: int) -> int:\n    c = C()\n    return C.s(n) + c.s(n)\n", "f", "int 6", 2L)]
  public void StaticMethods_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A static method is called through its class and through a variable of it, not through a value that has to be evaluated. Each is refused with exactly these diagnostics.</summary>
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\n    @staticmethod\n    def s(x: int) -> int:\n        return x + 1\n\ndef make() -> C:\n    return C()\n\ndef f(n: int) -> int:\n    return make().s(n)\n", "snippet.py(13,12): error PLY1802: 'the static method s called through a value that has to be evaluated' is not supported by the Python frontend")]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\n    @staticmethod\n    def s(x):\n        return x + 1\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(6,5): error PLY1801: the result type of 'C.s' is not known: it has no return type hint and no return value of a known type\nsnippet.py(6,11): error PLY1801: the type of 'x' in 'C.s' is not known: it has no type hint and nothing in the import calls 'C.s'; add a type hint such as 'x: int'")]
  public void StaticMethods_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>An optional that is the operand of 'or' or the owner of an attribute store is the value it holds where it is used. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 7\n\n    def m(self) -> int:\n        return self.v\n\ndef f(n: int) -> int:\n    o: C | None = C() if n > 0 else None\n    return o.m() + o.v\n", "f", "int 14", 2L)]
  [TestCase("def f(n: int) -> int:\n    o: int | None = n if n > 0 else None\n    p: int | None = None\n    return (p or o) or -1\n", "f", "int -1", 0L)]
  [TestCase("def f(n: int) -> int:\n    o: int | None = n if n > 0 else None\n    p: int | None = None\n    return (p or o) or -1\n", "f", "int 2", 2L)]
  [TestCase("def f(n: int) -> bool:\n    o: int | None = n if n > 0 else None\n    return o != None\n", "f", "bool True", 2L)]
  [TestCase("def f(n: int) -> bool:\n    o: int | None = n if n > 0 else None\n    return o != None\n", "f", "bool False", 0L)]
  [TestCase("def f(n: int) -> int:\n    o: float | None = 1.5 if n > 0 else None\n    x: float = 2.0\n    return int(x + (o or 0.5))\n", "f", "int 3", 2L)]
  [TestCase("def f(n: int) -> int:\n    o: float | None = 1.5 if n > 0 else None\n    x: float = 2.0\n    return int(x + (o or 0.5))\n", "f", "int 2", 0L)]
  [TestCase("def f(a: int, b: int) -> int:\n    o: int | None = a if a > 0 else None\n    return o or b\n", "f", "int 3", 0L, 3L)]
  [TestCase("def f(a: int, b: int) -> int:\n    o: int | None = a if a > 0 else None\n    return o or b\n", "f", "int 5", 5L, 3L)]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\ndef f(n: int) -> int:\n    o: C | None = C() if n > 0 else None\n    if o is not None:\n        o.v = 5\n        return o.v\n    return 0\n", "f", "int 5", 1L)]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\ndef f(n: int) -> int:\n    o: C | None = C() if n > 0 else None\n    if o is not None:\n        o.v = 5\n        return o.v\n    return 0\n", "f", "int 0", 0L)]
  public void Optional_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>An optional that is the operand of 'or' or the owner of an attribute store is the value it holds where it is used. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> bool:\n    o: int | None = n if n > 0 else None\n    return o == 2\n", "snippet.py(3,12): error PLY1802: '== of the optional value of the type int | None; test it with 'is None' first' is not supported by the Python frontend")]
  public void Optional_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>Values of two branches join: a tuple of a list is refused, two generators of one element type and two lists of one element type join. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    t = (n, [1]) if n > 0 else (n, [])\n    return len(t[1])\n", "snippet.py(2,9): error PLY1802: 'a tuple of the types int, list[int]; a tuple holds numbers, bytes, enum members, objects and tuples of them' is not supported by the Python frontend")]
  public void Joins_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>Values of two branches join: a tuple of a list is refused, two generators of one element type and two lists of one element type join. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def g(n: int):\n    for i in range(n):\n        yield i\n\ndef f(n: int) -> int:\n    it = g(n) if n > 0 else g(1)\n    return sum(it)\n", "f", "int 1", 2L)]
  [TestCase("def g(n: int):\n    for i in range(n):\n        yield i\n\ndef f(n: int) -> int:\n    it = g(n) if n > 0 else g(1)\n    return sum(it)\n", "f", "int 0", -1L)]
  [TestCase("def f(n: int) -> int:\n    xs = [1] if n > 0 else []\n    return len(xs)\n", "f", "int 1", 2L)]
  [TestCase("def f(n: int) -> int:\n    xs = [1] if n > 0 else []\n    return len(xs)\n", "f", "int 0", -1L)]
  public void Joins_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A generator that yields from an empty list has no element type. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("from typing import Iterator\n\ndef g(n: int) -> Iterator[int]:\n    yield n\n    yield from [1, 2]\n\ndef f(n: int) -> int:\n    return sum(g(n))\n", "f", "int 5", 2L)]
  public void Generators_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A generator that yields from an empty list has no element type. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def g(n: int):\n    yield 1\n    yield 2.5\n\ndef f(n: int) -> float:\n    return sum(g(n))\n", "snippet.py(6,12): error PLY1820: sequence operation 'sum of float values', which CPython adds with compensation, is not supported by the Python frontend")]
  [TestCase("from typing import Iterator\n\ndef g(n: int) -> Iterator[int]:\n    if n > 100:\n        yield from []\n\ndef f(n: int) -> int:\n    return len(list(g(n)))\n", "snippet.py(5,20): error PLY1801: the type of the elements of '[]' is not known; add a type hint such as 'list[int]' to what holds it")]
  public void Generators_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>Unary operators of bools and ints give ints; of a str, and ~ of a float, they raise TypeError and are refused. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    return -True + ~n + +False\n", "f", "int -4", 2L)]
  public void UnaryTypes_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>Unary operators of bools and ints give ints; of a str, and ~ of a float, they raise TypeError and are refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(s: str) -> int:\n    return -s\n", "snippet.py(2,12): error PLY1802: '- of a value of the type str, which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("def f(s: str) -> int:\n    return ~1.5\n", "snippet.py(2,12): error PLY1802: '~ with a float operand, which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("def f(s: str) -> int:\n    return +s\n", "snippet.py(2,12): error PLY1802: '+ of a value of the type str, which raises TypeError,' is not supported by the Python frontend")]
  public void UnaryTypes_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>The bitwise operators of bools give bools and of ints ints; ** of a negative constant power and of floats gives a float; - and + of other types, and * of a tuple, raise TypeError and are refused. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    return (True | False) + (True & n) + (n ^ True) + (n << 2) + (n >> 1) + int(2 ** -1 * 4) + 2 ** n\n", "f", "int 19", 2L)]
  [TestCase("def f(x: float) -> float:\n    return x ** 2 + 2 ** x + x // 1 + x % 2\n", "f", "float 14.40685424949238", 2.5)]
  [TestCase("def f(n: int) -> int:\n    t = (1, 2) + (n,)\n    return t[2]\n", "f", "int 2", 2L)]
  public void BinaryTypes_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>The bitwise operators of bools give bools and of ints ints; ** of a negative constant power and of floats gives a float; - and + of other types, and * of a tuple, raise TypeError and are refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(s: str) -> int:\n    return s - s\n", "snippet.py(2,12): error PLY1802: '- of the types str and str, which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("def f(s: str) -> int:\n    return s + 1\n", "snippet.py(2,12): error PLY1802: '+ of the types str and int, which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return (1, 2) * 2\n", "snippet.py(2,12): error PLY1802: '* of the types tuple[int, int] and int, which raises TypeError,' is not supported by the Python frontend")]
  public void BinaryTypes_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>divmod, pow, list, sorted, reversed, zip, filter, map, sum, min, max, abs and str of exceptions are typed from their arguments. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    q, r = divmod(n, 3)\n    a, b = divmod(7.5, 2)\n    return q + r + int(a) + int(b) + pow(n, 2)\n", "f", "int 10", 2L)]
  [TestCase("def f(n: int) -> int:\n    t = 0\n    for x in reversed([1, 2]):\n        t = t * 10 + x\n    for x in reversed(b\"ab\"):\n        t += x\n    return t\n", "f", "int 216", 2L)]
  [TestCase("def f(n: int) -> int:\n    t = 0\n    for a, b in zip([1, 2], b\"xy\"):\n        t += a * b\n    for x in filter(lambda v: v > 1, [1, 2, 3]):\n        t += x\n    for x in map(lambda v: v * 2, (1, 2)):\n        t += x\n    return t\n", "f", "int 373", 2L)]
  public void Builtins_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>divmod, pow, list, sorted, reversed, zip, filter, map, sum, min, max, abs and str of exceptions are typed from their arguments. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    xs = list(b\"ab\")\n    ys = sorted(\"ba\")\n    zs = list((1, 2))\n    return xs[0] + len(ys) + zs[1] + len(list())\n", "snippet.py(5,42): error PLY1801: the type of the elements of 'list()' is not known; add a type hint such as 'list[int]' to what holds it")]
  [TestCase("def f(n: int) -> float:\n    return sum([1.5, 2.0]) + sum([True, True]) + sum([1, 2], 0.5) + min(1, 2.5) + abs(-n) + abs(-1.5) + abs(True)\n", "snippet.py(2,12): error PLY1820: sequence operation 'sum of float values', which CPython adds with compensation, is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return min([3, 1, 2]) + max(\"ab\" == \"ab\", False) + len(tuple((1, 2)))\n", "snippet.py(2,56): error PLY1802: 'len of a value of the type tuple[int, int], which raises TypeError,' is not supported by the Python frontend")]
  public void Builtins_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A tuple is indexed by a constant within it, negative ones included; a slice of a tuple has no type the import holds. Each is refused with exactly these diagnostics.</summary>
  [TestCase("T = (1, 2, 3)\n\ndef f(n: int) -> int:\n    return len(T[0:2])\n", "snippet.py(4,16): error PLY1821: slice form 'T[0:2]' of a value of the type tuple[int, int, int] is not supported; slices are imported as values only")]
  [TestCase("def f(n: int) -> int:\n    t = (1, 2, 3)\n    s = t[0:2]\n    return 0\n", "snippet.py(3,5): error PLY1801: the type of 's' in 'f' is not known: no value of a known type reaches it")]
  [TestCase("def f(n: int) -> int:\n    t = (1, 2.5)\n    return t[n]\n", "snippet.py(3,12): error PLY1802: 'the index n of a tuple of 2 items; a tuple is indexed by a constant within it' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    t = (1, 2.5)\n    return t[5]\n", "snippet.py(3,12): error PLY1802: 'the index 5 of a tuple of 2 items; a tuple is indexed by a constant within it' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    t = (1, 2.5)\n    return t[-3]\n", "snippet.py(3,12): error PLY1802: 'the index -3 of a tuple of 2 items; a tuple is indexed by a constant within it' is not supported by the Python frontend")]
  public void TupleTypes_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A tuple is indexed by a constant within it, negative ones included; a slice of a tuple has no type the import holds. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> float:\n    t = (1, 2.5)\n    return t[-1] + t[-2]\n", "f", "float 3.5", 1L)]
  public void TupleTypes_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A member an enum does not have and a method of an enum are refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 1\n\ndef f(n: int) -> int:\n    return E.B.value\n", "snippet.py(7,12): error PLY1840: 'E.B, a class attribute,' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481")]
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 1\n    B = 2\n\n    def twice(self) -> int:\n        return self.value * 2\n\ndef f(n: int) -> int:\n    return E.B.twice()\n", "snippet.py(7,5): error PLY1840: 'the method twice of the enum E' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481\nsnippet.py(11,12): error PLY1833: 'E' is neither part of this import nor in the Python call map")]
  public void Enums_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A built-in applied to a value it does not take has no type, and so neither has what it is assigned to; print is no built-in of the import. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    return len(list(5))\n", "snippet.py(2,21): error PLY1813: iteration over '5' of the type int is not supported")]
  [TestCase("def f(n: int) -> int:\n    for x in reversed(5):\n        pass\n    return 0\n", "snippet.py(2,9): error PLY1801: the type of 'x' in 'f' is not known: no value of a known type reaches it")]
  [TestCase("def f(n: int) -> int:\n    t = tuple([1, 2])\n    return 0\n", "snippet.py(2,5): error PLY1801: the type of 't' in 'f' is not known: no value of a known type reaches it")]
  [TestCase("def f(n: int) -> int:\n    for a, b in zip([1], 5):\n        pass\n    return 0\n", "snippet.py(2,12): error PLY1801: the type of 'b' in 'f' is not known: no value of a known type reaches it")]
  [TestCase("def f(n: int) -> int:\n    for a in filter(lambda x: x > 0, 5):\n        pass\n    return 0\n", "snippet.py(2,9): error PLY1801: the type of 'a' in 'f' is not known: no value of a known type reaches it")]
  [TestCase("def f(n: int) -> int:\n    x = print(n)\n    return 0\n", "snippet.py(2,9): error PLY1833: 'print' is neither part of this import nor in the Python call map")]
  public void BuiltinTypes_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A method is typed by its receiver: a method of a class through the class, a method of an int, an optional receiver, append as a value, a property called, a method a str does not have. Each is refused with exactly these diagnostics.</summary>
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\n    def m(self) -> int:\n        return self.v\n\ndef f(n: int) -> int:\n    return C.m(C())\n", "snippet.py(9,12): error PLY1840: 'C.m, a class attribute,' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481")]
  [TestCase("def f(n: int) -> int:\n    return int.bit_length(n)\n", "snippet.py(2,12): error PLY1802: 'int.bit_length' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    xs = [1]\n    y = xs.append(n)\n    return len(xs)\n", "snippet.py(3,5): error PLY1802: 'the variable y, which only ever holds None,' is not supported by the Python frontend")]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 7\n\n    @property\n    def p(self) -> int:\n        return self.v\n\ndef f(n: int) -> int:\n    return C().p()\n", "snippet.py(10,12): error PLY1844: attribute 'p' of class 'C' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported")]
  [TestCase("def f(s: str) -> int:\n    return s.frob()\n", "snippet.py(2,12): error PLY1822: string operation 'frob' is not supported by the Python frontend")]
  public void Methods_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A method is typed by its receiver: a method of a class through the class, a method of an int, an optional receiver, append as a value, a property called, a method a str does not have. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 7\n\n    def m(self) -> int:\n        return self.v\n\ndef f(n: int) -> int:\n    o: C | None = C() if n > 0 else None\n    if o is not None:\n        return o.m()\n    return 0\n", "f", "int 7", 1L)]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 7\n\n    def m(self) -> int:\n        return self.v\n\ndef f(n: int) -> int:\n    o: C | None = C() if n > 0 else None\n    if o is not None:\n        return o.m()\n    return 0\n", "f", "int 0", 0L)]
  public void Methods_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>+ of lists of int and float, of bytes and bytearray, and * of a sequence by an int or a bool on either side; * by a float raises TypeError. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> float:\n    xs = [1] + [1.5]\n    return xs[0] + xs[1]\n", "snippet.py(2,5): error PLY1801: the type of 'xs' in 'f' is not known: no value of a known type reaches it")]
  [TestCase("def f(n: int) -> int:\n    return len([1] * 2.0)\n", "snippet.py(2,16): error PLY1802: '* of the types list[int] and float, which raises TypeError,' is not supported by the Python frontend")]
  public void SequenceOperators_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>+ of lists of int and float, of bytes and bytearray, and * of a sequence by an int or a bool on either side; * by a float raises TypeError. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    return len(b\"a\" + bytearray(b\"bc\")) + len(bytearray(b\"x\") + b\"yz\")\n", "f", "int 6", 1L)]
  [TestCase("def f(n: int) -> int:\n    return len(n * \"ab\") + len(True * [1, 2]) + len(\"xy\" * True) + len(n * b\"q\")\n", "f", "int 10", 2L)]
  [TestCase("def f(n: int) -> int:\n    return len(n * \"ab\") + len(True * [1, 2]) + len(\"xy\" * True) + len(n * b\"q\")\n", "f", "int 4", 0L)]
  [TestCase("def f(n: int) -> int:\n    return len(n * \"ab\") + len(True * [1, 2]) + len(\"xy\" * True) + len(n * b\"q\")\n", "f", "int 4", -1L)]
  public void SequenceOperators_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A module list without elements and a parameter of a function nothing calls have no type. Each is refused with exactly these diagnostics.</summary>
  [TestCase("xs = []\n\ndef f(n: int) -> int:\n    return len(xs)\n", "snippet.py(1,6): error PLY1801: the type of the elements of '[]' is not known; add a type hint such as 'list[int]' to what holds it")]
  [TestCase("def g(x):\n    return x\n\ndef f(n: int) -> int:\n    try:\n        return 1\n    except ValueError:\n        return 2\n    match n:\n        case 1:\n            return 3\n    return 0\n", "snippet.py(1,1): error PLY1801: the result type of 'g' is not known: it has no return type hint and no return value of a known type\nsnippet.py(1,7): error PLY1801: the type of 'x' in 'g' is not known: it has no type hint and nothing in the import calls 'g'; add a type hint such as 'x: int'")]
  public void Unknowns_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A field hinted None is refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("from dataclasses import dataclass\n\n@dataclass\nclass D:\n    x: None\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(5,8): error PLY1801: the type hint 'None' of the field 'x' of 'D' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  public void Hints_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A module-level tuple assignment types each name from its position, from a tuple display or a tuple value. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("A, B = 1, 2.5\nC, D = A, B\n\ndef f(n: int) -> float:\n    return A + B + C + D\n", "f", "float 7.0", 1L)]
  public void ModuleTuples_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A default without a hint types the parameter of a nested function and of a method. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    def g(x=2) -> int:\n        return x * n\n    return g() + g(5)\n", "f", "int 21", 3L)]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\n    def m(self, k=4) -> int:\n        return self.v + k\n\ndef f(n: int) -> int:\n    return C().m() + C().m(n)\n", "f", "int 9", 3L)]
  public void Defaults_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>An except clause of a tuple of classes binds the common base. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    try:\n        raise ValueError(\"x\")\n    except (ValueError, KeyError) as e:\n        return 1\n    return 0\n", "f", "int 1", 1L)]
  public void Handlers_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>Unpacking an element of no tuple type into names is refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    for a, b in [[], []]:\n        pass\n    return 0\n", "snippet.py(2,9): error PLY1801: the type of 'a' in 'f' is not known: no value of a known type reaches it\nsnippet.py(2,12): error PLY1801: the type of 'b' in 'f' is not known: no value of a known type reaches it")]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\ndef f(n: int) -> int:\n    for x in C:\n        pass\n    return 0\n", "snippet.py(6,9): error PLY1801: the type of 'x' in 'f' is not known: no value of a known type reaches it")]
  public void Unpacking_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A list stored into by index takes the element type of what is stored. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    xs = []\n    xs[n] = 5\n    return xs[0]\n", "f", "raise IndexError", 0L)]
  [TestCase("def f(n: int) -> int:\n    xs = []\n    xs[n] = 5\n    return xs[0]\n", "f", "raise IndexError", 1L)]
  public void Containers_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A key of min, max and sorted is a lambda or a function of the module whose parameter takes the element type. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    return max(1, 2, key=lambda x: -x) + min(3, 1, key=lambda x: -x)\n", "f", "int 4", 0L)]
  [TestCase("def k(x):\n    return -x\n\ndef f(n: int) -> int:\n    return sorted([1, 3, 2], key=k)[0]\n", "f", "int 3", 0L)]
  public void KeyFunctions_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

}
