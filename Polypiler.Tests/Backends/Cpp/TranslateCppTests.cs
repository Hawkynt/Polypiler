using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Cpp;

/// <summary>CLI unit tests for <c>polypiler translate</c> with <c>--from cpp</c> and <c>--to cpp</c>.</summary>
[TestFixture]
public sealed class TranslateCppTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-cpp-cli-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() {
    if (Directory.Exists(this._directory))
      Directory.Delete(this._directory, true);
  }

  private (int ExitCode, string Output, string Error) RunCli(params string[] arguments) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", .. arguments], output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  [Test]
  public void CSharpToCppAndBackToJavaScript_RoundTripsThroughCli() {
    var csPath = Path.Combine(this._directory, "Kernel.cs");
    File.WriteAllText(csPath, PrototypeFixture.Wrap("public static int DoubleAdd(int a, int b) => (a + b) * 2;"));

    var cppOut = Path.Combine(this._directory, "cpp-out");
    var (exit1, _, err1) = this.RunCli(csPath, "--from", "csharp", "--to", "cpp", "--profile", "cpp/cpp20", "--namespace", "demo::math", "--float", "strict", "--output", cppOut);
    Assert.That(exit1, Is.Zero, err1);
    var hpp = Directory.GetFiles(cppOut, "*.hpp").Single();
    var cpp = Directory.GetFiles(cppOut, "*.cpp").Single();
    Assert.That(File.ReadAllText(hpp), Does.Contain("namespace demo::math"));
    Assert.That(File.ReadAllText(cpp), Does.Contain("double_add"));
    Assert.That(File.ReadAllText(Path.Combine(cppOut, "polypiler.manifest.json")), Does.Contain("\"cpp\"").And.Contain("\"cpp/cpp20\""));

    var cppSrc = Path.Combine(this._directory, "Calc.cpp");
    File.WriteAllText(cppSrc, """
      #include <cstdint>
      int32_t tri(int32_t n) {
        int32_t s = 0;
        for (int32_t i = 1; i <= n; ++i) s += i;
        return s;
      }
      """);
    var jsOut = Path.Combine(this._directory, "js-out");
    var (exit2, _, err2) = this.RunCli(cppSrc, "--from", "cpp", "--to", "javascript", "--output", jsOut);
    Assert.That(exit2, Is.Zero, err2);
    Assert.That(File.ReadAllText(Directory.GetFiles(jsOut, "*.mjs").Single()), Does.Contain("export function tri"));
  }

  [Test]
  public void InvalidCppSource_ReportsSyntaxDiagnostic() {
    var badPath = Path.Combine(this._directory, "Bad.cpp");
    File.WriteAllText(badPath, "int32_t broken( { return 1; }");
    var (exitCode, _, error) = this.RunCli(badPath, "--from", "cpp", "--to", "csharp", "--output", Path.Combine(this._directory, "out"));
    Assert.That(exitCode, Is.EqualTo(TranslateCommand.InvalidSource));
    Assert.That(error, Does.Contain("PLY1400"));
  }

  [TestCase(new[] { "--to", "cpp", "--profile", "cpp/cpp11" }, "unknown C++ profile 'cpp/cpp11'")]
  [TestCase(new[] { "--to", "cpp", "--float", "fast" }, "unknown floating-point mode 'fast'")]
  [TestCase(new[] { "--to", "cpp", "--module", "esm" }, "the option '--module' does not apply to the target language 'cpp'")]
  public void InvalidCppOptions_AreUsageErrors(string[] extra, string expectedMessage) {
    var csPath = Path.Combine(this._directory, "S.cs");
    File.WriteAllText(csPath, PrototypeFixture.Wrap("public static int F(int x) => x;"));
    var outDir = Path.Combine(this._directory, "bad-out");
    var (exitCode, _, error) = this.RunCli([csPath, "--from", "csharp", .. extra, "--output", outDir]);
    Assert.That(exitCode, Is.EqualTo(2), error);
    Assert.That(error, Does.Contain("PLY0003").And.Contain(expectedMessage));
    Assert.That(Directory.Exists(outDir), Is.False);
  }

}
