using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Batch;

/// <summary>Compares generated Batch scripts with the original C# on native Windows.</summary>
[TestFixture, Layer("core"), Category("Acceptance"), Category("EndToEnd")]
public sealed class BatchBackendTests {

  private static string Fixture(params string[] parts)
    => Path.Combine([PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", .. parts]);

  [Test, RequiresToolchain("cmd")]
  public void CountdownMatchesTheCSharpKernelOnNativeWindows() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("This test needs native Windows cmd.exe.");

    Verify(File.ReadAllText(Fixture("countdown.cs")), 42);
  }

  [Test, RequiresToolchain("cmd")]
  public void BitwiseFixtureMatchesCSharpOnNativeWindows() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("This test needs native Windows cmd.exe.");

    Verify(File.ReadAllText(Fixture("bitwise.cs")), 3);
  }

  [Test, RequiresToolchain("cmd")]
  public void ConsoleOutputFixtureMatchesStdoutHashAndStatusOnNativeWindows() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("This test needs native Windows cmd.exe.");

    var source = File.ReadAllText(Fixture("console-output.cs"));
    var output = Directory.CreateTempSubdirectory();
    try {
      var script = TranslateToCmd(source, output.FullName);
      var run = NativeRun(output.FullName, Path.GetFileName(script));
      Assert.That(run.ExitCode, Is.EqualTo(42));
      Assert.That(run.StandardError, Is.Empty);
      var sha = Convert.ToHexStringLower(SHA256.HashData(run.StandardOutput));
      Assert.That(sha, Is.EqualTo("7fe52560bc51f640ece2d6c204838c52497bfc2e5d560894a1496ee71e4526ff"));
    } finally {
      output.Delete(recursive: true);
    }
  }

  [Test, RequiresToolchain("cmd")]
  public void ArrayAliasFixtureMatchesCSharpOnNativeWindows() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("This test needs native Windows cmd.exe.");

    var source = File.ReadAllText(Fixture("array-alias.cs"));
    var output = Directory.CreateTempSubdirectory();
    try {
      var script = TranslateToCmd(source, output.FullName);
      var run = NativeRun(output.FullName, Path.GetFileName(script));
      Assert.That(run.ExitCode, Is.EqualTo(72));
      Assert.That(run.StandardOutput, Is.Empty);
      Assert.That(run.StandardError, Is.Empty);
    } finally {
      output.Delete(recursive: true);
    }
  }

  [TestCase(-2147483648)]
  [TestCase(-1)]
  [TestCase(3)]
  [TestCase(2147483647)]
  [RequiresToolchain("cmd")]
  public void ArrayBoundsTrapReportsDistinctMarkerAndStatus97OnNativeWindows(int badIndex) {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("This test needs native Windows cmd.exe.");

    var source = $"public static class Kernel {{ public static int Run() {{ int[] a = [97, 20, 30]; int i = {badIndex}; return a[i]; }} }}";
    var output = Directory.CreateTempSubdirectory();
    try {
      var script = TranslateToCmd(source, output.FullName);
      var run = NativeRun(output.FullName, Path.GetFileName(script));
      Assert.That(run.ExitCode, Is.EqualTo(97));
      Assert.That(run.StandardError, Does.Contain("PLY_BATCH_TRAP=IndexOutOfRange"));
    } finally {
      output.Delete(recursive: true);
    }
  }

  [Test, RequiresToolchain("cmd")]
  public void ValidReturn97LeavesStderrEmptyOnNativeWindows() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("This test needs native Windows cmd.exe.");

    const string source = "public static class Kernel { public static int Run() { int[] a = [97, 20, 30]; int i = 0; return a[i]; } }";
    var output = Directory.CreateTempSubdirectory();
    try {
      var script = TranslateToCmd(source, output.FullName);
      var run = NativeRun(output.FullName, Path.GetFileName(script));
      Assert.That(run.ExitCode, Is.EqualTo(97));
      Assert.That(run.StandardError, Is.Empty);
    } finally {
      output.Delete(recursive: true);
    }
  }

  [TestCase("int x = 2147483647; return unchecked(x + 1);", int.MinValue)]
  [TestCase("int x = -2147483648; return x;", int.MinValue)]
  [TestCase("int x = -2, y = -1; if (x < y) return 2147483647; return 0;", int.MaxValue)]
  [TestCase("int x = -1, y = 0; if (x >= y) return 1; return 0;", 0)]
  [TestCase("int x = 0; while (x < 0) x += 1; return x;", 0)]
  [TestCase("int x = 0; do { x += 1; } while (x < 2); return x;", 2)]
  [TestCase("int sum = 0; for (int i = 0; i < 4; i += 1) { if (i == 0) continue; if (i != 2) sum += i; else break; } return -sum;", -1)]
  [TestCase("int n = 0; do { n += 1; } while (n >= 2); return n;", 1)]
  [TestCase("int n = int.MinValue; if (n > -1) return 42; if (n <= -1) return n; return 0;", int.MinValue)]
  [TestCase("while (true) { break; } return 1;", 1)]
  [TestCase("int x = 1, y = 2; x = (x + 1) + (y - 1); if ((x - 1) * (x + 1) >= (x + 2) * (x - 2)) return -x; return x;", -3)]
  [TestCase("int acc = 0; for (int i = -1; i <= 2; i += 1) { switch (i) { case -1: acc += 10; break; case 0: continue; case 1: acc += 20; break; default: acc += 3; break; } } return acc;", 33)]
  [TestCase("int x = int.MinValue; int c = 0; return (x >>> c) ^ (x >> 31);", int.MaxValue)]
  [TestCase("int x = 1; int c = 32; return (x << c) + (x << -1);", int.MinValue + 1)]
  [RequiresToolchain("cmd")]
  public void SignedStatusAndControlFlowMatchCSharp(string body, int expected)
    => Verify($"public static class Kernel {{ public static int Run() {{ {body} }} }}", expected);

  private static string TranslateToCmd(string source, string outputDirectory) {
    var fixture = Path.Combine(outputDirectory, "Kernel.cs");
    File.WriteAllText(fixture, source);
    using var errors = new StringWriter();
    var status = Program.Run(["translate", fixture, "--from", "csharp", "--to", "batch", "--profile", "cmd",
      "--opt", "none", "--output", outputDirectory], TextWriter.Null, errors);
    Assert.That(status, Is.Zero, errors.ToString());
    return Directory.EnumerateFiles(outputDirectory, "*.bat").Single();
  }

  private static void Verify(string source, int knownResult) {
    var original = GeneratedCSharp.CompileSource(source);
    var expected = (int)original.Method("Run").Invoke(null, null)!;
    var output = Directory.CreateTempSubdirectory();
    try {
      var script = TranslateToCmd(source, output.FullName);
      Assert.That(NativeStatus(output.FullName, Path.GetFileName(script)), Is.EqualTo(expected).And.EqualTo(knownResult));
    } finally {
      output.Delete(recursive: true);
    }
  }

  internal static int NativeStatus(string directory, string file) => NativeRun(directory, file).ExitCode;

  internal static (int ExitCode, byte[] StandardOutput, string StandardError) NativeRun(string directory, string file) {
    var command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    TestContext.Out.WriteLine($"{Environment.OSVersion}; cmd.exe {FileVersionInfo.GetVersionInfo(command).FileVersion}");
    var start = new ProcessStartInfo(command) {
      WorkingDirectory = directory,
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true
    };
    start.Environment.Remove("ERRORLEVEL");
    foreach (var argument in new[] { "/d", "/e:on", "/v:off", "/c", file })
      start.ArgumentList.Add(argument);
    using var shell = Process.Start(start)!;
    using var stdoutBuffer = new MemoryStream();
    var stdoutTask = shell.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer);
    var stderrTask = shell.StandardError.ReadToEndAsync();
    if (!shell.WaitForExit(10000)) {
      shell.Kill(entireProcessTree: true);
      shell.WaitForExit();
      Assert.Fail("Native cmd.exe timed out.");
    }
    stdoutTask.GetAwaiter().GetResult();
    return (shell.ExitCode, stdoutBuffer.ToArray(), stderrTask.GetAwaiter().GetResult());
  }

}
