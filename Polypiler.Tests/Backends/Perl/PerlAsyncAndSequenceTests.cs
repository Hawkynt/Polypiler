using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Perl;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using static Polypiler.Tests.Backends.Perl.PerlIr;

namespace Polypiler.Tests.Backends.Perl;

/// <summary>
/// Emission and live <c>perl</c> execution tests for lazy sequences (<c>yield return</c>,
/// <c>yield break</c>, <c>yield*</c>, <c>foreach</c>, restartability) and async tasks
/// (<c>async</c>/<c>await</c>, <c>Task.FromResult</c>, <c>Task.WhenAll</c>, <c>Task.CompletedTask</c>)
/// across <c>perl/5.6</c> and <c>perl/5</c>.
/// </summary>
[TestFixture]
public sealed class PerlAsyncAndSequenceTests {

  private static string RepoRoot {
    get {
      var directory = AppContext.BaseDirectory;
      while (directory is not null && !File.Exists(Path.Combine(directory, "Polypiler.slnx")))
        directory = Path.GetDirectoryName(directory);
      return directory ?? throw new InvalidOperationException("Could not locate Polypiler.slnx.");
    }
  }

  private static IrModule ImportShowcase(string fileName) {
    var path = Path.Combine(RepoRoot, "docs", "showcase", fileName);
    return PrototypeFixture.Import(fileName, File.ReadAllText(path));
  }

  private static GeneratedFile EmitForProfile(IrModule module, PerlProfile profile) {
    var legalized = PerlEmitter.Legalize(module, () => PrecomputedImplementations.Module, profile, FloatMode.Accurate);
    Assert.That(PerlEmitter.Check(legalized, profile), Is.Empty, profile.Name);
    return PerlEmitter.Emit(legalized, profile)[0];
  }

