using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>The <c>translate</c> command with <c>--to rust</c>, run inside the test process.</summary>
[TestFixture]
public sealed class TranslateToRustTests {

  private string _directory = "";
  private string _out = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    this._out = Path.Combine(this._directory, "out");
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private static (int ExitCode, string Output, string Error) Invoke(params string[] arguments) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(arguments, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  [Test]
  public void Translate_WritesTheCrateAndItsManifest() {
    var (exitCode, output, error) = Invoke("translate", PrototypeFixture.PathOf("Numbers"), "--from", "csharp", "--to", "rust", "--output", this._out);
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(output.Trim().Split('\n').Select(line => Path.GetRelativePath(this._out, line.Trim()).Replace('\\', '/')), Is.EqualTo(new[] { "Cargo.toml", "src/lib.rs", "src/numbers.rs" }));
    Assert.That(File.ReadAllText(Path.Combine(this._out, "src", "numbers.rs")), Does.Contain("pub fn gcd(a: i32, b: i32) -> i32 {"));
    var manifest = File.ReadAllText(Path.Combine(this._out, Manifest.FileName));
    Assert.That(manifest, Does.Contain("\"rust\"").And.Contain("rust2024 (Rust 1.85)").And.Contain("src/numbers.rs"));
  }

  [Test]
  public void ProfileAndRustVersion_AreTheTarget() {
    var (exitCode, _, error) = Invoke("translate", PrototypeFixture.PathOf("Numbers"), "--from", "csharp", "--to", "rust", "--output", this._out, "--profile", "rust2021", "--rust-version", "1.77");
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(File.ReadAllText(Path.Combine(this._out, "Cargo.toml")), Does.Contain("edition = \"2021\"\nrust-version = \"1.77\"\n"));
    Assert.That(File.ReadAllText(Path.Combine(this._out, "src", "lib.rs")), Does.Contain("//! Rust profile: rust2021 (Rust 1.77)."));
  }

  [TestCase("--profile", "rust2015", "unknown Rust profile 'rust2015'")]
  [TestCase("--rust-version", "1.2", "Rust 1.2 is older than the 2024 edition")]
  [TestCase("--rust-version", "two", "'two' is not a Rust version")]
  [TestCase("--float", "fast", "unknown floating-point mode 'fast'")]
  [TestCase("--namespace", "A.B", "the option '--namespace' does not apply to the target language 'rust'")]
  public void Misuse_IsRefused(string option, string value, string message) {
    var (exitCode, _, error) = Invoke("translate", PrototypeFixture.PathOf("Numbers"), "--from", "csharp", "--to", "rust", "--output", this._out, option, value);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(error, Does.Contain("PLY0003").And.Contain(message));
  }

  [Test]
  public void RustVersion_DoesNotApplyToCSharp() {
    var (exitCode, _, error) = Invoke("translate", PrototypeFixture.PathOf("Numbers"), "--from", "csharp", "--to", "csharp", "--output", this._out, "--rust-version", "1.80");
    Assert.That((exitCode, error.Contains("the option '--rust-version' does not apply to the target language 'csharp'", StringComparison.Ordinal)), Is.EqualTo(((int)ExitCode.UsageError, true)));
  }

  [Test]
  public void UnsupportedConstruct_IsNamed() {
    var (exitCode, _, error) = Invoke("translate", PrototypeFixture.PathOf("pointers/Pointers"), "--from", "csharp", "--to", "rust", "--output", this._out);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UnsupportedConstruct));
    Assert.That(error, Does.Contain("PLY1550").And.Contain("'the type u8*'"));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

}
