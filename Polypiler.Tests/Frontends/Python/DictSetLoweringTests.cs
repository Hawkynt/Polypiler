using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// #451: <c>dict</c> and <c>set</c> as the IR's maps and sets in the order of insertion - displays,
/// comprehensions, indexing, the methods, membership, <c>len</c>, truth, <c>==</c>, the iteration of a
/// dict and the built-ins over a set whose result no order changes - and every refusal with its code.
/// Every expected value is what CPython 3.14 gives, except where <c>py-int-held-64</c> says otherwise.
/// </summary>
[TestFixture]
public sealed class DictSetLoweringTests {

  /// <summary>
  /// One case per operation of a dict: displays, lookups, stores, deletions, the methods, iteration in
  /// the order of insertion, a change of size during iteration, comprehensions, built-ins and <c>==</c>.
  /// </summary>
  [TestCase("    d = {'a': 1, 'b': 2}\n    return d['b'] * 10 + len(d)\n", "int 22")]
  [TestCase("    d: dict[str, int] = {}\n    d['x'] = 5\n    d['x'] += 2\n    return d['x'] * 10 + len(d)\n", "int 71")]
  [TestCase("    d = {1: 2}\n    return d[3]\n", "raise KeyError")]
  [TestCase("    d = {1: 2}\n    return (1 in d) and (2 not in d)\n", "bool True")]
  [TestCase("    d = {1: 10}\n    a = d.get(1)\n    b = d.get(2)\n    return (a if a is not None else 0) + (7 if b is None else 0)\n", "int 17")]
  [TestCase("    d = {1: 10}\n    return d.get(1, 5) * 100 + d.get(2, 5)\n", "int 1005")]
  [TestCase("    d = {1: 10}\n    return d.get(2, None) is None\n", "bool True")]
  [TestCase("    d = {1: 1, 2: 2}\n    del d[1]\n    return len(d) * 10 + int(1 in d)\n", "int 10")]
  [TestCase("    d = {1: 1}\n    del d[2]\n    return 0\n", "raise KeyError")]
  [TestCase("    d = {1: 10, 2: 20}\n    v = d.pop(1)\n    return v * 10 + len(d)\n", "int 101")]
  [TestCase("    d = {1: 10}\n    return d.pop(2, 7) * 10 + len(d)\n", "int 71")]
  [TestCase("    d = {1: 10}\n    return d.pop(2)\n", "raise KeyError")]
  [TestCase("    d: dict[int, int] = {}\n    for i in [3, 1, 2]:\n        d[i] = i\n    del d[3]\n    d[3] = 0\n    d[1] = 9\n    r = 0\n    for k in d:\n        r = r * 10 + k\n    return r\n", "int 123")]
  [TestCase("    d = {1: 2, 3: 4}\n    r = 0\n    for k, v in d.items():\n        r = r * 100 + k * 10 + v\n    return r\n", "int 1234")]
  [TestCase("    d = {1: 2, 3: 4}\n    r = 0\n    for v in d.values():\n        r = r * 10 + v\n    for k in d.keys():\n        r = r * 10 + k\n    return r\n", "int 2413")]
  [TestCase("    d = {1: 2, 3: 4}\n    for k in d:\n        d[k + 10] = 0\n    return len(d)\n", "raise RuntimeError")]
  [TestCase("    d = {1: 2, 3: 4}\n    for k in d:\n        del d[3]\n    return len(d)\n", "raise RuntimeError")]
  [TestCase("    d = {1: 2, 3: 4}\n    for k in d:\n        if k == 3:\n            del d[1]\n    return len(d)\n", "raise RuntimeError")]
  [TestCase("    d = {1: 2, 3: 4}\n    for k in d:\n        d[k] = d[k] + 1\n    return d[1] * 10 + d[3]\n", "int 35")]
  [TestCase("    d = {1: 2, 3: 4}\n    for k in d:\n        d[9] = 9\n        break\n    return len(d)\n", "int 3")]
  [TestCase("    d = {1: 2}\n    for v in d.values():\n        d.pop(1)\n    return len(d)\n", "raise RuntimeError")]
  [TestCase("    d = {1: 1, 2: 2}\n    r = 0\n    for k in d:\n        r = r * 10 + k\n        if k == 1:\n            del d[2]\n            d[3] = 3\n    return r\n", "int 13")]
  [TestCase("    d = {1: 1, 2: 2}\n    r = 0\n    for k in d:\n        r = r * 10 + k\n        if k == 1:\n            del d[1]\n            d[1] = 3\n    return r\n", "raise RuntimeError")]
  [TestCase("    d = {k: k * k for k in range(4) if k}\n    return sum(d.values()) * 1000 + len(d) * 100 + d[3]\n", "int 14309")]
  [TestCase("    d = {k: v for k, v in [(1, 2), (1, 3), (4, 5)]}\n    return d[1] * 100 + len(d) * 10 + list(d)[1]\n", "int 324")]
  [TestCase("    d = {3: 1, 1: 2, 2: 3}\n    return sorted(d)[0] * 1000 + sum(d) * 100 + min(d) * 10 + max(d.values())\n", "int 1613")]
  [TestCase("    d = {3: 1, 1: 2}\n    r = 0\n    for k, v in list(d.items()):\n        r = r * 100 + k * 10 + v\n    return r\n", "int 3112")]
  [TestCase("    d = {3: 1, 1: 2}\n    return [k * 10 + v for k, v in d.items()][1]\n", "int 12")]
  [TestCase("    return {1: 2, 3: 4} == {3: 4, 1: 2}\n", "bool True")]
  [TestCase("    return {1: 2, 3: 4} != {1: 2, 3: 5}\n", "bool True")]
  [TestCase("    d = {'a': 'x', 'b': 'y'}\n    return d['a'] == 'x' and 'b' in d and 'c' not in d\n", "bool True")]
  [TestCase("    d = {1: 2}\n    e = dict(d)\n    c = d.copy()\n    e[3] = 4\n    c.clear()\n    return len(d) * 100 + len(e) * 10 + len(c)\n", "int 120")]
  [TestCase("    d: dict[str, list[int]] = {}\n    d['a'] = [1]\n    d['a'].append(2)\n    return len(d['a']) * 10 + d['a'][1]\n", "int 22")]
  [TestCase("    d: dict[int, int] = {}\n    if d:\n        return 1\n    d[0] = 0\n    return 2 if d else 3\n", "int 2")]
  [TestCase("    d = {1: 'a', True: 'b'}\n    return len(d) * 10 + int(d[1] == 'b')\n", "int 11")]
  [TestCase("    d = {1: [1, 2]}\n    return sum(len(v) for v in d.values()) + int(any(d)) * 10 + int(all(d)) * 100\n", "int 112")]
  [TestCase("    d = {2: 3}\n    return {k: 0 for k in d} == {2: 0}\n", "bool True")]
  public void Dict_BehavesAsInPython(string body, string expected)
    => Assert.That(PythonSnippet.Run("def f():\n" + body, "f"), Is.EqualTo(expected));

