using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Frontends.Batch;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Batch;

[TestFixture]
public sealed class BatchFrontendTests {

  [Test]
  public void CountdownLiftsAndRunsAsCSharp() {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "countdown.bat");
    using var errors = new StringWriter();
    var output = Directory.CreateTempSubdirectory();
    try {
      var status = Program.Run(["translate", path, "--from", "batch", "--to", "csharp", "--opt", "none",
        "--output", output.FullName], TextWriter.Null, errors);
      Assert.That(status, Is.Zero, errors.ToString());
      var generated = File.ReadAllText(Path.Combine(output.FullName, "countdown.cs"));
      var compiled = GeneratedCSharp.Compile(generated, checkOverflow: true);
      Assert.That(compiled.Method("Run").Invoke(null, null), Is.EqualTo(42));
    } finally {
      output.Delete(recursive: true);
    }
  }

  private const string _prologue = "setlocal EnableExtensions DisableDelayedExpansion\n";

  [TestCase("set /a \"n=0\"\nif %N% EQU 0 goto yes\nset /a \"y=1\"\ngoto end\n:YES\nset /a \"Y=42\"\n:END\nexit /b %y%", 42)]
  [TestCase("set /a \"x=2147483647\"\nset /a \"X=x+1\"\nexit /b %x%", int.MinValue)]
  [TestCase("set /a \"n=6\"\nset /a \"x=-(2+3)*n+4\"\nexit /b %x%", -26)]
  [TestCase("goto finish\nset /a \"x=y+1\"\n:finish\nexit /b 2147483647", int.MaxValue)]
  [TestCase("set /a \"x=-2147483647\"\nexit /b %x%", int.MinValue + 1)]
  [TestCase("set /a \"x=-2147483647-1\"\nexit /b %x%", int.MinValue)]
  [TestCase("exit /b -2147483648", int.MinValue)]
  [TestCase("exit /b 0", 0)]
  public void InterpretsClosedScalarScripts(string body, int expected) {
    var lifted = BatchFrontend.Import("fixture.bat", _prologue + body);
    Assert.That(lifted.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(lifted.Module!, "Run", []), Is.EqualTo(expected));
  }

  [TestCase(8190, true)]
  [TestCase(8191, true)]
  [TestCase(8192, false)]
  public void BoundsPhysicalCommandLength(int length, bool accepted) {
    const string assignment = "set /a \"n=42\"";
    var command = assignment.Insert(10, new string(' ', length - assignment.Length));
    var lifted = BatchFrontend.Import("fixture.bat", _prologue + command + "\nexit /b %n%");

    if (accepted) {
      Assert.That(lifted.Diagnostics, Is.Empty);
      Assert.That(IrInterpreter.Call(lifted.Module!, "Run", []), Is.EqualTo(42));
    } else {
      Assert.That(lifted.Module, Is.Null);
      Assert.That(lifted.Diagnostics.Select(problem => (problem.Code, problem.Line)), Is.EqualTo(new[] { ("PLY2802", 2) }));
    }
  }

  [TestCase("if %n% LSS 0 goto done", 8183, 42, true)]
  [TestCase("if %n% LSS 0 goto done", 8184, 42, false)]
  [TestCase("if %n% EQU %m% goto done", 8175, 42, true)]
  [TestCase("if %n% EQU %m% goto done", 8176, 42, false)]
  [TestCase("exit /b %n%", 8183, int.MinValue, true)]
  [TestCase("exit /b %n%", 8184, int.MinValue, false)]
  public void BoundsCommandLengthAfterExpansion(string command, int length, int expected, bool accepted) {
    var padded = command.Insert(command.IndexOf(' '), new string(' ', length - command.Length));
    var body = $"set /a \"n=-2147483647-1\"\nset /a \"m=n\"\n{padded}\nexit /b 0\n:done\nexit /b 42";
    var lifted = BatchFrontend.Import("fixture.bat", _prologue + body);

    if (accepted) {
      Assert.That(lifted.Diagnostics, Is.Empty);
      Assert.That(IrInterpreter.Call(lifted.Module!, "Run", []), Is.EqualTo(expected));
    } else {
      Assert.That(lifted.Module, Is.Null);
      Assert.That(lifted.Diagnostics.Select(problem => (problem.Code, problem.Line)), Is.EqualTo(new[] { ("PLY2802", 4) }));
    }
  }

  [Test, Category("Performance")]
  public void DoublingALongReducibleLoopDoesNotQuadrupleAllocations() {
    _ = BatchFrontend.Import("warmup.bat", _prologue + "exit /b 0");
    var smaller = AllocatedPerImport(1024);
    var larger = AllocatedPerImport(2048);

    // A single-local loop should grow roughly with its instruction count.
    Assert.That(larger, Is.LessThan(3 * smaller));
  }

  private static long AllocatedPerImport(int increments) {
    var source = _prologue + "set /a \"x=0\"\n:head\n"
      + string.Concat(Enumerable.Repeat("set /a \"x=x+1\"\n", increments))
      + "if %x% LSS 2048 goto head\nexit /b %x%";
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var clock = Stopwatch.StartNew();
    var runs = 0;
    do {
      var lifted = BatchFrontend.Import("fixture.bat", source);
      Assert.That(lifted.Diagnostics, Is.Empty);
      ++runs;
    } while (clock.Elapsed < TimeSpan.FromMilliseconds(150));

    return (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / runs;
  }

  [TestCase("call :unknown\nexit /b 0", "PLY2802", 2)]
  [TestCase("set /a \"n=n+1\"\nexit /b %n%", "PLY2802", 2)]
  [TestCase("set /a \"n=0\"\nif %n% EQU 0 goto end\nset /a \"x=2\"\n:end\nexit /b %x%", "PLY2802", 6)]
  [TestCase(":Done\n:done\nexit /b 0", "PLY2800", 3)]
  [TestCase("goto absent\nexit /b 0", "PLY2800", 2)]
  [TestCase("set /a \"x=1\"", "PLY2800", 2)]
  [TestCase("set /a \"x=08\"\nexit /b %x%", "PLY2800", 2)]
  [TestCase("set /a \"x=(1\"\nexit /b %x%", "PLY2800", 2)]
  [TestCase("set /a \"x=2147483648\"\nexit /b %x%", "PLY2800", 2)]
  [TestCase("set /a \"x=-2147483648\"\nexit /b %x%", "PLY2800", 2)]
  [TestCase("set /a \"x=-(2147483648)\"\nexit /b %x%", "PLY2800", 2)]
  [TestCase("exit /b !x!", "PLY2802", 2)]
  [TestCase("exit /b 0 & echo unexpected", "PLY2802", 2)]
  [TestCase("exit /b 0 é", "PLY2800", 2)]
  [TestCase("set /a \"x=0\"\nif %x% EQU 0 goto a\ngoto b\n:a\ngoto b\n:b\nif %x% NEQ 0 goto a\nexit /b 0", "PLY2802", 6)]
  public void RejectsMalformedOrOutOfProfileInput(string body, string code, int line) {
    var lifted = BatchFrontend.Import("fixture.bat", _prologue + body);
    Assert.That(lifted.Module, Is.Null);
    Assert.That(lifted.Diagnostics, Has.Count.EqualTo(1));
    Assert.That((lifted.Diagnostics[0].Code, lifted.Diagnostics[0].Line), Is.EqualTo((code, line)));
  }


}
