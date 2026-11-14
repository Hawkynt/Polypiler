using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The scenarios of <c>tests/features/infra/infra-toolchains.feature</c> with the real
/// <c>polypiler-tooling</c> executable and the fake tool of the fixtures, copied into a working
/// directory as <c>fake-tool</c> and found on <c>PATH</c>.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
public sealed class ToolchainProbeTests {

  private static readonly string[] _scenarios = [
    "The repository lock is valid and complete",
    "An installed tool with the pinned version probes as Ok",
    "A different installed version is a mismatch",
    "A tool that is not installed is reported, not skipped",
    "A malformed lock is refused with its reason",
    "A test that requires an absent toolchain fails in CI"
  ];

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "toolchains");

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Ensures the feature and its NUnit bindings stay in agreement.</summary>
  [Test]
  public void FeatureScenarios_HaveBindings() {
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/infra/infra-toolchains.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  /// <summary>Runs <c>polypiler-tooling</c> in a directory, with the working directory first on <c>PATH</c>.</summary>
  private static (int ExitCode, string Output, string Error) RunTooling(string workingDirectory, params string[] arguments) {
    var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "polypiler-tooling.exe" : "polypiler-tooling");
    Assert.That(File.Exists(executable), Is.True, $"the polypiler-tooling executable was not built next to the tests: {executable}");
    var info = new ProcessStartInfo(executable) {
      WorkingDirectory = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    foreach (var argument in arguments)
      info.ArgumentList.Add(argument);
    info.Environment["PATH"] = workingDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
    using var process = Process.Start(info)!;
    var error = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output, error.Result);
  }

  /// <summary>Copies the fake tool into the working directory as <c>fake-tool</c> and writes a lock of one entry that runs it.</summary>
  private string FakeToolLock(string key, string version, string printed) {
    var tool = Path.Combine(this._directory, OperatingSystem.IsWindows() ? "fake-tool.cmd" : "fake-tool");
    File.Copy(Path.Combine(_fixtures, OperatingSystem.IsWindows() ? "fake-tool.cmd" : "fake-tool.sh"), tool);
    if (!OperatingSystem.IsWindows())
      File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    File.WriteAllText(Path.Combine(this._directory, "toolchains.lock"), $"""
      schema: 1
      toolchains:
        {key}:
          version: "{version}"
          probe: [fake-tool, '{printed}']
          versionPattern: '^fake (?<version>\S+)$'
          platforms: [linux, windows, macos]
          licence: MIT
          source: https://example.org/{key}

      """);
    return tool;
  }

  [Test]
  public void RepositoryLock_IsValidAndComplete() {
    var (exitCode, output, error) = RunTooling(PrototypeFixture.RepositoryRoot, "toolchains", "validate");
    Assert.That((exitCode, error), Is.EqualTo((0, "")), output + error);

    var readme = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "backlog", "README.md"));
    var keys = Regex.Matches(Regex.Match(readme, @"Keys in use:(.*?)An issue that needs another", RegexOptions.Singleline).Groups[1].Value, "`([a-z0-9-]+)`")
      .Select(match => match.Groups[1].Value).ToArray();
    var toolchains = ToolchainLock.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "tooling", "toolchains.lock"));
    Assert.That(keys, Has.Length.EqualTo(26));
    Assert.That(keys.Where(key => !toolchains.TryGet(key, out _)), Is.Empty);
    Assert.That(keys, Is.SupersetOf(new[] { "dotnet-sdk", "node", "rust", "pb-compiler", "z3", "vice" }));
  }

  [Test]
  public void PinnedVersion_ProbesAsOk() {
    var tool = this.FakeToolLock("fake", "1.2.3", "fake 1.2.3");
    var (exitCode, output, error) = RunTooling(this._directory, "toolchains", "probe", "--lock", "toolchains.lock", "--only", "fake");
    Assert.That(output.TrimEnd(), Is.EqualTo($"fake Ok expected=1.2.3 actual=1.2.3 path={tool}"), error);
    Assert.That(exitCode, Is.EqualTo(0));
  }

  [Test]
  public void DifferentVersion_IsAMismatch() {
    this.FakeToolLock("fake", "1.2.3", "fake 1.2.4");
    var (exitCode, _, error) = RunTooling(this._directory, "toolchains", "probe", "--lock", "toolchains.lock", "--only", "fake");
    Assert.That(error, Does.Contain("error PLY0022: toolchain 'fake' is 1.2.4, the lock pins 1.2.3"));
    Assert.That(exitCode, Is.EqualTo(1));
  }

  [Test]
  public void AbsentTool_IsReportedNotSkipped() {
    File.Copy(Path.Combine(_fixtures, "ghost.lock"), Path.Combine(this._directory, "toolchains.lock"));
    var (exitCode, output, error) = RunTooling(this._directory, "toolchains", "probe", "--lock", "toolchains.lock", "--only", "ghost");
    Assert.That(error, Does.Contain("error PLY0021: toolchain 'ghost' is not installed"));
    Assert.That(output, Does.StartWith("ghost Missing "));
    Assert.That(exitCode, Is.EqualTo(1));
  }

  [TestCase("no-version.lock", "missing property 'version' in 'node'")]
  [TestCase("extra-property.lock", "unknown property 'channel' in 'node'")]
  [TestCase("no-group.lock", "versionPattern of 'node' has no group 'version'")]
  [TestCase("unsorted.lock", "keys are not in ordinal order: 'gcc' after 'node'")]
  [TestCase("schema-2.lock", "unsupported schema 2")]
  public void MalformedLock_IsRefusedWithItsReason(string fixture, string reason) {
    var (exitCode, _, error) = RunTooling(_fixtures, "toolchains", "validate", "--lock", fixture);
    Assert.That(error, Does.Contain("error PLY0020: " + reason));
    Assert.That(exitCode, Is.EqualTo(1));
  }

  [Test]
  public void RequiredAbsentToolchain_FailsInCi() {
    var attribute = new RequiresToolchainAttribute("ghost") { Lock = "tests/fixtures/infra/toolchains/ghost.lock" };
    var failure = Assert.Throws<AssertionException>(() => attribute.Check("1"))!;
    Assert.That(failure.Message, Does.Contain("PLY0021"));
  }

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
    var tool = this.FakeToolLock("fake", "1.2.3", "fake 1.2.3");
    var lockFile = Path.Combine(this._directory, "toolchains.lock");
    File.WriteAllText(lockFile, File.ReadAllText(lockFile).Replace("[fake-tool,", $"['{tool}',"));
    Assert.That(() => new RequiresToolchainAttribute("fake") { Lock = lockFile }.Check("1"), Throws.Nothing);
  }

}
