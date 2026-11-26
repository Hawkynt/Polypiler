using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;

namespace Polypiler.Tests.Backends.VBScript;

/// <summary>CLI integration tests for <c>polypiler translate</c> with <c>--from vbscript</c> and <c>--to vbscript</c>.</summary>
[TestFixture]
public sealed class TranslateVBScriptTests {

  private string _directory = "";
  private string _out = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-vbs-cli-" + Guid.NewGuid().ToString("N"));
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

  [Test]
  public void CSharpToVBScript_WritesValidScript() {
    var source = Path.Combine(this._directory, "Calc.cs");
    File.WriteAllText(source, """
      public static class Calc {
        public static int DoubleAdd(int a, int b) => (a + b) * 2;
      }
      """);
    var (exitCode, output, error) = Invoke("translate", source, "--from", "csharp", "--to", "vbscript", "--profile", "vbscript/5.8", "--output", this._out);
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    var vbsFile = output.Trim();
    Assert.That(vbsFile, Does.EndWith(".vbs"));
    var text = File.ReadAllText(vbsFile);
    Assert.That(text, Does.Contain("Option Explicit").And.Contain("Function DoubleAdd("));
  }

  [Test]
  public void VBScriptToCSharp_AndJavaScript_WithInlineAndFileContracts_Succeeds() {
    var source = Path.Combine(this._directory, "MathMod.vbs");
    File.WriteAllText(source, """
      Option Explicit
      Function Add(a, b)
        Add = a + b
      End Function
      """);
    var contractFile = Path.Combine(this._directory, "contracts.txt");
    File.WriteAllText(contractFile, "Add.a: i32\nAdd.b: i32\n");

    var (exitCs, outCs, errCs) = Invoke("translate", source, "--from", "vbscript", "--to", "csharp",
      "--vbscript-contract", contractFile, "--vbscript-contract", "Add.return: i32", "--output", this._out);
    Assert.That((exitCs, errCs), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(File.ReadAllText(outCs.Trim()), Does.Contain("public static int Add(int a, int b)"));

    var jsOut = Path.Combine(this._directory, "js");
    var (exitJs, outJs, errJs) = Invoke("translate", source, "--from", "vbscript", "--to", "javascript",
      "--vbscript-contract", contractFile, "--vbscript-contract", "Add.return: i32", "--output", jsOut);
    Assert.That((exitJs, errJs), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(File.ReadAllText(outJs.Trim()), Does.Contain("function Add("));
  }

  [Test]
  public void VBScriptCli_ErrorPaths_ReportProperDiagnosticsAndExitCodes() {
    var source = Path.Combine(this._directory, "Bad.vbs");
    File.WriteAllText(source, "Function F()\n  F = 1 + )\nEnd Function\n");

    var (syntaxExit, _, syntaxErr) = Invoke("translate", source, "--from", "vbscript", "--to", "csharp", "--output", this._out);
    Assert.That(syntaxExit, Is.EqualTo(TranslateCommand.InvalidSource));
    Assert.That(syntaxErr, Does.Contain("PLY2600"));

    var unsupported = Path.Combine(this._directory, "Unsup.vbs");
    File.WriteAllText(unsupported, "Sub S()\n  Eval \"1\"\nEnd Sub\n");
    var (unsupExit, _, unsupErr) = Invoke("translate", unsupported, "--from", "vbscript", "--to", "csharp", "--output", this._out);
    Assert.That(unsupExit, Is.EqualTo(TranslateCommand.UnsupportedConstruct));
    Assert.That(unsupErr, Does.Contain("PLY2601"));

    var valid = Path.Combine(this._directory, "Ok.vbs");
    File.WriteAllText(valid, "Function F(x)\n  F = x + 1\nEnd Function\n");
    var (missingExit, _, missingErr) = Invoke("translate", valid, "--from", "vbscript", "--to", "csharp",
      "--vbscript-contract", "does_not_exist.txt", "--output", this._out);
    Assert.That(missingExit, Is.EqualTo((int)ExitCode.EnvironmentError));
    Assert.That(missingErr, Does.Contain("PLY0004"));

    var cs = Path.Combine(this._directory, "Ok.cs");
    File.WriteAllText(cs, "public static class Ok { public static int F(int x) => x; }");
    var (profExit, _, profErr) = Invoke("translate", cs, "--from", "csharp", "--to", "vbscript", "--profile", "vbscript/4.0", "--output", this._out);
    Assert.That(profExit, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(profErr, Does.Contain("unknown VBScript profile"));
  }

}
