using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Perl;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Perl;

/// <summary>Tests for Perl control-flow lowering (#435): branches, modifiers, loops, ranges, labels and refusals.</summary>
[TestFixture]
public sealed class PerlControlFrontendTests {

  private static IrModule Import(string source, string contracts = "") {
    var result = PerlFrontend.Import("ctrl.pl", source, new PerlImportOptions { Contracts = contracts });
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    return result.Module!;
  }

  [Test]
  public void Branches_Modifiers_While_Until_CFor_RangeFor_Reverse_AndLabels() {
    const string src = """
      use strict;
      use warnings;
      sub classify {
        my ($x) = @_;
        return -1 if $x < 0;
        return 0 unless $x != 0;
        if ($x < 10) {
          return 1;
        } elsif ($x < 100) {
          return 2;
        } else {
          return 3;
        }
      }
      sub sum_loops {
        my ($n) = @_;
        my $s = 0;
        for (my $i = 0; $i < $n; $i++) {
          $s += $i;
        }
        for my $j (0 .. $n - 1) {
          $s += $j;
        }
        for my $k (reverse 0 .. $n - 1) {
          $s += $k;
        }
        my $w = $n;
        while ($w > 0) {
          $w--;
          next if $w == 2;
          $s += $w;
        }
        my $u = 0;
        until ($u >= 3) {
          $u++;
          $s += $u;
        }
        return $s;
      }
      sub labelled_break {
        my ($n) = @_;
        my $hits = 0;
        OUTER: for my $i (0 .. $n - 1) {
          for my $j (0 .. $n - 1) {
            last OUTER if $i == 2 && $j == 1;
            $hits++;
          }
        }
        return $hits;
      }
      1;
      """;
    const string contracts = """
      classify.x: i32
      classify.return: i32
      sum_loops.n: i32
      sum_loops.return: i32
      labelled_break.n: i32
      labelled_break.return: i32
      """;
    var mod = Import(src, contracts);
    Assert.That(IrVerifier.Verify(mod), Is.Empty);

    Assert.That(IrInterpreter.Call(mod, "classify", [-5]), Is.EqualTo(-1));
    Assert.That(IrInterpreter.Call(mod, "classify", [0]), Is.EqualTo(0));
    Assert.That(IrInterpreter.Call(mod, "classify", [5]), Is.EqualTo(1));
    Assert.That(IrInterpreter.Call(mod, "classify", [50]), Is.EqualTo(2));
    Assert.That(IrInterpreter.Call(mod, "classify", [500]), Is.EqualTo(3));

    Assert.That(IrInterpreter.Call(mod, "sum_loops", [4]), Is.EqualTo(28));
    Assert.That(IrInterpreter.Call(mod, "labelled_break", [4]), Is.EqualTo(9));
  }

  [Test]
  public void Redo_Goto_AndUnknownLabel_AreRefused() {
    var redo = PerlFrontend.Import("r.pl", "sub f { my $i = 0; while ($i < 3) { $i++; redo if $i == 1; } return $i; }");
    Assert.That(redo.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1710"));

    var gotoRes = PerlFrontend.Import("g.pl", "sub f { L: return 1; goto L; }");
    Assert.That(gotoRes.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1710"));

    var badLabel = PerlFrontend.Import("l.pl", "sub f { for my $i (0 .. 3) { last NOPE; } return 0; }");
    Assert.That(badLabel.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1711"));
  }

}
