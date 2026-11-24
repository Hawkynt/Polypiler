using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Frontends.Batch;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Batch;

/// <summary>Compares lifted integer scripts with the system command interpreter.</summary>
[TestFixture, Layer("core"), Category("Acceptance"), Category("EndToEnd"), RequiresToolchain("cmd")]
public sealed class BatchFrontendNativeTests {

  [Test]
  public void CountdownMatchesTheNativeStatus() {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "countdown.bat");
    Compare(File.ReadAllText(path), 42);
  }

  [TestCase("set /a \"x=-2147483648\"\r\nexit /b %x%", int.MinValue)]
  [TestCase("set /a \"x=2147483647\"\r\nset /a \"x=x+1\"\r\nexit /b %x%", int.MinValue)]
  [TestCase("set /a \"negative=-2\"\r\nif %negative% LSS -1 goto yes\r\nexit /b 0\r\n:yes\r\nexit /b 2147483647", int.MaxValue)]
  [TestCase("set /a \"n=0\"\r\n:again\r\nif %n% LEQ 0 goto done\r\nset /a \"n=n-1\"\r\ngoto again\r\n:done\r\nexit /b %n%", 0)]
  public void SignedBoundariesAndZeroIterationsMatchNative(string body, int expected)
    => Compare("@echo off\r\nsetlocal EnableExtensions DisableDelayedExpansion\r\n" + body, expected);

  [TestCase(8190)]
  [TestCase(8191)]
  public void CommandsAtTheDocumentedLengthLimitMatchNative(int length) {
    const string assignment = "set /a \"n=42\"";
    var command = assignment.Insert(10, new string(' ', length - assignment.Length));
    Compare($"@echo off\r\nsetlocal EnableExtensions DisableDelayedExpansion\r\nset /a \"n=7\"\r\n{command}\r\nexit /b %n%\r\n", 42);
  }

  private static void Compare(string source, int expected) {
    var lifted = BatchFrontend.Import("kernel.bat", source);
    Assert.That(lifted.Diagnostics, Is.Empty);
    var ir = IrInterpreter.Call(lifted.Module!, "Run", [], 1000);
    Assert.That(NativeStatus(source), Is.EqualTo(expected).And.EqualTo(ir));
  }

  internal static int NativeStatus(string source) {
    var directory = Directory.CreateTempSubdirectory();
    try {
      File.WriteAllText(Path.Combine(directory.FullName, "kernel.bat"), source);
      var command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
      TestContext.Out.WriteLine($"{Environment.OSVersion}; cmd.exe {FileVersionInfo.GetVersionInfo(command).FileVersion}");
      var start = new ProcessStartInfo(command) { WorkingDirectory = directory.FullName, UseShellExecute = false };
      foreach (var argument in new[] { "/d", "/e:on", "/v:off", "/c", "kernel.bat" })
        start.ArgumentList.Add(argument);
      using var process = Process.Start(start)!;
      if (!process.WaitForExit(10000)) {
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
        Assert.Fail("Native cmd.exe timed out.");
      }
      return process.ExitCode;
    } finally {
      directory.Delete(recursive: true);
    }
  }

}