  /// <summary>
  /// One case per operation of a set: displays, <c>add</c>, <c>remove</c>, <c>discard</c>, membership,
  /// the built-ins whose result no order changes, comprehensions, copies, truth and <c>==</c>.
  /// </summary>
  [TestCase("    s = {1, 2, 2, 3}\n    return len(s) * 10 + int(2 in s)\n", "int 31")]
  [TestCase("    s: set[int] = set()\n    s.add(3)\n    s.add(3)\n    s.add(1)\n    s.discard(7)\n    s.discard(1)\n    return len(s) * 10 + int(3 in s)\n", "int 11")]
  [TestCase("    s = {1}\n    s.remove(2)\n    return 0\n", "raise KeyError")]
  [TestCase("    s = {1, 2}\n    s.remove(2)\n    return len(s)\n", "int 1")]
  [TestCase("    s = {3, 1, 2}\n    l = sorted(s)\n    return l[0] * 100 + l[1] * 10 + l[2]\n", "int 123")]
  [TestCase("    s = {3, 1, 2}\n    return sum(s) * 100 + min(s) * 10 + max(s)\n", "int 613")]
  [TestCase("    s = {'b', 'a'}\n    return sorted(s)[0] == 'a' and min(s) == 'a' and max(s) == 'b'\n", "bool True")]
  [TestCase("    s = {0, 1}\n    return int(any(s)) * 10 + int(all(s))\n", "int 10")]
  [TestCase("    s = set([3, 1, 3])\n    t = {x % 2 for x in [1, 2, 3]}\n    return len(s) * 10 + len(t)\n", "int 22")]
  [TestCase("    return {1, 2} == {2, 1}\n", "bool True")]
  [TestCase("    return {1, 2} != {1, 3}\n", "bool True")]
  [TestCase("    s = {1}\n    c = s.copy()\n    t = set(s)\n    c.add(2)\n    t.clear()\n    return len(s) * 100 + len(c) * 10 + len(t)\n", "int 120")]
  [TestCase("    s: set[str] = set()\n    if s:\n        return 1\n    s.add('x')\n    return 2 if s else 3\n", "int 2")]
  [TestCase("    s = {True, 1, 0}\n    return len(s)\n", "int 2")]
  public void Set_BehavesAsInPython(string body, string expected)
    => Assert.That(PythonSnippet.Run("def f():\n" + body, "f"), Is.EqualTo(expected));

