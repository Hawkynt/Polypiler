using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Perl;

/// <summary>
/// End-to-end CLI acceptance tests transpiling Perl 5 modules into runnable .NET MSIL executables
/// and chaining them further into JavaScript IIFE modules.
/// </summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
public sealed class PerlToMsilAcceptanceTests {

  private string _dir = "";

  [SetUp]
  public void CreateWorkingDirectory() => this._dir = Directory.CreateTempSubdirectory("polypiler-perl-msil-").FullName;

  [TearDown]
  public void DeleteWorkingDirectory() {
    if (Directory.Exists(this._dir))
      Directory.Delete(this._dir, true);
  }

  private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, string workingDir, params string[] args) {
    var psi = new ProcessStartInfo(fileName) {
      WorkingDirectory = workingDir,
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

  private static (int ExitCode, string Stdout, string Stderr) RunPolypiler(params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var code = Program.Run(args, output, error);
    return (code, output.ToString(), error.ToString());
  }

  [Test]
  public void PerlModule_ToDotNetExecutableAssembly_RunsLiveInDotNet() {
    var pmPath = Path.Combine(this._dir, "TriSum.pm");
    File.WriteAllText(pmPath, """
      package TriSum;
      use strict;
      use warnings;
      use Exporter 'import';
      our @EXPORT_OK = qw(tri_sum main);

      sub tri_sum {
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
        return tri_sum(9);
      }

      1;
      """);

    var outDir = Path.Combine(this._dir, "msil-exe");
    var (code, _, stderr) = RunPolypiler(
      "translate", pmPath,
      "--from", "perl",
      "--perl-contract", "tri_sum.n: i32",
      "--perl-contract", "tri_sum.return: i32",
      "--perl-contract", "main.return: i32",
      "--to", "msil",
      "--output-type", "exe",
      "--output", outDir);
    Assert.That(code, Is.Zero, stderr);

    var dllPath = Directory.GetFiles(outDir, "*.dll").Single();
    var runtimeConfigPath = Directory.GetFiles(outDir, "*.runtimeconfig.json").Single();
    Assert.That(File.Exists(runtimeConfigPath), Is.True);

    var run = RunProcess("dotnet", outDir, dllPath);
    Assert.That(run.ExitCode, Is.EqualTo(45), $"STDOUT:\n{run.Stdout}\nSTDERR:\n{run.Stderr}");
  }

  [Test]
  public void PerlModule_ToDotNetLibrary_ToPythonModule_RunsLiveInPython3() {
    var pmPath = Path.Combine(this._dir, "GcdMod.pm");
    File.WriteAllText(pmPath, """
      package GcdMod;
      use strict;
      use warnings;
      use Exporter 'import';
      our @EXPORT_OK = qw(gcd);

      sub gcd {
        my ($a, $b) = @_;
        while ($b != 0) {
          my $t = $a % $b;
          $a = $b;
          $b = $t;
        }
        return $a;
      }

      1;
      """);

    var contractsPath = Path.Combine(this._dir, "GcdMod.contracts");
    File.WriteAllText(contractsPath, "gcd.a: i32\ngcd.b: i32\ngcd.return: i32\n");

    var msilOut = Path.Combine(this._dir, "msil-lib");
    var r1 = RunPolypiler(
      "translate", pmPath,
      "--from", "perl",
      "--perl-contract", contractsPath,
      "--to", "msil",
      "--output", msilOut);
    Assert.That(r1.ExitCode, Is.Zero, r1.Stderr);

    var pyOut = Path.Combine(this._dir, "py-out");
    var r2 = RunPolypiler(
      "translate", Directory.GetFiles(msilOut, "*.dll").Single(),
      "--from", "msil",
      "--to", "python",
      "--output", pyOut);
    Assert.That(r2.ExitCode, Is.Zero, r2.Stderr);

    var pyFile = Directory.GetFiles(pyOut, "*.py").Single();
    var mod = Path.GetFileNameWithoutExtension(pyFile);
    var pyRun = RunProcess("python3", pyOut, "-c", $"import {mod} as m; assert m.gcd(84, 36) == 12; print('OK')");
    Assert.That(pyRun.ExitCode, Is.Zero, pyRun.Stderr);
    Assert.That(pyRun.Stdout.Trim(), Is.EqualTo("OK"));
  }

}
