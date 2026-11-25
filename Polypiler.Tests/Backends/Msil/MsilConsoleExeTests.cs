using NUnit.Framework;
using Polypiler.Core.Backends.Msil;
using Polypiler.Core.Frontends.Perl;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Msil.MsilTestHarness;

namespace Polypiler.Tests.Backends.Msil;

/// <summary>End-to-end tests executing emitted <see cref="MsilOutputKind.ConsoleExe"/> assemblies directly via <c>dotnet</c>.</summary>
[TestFixture]
public sealed class MsilConsoleExeTests {

  [Test]
  public void ConsoleExe_MainReturningInt32_ExitsWithReturnCode() {
    var mod = new IrModule("AppInt", [Function("Main", IrType.I32, [], new IrReturn(I(42)))]);
    var files = MsilEmitter.Emit(mod, MsilProfile.Net10 with { OutputKind = MsilOutputKind.ConsoleExe });
    Assert.That(files, Has.Count.EqualTo(2));
    var (exitCode, _, stderr) = RunConsoleExe(files);
    Assert.That(exitCode, Is.EqualTo(42), stderr);
  }

  [Test]
  public void ConsoleExe_MainWithArgs_ReceivesCommandLineArguments() {
    var args = L("args", IrType.Array(IrType.String));
    var mod = new IrModule("AppArgs", [Function("Main", IrType.I32, [args], new IrReturn(new IrArrayLength(R(args))))]);
    var files = MsilEmitter.Emit(mod, MsilProfile.Net10 with { OutputKind = MsilOutputKind.ConsoleExe });
    var (exitCode, _, stderr) = RunConsoleExe(files, "one", "two", "three");
    Assert.That(exitCode, Is.EqualTo(3), stderr);
  }

  [Test]
  public void ConsoleExe_VoidMain_ExitsZero() {
    var mod = new IrModule("AppVoid", [Function("Main", IrType.Void, [], new IrReturn(null))]);
    var files = MsilEmitter.Emit(mod, MsilProfile.Net10 with { OutputKind = MsilOutputKind.ConsoleExe });
    var (exitCode, _, stderr) = RunConsoleExe(files);
    Assert.That(exitCode, Is.Zero, stderr);
  }

  [Test]
  public void ConsoleExe_LowercaseMainReturningInt64_ExitsWithTruncatedCode() {
    var mod = new IrModule("AppLong", [Function("main", IrType.I64, [], new IrReturn(I64(7)))]);
    var files = MsilEmitter.Emit(mod, MsilProfile.Net10 with { OutputKind = MsilOutputKind.ConsoleExe });
    var (exitCode, _, stderr) = RunConsoleExe(files);
    Assert.That(exitCode, Is.EqualTo(7), stderr);
  }

  [Test]
  public void ConsoleExe_WithoutMain_SynthesizesRunnableEntryPoint() {
    var x = L("x", IrType.I32);
    var mod = new IrModule("LibAsExe", [Function("Double", IrType.I32, [x], new IrReturn(Bin(IrBinaryOp.Add, R(x), R(x))))]);
    var files = MsilEmitter.Emit(mod, MsilProfile.Net10 with { OutputKind = MsilOutputKind.ConsoleExe });
    var (exitCode, _, stderr) = RunConsoleExe(files);
    Assert.That(exitCode, Is.Zero, stderr);
  }

  [Test]
  public void PerlToMsil_ConsoleExe_RunsDirectlyUnderDotNet() {
    const string perl = """
      use strict;
      use warnings;

      sub sum_to {
        my ($n) = @_;
        my $acc = 0;
        my $i = 1;
        while ($i <= $n) {
          $acc = $acc + $i;
          $i = $i + 1;
        }
        return $acc;
      }

      sub main {
        my $acc = 0;
        my $i = 1;
        while ($i <= 6) {
          $acc = $acc + $i;
          $i = $i + 1;
        }
        return $acc;
      }

      1;
      """;
    var options = new PerlImportOptions {
      Contracts = """
        main::sum_to.n: i32
        main::sum_to.return: i32
        main::main.return: i32
        """
    };

    var imported = PerlFrontend.Import("calc.pl", perl, options);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(imported.Module, Is.Not.Null);

    var legalized = Legalize(imported.Module!);
    var profile = MsilProfile.Net10 with { OutputKind = MsilOutputKind.ConsoleExe };
    var files = MsilEmitter.Emit(legalized, profile);
    Assert.That(files, Has.Count.EqualTo(2));

    var (exitCode, _, stderr) = RunConsoleExe(files);
    Assert.That(exitCode, Is.EqualTo(21), stderr);
    Assert.That(Invoke(legalized, "sum_to", 10), Is.EqualTo(55));
  }

}
