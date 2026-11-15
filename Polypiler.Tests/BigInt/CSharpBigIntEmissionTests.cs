using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.BigInt;

/// <summary>
/// The arbitrary-precision integer written by the C# backend: <c>System.Numerics.BigInteger</c> from
/// .NET Framework 4.6 on, compiled at every such profile and run against the interpreter on every
/// operation and conversion; refused with PLY1053 by the two profiles whose framework has no such type.
/// </summary>
[TestFixture]
public sealed class CSharpBigIntEmissionTests {

  private static string Emitted(CSharpProfile profile) => LegalizedCSharp.Emit(BigIntCases.Module(), profile)[0].Content;

  [TestCase("net4.6"), TestCase("net4.8"), TestCase("netstandard2.0"), TestCase("netcoreapp3.1"), TestCase("net8.0"), TestCase("net10.0")]
  public void Module_CompilesForEveryProfileWithTheType(string profile) {
    var text = Emitted(CSharpProfile.Parse(profile));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile, CSharpProfile.Parse(profile).LanguageVersionName), Is.Empty, text);
  }

  /// <summary>Every call has the interpreter's outcome, with overflow checking off and on: the wrapping conversions keep their bits either way.</summary>
  [TestCase(false), TestCase(true)]
  public void EveryOperation_ComputesWhatTheInterpreterComputes(bool checkOverflow) {
    var module = BigIntCases.Module();
    var text = Emitted(CSharpProfile.Default);
    var build = GeneratedCSharp.Compile(text, checkOverflow);
    var mismatches = BigIntCases.Calls().Select(call => (call, expected: BigIntCases.Interpreted(module, call.Function, call.Arguments), actual: Invoked(build.Method(call.Function), call.Arguments)))
      .Where(row => row.expected != row.actual)
      .Select(row => $"{row.call.Function}({string.Join(", ", row.call.Arguments.Select(BigIntCases.Show))}): interpreter {row.expected}, C# {row.actual}").ToList();
    Assert.That(mismatches.Take(20), Is.Empty, text);
  }

  [TestCase("net2.0"), TestCase("net3.5")]
  public void ProfileWithoutTheType_RefusesItWithPLY1053(string profile)
    => Assert.That(LegalizedCSharp.Check(BigIntCases.Module(), CSharpProfile.Parse(profile)).Select(diagnostic => diagnostic.ToString()),
      Is.EqualTo(new[] { $"error PLY1053: 'System.Numerics.BigInteger' (the arbitrary-precision integer, a Python int no fixed width holds) cannot be expressed in C# profile '{profile}'" }));

  [Test]
  public void Forms_AreTheTypesOwn() {
    var text = Emitted(CSharpProfile.Default);
    Assert.That(text, Does.Contain("global::System.Numerics.BigInteger.Parse(\"1000000000000000000000000000007\", global::System.Globalization.CultureInfo.InvariantCulture)"));
    Assert.That(text, Does.Contain("(global::System.Numerics.BigInteger)(-5L)"));
    Assert.That(text, Does.Contain("(int)(uint)(a & uint.MaxValue)"));
    Assert.That(text, Does.Contain("private static double BigToDouble(global::System.Numerics.BigInteger value)"));
  }

  private static string Invoked(MethodInfo method, object[] arguments) {
    try {
      return BigIntCases.Show(method.Invoke(null, arguments));
    } catch (TargetInvocationException exception) when (exception.InnerException is { } inner) {
      return "raise " + inner.GetType().Name;
    }
  }

}
