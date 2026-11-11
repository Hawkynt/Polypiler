using System.Reflection;
using NUnit.Framework;
using Polypiler.Core;

namespace Polypiler.Tests;

[TestFixture]
public sealed class ProductInfoTests {

  [TestCase(null, ProductInfo.UnknownVersion, TestName = "Absent version reports the unknown version")]
  [TestCase("", ProductInfo.UnknownVersion, TestName = "Empty version reports the unknown version")]
  [TestCase("1.2.3", "1.2.3", TestName = "Version without metadata is unchanged")]
  [TestCase("1.2.3+abc123", "1.2.3", TestName = "Build metadata is removed")]
  [TestCase("1.2.3+", "1.2.3", TestName = "Trailing plus is removed")]
  [TestCase("+abc", "", TestName = "Metadata only leaves an empty version")]
  [TestCase("1.2.3+a+b", "1.2.3", TestName = "Everything from the first plus is removed")]
  public void StripBuildMetadata(string? input, string expected)
    => Assert.That(ProductInfo.StripBuildMetadata(input), Is.EqualTo(expected));

  [Test]
  public void VersionOf_MissingAttribute_ReportsTheUnknownVersion()
    => Assert.That(ProductInfo.VersionOf(null), Is.EqualTo(ProductInfo.UnknownVersion));

  [Test]
  public void VersionOf_Attribute_StripsBuildMetadata()
    => Assert.That(ProductInfo.VersionOf(new AssemblyInformationalVersionAttribute("4.5.6+sha")), Is.EqualTo("4.5.6"));

  [Test]
  public void Version_HasNoBuildMetadata() {
    Assert.That(ProductInfo.Version, Does.Not.Contain("+"));
    Assert.That(ProductInfo.Version, Is.Not.Empty);
  }

}
