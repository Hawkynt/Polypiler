using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The rows of the Rust layer's test design that <c>tests/features/infra/infra-ci-rust.feature</c> does
/// not hold: the failing and the empty test run, Miri on both sides of the slice's end, the checksum
/// boundaries, a compile error, a registry dependency offline, and the probe of both toolchains when
/// one is absent, at another version, or a nightly without Miri. The commands run on copies of the
/// crates under <c>tests/fixtures/infra/ci/rust</c>; the probes run on folders the test lays out and a
/// process runner that answers for the tool.
/// </summary>
[TestFixture]
[Layer("rust")]
public sealed class RustLayerTests {

  private const string Adler32 = RustLayerRun.Fixtures + "/adler32";
  private const string MiriUb = RustLayerRun.Fixtures + "/miri-ub";

  /// <summary>Answers every probe with the given standard output.</summary>
  private sealed class FixedOutput(string output) : IProcessRunner {

    public ProcessResult Run(IReadOnlyList<string> argv, TimeSpan timeout) => new(0, output, "", false);

  }

  private string _directory = "";

  [SetUp]
  public void CreateDirectory() => this._directory = Directory.CreateTempSubdirectory("polypiler-rust-layer-").FullName;

  [TearDown]
  public void DeleteDirectory() => Directory.Delete(this._directory, true);

  private static RustCommands Commands => RustLayerRun.Commands;

  private static ToolchainLock RepositoryLock => ToolchainLock.Load(Path.Combine(PrototypeFixture.RepositoryRoot, ToolchainLock.RepositoryPath));

  // Test design 4: a failing unit test.
  [Test, Category("Slow")]
  public async Task WrongExpectation_FailsCargoTest() {
    RustLayerRun.Require(RustCommands.Toolchain);
    var result = await RustLayerRun.RunAsync(RustLayerRun.Crate(Adler32), Commands.Test("--features", "wrong-expectation"));
    var stdout = RustLayerRun.Stdout(result);
    Assert.That((result.Outcome, result.ExitCode), Is.EqualTo((RunOutcome.Completed, 101)));
    Assert.That(stdout, Does.Contain("1 failed"));
    Assert.That(RustCommands.Failed(stdout), Is.EqualTo(1));
  }

  // Test design 5: get_unchecked at the last valid index and one past it.
  [Test, Category("Slow")]
  public async Task Miri_PassesTheLastValidIndex() {
    RustLayerRun.Require(RustCommands.Toolchain);
    RustLayerRun.Require(RustCommands.MiriToolchain);
    var result = await RustLayerRun.RunAsync(RustLayerRun.Crate(MiriUb), Commands.Miri("last_valid_index"));
    Assert.That((result.Outcome, result.ExitCode), Is.EqualTo((RunOutcome.Completed, 0)), RustLayerRun.Stderr(result));
    Assert.That(RustCommands.Passed(RustLayerRun.Stdout(result)), Is.EqualTo(1));
  }

