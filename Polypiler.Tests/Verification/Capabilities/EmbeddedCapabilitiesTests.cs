using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Verification.Capabilities;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Verification.Capabilities;

/// <summary>The capability report the <c>polypiler</c> executable carries: <c>docs/capabilities.json</c> as a resource.</summary>
[TestFixture]
public sealed class EmbeddedCapabilitiesTests {

  [Test]
  public void TheExecutable_CarriesTheCommittedReport() {
    using var resource = typeof(EmbeddedCapabilities).Assembly.GetManifestResourceStream(EmbeddedCapabilities.ResourceName)!;
    using var reader = new StreamReader(resource);
    Assert.That(reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal), Is.EqualTo(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "docs", CapabilityReport.JsonFile)).Replace("\r\n", "\n", StringComparison.Ordinal)));

    var report = EmbeddedCapabilities.Load();
    Assert.That(report.Pairs, Is.Not.Empty);
    Assert.That(report.Find("csharp", "net10.0", "javascript", "es2022"), Is.Not.Null);
  }

  /// <summary>Test design 20: an executable built without the resource names it.</summary>
  [Test]
  public void AMissingResource_IsNamed()
    => Assert.That(() => EmbeddedCapabilities.Load(() => typeof(object).Assembly.GetManifestResourceStream(EmbeddedCapabilities.ResourceName)),
      Throws.InvalidOperationException.With.Message.Contains("'Polypiler.Cli.capabilities.json'"));

  [Test]
  public void ADamagedResource_IsInvalidData()
    => Assert.That(() => EmbeddedCapabilities.Load(() => new MemoryStream("{}"u8.ToArray())), Throws.TypeOf<InvalidDataException>());

}
