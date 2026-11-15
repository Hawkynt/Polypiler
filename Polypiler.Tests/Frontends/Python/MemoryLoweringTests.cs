using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.Frontends.Python.Lowering;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// #451: lists, tuples, str, bytes and bytearray, slices, comprehensions, the iteration built-ins,
/// <c>int.from_bytes</c>/<c>to_bytes</c> and <c>struct</c> - the scenarios of
/// <c>fe-py-memory.feature</c> and the rows of the test design the <c>memory</c> fixture does not
/// already hold to CPython, and every refusal with its code.
/// </summary>
[TestFixture]
public sealed class MemoryLoweringTests {

  /// <summary>A list's digits as a number, followed by its length: what a function of a list gives back to a test.</summary>
  private const string Digits = "\n\ndef digits(r: list[int]) -> int:\n    s = 0\n    for x in r:\n        s = s * 10 + x\n    return s * 10 + len(r)\n";

  [TestCase(0L, "int 1")]
  [TestCase(-1L, "int 5")]
  [TestCase(5L, "raise IndexError")]
  [TestCase(-6L, "raise IndexError")]
  public void Indexing_NormalisesNegativeIndicesAndTrapsOutsideTheList(long i, string expected)
    => Assert.That(PythonSnippet.Run("def f(a: list[int], i: int) -> int:\n    return a[i]\n\ndef g(i: int) -> int:\n    return f([1, 2, 3, 4, 5], i)\n", "g", i), Is.EqualTo(expected));

  [TestCase("a[1:3]", "int 232")]
  [TestCase("a[-2:]", "int 452")]
  [TestCase("a[10:]", "int 0")]
  [TestCase("a[::-1]", "int 543215")]
  [TestCase("a[1:4:2]", "int 242")]
  public void Slices_ClampInsteadOfTrapping(string expression, string expected)
    => Assert.That(PythonSnippet.Run($"def f(a: list[int]) -> list[int]:\n    return {expression}\n\ndef g() -> int:\n    return digits(f([1, 2, 3, 4, 5]))" + Digits, "g"), Is.EqualTo(expected));

  [Test]
  public void Comprehension_BuildsANewListInItsOwnScope()
    => Assert.That(PythonSnippet.Run("def f(n: int) -> list[int]:\n    x = 99\n    r = [x * x for x in range(n) if x % 2]\n    r.append(x)\n    return r\n\ndef g() -> int:\n    r = f(5)\n    return r[0] * 10000 + r[1] * 100 + r[2]\n", "g"),
      Is.EqualTo("int 10999"));

  [Test]
  public void BytearrayStores_AreRangeChecked() {
    const string source = "def f(v: int) -> int:\n    b = bytearray(2)\n    b[0] = v\n    return b[0] + len(b)\n";
    Assert.That(PythonSnippet.Run(source, "f", 255L), Is.EqualTo("int 257"));
    Assert.That(PythonSnippet.Run(source, "f", 256L), Is.EqualTo("raise ValueError"));
  }

  [Test]
  public void NativeOrderStructFormat_IsRefused() {
    var refusals = PythonSnippet.Refusals("import struct\n\ndef f(b: bytes) -> int:\n    return struct.unpack(\"I\", b)[0]\n");
    Assert.That(refusals, Has.One.Contains("error PLY1823: struct format or array typecode '\"I\"' is not supported").And.Contains("byte order"));
  }

  /// <summary>Test design 3: each sequence type is made, indexed and measured.</summary>
  [TestCase("[1.5, 2.5]", "a[1] + len(a)", "float 4.5")]
  [TestCase("[True, False]", "a[-1] or len(a) > 1", "bool True")]
  [TestCase("[[1], [2, 3]]", "a[1][0] + len(a[1])", "int 4")]
  [TestCase("(1, 2, 3)", "a[2] + len(a)", "int 6")]
  [TestCase("'abc'", "ord(a[1]) + len(a)", "int 101")]
  [TestCase("b'xy'", "a[0] + len(a)", "int 122")]
  [TestCase("bytearray(b'xy')", "a[-1] + len(a)", "int 123")]
  public void SequenceTypes_AreMadeIndexedAndMeasured(string made, string expression, string expected)
    => Assert.That(PythonSnippet.Run($"def f():\n    a = {made}\n    return {expression}\n", "f"), Is.EqualTo(expected));

