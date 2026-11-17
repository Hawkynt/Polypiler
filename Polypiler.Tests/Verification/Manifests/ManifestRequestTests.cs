using NUnit.Framework;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Tests.Verification.Manifests;

/// <summary>
/// The parts of a manifest <c>translate</c> adds (#180): <c>requestedConfig</c>, the canonical command
/// line, and <c>verification.result</c>, which says whether every compared vector passed.
/// </summary>
[TestFixture]
public sealed class ManifestRequestTests {

  private static ManifestVerification Verification(long total, long passed, long failed)
    => new("v.json", "hand", new("accurate", "exact", "signed-zero", [], [], new(passed, 0, 0, null)), total, passed, failed);

  [Test]
  public void TheRequestedConfig_IsWrittenAndReadBack() {
    var manifest = ManifestFixtures.Minimal with { RequestedConfig = "translate a.cs --from csharp --to javascript" };
    var text = ManifestWriter.Serialize(manifest);
    var (read, diagnostics) = ManifestFixtures.Parse(text);
    Assert.That((diagnostics, read!.RequestedConfig), Is.EqualTo((System.Array.Empty<Polypiler.Core.Diagnostic>(), manifest.RequestedConfig)));
    Assert.That(text, Does.Contain("\"requestedConfig\": \"translate a.cs --from csharp --to javascript\""));
  }

  [Test]
  public void AManifestWithoutARequestedConfig_HasNone() {
    var text = ManifestWriter.Serialize(ManifestFixtures.Minimal);
    Assert.That((text.Contains("requestedConfig", System.StringComparison.Ordinal), ManifestFixtures.Parse(text).Manifest!.RequestedConfig), Is.EqualTo((false, (string?)null)));
  }

  [TestCase(3, 3, 0, true, "passed")]
  [TestCase(1, 1, 0, true, "passed")]
  [TestCase(3, 2, 1, false, "failed")]
  [TestCase(0, 0, 0, false, "failed")]
  [TestCase(3, 2, 0, false, "failed")]
  public void TheResult_PassesOnlyWhenEveryVectorOfAtLeastOnePassed(long total, long passed, long failed, bool expected, string written) {
    var verification = Verification(total, passed, failed);
    Assert.That(ManifestStateRule.Passed(verification), Is.EqualTo(expected));
    var text = ManifestWriter.Serialize(ManifestFixtures.Minimal with { Build = new("node", "node --check a.mjs", 0), Execution = new("node a.mjs", 0), Verification = verification });
    Assert.That(text, Does.Contain($"\"result\": \"{written}\""));
    Assert.That(ManifestFixtures.Parse(text).Diagnostics, Is.Empty);
  }

  [Test]
  public void TheResultOfNothing_IsRefused()
    => Assert.That(() => ManifestStateRule.Passed(null!), Throws.ArgumentNullException);

}
