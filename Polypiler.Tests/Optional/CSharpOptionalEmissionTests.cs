using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Optional;

/// <summary>
/// Optional values written by the C# backend for every profile: the fixture compiled against the
/// reference assemblies of the profile at its language version and run against what .NET computes of
/// the original, and the forms of the lowering table on both sides of each first profile.
/// </summary>
[TestFixture]
internal sealed class CSharpOptionalEmissionTests {

  private static string Emitted(CSharpProfile profile) => LegalizedCSharp.Emit(OptionalFixtures.Imported(), profile)[0].Content;

  /// <summary>Test design 1: the fixture compiles for every profile and computes what .NET computes.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void Fixture_CompilesForTheProfile_AndRunsLikeDotNet(CSharpProfile profile) {
    var text = Emitted(profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, false);
    foreach (var call in OptionalFixtures.Calls())
      Assert.That(build.Outcome(call), Is.EqualTo(PrototypeFixture.InvokedOutcome(OptionalFixtures.Compiled(call.Method), call)), $"{call}\n{text}");
  }

  /// <summary>Test design 9: .Value of none throws the runtime's own exception in every profile.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void ValueOfNone_ThrowsInvalidOperationException(CSharpProfile profile) {
    var method = GeneratedCSharp.Compile(Emitted(profile), false).Method("ValueOfNone");
    var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, []));
    Assert.That(thrown!.InnerException, Is.TypeOf<System.InvalidOperationException>());
    Assert.That(thrown.InnerException!.Message, Is.EqualTo("Nullable object must have a value."));
  }

  /// <summary>Test design 7: ?. from C# 6 (net4.6) on, the temporary and the conditional before; ??= from C# 8 (netstandard2.1) on.</summary>
  [TestCase("net3.5", "a?.Length", false)]
  [TestCase("net3.5", "(a == null) ? (int?)null : (int?)a.Length", true)]
  [TestCase("net4.6", "a?.Length ?? -1", true)]
  [TestCase("net4.6", "a?[i] ?? -1", true)]
  [TestCase("netstandard2.0", "q = 7;", true)]
  [TestCase("netstandard2.0", "??=", false)]
  [TestCase("netstandard2.1", "q ??= 7;", true)]
  [TestCase("netstandard2.1", "a ??= new int[3];", true)]
  [TestCase("net4.6", "b?.V ?? -1", true)]
  [TestCase("net2.0", "public static int? Find(int[] a, int v)", true)]
  [TestCase("net2.0", "Find(a, v) ?? -1", true)]
  public void Form_IsTheOneOfTheProfile(string profile, string form, bool present) {
    var text = Emitted(CSharpProfile.Parse(profile));
    Assert.That(text, present ? Does.Contain(form) : Does.Not.Contain(form));
  }

  /// <summary>Test design 12: the C# backend writes every node of optional values in every profile, so PLY1053 never names one.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void Check_FindsNothingMissing(CSharpProfile profile) => Assert.That(LegalizedCSharp.Check(OptionalFixtures.Imported(), profile), Is.Empty);

}