  /// <summary>Test design 5: empty sequences.</summary>
  [TestCase("len(a) + sum(a)", "int 0")]
  [TestCase("len(a[1:]) + len([x for x in a])", "int 0")]
  [TestCase("min(a)", "raise ValueError")]
  [TestCase("min(a, default=7)", "int 7")]
  [TestCase("a.pop()", "raise IndexError")]
  [TestCase("int(any(a)) * 10 + int(all(a))", "int 1")]
  public void EmptyList_BehavesAsInPython(string expression, string expected)
    => Assert.That(PythonSnippet.Run($"def f() -> int:\n    a: list[int] = []\n    return {expression}\n", "f"), Is.EqualTo(expected));

  /// <summary>Test design 6: a zero step raises, other steps clamp; strings slice by code point.</summary>
  [TestCase("a[::0]", "raise ValueError")]
  [TestCase("a[3:1]", "int 0")]
  [TestCase("a[-100:100:-2]", "int 0")]
  [TestCase("a[100:-100:-2]", "int 5313")]
  public void SliceSteps_AreThoseOfPython(string expression, string expected)
    => Assert.That(PythonSnippet.Run($"def f() -> int:\n    return digits([1, 2, 3, 4, 5]{expression[1..]})" + Digits, "f"), Is.EqualTo(expected));

  [TestCase("s[1:]", "str", "int 3")]
  [TestCase("s[::-1]", "str", "int 4")]
  [TestCase("s[-2:-1]", "str", "int 1")]
  [TestCase("b'abcd'[1:3]", "bytes", "int 2")]
  [TestCase("bytearray(b'abcd')[::2]", "bytearray", "int 2")]
  public void StringAndBytesSlices_AreTheirOwnTypes(string expression, string type, string expected)
    => Assert.That(PythonSnippet.Run($"def f() -> int:\n    s = 'a😀bc'\n    t: {type} = {expression}\n    return len(t)\n", "f"), Is.EqualTo(expected));

  [Test]
  public void TupleIterationMembershipAndTruth_FollowPython() {
    const string source = "def f(n: int) -> int:\n    total = 0\n    for x in (1, 2, n):\n        total += x\n    t = (n, n)\n"
      + "    return total * 100 + int(n in (1, 2)) * 10 + int(bool(t)) + int(t == (n, n)) + int(t != (n, 0))\n";
    Assert.That(PythonSnippet.Run(source, "f", 2L), Is.EqualTo("int 513"));
  }

  [Test]
  public void TruthOfSequences_IsTheirEmptiness() {
    const string source = "def f(n: int) -> int:\n    a = [1] * n\n    s = 'x' * n\n    b = bytes(n)\n    return (1 if a else 0) + (2 if s else 0) + (4 if b else 0) + (8 if not a else 0)\n";
    Assert.That(PythonSnippet.Run(source, "f", 0L), Is.EqualTo("int 8"));
    Assert.That(PythonSnippet.Run(source, "f", 2L), Is.EqualTo("int 7"));
  }

  [Test]
  public void RepetitionWithTheCountFirst_KeepsTheOrderWritten() {
    const string source = "def f(n: int) -> int:\n    return len(n * b'ab') + len(n * 'xyz') + len(3 * [n])\n";
    Assert.That(PythonSnippet.Run(source, "f", 2L), Is.EqualTo("int 13"));
    Assert.That(PythonSnippet.Run(source, "f", -1L), Is.EqualTo("int 3"));
  }

