using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// The <c>translate</c> command with a Rust source, run inside the test process: <c>--from rust</c>
/// with <c>--overflow-checks</c>, and the exit code of every way an import fails. The acceptance
/// tests run what it writes.
/// </summary>
[TestFixture]
public sealed class RustTranslateCommandTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private (int ExitCode, string Output, string Error) Translate(string source, string target, params string[] further) {
    var path = Path.Combine(this._directory, "src", "hash.rs");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, source);
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", path, "--from", "rust", "--to", target, "--output", Path.Combine(this._directory, "out"), .. further], output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  /// <summary>A Rust file comes out as C# and as JavaScript, the overflow mode deciding the arithmetic.</summary>
  [TestCase("csharp", "panic", "hash.cs", "checked(")]
  [TestCase("javascript", "wrap", "hash.mjs", "export function fnv1a(")]
  public void Translate_WritesTheTarget(string target, string mode, string file, string contains) {
    var (exitCode, output, error) = this.Translate("pub fn fnv1a(h: u32, b: u32) -> u32 { (h ^ b).wrapping_mul(16777619) + b }", target, "--overflow-checks", mode);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success), error);
    Assert.That(output.Trim(), Does.EndWith(file));
    Assert.That(File.ReadAllText(Path.Combine(this._directory, "out", file)), Does.Contain(contains));
  }

  /// <summary>A value of <c>--overflow-checks</c> other than panic or wrap is the command line's error.</summary>
  [Test]
  public void OverflowMode_IsChecked() {
    var (wrong, _, wrongError) = this.Translate("pub fn f() -> u32 { 1 }", "csharp", "--overflow-checks", "trap");
    Assert.That(wrong, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(wrongError, Does.Contain("unknown overflow mode 'trap'; the modes are panic and wrap"));
  }

  private const string Addition = "pub fn add(a: i32, b: i32) -> i32 { a + b }";

  private string Manifest() => File.ReadAllText(Path.Combine(this._directory, "out", "polypiler.manifest.json"));

  /// <summary>
  /// Without <c>--overflow-checks</c> and without a <c>Cargo.toml</c>, the mode is the one of Cargo's
  /// <c>dev</c> profile: panic. The mode and where it came from are a note and a caveat of the manifest.
  /// </summary>
  [Test]
  public void WithoutFlagOrManifest_TheDevProfilePanics() {
    var (exitCode, _, error) = this.Translate(Addition, "csharp");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success), error);
    Assert.That(File.ReadAllText(Path.Combine(this._directory, "out", "hash.cs")), Does.Contain("checked("));
    Assert.That(error, Does.Contain("note rs-overflow-checks: overflow checks on, an overflowing operator panics (the default of Cargo profile 'dev', no Cargo.toml above the source)"));
    Assert.That(this.Manifest(), Does.Contain(TranslateCommand.RustOverflowCaveat).And.Contain("the default of Cargo profile 'dev'"));
  }

  /// <summary>The <c>Cargo.toml</c> above the source decides; <c>--cargo-profile</c> picks the profile; the flag overrides both.</summary>
  [TestCase(new string[0], "checked(", "overflow checks on, an overflowing operator panics (Cargo profile 'dev' of ../Cargo.toml)")]
  [TestCase(new[] { "--cargo-profile", "release" }, "unchecked(", "overflow checks off, an overflowing operator wraps (Cargo profile 'release' of ../Cargo.toml)")]
  [TestCase(new[] { "--cargo-profile", "fast" }, "checked(", "overflow checks on, an overflowing operator panics (Cargo profile 'fast' of ../Cargo.toml)")]
  [TestCase(new[] { "--cargo-profile", "release", "--overflow-checks", "panic" }, "checked(", "overflow checks on, an overflowing operator panics (--overflow-checks)")]
  [TestCase(new[] { "--overflow-checks", "wrap" }, "unchecked(", "overflow checks off, an overflowing operator wraps (--overflow-checks)")]
  public void CargoManifest_DecidesUnlessTheFlagIsGiven(string[] options, string contains, string note) {
    File.WriteAllText(Path.Combine(this._directory, "Cargo.toml"), "[package]\nname = \"hash\"\n\n[profile.release]\noverflow-checks = false # the default\n\n[profile.fast]\ninherits = \"release\"\noverflow-checks = true\n");
    var (exitCode, _, error) = this.Translate(Addition, "csharp", options);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success), error);
    Assert.That(File.ReadAllText(Path.Combine(this._directory, "out", "hash.cs")), Does.Contain(contains));
    Assert.That(error, Does.Contain("note rs-overflow-checks: " + note));
  }

  /// <summary>A profile the manifest does not define, or a manifest that is no TOML, is <c>PLY1503</c>, a usage error.</summary>
  [TestCase("[package]\nname = \"hash\"\n", "fast", "Cargo profile 'fast' is not defined in ../Cargo.toml")]
  [TestCase("[package\n", "dev", "is no TOML: line 1: a table header ends with ']'")]
  public void UnreadableProfile_IsPLY1503(string manifest, string profile, string message) {
    File.WriteAllText(Path.Combine(this._directory, "Cargo.toml"), manifest);
    var (exitCode, _, error) = this.Translate(Addition, "csharp", "--cargo-profile", profile);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(error, Does.Contain("PLY1503").And.Contain("the overflow mode cannot be read from Cargo: ").And.Contain(message));
  }

  /// <summary>Without a manifest a custom profile has nothing to inherit from.</summary>
  [Test]
  public void CustomProfileWithoutManifest_IsPLY1503() {
    var (exitCode, _, error) = this.Translate(Addition, "csharp", "--cargo-profile", "fast");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(error, Does.Contain("PLY1503").And.Contain("Cargo profile 'fast' is not built in, and there is no Cargo.toml above the source to define it"));
  }

  /// <summary>Source rustc rejects is exit code 4, a construct outside the subset exit code 3; the option belongs to Rust alone.</summary>
  [Test]
  public void FailedImport_HasTheExitCodeOfItsCause() {
    Assert.That(this.Translate("pub fn f() -> u32 { true }", "csharp", "--overflow-checks", "wrap").ExitCode, Is.EqualTo((int)ExitCode.InvalidSource));
    Assert.That(this.Translate("pub fn f() -> u128 { 1 }", "csharp", "--overflow-checks", "wrap").ExitCode, Is.EqualTo((int)ExitCode.UnsupportedConstruct));
    using var output = new StringWriter();
    using var error = new StringWriter();
    var path = Path.Combine(this._directory, "a.js");
    File.WriteAllText(path, "function f() { return 1; }");
    Assert.That(Program.Run(["translate", path, "--from", "javascript", "--to", "csharp", "--output", "out", "--overflow-checks", "wrap"], output, error), Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(error.ToString(), Does.Contain("does not apply to the source language 'javascript'"));
    using var cargoError = new StringWriter();
    Assert.That(Program.Run(["translate", path, "--from", "javascript", "--to", "csharp", "--output", "out", "--cargo-profile", "release"], output, cargoError), Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(cargoError.ToString(), Does.Contain("the option '--cargo-profile' does not apply to the source language 'javascript'"));
  }

}
