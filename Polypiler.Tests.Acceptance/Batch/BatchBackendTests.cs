using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Batch;

/// <summary>Compares generated Batch scripts with the original C# on native Windows.</summary>
[TestFixture, Layer("core"), Category("Acceptance"), Category("EndToEnd")]
public sealed class BatchBackendTests {

  [Test, RequiresToolchain("cmd")]
  public void CountdownMatchesTheCSharpKernelOnNativeWindows() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("This test needs native Windows cmd.exe.");

    var fixture = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "countdown.cs");
    Verify(File.ReadAllText(fixture), 42);
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
  [RequiresToolchain("cmd")]
  public void SignedStatusAndControlFlowMatchCSharp(string body, int expected)
    => Verify($"public static class Kernel {{ public static int Run() {{ {body} }} }}", expected);

  private static void Verify(string source, int knownResult) {
    var original = GeneratedCSharp.CompileSource(source);
    var expected = (int)original.Method("Run").Invoke(null, null)!;
    var output = Directory.CreateTempSubdirectory();
    try {
      var fixture = Path.Combine(output.FullName, "Kernel.cs");
      File.WriteAllText(fixture, source);
      using var errors = new StringWriter();
      var status = Program.Run(["translate", fixture, "--from", "csharp", "--to", "batch", "--profile", "cmd",
        "--opt", "none", "--output", output.FullName], TextWriter.Null, errors);
      Assert.That(status, Is.Zero, errors.ToString());
      var script = Directory.EnumerateFiles(output.FullName, "*.bat").Single();
      Assert.That(NativeStatus(output.FullName, Path.GetFileName(script)), Is.EqualTo(expected).And.EqualTo(knownResult));
    } finally {
      output.Delete(recursive: true);
    }
  }

  internal static int NativeStatus(string directory, string file) {
    var command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    TestContext.Out.WriteLine($"{Environment.OSVersion}; cmd.exe {FileVersionInfo.GetVersionInfo(command).FileVersion}");
    var start = new ProcessStartInfo(command) { WorkingDirectory = directory, UseShellExecute = false };
    foreach (var argument in new[] { "/d", "/e:on", "/v:off", "/c", file })
      start.ArgumentList.Add(argument);
    using var shell = Process.Start(start)!;
    if (!shell.WaitForExit(10000)) {
      shell.Kill(entireProcessTree: true);
      shell.WaitForExit();
      Assert.Fail("Native cmd.exe timed out.");
    }
    return shell.ExitCode;
  }

}