  /// <summary>
  /// The equivalence classes of keys and values - float, tuple and nested dict values - the empty dict
  /// and set, the built-ins over a dict's keys, values and pairs, and == that finds a difference.
  /// </summary>
  [TestCase("    d: dict[str, float] = {'a': 1.5}\n    d['b'] = 2\n    return d['a'] + d['b']\n", "float 3.5")]
  [TestCase("    d = {1: (2, 3)}\n    r = 0\n    for k, v in d.items():\n        r = k * 100 + v[0] * 10 + v[1]\n    return r\n", "int 123")]
  [TestCase("    d: dict[str, dict[str, int]] = {'a': {'x': 1}}\n    d['a']['y'] = 2\n    return len(d['a']) * 10 + d['a']['y']\n", "int 22")]
  [TestCase("    d: dict[int, int] = {}\n    return len(d) + len(sorted(d)) + sum(d.values())\n", "int 0")]
  [TestCase("    d: dict[int, int] = {}\n    return min(d)\n", "raise ValueError")]
  [TestCase("    d: dict[int, int] = {}\n    return d.pop(1, 4)\n", "int 4")]
  [TestCase("    s: set[int] = set()\n    return sum(s) + len(s)\n", "int 0")]
  [TestCase("    s: set[int] = set()\n    return max(s)\n", "raise ValueError")]
  [TestCase("    s: set[int] = set()\n    return int(any(s)) * 10 + int(all(s))\n", "int 1")]
  [TestCase("    d = {'b': 1, 'a': 2}\n    return max(d) == 'b' and sorted(d.values())[0] == 1\n", "bool True")]
  [TestCase("    d = {1: 2}\n    n = 0\n    for i, k in enumerate(d):\n        n = i * 10 + k\n    return n\n", "int 1")]
  [TestCase("    d = {1: 2, 3: 4}\n    return sum(k * v for k, v in d.items())\n", "int 14")]
  [TestCase("    d = {1: 2}\n    d[1] = 5\n    return len(d) * 10 + d[1]\n", "int 15")]
  [TestCase("    d = {1: 2, 2: 3}\n    return {k: v for k, v in d.items() if v > 2} == {2: 3}\n", "bool True")]
  [TestCase("    d = {1: 2}\n    return d == {1: 2, 3: 4}\n", "bool False")]
  [TestCase("    d = {1: 2}\n    return d == {3: 2}\n", "bool False")]
  [TestCase("    s = {1, 2}\n    return s == {1}\n", "bool False")]
  [TestCase("    s = {1, 2}\n    return s == {1, 3}\n", "bool False")]
  [TestCase("    d = {(1 if True else 2): 3 for _ in range(2)}\n    return len(d)\n", "int 1")]
  [TestCase("    d = {'a': 1, 'bb': 2, 'c': 3}\n    r = 0\n    for k, v in d.items():\n        r = r * 10 + len(k) * v\n    return r\n", "int 143")]
  [TestCase("    d = {'a': 1, 'bb': 2, 'c': 3}\n    e = {k: v for k, v in d.items() if v != 2}\n    return len(e) * 10 + e['c']\n", "int 23")]
  [TestCase("    d = {'a': 1, 'bb': 2}\n    return sum(len(k) * v for k, v in d.items())\n", "int 5")]
  [TestCase("    d = {'a': 'x', 'b': 'yy'}\n    return [len(k + v) for k, v in d.items() if v != 'x'][0]\n", "int 3")]
  [TestCase("    d = {'a': 1, 'bb': 2}\n    r = 0\n    for i, k in enumerate(d, 5):\n        r = r * 100 + i * 10 + len(k)\n    return r\n", "int 5162")]
  [TestCase("    d = {'a': 1}\n    r = 0\n    for k, v in d.items():\n        d['b'] = 2\n    return r\n", "raise RuntimeError")]
  public void KeyAndValueClasses_BehaveAsInPython(string body, string expected)
    => Assert.That(PythonSnippet.Run("def f():\n" + body, "f"), Is.EqualTo(expected));