  [Test]
  public void SortedStringsAndTuples_CompareByCodePointsAndItems() {
    const string source = "def f() -> int:\n    words = sorted(['b', 'ab', '\uffff', '😀', 'a'])\n    pairs = sorted([(2, 1), (1, 5), (1, 2)], reverse=True)\n"
      + "    return int(words == ['a', 'ab', 'b', '\uffff', '😀']) * 100 + pairs[0][0] * 10 + pairs[2][1]\n";
    Assert.That(PythonSnippet.Run(source, "f"), Is.EqualTo("int 122"));
  }

  [Test]
  public void ExtremesWithKeys_TakeTheFirstOfEqualKeys() {
    const string source = "def f() -> int:\n    a = [3, -3, 2, -2]\n    return max(a, key=lambda v: v * v) * 10 + min(a, key=abs_key)\n\ndef abs_key(v: int) -> int:\n    return v if v >= 0 else -v\n";
    Assert.That(PythonSnippet.Run(source, "f"), Is.EqualTo("int 32"));
  }

  [Test]
  public void ListOfTuplesAndNestedGenerators_AreSequences() {
    const string source = "def f(n: int) -> int:\n    pairs = [(x, y) for x in range(n) for y in range(x)]\n    flat = list(x * y for x, y in pairs if x > y)\n"
      + "    return len(pairs) * 100 + sum(flat) + sum(1 for c in 'ab😀')\n";
    Assert.That(PythonSnippet.Run(source, "f", 4L), Is.EqualTo("int 614"));
  }

