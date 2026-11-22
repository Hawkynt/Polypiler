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

  [TestCase("perl/5.6")]
  [TestCase("perl/5.8")]
  [TestCase("perl/5")]
  public void Dialects56_58_And5_ExecuteIdenticallyUnderFatalWarnings(string profileName) {
    const string cs = """
      using System;
      using System.Buffers.Binary;

      public enum Mode { Fast = 1, Safe = 2 }

      public static class DialectCheck {
        public static int Coalesce(int? x) => x ?? 42;
        public static double FloatSuite(double a, double b) {
          var t = Math.Truncate(a);
          var r = Math.Round(b, MidpointRounding.AwayFromZero);
          var e = Math.Round(2.5, MidpointRounding.ToEven);
          var c = Math.CopySign(3.0, -1.0);
          return t + r + e + c + (a / b);
        }
        public static bool FloatFlags(double x)
          => double.IsNaN(x) || double.IsInfinity(x) || !double.IsNormal(x);
        public static long EndianRoundTrip(byte[] buf, short s, ulong u, double d) {
          BinaryPrimitives.WriteInt16LittleEndian(buf, s);
          var rs = BinaryPrimitives.ReadInt16LittleEndian(buf);
          BinaryPrimitives.WriteUInt64BigEndian(buf, u);
          var ru = BinaryPrimitives.ReadUInt64BigEndian(buf);
          BinaryPrimitives.WriteDoubleBigEndian(buf, d);
          var rd = BinaryPrimitives.ReadDoubleBigEndian(buf);
          return (long)rs + (long)(ru & 0xFFFFUL) + (long)rd;
        }
        public static int EnumVal(Mode m) => m == Mode.Safe ? 20 : 10;
      }
      """;
    var imported = CSharpFrontend.Import([new CSharpSource("DialectCheck.cs", cs)], false);
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    var profile = PerlProfile.Parse(profileName);
    var legalized = PerlEmitter.Legalize(imported.Module!, () => PrecomputedImplementations.Module, profile);
    var files = PerlEmitter.Emit(legalized, profile);
    this.WriteGenerated(files);

    if (profileName is "perl/5.6" or "perl/5.8") {
      Assert.That(files[0].Content, Does.Not.Contain(" // ").And.Not.Contain("POSIX::isnan").And.Not.Contain("d>").And.Not.Contain("Q>"));
      Assert.That(files[0].Content, Does.Contain("our @ISA = qw(Exporter);"));
    }

    var libDir = Path.Combine(this._dir, "lib");
    var driver = """
      use strict;
      use warnings FATAL => 'all';
      use DialectCheck qw(coalesce float_suite float_flags endian_round_trip enum_val);
      die "coalesce undef" unless coalesce(undef) == 42;
      die "coalesce val" unless coalesce(9) == 9;
      die "float_suite" unless abs(float_suite(3.7, 2.5) - (3.0 + 3.0 + 2.0 - 3.0 + 3.7/2.5)) < 1e-9;
      die "float_flags normal" if float_flags(1.5);
      die "float_flags zero" unless float_flags(0.0);
      my $buf = [(0) x 16];
      die "endian" unless endian_round_trip($buf, -5, 72623859790381066, 12.0) == (-5 + 10 + 12);
      die "enum" unless enum_val(2) == 20 && enum_val(1) == 10;
      print "OK\n";
      """;
    var run = this.RunPerl("-I" + libDir, "-e", driver);
    Assert.That(run.ExitCode, Is.Zero, $"{run.Stderr}\n--- MODULE ---\n{files[0].Content}");
    Assert.That(run.Stdout.Trim(), Is.EqualTo("OK"));
  }

  [Test]
  public void BlessedHashObjects_HierarchyInterfacesCastsAndUsing_ExecuteInLivePerl() {
    const string cs = """
      using System;

      public interface IScalable {
        int Scaled(int factor);
      }

      public class Shape : IScalable {
        private int _base;
        public Shape(int b) { this._base = b; }
        public virtual int Area() => this._base;
        public virtual int Scaled(int factor) => this.Area() * factor;
      }

      public class Rect : Shape {
        private int _height;
        public Rect(int w, int h) : base(w) { this._height = h; }
        public override int Area() => base.Area() * this._height;
      }

      public sealed class Tracked : IDisposable {
        private int[] _log;
        public Tracked(int[] log) { this._log = log; }
        public void Dispose() { this._log[0] = 99; }
      }

      public static class ShapesRunner {
        public static int Compute(int w, int h, int[] log) {
          Shape s = new Rect(w, h);
          IScalable sc = s;
          int bonus = 0;
          if (s is Rect r) {
            if (sc is IScalable) bonus = r.Area();
          }
          using (var t = new Tracked(log)) {
            bonus += sc.Scaled(2);
          }
          return bonus;
        }
      }
      """;
    var imported = CSharpFrontend.Import([new CSharpSource("ShapesRunner.cs", cs)], false);
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    var files = PerlEmitter.Emit(imported.Module!, PerlProfile.Default);
    this.WriteGenerated(files);

    var libDir = Path.Combine(this._dir, "lib");
    var driver = """
      use strict;
      use warnings FATAL => 'all';
      use ShapesRunner qw(compute);
      my $log = [0];
      my $res = compute(3, 4, $log);
      die "compute=$res" unless $res == 12 + 24;
      die "dispose log=$log->[0]" unless $log->[0] == 99;
      my $r = ShapesRunner::Rect->new(5, 6);
      die "blessed ref" unless ref($r) eq 'ShapesRunner::Rect';
      die "hash access" unless $r->{height} == 6;
      die "area" unless $r->area() == 30;
      print "OK\n";
      """;
    var run = this.RunPerl("-I" + libDir, "-e", driver);
    Assert.That(run.ExitCode, Is.Zero, $"{run.Stderr}\n--- MODULE ---\n{files[0].Content}");
    Assert.That(run.Stdout.Trim(), Is.EqualTo("OK"));
  }

  [TestCase("perl/5.6")]
  [TestCase("perl/5.8")]
  [TestCase("perl/5")]
  public void InsideOutObjects_EnforcePrivateAndProtectedEncapsulation_InLivePerl(string profileName) {
    var baseCls = new IrClassType("VaultBase");
    var fSecret = new IrField("Secret", IrType.I32, false) { Visibility = IrVisibility.Private };
    var fTag = new IrField("Tag", IrType.I32, false) { Visibility = IrVisibility.Protected };
    var fId = new IrField("Id", IrType.I32, true) { Visibility = IrVisibility.Public };
    var fLabel = new IrField("Label", IrType.I32, false) { Visibility = IrVisibility.Public };
    baseCls.Define([fSecret, fTag, fId, fLabel]);
    var bCtor = new IrConstructor("VaultBase__ctor");
    var mReveal = new IrMethod("Reveal", "VaultBase__reveal", false);
    var mRotate = new IrMethod("RotateSecret", "VaultBase__rotate", false);
    baseCls.DefineMembers([bCtor], [mReveal, mRotate], [], []);

    var subCls = new IrClassType("VaultSub");
    subCls.Inherit(baseCls, []);
    subCls.Define([]);
    var sCtor = new IrConstructor("VaultSub__ctor");
    var mReadTag = new IrMethod("ReadTag", "VaultSub__read_tag", false);
    subCls.DefineMembers([sCtor], [mReadTag], [], []);

    var bSelf = new IrLocal("self", IrType.Object(baseCls));
    var sSelf = new IrLocal("self", IrType.Object(subCls));
    var pS = new IrLocal("s", IrType.I32);
    var pT = new IrLocal("t", IrType.I32);
    var pI = new IrLocal("id", IrType.I32);
    var pL = new IrLocal("lbl", IrType.I32);
    var pNext = new IrLocal("next", IrType.I32);
    var spS = new IrLocal("s", IrType.I32);
    var spT = new IrLocal("t", IrType.I32);
    var spI = new IrLocal("id", IrType.I32);
    var spL = new IrLocal("lbl", IrType.I32);
    var mpS = new IrLocal("s", IrType.I32);
    var mpT = new IrLocal("t", IrType.I32);
    var mpI = new IrLocal("id", IrType.I32);
    var mpL = new IrLocal("lbl", IrType.I32);

    var mod = new IrModule("VaultApp", [
      new IrFunction("VaultBase__ctor", false, IrType.Void, [bSelf, pS, pT, pI, pL], [], new([
        new IrFieldStore(new IrLocalRef(bSelf), fSecret, new IrLocalRef(pS)),
        new IrFieldStore(new IrLocalRef(bSelf), fTag, new IrLocalRef(pT)),
        new IrFieldStore(new IrLocalRef(bSelf), fId, new IrLocalRef(pI)),
        new IrFieldStore(new IrLocalRef(bSelf), fLabel, new IrLocalRef(pL)),
        new IrReturn(null)
      ])),
      new IrFunction("VaultBase__reveal", false, IrType.I32, [bSelf], [], new([
        new IrReturn(new IrFieldLoad(new IrLocalRef(bSelf), fSecret))
      ])),
      new IrFunction("VaultBase__rotate", false, IrType.Void, [bSelf, pNext], [], new([
        new IrFieldStore(new IrLocalRef(bSelf), fSecret, new IrLocalRef(pNext)),
        new IrReturn(null)
      ])),
      new IrFunction("VaultSub__ctor", false, IrType.Void, [sSelf, spS, spT, spI, spL], [], new([
        new IrExprStmt(new IrCall("VaultBase__ctor", IrType.Void, [new IrLocalRef(sSelf), new IrLocalRef(spS), new IrLocalRef(spT), new IrLocalRef(spI), new IrLocalRef(spL)])),
        new IrReturn(null)
      ])),
      new IrFunction("VaultSub__read_tag", false, IrType.I32, [sSelf], [], new([
        new IrReturn(new IrFieldLoad(new IrLocalRef(sSelf), fTag))
      ])),
      new IrFunction("Make", true, IrType.Object(subCls), [mpS, mpT, mpI, mpL], [], new([
        new IrReturn(new IrNew(IrType.Object(subCls), sCtor, [new IrLocalRef(mpS), new IrLocalRef(mpT), new IrLocalRef(mpI), new IrLocalRef(mpL)]))
      ]))
    ]) { Types = [baseCls, subCls] };

    var profile = PerlProfile.Parse(profileName) with { ObjectModel = PerlObjectModel.InsideOut };
    var files = PerlEmitter.Emit(mod, profile);
    this.WriteGenerated(files);

    var libDir = Path.Combine(this._dir, "lib");
    var driver = """
      use strict;
      use warnings FATAL => 'all';
      use VaultApp qw(make);

      my $box = make(42, 7, 100, 5);
      die "reveal" unless $box->reveal() == 42;
      die "read_tag" unless $box->read_tag() == 7;
      die "id" unless $box->id() == 100;
      die "label" unless $box->label() == 5;
      $box->label(9);
      die "label updated" unless $box->label() == 9;
      $box->rotate_secret(99);
      die "reveal updated" unless $box->reveal() == 99;

      # 1. External hash dereference MUST die (opaque blessed scalar ref, NOT a hash!)
      my $hash_ok = eval { my $x = $box->{_secret}; 1 };
      die "external hash access should fail" if $hash_ok;
      die "unexpected error: $@" unless $@ =~ /Not a HASH reference/i;

      # 2. Private field has NO accessor method at all
      die "private field must not have accessor" if $box->can('_secret') || $box->can('secret');

      # 3. Lexical storage hash is invisible from the symbol table
      {
          no strict 'refs';
          my %stash_hash = %{'VaultApp::VaultBase::_f_secret'};
          die "lexical hash leaked to stash" if keys %stash_hash;
      }

      # 4. Protected field cannot be read externally outside the class hierarchy
      my $prot_ok = eval { $box->_prot_tag(); 1 };
      die "external protected access should fail" if $prot_ok;

      # 5. Readonly public field rejects external writes
      my $ro_ok = eval { $box->id(123); 1 };
      die "readonly write should fail" if $ro_ok;

      # 6. Destruction cleans up cleanly without warnings
      undef $box;
      print "OK\n";
      """;
    var run = this.RunPerl("-I" + libDir, "-e", driver);
    Assert.That(run.ExitCode, Is.Zero, $"{run.Stderr}\n--- MODULE ---\n{files[0].Content}");
    Assert.That(run.Stdout.Trim(), Is.EqualTo("OK"));
  }

  [TestCase("perl/5.8")]
  [TestCase("perl/5")]
  public void MooseObjects_ClassesRolesAndInheritance_ExecuteInLivePerl(string profileName) {
    const string cs = """
      public interface IGreeter {
        int Score();
      }
      public class BaseCounter {
        public readonly int BaseVal;
        public BaseCounter(int v) { this.BaseVal = v; }
        public virtual int Score() => this.BaseVal;
      }
      public class Counter : BaseCounter, IGreeter {
        private int _bonus;
        public Counter(int v, int b) : base(v) { this._bonus = b; }
        public override int Score() => base.Score() + this._bonus;
      }
      public static class MooseDemo {
        public static int Run(int v, int b) {
          Counter c = new Counter(v, b);
          IGreeter g = c;
          return (g is IGreeter ? 1 : 0) + g.Score();
        }
      }
      """;
    var imported = CSharpFrontend.Import([new CSharpSource("MooseDemo.cs", cs)], false);
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    var profile = PerlProfile.Parse(profileName) with { ObjectModel = PerlObjectModel.Moose };
    var files = PerlEmitter.Emit(imported.Module!, profile);
    this.WriteGenerated(files);

    Assert.That(files[0].Content, Does.Contain("use Moose;").And.Contain("use Moose::Role;").And.Contain("extends 'MooseDemo::BaseCounter';").And.Contain("with 'MooseDemo::IGreeter';").And.Contain("requires 'score';").And.Contain("__PACKAGE__->meta->make_immutable;"));

    var libDir = Path.Combine(this._dir, "lib");
    var incArgs = new List<string> { "-I" + libDir };
    var localPerlLib = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "perl5", "lib", "perl5");
    if (Directory.Exists(localPerlLib))
      incArgs.Add("-I" + localPerlLib);

    // Use real CPAN Moose whenever installed (system @INC, PERL5LIB, or ~/perl5/lib/perl5); fall back to a
    // minimal test-only shim only on hosts where CPAN Moose is not installed.
    var probeArgs = new List<string>(incArgs) { "-MMoose", "-e", "1" };
    var hasCpanMoose = this.RunPerl([.. probeArgs]).ExitCode == 0;
    if (!hasCpanMoose) {
      var shimDir = Path.Combine(this._dir, "moose-shim");
      Directory.CreateDirectory(Path.Combine(shimDir, "Moose"));
      File.WriteAllText(Path.Combine(shimDir, "Moose.pm"), """
        package Moose;
        use strict;
        use warnings;
        our %ROLES;
        our %ATTRS;
        sub import {
            my $caller = caller;
            strict->import;
            warnings->import;
            no strict 'refs';
            *{"${caller}::extends"} = sub { @{"${caller}::ISA"} = @_; };
            *{"${caller}::with"} = sub {
                for my $role (@_) {
                    for my $req (@{$Moose::Role::REQUIRES{$role} || []}) {
                        die "$role requires $req" unless $caller->can($req);
                    }
                    $ROLES{$caller}{$role} = 1;
                }
            };
            *{"${caller}::has"} = sub {
                my ($name, %spec) = @_;
                push @{$ATTRS{$caller}}, [$name, $spec{default}];
                *{"${caller}::$name"} = sub {
                    my $self = shift;
                    if (@_) {
                        die "readonly $name" if ($spec{is} || 'rw') eq 'ro';
                        $self->{$name} = shift;
                    }
                    return $self->{$name};
                };
                if (defined $spec{writer}) {
                    my $w = $spec{writer};
                    *{"${caller}::$w"} = sub { my ($self, $v) = @_; $self->{$name} = $v; };
                }
            };
            *{"${caller}::new"} = sub {
                my ($class, %args) = @_;
                my $self = bless {}, $class;
                my @chain = ($class);
                my @walk = ($class);
                while (my $p = shift @walk) {
                    for my $isa (@{"${p}::ISA"}) { unshift @chain, $isa; push @walk, $isa; }
                }
                for my $c (@chain) {
                    for my $a (@{$ATTRS{$c} || []}) {
                        my ($n, $def) = @$a;
                        $self->{$n} = exists $args{$n} ? $args{$n} : (ref($def) eq 'CODE' ? $def->() : $def);
                    }
                }
                return $self;
            };
            *{"${caller}::does"} = sub {
                my ($self, $role) = @_;
                my $c = ref($self) || $self;
                return $ROLES{$c}{$role} ? 1 : 0;
            };
            *{"${caller}::meta"} = sub { bless {}, 'Moose::_Meta'; };
        }
        package Moose::_Meta;
        sub make_immutable { return 1; }
        sub is_immutable { return 1; }
        1;
        """);
      File.WriteAllText(Path.Combine(shimDir, "Moose", "Role.pm"), """
        package Moose::Role;
        use strict;
        use warnings;
        our %REQUIRES;
        sub import {
            my $caller = caller;
            strict->import;
            warnings->import;
            no strict 'refs';
            *{"${caller}::requires"} = sub { push @{$REQUIRES{$caller}}, @_; };
            *{"${caller}::with"} = sub { };
        }
        1;
        """);
      incArgs.Add("-I" + shimDir);
    }

    var driver = """
      use strict;
      use warnings FATAL => 'all';
      use MooseDemo qw(run);
      die "moose run failed" unless run(30, 11) == 42;
      my $c = MooseDemo::Counter->_ctor_0(10, 5);
      die "isa failed" unless $c->isa('MooseDemo::BaseCounter');
      die "does failed" unless $c->does('MooseDemo::IGreeter');
      die "immutable failed" unless MooseDemo::Counter->meta->is_immutable;
      my $ro_ok = eval { $c->base_val(99); 1 };
      die "ro write should fail" if $ro_ok;
      die "base_val mutated" unless $c->base_val() == 10;
      print "OK\n";
      """;
    incArgs.Add("-e");
    incArgs.Add(driver);
    var run = this.RunPerl([.. incArgs]);
    Assert.That(run.ExitCode, Is.Zero, $"{run.Stderr}\n--- MODULE ---\n{files[0].Content}");
    Assert.That(run.Stdout.Trim(), Is.EqualTo("OK"));
  }

  [Test]
  public void PerlBlessedAndMoose_FrontendToInsideOutPerl_EnforcesPrivacyLive() {
    const string perlSource = """
      package Account;
      use Moose;
      has '_balance' => (is => 'rw', isa => 'Int', default => 0);
      sub deposit {
        use integer;
        my ($self, $amt) = @_;
        $self->_balance($self->_balance() + $amt);
        return;
      }
      sub balance {
        my ($self) = @_;
        return $self->_balance();
      }
      __PACKAGE__->meta->make_immutable;

      package main;
      sub run_account {
        my ($start, $add) = @_;
        my $acc = Account->new(_balance => $start);
        $acc->deposit($add);
        return $acc->balance();
      }
      1;
      """;
    const string contracts = """
      Account::deposit.amt: i64
      Account::deposit.return: void
      Account::balance.return: i64
      run_account.start: i64
      run_account.add: i64
      run_account.return: i64
      """;
    var imported = PerlFrontend.Import("AccountApp.pl", perlSource, new PerlImportOptions { Contracts = contracts });
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));

    var files = PerlEmitter.Emit(imported.Module!, PerlProfile.Default with { ObjectModel = PerlObjectModel.InsideOut });
    this.WriteGenerated(files);
    var libDir = Path.Combine(this._dir, "lib");
    var driver = """
      use strict;
      use warnings FATAL => 'all';
      use Account qw(run_account);
      die "account sum" unless run_account(15, 27) == 42;
      my $a = Account::Account->new(10);
      $a->deposit(5);
      die "balance" unless $a->balance() == 15;
      die "private leaked" if $a->can('_balance') || eval { my $x = $a->{_balance}; 1 };
      print "OK\n";
      """;
    var run = this.RunPerl("-I" + libDir, "-e", driver);
    Assert.That(run.ExitCode, Is.Zero, $"{run.Stderr}\n--- MODULE ---\n{files[0].Content}");
    Assert.That(run.Stdout.Trim(), Is.EqualTo("OK"));
  }

}
