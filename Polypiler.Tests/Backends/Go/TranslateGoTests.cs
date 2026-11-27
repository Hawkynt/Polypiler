using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Go;

/// <summary>The command line with <c>--to go</c> and <c>--from go</c>: the profile, the module path, the files written and the refusals.</summary>
[TestFixture]
public sealed class TranslateGoTests {

  private string _directory = "";
  private string _out = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-go-cli-" + Guid.NewGuid().ToString("N"));
    this._out = Path.Combine(this._directory, "out");
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() {
    if (Directory.Exists(this._directory))
      Directory.Delete(this._directory, true);
  }

  private static (int ExitCode, string Output, string Error) Invoke(params string[] arguments) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(arguments, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  private string Source(string text) {
    var path = Path.Combine(this._directory, "Calc.cs");
    File.WriteAllText(path, text);
    return path;
  }

  [Test]
  public void CSharpToGo_WritesTheModule() {
    var source = this.Source("public static class Calc { public static int Half(int a, int b) => a / b; }");
    var (exitCode, output, error) = Invoke("translate", source, "--from", "csharp", "--to", "go", "--profile", "go1.22", "--go-module", "example.test/calc", "--output", this._out);
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(output, Does.Contain("calc.go").And.Contain("go.mod"));
    Assert.That(File.ReadAllText(Path.Combine(this._out, "go.mod")), Is.EqualTo("module example.test/calc\n\ngo 1.22\n"));
    Assert.That(File.ReadAllText(Path.Combine(this._out, "calc.go")), Does.Contain("package calc\n").And.Contain("func Half(a, b int32) int32 {"));
    Assert.That(File.ReadAllText(Path.Combine(this._out, "polypiler.manifest.json")), Does.Contain("go1.22"));
  }

  [Test]
  public void AnUnknownProfile_IsAMisuse() {
    var source = this.Source("public static class Calc { public static int One() => 1; }");
    var (exitCode, _, error) = Invoke("translate", source, "--from", "csharp", "--to", "go", "--profile", "go1.9", "--output", this._out);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(error, Does.Contain("unknown Go profile 'go1.9'"));
  }

  [Test]
  public void AnInvalidModulePath_IsRefused() {
    var source = this.Source("public static class Calc { public static int One() => 1; }");
    var (exitCode, _, error) = Invoke("translate", source, "--from", "csharp", "--to", "go", "--go-module", "Bad Path", "--output", this._out);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UnsupportedConstruct));
    Assert.That(error, Does.Contain("PLY2173"));
  }

  [Test]
  public void AConstructOutsideTheSubset_IsRefusedWithTheCodeOfTheBackend() {
    var source = this.Source("public sealed class Box { public int V; } public static class Calc { public static int Get() => new Box().V; }");
    var (exitCode, _, error) = Invoke("translate", source, "--from", "csharp", "--to", "go", "--output", this._out);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UnsupportedConstruct));
    Assert.That(error, Does.Contain("PLY2150"));
  }

  [Test]
  public void GoSource_IsImportedWhereGoIsInstalled() {
    var source = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "go", "frontend", "checksums.go");
    var (exitCode, output, error) = Invoke("translate", source, "--from", "go", "--to", "javascript", "--output", this._out);
    if (error.Contains("the Go helper failed", StringComparison.Ordinal))
      Assert.Inconclusive("no Go is installed");

    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success), error);
    Assert.That(error, Does.Contain("note go-float-to-int"));
    Assert.That(output, Does.Contain(".mjs"));
    var refused = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "go", "frontend", "refused", "syntax.go");
    var (code, _, message) = Invoke("translate", refused, "--from", "go", "--to", "javascript", "--output", this._out);
    Assert.That((code, message.Contains("PLY2100", StringComparison.Ordinal)), Is.EqualTo(((int)ExitCode.InvalidSource, true)));
    var unsupported = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "go", "frontend", "refused", "method.go");
    Assert.That(Invoke("translate", unsupported, "--from", "go", "--to", "javascript", "--output", this._out).ExitCode, Is.EqualTo((int)ExitCode.UnsupportedConstruct));
  }

}