  [Test]
  public void ListOfObjectsAndOfLists_AreBuiltByAppending() {
    var module = PythonSnippet.Module("def f(n: int) -> int:\n    rows = [[n], [n, n]]\n    rows.append([1])\n    return len(rows) + rows[1][1]\n");
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("f", [4L])), Is.EqualTo("int 7"));
    Assert.That(module.Functions.Select(function => function.Name), Has.Some.StartsWith("PyListOf2_"));
  }

  [Test]
  public void FloatsInLists_CompareByValueAsTheCaveatSays() {
    var result = PythonSnippet.Import("def f(x: float) -> bool:\n    return x in [1.5, x] and [x] == [x]\n");
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Does.Contain(PythonFrontend.FloatIdentity));
    Assert.That(PythonOutcome.Interpreted(result.Module!, new PythonCall("f", [double.NaN])), Is.EqualTo("bool False"));
    Assert.That(PythonOutcome.Interpreted(result.Module!, new PythonCall("f", [2.0])), Is.EqualTo("bool True"));
  }

  [Test]
  public void SortedFloats_CarryTheirCaveat()
    => Assert.That(PythonSnippet.Import("def f() -> float:\n    return sorted([2.5, 1.5])[0]\n").Caveats.Select(caveat => caveat.Id), Does.Contain(PythonFrontend.SortNaN));

  /// <summary>Test design 12: the struct letters and counts in both orders, and int.from_bytes beyond 8 bytes.</summary>
  [TestCase("struct.unpack('!2Hx?', b'\\x00\\x01\\x00\\x02\\x00\\x01')[1]", "int 2")]
  [TestCase("struct.unpack('>bBhHiIlLqQ', bytes(range(1, 39)))[9]", "int 2242829044932683046")]
  [TestCase("struct.unpack('<bh', b'\\xff\\xfe\\xff')[0] + struct.unpack('<bh', b'\\xff\\xfe\\xff')[1]", "int -3")]
  [TestCase("struct.calcsize('<2x3Bq')", "int 13")]
  [TestCase("struct.unpack('<Q', b'\\xff' * 8)[0]", "raise OverflowError")]
  [TestCase("len(struct.pack('>i', 2 ** 31))", "raise error")]
  [TestCase("len(struct.pack('<q?', -1, 5))", "int 9")]
  [TestCase("int.from_bytes(b'\\x00' * 9 + b'\\x01', 'big')", "int 1")]
  [TestCase("int.from_bytes(b'\\x01' + b'\\x00' * 9, 'big')", "raise OverflowError")]
  [TestCase("int.from_bytes(b'\\xff' * 9 + b'\\x7f', 'little', signed=True)", "raise OverflowError")]
  [TestCase("int.from_bytes(b'\\x80' + b'\\x00' * 7, 'big')", "raise OverflowError")]
  [TestCase("int.from_bytes(b'')", "int 0")]
  [TestCase("len((0).to_bytes(0, 'big')) + len((5).to_bytes())", "int 1")]
  [TestCase("len((1).to_bytes(0, 'big'))", "raise OverflowError")]
  [TestCase("len((1).to_bytes(-1, 'big'))", "raise ValueError")]
  [TestCase("(-1).to_bytes(9, 'little', signed=True)[8]", "int 255")]
  [TestCase("struct.unpack_from('<H', b'\\x01\\x02\\x03', offset=1)[0]", "int 770")]
  [TestCase("struct.unpack_from('<H', b'\\x01\\x02\\x03', -5)[0]", "raise error")]
  public void ByteLayouts_ReadAndWriteAsCPython(string expression, string expected)
    => Assert.That(PythonSnippet.Run($"import struct\n\ndef f() -> int:\n    return {expression}\n", "f"), Is.EqualTo(expected));

  [TestCase("bytes([1, 256])", "raise ValueError")]
  [TestCase("bytes(-1)", "raise ValueError")]
  [TestCase("bytes(b'ab')", "int 2")]
  [TestCase("bytearray(range(3))", "int 3")]
  [TestCase("bytes([True, False])", "int 2")]
  [TestCase("bytes()", "int 0")]
  [TestCase("bytes('é', 'utf-8')", "int 2")]
  [TestCase("'😀'.encode()", "int 4")]
  public void BytesConstructions_AreThoseOfPython(string made, string expected)
    => Assert.That(PythonSnippet.Run($"def f() -> int:\n    return len({made})\n", "f"), Is.EqualTo(expected));

  [TestCase("chr(0x1F600) == '😀'", "bool True")]
  [TestCase("ord('ab')", "raise TypeError")]
  [TestCase("'' in 'abc' and 'bd' not in 'abcd'", "bool True")]
  [TestCase("256 in b'ab'", "raise ValueError")]
  [TestCase("98 in b'ab'", "bool True")]
  [TestCase("'😀b'[1] == 'b' and len('😀') == 1", "bool True")]
  [TestCase("'abc'[3]", "raise IndexError")]
  [TestCase("str('x') == 'x' and str() == ''", "bool True")]
  [TestCase("'\\ud800'.encode()", "raise UnicodeEncodeError")]
  public void StringOperations_WorkOnCodePoints(string expression, string expected)
    => Assert.That(PythonSnippet.Run($"def f():\n    return {expression}\n", "f"), Is.EqualTo(expected));

  /// <summary>
  /// Test design 15: a lone surrogate is a code point of its own, but a lone high surrogate before a
  /// lone low one would be one code point in UTF-16 where Python has two, and is refused.
  /// </summary>
  [Test]
  public void LoneSurrogates_AreCodePointsAndAPairOfThemIsRefused() {
    Assert.That(PythonSnippet.Run("def f() -> int:\n    return len('\\ud800') * 10 + len('😀')\n", "f"), Is.EqualTo("int 11"));
    Assert.That(PythonSnippet.Refusals("def f() -> int:\n    return len('\\ud83d\\ude00')\n"),
      Has.Some.Contains("error PLY1822: string operation 'a str of a lone high surrogate before a lone low one"));
  }

  /// <summary>Iterables as lazy sequences: bytes, tuples, strs, enumerate and zip in comprehensions, map and filter, range with every step.</summary>
  [TestCase("sum(b'\\x01\\x02')", "int 3")]
  [TestCase("list(b'ab')[1]", "int 98")]
  [TestCase("max((3, 1, 2))", "int 3")]
  [TestCase("sum([x for x in (1, 2)])", "int 3")]
  [TestCase("list(enumerate([5, 6], 1))[1][0]", "int 2")]
  [TestCase("sum([i * x for i, x in enumerate([3, 4])])", "int 4")]
  [TestCase("len(list(zip([1, 2], [3, 4, 5])))", "int 2")]
  [TestCase("sum([a * b for a, b in zip(range(3), [4, 5, 6])])", "int 17")]
  [TestCase("list(reversed([1, 2, 3]))[0]", "int 3")]
  [TestCase("list(map(lambda v: v > 1, [1, 2]))[1]", "bool True")]
  [TestCase("len(list(filter(lambda v: v, [0, 1, 2])))", "int 2")]
  [TestCase("any(x for x in 'ab' if x == 'b')", "bool True")]
  [TestCase("list(range(1, 10, 3))[-1] * 10 + list(range(10, 0, -3))[-1]", "int 71")]
  [TestCase("list(range(0, 5, 0))", "raise ValueError")]
  [TestCase("list(range(0, 5, step))", "raise ValueError")]
  [TestCase("tuple((1, 2))[1]", "int 2")]
  [TestCase("sorted('cab')[0] == 'a'", "bool True")]
  public void IterablesAsSequences_AreEnumeratedLazily(string expression, string expected)
    => Assert.That(PythonSnippet.Run($"def f():\n    step = 0\n    return {expression}\n", "f"), Is.EqualTo(expected));

  [TestCase("list(zip([1], (1, 2)))", "error PLY1820: sequence operation 'zip with a second argument of the type tuple[int, int]' is not supported")]
  [TestCase("list(reversed('ab'))", "error PLY1813: iteration over 'reversed of the type str' is not supported")]
  [TestCase("reversed([1])[0]", "error PLY1802: 'an index of a value of the type Iterator[int]' is not supported")]
  [TestCase("list(enumerate(1))", "error PLY1813: iteration over '1' of the type int is not supported")]
  public void IterableMisuse_IsRefused(string expression, string diagnostic)
    => Assert.That(PythonSnippet.Refusals($"def f() -> int:\n    {expression}\n    return 0\n"), Has.Some.Contains(diagnostic));

  /// <summary>Test design 17: the operations outside the subset, each with its code.</summary>
  [TestCase("a.sort()", "error PLY1820: sequence operation 'sort' is not supported")]
  [TestCase("a.insert(0, 1)", "error PLY1820: sequence operation 'insert' is not supported")]
  [TestCase("a.pop(0)", "error PLY1820: sequence operation 'pop' with these arguments is not supported")]
  [TestCase("a.extend(1)", "error PLY1802: 'extend of a list of int by a value of the type int' is not supported")]
  [TestCase("iter(a)", "error PLY1820: sequence operation 'iter' is not supported")]
  [TestCase("a is a", "error PLY1820: sequence operation 'is between values of the types list[int] and list[int]' is not supported")]
  [TestCase("a < a", "error PLY1820: sequence operation '< of the types list[int] and list[int]' is not supported")]
  [TestCase("tuple(a)", "error PLY1820: sequence operation 'tuple of a value of the type list[int], whose length is not known' is not supported")]
  [TestCase("1 in 5", "error PLY1820: sequence operation 'in of the types int and int' is not supported")]
  [TestCase("[*a]", "error PLY1820: sequence operation '*a in a display' is not supported")]
  [TestCase("max(a, b=1)", "error PLY1820: sequence operation 'max(b=…)' is not supported")]
  [TestCase("sum([0.5])", "error PLY1820: sequence operation 'sum of float values', which CPython adds with compensation, is not supported")]
  [TestCase("sorted(a, a)", "error PLY1820: sequence operation 'sorted(a, a)' is not supported")]
  [TestCase("a[1:2, 3]", "error PLY1821: slice form 'a[1:2, 3]' is not supported")]
  [TestCase("f'{a}'", "error PLY1822: string operation 'f-string' is not supported")]
  [TestCase("', '.join(['a'])", "error PLY1822: string operation 'join' is not supported")]
  [TestCase("str(1)", "error PLY1822: string operation 'str() of a value of the type int' is not supported")]
  [TestCase("str(1, 2)", "error PLY1822: string operation 'str(1, 2)' is not supported")]
  [TestCase("repr(a)", "error PLY1822: string operation 'repr(a)' is not supported")]
  [TestCase("'a' < 'b'", "error PLY1822: string operation 'ordering of strings by <' is not supported")]
  [TestCase("'a'.encode('latin-1')", "error PLY1822: string operation 'encode to 'latin-1'' is not supported")]
  [TestCase("'a'.encode('utf-8', 'strict')", "error PLY1822: string operation 'encode with an error handler' is not supported")]
  [TestCase("b'a'.decode()", "error PLY1822: string operation 'decode' is not supported")]
  [TestCase("bytes('a')", "error PLY1802: 'bytes('a') of a str without an encoding, which raises TypeError,' is not supported")]
  [TestCase("bytes(1.5)", "error PLY1802: 'bytes(1.5) of a value of the type float' is not supported")]
  [TestCase("bytes(1, 'utf-8')", "error PLY1802: 'bytes(1, 'utf-8') of a value of the type int with an encoding, which raises TypeError,' is not supported")]
  [TestCase("len(1)", "error PLY1802: 'len of a value of the type int, which raises TypeError,' is not supported")]
  [TestCase("a[1.5]", "error PLY1802: 'an index of the type float, which raises TypeError,' is not supported")]
  [TestCase("a[1.5:]", "error PLY1802: 'a slice bound of the type float, which raises TypeError,' is not supported")]
  [TestCase("(1, 2)[a[0]]", "error PLY1802: 'the index a[0] of a tuple of 2 items; a tuple is indexed by a constant within it' is not supported")]
  [TestCase("[1] + [1.5]", "error PLY1802: '+ of the types list[int] and list[float], which raises TypeError,' is not supported")]
  [TestCase("ord(1)", "error PLY1802")]
  [TestCase("chr(1.5)", "error PLY1802")]
  [TestCase("list()", "error PLY1801: the type of the elements of 'list()' is not known")]
  public void UnsupportedSequenceOperation_IsRefusedWithItsCode(string expression, string diagnostic)
    => Assert.That(PythonSnippet.Refusals($"def f() -> int:\n    a = [1]\n    b = 2\n    {expression}\n    return 0\n"), Has.Some.Contains(diagnostic));

  [Test]
  public void KeyOfNone_ComparesTheElementsThemselves()
    => Assert.That(PythonSnippet.Run("def f() -> int:\n    return min([3, 1, 2], key=None) * 10 + sorted([2, 1], key=None)[0] + max([1], default=1, key=None)\n", "f"), Is.EqualTo("int 12"));

  [TestCase("    a[1:3] = [9]\n", "error PLY1821: slice form 'a[1:3] = …' is not supported")]
  [TestCase("    del a[0]\n", "error PLY1820: sequence operation 'del a[0]' is not supported")]
  [TestCase("    del a[1:2]\n", "error PLY1821: slice form 'del a[1:2]' is not supported")]
  [TestCase("    b'ab'[0] = 1\n", "error PLY1802: 'assignment to an element of a value of the type bytes, which raises TypeError,' is not supported")]
  [TestCase("    c = bytearray(1)\n    c[0] = 1.5\n", "error PLY1802: 'storing a value of the type float into a bytearray, which raises TypeError,' is not supported")]
  [TestCase("    for x, y, z in zip(a, a, a):\n        pass\n", "error PLY1820: sequence operation 'zip of other than two sequences' is not supported")]
  [TestCase("    for x, y, z in zip(a, a):\n        pass\n", "error PLY1810: statement 'unpacking a value of the type tuple[int, int] into 3 names")]
  [TestCase("    for i, x in enumerate(a, 1, 2):\n        pass\n", "error PLY1813: iteration over 'enumerate(a, 1, 2)' is not supported")]
  [TestCase("    for i, x in enumerate('ab'):\n        pass\n", "error PLY1813: iteration over 'enumerate('ab')' is not supported: enumerate of the type str")]
  [TestCase("    for x, y in zip('a', a):\n        pass\n", "error PLY1813")]
  [TestCase("    for x in reversed('ab'):\n        pass\n", "error PLY1813: iteration over 'reversed('ab')' is not supported: reversed of the type str")]
  [TestCase("    for x in reversed(a, a):\n        pass\n", "error PLY1813")]
  [TestCase("    x = []\n    return len(x)\n", "error PLY1801: the type of 'x' in 'f' is not known: nothing tells the type of its elements")]
  public void UnsupportedSequenceStatement_IsRefusedWithItsCode(string body, string diagnostic)
    => Assert.That(PythonSnippet.Refusals($"def f() -> int:\n    a = [1]\n{body}    return 0\n"), Has.Some.Contains(diagnostic));

  /// <summary>Test design 18: the struct formats outside the portable subset.</summary>
  [TestCase("struct.unpack('I', b)", "only the formats with an explicit byte order")]
  [TestCase("struct.unpack('=I', b)", "only the formats with an explicit byte order")]
  [TestCase("struct.unpack('<4s', b)", "the letter 's' is not imported")]
  [TestCase("struct.unpack(f, b)", "the format is not a constant")]
  [TestCase("struct.unpack('<5000B', b)", "a count beyond 4096")]
  [TestCase("struct.unpack('<2', b)", "a count without a letter")]
  [TestCase("struct.pack('<B')", "it packs 1 values, the call gives 0, which raises struct.error")]
  [TestCase("int.from_bytes(b, f)", "the byte order is a constant \"little\" or \"big\"")]
  public void UnsupportedLayout_IsRefusedWithPLY1823(string expression, string reason)
    => Assert.That(PythonSnippet.Refusals($"import struct\n\ndef g(b: bytes, f: str) -> int:\n    {expression}\n    return 0\n"), Has.Some.Contains("error PLY1823").And.Contains(reason));

  [TestCase("struct.pack('<H', 1.5)", "error PLY1802: 'packing a value of the type float with the letter H, which raises struct.error,' is not supported")]
  [TestCase("struct.unpack('<H', 1)", "error PLY1802: 'struct.unpack of a value of the type int' is not supported")]
  [TestCase("struct.unpack_from('<H', 1)", "error PLY1802: 'struct.unpack_from of a value of the type int' is not supported")]
  [TestCase("struct.calcsize('<H', 1)", "error PLY1802: 'struct.calcsize with 2 arguments' is not supported")]
  [TestCase("struct.unpack()", "error PLY1802: 'struct.unpack without a format' is not supported")]
  [TestCase("int.from_bytes(1, 'big')", "error PLY1802: 'int.from_bytes of a value of the type int' is not supported")]
  [TestCase("int.from_bytes()", "error PLY1802: 'int.from_bytes without its bytes' is not supported")]
  [TestCase("int.bit_length(1)", "error PLY1802: 'int.bit_length' is not supported")]
  [TestCase("int.from_bytes(b'', 'big', signed=b)", "error PLY1802: 'signed= of a value other than True or False' is not supported")]
  public void UnsupportedLayoutCall_IsRefused(string expression, string diagnostic)
    => Assert.That(PythonSnippet.Refusals($"import struct\n\ndef g(b: bool) -> int:\n    {expression}\n    return 0\n"), Has.Some.Contains(diagnostic));

  [Test]
  public void StructFormats_ParseIntoFieldsAndSizes() {
    var (format, problem) = StructFormat.Parse("> h 2x 3B ?d");
    Assert.That(problem, Is.Null);
    Assert.That(format!.Size, Is.EqualTo(2 + 2 + 3 + 1 + 8));
    Assert.That(format.Fields.Select(field => (field.Letter, field.Offset)), Is.EqualTo(new[] { ('h', 0), ('B', 4), ('B', 5), ('B', 6), ('?', 7), ('d', 8) }));
    Assert.That(format.Little, Is.False);
    Assert.That(format.Fields.Select(field => field.Signed), Is.EqualTo(new[] { true, false, false, false, false, true }));
    Assert.That(StructFormat.Parse("").Problem, Does.Contain("explicit byte order"));
    Assert.That(StructFormat.Parse("<lLqQfi").Format!.Size, Is.EqualTo(4 + 4 + 8 + 8 + 4 + 4));
  }

}
