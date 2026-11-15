using System;
using NUnit.Framework;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Tests.Verification.Manifests;

/// <summary>The state rule: the highest state whose conditions hold, and why the next is not reached.</summary>
[TestFixture]
public sealed class ManifestStateRuleTests {

  private static readonly ManifestExactness _exact = new("accurate", "exact", "signed-zero", [], [], new(3, 0, 0, null));

  private static ManifestVerification Verification(long passed, long total, long failed = 0, string producer = "dotnet-sdk")
    => new("dotnet-sdk", producer, _exact, total, passed, failed);

  /// <summary>Build exit code, run exit code, passed and total (-1 for none), and the state they give.</summary>
  [TestCase(-1, -1, -1, -1, ManifestState.Generated, "no build is recorded")]
  [TestCase(1, -1, -1, -1, ManifestState.Generated, "the build exited with 1")]
  [TestCase(0, -1, -1, -1, ManifestState.Built, "no execution is recorded")]
  [TestCase(0, 2, -1, -1, ManifestState.Built, "the execution exited with 2")]
  [TestCase(0, 0, -1, -1, ManifestState.Executed, "no verification results are recorded")]
  [TestCase(0, 0, 0, 0, ManifestState.Executed, "the verification compared no vector")]
  [TestCase(0, 0, 1, 1, ManifestState.Verified, "")]
  [TestCase(0, 0, 2, 3, ManifestState.Executed, "the verification passed 2 of 3 with 0 failed")]
  [TestCase(0, 0, 3, 3, ManifestState.Verified, "")]
  public void State_IsTheHighestWhoseConditionsHold(int build, int run, int passed, int total, ManifestState state, string missing) {
    var derived = ManifestStateRule.Derive(
      build < 0 ? null : new ManifestBuild("node", "node --check a.mjs", build),
      run < 0 ? null : new ManifestExecution("node a.test.mjs", run),
      total < 0 ? null : Verification(passed, total));
    Assert.That(derived, Is.EqualTo((state, missing)));
  }

  [Test]
  public void AFailureCount_KeepsVerifiedAway_EvenWhenPassedEqualsTotal()
    => Assert.That(ManifestStateRule.Derive(new("node", "b", 0), new("r", 0), Verification(3, 3, 1)),
      Is.EqualTo((ManifestState.Executed, "the verification passed 3 of 3 with 1 failed")));

  [Test]
  public void Polypiler_NeverVerifiesItself()
    => Assert.That(ManifestStateRule.Derive(new("node", "b", 0), new("r", 0), Verification(3, 3, 0, "polypiler")),
      Is.EqualTo((ManifestState.Executed, "the expected values were produced by polypiler itself")));

  [Test]
  public void AnExecutionWithoutBuild_IsNoEvidence()
    => Assert.That(ManifestStateRule.Derive(null, new("r", 0), Verification(3, 3)).State, Is.EqualTo(ManifestState.Generated));

  [Test]
  public void TheModelsState_IsDerived() {
    var manifest = ManifestFixtures.Minimal with { Build = new("node", "b", 0), Execution = new("r", 0), Verification = Verification(1, 1) };
    Assert.That((ManifestFixtures.Minimal.State, manifest.State, (manifest with { Verification = null }).State),
      Is.EqualTo((ManifestState.Generated, ManifestState.Verified, ManifestState.Executed)));
  }

  [TestCase(ManifestState.Generated, "generated")]
  [TestCase(ManifestState.Built, "built")]
  [TestCase(ManifestState.Executed, "executed")]
  [TestCase(ManifestState.Verified, "verified")]
  public void Names_ReadBack(ManifestState state, string name)
    => Assert.That((ManifestStateRule.Name(state), ManifestStateRule.Parse(name)), Is.EqualTo((name, state)));

  [Test]
  public void AnUnknownName_IsRefused()
    => Assert.That(() => ManifestStateRule.Parse("certified"), Throws.ArgumentException.With.Message.Contains("'certified' is not a manifest state"));

}
