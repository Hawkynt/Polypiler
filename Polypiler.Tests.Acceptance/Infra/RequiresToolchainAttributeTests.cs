using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The outcomes of <see cref="RequiresToolchainAttribute"/> beyond the failure in CI, which
/// <c>tests/features/infra/infra-toolchains.feature</c> holds: inconclusive outside CI, inconclusive
/// on a platform the toolchain is not used on, and no outcome at all at the pinned version.
/// </summary>
[TestFixture, Category("EndToEnd")]
public sealed class RequiresToolchainAttributeTests {

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "toolchains");

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() => this._directory = Directory.CreateTempSubdirectory("polypiler-toolchain-").FullName;

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  [TestCase(null, TestName = "Without the CI variable an absent toolchain leaves the test inconclusive")]
  [TestCase("0", TestName = "A CI variable other than 1 leaves the test inconclusive")]
  public void RequiredAbsentToolchain_IsInconclusiveOutsideCi(string? require) {
    var attribute = new RequiresToolchainAttribute("ghost") { Lock = "tests/fixtures/infra/toolchains/ghost.lock" };
    var inconclusive = Assert.Throws<InconclusiveException>(() => attribute.Check(require))!;
    Assert.That(inconclusive.Message, Does.Contain("toolchain 'ghost'").And.Contain("PLY0021"));
  }

  [Test]
  public void RequiredToolchainOfAnotherPlatform_IsInconclusive() {
    var lockFile = Path.Combine(this._directory, "other.lock");
    var other = OperatingSystem.IsWindows() ? "linux" : "windows";
    File.WriteAllText(lockFile, $"schema: 1\ntoolchains:\n  elsewhere:\n    version: \"1\"\n    probe: [x]\n    versionPattern: '(?<version>.+)'\n    platforms: [{other}]\n    licence: MIT\n    source: x\n");
    var attribute = new RequiresToolchainAttribute("elsewhere") { Lock = lockFile };
    Assert.That(Assert.Throws<InconclusiveException>(() => attribute.Check("1"))!.Message, Does.Contain("not used on"));
  }

  [Test]
  public void RequiredToolchainAtItsPin_LetsTheTestRun() {
    var tool = Path.Combine(this._directory, OperatingSystem.IsWindows() ? "fake-tool.cmd" : "fake-tool");
    File.Copy(Path.Combine(_fixtures, OperatingSystem.IsWindows() ? "fake-tool.cmd" : "fake-tool.sh"), tool);
    if (!OperatingSystem.IsWindows())
      File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var lockFile = Path.Combine(this._directory, "toolchains.lock");
    File.WriteAllText(lockFile, $"""
      schema: 1
      toolchains:
        fake:
          version: "1.2.3"
          probe: ['{tool}', 'fake 1.2.3']
          versionPattern: '^fake (?<version>\S+)$'
          platforms: [linux, windows, macos]
          licence: MIT
          source: https://example.org/fake

      """);
    Assert.That(() => new RequiresToolchainAttribute("fake") { Lock = lockFile }.Check("1"), Throws.Nothing);
  }

}