  [Test, Category("Slow")]
  public async Task Miri_ReportsTheReadOnePastTheEnd() {
    RustLayerRun.Require(RustCommands.Toolchain);
    RustLayerRun.Require(RustCommands.MiriToolchain);
    var result = await RustLayerRun.RunAsync(RustLayerRun.Crate(MiriUb), Commands.Miri("one_past_the_end"));
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed));
    Assert.That(result.ExitCode, Is.Not.Zero);
    Assert.That(RustLayerRun.Stderr(result), Does.Contain("Undefined Behavior"));
  }

  // Test design 6: the checksum of the empty slice and of one byte, asserted by the crate's own test.
  [Test, Category("Slow")]
  public async Task Adler32UnitTest_CoversTheEmptySliceAndOneByte() {
    RustLayerRun.Require(RustCommands.Toolchain);
    var crate = RustLayerRun.Crate(Adler32);
    var source = Encoding.UTF8.GetString(crate["src/lib.rs"].Span);
    Assert.That(source, Does.Contain("assert_eq!(adler32(b\"\"), 1);"));
    Assert.That(source, Does.Contain("assert_eq!(adler32(b\"a\"), 0x0062_0062);"));
    var sum = 1 + "a"[0];
    Assert.That((sum << 16) | sum, Is.EqualTo(0x0062_0062).And.EqualTo(6422626), "RFC 1950: a = 1 + 97, b = a");
    var result = await RustLayerRun.RunAsync(crate, Commands.Test("--", "--exact", "tests::checksums"));
    Assert.That(result.ExitCode, Is.Zero, RustLayerRun.Stderr(result));
    Assert.That(RustCommands.Passed(RustLayerRun.Stdout(result)), Is.EqualTo(1));
  }

  // Test design 7: a crate whose tests are compiled out.
  [Test, Category("Slow")]
  public async Task CrateWithoutTests_PassesCargoTestButNotTheSmokeCheck() {
    RustLayerRun.Require(RustCommands.Toolchain);
    var result = await RustLayerRun.RunAsync(RustLayerRun.Crate(Adler32), Commands.Test("--features", "no-tests"));
    var stdout = RustLayerRun.Stdout(result);
    Assert.That(result.ExitCode, Is.Zero, RustLayerRun.Stderr(result));
    Assert.That(stdout, Does.Contain("0 passed"));
    Assert.That(RustCommands.Passed(stdout), Is.Zero);
    Assert.That(RustCommands.SmokeProblem(stdout), Is.EqualTo("a smoke crate must run at least one test, and cargo test passed none"));
  }

  // Test design 8: a compile error ends the run before anything is executed.
  [Test, Category("Slow")]
  public async Task TypeError_IsABuildFailure() {
    RustLayerRun.Require(RustCommands.Toolchain);
    var result = await RustLayerRun.RunAsync(RustLayerRun.Crate(Adler32), Commands.Run, Commands.Build("--features", "type-error"));
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.BuildFailed));
    var diagnostic = result.Diagnostics.Single();
    Assert.That(diagnostic.Code, Is.EqualTo("PLY0030"));
    Assert.That(diagnostic.Cause, Does.Contain("error[E0308]: mismatched types"));
  }

  // Test design 9: cargo is offline and resolves nothing from the registry.
  [Test, Category("Slow")]
  public async Task RegistryDependency_FailsOffline() {
    RustLayerRun.Require(RustCommands.Toolchain);
    var result = await RustLayerRun.RunAsync(RustLayerRun.Crate(RustLayerRun.Fixtures + "/registry-dep"), Commands.Build());
    var stderr = RustLayerRun.Stderr(result);
    Assert.That((result.Outcome, result.ExitCode), Is.EqualTo((RunOutcome.Completed, 101)));
    Assert.That(stderr, Does.Contain("no matching package named `itoa` found"));
    Assert.That(stderr, Does.Contain("offline"));
    Assert.That(stderr, Does.Not.Contain("Downloaded").And.Not.Contain("Updating crates.io index"));
  }

  // Test design 10: a toolchain absent or at another version.
  [Test]
  public void Probe_StableAbsent_IsNotInstalled() {
    var probe = this.Locator("", new() { ["PATH"] = this._directory }).Probe(RustCommands.Toolchain);
    Assert.That(probe.Problem?.Code, Is.EqualTo(ToolchainDiagnostic.NotInstalled));
    Assert.That(probe.Problem?.Message, Does.StartWith("toolchain 'rust' is not installed (probe: cargo --version)"));
  }

  [Test]
  public void Probe_StableAtAnotherVersion_IsAMismatch() {
    File.WriteAllText(Path.Combine(this._directory, "cargo"), "");
    var probe = this.Locator("cargo 1.98.0 (0123456789 2026-07-01)\n", new() { ["PATH"] = this._directory }).Probe(RustCommands.Toolchain);
    Assert.That(probe.Problem, Is.EqualTo((ToolchainDiagnostic.VersionMismatch, "toolchain 'rust' is 1.98.0, the lock pins 1.99.0")));
  }

  [Test]
  public void Probe_NightlyAtItsPin_IsOk() {
    var sysroot = this.Nightly("nightly-2026-11-01", withMiri: true);
    var probe = this.Locator(sysroot + "\n", new() { ["RUST_MIRI_HOME"] = sysroot }).Probe(RustCommands.MiriToolchain);
    Assert.That((probe.Status, probe.ActualVersion), Is.EqualTo((ToolchainStatus.Ok, "nightly-2026-11-01")));
    Assert.That(probe.Path, Is.EqualTo(Path.Combine(sysroot, "bin", "miri")));
  }

  [Test]
  public void Probe_AnotherNightly_IsAMismatch() {
    var sysroot = this.Nightly("nightly-2026-10-01", withMiri: true);
    var probe = this.Locator(sysroot + "\n", new() { ["RUST_MIRI_HOME"] = sysroot }).Probe(RustCommands.MiriToolchain);
    Assert.That(probe.Problem, Is.EqualTo((ToolchainDiagnostic.VersionMismatch, "toolchain 'rust-miri' is nightly-2026-10-01, the lock pins nightly-2026-11-01")));
  }

  [Test]
  public void Probe_NightlyWithoutRustupSysroot_IsUnrecognised() {
    var sysroot = Path.Combine(this._directory, "usr");
    Directory.CreateDirectory(Path.Combine(sysroot, "bin"));
    File.WriteAllText(Path.Combine(sysroot, "bin", "miri"), "");
    var probe = this.Locator(sysroot + "\n", new() { ["RUST_MIRI_HOME"] = sysroot }).Probe(RustCommands.MiriToolchain);
    Assert.That(probe.Problem?.Code, Is.EqualTo(ToolchainDiagnostic.VersionMismatch));
    Assert.That(probe.Cause, Is.EqualTo("version not recognised"));
  }

  // Test design 11: the nightly is installed, its miri component is not.
  [Test]
  public void Probe_NightlyWithoutMiri_IsNotInstalled() {
    var sysroot = this.Nightly("nightly-2026-11-01", withMiri: false);
    var probe = this.Locator(sysroot + "\n", new() { ["RUST_MIRI_HOME"] = sysroot }).Probe(RustCommands.MiriToolchain);
    Assert.That(probe.Problem?.Code, Is.EqualTo(ToolchainDiagnostic.NotInstalled));
    Assert.That(probe.Problem?.Message, Does.StartWith("toolchain 'rust-miri' is not installed (probe: miri --print sysroot): 'miri' was not found in RUST_MIRI_HOME"));
  }

  // Test design 3: Miri applies to a crate with unsafe code only.
  [Test]
  public void NeedsMiri_FollowsTheUnsafeToken() {
    Assert.That(RustCommands.NeedsMiri(RustLayerRun.Crate(MiriUb)), Is.True);
    Assert.That(RustCommands.NeedsMiri(RustLayerRun.Crate(Adler32)), Is.False);
  }

  [TestCase("fn f() { unsafe { g() } }", true, TestName = "An unsafe block needs Miri")]
  [TestCase("pub unsafe fn f() {}", true, TestName = "An unsafe function needs Miri")]
  [TestCase("#![forbid(unsafe_code)]", false, TestName = "The word inside a longer identifier is no token")]
  [TestCase("fn not_unsafe_at_all() {}", false, TestName = "The word inside an identifier is no token")]
  [TestCase("", false, TestName = "An empty source needs no Miri")]
  public void NeedsMiri_LooksForTheWholeToken(string source, bool expected)
    => Assert.That(RustCommands.NeedsMiri(Files(("src/lib.rs", source))), Is.EqualTo(expected));

  [Test]
  public void NeedsMiri_ReadsRustSourcesOnly()
    => Assert.That(RustCommands.NeedsMiri(Files(("Cargo.toml", "description = \"unsafe\""), ("README.md", "unsafe"))), Is.False);

  [Test]
  public void TestCounts_AddUpEveryTestBinary() {
    const string output = "running 1 test\ntest result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n\n"
      + "test result: FAILED. 2 passed; 3 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n"
      + "the summary test result: ok. 9 passed; 9 failed; is not at the start of a line\n";
    Assert.That((RustCommands.Passed(output), RustCommands.Failed(output)), Is.EqualTo((3, 3)));
    Assert.That((RustCommands.Passed(""), RustCommands.Failed("")), Is.EqualTo((0, 0)));
  }

  [Test]
  public void SmokeProblem_IsNullOnceATestPassed()
    => Assert.That(RustCommands.SmokeProblem("test result: ok. 1 passed; 0 failed; 0 ignored;"), Is.Null);

  [Test]
  public void Commands_AreTheFiveLinesOfTheLayer() {
    var commands = new RustCommands("1.99.0", "nightly-2026-11-01", null, null);
    var lines = commands.Smoke.Append(commands.Miri()).Select(spec => string.Join(' ', spec.Arguments.Prepend("cargo")));
    Assert.That(lines, Is.EqualTo(new[] {
      "cargo fmt --check", "cargo clippy --all-targets -- -D warnings", "cargo test", "cargo run --quiet", "cargo +nightly-2026-11-01 miri test"
    }));
    Assert.That(commands.Smoke.Append(commands.Miri()).Select(spec => spec.Toolchain), Is.All.EqualTo(RustCommands.Toolchain));
  }

  [Test]
  public void Commands_AreOfflineAndPinTheStableToolchain() {
    var commands = new RustCommands("1.99.0", "nightly-2026-11-01", "/opt/rustup", "/opt/miri");
    foreach (var spec in commands.Smoke.Append(commands.Build()).Append(commands.Miri())) {
      Assert.That(spec.Environment["CARGO_NET_OFFLINE"], Is.EqualTo("true"), spec.ToString());
      Assert.That(spec.Environment["RUSTUP_TOOLCHAIN"], Is.EqualTo("1.99.0"), spec.ToString());
      Assert.That(spec.Environment["RUSTUP_HOME"], Is.EqualTo("/opt/rustup"), spec.ToString());
    }

    Assert.That(commands.Miri().Environment["MIRI_SYSROOT"], Is.EqualTo("/opt/miri"));
    Assert.That(commands.Run.Environment.ContainsKey("MIRI_SYSROOT"), Is.False);
    Assert.That(commands.HostFolders, Is.EqualTo(new[] { "/opt/rustup", "/opt/miri" }));
  }

  [Test]
  public void Commands_WithoutHostFolders_SetNeither() {
    var commands = new RustCommands("1.99.0", "nightly-2026-11-01", "", null);
    Assert.That(commands.Miri().Environment.Keys, Has.None.EqualTo("RUSTUP_HOME").And.None.EqualTo("MIRI_SYSROOT"));
    Assert.That(commands.HostFolders, Is.Empty);
  }

  [TestCase("", "nightly-2026-11-01")]
  [TestCase("1.99.0", "")]
  public void Commands_NeedBothVersions(string stable, string channel)
    => Assert.That(() => new RustCommands(stable, channel, null, null), Throws.ArgumentException);

  [Test]
  public void FromLock_TakesTheVersionsOfTheLock() {
    var commands = RustCommands.FromLock(RepositoryLock, _ => null);
    Assert.That((commands.Stable, commands.MiriChannel), Is.EqualTo((RepositoryLock["rust"].Version, RepositoryLock["rust-miri"].Version)));
    Assert.That(commands.HostFolders, Is.Empty);
  }

  [Test]
  public void FromLock_PrefersTheVariables() {
    var environment = new Dictionary<string, string> { ["RUSTUP_HOME"] = "/r", ["MIRI_SYSROOT"] = "/m", ["HOME"] = this._directory };
    var commands = RustCommands.FromLock(RepositoryLock, name => environment.GetValueOrDefault(name));
    Assert.That((commands.RustupHome, commands.MiriSysroot), Is.EqualTo(("/r", "/m")));
  }

  [Test]
  public void FromLock_FallsBackToTheHomeFolders() {
    Directory.CreateDirectory(Path.Combine(this._directory, ".rustup"));
    Directory.CreateDirectory(Path.Combine(this._directory, ".cache", "miri"));
    var commands = RustCommands.FromLock(RepositoryLock, name => name == "HOME" ? this._directory : null);
    Assert.That((commands.RustupHome, commands.MiriSysroot), Is.EqualTo((Path.Combine(this._directory, ".rustup"), Path.Combine(this._directory, ".cache", "miri"))));
  }

  [Test]
  public void FromLock_FollowsTheCacheVariable() {
    Directory.CreateDirectory(Path.Combine(this._directory, "miri"));
    var commands = RustCommands.FromLock(RepositoryLock, name => name == "XDG_CACHE_HOME" ? this._directory : null);
    Assert.That((commands.RustupHome, commands.MiriSysroot), Is.EqualTo(((string?)null, Path.Combine(this._directory, "miri"))));
  }

  [Test]
  public void FromLock_IgnoresHomeFoldersThatDoNotExist() {
    var commands = RustCommands.FromLock(RepositoryLock, name => name == "HOME" ? this._directory : null);
    Assert.That(commands.HostFolders, Is.Empty);
  }

  private static Dictionary<string, ReadOnlyMemory<byte>> Files(params (string Name, string Text)[] files)
    => files.ToDictionary(file => file.Name, file => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(file.Text));

  /// <summary>A sysroot as rustup lays one out, its <c>bin</c> with or without <c>miri</c>.</summary>
  private string Nightly(string channel, bool withMiri) {
    var sysroot = Path.Combine(this._directory, "toolchains", channel + "-x86_64-unknown-linux-gnu");
    Directory.CreateDirectory(Path.Combine(sysroot, "bin"));
    if (withMiri)
      File.WriteAllText(Path.Combine(sysroot, "bin", "miri"), "");
    return sysroot;
  }

  private ToolchainLocator Locator(string output, Dictionary<string, string> environment)
    => new(RepositoryLock, new FixedOutput(output), name => environment.GetValueOrDefault(name), ToolchainLocator.CurrentPlatform, ToolchainLocator.DefaultTimeout);

}
