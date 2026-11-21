using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Backends.Perl;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.Perl;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Acceptance.Perl;

/// <summary>
/// End-to-end acceptance tests for the Perl 5 frontend and backend executed against the host Perl 5
/// interpreter under <c>use strict; use warnings FATAL =&gt; 'all';</c>.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
public sealed class PerlAcceptanceTests {

  private string _dir = "";

  [SetUp]
  public void CreateWorkingDirectory() => this._dir = Directory.CreateTempSubdirectory("polypiler-perl-e2e-").FullName;

  [TearDown]
  public void DeleteWorkingDirectory() {
    if (Directory.Exists(this._dir))
      Directory.Delete(this._dir, true);
  }

  private (int ExitCode, string Stdout, string Stderr) RunPerl(params string[] args) {
    var psi = new ProcessStartInfo("perl") {
      WorkingDirectory = this._dir,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    foreach (var arg in args)
      psi.ArgumentList.Add(arg);
    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    return (proc.ExitCode, stdout, stderr);
  }

  private void WriteGenerated(IReadOnlyList<Polypiler.Core.GeneratedFile> files) {
    foreach (var f in files) {
      var full = Path.Combine(this._dir, f.Path);
      Directory.CreateDirectory(Path.GetDirectoryName(full)!);
      File.WriteAllText(full, f.Content, new UTF8Encoding(false));
    }
  }

  [TestCase(PerlByteStorage.Array)]
  [TestCase(PerlByteStorage.String)]
  public void CSharpAdler32_ToPerl_PassesStrictFatalWarnings_AndMatchesReference(PerlByteStorage storage) {
    const string cs = """
      public static class Adler32 {
        public static uint Compute(byte[] data) {
          uint s1 = 1;
          uint s2 = 0;
          for (int i = 0; i < data.Length; i++) {
            s1 = (s1 + data[i]) % 65521u;
            s2 = (s2 + s1) % 65521u;
          }
          return (s2 << 16) | s1;
        }
      }
      """;
    var imported = CSharpFrontend.Import([new CSharpSource("Adler32.cs", cs)], false);
    Assert.That(imported.Diagnostics, Is.Empty);
    var profile = PerlProfile.Default with { ByteStorage = storage };
    var legalized = PerlEmitter.Legalize(imported.Module!, () => PrecomputedImplementations.Module, profile);
    var files = PerlEmitter.Emit(legalized, profile);
    this.WriteGenerated(files);

    var libDir = Path.Combine(this._dir, "lib");
    var pmPath = Path.Combine(this._dir, files[0].Path);

    var check = this.RunPerl("-I" + libDir, "-Mwarnings=FATAL,all", "-c", pmPath);
    Assert.That(check.ExitCode, Is.Zero, check.Stderr);

    var driver = storage == PerlByteStorage.Array
      ? """
        use strict;
        use warnings FATAL => 'all';
        use Adler32 qw(compute);
        die "octet string failed" unless compute("Wikipedia") == 0x11E60398;
        die "array ref failed" unless compute([unpack('C*', 'Wikipedia')]) == 0x11E60398;
        die "empty failed" unless compute("") == 1;
        my $ok = eval { compute("\x{20AC}"); 1 };
        die "wide char should croak" if $ok;
        print "OK\n";
        """
      : """
        use strict;
        use warnings FATAL => 'all';
        use Adler32 qw(compute);
        my $b = "Wikipedia";
        die "string storage failed" unless compute(\$b) == 0x11E60398;
        my $e = "";
        die "empty failed" unless compute(\$e) == 1;
        print "OK\n";
        """;
    var run = this.RunPerl("-I" + libDir, "-e", driver);
    Assert.That(run.ExitCode, Is.Zero, run.Stderr);
    Assert.That(run.Stdout.Trim(), Is.EqualTo("OK"));
  }

  [Test]
  public void CSharpScalarsControlStructsAndTryFinally_ToPerl_ExecuteCleanlyUnderFatalWarnings() {
    const string cs = """
      public struct Point {
        public int X;
        public int Y;
        public Point(int x, int y) { this.X = x; this.Y = y; }
      }
      public static class Suite {
        public static int WrapAdd(int a, int b) => a + b;
        public static uint Xorshift(uint s) {
          s ^= s << 13;
          s ^= s >> 17;
          s ^= s << 5;
          return s;
        }
        public static int SwitchRange(int x) {
          switch (x) {
            case 1: case 2: case 3: case 4: return 10;
            case 9: return 20;
            default: return -1;
          }
        }
        public static int StructValueSemantics(int x) {
          var a = new Point(x, 2);
          var b = a;
          b.X = 99;
          return a.X * 100 + b.X;
        }
        public static int SafeDivFinally(int a, int b, int[] log) {
          try {
            return a / b;
          } catch (System.DivideByZeroException) {
            return -1;
          } finally {
            log[0] = 77;
          }
        }
      }
      """;
    var imported = CSharpFrontend.Import([new CSharpSource("Suite.cs", cs)], false);
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    var legalized = PerlEmitter.Legalize(imported.Module!, () => PrecomputedImplementations.Module);
    var files = PerlEmitter.Emit(legalized);
    this.WriteGenerated(files);

    var libDir = Path.Combine(this._dir, "lib");
    var driver = """
      use strict;
      use warnings FATAL => 'all';
      use Suite qw(wrap_add xorshift switch_range struct_value_semantics safe_div_finally);
      die "wrap_add" unless wrap_add(2147483647, 1) == -2147483648;
      die "xorshift" unless xorshift(1) == 270369;
      die "switch 3" unless switch_range(3) == 10;
      die "switch 9" unless switch_range(9) == 20;
      die "switch 0" unless switch_range(0) == -1;
      die "struct copy" unless struct_value_semantics(5) == 599;
      my $log = [0];
      die "safe_div ok" unless safe_div_finally(20, 4, $log) == 5 && $log->[0] == 77;
      $log = [0];
      die "safe_div zero" unless safe_div_finally(20, 0, $log) == -1 && $log->[0] == 77;
      print "OK\n";
      """;
    var run = this.RunPerl("-I" + libDir, "-e", driver);
    Assert.That(run.ExitCode, Is.Zero, $"{run.Stderr}\n--- MODULE ---\n{files[0].Content}");
    Assert.That(run.Stdout.Trim(), Is.EqualTo("OK"));
  }

  [Test]
  public void PerlFrontend_RoundTripToPerl_MatchesLivePerlOracle() {
    const string perlSource = """
      package Checksum;
      use strict;
      use warnings;
      use Exporter qw(import);
      our @EXPORT_OK = qw(adler32 mix_u32);

      sub adler32 {
        my ($data) = @_;
        my $s1 = 1;
        my $s2 = 0;
        my $n = length($data);
        for my $i (0 .. $n - 1) {
          $s1 = ($s1 + ord(substr($data, $i, 1))) % 65521;
          $s2 = ($s2 + $s1) % 65521;
        }
        return ($s2 << 16) | $s1;
      }

      sub mix_u32 {
        use integer;
        my ($x, $y) = @_;
        my $z = (($x * 33) ^ $y) & 0xFFFFFFFF;
        return $z;
      }

      1;
      """;
    const string contracts = """
      Checksum::adler32.data: seq<u8>
      Checksum::adler32.return: u32
      Checksum::mix_u32.x: u32
      Checksum::mix_u32.y: u32
      Checksum::mix_u32.return: u32
      """;
    var imported = PerlFrontend.Import("Checksum.pm", perlSource, new PerlImportOptions { Contracts = contracts });
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));

