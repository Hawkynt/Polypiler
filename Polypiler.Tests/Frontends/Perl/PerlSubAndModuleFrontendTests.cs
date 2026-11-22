using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Perl;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Perl;

/// <summary>Tests for Perl subs, signatures, closures, packages, Exporter, core modules and Test::More (#436).</summary>
[TestFixture]
public sealed class PerlSubAndModuleFrontendTests {

  private static PerlImportResult ImportRaw(string source, string contracts = "")
    => PerlFrontend.Import("mod.pm", source, new PerlImportOptions { Contracts = contracts });

  [Test]
  public void ParameterForms_Exporter_CoreCallMap_AndSignatures_Work() {
    const string src = """
      package Sample::Math;
      use strict;
      use warnings;
      use Exporter qw(import);
      use feature qw(signatures);
      no warnings qw(experimental::signatures);
      use List::Util qw(min max);
      use POSIX qw(floor ceil);
      our @EXPORT_OK = qw(via_list via_shift via_elem via_sig use_core);

      sub via_list {
        my ($a, $b) = @_;
        return $a + $b;
      }
      sub via_shift {
        my $a = shift;
        my $b = shift;
        return $a - $b;
      }
      sub via_elem {
        return $_[0] * $_[1];
      }
      sub via_sig ($a, $b) {
        return $a + $b * 2;
      }
      sub use_core {
        my ($x, $y) = @_;
        return min($x, $y) + max($x, $y);
      }
      sub _private_helper {
        my ($x) = @_;
        return $x + 1;
      }
      1;
      """;
    const string contracts = """
      Sample::Math::via_list.a: i32
      Sample::Math::via_list.b: i32
      Sample::Math::via_list.return: i32
      Sample::Math::via_shift.a: i32
      Sample::Math::via_shift.b: i32
      Sample::Math::via_shift.return: i32
      Sample::Math::via_elem.arg0: i32
      Sample::Math::via_elem.arg1: i32
      Sample::Math::via_elem.return: i32
      Sample::Math::via_sig.a: i32
      Sample::Math::via_sig.b: i32
      Sample::Math::via_sig.return: i32
      Sample::Math::use_core.x: i32
      Sample::Math::use_core.y: i32
      Sample::Math::use_core.return: i32
      Sample::Math::_private_helper.x: i32
      Sample::Math::_private_helper.return: i32
      """;
    var res = ImportRaw(src, contracts);
    Assert.That(res.Diagnostics, Is.Empty, string.Join("\n", res.Diagnostics));
    var mod = res.Module!;
    Assert.That(IrVerifier.Verify(mod), Is.Empty);
    Assert.That(res.Exports, Does.Contain("Sample::Math::via_list"));
    Assert.That(res.Exports, Does.Not.Contain("Sample::Math::_private_helper"));

    Assert.That(IrInterpreter.Call(mod, "via_list", [3, 4]), Is.EqualTo(7));
    Assert.That(IrInterpreter.Call(mod, "via_shift", [10, 3]), Is.EqualTo(7));
    Assert.That(IrInterpreter.Call(mod, "via_elem", [6, 7]), Is.EqualTo(42));
    Assert.That(IrInterpreter.Call(mod, "via_sig", [3, 4]), Is.EqualTo(11));
    Assert.That(IrInterpreter.Call(mod, "use_core", [3, 9]), Is.EqualTo(12));
  }

  [Test]
  public void UnknownModule_AndEscapingClosure_AreRefused() {
    var unknown = ImportRaw("use Data::Dumper;\nsub f { return 1; }");
    Assert.That(unknown.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1733"));

    var escaping = ImportRaw("sub make_adder { use integer; my ($k) = @_; my $f = sub { my ($x) = @_; use integer; return $x + $k; }; return $f; }");
    Assert.That(escaping.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1732"));
  }

}
