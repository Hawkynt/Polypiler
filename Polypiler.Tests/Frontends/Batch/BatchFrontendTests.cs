using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Batch;
using Polypiler.Core.Frontends.Batch;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Batch;

[TestFixture]
public sealed class BatchFrontendTests {

  private const string _prologue = "setlocal EnableExtensions DisableDelayedExpansion\n";

  [Test]
  public void CountdownLiftsAndRunsAsCSharp() {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "countdown.bat");
    Assert.That(LiftAndRunCSharp(path), Is.EqualTo(42));
  }

  [Test]
  public void ForNumericFixtureLiftsAndRunsAsCSharp() {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "for-l.bat");
    Assert.That(LiftAndRunCSharp(path), Is.EqualTo(9));
  }

  [Test]
  public void ScopedReturnFixtureLiftsAndRunsAsCSharp() {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "scoped-return.bat");
    Assert.That(LiftAndRunCSharp(path), Is.EqualTo(42));
  }

  [TestCase("delayed-expansion.bat", 140)]
  [TestCase("delayed-expansion-zero.bat", 7)]
  public void DelayedExpansionFixturesLiftAndRunAsCSharp(string fixture, int expected) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", fixture);
    var lifted = BatchFrontend.Import(path, File.ReadAllText(path));
    Assert.That(lifted.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(lifted.Module!, "Run", []), Is.EqualTo(expected));
    Assert.That(LiftAndRunCSharp(path), Is.EqualTo(expected));
  }

  private static int LiftAndRunCSharp(string path) {
    using var errors = new StringWriter();
    var output = Directory.CreateTempSubdirectory();
    try {
      var status = Program.Run(["translate", path, "--from", "batch", "--to", "csharp", "--opt", "none",
        "--output", output.FullName], TextWriter.Null, errors);
      Assert.That(status, Is.Zero, errors.ToString());
      var generated = File.ReadAllText(Directory.EnumerateFiles(output.FullName, "*.cs").Single());
      var compiled = GeneratedCSharp.Compile(generated, checkOverflow: true);
      return (int)compiled.Method("Run").Invoke(null, null)!;
    } finally {
      output.Delete(recursive: true);
    }
  }

  [TestCase("set /a \"n=0\"\nif %N% EQU 0 goto yes\nset /a \"y=1\"\ngoto end\n:YES\nset /a \"Y=42\"\n:END\nexit /b %y%", 42)]
  [TestCase("set /a \"x=2147483647\"\nset /a \"X=x+1\"\nexit /b %x%", int.MinValue)]
  [TestCase("set /a \"n=6\"\nset /a \"x=-(2+3)*n+4\"\nexit /b %x%", -26)]
  [TestCase("goto finish\nset /a \"x=y+1\"\n:finish\nexit /b 2147483647", int.MaxValue)]
  [TestCase("set /a \"x=-2147483647\"\nexit /b %x%", int.MinValue + 1)]
  [TestCase("set /a \"x=-2147483647-1\"\nexit /b %x%", int.MinValue)]
  [TestCase("exit /b -2147483648", int.MinValue)]
  [TestCase("exit /b 0", 0)]
  [TestCase("set /a \"number=-2147483647-1\"\nset /a \"sign=(number>>31)&1\"\nexit /b %sign%", 1)]
  [TestCase("set /a \"x=1+7>>2&1\"\nexit /b %x%", 0)]
  [TestCase("set /a \"x=-2147483647-1\"\nset /a \"y=(x>>(1+30))&1\"\nexit /b %y%", 1)]
  [TestCase("set /a \"x=-2147483647-1\"\nset /a \"y=x>>32\"\nexit /b %y%", int.MinValue)]
  [TestCase("set /a \"x=-2147483647-1\"\nset /a \"y=x>>-1\"\nexit /b %y%", -1)]
  public void InterpretsClosedScalarScripts(string body, int expected) {
    var lifted = BatchFrontend.Import("fixture.bat", _prologue + body);
    Assert.That(lifted.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(lifted.Module!, "Run", []), Is.EqualTo(expected));
  }

  [TestCase("set /a \"total=0\"\nfor /l %%I in (5,-2,1) do set /a \"total=total+%%I\"\nexit /b %total%", 9)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (1,2,5) do set /a \"total=total+%%I\"\nexit /b %total%", 9)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (1,3,5) do set /a \"total=total+%%I\"\nexit /b %total%", 5)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (3,1,1) do set /a \"total=total+%%I\"\nexit /b %total%", 0)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (1,-1,3) do set /a \"total=total+%%I\"\nexit /b %total%", 0)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (4,1,4) do set /a \"total=total+%%I\"\nexit /b %total%", 4)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (2147483646,1,2147483646) do set /a \"total=total+1\"\nexit /b %total%", 1)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (-2147483647,-1,-2147483647) do set /a \"total=total+1\"\nexit /b %total%", 1)]
  [TestCase("set /a \"i=10\"\nfor /l %%I in (1,1,3) do set /a \"i=i+%%I\"\nexit /b %i%", 16)]
  [TestCase("set /a \"total=2147483647\"\nfor /l %%I in (1,1,1) do set /a \"total=total+%%I\"\nexit /b %total%", int.MinValue)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (1,1,3) do set /a \"total=total-%%I\"\nexit /b %total%", -6)]
  public void LiftsNumericForLoops(string body, int expected) {
    var lifted = BatchFrontend.Import("for-l.bat", _prologue + body);
    Assert.That(lifted.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(lifted.Module!, "Run", []), Is.EqualTo(expected));
  }

  [TestCase("set /a \"total=0\"\nfor /l %%I in (1,0,3) do set /a \"total=total+%%I\"\nexit /b %total%", "PLY2802", 3)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (2147483647,1,2147483647) do set /a \"total=total+1\"\nexit /b %total%", "PLY2802", 3)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (-2147483648,1,-2147483648) do set /a \"total=total+%%I\"\nexit /b %total%", "PLY2802", 3)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (-2147483648,-1,-2147483648) do set /a \"total=total+1\"\nexit /b %total%", "PLY2802", 3)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (1,2147483647,2) do set /a \"total=total+1\"\nexit /b %total%", "PLY2802", 3)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (1,1,3) do set /a \"total=total+%%i\"\nexit /b %total%", "PLY2802", 3)]
  [TestCase("for /l %%I in (1,1,3) do set /a \"x=%%I\"\nexit /b %x%", "PLY2802", 3)]
  [TestCase("set /a \"s=1\"\nfor /l %%I in (%s%,1,3) do set /a \"s=s+%%I\"\nexit /b %s%", "PLY2802", 3)]
  [TestCase("set /a \"total=0\"\nfor /l %%I in (08,1,9) do set /a \"total=total+%%I\"\nexit /b %total%", "PLY2800", 3)]
  [TestCase("for /f %%I in (a.txt) do set /a \"x=1\"\nexit /b 0", "PLY2802", 2)]
  public void RejectsInvalidOrUnsupportedForLoops(string body, string code, int line) {
    var lifted = BatchFrontend.Import("for-l.bat", _prologue + body);
    Assert.That(lifted.Module, Is.Null);
    Assert.That(lifted.Diagnostics, Has.Count.EqualTo(1));
    Assert.That((lifted.Diagnostics[0].Code, lifted.Diagnostics[0].Line), Is.EqualTo((code, line)));
  }

  [TestCase("call :seed\ngoto :eof\n:seed\nexit /b 7", 7)]
  [TestCase("set \"x=1\"\ncall :seed && set \"x=99\"\nexit /b %x%\n:seed\nexit /b 7", 1)]
  [TestCase("set \"x=1\"\ncall :zero && set \"x=99\"\nexit /b %x%\n:zero\nexit /b 0", 99)]
  [TestCase("set \"x=5\"\ncall :nested\nexit /b %x%\n:nested\nsetlocal\nset \"x=10\"\nsetlocal\nset \"x=20\"\nendlocal\nendlocal & set \"x=%x%\"\ngoto :eof", 10)]
  [TestCase("set \"x=1\"\ncall :pass 10 20\nexit /b %x%\n:pass\nset \"a=%1\"\nset \"b=%2\"\nset /a \"x=a+b\"\nexit /b", 30)]
  [TestCase("set \"x=0\"\nif %x% EQU 0 goto :eof\nexit /b 9", 0)]
  [TestCase("set \"x=1\"\ncall :cond 0\nexit /b %x%\n:cond\nset \"x=5\"\nif %1 EQU 0 goto :eof\nset \"x=9\"\ngoto :eof", 5)]
  [TestCase("call :implicit\nexit /b %errorlevel%\n:implicit\nexit /b -5", -5)]
  [TestCase("set \"x=1\"\ncall :falloff\nexit /b %x%\n:falloff\nset \"x=8\"", 8)]
  public void LiftsLabelCallsAndScopedReturns(string body, int expected) {
    var lifted = BatchFrontend.Import("scoped.bat", _prologue + body);
    Assert.That(lifted.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(lifted.Module!, "Run", []), Is.EqualTo(expected));
  }

  [TestCase("call :absent\nexit /b 0", "PLY2800", 2)]
  [TestCase("call :a\nexit /b 0\n:a\ncall :b\ngoto :eof\n:b\ncall :a\ngoto :eof", "PLY2802", 8)]
  [TestCase("endlocal\nexit /b 0", "PLY2802", 2)]
  [TestCase("setlocal\ncall :bad\nendlocal\nexit /b 0\n:bad\nendlocal\ngoto :eof", "PLY2802", 7)]
  [TestCase("set \"x=1\"\nif %x% EQU 1 goto push\ngoto merge\n:push\nsetlocal\n:merge\nexit /b 0", "PLY2802", 8)]
  [TestCase("setlocal\nset \"onlyInner=5\"\nendlocal\nexit /b %onlyInner%", "PLY2802", 5)]
  [TestCase("call :f 1\nexit /b 0\n:f\nexit /b %2", "PLY2802", 5)]
  [TestCase("call :f 1 2 3 4 5 6 7 8 9 10\nexit /b 0\n:f\ngoto :eof", "PLY2802", 2)]
  [TestCase("set \"x=1\"\ncall :f %x%\nexit /b 0\n:f\ngoto :eof", "PLY2802", 3)]
  [TestCase("call :f abc\nexit /b 0\n:f\ngoto :eof", "PLY2800", 2)]
  [TestCase("set \"x=1\" &\nexit /b 0", "PLY2800", 2)]
  [TestCase("& set \"x=1\"\nexit /b 0", "PLY2800", 2)]
  [TestCase("set \"errorlevel=5\"\nexit /b 0", "PLY2802", 1)]
  [TestCase("set /a \"x=1&&2\"\nexit /b %x%", "PLY2802", 2)]
  [TestCase("set /a \"x=%%I\"\nexit /b %x%", "PLY2802", 2)]
  [TestCase("set /a \"x=%a%\"\nexit /b %x%", "PLY2802", 2)]
  [TestCase("set /a \"x=1+\"\nexit /b %x%", "PLY2800", 2)]
  [TestCase("set /a \"x=1&\"\nexit /b %x%", "PLY2800", 2)]
  [TestCase("set /a \"x=@\"\nexit /b %x%", "PLY2802", 2)]
  [TestCase("exit /b %0", "PLY2802", 2)]
  public void RejectsInvalidCallsScopesAndOperators(string body, string code, int line) {
    var lifted = BatchFrontend.Import("invalid.bat", _prologue + body);
    Assert.That(lifted.Module, Is.Null);
    Assert.That(lifted.Diagnostics, Has.Count.EqualTo(1));
    Assert.That((lifted.Diagnostics[0].Code, lifted.Diagnostics[0].Line), Is.EqualTo((code, line)));
  }

  [Test]
  public void CoversParserBoundariesBackwardCallsAndComparisons() {
    Assert.That(() => BatchFrontend.Import("a.bat", "", (BatchDialect)99), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(BatchFrontend.Import("no-prologue.bat", "exit /b 0").Diagnostics, Has.One.Matches<Diagnostic>(d => d.Code == "PLY2800"));
    Assert.That(BatchFrontend.Import("empty.bat", _prologue).Diagnostics, Has.One.Matches<Diagnostic>(d => d.Code == "PLY2800"));

    var deep = new string('(', 128) + "1" + new string(')', 128);
    Assert.That(BatchFrontend.Import("deep.bat", _prologue + $"set /a \"x={deep}\"\nexit /b %x%").Diagnostics,
      Has.One.Matches<Diagnostic>(d => d.Code == "PLY2802"));

    // Backward jump from subroutine + GTR/GEQ comparisons + unreachable island between blocks
    const string script = """
      setlocal EnableExtensions DisableDelayedExpansion
      set /a "x=5"
      if %x% GTR 4 goto next
      exit /b 1
      :dead
      set /a "dead=1"
      exit /b 2
      :next
      if %x% GEQ 5 goto run
      exit /b 3
      :earlier
      set /a "x=42"
      goto :eof
      :run
      call :sub
      exit /b %x%
      :sub
      goto earlier
      """;
    var lifted = BatchFrontend.Import("back.bat", script);
    Assert.That(lifted.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(lifted.Module!, "Run", []), Is.EqualTo(42));
  }

  [Test]
  public void RoundTripsCSharpCountdownThroughBatchAndBack() {
    var fixture = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "countdown.cs");
    var csharpModule = CSharpFrontend.Import(fixture, File.ReadAllText(fixture)).Module!;
    var batchSource = BatchEmitter.Emit(csharpModule).Single().Content;
    var lifted = BatchFrontend.Import("kernel.bat", batchSource);
    Assert.That(lifted.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(lifted.Module!, "Run", [], 10000), Is.EqualTo(42));
  }

  [Test]
  public void RoundTripsBatchCountdownThroughIrAndBack() {
    var fixture = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "countdown.bat");
    var lifted = BatchFrontend.Import(fixture, File.ReadAllText(fixture));
    Assert.That(lifted.Diagnostics, Is.Empty);
    var reemitted = BatchEmitter.Emit(lifted.Module!).Single().Content;
    var relifted = BatchFrontend.Import("kernel.bat", reemitted);
    Assert.That(relifted.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(relifted.Module!, "Run", [], 10000), Is.EqualTo(42));
  }

  [Test]
  public void RoundTripsSignedLoopAndSwitchKernels() {
    const string signedLoop = """
      public static class Kernel {
        public static int Run() {
          int product = 1;
          for (int i = -3; i <= 2; i += 1) {
            if (i == 0 || i == 1)
              continue;
            product *= i;
          }
          return product;
        }
      }
      """;
    var ir = CSharpFrontend.Import("SignedLoop.cs", signedLoop).Module!;
    var expected = (int)IrInterpreter.Call(ir, "Run", [], 10000)!;
    var batch = BatchEmitter.Emit(ir).Single().Content;
    var relifted = BatchFrontend.Import("kernel.bat", batch);
    Assert.That(relifted.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(relifted.Module!, "Run", [], 10000), Is.EqualTo(expected));
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

  [TestCase("call %target%\nexit /b 0", "PLY2802", 2)]
  [TestCase("call :unknown\nexit /b 0", "PLY2800", 2)]
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
