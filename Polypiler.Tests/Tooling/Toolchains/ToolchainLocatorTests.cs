using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Tooling.Toolchains;
using static Polypiler.Tests.Tooling.Toolchains.ToolchainTesting;

namespace Polypiler.Tests.Tooling.Toolchains;

/// <summary>
/// <see cref="ToolchainLocator"/> against a scripted runner and executables that are empty files in a
/// scratch folder: where a toolchain is found, how its version is read and what each status means.
/// </summary>
[TestFixture]
public sealed class ToolchainLocatorTests {

  private ScratchFolder _folder = null!;
  private Dictionary<string, string> _environment = null!;

  [SetUp]
  public void CreateFolder() {
    this._folder = new();
    this._environment = new() { ["PATH"] = Path.Combine(this._folder.Path, "path") };
  }

  [TearDown]
  public void DeleteFolder() => this._folder.Dispose();

  private ToolchainLocator Locator(ToolchainLock toolchains, IProcessRunner runner, string platform = "linux", TimeSpan? timeout = null)
    => new(toolchains, runner, name => this._environment.GetValueOrDefault(name), platform, timeout ?? TimeSpan.FromSeconds(30));

  private ToolchainProbe Probe(string output, string version = "1.2.3", string key = "fake") {
    this._folder.File(Path.Combine("path", "fake"));
    return this.Locator(Parse(Entry(key, version)), new ScriptedRunner(output)).Probe(key);
  }