  /// <summary>An empty dict, set or list without a hint takes its types from what is stored or added; dict() from its hint.</summary>
  [Test]
  public void EmptyCollections_TakeTheirTypesFromTheirUse()
    => Assert.That(PythonSnippet.Run("def f():\n    d = {}\n    d[1] = 2\n    s = set()\n    s.add('a')\n    l = []\n    l.append(3)\n    e: dict[int, int] = dict()\n    e[4] = 5\n"
      + "    return d[1] * 1000 + len(s) * 100 + l[0] * 10 + int(any({'a'}))\n", "f"), Is.EqualTo("int 2131"));

  /// <summary>get of a dict of objects gives the object or None; a method named keys of a class is the class's own.</summary>
  [Test]
  public void ObjectValues_AndMethodsNamedAsViews_AreTheirOwn() {
    Assert.That(PythonSnippet.Run("class N:\n    def __init__(self, v: int):\n        self.v = v\n\n"
      + "def g():\n    d = {1: N(5)}\n    a = d.get(1)\n    b = d.get(2)\n    return (a.v if a is not None else 0) * 10 + (1 if b is None else 0)\n", "g"), Is.EqualTo("int 51"));
    Assert.That(PythonSnippet.Run("class C:\n    def keys(self) -> list[int]:\n        return [1, 2]\n\n"
      + "def h():\n    c = C()\n    t = 0\n    for k in c.keys():\n        t += k\n    return t\n", "h"), Is.EqualTo("int 3"));
  }

  /// <summary>A dict a call gives is iterated as one held in a variable: its size is taken at the first step.</summary>
  [Test]
  public void DictOfACall_IsIterated()
    => Assert.That(PythonSnippet.Run("def mk():\n    return {1: 2, 3: 4}\n\ndef f():\n    t = 0\n    for k in mk():\n        t = t * 10 + k\n    return t * 100 + sum(mk().values())\n", "f"),
      Is.EqualTo("int 1306"));

  /// <summary>An object of a plain class is a key by its identity: two equal-looking objects are two keys.</summary>
  [Test]
  public void ObjectKeys_AreFoundByIdentity()
    => Assert.That(PythonSnippet.Run("class K:\n    def __init__(self, v: int):\n        self.v = v\n\n"
      + "def f() -> int:\n    a = K(1)\n    b = K(1)\n    d = {a: 1}\n    d[b] = 2\n    s = {a, b, a}\n    return len(d) * 100 + d[a] * 10 + len(s)\n", "f"), Is.EqualTo("int 212"));

