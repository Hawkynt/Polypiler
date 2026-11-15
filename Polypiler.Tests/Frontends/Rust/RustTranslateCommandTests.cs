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
    var path = Path.Combine(this._directory, "hash.rs");
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

  /// <summary>Without <c>--overflow-checks</c> the import is refused as a usage error; a value other than panic or wrap is the command line's error.</summary>
  [Test]
  public void OverflowMode_IsRequiredAndChecked() {
    var (missing, _, missingError) = this.Translate("pub fn f() -> u32 { 1 }", "csharp");
    Assert.That(missing, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(missingError, Does.Contain("PLY1503"));
    var (wrong, _, wrongError) = this.Translate("pub fn f() -> u32 { 1 }", "csharp", "--overflow-checks", "trap");
    Assert.That(wrong, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(wrongError, Does.Contain("unknown overflow mode 'trap'; the modes are panic and wrap"));
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
  }

}
