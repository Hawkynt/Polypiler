using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Perl;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Perl;

/// <summary>Tests for Perl hashes, constants, undef/defined///, die/eval/try and OOP/magic refusals (#439).</summary>
[TestFixture]
public sealed class PerlTypesFrontendTests {

  private static IrModule Import(string source, string contracts = "") {
    var result = PerlFrontend.Import("types.pl", source, new PerlImportOptions { Contracts = contracts });
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    return result.Module!;
  }

  [Test]
  public void Constants_UndefCoalesce_RecordHashes_AndEvalDie_LowerAndInterpret() {
    const string src = """
      use strict;
      use warnings;
      use constant {
        FLAG_A => 1,
        FLAG_B => 2,
      };
      sub flags {
        return FLAG_A | FLAG_B;
      }
      sub coalesce {
        my ($x) = @_;
        return $x // 99;
      }
      sub is_set {
        my ($x) = @_;
        return defined($x) ? 1 : 0;
      }
      sub plane_area {
        my ($w, $h) = @_;
        my $p = { width => $w, height => $h };
        return $p->{width} * $p->{height};
      }
      sub safe_div {
        my ($a, $b) = @_;
        my $r = 0;
        eval {
          die "zero" if $b == 0;
          $r = int($a / $b);
        };
        if ($@) {
          return -1;
        }
        return $r;
      }
      1;
      """;
    const string contracts = """
      flags.return: i32
      coalesce.x: nullable(i32)
      coalesce.return: i32
      is_set.x: nullable(i32)
      is_set.return: i32
      plane_area.w: i32
      plane_area.h: i32
      plane_area.return: i32
      safe_div.a: i32
      safe_div.b: i32
      safe_div.return: i32
      """;
    var mod = Import(src, contracts);
    Assert.That(IrVerifier.Verify(mod), Is.Empty);

    Assert.That(IrInterpreter.Call(mod, "flags", []), Is.EqualTo(3));
    Assert.That(IrInterpreter.Call(mod, "plane_area", [6, 7]), Is.EqualTo(42));
    Assert.That(IrInterpreter.Call(mod, "safe_div", [20, 4]), Is.EqualTo(5));
    Assert.That(IrInterpreter.Call(mod, "safe_div", [20, 0]), Is.EqualTo(-1));
  }

  [Test]
  public void Bless_UnsortedKeys_StringEval_AndTie_AreRefused() {
    var blessed = PerlFrontend.Import("oo.pl", "sub new { my ($cls) = @_; return bless {}, $cls; }");
    Assert.That(blessed.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1740"));

    var unsorted = PerlFrontend.Import("h.pl", "sub f { my ($h) = @_; my @k = keys %$h; return scalar(@k); }",
      new PerlImportOptions { Contracts = "f.h: map<str,i32>\nf.return: i32" });
    Assert.That(unsorted.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1741"));

    var strEval = PerlFrontend.Import("e.pl", "sub f { my ($s) = @_; return eval $s; }",
      new PerlImportOptions { Contracts = "f.s: str\nf.return: i32" });
    Assert.That(strEval.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1742"));

    var tied = PerlFrontend.Import("t.pl", "sub f { my %h; tie %h, 'Foo'; return 1; }");
    Assert.That(tied.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1743"));
  }

}
