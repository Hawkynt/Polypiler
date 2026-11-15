using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Python;

namespace Polypiler.Tests.BigInt;

/// <summary>
/// The arbitrary-precision integer written by the Python backend: Python's own <c>int</c> in every
/// profile, its operators where they mean what the IR's do and a helper where they do not. What the
/// module computes CPython decides in the acceptance tests (<c>PythonBigIntTests</c>).
/// </summary>
[TestFixture]
public sealed class PythonBigIntEmissionTests {

  [TestCase("py3.8"), TestCase("py3.14")]
  public void EveryProfile_WritesEveryOperation(string profile)
    => Assert.That(PythonFixtures.Translate(BigIntCases.Module(), PythonProfile.Parse(profile)).Problems, Is.Empty);

  [TestCase("def add(a: int, b: int) -> int:\n    return a + b")]
  [TestCase("return _ply_big_div(a, b)")]
  [TestCase("return _ply_big_rem(a, b)")]
  [TestCase("return _ply_big_shl(a, n)")]
  [TestCase("return _ply_big_float(a)")]
  [TestCase("return _ply_float_big(x)")]
  [TestCase("return ~a")]
  [TestCase("return -a")]
  [TestCase("return 1000000000000000000000000000007 - -5")]
  [TestCase("return a & 0xFFFF_FFFF_FFFF_FFFF")]
  [TestCase("return _ply_checked(a, -9223372036854775808, 9223372036854775807)")]
  public void Forms_AreTheOnesOfInt(string form)
    => Assert.That(PythonFixtures.Translate(BigIntCases.Module(), PythonProfile.Parse("py3.14")).Source, Does.Contain(form));

}
