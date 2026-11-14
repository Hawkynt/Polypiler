using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Benchmarks;
using static Polypiler.Tests.Verification.Benchmarks.BenchTesting;

namespace Polypiler.Tests.Verification.Benchmarks;

/// <summary>The environment object, its fingerprint and the first field in which two differ (test design 15).</summary>
[TestFixture]
public sealed class BenchmarkEnvironmentTests {

  [Test]
  public void Canonical_IsTheFixedPropertyOrder_WithToolchainsInKeyOrder() {
    var environment = new BenchmarkEnvironment("linux", "6.8.0", "x64", "CPU", 8, new Dictionary<string, string> { ["node"] = "24.22.2", ["dotnet-sdk"] = "10.0.404" });
    Assert.That(environment.Canonical(), Is.EqualTo("""{"os":"linux","osVersion":"6.8.0","architecture":"x64","cpuModel":"CPU","logicalCores":8,"toolchains":{"dotnet-sdk":"10.0.404","node":"24.22.2"}}"""));
  }

  [Test]
  public void Fingerprint_IsTheSha256OfTheCanonicalJson() {
    var environment = Machine();
    Assert.That(environment.Fingerprint, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(environment.Canonical())))));
    Assert.That(Machine().Fingerprint, Is.EqualTo(environment.Fingerprint));
    Assert.That(Machine(cores: 4).Fingerprint, Is.Not.EqualTo(environment.Fingerprint));
  }

  [Test]
  public void EqualEnvironments_HaveNoDifference() => Assert.That(Machine().FirstDifference(Machine()), Is.Null);

  [Test]
  public void TheCoreCount_IsNamedWithBothValues() => Assert.That(Machine(8).FirstDifference(Machine(4)), Is.EqualTo(("logicalCores", "8", "4")));

  [Test]
  public void AToolchainVersion_IsNamedByItsKey() => Assert.That(Machine(node: "24.22.2").FirstDifference(Machine(node: "22.23.3")), Is.EqualTo(("toolchains.node", "24.22.2", "22.23.3")));

  [Test]
  public void TheFirstDifferingField_IsNamed_InTheFixedOrder() {
    var other = Machine(4, "1.0.0") with { Os = "windows" };
    Assert.That(Machine().FirstDifference(other), Is.EqualTo(("os", "linux", "windows")));
    Assert.That(Machine().FirstDifference(Machine() with { OsVersion = "7" })!.Value.Field, Is.EqualTo("osVersion"));
    Assert.That(Machine().FirstDifference(Machine() with { Architecture = "arm64" })!.Value.Field, Is.EqualTo("architecture"));
    Assert.That(Machine().FirstDifference(Machine() with { CpuModel = "Other" })!.Value.Field, Is.EqualTo("cpuModel"));
  }

  [Test]
  public void AToolchainOnOneSideOnly_ReadsNoneOnTheOther() {
    var both = Machine() with { Toolchains = new Dictionary<string, string> { ["node"] = "24.22.2", ["dotnet-sdk"] = "10.0.404" } };
    Assert.That(both.FirstDifference(Machine()), Is.EqualTo(("toolchains.dotnet-sdk", "10.0.404", "none")));
    Assert.That(Machine().FirstDifference(both), Is.EqualTo(("toolchains.dotnet-sdk", "none", "10.0.404")));
  }

  [Test]
  public void TheArguments_MustNotBeNull() {
    Assert.That(() => Machine() with { Toolchains = null! }, Throws.ArgumentNullException);
    Assert.That(() => Machine().FirstDifference(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Detect_DescribesThisMachine() {
    var environment = BenchmarkEnvironment.Detect(new Dictionary<string, string> { ["node"] = "1" });
    Assert.That(environment.Os, Is.AnyOf("linux", "windows", "macos", "other"));
    Assert.That((environment.LogicalCores, environment.Toolchains["node"]), Is.EqualTo((Environment.ProcessorCount, "1")));
    Assert.That(environment.CpuModel, Is.Not.Empty);
    Assert.That(environment.Architecture, Is.EqualTo(environment.Architecture.ToLowerInvariant()));
  }

  [Test]
  public void ReadIfPresent_ReadsAFile_AndHasNothingForAMissingOne() {
    Assert.That(BenchmarkEnvironment.ReadIfPresent(System.IO.Path.Combine(FixtureFolder, "size", "abc.txt")), Is.EqualTo("abc"));
    Assert.That(BenchmarkEnvironment.ReadIfPresent(System.IO.Path.Combine(FixtureFolder, "size", "none.txt")), Is.Null);
  }

  [TestCase("LINUX", "linux")]
  [TestCase("WINDOWS", "windows")]
  [TestCase("OSX", "macos")]
  [TestCase("FREEBSD", "other")]
  public void OsName_NamesThePlatform(string platform, string expected) {
    Assert.That(BenchmarkEnvironment.OsName(candidate => candidate == System.Runtime.InteropServices.OSPlatform.Create(platform)), Is.EqualTo(expected));
    Assert.That(() => BenchmarkEnvironment.OsName(null!), Throws.ArgumentNullException);
  }

  [TestCase("processor\t: 0\nmodel name\t: Intel(R) Core(TM) i9\nflags: x\n", null, "Intel(R) Core(TM) i9")]
  [TestCase("processor\t: 0\n", "Intel64 Family 6 Model 141", "Intel64 Family 6 Model 141")]
  [TestCase("model name without colon\n", null, "unknown")]
  [TestCase(null, "  ", "unknown")]
  [TestCase(null, null, "unknown")]
  public void CpuModelOf_PrefersCpuinfo_ThenTheProcessorIdentifier(string? cpuinfo, string? identifier, string expected)
    => Assert.That(BenchmarkEnvironment.CpuModelOf(cpuinfo, identifier), Is.EqualTo(expected));

}
