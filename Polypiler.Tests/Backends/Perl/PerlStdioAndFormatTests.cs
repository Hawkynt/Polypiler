using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Perl;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Perl.PerlIr;

namespace Polypiler.Tests.Backends.Perl;

/// <summary>
/// Tests for Perl <see cref="IrWriteLine"/>, <see cref="IrIntrinsicOp.FormatDotNet"/>,
/// <see cref="IrIntrinsicOp.FormatEcma"/>, boolean formatting, <c>17-stdio.cs</c>,
/// <c>09-lambdas-and-closures.cs</c>, and honest refusal of <see cref="IrForeignCall"/>.
/// </summary>
[TestFixture]
public sealed class PerlStdioAndFormatTests {

  private static readonly PerlProfile[] _allProfiles = [PerlProfile.Perl56, PerlProfile.Perl58, PerlProfile.Default];

  [Test]
  public void Showcase17Stdio_TranslatesAndRunsLiveUnderPerlW_AcrossAllProfiles() {
    var mod = ImportShowcase("17-stdio.cs");
    foreach (var profile in _allProfiles) {
      Assert.That(PerlEmitter.Check(mod, profile), Is.Empty, profile.Name);
      var source = Emit(mod, profile);
      Assert.That(source, Does.Contain("print("), profile.Name);
      if (File.Exists("/usr/bin/perl")) {
        var stdout = RunPerl(source, "StdioShowcase", "my $r = StdioShowcase::run(); die \"bad $r\" unless $r == 14;");
        var lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        Assert.That(lines, Is.EqualTo(new[] { "step 1: sum=1", "step 2: sum=5", "step 3: sum=14" }), profile.Name);
      }
    }
  }

