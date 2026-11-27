using System.Globalization;
using NUnit.Framework;
using Polypiler.Core.MachineSemantics;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>Binds the ISA feature level scenarios without relying on the test host's CPU.</summary>
[Binding, Scope(Feature = "x86-64 CPU feature levels")]
public sealed class X64FeatureProfileSteps {

  private X64FeatureProfile? _profile;

  [When("^the x86-64 profile \"([^\"]*)\" is selected$")]
  public void Select(string id) => this._profile = X64FeatureProfile.Find(id);

  [Then("^it requires \"([^\"]*)\"$")]
  public void Requires(string features) {
    Assert.That(this._profile, Is.Not.Null);
    foreach (var feature in features.Split(','))
      Assert.That(this._profile!.RequiredFeatures, Does.Contain(feature));
  }

  [Then("^it does not require \"([^\"]*)\"$")]
  public void Omits(string features) {
    Assert.That(this._profile, Is.Not.Null);
    foreach (var feature in features.Split(','))
      Assert.That(this._profile!.RequiredFeatures, Does.Not.Contain(feature));
  }

  [Then("^it requires XCR0 bits \"0x([0-9A-F]+)\"$")]
  public void RequiresXcr0Bits(string bits) {
    Assert.That(this._profile, Is.Not.Null);
    Assert.That(this._profile!.RequiredXcr0Bits, Is.EqualTo(ulong.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
  }

  [Then("^no profile is found$")]
  public void Absent() => Assert.That(this._profile, Is.Null);

}
