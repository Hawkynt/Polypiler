using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.JavaScript;

namespace Polypiler.Tests.BigInt;

/// <summary>
/// The arbitrary-precision integer written by the JavaScript backend: a <c>BigInt</c> from ECMAScript
/// 2020 on, refused with PLY1152 below, where a level has no arbitrary-precision type at all. What the
/// code computes node decides in the acceptance tests (<c>BigIntJavaScriptTests</c>).
/// </summary>
[TestFixture]
public sealed class JavaScriptBigIntEmissionTests {

  private static string Emitted(string profile) => LegalizedJavaScript.Emit(BigIntCases.Module(), JavaScriptProfile.Parse(profile))[0].Content;

  [TestCase("es2020"), TestCase("es2022")]
  public void LevelWithBigInt_WritesEveryOperation(string profile)
    => Assert.That(LegalizedJavaScript.Check(BigIntCases.Module(), JavaScriptProfile.Parse(profile)), Is.Empty);

  [TestCase("es5"), TestCase("es2015"), TestCase("es2017")]
  public void LevelWithoutBigInt_RefusesItWithPLY1152(string profile)
    => Assert.That(LegalizedJavaScript.Check(BigIntCases.Module(), JavaScriptProfile.Parse(profile)).Select(diagnostic => diagnostic.ToString()),
      Has.Some.EqualTo($"error PLY1152: 'BigInt' (the arbitrary-precision integer, a Python int no fixed width holds) cannot be expressed in JavaScript profile '{profile}'"));

  [TestCase("1000000000000000000000000000007n - (-5n)")]
  [TestCase("$udiv64(a, b)")]
  [TestCase("$urem64(a, b)")]
  [TestCase("a << BigInt(n)")]
  [TestCase("Number(BigInt.asIntN(32, a))")]
  [TestCase("BigInt.asUintN(64, a)")]
  [TestCase("Number($bigfit(a, -2147483648n, 2147483647n))")]
  [TestCase("$f2lc(x, -1.7976931348623157e308, Infinity)")]
  [TestCase("return Number(a);")]
  [TestCase("~a")]
  public void Forms_AreTheOnesOfBigInt(string form) => Assert.That(Emitted("es2022"), Does.Contain(form));

}
