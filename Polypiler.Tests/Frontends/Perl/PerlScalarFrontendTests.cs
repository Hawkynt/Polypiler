using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Perl;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Perl;

/// <summary>Tests for the Perl scalar frontend (#438) and the diagnostics of the parser it runs first (#1561).</summary>
[TestFixture]
public sealed class PerlScalarFrontendTests {

  private static IrModule Import(string source, string contracts = "") {
    var result = PerlFrontend.Import("test.pl", source, new PerlImportOptions { Contracts = contracts });
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    Assert.That(result.Module, Is.Not.Null);
    return result.Module!;
  }

  [Test]
  public void UseInteger_AndDefaultScalarArithmetic_InterpretIdenticallyToPerl() {
    const string src = """
      use strict;
      use warnings;
      sub add_i32 {
        use integer;
        my ($a, $b) = @_;
        return ($a + $b) & 0xffffffff;
      }
      sub shift_and_bit {
        use integer;
        my ($x, $n) = @_;
        return (($x << ($n & 31)) ^ ($x >> 1)) | (~$x & 0xff);
      }
      sub mix_float {
        my ($a, $b) = @_;
        return ($a + $b) * 0.5;
      }
      sub compare_ops {
        my ($a, $b) = @_;
        return ($a < $b ? 1 : ($a == $b ? 0 : -1));
      }
      1;
      """;
    const string contracts = """
      add_i32.a: i32
      add_i32.b: i32
      add_i32.return: u32
      shift_and_bit.x: i32
      shift_and_bit.n: i32
      shift_and_bit.return: i32
      mix_float.a: f64
      mix_float.b: f64
      mix_float.return: f64
      compare_ops.a: i32
      compare_ops.b: i32
      compare_ops.return: i32
      """;
    var mod = Import(src, contracts);
    Assert.That(IrVerifier.Verify(mod), Is.Empty);

    Assert.That(IrInterpreter.Call(mod, "add_i32", [10, 32]), Is.EqualTo(42u));
    Assert.That((double)IrInterpreter.Call(mod, "mix_float", [3.0, 5.0])!, Is.EqualTo(4.0).Within(1e-12));
    Assert.That(IrInterpreter.Call(mod, "compare_ops", [2, 5]), Is.EqualTo(1));
    Assert.That(IrInterpreter.Call(mod, "compare_ops", [5, 5]), Is.EqualTo(0));
    Assert.That(IrInterpreter.Call(mod, "compare_ops", [9, 5]), Is.EqualTo(-1));
  }

  [Test]
  public void SyntaxErrors_ArePLY1700_AndConstructsOutsideTheParser_ArePLY1701() {
    var syntaxErr = PerlFrontend.Import("bad.pl", "sub f { my $x = ; }");
    Assert.That(syntaxErr.Module, Is.Null);
    Assert.That(syntaxErr.Diagnostics.Select(d => d.Code), Is.EqualTo(new[] { "PLY1700" }));
    Assert.That(syntaxErr.Diagnostics[0].Message, Does.StartWith("Perl syntax error: "));

    var unterminated = PerlFrontend.Import("bad.pl", "sub f {\n  return 'x;\n}\n");
    Assert.That(unterminated.Diagnostics.Select(d => d.Code), Is.EqualTo(new[] { "PLY1700" }));
    Assert.That(unterminated.Diagnostics[0].Line, Is.EqualTo(2));

    var filter = PerlFrontend.Import("f.pl", "use Filter::Simple;\nsub f { return 1; }");
    Assert.That(filter.Module, Is.Null);
    Assert.That(filter.Diagnostics.Select(d => d.Code), Is.EqualTo(new[] { "PLY1701" }));
    Assert.That(filter.Diagnostics[0].Message, Does.Contain("Filter::Simple"));

    var format = PerlFrontend.Import("f.pl", "format STDOUT =\n.\nsub f { return 1; }");
    Assert.That(format.Diagnostics.Select(d => d.Code), Is.EqualTo(new[] { "PLY1701" }));
  }

  [Test]
  public void MalformedContracts_ReportDiagnostics() {
    var badContract = PerlFrontend.Import("c.pl", "sub f { return 1; }", new PerlImportOptions { Contracts = "not_a_contract" });
    Assert.That(badContract.Module, Is.Null);
    Assert.That(badContract.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1700"));

    var dupContract = PerlFrontend.Import("c.pl", "sub f { my ($x) = @_; return $x; }",
      new PerlImportOptions { Contracts = "f.x: i32\nf.x: i64" });
    Assert.That(dupContract.Module, Is.Null);
    Assert.That(dupContract.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1700"));

    var badCtx = PerlFrontend.Import("c.pl", "sub f { return 1; }",
      new PerlImportOptions { Contracts = "f.context: invalid" });
    Assert.That(badCtx.Module, Is.Null);
    Assert.That(badCtx.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1700"));

    var badType = PerlFrontend.Import("c.pl", "sub f { my ($x) = @_; return $x; }",
      new PerlImportOptions { Contracts = "f.x: complex128" });
    Assert.That(badType.Module, Is.Null);
    Assert.That(badType.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1700"));
  }

}
