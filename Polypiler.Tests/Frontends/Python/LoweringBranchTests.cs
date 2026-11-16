using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>The branches of the lowering of the Python frontend beside the scenarios: built-ins without arguments and with the wrong ones, isinstance of every built-in type, the loop forms of range, enumerate, zip and reversed, match against None and the bools, attributes assigned in branches of __init__, scopes of nested functions, and type hints through imports and typing. Every value is CPython's.</summary>
[TestFixture]
public sealed class LoweringBranchTests {

  /// <summary>Imports a snippet beside a module <c>helper</c> it imports, both from their recorded trees.</summary>
  private static PythonImportResult WithHelper(string source, string helper)
    => PythonSnippet.Import(source, new PythonImportOptions { Modules = name => name == "helper" ? ("helper.py", helper, PythonFixture.Dump("helper.py", helper)) : null });

  /// <summary>What the interpreter does for a call of a function of a snippet imported beside <c>helper</c>.</summary>
  private static string RunWithHelper(string source, string helper, string function, object[] arguments) {
    var module = PythonFixture.Module(WithHelper(source, helper));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return PythonOutcome.Interpreted(module, new PythonCall(function, arguments));
  }

  /// <summary>A tuple holds numbers, tables and optional numbers; an index of a list is an integer; len of a tuple is known for a variable and an empty display. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    t = (\"a\", 1)\n    return 0\n", "snippet.py(2,9): error PLY1802: 'a tuple of the types str, int; a tuple holds numbers, bytes, enum members, objects and tuples of them' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    xs = [1]\n    return xs[1.5]\n", "snippet.py(3,12): error PLY1802: 'an index of the type float, which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    xs = [1]\n    return xs[\"a\"]\n", "snippet.py(3,12): error PLY1802: 'an index of the type str, which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("def g(n: int) -> tuple[int, int]:\n    return (n, 1)\n\ndef f(n: int) -> int:\n    return len(g(n))\n", "snippet.py(5,12): error PLY1802: 'len of a value of the type tuple[int, int], which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return len((n, 1))\n", "snippet.py(2,12): error PLY1802: 'len of a value of the type tuple[int, int], which raises TypeError,' is not supported by the Python frontend")]
  public void Tuples_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A tuple holds numbers, tables and optional numbers; an index of a list is an integer; len of a tuple is known for a variable and an empty display. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def g(n: int) -> tuple[int, int]:\n    return (n, 1)\n\ndef f(n: int) -> int:\n    t = g(n)\n    return len(t) + len(())\n", "f", "int 2", 2L)]
  public void Tuples_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>An element is stored into a list, a bytearray and an object with __setitem__, augmented too; other stores raise TypeError and are refused. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    xs = [1, 2, 3]\n    xs[n] += 10\n    b = bytearray(b\"abc\")\n    b[n] += 1\n    return xs[2] + b[2]\n", "f", "int 113", 2L)]
  [TestCase("def f(n: int) -> int:\n    xs = [1, 2, 3]\n    xs[n] += 10\n    b = bytearray(b\"abc\")\n    b[n] += 1\n    return xs[2] + b[2]\n", "f", "raise IndexError", 5L)]
  public void Stores_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>An element is stored into a list, a bytearray and an object with __setitem__, augmented too; other stores raise TypeError and are refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("class Box:\n    def __init__(self) -> None:\n        self.v = [0, 0, 0]\n\n    def __getitem__(self, i: int) -> int:\n        return self.v[i]\n\n    def __setitem__(self, i: int, x: int) -> None:\n        self.v[i] = x\n\ndef f(n: int) -> int:\n    b = Box()\n    b[n] = 5\n    b[n] += 2\n    return b[n]\n", "snippet.py(3,9): error PLY1840: 'the attribute v of Box of the type list[int]' is outside the first Python subset (dataclasses, simple final classes, enums): an attribute holds a number, a truth value, bytes, a tuple of numbers, an enum member or an object")]
  [TestCase("def f(n: int) -> int:\n    b = bytearray(b\"abc\")\n    b[0] = 1.5\n    return 0\n", "snippet.py(3,5): error PLY1802: 'storing a value of the type float into a bytearray, which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("def f(s: str) -> int:\n    s[0] = \"a\"\n    return 0\n", "snippet.py(2,5): error PLY1802: 'assignment to an element of a value of the type str, which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("class Box:\n    def __init__(self) -> None:\n        self.v = 1\n\ndef f(n: int) -> int:\n    return Box()[0]\n", "snippet.py(6,12): error PLY1802: 'Box()[0] of an object of Box, which has no __getitem__, and raises TypeError' is not supported by the Python frontend")]
  public void Stores_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>Augmented assignments of sequences make new sequences; *= of a tuple is refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    xs = [1]\n    xs += [n]\n    s = \"a\"\n    s += \"bc\"\n    t = (1,)\n    t *= 2\n    b = b\"x\"\n    b *= n\n    return len(xs) + len(s) + len(t) + len(b)\n", "snippet.py(7,5): error PLY1802: '* of the types tuple[int] and int, which raises TypeError,' is not supported by the Python frontend")]
  public void Augmented_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A list is extended by bytes, tuples, tables, ranges, generators and strs of its element type; other elements are refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("T = [5, 6]\nF = [1.5]\n\ndef g(n: int):\n    yield n\n\ndef f(n: int) -> int:\n    xs = [1]\n    xs.extend(b\"ab\")\n    xs.extend((7, 8))\n    xs.extend(T)\n    xs.extend(range(2))\n    xs.extend(g(n))\n    xs.extend([])\n    ys = [\"a\"]\n    ys.extend(\"bc\")\n    zs = [0.5]\n    zs.extend(F)\n    return sum(xs) + len(ys) + len(zs)\n", "snippet.py(14,15): error PLY1801: the type of the elements of '[]' is not known; add a type hint such as 'list[int]' to what holds it")]
  [TestCase("def f(n: int) -> int:\n    xs = [1]\n    xs.extend([\"a\"])\n    return 0\n", "snippet.py(3,5): error PLY1802: 'extend of a list of int by a value of the type list[str]' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    xs = [1]\n    xs.extend((1, 2.5))\n    return 0\n", "snippet.py(3,5): error PLY1802: 'extend of a list of int by a value of the type tuple[int, float]' is not supported by the Python frontend")]
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 1\n    B = 2\n\ndef f(n: int) -> int:\n    xs = [E.A]\n    xs.extend(E)\n    return len(xs)\n", "snippet.py(9,15): error PLY1832: the function value 'E' escapes, or 'E' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported (escaping function values: #753)")]
  public void Extend_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A list is extended by bytes, tuples, tables, ranges, generators and strs of its element type; other elements are refused. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    xs = [(1, 2)]\n    xs.extend(((3, 4), (5, 6)))\n    return xs[2][1]\n", "f", "int 6", 2L)]
  public void Extend_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>enumerate, map with a nested function, comprehension variables of their own, sum and max with the arguments they take, and refusals with others. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    ps = list(enumerate([5, 6], n))\n    qs = list(enumerate([7]))\n    return ps[1][0] + ps[1][1] + qs[0][1] + sum(i * x for i, x in enumerate([1, 2]))\n", "f", "int 18", 2L)]
  [TestCase("def f(n: int) -> int:\n    def g(x: int) -> int:\n        return x * n\n    return sum(list(map(g, [1, 2])))\n", "f", "int 6", 2L)]
  [TestCase("def f(n: int) -> int:\n    x = 5\n    ys = [x * 2 for x in [1, 2]]\n    return x + ys[1]\n", "f", "int 9", 2L)]
  public void Iterables_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>enumerate, map with a nested function, comprehension variables of their own, sum and max with the arguments they take, and refusals with others. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    ps = list(enumerate([5, 6], start=1))\n    return 0\n", "snippet.py(2,15): error PLY1802: 'enumerate([5, 6], start=1)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return sum(start=1)\n", "snippet.py(2,12): error PLY1802: 'sum(start=1)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return sum([1], 2, 3)\n", "snippet.py(2,12): error PLY1802: 'sum([1], 2, 3)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> float:\n    return sum([0.5, 1.5], n) + sum([n, 2], 0.5)\n", "snippet.py(2,12): error PLY1820: sequence operation 'sum of float values', which CPython adds with compensation, is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> str:\n    return sum([\"a\"], \"\")\n", "snippet.py(2,12): error PLY1820: sequence operation 'sum of str values', which CPython adds with compensation, is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return max()\n", "snippet.py(2,12): error PLY1820: sequence operation 'max()' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return max(1, 2, default=0)\n", "snippet.py(2,12): error PLY1820: sequence operation 'max(1, 2, default=0)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return max([1], cmp=0)\n", "snippet.py(2,21): error PLY1820: sequence operation 'max(cmp=…)' is not supported by the Python frontend")]
  public void Iterables_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A value is converted to the type its place holds: an optional returned, a member of an IntEnum as an int, a bool added to an int. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    o: int | None = n if n > 0 else None\n    return o\n", "f", "int 2", 2L)]
  [TestCase("def f(n: int) -> int:\n    o: int | None = n if n > 0 else None\n    return o\n", "f", "None", -2L)]
  [TestCase("from enum import IntEnum\n\nclass E(IntEnum):\n    A = 1\n    B = 2\n\ndef f(n: int) -> int:\n    return E.B\n", "f", "int 2", 2L)]
  [TestCase("def f(n: int) -> int:\n    b = n > 1\n    return n + b\n", "f", "int 3", 2L)]
  [TestCase("def f(n: int) -> int:\n    b = n > 1\n    return n + b\n", "f", "int 0", 0L)]
  public void Coercions_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A value is converted to the type its place holds: an optional returned, a member of an IntEnum as an int, a bool added to an int. Each is refused with exactly these diagnostics.</summary>
  [TestCase("from enum import IntEnum\n\nclass E(IntEnum):\n    A = 1\n    B = 2\n\ndef f(n: int) -> float:\n    x: float = 0.5\n    x = E.B\n    return x + E.A\n", "snippet.py(9,5): error PLY1803: 'f.x' holds values of the types float and E, which no type of the import holds both of")]
  public void Coercions_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>The truth of a tuple display is refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def g(n: int) -> list[int]:\n    return [n] * n\n\ndef f(n: int) -> int:\n    t = 0\n    if g(n):\n        t += 1\n    if (n, 1):\n        t += 2\n    if ():\n        t += 4\n    if [1, 2]:\n        t += 8\n    return t\n", "snippet.py(4,1): error PLY1802: 'the truth of a value of the type tuple[int, int]' is not supported by the Python frontend")]
  public void Truth_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>Comparisons of enum members, of bytes and bytearray, membership in bytes, None against optionals, and of mixed numbers. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 1\n    B = 2\n\ndef f(n: int) -> int:\n    e = E.A if n > 0 else E.B\n    t = 0\n    if e == E.A:\n        t += 1\n    if e != E.B:\n        t += 2\n    if b\"a\" == bytearray(b\"a\"):\n        t += 4\n    if 1 in b\"ab\" or 97 in b\"ab\":\n        t += 8\n    if True in b\"\\x01\":\n        t += 16\n    o: int | None = n if n > 0 else None\n    if None is o:\n        t += 32\n    if o == None:\n        t += 64\n    return t\n", "f", "int 31", 2L)]
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 1\n    B = 2\n\ndef f(n: int) -> int:\n    e = E.A if n > 0 else E.B\n    t = 0\n    if e == E.A:\n        t += 1\n    if e != E.B:\n        t += 2\n    if b\"a\" == bytearray(b\"a\"):\n        t += 4\n    if 1 in b\"ab\" or 97 in b\"ab\":\n        t += 8\n    if True in b\"\\x01\":\n        t += 16\n    o: int | None = n if n > 0 else None\n    if None is o:\n        t += 32\n    if o == None:\n        t += 64\n    return t\n", "f", "int 124", 0L)]
  [TestCase("def f(n: int) -> int:\n    x = 1.5\n    t = 0\n    if n == x:\n        t += 1\n    if 2 == 2.0:\n        t += 2\n    if True == 1:\n        t += 4\n    return t\n", "f", "int 6", 2L)]
  [TestCase("def f(n: int) -> int:\n    x = 1.5\n    t = 0\n    if n == x:\n        t += 1\n    if 2 == 2.0:\n        t += 2\n    if True == 1:\n        t += 4\n    return t\n", "f", "int 6", 0L)]
  public void Comparisons_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>Comparisons of enum members, of bytes and bytearray, membership in bytes, None against optionals, and of mixed numbers. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    o: int | None = n if n > 0 else None\n    p: int | None = 2 if n > 1 else None\n    t = 0\n    if o == p:\n        t += 1\n    if o < 5:\n        t += 2\n    return t\n", "snippet.py(5,8): error PLY1802: '== of the optional value of the type int | None; test it with 'is None' first' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> bool:\n    return [1] == (1,)\n", "snippet.py(2,12): error PLY1802: '== of the types list[int] and tuple[int]' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> bool:\n    return [n] == [] or [1.5] == [n]\n", "snippet.py(2,19): error PLY1801: the type of the elements of '[]' is not known; add a type hint such as 'list[int]' to what holds it")]
  public void Comparisons_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>An or and a conditional expression of an int and a str are refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    return n or \"a\"\n", "snippet.py(2,12): error PLY1803: 'n or \"a\"' gives values of types no type of the import holds both of")]
  [TestCase("def f(n: int) -> int:\n    return n if n else \"a\"\n", "snippet.py(2,24): error PLY1803: '\"a\"' gives a value of the type str where one of the type int is held")]
  public void Mixed_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A function that returns the None of a call returns None, also where its result is optional. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def g(n: int) -> None:\n    pass\n\ndef h(n: int) -> int | None:\n    if n > 1:\n        return g(n)\n    if n > 0:\n        return None\n    return n\n\ndef f(n: int) -> int:\n    r = h(n)\n    return -1 if r is None else r\n", "f", "int -1", 2L)]
  [TestCase("def g(n: int) -> None:\n    pass\n\ndef h(n: int) -> int | None:\n    if n > 1:\n        return g(n)\n    if n > 0:\n        return None\n    return n\n\ndef f(n: int) -> int:\n    r = h(n)\n    return -1 if r is None else r\n", "f", "int 0", 0L)]
  [TestCase("def g(n: int) -> None:\n    pass\n\ndef f(n: int) -> None:\n    return g(n)\n", "f", "None", 2L)]
  public void Returns_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A tuple assignment of attributes is refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("class C:\n    def __init__(self, a: int, b: int) -> None:\n        self.a = a\n        self.b = b\n\n    def swap(self) -> int:\n        self.a, self.b = self.b, self.a\n        return self.a * 10 + self.b\n\ndef f(n: int) -> int:\n    return C(n, 3).swap()\n", "snippet.py(7,9): error PLY1810: statement 'nested tuple assignment' is not supported by the Python frontend\nsnippet.py(7,17): error PLY1810: statement 'nested tuple assignment' is not supported by the Python frontend")]
  public void Unpacking_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A break in a try of a loop, in its handler or its else, skips the else of the loop. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    for i in [1, 2, 3]:\n        try:\n            if i == n:\n                break\n        except ValueError:\n            break\n        finally:\n            pass\n    else:\n        return -1\n    return n\n", "f", "int 2", 2L)]
  [TestCase("def f(n: int) -> int:\n    for i in [1, 2, 3]:\n        try:\n            if i == n:\n                break\n        except ValueError:\n            break\n        finally:\n            pass\n    else:\n        return -1\n    return n\n", "f", "int -1", 0L)]
  [TestCase("def f(n: int) -> int:\n    for i in [1, 2, 3]:\n        try:\n            pass\n        except ValueError:\n            break\n        else:\n            if i == n:\n                break\n    else:\n        return -1\n    return n\n", "f", "int 2", 2L)]
  [TestCase("def f(n: int) -> int:\n    for i in [1, 2, 3]:\n        try:\n            pass\n        except ValueError:\n            break\n        else:\n            if i == n:\n                break\n    else:\n        return -1\n    return n\n", "f", "int -1", 0L)]
  public void Breaks_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>struct packs and unpacks every letter in both byte orders, a format of bytes too, with counts up to 4096. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("import struct\n\ndef f(n: int) -> float:\n    b = struct.pack(\"<HhIiLlQqfd?\", n, -n, n, -n, n, -n, n, -n, 1.5, 2.25, True)\n    t = struct.unpack(\"<HhIiLlQqfd?\", b)\n    return t[0] + t[1] + t[2] + t[3] + t[4] + t[5] + t[6] + t[7] + t[8] + t[9] + t[10]\n", "f", "float 4.75", 2L)]
  [TestCase("import struct\n\ndef f(n: int) -> float:\n    b = struct.pack(\"<HhIiLlQqfd?\", n, -n, n, -n, n, -n, n, -n, 1.5, 2.25, True)\n    t = struct.unpack(\"<HhIiLlQqfd?\", b)\n    return t[0] + t[1] + t[2] + t[3] + t[4] + t[5] + t[6] + t[7] + t[8] + t[9] + t[10]\n", "f", "float 4.75", 0L)]
  [TestCase("import struct\n\ndef f(n: int) -> float:\n    b = struct.pack(\">fdHq\", 1.5, 2.25, n, -n)\n    t = struct.unpack(\">fdHq\", b)\n    return t[0] + t[1] + t[2] + t[3]\n", "f", "float 3.75", 2L)]
  [TestCase("import struct\n\ndef f(n: int) -> int:\n    return struct.calcsize(\"<4096b\")\n", "f", "int 4096", 2L)]
  public void Struct_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>struct packs and unpacks every letter in both byte orders, a format of bytes too, with counts up to 4096. Each is refused with exactly these diagnostics.</summary>
  [TestCase("import struct\n\ndef f(n: int) -> int:\n    b = struct.pack(b\"<h\", n)\n    return struct.unpack(b\"<h\", b)[0] + struct.unpack_from(\"<b\", b, offset=1)[0] + struct.calcsize(\"<99999b\")\n", "snippet.py(5,100): error PLY1823: struct format or array typecode '\"<99999b\"' is not supported: a count beyond 4096")]
  [TestCase("import struct\n\ndef f(n: int) -> int:\n    return struct.calcsize(\"<99999999999999999999b\")\n", "snippet.py(4,28): error PLY1823: struct format or array typecode '\"<99999999999999999999b\"' is not supported: a count beyond 4096")]
  [TestCase("import struct\n\ndef f(n: int) -> int:\n    return struct.calcsize(\"<4097b\")\n", "snippet.py(4,28): error PLY1823: struct format or array typecode '\"<4097b\"' is not supported: a count beyond 4096")]
  [TestCase("import struct\n\ndef f(n: int) -> int:\n    return struct.unpack(\"<h\", [1, 2])[0]\n", "snippet.py(4,12): error PLY1802: 'struct.unpack of a value of the type list[int]' is not supported by the Python frontend")]
  [TestCase("import struct\n\ndef f(n: int) -> int:\n    return struct.unpack_from(\"<h\", [1, 2])[0]\n", "snippet.py(4,12): error PLY1802: 'struct.unpack_from of a value of the type list[int]' is not supported by the Python frontend")]
  public void Struct_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>bytes and bytearray of lists, bools, counts and ranges; str.encode of UTF-8 only. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    a = bytes([1, 2]) + bytes([True, False]) + bytes(n) + bytearray(range(3))\n    return len(a) + a[0]\n", "f", "int 10", 2L)]
  [TestCase("def f(n: int) -> int:\n    return len(\"é\".encode(\"UTF_8\")) + len(bytes(\"é\", \"utf-8\"))\n", "f", "int 4", 2L)]
  public void Bytes_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>bytes and bytearray of lists, bools, counts and ranges; str.encode of UTF-8 only. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    a = bytes(\"ab\")\n    return 0\n", "snippet.py(2,9): error PLY1802: 'bytes(\"ab\") of a str without an encoding, which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    a = bytes(n, \"utf-8\")\n    return 0\n", "snippet.py(2,9): error PLY1802: 'bytes(n, \"utf-8\") of a value of the type int with an encoding, which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    a = bytes(1.5)\n    return 0\n", "snippet.py(2,9): error PLY1802: 'bytes(1.5) of a value of the type float' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    a = bytes(b\"a\", \"x\", \"y\")\n    return 0\n", "snippet.py(2,9): error PLY1802: 'bytes(b\"a\", \"x\", \"y\")' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    a = bytes(source=b\"a\")\n    return 0\n", "snippet.py(2,9): error PLY1802: 'bytes(source=b\"a\")' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return len(\"a\".encode(\"latin-1\"))\n", "snippet.py(2,27): error PLY1822: string operation 'encode to \"latin-1\"' is not supported by the Python frontend; UTF-8 is")]
  [TestCase("def f(n: int) -> int:\n    e = \"utf-8\"\n    return len(\"a\".encode(e))\n", "snippet.py(3,27): error PLY1822: string operation 'encode to e' is not supported by the Python frontend; UTF-8 is")]
  [TestCase("def f(n: int) -> int:\n    return len(\"a\".encode(\"utf-8\", \"strict\"))\n", "snippet.py(2,16): error PLY1822: string operation 'encode with an error handler' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return len(\"a\".encode(errors=\"strict\"))\n", "snippet.py(2,16): error PLY1822: string operation 'encode with an error handler' is not supported by the Python frontend")]
  public void Bytes_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>Attributes hold tuples, optional objects, bytes and bools; a str or a tuple of a str is refused. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("class C:\n    def __init__(self, n: int) -> None:\n        self.t = (n, 1.5)\n        self.o: C | None = None\n        self.b = b\"x\"\n        self.f = True\n\ndef f(n: int) -> float:\n    c = C(n)\n    c.o = C(n + 1)\n    return c.t[0] + c.t[1] + (c.o.t[0] if c.o is not None else 0) + len(c.b) + c.f\n", "f", "float 8.5", 2L)]
  public void Records_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>Attributes hold tuples, optional objects, bytes and bools; a str or a tuple of a str is refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("class C:\n    def __init__(self, n: int) -> None:\n        self.s = \"a\"\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(3,9): error PLY1840: 'the attribute s of C of the type str' is outside the first Python subset (dataclasses, simple final classes, enums): an attribute holds a number, a truth value, bytes, a tuple of numbers, an enum member or an object")]
  [TestCase("class C:\n    def __init__(self, n: int) -> None:\n        self.t = (n, \"a\")\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(3,9): error PLY1840: 'the attribute t of C of the type tuple[int, str]' is outside the first Python subset (dataclasses, simple final classes, enums): an attribute holds a number, a truth value, bytes, a tuple of numbers, an enum member or an object")]
  public void Records_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>Module-level values: __all__, chained assignment, tables of enum members, globals of numbers, constant defaults; mutable defaults and values that raise are refused. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("__all__ = [\"f\", 3]\n\ndef f(n: int) -> int:\n    return n\n", "f", "int 2", 2L)]
  [TestCase("A = B = 3\n\ndef f(n: int) -> int:\n    return A + B + n\n", "f", "int 8", 2L)]
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 1\n    B = 2\n\nT = [E.A, E.B]\nG = 5\n\ndef f(n: int) -> int:\n    global G\n    G += n\n    return T[1].value + G\n", "f", "int 9", 2L)]
  [TestCase("G = 1\n\ndef f(n: int) -> float:\n    global G\n    G = 2.5\n    return G\n", "f", "float 2.5", 2L)]
  [TestCase("T = (1, 2)\nU = -(3)\nV = +4\n\ndef f(n: int, d: tuple[int, int] = T) -> int:\n    return d[1] + U + V\n\ndef g(n: int) -> int:\n    return f(n) + f(n, (5, 6))\n", "g", "int 10", 2L)]
  [TestCase("X: int = 5\nY: float = 2\n\ndef f(n: int) -> float:\n    return X + Y + n\n", "f", "int 9", 2L)]
  [TestCase("__all__ = [\"f\", \"g\"]\n\ndef f(n: int) -> int:\n    return n\n\ndef g(n: int) -> int:\n    return n\n", "f", "int 2", 2L)]
  public void ModuleValues_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>Module-level values: __all__, chained assignment, tables of enum members, globals of numbers, constant defaults; mutable defaults and values that raise are refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("A, B = 3\n\ndef f(n: int) -> int:\n    return A\n", "snippet.py(1,8): error PLY1810: statement 'module-level unpacking of 3' is not supported by the Python frontend")]
  [TestCase("def g() -> int:\n    return 1\n\nA, B = g(), 2\n\ndef f(n: int) -> int:\n    return A\n", "snippet.py(4,8): error PLY1810: statement 'module-level call of g, which runs code of the module when it is imported,' is not supported by the Python frontend")]
  [TestCase("A, B = (1, 2)[0], 2\nC, D = [1, 2]\n\ndef f(n: int) -> int:\n    return A\n", "snippet.py(2,8): error PLY1810: statement 'module-level unpacking of [1, 2]' is not supported by the Python frontend")]
  [TestCase("G = \"a\"\n\ndef f(n: int) -> int:\n    global G\n    G = \"b\"\n    return 0\n", "snippet.py(1,1): error PLY1810: statement 'module variable G of the type str' is not supported by the Python frontend: a variable functions assign through global holds a number or a truth value")]
  [TestCase("T = [1, 2]\n\ndef f(n: int, d: list[int] = T) -> int:\n    return 0\n", "snippet.py(3,30): error PLY1834: the default of parameter 'd' is a mutable object shared across calls")]
  [TestCase("T = (1, [2])\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(1,5): error PLY1802: 'a tuple of the types int, list[int]; a tuple holds numbers, bytes, enum members, objects and tuples of them' is not supported by the Python frontend")]
  [TestCase("T = 1 // 0\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(1,5): error PLY1810: statement 'module-level 1 // 0, which raises ZeroDivisionError when the module is imported,' is not supported by the Python frontend")]
  [TestCase("T = [\"a\"]\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(1,5): error PLY1810: statement 'module-level [\"a\"]' of the type list[str] is not supported by the Python frontend: a module-level list holds ints, floats or members of an enum")]
  [TestCase("def f(n: int, d: bytearray = bytearray(b\"x\")) -> int:\n    return 0\n", "snippet.py(1,30): error PLY1834: the default of parameter 'd' is a mutable object shared across calls")]
  [TestCase("def f(n: int, d: bytearray = bytearray()) -> int:\n    return 0\n", "snippet.py(1,30): error PLY1834: the default of parameter 'd' is a mutable object shared across calls")]
  [TestCase("def f(n: int, d: list[int] = [i for i in range(2)]) -> int:\n    return 0\n", "snippet.py(1,30): error PLY1834: the default of parameter 'd' is a mutable object shared across calls")]
  [TestCase("def f(n: int, d: list[int] = list()) -> int:\n    return 0\n", "snippet.py(1,30): error PLY1834: the default of parameter 'd' is a mutable object shared across calls")]
  [TestCase("import struct\n\nT = struct.unpack(\"<h\", b\"a\")\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(3,5): error PLY1810: statement 'module-level struct.unpack(\"<h\", b\"a\"), which raises struct.error when the module is imported,' is not supported by the Python frontend")]
  [TestCase("from math import (sqrt,\n    sin)\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(2,5): error PLY1833: 'math.sin' is neither part of this import nor in the Python call map")]
  public void ModuleValues_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>An exception class raised with and without a message; a yield inside try with except is refused; a comprehension inside try. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("class Oops(Exception):\n    pass\n\ndef f(n: int) -> int:\n    try:\n        if n > 1:\n            raise Oops(\"big\")\n        raise Oops\n    except Oops as e:\n        return len(str(e))\n", "f", "int 3", 2L)]
  [TestCase("def f(n: int) -> int:\n    try:\n        x = [i for i in range(n)]\n    except ValueError:\n        return -1\n    return len(x)\n", "f", "int 2", 2L)]
  [TestCase("def f(n: int) -> int:\n    try:\n        x = [i for i in range(n)]\n    except ValueError:\n        return -1\n    return len(x)\n", "f", "int 0", 0L)]
  public void Exceptions_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>An exception class raised with and without a message; a yield inside try with except is refused; a comprehension inside try. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def g(n: int):\n    try:\n        yield n\n    except ValueError:\n        yield -1\n\ndef f(n: int) -> int:\n    return sum(g(n))\n", "snippet.py(2,5): error PLY1832: the function value 'g' escapes, or 'g' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported (a yield inside a try with except: #760)")]
  public void Exceptions_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>Definite assignment through while True, finally and a false constant condition. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    while True:\n        if n > 1:\n            x = 1\n            break\n        x = 2\n        break\n    return x\n", "f", "int 1", 2L)]
  [TestCase("def f(n: int) -> int:\n    while True:\n        if n > 1:\n            x = 1\n            break\n        x = 2\n        break\n    return x\n", "f", "int 2", 0L)]
  [TestCase("def f(n: int) -> int:\n    try:\n        x = 1 // n\n    finally:\n        y = 2\n    return x + y\n", "f", "int 2", 2L)]
  [TestCase("def f(n: int) -> int:\n    try:\n        x = 1 // n\n    finally:\n        y = 2\n    return x + y\n", "f", "raise ZeroDivisionError", 0L)]
  [TestCase("def f(n: int) -> int:\n    while b\"\":\n        x = n\n    return 1\n", "f", "int 1", 2L)]
  [TestCase("def f(n: int) -> int:\n    while b\"\":\n        x = n\n    return 1\n", "f", "int 1", 0L)]
  public void Assignment_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>Definite assignment through while True, finally and a false constant condition. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    while \"a\":\n        x = n\n        break\n    return x\n", "snippet.py(5,12): error PLY1811: 'x' may be unbound here; python would raise UnboundLocalError")]
  public void Assignment_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>Lone surrogates and astral characters in str literals, and nested signs of constants. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    return len(\"\\ud800\") + len(\"a\\udc00b\") + -(-(+3)) + len(\"\\U0001F600\")\n", "f", "int 8", 2L)]
  public void Constants_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>The math functions and constants of the call map; others of math, os and star imports are refused. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("import math\n\ndef f(n: int) -> float:\n    return math.tau + math.e + math.fabs(-1.5) + math.copysign(2.0, -1.0) + math.trunc(2.5) + math.ceil(1.5) + math.isinf(math.inf) + math.isfinite(1.0) + math.isnan(math.nan)\n", "f", "float 15.50146713563863", 2L)]
  public void Library_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>The math functions and constants of the call map; others of math, os and star imports are refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("import math\n\ndef f(n: int) -> float:\n    return math.sin(1.0)\n", "snippet.py(4,12): error PLY1833: 'math.sin' is neither part of this import nor in the Python call map")]
  [TestCase("from math import sin\n\ndef f(n: int) -> float:\n    return 0.0\n", "snippet.py(1,1): error PLY1833: 'math.sin' is neither part of this import nor in the Python call map")]
  [TestCase("from os import path\n\ndef f(n: int) -> float:\n    return 0.0\n", "snippet.py(1,1): error PLY1833: 'os' is neither part of this import nor in the Python call map")]
  [TestCase("from math import *\n\ndef f(n: int) -> float:\n    return 0.0\n", "snippet.py(1,1): error PLY1833: 'from math import *' is neither part of this import nor in the Python call map: a star import")]
  public void Library_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A base that is a subscript other than Generic, a field that is no name, a class variable, a method with ** or type parameters are refused; __init__ assigns attributes of other objects and locals beside its own. Each is refused with exactly these diagnostics.</summary>
  [TestCase("class C(list[int]):\n    def __init__(self) -> None:\n        self.v = 1\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(1,9): error PLY1840: 'the base class list[int] of C' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #762")]
  [TestCase("from dataclasses import dataclass\n\n@dataclass\nclass C:\n    v: int\n    C.w: int = 1\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(6,5): error PLY1840: 'the field C.w of C' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481")]
  [TestCase("from dataclasses import dataclass\nfrom typing import ClassVar\n\n@dataclass\nclass C:\n    v: int\n    k: ClassVar = 1\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(7,5): error PLY1840: 'the class attribute k of C' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481")]
  [TestCase("from dataclasses import dataclass\nfrom typing import ClassVar\n\n@dataclass\nclass C:\n    v: int\n    k: ClassVar[int] = 1\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(7,5): error PLY1840: 'the class attribute k of C' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481")]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\n    def m(self, **k: int) -> int:\n        return 1\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(5,5): error PLY1830: '**k of m' is not supported for functions by the Python frontend")]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\n    def m[T](self, x: T) -> int:\n        return 1\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(5,5): error PLY1830: 'a type parameter of m' is not supported for functions by the Python frontend")]
  public void ClassShapes_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A base that is a subscript other than Generic, a field that is no name, a class variable, a method with ** or type parameters are refused; __init__ assigns attributes of other objects and locals beside its own. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("class D:\n    def __init__(self) -> None:\n        self.w = 0\n\nclass C:\n    def __init__(self, d: D) -> None:\n        self.v = 1\n        x = 2\n        d.w = x\n        self.v += x\n\ndef f(n: int) -> int:\n    d = D()\n    c = C(d)\n    return c.v + d.w\n", "f", "int 5", 2L)]
  public void ClassShapes_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A yield in branches, in the else of a loop and in a try with finally makes a generator; a function nested in a generator is no part of it. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def g(n: int):\n    if n > 1:\n        yield 1\n    else:\n        yield 2\n    for i in range(n):\n        pass\n    else:\n        yield 3\n    try:\n        yield 4\n    finally:\n        pass\n\ndef f(n: int) -> int:\n    return sum(g(n))\n", "f", "int 8", 2L)]
  [TestCase("def g(n: int):\n    if n > 1:\n        yield 1\n    else:\n        yield 2\n    for i in range(n):\n        pass\n    else:\n        yield 3\n    try:\n        yield 4\n    finally:\n        pass\n\ndef f(n: int) -> int:\n    return sum(g(n))\n", "f", "int 9", 0L)]
  [TestCase("def g(n: int):\n    def h() -> int:\n        return 5\n    yield h()\n\ndef f(n: int) -> int:\n    return sum(g(n))\n", "f", "int 5", 2L)]
  public void Generators_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>Module-level values: __all__, chained assignment, tables of enum members, globals of numbers, constant defaults; mutable defaults and values that raise are refused. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("import helper\n\nhelper_T = [1, 2]\n\ndef f(n: int) -> int:\n    return helper_T[1] + helper.T[0] + n\n", "T = [10, 20]\n", "f", "int 14", 2L)]
  public void ModuleValues_RunAsCPython_WithHelper(string source, string helper, string function, string expected, params object[] arguments)
    => Assert.That(RunWithHelper(source, helper, function, arguments), Is.EqualTo(expected));

  /// <summary>Module-level values: __all__, chained assignment, tables of enum members, globals of numbers, constant defaults; mutable defaults and values that raise are refused. Each is refused with exactly these diagnostics.</summary>
  [TestCase("from helper import nope\n\ndef f(n: int) -> int:\n    return 0\n", "def g(n: int) -> int:\n    return n\n", "snippet.py(1,1): error PLY1833: 'helper.nope' is neither part of this import nor in the Python call map: 'helper' has no function or class 'nope'")]
  public void ModuleValues_AreRefused_WithHelper(string source, string helper, string expected) {
    var result = WithHelper(source, helper);
    Assert.That(result.Module, Is.Null);
    Assert.That(string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString())), Is.EqualTo(expected));
  }

  /// <summary>A type hint names a class of the module or of an imported module, a typing alias, or a built-in type of the import. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("import helper\nfrom helper import B\n\ndef f(n: int) -> int:\n    return g(helper.B(n), B(1))\n\ndef g(a: helper.B, b: B) -> int:\n    return a.v + b.v\n", "class B:\n    def __init__(self, v: int) -> None:\n        self.v = v\n", "f", "int 3", 2L)]
  [TestCase("import helper\n\ndef f(n: int) -> int:\n    b: helper.B = helper.B(n)\n    return b.v\n", "class B:\n    def __init__(self, v: int) -> None:\n        self.v = v\n", "f", "int 3", 3L)]
  [TestCase("from helper import B\n\ndef f(n: int) -> int:\n    b: B = B(n)\n    return b.v\n", "class B:\n    def __init__(self, v: int) -> None:\n        self.v = v\n", "f", "int 3", 3L)]
  public void Hints_RunAsCPython_WithHelper(string source, string helper, string function, string expected, params object[] arguments)
    => Assert.That(RunWithHelper(source, helper, function, arguments), Is.EqualTo(expected));

  /// <summary>A type hint names a class of the module or of an imported module, a typing alias, or a built-in type of the import. Each is refused with exactly these diagnostics.</summary>
  [TestCase("import helper\n\ndef g(a: helper.Nope) -> int:\n    return 0\n", "class B:\n    def __init__(self, v: int) -> None:\n        self.v = v\n", "snippet.py(3,10): error PLY1801: the type hint 'helper.Nope' of 'a' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("from helper import g\n\ndef f(a: g) -> int:\n    return 0\n", "def g(v: int) -> int:\n    return v\n", "snippet.py(3,10): error PLY1801: the type hint 'g' of 'a' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  public void Hints_AreRefused_WithHelper(string source, string helper, string expected) {
    var result = WithHelper(source, helper);
    Assert.That(result.Module, Is.Null);
    Assert.That(string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString())), Is.EqualTo(expected));
  }

  /// <summary>A type hint names a class of the module or of an imported module, a typing alias, or a built-in type of the import. Each is refused with exactly these diagnostics.</summary>
  [TestCase("import math\n\ndef g(a: math.pi) -> int:\n    return 0\n", "snippet.py(3,10): error PLY1801: the type hint 'math.pi' of 'a' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("def f(n: tuple) -> int:\n    return 0\n", "snippet.py(1,10): error PLY1801: the type hint 'tuple' of 'n' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("def f(n: list[int][0]) -> int:\n    return 0\n", "snippet.py(1,10): error PLY1801: the type hint 'list[int][0]' of 'n' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("from typing import Union\n\ndef f(n: Union[int, str]) -> int:\n    return 0\n", "snippet.py(3,10): error PLY1801: the type hint 'Union[int, str]' of 'n' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("from typing import Optional\n\ndef f(n: Optional[int, str]) -> int:\n    return 0\n", "snippet.py(3,10): error PLY1801: the type hint 'Optional[int, str]' of 'n' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("def f(n: None) -> int:\n    return 0\n", "snippet.py(1,10): error PLY1801: the type hint 'None' of 'n' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("def f(n: int) -> 5:\n    pass\n", "snippet.py(1,18): error PLY1801: the return type hint '5' of 'f' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module and None")]
  [TestCase("import math\n\ndef f(n: math.x) -> int:\n    return 0\n", "snippet.py(3,10): error PLY1801: the type hint 'math.x' of 'n' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("def f(n: dict[int]) -> int:\n    return 0\n", "snippet.py(1,10): error PLY1801: the type hint 'dict[int]' of 'n' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("def f(n: list[int, int]) -> int:\n    return 0\n", "snippet.py(1,10): error PLY1801: the type hint 'list[int, int]' of 'n' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("def f(n: frozenset[int]) -> int:\n    return 0\n", "snippet.py(1,10): error PLY1801: the type hint 'frozenset[int]' of 'n' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("from typing import Final\n\ndef f(n: Final[int]) -> int:\n    return 0\n", "snippet.py(3,10): error PLY1801: the type hint 'Final[int]' of 'n' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("def f(n: int | str) -> int:\n    return 0\n", "snippet.py(1,10): error PLY1801: the type hint 'int | str' of 'n' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("def f(n: tuple[int, ...]) -> int:\n    return 0\n", "snippet.py(1,10): error PLY1801: the type hint 'tuple[int, ...]' of 'n' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  [TestCase("def f(n: tuple[()]) -> int:\n    return 0\n", "snippet.py(1,10): error PLY1801: the type hint 'tuple[()]' of 'n' is not supported; supported are int, float, bool, str, bytes, bytearray, list[T], tuple[…], dict[K, V], set[K] (K an int, a bool, a str or a plain class), T | None and the classes of the module")]
  public void Hints_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A type hint names a class of the module or of an imported module, a typing alias, or a built-in type of the import. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("from typing import Iterator, Generator, Iterable, Optional, List, Tuple\nimport typing\nfrom collections.abc import Iterator as It\n\ndef g(n: int) -> typing.Iterator[int]:\n    yield n\n\ndef h(n: int) -> It[int]:\n    yield n\n\ndef k(n: int) -> Generator[int, None, None]:\n    yield n\n\ndef f(n: int) -> int:\n    a: typing.List[int] = [n]\n    b: typing.Optional[int] = n\n    c: Optional[bool] = None\n    return sum(g(n)) + sum(h(n)) + sum(k(n)) + a[0] + (b or 0)\n", "f", "int 10", 2L)]
  [TestCase("def f(n: int) -> int:\n    a: list = [n]\n    return 0\n", "f", "int 0", 2L)]
  [TestCase("def f(n: int) -> None:\n    pass\n", "f", "None", 2L)]
  [TestCase("from typing import List, Tuple, Optional, Union\n\ndef f(n: int) -> int:\n    a: List[int] = [n]\n    b: Tuple[int, int] = (n, 1)\n    c: Optional[int] = None\n    d: Union[int, None] = n\n    return a[0] + b[1] + (d or 0)\n", "f", "int 7", 3L)]
  public void Hints_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A dict of lists takes its element type from a hint and is stored into and appended to. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    d: dict[int, list[int]] = {}\n    d[n] = []\n    d[n].append(5)\n    return len(d[n])\n", "f", "int 1", 2L)]
  public void Dicts_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A nested function reads the module's names through every enclosing scope; a default is evaluated where the function is defined. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    def g(k: int) -> int:\n        return sum([x + k for x in range(n)]) + max(map(lambda y: y + k, [1, 2]))\n    return g(1)\n", "f", "int 6", 2L)]
  [TestCase("G = 5\n\ndef f(n: int) -> int:\n    def g() -> int:\n        def h() -> int:\n            return G\n        return h()\n    return g()\n", "f", "int 5", 1L)]
  [TestCase("G = 5\n\ndef f(n: int) -> int:\n    global G\n    def g() -> int:\n        return G + n\n    G = 7\n    return g()\n", "f", "int 8", 1L)]
  [TestCase("def f(n: int) -> int:\n    def g(k: int = max(1, 2)) -> int:\n        return k\n    return g(n)\n", "f", "int 3", 3L)]
  public void Scope_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>The built-ins are called with the arguments they take, and refused with others. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    return int() + len(str()) + int(bool()) + int(float())\n", "f", "int 0", 1L)]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 7\n\n    def m(self) -> int:\n        return self.v\n\ndef f(n: int) -> int:\n    o: C | None = C() if n > 0 else None\n    return o.m()\n", "f", "raise AttributeError", 0L)]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 7\n\n    def m(self) -> int:\n        return self.v\n\ndef f(n: int) -> int:\n    o: C | None = C() if n > 0 else None\n    return o.m()\n", "f", "int 7", 1L)]
  [TestCase("def f(n: int) -> int:\n    b = True\n    s = \"a\"\n    a = bytearray(b\"x\")\n    xs = [1]\n    t = (1, 2)\n    x = 1.5\n    return int(isinstance(b, bool)) + int(isinstance(s, str)) + int(isinstance(a, bytearray)) + int(isinstance(xs, list)) + int(isinstance(t, tuple)) + int(isinstance(n, bool)) + int(isinstance(x, (bool, str))) + int(isinstance(n, int))\n", "f", "int 6", 1L)]
  [TestCase("def f(x: float) -> int:\n    return round(x) + int(x) + int(abs(x))\n", "f", "int 6", 2.5)]
  [TestCase("def f(x: float) -> int:\n    return round(x) + int(x) + int(abs(x))\n", "f", "int -4", -3.5)]
  public void Calls_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>The built-ins are called with the arguments they take, and refused with others. Each is refused with exactly these diagnostics.</summary>
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 7\n\ndef f(n: int) -> int:\n    return C().nope()\n", "snippet.py(6,12): error PLY1844: attribute 'nope' of class 'C' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported")]
  [TestCase("def f(b: bytes) -> int:\n    return b.frob()\n", "snippet.py(2,12): error PLY1822: string operation 'frob' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    xs = [1]\n    return xs.frob()\n", "snippet.py(3,12): error PLY1820: sequence operation 'frob' with these arguments is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> bool:\n    return isinstance(n, complex)\n", "snippet.py(2,12): error PLY1802: 'isinstance of a value of the type int against complex' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> bool:\n    return isinstance(n + 1, int)\n", "snippet.py(2,12): error PLY1802: 'isinstance of n + 1, which has to be evaluated' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> bool:\n    return isinstance(n, n.x)\n", "snippet.py(2,12): error PLY1802: 'isinstance against n.x' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return pow(n)\n", "snippet.py(2,12): error PLY1802: 'pow(n)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return len(n, n)\n", "snippet.py(2,12): error PLY1802: 'len(n, n)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    return abs(n, key=1)\n", "snippet.py(2,12): error PLY1802: 'abs(n, key=1)' is not supported by the Python frontend")]
  [TestCase("def f(n: int) -> int:\n    for i in range(1, 2, 3, 4):\n        pass\n    return 0\n", "snippet.py(2,14): error PLY1813: iteration over 'range(1, 2, 3, 4)' is not supported")]
  [TestCase("def f(n: int) -> str:\n    return str(n)\n", "snippet.py(2,12): error PLY1822: string operation 'str() of a value of the type int' is not supported by the Python frontend")]
  [TestCase("def f(s: str) -> int:\n    return round(s)\n", "snippet.py(2,12): error PLY1802: 'round() of a value of the type str' is not supported by the Python frontend")]
  [TestCase("def f(n: float) -> int:\n    return round(n, 2)\n", "snippet.py(2,12): error PLY1802: 'round(n, 2)' is not supported by the Python frontend")]
  [TestCase("def f(s: str) -> int:\n    return ord(5)\n", "snippet.py(2,12): error PLY1802: 'ord() of a value of the type int' is not supported by the Python frontend")]
  [TestCase("def f(s: str) -> str:\n    return chr(s)\n", "snippet.py(2,12): error PLY1802: 'chr() of a value of the type str' is not supported by the Python frontend")]
  public void Calls_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>The loops, comprehensions and match statements are lowered for every form they take, and refused for the others. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("def f(n: int) -> int:\n    return sum([i for i in range(n)]) + sum(i * 2 for i in range(n))\n", "f", "int 9", 3L)]
  [TestCase("def f(n: int) -> int:\n    t = 0\n    for i in range(n):\n        if (k := i * 2) > 2:\n            t += k\n        try:\n            t += 1\n        except ValueError:\n            i = 5\n    return t\n", "f", "int 7", 3L)]
  [TestCase("def f(n: int) -> int:\n    o: int | None = n if n > 0 else None\n    match o:\n        case None:\n            return -1\n        case _:\n            return 1\n", "f", "int 1", 1L)]
  [TestCase("def f(n: int) -> int:\n    o: int | None = n if n > 0 else None\n    match o:\n        case None:\n            return -1\n        case _:\n            return 1\n", "f", "int -1", 0L)]
  [TestCase("def f(n: int) -> int:\n    match n:\n        case True:\n            return 5\n        case None:\n            return 6\n    return 0\n", "f", "int 0", 1L)]
  [TestCase("def f(n: int) -> int:\n    match n:\n        case True:\n            return 5\n        case None:\n            return 6\n    return 0\n", "f", "int 0", 0L)]
  [TestCase("def f(b: bool) -> int:\n    match b:\n        case False:\n            return 5\n        case None:\n            return 6\n    return 0\n", "f", "int 0", true)]
  [TestCase("def f(b: bool) -> int:\n    match b:\n        case False:\n            return 5\n        case None:\n            return 6\n    return 0\n", "f", "int 5", false)]
  [TestCase("def f(n: int) -> int:\n    t = 0\n    for i, x in enumerate([5, 6], n):\n        t += i * x\n    return t\n", "f", "int 39", 3L)]
  [TestCase("def f(n: int) -> int:\n    t = 0\n    for x in (n, n + 1):\n        t += x\n    for x in b\"ab\":\n        t += x\n    for x in reversed((1, 2)):\n        t += x\n    return t\n", "f", "int 203", 2L)]
  public void Control_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>The loops, comprehensions and match statements are lowered for every form they take, and refused for the others. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    for a, b, c in [(1, 2)]:\n        pass\n    return 0\n", "snippet.py(2,9): error PLY1810: statement 'unpacking a value of the type tuple[int, int] into 3 names, which raises TypeError or ValueError,' is not supported by the Python frontend\nsnippet.py(2,9): error PLY1801: the type of 'a' in 'f' is not known: no value of a known type reaches it\nsnippet.py(2,12): error PLY1801: the type of 'b' in 'f' is not known: no value of a known type reaches it\nsnippet.py(2,15): error PLY1801: the type of 'c' in 'f' is not known: no value of a known type reaches it")]
  [TestCase("def f(n: int) -> int:\n    for i in enumerate([1]):\n        pass\n    return 0\n", "snippet.py(2,14): error PLY1813: iteration over 'enumerate([1])' is not supported: enumerate takes one sequence and a start, its target two names")]
  [TestCase("def f(n: int) -> int:\n    for i, x in enumerate([1], 0, 1):\n        pass\n    return 0\n", "snippet.py(2,17): error PLY1813: iteration over 'enumerate([1], 0, 1)' is not supported")]
  [TestCase("def f(s: str) -> int:\n    for i, x in enumerate(s):\n        pass\n    return 0\n", "snippet.py(2,17): error PLY1813: iteration over 'enumerate(s)' is not supported: enumerate of the type str")]
  [TestCase("def f(n: int) -> int:\n    for x in zip([1], [2]):\n        pass\n    return 0\n", "snippet.py(2,14): error PLY1820: sequence operation 'zip of other than two sequences into two names' is not supported by the Python frontend")]
  [TestCase("def f(s: str) -> int:\n    for a, b in zip([1], s):\n        pass\n    return 0\n", "snippet.py(2,26): error PLY1813: iteration over 'zip([1], s)' is not supported: zip of the type str")]
  [TestCase("def f(s: str) -> int:\n    for a in reversed(s):\n        pass\n    return 0\n", "snippet.py(2,14): error PLY1813: iteration over 'reversed(s)' is not supported: reversed of the type str")]
  [TestCase("def f(n: int) -> int:\n    for a in reversed([1], [2]):\n        pass\n    return 0\n", "snippet.py(2,14): error PLY1813: iteration over 'reversed([1], [2])' is not supported")]
  [TestCase("def f(n: int) -> int:\n    for a in range(1.5):\n        pass\n    return 0\n", "snippet.py(2,20): error PLY1813: iteration over 'range(1.5)' is not supported: a float argument raises TypeError")]
  [TestCase("def f(n: int) -> int:\n    for a in range(n, step=1):\n        pass\n    return 0\n", "snippet.py(2,14): error PLY1813: iteration over 'range(n, step=1)' is not supported")]
  [TestCase("def f(n: int) -> int:\n    for a, b in range(n):\n        pass\n    return 0\n", "snippet.py(2,9): error PLY1810: statement 'unpacking a value of the type int into 2 names, which raises TypeError or ValueError,' is not supported by the Python frontend\nsnippet.py(2,9): error PLY1801: the type of 'a' in 'f' is not known: no value of a known type reaches it\nsnippet.py(2,12): error PLY1801: the type of 'b' in 'f' is not known: no value of a known type reaches it")]
  [TestCase("def f(n: int) -> int:\n    for a in n:\n        pass\n    return 0\n", "snippet.py(2,9): error PLY1801: the type of 'a' in 'f' is not known: no value of a known type reaches it\nsnippet.py(2,14): error PLY1813: iteration over 'n' of the type int is not supported")]
  public void Control_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>The bases, decorators and members of a class are those of the subset; attributes assigned anywhere in __init__ are fields. Each is refused with exactly these diagnostics.</summary>
  [TestCase("class C(metaclass=type):\n    def __init__(self) -> None:\n        self.v = 1\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(1,9): error PLY1840: 'metaclass= in the bases of C' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #751")]
  [TestCase("class C(x=1):\n    def __init__(self) -> None:\n        self.v = 1\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(1,9): error PLY1840: 'x= in the bases of C' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481")]
  [TestCase("from dataclasses import dataclass\n\n@dataclass(frozen=1)\nclass C:\n    v: int\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(3,2): error PLY1840: '@dataclass(frozen=…) of a value other than True or False' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481")]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\n    @staticmethod\n    @property\n    def m() -> int:\n        return 1\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(7,5): error PLY1840: 'the method m of C without its receiver' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481")]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\n    @classmethod\n    def m(cls) -> int:\n        return 1\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(5,6): error PLY1840: 'the decorator classmethod of C.m' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481")]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\n    @x.y\n    def m(self) -> int:\n        return 1\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(5,6): error PLY1840: 'the decorator x.y of C.m' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481")]
  [TestCase("class C:\n    def __init__(self) -> None:\n        self.v = 1\n\n    def m(self, *a: int) -> int:\n        return 1\n\ndef f(n: int) -> int:\n    return 0\n", "snippet.py(5,5): error PLY1830: '*a of m' is not supported for functions by the Python frontend")]
  public void Classes_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>The bases, decorators and members of a class are those of the subset; attributes assigned anywhere in __init__ are fields. Each imports into IR that verifies.</summary>
  [TestCase("from dataclasses import dataclass\n\n@dataclass\nclass C:\n    v: int\n    (w): int\n\ndef f(n: int) -> int:\n    return 0\n")]
  public void Classes_Import(string source)
    => Assert.That(PythonSnippet.Module(source).Functions, Is.Not.Empty);

  /// <summary>The bases, decorators and members of a class are those of the subset; attributes assigned anywhere in __init__ are fields. Every value is the one CPython 3.14 gives.</summary>
  [TestCase("class C:\n    def __init__(self, n: int) -> None:\n        self.v = 1\n        self.w: int = n\n        self.v += n\n        if n > 0:\n            self.u = 2\n        else:\n            self.u = 3\n        try:\n            self.t = 4\n        except ValueError:\n            self.t = 5\n\n    def m(self) -> int:\n        return self.v + self.w + self.u + self.t\n\ndef f(n: int) -> int:\n    return C(n).m()\n", "f", "int 9", 1L)]
  [TestCase("class C:\n    def __init__(self, n: int) -> None:\n        self.v = 1\n        try:\n            pass\n        except ValueError:\n            self.q = 5\n\ndef f(n: int) -> int:\n    return C(n).v\n", "f", "int 1", 1L)]
  public void Classes_RunAsCPython(string source, string function, string expected, params object[] arguments)
    => Assert.That(PythonSnippet.Run(source, function, arguments), Is.EqualTo(expected));

  /// <summary>A nested function reads the module's names through every enclosing scope; a default is evaluated where the function is defined. Each is refused with exactly these diagnostics.</summary>
  [TestCase("def f(n: int) -> int:\n    xs = [1, 2, 3]\n    def g(k: int = sum([x for x in xs])) -> int:\n        return k\n    return g()\n", "snippet.py(3,24): error PLY1801: the type of the elements of '[x for x in xs]' is not known")]
  public void Scope_AreRefused(string source, string expected)
    => Assert.That(string.Join("\n", PythonSnippet.Refusals(source)), Is.EqualTo(expected));

  /// <summary>A type hint names a class of the module or of an imported module, a typing alias, or a built-in type of the import. Each imports into IR that verifies.</summary>
  [TestCase("import helper\n\ndef f(n: int) -> int:\n    b: helper.Nope = helper.B(n)\n    return 0\n", "class B:\n    def __init__(self, v: int) -> None:\n        self.v = v\n")]
  [TestCase("from helper import g\n\ndef f(n: int) -> int:\n    b: g = 1\n    return 0\n", "def g() -> int:\n    return 1\n")]
  public void Hints_Import_WithHelper(string source, string helper) {
    var module = PythonFixture.Module(WithHelper(source, helper));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>A type hint names a class of the module or of an imported module, a typing alias, or a built-in type of the import. Each imports into IR that verifies.</summary>
  [TestCase("def f(n: \"int\") -> int:\n    return 0\n")]
  public void Hints_Import(string source)
    => Assert.That(PythonSnippet.Module(source).Functions, Is.Not.Empty);

}
