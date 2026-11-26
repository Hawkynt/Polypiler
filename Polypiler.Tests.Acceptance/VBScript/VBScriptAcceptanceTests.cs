using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.VBScript;

/// <summary>
/// End-to-end acceptance tests for VBScript 5.8 (<c>--from vbscript</c> and <c>--to vbscript</c>)
/// through the <c>polypiler</c> CLI, executing generated JavaScript in Node.js and generated
/// VBScript in Windows Script Host (<c>cscript.exe</c> on Windows or <c>wine cscript</c> when installed).
/// </summary>
[TestFixture, Layer("core")]
[Category("EndToEnd")]
public sealed class VBScriptAcceptanceTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-vbs-e2e-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() {
    if (Directory.Exists(this._directory))
      Directory.Delete(this._directory, true);
  }

  private static (int ExitCode, string Output, string Error) RunCli(params string[] arguments) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(arguments, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  [Test]
  public void VBScriptToJavaScript_ExecutesLiveInNode() {
    var src = Path.Combine(this._directory, "Kernel.vbs");
    File.WriteAllText(src, """
      Option Explicit
      ' @param n: i32
      ' @return: i32
      Function Fact(n)
        Dim r, i
        r = 1
        For i = 2 To n
          r = r * i
        Next
        Fact = r
      End Function
      """);
    var outDir = Path.Combine(this._directory, "js");
    var result = RunCli("translate", src, "--from", "vbscript", "--to", "javascript", "--module", "cjs", "--output", outDir);
    Assert.That(result.ExitCode, Is.Zero, result.Error);

    var jsPath = result.Output.Trim();
    var driver = Path.Combine(this._directory, "run.cjs");
    File.WriteAllText(driver, $"const m = require({System.Text.Json.JsonSerializer.Serialize(jsPath)}); console.log(m.Fact(6));");
    var node = RunProcess("node", $"\"{driver}\"");
    Assert.That(node.ExitCode, Is.Zero, node.Stderr);
    Assert.That(node.Stdout.Trim(), Is.EqualTo("720"));
  }

  [Test]
  public void CSharpToVBScript_AndRoundTrip_ExecutesLiveWhenCScriptAvailable() {
    var src = Path.Combine(this._directory, "Check.cs");
    File.WriteAllText(src, """
      public static class Check {
        public static int Fib(int n) {
          int a = 0;
          int b = 1;
          for (int i = 0; i < n; ++i) {
            int t = a + b;
            a = b;
            b = t;
          }
          return a;
        }
      }
      """);
    var vbsOut = Path.Combine(this._directory, "vbs");
    var result = RunCli("translate", src, "--from", "csharp", "--to", "vbscript", "--profile", "vbscript/5.8", "--output", vbsOut);
    Assert.That(result.ExitCode, Is.Zero, result.Error);

    var vbsPath = result.Output.Trim();
    var vbsCode = File.ReadAllText(vbsPath);
    Assert.That(vbsCode, Does.Contain("Option Explicit").And.Contain("Function Fib("));

    // Round-trip VBScript back to JavaScript and verify live in Node.js on every OS!
    var rtOut = Path.Combine(this._directory, "rt-js");
    var rt = RunCli("translate", vbsPath, "--from", "vbscript", "--to", "javascript", "--module", "cjs",
      "--vbscript-contract", "Fib.n: i32", "--vbscript-contract", "Fib.return: i32", "--output", rtOut);
    Assert.That(rt.ExitCode, Is.Zero, rt.Error);

    var rtJs = rt.Output.Trim();
    var driver = Path.Combine(this._directory, "rt.cjs");
    File.WriteAllText(driver, $"const m = require({System.Text.Json.JsonSerializer.Serialize(rtJs)}); console.log(m.Fib(10));");
    var node = RunProcess("node", $"\"{driver}\"");
    Assert.That(node.ExitCode, Is.Zero, node.Stderr);
    Assert.That(node.Stdout.Trim(), Is.EqualTo("55"));

    // Execute generated VBScript directly in cscript.exe (Windows) or wine cscript (Linux when installed).
    var host = FindCScriptHost();
    if (host is null)
      return;

    var runner = Path.Combine(this._directory, "run.vbs");
    File.WriteAllText(runner, vbsCode + "\nWScript.Echo Fib(10)\n");
    var cscript = RunProcess(host.Value.FileName, $"{host.Value.Prefix} \"{runner}\"", setWineDebug: !OperatingSystem.IsWindows());
    Assert.That(cscript.ExitCode, Is.Zero, cscript.Stderr);
    Assert.That(cscript.Stdout.Trim(), Is.EqualTo("55"));
  }

  private static (string FileName, string Prefix)? FindCScriptHost() {
    if (OperatingSystem.IsWindows()) {
      var sys = Path.Combine(Environment.SystemDirectory, "cscript.exe");
      return File.Exists(sys) ? (sys, "//Nologo") : ("cscript.exe", "//Nologo");
    }

    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
      var candidate = Path.Combine(dir, "wine");
      if (File.Exists(candidate))
        return (candidate, "cscript //Nologo");
    }

    return null;
  }

  private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, string arguments, bool setWineDebug = false) {
    var psi = new ProcessStartInfo(fileName, arguments) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    if (setWineDebug)
      psi.Environment["WINEDEBUG"] = "-all";
    using var process = Process.Start(psi)!;
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit(30_000);
    return (process.ExitCode, stdout, stderr);
  }

}
