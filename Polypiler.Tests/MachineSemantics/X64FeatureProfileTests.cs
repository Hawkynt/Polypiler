using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Polypiler.Core.MachineSemantics;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.MachineSemantics;

[TestFixture]
public sealed class X64FeatureProfileTests {

  private const string _baseline = "cmov,cx8,fpu,fxsr,mmx,osfxsr,sce,sse,sse2";
  private const string _v2 = _baseline + ",cx16,lahf-sahf,popcnt,sse3,sse4.1,sse4.2,ssse3";
  private const string _v3 = _v2 + ",avx,avx2,bmi1,bmi2,f16c,fma,lzcnt,movbe,osxsave";
  private const string _v4 = _v3 + ",avx512f,avx512bw,avx512cd,avx512dq,avx512vl";

  [TestCase("x86-64-v1", 0UL, _baseline)]
  [TestCase("x86-64-v2", 0UL, _v2)]
  [TestCase("x86-64-v3", 0x06UL, _v3)]
  [TestCase("x86-64-v4", 0xE6UL, _v4)]
  public void LevelsHaveTheExactPsAbiRequirements(string id, ulong xcr0Bits, string features) {
    var profile = X64FeatureProfile.Find(id)!;
    Assert.That(profile.Id, Is.EqualTo(id));
    Assert.That(profile.RequiredFeatures, Is.EquivalentTo(features.Split(',')));
    Assert.That(profile.RequiredXcr0Bits, Is.EqualTo(xcr0Bits));
  }

  [Test]
  public void BaselineAliasUsesTheCanonicalProfile()
    => Assert.That(X64FeatureProfile.Find("x86-64"), Is.SameAs(X64FeatureProfile.Find("x86-64-v1")).And.Not.Null);

  [TestCase(null)]
  [TestCase("")]
  [TestCase("X86-64-v3")]
  [TestCase(" x86-64-v2")]
  [TestCase("x86-64-v4 ")]
  [TestCase("x86-64-v0")]
  [TestCase("x86-64-v5")]
  public void UnknownNamesDoNotSelectAProfile(string? id)
    => Assert.That(X64FeatureProfile.Find(id), Is.Null);

  [Test]
  public void CompilerFeatureSelectionMatchesThePinnedGccOracle() {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "native", "x64", "features", "gcc-levels.txt");
    var sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    Assert.That(sha256, Is.EqualTo("dbffaeaf8f280bcdb5798ae84d647d2d282479eb3a51d5a9ab1e5edea20ef148"));

    foreach (var line in File.ReadLines(path)) {
      var fields = line.Split(' ', 2);
      var profile = X64FeatureProfile.Find(fields[0])!;
      // GCC's preprocessor has no individual macro for these psABI requirements.
      var features = profile.RequiredFeatures.Where(feature => feature is not ("cmov" or "cx8" or "fpu" or "osfxsr" or "sce" or "osxsave"));
      Assert.That(features, Is.EquivalentTo(fields[1].Split(',')), profile.Id);
    }
  }

}