  [Test]
  public void MatchingVersion_IsOk() {
    var executable = this._folder.File(Path.Combine("path", "fake"));
    var runner = new ScriptedRunner("fake 1.2.3");
    var probe = this.Locator(Parse(Entry("fake", probe: "[fake, --version, -q]")), runner).Probe("fake");
    Assert.That(probe, Is.EqualTo(new ToolchainProbe("fake", ToolchainStatus.Ok, "1.2.3", "1.2.3", executable) { Command = probe.Command }));
    Assert.That(probe.Command, Is.EqualTo(new[] { "fake", "--version", "-q" }));
    Assert.That(runner.Calls.Single(), Is.EqualTo(new[] { executable, "--version", "-q" }));
    Assert.That(runner.LastTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
    Assert.That((probe.IsAcceptable, probe.Problem, probe.Cause), Is.EqualTo((true, ((string, string)?)null, (string?)null)));
  }

  [TestCase("fake 1.2.4", "1.2.3", "1.2.4", TestName = "Another version is a mismatch")]
  [TestCase("fake 1.2.30", "1.2.3", "1.2.30", TestName = "A version differing in its last character is a mismatch")]
  [TestCase("fake 22.1.0", "22.1", "22.1.0", TestName = "A version the pin is a prefix of is a mismatch")]
  [TestCase("fake 22.1", "22.1.0", "22.1", TestName = "A version that is a prefix of the pin is a mismatch")]
  [TestCase("fake 1.2.3-RC", "1.2.3-rc", "1.2.3-RC", TestName = "Versions are compared case-sensitively")]
  public void DifferentVersion_IsAMismatch(string output, string pinned, string actual) {
    var probe = this.Probe(output, pinned);
    Assert.That((probe.Status, probe.ActualVersion, probe.IsAcceptable), Is.EqualTo((ToolchainStatus.Mismatch, actual, false)));
    Assert.That(probe.Problem, Is.EqualTo(("PLY0022", $"toolchain 'fake' is {actual}, the lock pins {pinned}")));
  }

  [Test]
  public void UnrecognisedOutput_IsAMismatchWithoutVersion() {
    var probe = this.Probe("hello");
    Assert.That((probe.Status, probe.ActualVersion, probe.Cause), Is.EqualTo((ToolchainStatus.Mismatch, (string?)null, "version not recognised")));
    Assert.That(probe.Path, Is.Not.Null);
    Assert.That(probe.Problem, Is.EqualTo(("PLY0022", "toolchain 'fake' is -, the lock pins 1.2.3: version not recognised")));
  }

  [TestCase("fake 1.2.3", "", TestName = "A version printed on standard output is read")]
  [TestCase("", "fake 1.2.3", TestName = "A version printed on standard error is read")]
  [TestCase("banner\r\nfake 1.2.3\r\n", "", TestName = "The first matching line counts, whatever the line ends")]
  [TestCase("fake 1.2.3", "fake 9.9.9", TestName = "Standard output comes before standard error")]
  public void Version_IsReadFromEitherStream(string output, string error) {
    this._folder.File(Path.Combine("path", "fake"));
    var probe = this.Locator(Parse(Entry("fake")), new ScriptedRunner(_ => new(0, output, error, false))).Probe("fake");
    Assert.That((probe.Status, probe.ActualVersion), Is.EqualTo((ToolchainStatus.Ok, "1.2.3")));
  }

  [Test]
  public void NonZeroExitWithAVersion_IsOk() {
    this._folder.File(Path.Combine("path", "fake"));
    var probe = this.Locator(Parse(Entry("fake")), new ScriptedRunner(_ => new(3, "fake 1.2.3\n", "", false))).Probe("fake");
    Assert.That(probe.Status, Is.EqualTo(ToolchainStatus.Ok));
  }

  [Test]
  public void AbsentExecutable_IsMissing() {
    var runner = new ScriptedRunner("fake 1.2.3");
    var probe = this.Locator(Parse(Entry("ghost", probe: "[no-such-binary-polypiler, --version]")), runner).Probe("ghost");
    Assert.That((probe.Status, probe.ActualVersion, probe.Path, probe.Cause), Is.EqualTo((ToolchainStatus.Missing, (string?)null, (string?)null, "'no-such-binary-polypiler' was not found on PATH")));
    Assert.That(probe.Problem, Is.EqualTo(("PLY0021", "toolchain 'ghost' is not installed (probe: no-such-binary-polypiler --version): 'no-such-binary-polypiler' was not found on PATH")));
    Assert.That(runner.Calls, Is.Empty);
  }

  [Test]
  public void NoPathVariable_IsMissing() {
    this._environment.Remove("PATH");
    Assert.That(this.Probe("fake 1.2.3").Status, Is.EqualTo(ToolchainStatus.Missing));
  }

  [Test]
  public void ExecutableThatCannotStart_IsMissing() {
    this._folder.File(Path.Combine("path", "fake"));
    var probe = this.Locator(Parse(Entry("fake")), new ScriptedRunner(_ => throw new Win32Exception(13, "Permission denied"))).Probe("fake");
    Assert.That((probe.Status, probe.Cause), Is.EqualTo((ToolchainStatus.Missing, "it cannot be started: Permission denied")));
  }

  [Test]
  public void ProbeThatTimesOut_IsMissing() {
    this._folder.File(Path.Combine("path", "fake"));
    var runner = new ScriptedRunner(_ => new(-1, "", "", true));
    var probe = this.Locator(Parse(Entry("fake")), runner, timeout: TimeSpan.FromMilliseconds(200)).Probe("fake");
    Assert.That((probe.Status, probe.Cause), Is.EqualTo((ToolchainStatus.Missing, "probe timed out")));
    Assert.That(runner.LastTimeout, Is.EqualTo(TimeSpan.FromMilliseconds(200)));
  }

  [TestCase("[linux]", "linux", ToolchainStatus.Ok, TestName = "A toolchain of this platform is probed")]
  [TestCase("[windows, macos]", "linux", ToolchainStatus.NotOnThisPlatform, TestName = "A toolchain of other platforms is not on this one")]
  [TestCase("[linux, macos]", "macos", ToolchainStatus.Ok, TestName = "Each listed platform is probed")]
  public void Platform_DecidesWhetherToProbe(string platforms, string platform, ToolchainStatus status) {
    this._folder.File(Path.Combine("path", "fake"));
    var runner = new ScriptedRunner("fake 1.2.3");
    var probe = this.Locator(Parse(Entry("fake", platforms: platforms)), runner, platform).Probe("fake");
    Assert.That(probe.Status, Is.EqualTo(status));
    Assert.That(probe.IsAcceptable, Is.True);
    Assert.That(probe.Problem, Is.Null);
    Assert.That(runner.Calls, Has.Count.EqualTo(status == ToolchainStatus.Ok ? 1 : 0));
  }

  [Test]
  public void HomeVariable_IsSearchedInsteadOfPath() {
    this._folder.File(Path.Combine("path", "fake"));
    var installed = this._folder.File(Path.Combine("install", "bin", "fake"));
    this._environment["FAKE_HOME"] = Path.Combine(this._folder.Path, "install");
    var toolchains = Parse(Entry("fake", extra: "home: FAKE_HOME"));
    var probe = this.Locator(toolchains, new ScriptedRunner("fake 1.2.3")).Probe("fake");
    Assert.That((probe.Status, probe.Path), Is.EqualTo((ToolchainStatus.Ok, installed)));

    var direct = this._folder.File(Path.Combine("install", "fake"));
    Assert.That(this.Locator(toolchains, new ScriptedRunner("fake 1.2.3")).Probe("fake").Path, Is.EqualTo(direct));
  }

  [Test]
  public void HomeVariableWithoutTheTool_IsMissing() {
    this._folder.File(Path.Combine("path", "fake"));
    var home = this._folder.Folder("install");
    this._environment["FAKE_HOME"] = home;
    var probe = this.Locator(Parse(Entry("fake", extra: "home: FAKE_HOME")), new ScriptedRunner("fake 1.2.3")).Probe("fake");
    Assert.That((probe.Status, probe.Cause), Is.EqualTo((ToolchainStatus.Missing, $"'fake' was not found in FAKE_HOME ({home})")));
  }

  [TestCase(null, TestName = "An unset home variable falls back to PATH")]
  [TestCase("", TestName = "An empty home variable falls back to PATH")]
  public void UnsetHomeVariable_FallsBackToPath(string? value) {
    var onPath = this._folder.File(Path.Combine("path", "fake"));
    if (value != null)
      this._environment["FAKE_HOME"] = value;
    var probe = this.Locator(Parse(Entry("fake", extra: "home: FAKE_HOME")), new ScriptedRunner("fake 1.2.3")).Probe("fake");
    Assert.That((probe.Status, probe.Path), Is.EqualTo((ToolchainStatus.Ok, onPath)));
  }

  [Test]
  public void PathEntries_AreSearchedInOrder() {
    var first = this._folder.File(Path.Combine("first", "fake"));
    this._folder.File(Path.Combine("second", "fake"));
    this._environment["PATH"] = string.Join(Path.PathSeparator, Path.Combine(this._folder.Path, "empty"), "", Path.Combine(this._folder.Path, "first"), Path.Combine(this._folder.Path, "second"));
    var probe = this.Locator(Parse(Entry("fake")), new ScriptedRunner("fake 1.2.3")).Probe("fake");
    Assert.That(probe.Path, Is.EqualTo(first));
  }

  [Test]
  public void ProbeWithAPath_IsTakenAsItIs() {
    var tool = this._folder.File(Path.Combine("tools", "fake"));
    var toolchains = Parse(Entry("fake", probe: $"['{tool}', --version]"), Entry("gone", probe: $"['{tool}-gone', --version]"));
    var locator = this.Locator(toolchains, new ScriptedRunner("fake 1.2.3"));
    Assert.That(locator.Probe("fake").Path, Is.EqualTo(tool));
    Assert.That(locator.Probe("gone").Cause, Is.EqualTo($"'{tool}-gone' was not found at that path"));
  }

  [Test]
  public void OnWindows_TheExecutableExtensionsAreTried() {
    var command = this._folder.File(Path.Combine("path", "fake.cmd"));
    this._environment["PATHEXT"] = ".EXE;.CMD";
    var probe = this.Locator(Parse(Entry("fake")), new ScriptedRunner("fake 1.2.3"), "windows").Probe("fake");
    Assert.That(probe.Path, Is.EqualTo(command));
  }

  [Test]
  public void OnWindows_WithoutPathExt_TheUsualExtensionsAreTried() {
    var executable = this._folder.File(Path.Combine("path", "fake.exe"));
    Assert.That(this.Locator(Parse(Entry("fake")), new ScriptedRunner("fake 1.2.3"), "windows").Probe("fake").Path, Is.EqualTo(executable));
  }

  [Test]
  public void OnWindows_ANameWithAnExtension_IsTakenAsItIs() {
    var script = this._folder.File(Path.Combine("path", "fake.ps1"));
    var probe = this.Locator(Parse(Entry("fake", probe: "[fake.ps1]")), new ScriptedRunner("fake 1.2.3"), "windows").Probe("fake");
    Assert.That(probe.Path, Is.EqualTo(script));
  }

  [Test]
  public void Package_IsProbedThroughItsHostsCommand() {
    var host = this._folder.File(Path.Combine("path", "fake"));
    var runner = new ScriptedRunner(argv => argv.Count == 2 ? new(0, "fake 1.0.0\n", "", false) : new(0, "fake 4.5.6\n", "", false));
    var toolchains = Parse(Entry("fake", "1.0.0"), Entry("fake-lib", "4.5.6", "[fake, -c, 'print(lib.version)']", extra: "kind: package\nrequires: fake"));
    var probe = this.Locator(toolchains, runner).Probe("fake-lib");
    Assert.That((probe.Status, probe.ActualVersion, probe.Path), Is.EqualTo((ToolchainStatus.Ok, "4.5.6", host)));
    Assert.That(runner.Calls.Select(call => call.Count), Is.EqualTo(new[] { 2, 3 }));
  }

  [Test]
  public void Package_InheritsItsHostsHome() {
    var installed = this._folder.File(Path.Combine("install", "fake"));
    this._environment["FAKE_HOME"] = Path.Combine(this._folder.Path, "install");
    var toolchains = Parse(Entry("fake", "1.2.3", extra: "home: FAKE_HOME"), Entry("fake-lib", "1.2.3", extra: "kind: package\nrequires: fake"));
    Assert.That(this.Locator(toolchains, new ScriptedRunner("fake 1.2.3")).Probe("fake-lib").Path, Is.EqualTo(installed));
  }

  [Test]
  public void Package_OfAMismatchingHost_IsStillProbed() {
    this._folder.File(Path.Combine("path", "fake"));
    var toolchains = Parse(Entry("fake", "9.9.9"), Entry("fake-lib", "1.2.3", extra: "kind: package\nrequires: fake"));
    Assert.That(this.Locator(toolchains, new ScriptedRunner("fake 1.2.3")).Probe("fake-lib").Status, Is.EqualTo(ToolchainStatus.Ok));
  }

  [TestCase("[linux]", ToolchainStatus.Missing, TestName = "A package of a missing host is missing")]
  [TestCase("[windows]", ToolchainStatus.NotOnThisPlatform, TestName = "A package of a host of another platform is not on this one")]
  public void Package_OfAnUnavailableHost_SharesItsStatus(string hostPlatforms, ToolchainStatus status) {
    var toolchains = Parse(Entry("fake", platforms: hostPlatforms), Entry("fake-lib", extra: "kind: package\nrequires: fake"));
    var probe = this.Locator(toolchains, new ScriptedRunner("fake 1.2.3")).Probe("fake-lib");
    Assert.That((probe.Status, probe.Cause), Is.EqualTo((status, $"its host toolchain 'fake' is {status}")));
  }

  [TestCase(null, ToolchainStatus.Ok, TestName = "An external toolchain is probed outside CI")]
  [TestCase("0", ToolchainStatus.Ok, TestName = "An external toolchain is probed unless CI asks for every toolchain")]
  [TestCase("1", ToolchainStatus.NotOnThisPlatform, TestName = "An external toolchain is not on a CI runner")]
  public void External_IsNotOnThisPlatformInCi(string? require, ToolchainStatus status) {
    this._folder.File(Path.Combine("path", "fake"));
    if (require != null)
      this._environment[ToolchainLocator.RequireVariable] = require;
    var locator = this.Locator(Parse(Entry("fake", extra: "kind: external")), new ScriptedRunner("fake 1.2.3"));
    Assert.That(locator.Probe("fake").Status, Is.EqualTo(status));
    Assert.That(locator.ToolchainsRequired, Is.EqualTo(require == "1"));
  }

  [Test]
  public void ToolInCi_IsStillProbed() {
    this._environment[ToolchainLocator.RequireVariable] = "1";
    Assert.That(this.Probe("fake 1.2.3").Status, Is.EqualTo(ToolchainStatus.Ok));
  }

  private ToolchainLock NuGet(string version = "1.2.3-Beta")
    => Parse(Entry("dotnet", "1.0.0", "[dotnet, --version]", @"^(?<version>\S+)$"), Entry("lib", version, "[Some.Library]", extra: "kind: nuget\nrequires: dotnet"));

  private ToolchainProbe ProbeNuGet(string platform = "linux", string version = "1.2.3-Beta") {
    this._folder.File(Path.Combine("path", platform == "windows" ? "dotnet.exe" : "dotnet"));
    var runner = new ScriptedRunner(argv => argv.Count == 2 ? new(0, "1.0.0\n", "", false) : throw new AssertionException("a NuGet package is not probed by a process"));
    return this.Locator(this.NuGet(version), runner, platform).Probe("lib");
  }

  [Test]
  public void NuGetPackage_IsFoundInThePackageFolder() {
    this._environment["NUGET_PACKAGES"] = Path.Combine(this._folder.Path, "packages");
    var pinned = this._folder.Folder(Path.Combine("packages", "some.library", "1.2.3-beta"));
    this._folder.Folder(Path.Combine("packages", "some.library", "2.0.0"));
    var probe = this.ProbeNuGet();
    Assert.That((probe.Status, probe.ActualVersion, probe.Path), Is.EqualTo((ToolchainStatus.Ok, "1.2.3-Beta", pinned)));
  }

  [TestCase("linux", "HOME", TestName = "Without NUGET_PACKAGES the package folder is below HOME")]
  [TestCase("windows", "USERPROFILE", TestName = "Without NUGET_PACKAGES the package folder is below USERPROFILE on Windows")]
  public void NuGetPackage_DefaultFolder_IsBelowTheUsersHome(string platform, string variable) {
    this._environment[variable] = this._folder.Path;
    var pinned = this._folder.Folder(Path.Combine(".nuget", "packages", "some.library", "1.2.3-beta"));
    Assert.That(this.ProbeNuGet(platform).Path, Is.EqualTo(pinned));
  }

  [Test]
  public void NuGetPackage_WithoutAnyHome_IsMissing() {
    this._environment["NUGET_PACKAGES"] = "";
    Assert.That(this.ProbeNuGet().Status, Is.EqualTo(ToolchainStatus.Missing));
  }

  [Test]
  public void NuGetPackage_AtOtherVersions_IsAMismatch() {
    this._environment["NUGET_PACKAGES"] = Path.Combine(this._folder.Path, "packages");
    this._folder.Folder(Path.Combine("packages", "some.library", "2.0.0"));
    this._folder.Folder(Path.Combine("packages", "some.library", "1.0.0"));
    var folder = Path.Combine(this._folder.Path, "packages", "some.library");
    var probe = this.ProbeNuGet();
    Assert.That((probe.Status, probe.ActualVersion, probe.Path), Is.EqualTo((ToolchainStatus.Mismatch, "1.0.0,2.0.0", folder)));
  }

  [TestCase(false, TestName = "A NuGet package without its folder is missing")]
  [TestCase(true, TestName = "A NuGet package with an empty folder is missing")]
  public void NuGetPackage_NotRestored_IsMissing(bool emptyFolder) {
    var root = Path.Combine(this._folder.Path, "packages");
    this._environment["NUGET_PACKAGES"] = root;
    if (emptyFolder)
      this._folder.Folder(Path.Combine("packages", "some.library"));
    var probe = this.ProbeNuGet();
    Assert.That((probe.Status, probe.Cause), Is.EqualTo((ToolchainStatus.Missing, $"'Some.Library' is not in the NuGet package folder '{root}'")));
  }

  [Test]
  public void UnknownKey_IsALockError()
    => Assert.That(Assert.Throws<ToolchainLockException>(() => this.Locator(Parse(Entry("fake")), new ScriptedRunner("")).Probe("nope"))!.Diagnostics.Single().Message,
      Is.EqualTo("unknown toolchain key 'nope'"));

  [Test]
  public void MachineLocator_ProbesThisPlatformWithTheDefaultTimeout() {
    var runner = new ScriptedRunner("fake 1.2.3");
    var locator = new ToolchainLocator(Parse(Entry("ghost", probe: "[no-such-binary-polypiler]")), runner);
    Assert.That(locator.Probe("ghost").Status, Is.EqualTo(ToolchainStatus.Missing));
    Assert.That(ToolchainLocator.DefaultTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
    Assert.That(ToolchainLocator.CurrentPlatform, Is.EqualTo(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux"));
  }

}
