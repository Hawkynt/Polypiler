using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Python;

/// <summary>
/// <c>polypiler translate --to python</c>, run inside the test process: the module it writes, the
/// profile option, the options that do not apply, the manifest, and a construct the backend refuses.
/// The acceptance tests run what it writes under CPython.
/// </summary>
[TestFixture]
public sealed class TranslateToPythonTests {

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

  private (int ExitCode, string Output, string Error) Translate(string methods, params string[] options) {
    var source = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(source, PrototypeFixture.Wrap(methods));
    using var output = new StringWriter();
    using var error = new StringWriter();
    string[] arguments = ["translate", source, "--from", "csharp", "--to", "python", .. options, "--output", this._out];
    var exitCode = Program.Run(arguments, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  [Test]
  public void ToPython_WritesTheModuleInSnakeCase_AndItsManifest() {
    var (exitCode, output, error) = this.Translate("public static int AddOne(int a) => a + 1;");
    Assert.That(exitCode, Is.Zero, error);
    Assert.That(output.Trim(), Is.EqualTo(Path.Combine(this._out, "snippet.py")));
    var text = File.ReadAllText(Path.Combine(this._out, "snippet.py"));
    Assert.That(text, Does.Contain("# Python profile: py3.14.").And.Contain("__all__ = [\"add_one\"]").And.Contain("def add_one(a: int) -> int:"));
    var manifest = File.ReadAllText(Path.Combine(this._out, "polypiler.manifest.json"));
    Assert.That(manifest, Does.Contain("\"python\"").And.Contain("\"py3.14\""));
  }

  [TestCase("py3.8")]
  [TestCase("3.11")]
  public void Profile_IsStatedInTheModule(string profile) {
    var (exitCode, _, error) = this.Translate("public static int F(int a) => a;", "--profile", profile, "--float", "strict", "--disable-opt", "O0501");
    Assert.That(exitCode, Is.Zero, error);
    Assert.That(File.ReadAllText(Path.Combine(this._out, "snippet.py")), Does.Contain($"# Python profile: py{profile.Replace("py", "", StringComparison.Ordinal)}."));
  }

  [TestCase(new[] { "--profile", "py2.7" }, "unknown Python profile 'py2.7'")]
  [TestCase(new[] { "--namespace", "N" }, "the option '--namespace' does not apply to the target language 'python'")]
  [TestCase(new[] { "--float", "fast" }, "unknown floating-point mode 'fast'")]
  public void OptionsThatDoNotApply_AreAUsageError(string[] options, string message) {
    var (exitCode, _, error) = this.Translate("public static int F(int a) => a;", options);
    Assert.That(exitCode, Is.EqualTo(2), error);
    Assert.That(error, Does.Contain("PLY0003").And.Contain(message));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  [Test]
  public void AConstructOutsideTheBackend_IsRefusedWithItsName_AndNothingIsWritten() {
    var (exitCode, _, error) = this.Translate("public static int F(string s) => s.Length;");
    Assert.That(exitCode, Is.EqualTo(3));
    Assert.That(error, Does.Contain("PLY1850: type 'str' has no Python representation in profile 'py3.14' (function 'F')").And.Contain("PLY1851: 'StrLength' cannot be expressed"));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

}
