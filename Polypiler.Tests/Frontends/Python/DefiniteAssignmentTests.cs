using NUnit.Framework;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// Test design 7 and 14 of #449: a local read where CPython could raise <c>UnboundLocalError</c> is
/// refused with PLY1811; every path that assigns it first imports.
/// </summary>
[TestFixture]
public sealed class DefiniteAssignmentTests {

  private static string F(string body, string parameters = "n: int") => $"def f({parameters}) -> int:\n{body}";

  [TestCase("    if n:\n        x = 1\n    return x\n", "(3,12)", "x")]
  [TestCase("    for i in range(n):\n        x = i\n    return x\n", "(3,12)", "x")]
  [TestCase("    while n > 0:\n        x = n\n        n -= 1\n    return x\n", "(4,12)", "x")]
  [TestCase("    x += 1\n    return 0\n", "(1,5)", "x")]
  [TestCase("    y = x\n    x = 1\n    return y\n", "(1,9)", "x")]
  [TestCase("    while n > 0:\n        y = z\n        z = 1\n        n -= 1\n    return 0\n", "(2,13)", "z")]
  [TestCase("    match n:\n        case 1:\n            x = 1\n    return x\n", "(4,12)", "x")]
  [TestCase("    match n:\n        case 1:\n            x = 1\n        case m if m > x:\n            x = 2\n        case _:\n            x = 3\n    return x\n", "(4,23)", "x")]
  [TestCase("    for i in range(n):\n        if i:\n            break\n        x = 1\n    else:\n        x = 2\n    return x\n", "(7,12)", "x")]
  public void PossiblyUnboundRead_IsRefused(string body, string position, string name) {
    var refusals = PythonSnippet.Refusals(F(body));
    var (line, column) = (int.Parse(position[1..position.IndexOf(',')]) + 1, position[(position.IndexOf(',') + 1)..^1]);
    Assert.That(refusals, Has.Some.EqualTo($"snippet.py({line},{column}): error PLY1811: '{name}' may be unbound here; python would raise UnboundLocalError"));
  }

  [TestCase("    if n:\n        x = 1\n    else:\n        x = 2\n    return x\n", 0L, "int 2")]
  [TestCase("    for i in range(3):\n        pass\n    return i\n", 0L, "int 2")]
  [TestCase("    i = 9\n    for i in range(n):\n        pass\n    return i\n", 0L, "int 9")]
  [TestCase("    while True:\n        x = n\n        break\n    return x\n", 4L, "int 4")]
  [TestCase("    while 1:\n        if n > 3:\n            x = n\n            break\n        n += 1\n    return x\n", 1L, "int 4")]
  [TestCase("    for i in range(n):\n        x = i\n        break\n    else:\n        x = -1\n    return x\n", 0L, "int -1")]
  [TestCase("    match n:\n        case 1:\n            x = 10\n        case m:\n            x = m\n    return x\n", 5L, "int 5")]
  [TestCase("    if n:\n        return 1\n    x = 2\n    return x\n", 0L, "int 2")]
  [TestCase("    while n > 0:\n        n -= 1\n        if n == 2:\n            continue\n    return n\n", 4L, "int 0")]
  public void AssignedOnEveryPath_Imports(string body, long n, string expected)
    => Assert.That(PythonSnippet.Run(F(body), "f", n), Is.EqualTo(expected));

  /// <summary>A function whose end is reachable returns None there; one that returns a value on another path is refused.</summary>
  [Test]
  public void ValueOnSomePathsAndNoneOnOthers_IsRefused()
    => Assert.That(PythonSnippet.Refusals("def f(n: int) -> int:\n    if n:\n        return 1\n"), Has.Some.Contains("error PLY1803: 'f result' holds values of the types int and None"));

}
