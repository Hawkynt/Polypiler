using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Cli;

namespace Polypiler.Tests.Frontends.Perl;

/// <summary>CLI tests for <c>polypiler translate --from perl</c>.</summary>
[TestFixture]
public sealed class TranslateFromPerlTests {

  private string _directory = "";
  private string _out = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-from-perl-" + Guid.NewGuid().ToString("N"));
    this._out = Path.Combine(this._directory, "out");
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() {
    if (Directory.Exists(this._directory))
      Directory.Delete(this._directory, true);
  }

  [Test]
  public void FromPerl_ToCSharpAndToPerl_Succeed() {
    var source = Path.Combine(this._directory, "Adder.pm");
    File.WriteAllText(source, """
      package Adder;
      use strict;
      use warnings;
      use Exporter qw(import);
      our @EXPORT_OK = qw(add_two);
      sub add_two {
        use integer;
        my ($a, $b) = @_;
        return ($a + $b) & 0xffffffff;
      }
      1;
      """);
    using var output = new StringWriter();
    using var error = new StringWriter();
    var csOut = Path.Combine(this._out, "cs");
    var rc = Program.Run([
      "translate", source, "--from", "perl", "--to", "csharp",
      "--perl-contract", "Adder::add_two.a: i32",
      "--perl-contract", "Adder::add_two.b: i32",
      "--perl-contract", "Adder::add_two.return: u32",
      "--output", csOut
    ], output, error);
    Assert.That(rc, Is.Zero, error.ToString());
    Assert.That(File.Exists(Path.Combine(csOut, "Adder.cs")), Is.True);

    using var output2 = new StringWriter();
    using var error2 = new StringWriter();
    var plOut = Path.Combine(this._out, "pl");
    var rc2 = Program.Run([
      "translate", source, "--from", "perl", "--to", "perl",
      "--perl-contract", "Adder::add_two.a: i32",
      "--perl-contract", "Adder::add_two.b: i32",
      "--perl-contract", "Adder::add_two.return: u32",
      "--output", plOut
    ], output2, error2);
    Assert.That(rc2, Is.Zero, error2.ToString());
    Assert.That(File.Exists(Path.Combine(plOut, "lib", "Adder.pm")), Is.True);
  }

  [Test]
  public void PerlContractOption_WithNonPerlSource_IsUsageError() {
    var source = Path.Combine(this._directory, "A.cs");
    File.WriteAllText(source, "public static class A { public static int F(int x) => x; }");
    using var output = new StringWriter();
    using var error = new StringWriter();
    var rc = Program.Run([
      "translate", source, "--from", "csharp", "--to", "perl",
      "--perl-contract", "f.x: i32",
      "--output", this._out
    ], output, error);
    Assert.That(rc, Is.EqualTo(2));
    Assert.That(error.ToString(), Does.Contain("PLY0003"));
  }

}
