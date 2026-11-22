using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Perl;

/// <summary>CLI tests for <c>polypiler translate --to perl</c>.</summary>
[TestFixture]
public sealed class TranslateToPerlTests {

  private string _directory = "";
  private string _out = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-perl-" + Guid.NewGuid().ToString("N"));
    this._out = Path.Combine(this._directory, "out");
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() {
    if (Directory.Exists(this._directory))
      Directory.Delete(this._directory, true);
  }

  private (int ExitCode, string Output, string Error) Translate(string methods, params string[] options) {
    var source = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(source, PrototypeFixture.Wrap(methods));
    using var output = new StringWriter();
    using var error = new StringWriter();
    string[] arguments = ["translate", source, "--from", "csharp", "--to", "perl", .. options, "--output", this._out];
    var exitCode = Program.Run(arguments, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  [Test]
  public void ToPerl_WritesCpanLayoutAndManifest() {
    var (exitCode, output, error) = this.Translate("public static int AddOne(int a) => a + 1;");
    Assert.That(exitCode, Is.Zero, error);
    var pmPath = Path.Combine(this._out, "lib", "Snippet.pm");
    Assert.That(output.Trim(), Is.EqualTo(pmPath));
    var text = File.ReadAllText(pmPath);
    Assert.That(text, Does.Contain("package Snippet;").And.Contain("use strict;").And.Contain("sub add_one"));
    var manifest = File.ReadAllText(Path.Combine(this._out, "polypiler.manifest.json"));
    Assert.That(manifest, Does.Contain("\"perl\"").And.Contain("\"perl/5\""));
  }

  [Test]
  public void ToPerl_AcceptsProfileByteStorageObjectModelAndFloat() {
    var (exitCode, _, error) = this.Translate("public static byte First(byte[] b) => b[0];", "--profile", "perl/5.8", "--byte-storage", "string", "--object-model", "inside-out", "--float", "strict");
    Assert.That(exitCode, Is.Zero, error);
    var text = File.ReadAllText(Path.Combine(this._out, "lib", "Snippet.pm"));
    Assert.That(text, Does.Contain("use 5.008;").And.Contain("vec("));
  }

  [TestCase(new[] { "--profile", "perl/6" }, "unknown Perl profile 'perl/6'")]
  [TestCase(new[] { "--byte-storage", "packed" }, "unknown Perl byte storage 'packed'")]
  [TestCase(new[] { "--object-model", "corinna" }, "unknown Perl object model 'corinna'")]
  [TestCase(new[] { "--profile", "perl/5.6", "--object-model", "moose" }, "Moose object model requires Perl 5.8 or later")]
  [TestCase(new[] { "--namespace", "N" }, "the option '--namespace' does not apply to the target language 'perl'")]
  [TestCase(new[] { "--float", "fast" }, "unknown floating-point mode 'fast'")]
  public void InvalidOptions_AreUsageErrors(string[] options, string message) {
    var (exitCode, _, error) = this.Translate("public static int F(int a) => a;", options);
    Assert.That(exitCode, Is.EqualTo(2), error);
    Assert.That(error, Does.Contain("PLY0003").And.Contain(message));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

}