  [Test]
  public void WriteLineNullAndFormatting_AllProfiles_RunLiveUnderPerlW() {
    var s = L("s", IrType.String);
    var i = L("i", IrType.I32);
    var u64 = L("u", IrType.U64);
    var d = L("d", IrType.F64);
    var f = L("f", IrType.F32);
    var b = L("b", IrType.Bool);

    var mod = new IrModule("StdioFmt", [
      Function("writeAll", IrType.Void, [s],
        new IrWriteLine(new IrStrConst("first")),
        new IrWriteLine(new IrNull(IrType.String)),
        new IrWriteLine(R(s)),
        new IrWriteLine(new IrCall("missing", IrType.String, [])),
        new IrWriteLine(new IrStrConst("last")),
        new IrReturn(null)),
      Function("missing", false, IrType.String, [], new IrReturn(new IrNull(IrType.String))),
      Function("fmtI32DotNet", IrType.String, [i], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [R(i)]))),
      Function("fmtI32Ecma", IrType.String, [i], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatEcma, IrType.String, [R(i)]))),
      Function("fmtU64DotNet", IrType.String, [u64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [R(u64)]))),
      Function("fmtF64DotNet", IrType.String, [d], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [R(d)]))),
      Function("fmtF64Ecma", IrType.String, [d], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatEcma, IrType.String, [R(d)]))),
      Function("fmtF32DotNet", IrType.String, [f], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [R(f)]))),
      Function("fmtBoolDotNet", IrType.String, [b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatBoolDotNet, IrType.String, [R(b)]))),
      Function("fmtBoolEcma", IrType.String, [b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatBoolEcma, IrType.String, [R(b)])))
    ]);

    foreach (var profile in _allProfiles) {
      Assert.That(PerlEmitter.Check(mod, profile), Is.Empty, profile.Name);
      var source = Emit(mod, profile);
      if (File.Exists("/usr/bin/perl")) {
        var check = """
          StdioFmt::write_all(undef);
          die 'i32' unless StdioFmt::fmt_i32_dot_net(-42) eq '-42';
          die 'i32e' unless StdioFmt::fmt_i32_ecma(123) eq '123';
          die 'u64' unless StdioFmt::fmt_u64_dot_net(18446744073709551615) eq '18446744073709551615';
          my $nan = unpack('d', pack('Q', 9221120237041090560));
          my $neg0 = unpack('d', pack('Q', 9223372036854775808));
          die 'nan' unless StdioFmt::fmt_f64_dot_net($nan) eq 'NaN';
          die 'inf' unless StdioFmt::fmt_f64_dot_net(9**9**9) eq 'Infinity';
          die 'ninf' unless StdioFmt::fmt_f64_dot_net(-9**9**9) eq '-Infinity';
          die 'neg0dn' unless StdioFmt::fmt_f64_dot_net($neg0) eq '-0';
          die 'pos0dn' unless StdioFmt::fmt_f64_dot_net(0.0) eq '0';
          die 'neg0ec' unless StdioFmt::fmt_f64_ecma($neg0) eq '0';
          die 'f64' unless StdioFmt::fmt_f64_dot_net(1.5) eq '1.5';
          die 'f64e' unless StdioFmt::fmt_f64_ecma(1e-7) eq '1e-7';
          die 'f32' unless StdioFmt::fmt_f32_dot_net(3.5) eq '3.5';
          die 'bdn' unless StdioFmt::fmt_bool_dot_net(1) eq 'True' && StdioFmt::fmt_bool_dot_net(0) eq 'False';
          die 'bec' unless StdioFmt::fmt_bool_ecma(1) eq 'true' && StdioFmt::fmt_bool_ecma(0) eq 'false';
          """;
        var stdout = RunPerl(source, "StdioFmt", check);
        var lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        Assert.That(lines, Is.EqualTo(new[] { "first", "", "", "", "last" }), profile.Name);
      }
    }
  }

  [Test]
  public void ForeignCall_RemainsRefusedWithPLY1751() {
    var mod = new IrModule("Native", [
      Function("callNative", IrType.I32, [], new IrReturn(new IrForeignCall("Abs", IrType.I32, [I(-5)])))
    ]) {
      ForeignFunctions = [new IrForeignFunction("Abs", false, "libc.so.6", IrType.I32, [new IrForeignParameter("x", IrType.I32, IrMarshal.Blittable)]) { EntryPoint = "abs" }]
    };
    Assert.That(Problems(mod), Has.Some.Contains("PLY1751"));
  }

  [Test]
  public void Showcase09LambdasAndClosures_TranslatesAndRunsLiveUnderPerlW() {
    var mod = ImportShowcase("09-lambdas-and-closures.cs");
    foreach (var profile in _allProfiles) {
      Assert.That(PerlEmitter.Check(mod, profile), Is.Empty, profile.Name);
      var source = Emit(mod, profile);
      if (File.Exists("/usr/bin/perl")) {
        RunPerl(source, "ClosuresShowcase", "my $r = ClosuresShowcase::run(10, 5); die \"got $r\" unless $r == 45;");
      }
    }
  }

  private static IrModule ImportShowcase(string fileName) {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Polypiler.slnx")))
      dir = dir.Parent;
    Assert.That(dir, Is.Not.Null);
    var result = CSharpFrontend.Import(fileName, File.ReadAllText(Path.Combine(dir!.FullName, "docs", "showcase", fileName)));
    Assert.That(result.Diagnostics, Is.Empty, () => string.Join("\n", result.Diagnostics));
    return result.Module!;
  }

  private static string RunPerl(string moduleSource, string packageName, string driverBody) {
    var dir = Path.Combine(Path.GetTempPath(), "polypiler-perl-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      File.WriteAllText(Path.Combine(dir, packageName + ".pm"), moduleSource);
      var script = Path.Combine(dir, "run.pl");
      File.WriteAllText(script, $"use strict;\nuse warnings;\nuse lib '.';\nuse {packageName};\n{driverBody}\n");
      var psi = new ProcessStartInfo("/usr/bin/perl") {
        WorkingDirectory = dir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
      };
      psi.ArgumentList.Add("-w");
      psi.ArgumentList.Add(script);
      using var proc = Process.Start(psi)!;
      var stdout = proc.StandardOutput.ReadToEnd();
      var stderr = proc.StandardError.ReadToEnd();
      proc.WaitForExit();
      Assert.That(proc.ExitCode, Is.Zero, () => $"perl -w failed:\n{stderr}\n--- PM ---\n{moduleSource}");
      Assert.That(stderr, Is.Empty, () => $"perl -w produced warnings:\n{stderr}\n--- PM ---\n{moduleSource}");
      return stdout;
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, true);
    }
  }

}
