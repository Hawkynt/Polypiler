using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// An <c>int</c> is exact wherever the IR keeps an arbitrary-precision integer - a variable, an
/// argument, a result, a list, a sequence - and a 64-bit integer where it keeps none: an attribute, a
/// tuple, an optional, a module table or variable, an index. Reading such a place gives the exact
/// <c>int</c> again; storing one beyond 64 bits raises <c>OverflowError</c>, which the caveat
/// <c>py-int-held-64</c> announces. Every value is CPython's.
/// </summary>
[TestFixture]
public sealed class HeldIntTests {

  private static readonly BigInteger _huge = BigInteger.Pow(2, 70) + 3;

  /// <summary>A list holds ints of any magnitude, and sum, sorted, min and max stay exact.</summary>
  [TestCase("sum(xs)", "int 2361183241434822606850")]
  [TestCase("sorted(xs)[0]", "int -5")]
  [TestCase("max(xs)", "int 1180591620717411303428")]
  [TestCase("min(xs)", "int -5")]
  [TestCase("len(xs) + xs[-1]", "int 1180591620717411303431")]
  public void List_HoldsExactInts(string expression, string expected)
    => Assert.That(PythonSnippet.Run($"def f(n: int) -> int:\n    xs = [n, -5, n + 1]\n    return {expression}\n", "f", _huge), Is.EqualTo(expected));

  /// <summary>A range and an enumerate count exactly at any magnitude.</summary>
  [TestCase("for i in range(n, n + 3):\n        t += i", "int 3541774862152233910284")]
  [TestCase("for i in range(n + 4, n, -2):\n        t += i", "int 2361183241434822606860")]
  [TestCase("for i, x in enumerate([1, 2], n):\n        t += i * x", "int 3541774862152233910283")]
  public void RangeAndEnumerate_AreExact(string loop, string expected)
    => Assert.That(PythonSnippet.Run($"def f(n: int) -> int:\n    t = 0\n    {loop}\n    return t\n", "f", _huge), Is.EqualTo(expected));

  /// <summary>math.isqrt and math.gcd are exact beyond 64 bits; isqrt of a negative number raises ValueError.</summary>
  [TestCase("math.isqrt(n * n)", "int 1180591620717411303427")]
  [TestCase("math.isqrt(n * n - 1)", "int 1180591620717411303426")]
  [TestCase("math.gcd(n * 6, -n * 4)", "int 2361183241434822606854")]
  [TestCase("math.isqrt(-n)", "raise ValueError")]
  [TestCase("math.isqrt(1) + math.isqrt(0) + math.isqrt(15)", "int 4")]
  public void IsqrtAndGcd_AreExact(string expression, string expected)
    => Assert.That(PythonSnippet.Run($"import math\n\ndef f(n: int) -> int:\n    return {expression}\n", "f", _huge), Is.EqualTo(expected));

  /// <summary>An attribute, a tuple and an optional keep an int of 64 bits exactly, the edges included, and raise OverflowError beyond them.</summary>
  [TestCase("9223372036854775807", "int 9223372036854775807")]
  [TestCase("-9223372036854775808", "int -9223372036854775808")]
  [TestCase("9223372036854775808", "raise OverflowError")]
  [TestCase("-9223372036854775809", "raise OverflowError")]
  public void HeldPlaces_KeepSixtyFourBits(string literal, string expected) {
    const string source = "from dataclasses import dataclass\n\n@dataclass\nclass Box:\n    v: int\n\n"
      + "def f(n: int) -> int:\n    b = Box(n)\n    t = (n, 1)\n    o: int | None = n\n    assert o is not None\n    return b.v + t[0] + o - 2 * n\n";
    Assert.That(PythonSnippet.Run(source, "f", BigInteger.Parse(literal, System.Globalization.CultureInfo.InvariantCulture)), Is.EqualTo(expected));
  }

  /// <summary>A held int read back is the exact int: arithmetic on it goes beyond 64 bits without raising.</summary>
  [Test]
  public void HeldInt_ReadBack_IsExact()
    => Assert.That(PythonSnippet.Run("def f(n: int) -> int:\n    t = (n, 2)\n    return t[0] * t[0] + t[1]\n", "f", (BigInteger)long.MaxValue),
      Is.EqualTo("int 85070591730234615847396907784232501251"));

  /// <summary>A module that keeps an int in a held place says so with the caveat; one that keeps none does not.</summary>
  [Test]
  public void HeldPlaces_AnnounceTheCaveat() {
    var held = PythonSnippet.Import("def f(n: int) -> int:\n    t = (n, 1)\n    return t[1]\n");
    Assert.That(held.Caveats.Select(caveat => caveat.Id), Does.Contain(PythonFrontend.HeldInt));
    Assert.That(PythonFrontend.Describe(PythonFrontend.HeldInt), Does.Contain("OverflowError"));
    var exact = PythonSnippet.Import("def f(n: int) -> int:\n    return n * n\n");
    Assert.That(exact.Caveats.Select(caveat => caveat.Id), Does.Not.Contain(PythonFrontend.HeldInt));
  }

  /// <summary>A tuple of ints, a table and bytes are iterated as exact ints.</summary>
  [TestCase("sum(x * n for x in (1, 2, 3))", "int 7083549724304467820562")]
  [TestCase("sum(x * n for x in T)", "int 3541774862152233910281")]
  [TestCase("sum(x * n for x in b'\\x01\\x02')", "int 3541774862152233910281")]
  public void TupleTableAndBytes_IterateExactInts(string expression, string expected)
    => Assert.That(PythonSnippet.Run($"T = [1, 2]\n\ndef f(n: int) -> int:\n    return {expression}\n", "f", _huge), Is.EqualTo(expected));

  /// <summary>A module variable keeps 64 bits: a value beyond them raises when it is stored.</summary>
  [TestCase(5L, "int 5")]
  [TestCase(long.MaxValue, "raise OverflowError")]
  public void ModuleVariable_KeepsSixtyFourBits(long add, string expected)
    => Assert.That(PythonSnippet.Run("TOTAL = 0\n\ndef f(n: int) -> int:\n    global TOTAL\n    TOTAL += n\n    TOTAL += n - 5 + 5\n    return TOTAL - n\n", "f", (BigInteger)add), Is.EqualTo(expected));

}