    var legalized = PerlEmitter.Legalize(imported.Module!, () => PrecomputedImplementations.Module);
    var files = PerlEmitter.Emit(legalized);
    this.WriteGenerated(files);

    var libDir = Path.Combine(this._dir, "lib");
    var driver = """
      use strict;
      use warnings FATAL => 'all';
      use Checksum qw(adler32 mix_u32);
      die "adler32" unless adler32("Wikipedia") == 0x11E60398;
      die "mix_u32" unless mix_u32(5381, 97) == ((5381 * 33) ^ 97);
      print "OK\n";
      """;
    var run = this.RunPerl("-I" + libDir, "-e", driver);
    Assert.That(run.ExitCode, Is.Zero, $"{run.Stderr}\n--- MODULE ---\n{files[0].Content}");
    Assert.That(run.Stdout.Trim(), Is.EqualTo("OK"));
  }

  [Test]
  public void EmittedTestMoreFile_PassesUnderPerl() {
    var a = new IrLocal("a", IrType.I32);
    var b = new IrLocal("b", IrType.I32);
    var add = new IrFunction("Add", true, IrType.I32, [a, b], [],
      new([new IrReturn(new IrBinary(IrBinaryOp.Add, new IrLocalRef(a), new IrLocalRef(b)))]));
    var test = new IrFunction("TestAdd", true, IrType.Void, [], [],
      new([
        new IrIf(
          new IrBinary(IrBinaryOp.NotEqual, new IrCall("Add", IrType.I32, [new IrConst(IrType.I32, 20), new IrConst(IrType.I32, 22)]), new IrConst(IrType.I32, 42)),
          new([new IrThrow(new IrNew(IrType.Object(IrExceptionClass.AssertionException), IrExceptionClass.Constructor(IrExceptionClass.AssertionException, 1), [new IrStrConst("bad sum")]))]),
          null),
        new IrReturn(null)
      ]));
    var mod = new IrModule("Demo.Calc", [add, test]);
    var files = PerlEmitter.Emit(mod, options: new PerlEmitOptions { TestFunctions = new HashSet<string> { "TestAdd" } });
    this.WriteGenerated(files);

    var libDir = Path.Combine(this._dir, "lib");
    var testPath = Path.Combine(this._dir, files[1].Path);
    var run = this.RunPerl("-I" + libDir, testPath);
    Assert.That(run.ExitCode, Is.Zero, $"{run.Stdout}\n{run.Stderr}");
    Assert.That(run.Stdout, Does.Contain("ok 1"));
  }

}