  /// <summary>
  /// An int key or element is held in 64 bits: exact on the edges, <c>OverflowError</c> beyond them,
  /// where CPython gives 3 (<c>py-int-held-64</c>); a value keeps an int of any size.
  /// </summary>
  [TestCase("9223372036854775807", "int 3")]
  [TestCase("-9223372036854775808", "int 3")]
  [TestCase("9223372036854775808", "raise OverflowError")]
  [TestCase("-9223372036854775809", "raise OverflowError")]
  public void IntKeys_AreHeldInSixtyFourBits(string literal, string expected) {
    var n = BigInteger.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);
    Assert.That(PythonSnippet.Run("def f(n: int) -> int:\n    d = {n: 1}\n    s = {n}\n    return d[n] + len(d) + int(n in s)\n", "f", n), Is.EqualTo(expected));
    Assert.That(PythonSnippet.Run("def f(n: int) -> int:\n    d = {1: n * n}\n    return d[1] - n * n\n", "f", n), Is.EqualTo("int 0"));
  }

  /// <summary>A module whose dict or set holds int keys announces <c>py-int-held-64</c>; one keyed by str does not.</summary>
  [Test]
  public void IntKeys_AnnounceTheCaveat() {
    var held = PythonSnippet.Import("def f(n: int) -> int:\n    d = {n: 1}\n    return len(d)\n");
    Assert.That(held.Caveats.Select(caveat => caveat.Id), Does.Contain(PythonFrontend.HeldInt));
    var text = PythonSnippet.Import("def f(n: str) -> int:\n    d = {n: 1}\n    return len(d)\n");
    Assert.That(text.Caveats.Select(caveat => caveat.Id), Does.Not.Contain(PythonFrontend.HeldInt));
  }

  /// <summary>A dict is the IR's map in the order of insertion, iterated live with the size checked; a set the IR's set.</summary>
  [Test]
  public void Dict_IsAnInsertionOrderedMap() {
    var module = PythonSnippet.Module("def f(d: dict[str, int], s: set[str]) -> int:\n    t = 0\n    for k in d:\n        t += d[k]\n    return t + len(s)\n");
    var function = module.Functions.Single(candidate => candidate.Name == "f");
    Assert.That(function.Parameters[0].Type, Is.EqualTo(IrType.Map(IrType.String, IrType.BigInt, MapOrder.Insertion)));
    Assert.That(function.Parameters[1].Type, Is.EqualTo(IrType.Set(IrType.String, MapOrder.Insertion)));
    var walk = module.Functions.Single(candidate => candidate.Name.StartsWith("PyDict_keys", System.StringComparison.Ordinal));
    Assert.That(PythonSnippet.Statements(walk.Body).OfType<IrForEachCollection>().Single().Mode, Is.EqualTo(EnumerationMode.Live));
  }

  /// <summary>Every dict or set construct outside the subset is refused with its code, naming it.</summary>
  [TestCase("    x = {}\n", "error PLY1801: the type of 'x' in 'f' is not known: nothing tells the type of its keys and values; add a type hint such as 'dict[str, int]'")]
  [TestCase("    x = set()\n", "error PLY1801: the type of 'x' in 'f' is not known: nothing tells the type of its elements; add a type hint such as 'set[int]'")]
  [TestCase("    x = len({})\n", "error PLY1801: the type of the keys and values of '{}' is not known; add a type hint such as 'dict[str, int]' to what holds it")]
  [TestCase("    x = len(set())\n", "error PLY1801: the type of the elements of 'set()' is not known; add a type hint such as 'set[int]' to what holds it")]
  [TestCase("    x = {1: 2, 'a': 3}\n", "error PLY1801")]
  [TestCase("    x = {1.5: 1}\n", "error PLY1842: dict or set operation 'a dict keyed by float' is not supported by the Python frontend: a key or an element of a set is an int, a bool, a str or an object of a plain class without __eq__")]
  [TestCase("    x = {(1, 2): 1}\n", "error PLY1842: dict or set operation 'a dict keyed by tuple[int, int]' is not supported")]
  [TestCase("    x = {P(1): 1}\n", "error PLY1842: dict or set operation 'a dict keyed by P' is not supported")]
  [TestCase("    x = {1.5}\n", "error PLY1842: dict or set operation 'a set of float' is not supported")]
  [TestCase("    x = {k: 1 for k in [1.5]}\n", "error PLY1842: dict or set operation 'a dict keyed by float' is not supported")]
  [TestCase("    x = {1: None, 2: 3}\n", "error PLY1842: dict or set operation 'a dict of int | None values' is not supported by the Python frontend: a value is no optional and no generator")]
  [TestCase("    x = {*[1]}\n", "error PLY1842: dict or set operation '*[1] in a display' is not supported")]
  [TestCase("    x = {**{1: 2}}\n", "error PLY1842: dict or set operation '**{1: 2} in a display' is not supported")]
  [TestCase("    d = {1: 2}\n    x = d.keys()\n", "error PLY1842: dict or set operation 'keys() as a value' is not supported by the Python frontend; a view is imported where a for, a comprehension or a built-in iterates it")]
  [TestCase("    d = {1: 'a'}\n    x = d.get(1)\n", "error PLY1842: dict or set operation 'get without a default from a dict of str values' is not supported by the Python frontend, which gives str | None")]
  [TestCase("    d = {1: 2}\n    x = d.get(1, 2.5)\n", "error PLY1842: dict or set operation 'get with a default of the type float from a dict of int values' is not supported")]
  [TestCase("    d = {1: 2}\n    x = d.get(1, default=2)\n", "error PLY1842: dict or set operation 'get with keyword arguments' is not supported")]
  [TestCase("    d = {1: 2}\n    d.setdefault(1, 2)\n", "error PLY1842: dict or set operation 'setdefault' is not supported")]
  [TestCase("    d = {1: 2}\n    d.popitem()\n", "error PLY1842: dict or set operation 'popitem' is not supported")]
  [TestCase("    d = {1: 2}\n    d.update({3: 4})\n", "error PLY1842: dict or set operation 'update' is not supported")]
  [TestCase("    d = {1: 2}\n    d.pop()\n", "error PLY1842: dict or set operation 'dict.pop' is not supported by the Python frontend with these arguments")]
  [TestCase("    s = {1}\n    s.union({2})\n", "error PLY1842: dict or set operation 'union' is not supported")]
  [TestCase("    s = {1}\n    s.pop()\n", "error PLY1842: dict or set operation 'set.pop' is not supported")]
  [TestCase("    s = {1}\n    s.add(1, 2)\n", "error PLY1842: dict or set operation 'set.add' is not supported")]
  [TestCase("    x = len(dict(a=1))\n", "error PLY1842: dict or set operation 'dict(a=1)' is not supported")]
  [TestCase("    x = len(dict([(1, 2)]))\n", "error PLY1842: dict or set operation 'dict([(1, 2)])' is not supported by the Python frontend: dict of a value of the type list[tuple[int, int]]")]
  [TestCase("    x = frozenset([1])\n", "error PLY1842: dict or set operation 'frozenset([1])' is not supported")]
  [TestCase("    s = {1}\n    x = list(s)\n", "error PLY1842: dict or set operation 'iteration over a set, whose order Python leaves to the hash,' is not supported by the Python frontend; sorted(s) iterates it in order")]
  [TestCase("    s = {1}\n    x = [v for v in s]\n", "error PLY1842: dict or set operation 'iteration over a set")]
  [TestCase("    s = {1}\n    x = sorted(s, key=lambda v: -v)\n", "error PLY1842: dict or set operation 'iteration over a set")]
  [TestCase("    s = {1}\n    x = min(s, key=lambda v: -v)\n", "error PLY1842: dict or set operation 'iteration over a set")]
  [TestCase("    s = {C()}\n    x = any(s)\n", "error PLY1842: dict or set operation 'iteration over a set")]
  [TestCase("    s = {1}\n    x = sum(v for v in s)\n", "error PLY1842: dict or set operation 'iteration over a set")]
  [TestCase("    d = {'a': 1}\n    x = len(list(d.items()))\n", "error PLY1842: dict or set operation 'items() of a dict of the types str and int outside 'for k, v in d.items()'' is not supported by the Python frontend, as a tuple of the IR holds no str, list or object")]
  [TestCase("    d = {'a': 1}\n    x = [kv[1] for kv in d.items()]\n", "error PLY1842: dict or set operation 'items() of a dict of the types str and int")]
  [TestCase("    d = {1: [1]}\n    for kv in d.items():\n        pass\n", "error PLY1842: dict or set operation 'items() of a dict of the types int and list[int]")]
  [TestCase("    d = {1: 2}\n    for x in enumerate(d):\n        pass\n", "error PLY1813: iteration over 'enumerate(d)' is not supported: enumerate takes one sequence and a start, its target two names")]
  [TestCase("    d = {1: 2}\n    x = 'a' in d\n", "error PLY1803")]
  [TestCase("    s = {1}\n    t = {2}\n    x = len(s | t)\n", "error PLY1842: dict or set operation '| of the types set[int] and set[int]' is not supported")]
  [TestCase("    s = {1}\n    x = len(2 * s)\n", "error PLY1842: dict or set operation '* of the types int and set[int]' is not supported")]
  [TestCase("    s = {1}\n    t = {2}\n    x = s < t\n", "error PLY1842: dict or set operation '< of the types set[int] and set[int]' is not supported by the Python frontend, which compares subsets")]
  [TestCase("    d = {1: 2}\n    x = 1 <= d\n", "error PLY1842: dict or set operation '<= of the types int and dict[int, int]' is not supported")]
  [TestCase("    x = {1}\n    x = {'a'}\n", "error PLY1803")]
  [TestCase("    x = {1: 2}\n    x = {1: 'a'}\n", "error PLY1803")]
  [TestCase("    x = {1: 2}\n    x = {'a': 2}\n", "error PLY1803")]
  [TestCase("    d = {1: []}\n    d['a'] = [1]\n", "error PLY1801")]
  [TestCase("    x = len(set(5))\n", "error PLY1801: the type of the elements of 'set(5)' is not known")]
  [TestCase("    s = {1}\n    x = s.add(1)\n", "error PLY1802: 'the variable x, which only ever holds None,'")]
  [TestCase("    x = g({})\n", "error PLY1801: the type of the keys and values of '{}' is not known")]
  [TestCase("    d = {1: 2}\n    x = d.pop(1, None)\n", "error PLY1802")]
  [TestCase("    d = {1: 2}\n    x = d.get(1, 2, 3)\n", "error PLY1842: dict or set operation 'dict.get' is not supported")]
  [TestCase("    d = {1: 2}\n    for k in d.keys(1):\n        pass\n", "error PLY1842: dict or set operation 'dict.keys' is not supported")]
  [TestCase("    d = {1: 2}\n    d.add(1)\n", "error PLY1842: dict or set operation 'dict.add' is not supported")]
  [TestCase("    d = {1: 2}\n    d.remove(1)\n", "error PLY1842: dict or set operation 'dict.remove' is not supported")]
  [TestCase("    s = {1}\n    s.remove()\n", "error PLY1842: dict or set operation 'set.remove' is not supported")]
  [TestCase("    s = {1}\n    s.get(1)\n", "error PLY1842: dict or set operation 'set.get' is not supported")]
  [TestCase("    s = {1}\n    s.keys()\n", "error PLY1842: dict or set operation 'set.keys' is not supported")]
  [TestCase("    l = [1]\n    del l[0]\n", "error PLY1820: sequence operation 'del l[0]' is not supported by the Python frontend")]
  [TestCase("    l = [1]\n    l.remove(1)\n", "error PLY1820: sequence operation 'remove' is not supported by the Python frontend")]
  [TestCase("    l = [1]\n    l.clear()\n", "error PLY1820: sequence operation 'clear' is not supported by the Python frontend")]
  public void DictOrSetOutsideTheSubset_IsRefusedByName(string body, string diagnostic)
    => Assert.That(PythonSnippet.Refusals("from dataclasses import dataclass\n\n@dataclass\nclass P:\n    a: int\n\nclass C:\n    def __init__(self):\n        self.a = 1\n\n"
      + "def g(x: list[int]) -> int:\n    return 0\n\ndef f() -> int:\n" + body + "    return 0\n"),
      Has.Some.Contains(diagnostic));

  /// <summary>A dict or a set at module level, as a default or as an attribute is refused: no table holds one, and a default would be shared across calls.</summary>
  [TestCase("D = {1: 2}\n\ndef f() -> int:\n    return 0\n", "error PLY1810: statement 'module-level {1: 2}' of the type dict[int, int] is not supported")]
  [TestCase("S = {1}\n\ndef f() -> int:\n    return 0\n", "error PLY1810: statement 'module-level {1}' of the type set[int] is not supported")]
  [TestCase("def f(d={}) -> int:\n    return 0\n", "error PLY1834")]
  [TestCase("def f(d=dict()) -> int:\n    return 0\n", "error PLY1834")]
  [TestCase("class A:\n    def __init__(self):\n        self.d = {1: 2}\n\ndef f() -> int:\n    return 0\n", "error PLY1840: 'the attribute d of A of the type dict[int, int]'")]
  public void DictOrSetOutsideAFunction_IsRefused(string source, string diagnostic)
    => Assert.That(PythonSnippet.Refusals(source), Has.Some.Contains(diagnostic));

}
