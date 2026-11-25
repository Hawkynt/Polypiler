using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Msil;

/// <summary>CLI unit tests for <c>polypiler translate</c> with <c>msil</c> and <c>jvm</c> frontends and backends.</summary>
[TestFixture]
public sealed class TranslateMsilAndJvmTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-bc-cli-" + Guid.NewGuid().ToString("N"));
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
  public void CSharpToMsilAndBackToJavaScriptIife_RoundTripsThroughCli() {
    var csPath = Path.Combine(this._directory, "Calc.cs");
    File.WriteAllText(csPath, PrototypeFixture.Wrap("""
      public static int AddMul(int a, int b) => (a + b) * 3;
      public static int Entry() => AddMul(4, 5);
      """));

    var msilOut = Path.Combine(this._directory, "msil-out");
    var (exit1, stdout1, stderr1) = this.RunCli(csPath, "--from", "csharp", "--to", "msil", "--profile", "msil/net10", "--output-type", "exe", "--float", "accurate", "--output", msilOut);
    Assert.That(exit1, Is.Zero, stderr1);
    var dllPath = Path.Combine(msilOut, "Snippet.dll");
    var rcPath = Path.Combine(msilOut, "Snippet.runtimeconfig.json");
    Assert.That(File.Exists(dllPath), Is.True, stdout1);
    Assert.That(File.Exists(rcPath), Is.True, stdout1);
    Assert.That(File.ReadAllText(Path.Combine(msilOut, "polypiler.manifest.json")), Does.Contain("\"msil\"").And.Contain("\"msil/net10\""));

    var jsOut = Path.Combine(this._directory, "js-out");
    var (exit2, _, stderr2) = this.RunCli(dllPath, "--from", "msil", "--export", "AddMul", "--to", "javascript", "--module", "iife", "--global", "CalcMod", "--output", jsOut);
    Assert.That(exit2, Is.Zero, stderr2);
    var jsFiles = Directory.GetFiles(jsOut, "*.js");
    Assert.That(jsFiles, Has.Length.EqualTo(1));
    var jsText = File.ReadAllText(jsFiles[0]);
    Assert.That(jsText, Does.Contain("CalcMod").And.Contain("AddMul"));
  }

  [Test]
  public void CSharpToJvmJarAndBackToPython_RoundTripsThroughCli() {
    var csPath = Path.Combine(this._directory, "MathPkg.cs");
    File.WriteAllText(csPath, PrototypeFixture.Wrap("""
      public static int SumUp(int n) {
        int s = 0;
        for (int i = 1; i <= n; i++) s += i;
        return s;
      }
      """));

    var jvmOut = Path.Combine(this._directory, "jvm-out");
    var (exit1, _, stderr1) = this.RunCli(csPath, "--from", "csharp", "--to", "jvm", "--profile", "jvm/17", "--package-format", "jar", "--float", "strict", "--output", jvmOut);
    Assert.That(exit1, Is.Zero, stderr1);
    var jarPath = Path.Combine(jvmOut, "Snippet.jar");
    Assert.That(File.Exists(jarPath), Is.True);
    Assert.That(File.ReadAllText(Path.Combine(jvmOut, "polypiler.manifest.json")), Does.Contain("\"jvm\"").And.Contain("\"jvm/17\""));

    var pyOut = Path.Combine(this._directory, "py-out");
    var (exit2, _, stderr2) = this.RunCli(jarPath, "--from", "jvm", "--export", "SumUp", "--to", "python", "--output", pyOut);
    Assert.That(exit2, Is.Zero, stderr2);
    var pyFiles = Directory.GetFiles(pyOut, "*.py");
    Assert.That(pyFiles, Has.Length.EqualTo(1));
    Assert.That(File.ReadAllText(pyFiles[0]), Does.Contain("def sum_up"));
  }

  [Test]
  public void MsilAndJvm_InvalidBinaryInputs_ReportInvalidSource() {
    var badPath = Path.Combine(this._directory, "corrupt.bin");
    File.WriteAllBytes(badPath, [0x01, 0x02, 0x03, 0x04]);

    var (exitMsil, _, errMsil) = this.RunCli(badPath, "--from", "msil", "--to", "javascript", "--output", Path.Combine(this._directory, "o1"));
    Assert.That(exitMsil, Is.EqualTo(TranslateCommand.InvalidSource));
    Assert.That(errMsil, Does.Contain("PLY3000"));

    var (exitJvm, _, errJvm) = this.RunCli(badPath, "--from", "jvm", "--to", "python", "--output", Path.Combine(this._directory, "o2"));
    Assert.That(exitJvm, Is.EqualTo(TranslateCommand.InvalidSource));
    Assert.That(errJvm, Does.Contain("PLY3100"));
  }

  [TestCase(new[] { "--to", "msil", "--profile", "msil/net6" }, "unknown MSIL profile 'msil/net6'")]
  [TestCase(new[] { "--to", "msil", "--output-type", "winexe" }, "unknown MSIL output kind 'winexe'")]
  [TestCase(new[] { "--to", "msil", "--float", "fast" }, "unknown floating-point mode 'fast'")]
  [TestCase(new[] { "--to", "msil", "--package-format", "jar" }, "the option '--package-format' does not apply to the target language 'msil'")]
  [TestCase(new[] { "--to", "jvm", "--profile", "jvm/11" }, "unknown JVM profile 'jvm/11'")]
  [TestCase(new[] { "--to", "jvm", "--package-format", "war" }, "unknown JVM package format 'war'")]
  [TestCase(new[] { "--to", "jvm", "--float", "fast" }, "unknown floating-point mode 'fast'")]
  [TestCase(new[] { "--to", "jvm", "--output-type", "exe" }, "the option '--output-type' does not apply to the target language 'jvm'")]
  public void InvalidMsilAndJvmOptions_AreUsageErrors(string[] extra, string expectedMessage) {
    var csPath = Path.Combine(this._directory, "S.cs");
    File.WriteAllText(csPath, PrototypeFixture.Wrap("public static int F(int x) => x;"));
    var outDir = Path.Combine(this._directory, "bad-out");
    var (exitCode, _, error) = this.RunCli([csPath, "--from", "csharp", .. extra, "--output", outDir]);
    Assert.That(exitCode, Is.EqualTo(2), error);
    Assert.That(error, Does.Contain("PLY0003").And.Contain(expectedMessage));
    Assert.That(Directory.Exists(outDir), Is.False);
  }

  [Test]
  public void JavaScriptSource_ToMsilAndJvm_LegalizesEcmaIntrinsics() {
    var jsPath = Path.Combine(this._directory, "Ecma.js");
    File.WriteAllText(jsPath, """
      /** @param {number} x @returns {int} */
      function toI(x) { return x | 0; }
      /** @param {number} x @returns {uint} */
      function toU(x) { return x >>> 0; }
      /** @param {number} x @returns {number} */
      function rnd(x) { return Math.round(x); }
      """);

    var msilOut = Path.Combine(this._directory, "ecma-msil");
    var (e1, _, err1) = this.RunCli(jsPath, "--from", "javascript", "--to", "msil", "--output", msilOut);
    Assert.That(e1, Is.Zero, err1);
    Assert.That(Directory.GetFiles(msilOut, "*.dll"), Has.Length.EqualTo(1));

    var jvmOut = Path.Combine(this._directory, "ecma-jvm");
    var (e2, _, err2) = this.RunCli(jsPath, "--from", "javascript", "--to", "jvm", "--output", jvmOut);
    Assert.That(e2, Is.Zero, err2);
    Assert.That(Directory.GetFiles(jvmOut, "*.jar"), Has.Length.EqualTo(1));
  }

  [Test]
  public void GeneratedFile_GetBytes_ReturnsTextOrBinaryBytes() {
    var text = new GeneratedFile("a.txt", "hi");
    Assert.That(text.GetBytes(), Is.EqualTo(new byte[] { (byte)'h', (byte)'i' }));
    var bin = new GeneratedFile("a.bin", "") { BinaryContent = [0xCA, 0xFE] };
    Assert.That(bin.GetBytes(), Is.EqualTo(new byte[] { 0xCA, 0xFE }));
  }

}