  private static string RunPerl(GeneratedFile pmFile, string script) {
    var dir = Path.Combine(Path.GetTempPath(), "ply-perl-" + Guid.NewGuid().ToString("N"));
    try {
      var fullPm = Path.Combine(dir, pmFile.Path);
      Directory.CreateDirectory(Path.GetDirectoryName(fullPm)!);
      File.WriteAllText(fullPm, pmFile.Content);
      var runner = Path.Combine(dir, "run.pl");
      File.WriteAllText(runner, script);

      using var process = new Process {
        StartInfo = new ProcessStartInfo {
          FileName = "perl",
          Arguments = $"-I\"{Path.Combine(dir, "lib")}\" -w \"{runner}\"",
          WorkingDirectory = dir,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          CreateNoWindow = true
        }
      };
      process.Start();
      var stdout = process.StandardOutput.ReadToEnd();
      var stderr = process.StandardError.ReadToEnd();
      process.WaitForExit();
      Assert.That(process.ExitCode, Is.Zero, $"perl failed:\n{stderr}\n--- pm ---\n{pmFile.Content}");
      Assert.That(stderr, Is.Empty, $"perl warnings:\n{stderr}\n--- pm ---\n{pmFile.Content}");
      return stdout.Trim();
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, true);
    }
  }

  [Test]
  [Category("ExternalInterop")]
  public void Showcase13_IteratorsAndYield_RunsLiveOnPerl56AndPerl5() {
    var module = ImportShowcase("13-iterators-and-yield.cs");
    foreach (var profile in new[] { PerlProfile.Parse("perl/5.6"), PerlProfile.Parse("perl/5") }) {
      var pm = EmitForProfile(module, profile);
      var pkg = PerlNaming.Package(module.Name);
      var output = RunPerl(pm, $$"""
        use strict;
        use warnings;
        use {{pkg}} qw(sum_evens);
        print sum_evens(10), ",", sum_evens(200), "\n";
        """);
      Assert.That(output, Is.EqualTo("30,2550"), profile.Name);
    }
  }

  [Test]
  [Category("ExternalInterop")]
  public void Showcase16_TasksAndAsync_RunsLiveOnPerl56AndPerl5() {
    var module = ImportShowcase("16-tasks-and-async.cs");
    foreach (var profile in new[] { PerlProfile.Parse("perl/5.6"), PerlProfile.Parse("perl/5") }) {
      var pm = EmitForProfile(module, profile);
      var pkg = PerlNaming.Package(module.Name);
      var output = RunPerl(pm, $$"""
        use strict;
        use warnings;
        use {{pkg}} qw(combine_async);
        my $t = combine_async(3, 4);
        die "expected done task" unless ref($t) eq 'HASH' && $t->{done} && !exists($t->{error});
        print {{pkg}}::_ply_await($t), "\n";
        """);
      Assert.That(output, Is.EqualTo("27"), profile.Name);
    }
  }

  [Test]
  [Category("ExternalInterop")]
  public void Sequences_RestartabilityBreakContinueReturnTryFinallyYieldFromAndArraySeq() {
    var source = """
      using System;
      using System.Collections.Generic;

      public static class SeqSuite {
        static int CleanupLog;

        private static IEnumerable<int> MutatingRange(int n) {
          try {
            while (n > 0) {
              yield return n;
              n -= 1;
            }
          } finally {
            CleanupLog += 1;
          }
        }

        private static IEnumerable<int> NestedWithBreak(int n) {
          try {
            foreach (var v in MutatingRange(n)) {
              if (v == 2)
                yield break;
              yield return v * 10;
            }
          } finally {
            CleanupLog += 100;
          }
        }

        public static int TestRestartAndControlFlow() {
          CleanupLog = 0;
          var seq = MutatingRange(3);
          int total = 0;
          foreach (var x in seq)
            total += x;
          foreach (var x in seq) {
            if (x == 2)
              continue;
            total += x;
            if (x == 1)
              break;
          }
          foreach (var x in NestedWithBreak(4))
            total += x;
          return total * 1000 + CleanupLog;
        }

        public static int EarlyReturnFromForeach(int limit) {
          CleanupLog = 0;
          try {
            foreach (var x in MutatingRange(limit)) {
              try {
                if (x == 2)
                  return x * 100 + CleanupLog;
              } finally {
                CleanupLog += 10;
              }
            }
          } finally {
            CleanupLog += 1000;
          }
          return -1;
        }

        public static int EarlyBreakInsideTry(int limit) {
          CleanupLog = 0;
          int acc = 0;
          foreach (var x in MutatingRange(limit)) {
            try {
              if (x == 3)
                continue;
              if (x == 1)
                break;
              acc += x;
            } finally {
              CleanupLog += 10;
            }
          }
          return acc * 1000 + CleanupLog;
        }
      }
      """;
    var module = PrototypeFixture.Import("SeqSuite.cs", source);
    var pm = EmitForProfile(module, PerlProfile.Default);
    var output = RunPerl(pm, """
      use strict;
      use warnings;
      use SeqSuite qw(test_restart_and_control_flow early_return_from_foreach early_break_inside_try);
      print test_restart_and_control_flow(), "|", early_return_from_foreach(4), "|", early_break_inside_try(4), "\n";
      """);
    // TestRestartAndControlFlow:
    //   1st pass MutatingRange(3): 3+2+1 = 6, CleanupLog += 1
    //   2nd pass MutatingRange(3): 3 + (skip 2) + 1 (break) = 4, CleanupLog += 1
    //   NestedWithBreak(4): yields 40, 30, then hits v==2 -> yield break (closes inner +1, outer +100) => 70, CleanupLog = 103
    //   total = 6 + 4 + 70 = 80 => 80 * 1000 + 103 = 80103
    // EarlyReturnFromForeach(4):
    //   x=4: inner finally (+10); x=3: inner finally (+10); x=2: return value computed as 2*100 + 20 = 220, then inner finally (+10), iterator finally (+1), outer finally (+1000) -> returns 220!
    // EarlyBreakInsideTry(4):
    //   x=4: acc=4, +10; x=3: continue, +10; x=2: acc=6, +10; x=1: break, +10, iter finally +1 -> CleanupLog=41, acc=6 -> 6041
    Assert.That(output, Is.EqualTo("80103|220|6041"));
  }

  [Test]
  [Category("ExternalInterop")]
  public void YieldFromAndArraySequence_WorkForStandardAndStringByteStorage() {
    var inner = Function("Inner", false, IrType.Sequence(IrType.I32, true), [],
      new IrYield(I(10)), new IrYield(I(20)), new IrYield(I(30)));
    var deleg = Function("Deleg", false, IrType.Sequence(IrType.I32, true), [],
      new IrYieldFrom(new IrCall("Inner", IrType.Sequence(IrType.I32, true), [])),
      new IrYield(I(40)));
    var arr = L("arr", IrType.Array(IrType.U8));
    var elem = L("elem", IrType.I32);
    var bElem = L("bElem", IrType.U8);
    var sum = L("sum", IrType.I32);
    var run = Function("Run", true, IrType.I32, [arr],
      new IrDeclare(sum, I(0)),
      new IrForEachSequence(elem, new IrCall("Deleg", IrType.Sequence(IrType.I32, true), []),
        Block(
          new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(elem))),
          new IrIf(Bin(IrBinaryOp.Equal, R(elem), I(20)), Block(new IrBreak()), null))),
      new IrForEachSequence(bElem, new IrArraySequence(R(arr)),
        Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrConvert(IrType.I32, R(bElem)))))),
      new IrReturn(R(sum)));

    var module = new IrModule("YieldFromMod", [inner, deleg, run]);

    // Standard array storage
    var pmArray = EmitForProfile(module, PerlProfile.Default);
    var outArray = RunPerl(pmArray, """
      use strict;
      use warnings;
      use YieldFromMod qw(run);
      print run([1, 2, 3]), "\n";
      """);
    Assert.That(outArray, Is.EqualTo("36"));

    // String byte storage for u8[] IrArraySequence
    var stringProfile = PerlProfile.Default with { ByteStorage = PerlByteStorage.String };
    var pmStr = EmitForProfile(module, stringProfile);
    var outStr = RunPerl(pmStr, """
      use strict;
      use warnings;
      use YieldFromMod qw(run);
      my $buf = pack('C*', 4, 5, 6);
      print run(\$buf), "\n";
      """);
    Assert.That(outStr, Is.EqualTo("45"));
  }

  [Test]
  [Category("ExternalInterop")]
  public void Async_CoversCompletedWhenAllVoidFaultOrderTryCatchAndFloatAndClasses() {
    var source = """
      using System;
      using System.Collections.Generic;
      using System.Threading.Tasks;

      public sealed class BoomException : Exception {
        public BoomException(string message) : base(message) { }
      }

      public static class AsyncSuite {
        private static async Task DoneAsync() {
          await Task.CompletedTask;
        }

        private static async Task<double> ScaleAsync(double x, int bias)
          => await Task.FromResult(x + bias);

        private static IEnumerable<int> Steps(int bias) {
          yield return bias;
          yield return bias + 1;
        }

        private static async Task<int> FailWith(int code) {
          if (code < 0)
            throw new InvalidOperationException("boom");
          return await Task.FromResult(code);
        }

        public static async Task<int> RunAsync(int a, int b) {
          await Task.WhenAll(DoneAsync(), DoneAsync());
          int[] empty = await Task.WhenAll<int>();
          int acc = empty.Length;
          foreach (var s in Steps(2))
            acc += s;
          double scaled = await ScaleAsync(1.5, 2);
          if (scaled > 3.0)
            acc += 10;
          try {
            int[] res = await Task.WhenAll(FailWith(a), FailWith(b));
            return acc + res[0] + res[1];
          } catch (InvalidOperationException) {
            return -acc;
          } finally {
            acc += 1;
          }
        }
      }
      """;
    var module = PrototypeFixture.Import("AsyncSuite.cs", source);
    var pm = EmitForProfile(module, PerlProfile.Default);
    // Float async function must NOT emit `use integer;` inside its sub.
    Assert.That(pm.Content, Does.Contain("sub _scale_async {\n    my ($x_, $bias) = @_;"));
    var output = RunPerl(pm, """
      use strict;
      use warnings;
      use AsyncSuite qw(run_async);
      my $ok = AsyncSuite::_ply_await(run_async(3, 4));
      my $err = AsyncSuite::_ply_await(run_async(-1, -2));
      print "$ok,$err\n";
      """);
    // acc: 0 + 2 + 3 + 10 = 15. ok: 15 + 3 + 4 = 22. err: -15.
    Assert.That(output, Is.EqualTo("22,-15"));
  }

  [Test]
  public void AsyncAndIterator_CoverLambdaIsolationAndNestedTries() {
    var fnType = IrType.Function(IrType.I32, IrType.I32);
    var fn = Function("Outer", true, IrType.Async(IrType.I32), [],
      new IrDeclare(L("f", fnType),
        new IrLambda(fnType, [], [L("x", IrType.I32)], [], Block(new IrReturn(I(1))))),
      new IrTry(Block(
        new IrTry(Block(new IrReturn(I(7))), [], Block())
      ), [], Block()));
    var src = Emit(new IrModule("AsyncLambda", [fn]));
    Assert.That(src, Does.Contain("eval {"));
    Assert.That(src, Does.Contain("done => 1"));

    // Void async returning early from inside IrForEachSequence + nested try inside loop
    var elem = L("e", IrType.I32);
    var voidAsync = Function("VoidAsync", true, IrType.Async(IrType.Void), [],
      new IrForEachSequence(elem, new IrArraySequence(new IrNewArray(IrType.Array(IrType.I32), I(2))),
        Block(
          new IrTry(Block(
            new IrTry(Block(
              new IrIf(Bin(IrBinaryOp.Equal, R(elem), I(0)), Block(new IrBreak()), null),
              new IrIf(Bin(IrBinaryOp.Equal, R(elem), I(1)), Block(new IrContinue()), null),
              new IrReturn(null)
            ), [], Block())
          ), [], Block()))));
    var voidSrc = Emit(new IrModule("VoidAsyncMod", [voidAsync]));
    Assert.That(voidSrc, Does.Contain("return 0;"));
    Assert.That(voidSrc, Does.Contain("return 1;"));
  }

}
