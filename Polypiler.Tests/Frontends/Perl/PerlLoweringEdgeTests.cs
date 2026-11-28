using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Perl;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Perl;

/// <summary>
/// The edges of the lowering of the parsed tree (#1561): every statement and expression form the
/// lowering reads, with the diagnostic it gives where the form is outside the subset.
/// </summary>
[TestFixture]
public sealed class PerlLoweringEdgeTests {

  private static PerlImportResult Import(string source, string contracts = "")
    => PerlFrontend.Import("edge.pl", source, new PerlImportOptions { Contracts = contracts });

  [TestCase("sub f { my ($a) = @_; my $x = 0; $x++ while $x < 3; $x-- until $x < 1; return $x; }", "f", 1L, 0L)]
  [TestCase("sub f { my ($a) = @_; my $s = 0; $s += $_ for 1 .. 3; return $s; }", "f", 1L, 6L)]
  [TestCase("sub f { my ($a) = @_; $a && return 1; return 2; }", "f", 1L, 1L)]
  [TestCase("sub f { my ($a) = @_; $a || return 1; return 2; }", "f", 0L, 1L)]
  [TestCase("sub f { my ($a) = @_; $a and return 1; $a or return 3; return 2; }", "f", 0L, 3L)]
  [TestCase("sub f { my ($a) = @_; my $x = 0; $a ? ($x = 1) : ($x = 2); return $x; }", "f", 1L, 1L)]
  [TestCase("sub f { my ($a) = @_; my $x = 0; $a ? ($x = 1) : 0; return $x; }", "f", 0L, 0L)]
  [TestCase("sub f { my ($a) = @_; my $x = 0; ($a && ($x = 5)); return $x; }", "f", 1L, 5L)]
  [TestCase("sub f { my ($a) = @_; return 5 if 1; return 6; }", "f", 0L, 5L)]
  [TestCase("sub f { my ($a) = @_; 0 && 5 }", "f", 0L, 0L)]
  [TestCase("sub f { my ($a) = @_; 1 || 5 }", "f", 0L, 1L)]
  [TestCase("sub f { my ($a) = @_; my $b = 0; $a && $b }", "f", 1L, 0L)]
  [TestCase("sub f { my ($a) = @_; my $b = 0; if ($a) { $a && $b; } return 7; }", "f", 1L, 7L)]
  [TestCase("sub f { my ($a) = @_; unless ($a) { return 1; } return 2; }", "f", 0L, 1L)]
  [TestCase("sub f { my ($a) = @_; unless ($a) { return 1; } else { return 2; } }", "f", 1L, 2L)]
  [TestCase("sub f { my ($a) = @_; unless ($a) { } else { return 3; } return 4; }", "f", 1L, 3L)]
  [TestCase("sub f { my ($a) = @_; my $x = 0; $a and do { $x = 1 }; return $x; }", "f", 1L, 1L)]
  [TestCase("sub f { my ($a) = @_; my $x = 0; for my $i ((1 .. 3)) { $x += $i; } return $x; }", "f", 0L, 6L)]
  [TestCase("sub f { my ($a) = @_; my $x = 0; for (my $i = 0; $i < 3; $i++) { $x++; } return $x; }", "f", 0L, 3L)]
  [TestCase("sub f { my ($a) = @_; my $x = 0; for (;;) { last; } while () { last; } return $x; }", "f", 0L, 0L)]
  [TestCase("sub f { my ($a) = @_; package Inner { } return 8; }", "f", 0L, 8L)]
  [TestCase("sub f { my ($a) = @_; my @l = (1, 2); my $n = 0; ($n) = (5); return $n; }", "f", 0L, 5L)]
  [TestCase("sub f { my ($a) = @_; my @w = qw(a b c); return scalar(@w); }", "f", 0L, 3L)]
  [TestCase("sub f { my ($a) = @_; my $x = 1 ? $a : 0; return $x; }", "f", 9L, 9L)]
  [TestCase("sub f { my ($a) = @_; return +$a; }", "f", 4L, 4L)]
  [TestCase("sub f { my ($a) = @_; return !$a ? 1 : 0; }", "f", 0L, 1L)]
  [TestCase("sub f { my ($r) = @_; my @l = (1, 2, 3); my $n = @l ? 1 : 0; return $n; }", "f", 0L, 1L)]
  [TestCase("sub f { my ($h) = @_; my %m = (a => 1); my $r = \\%m; my $s = %$r; return 1; }", "f", 0L, 1L)]
  [TestCase("sub f { my ($a) = @_; my $x = $a; $x **= 1 if 0; return $x; }", "f", 3L, 3L)]
  public void Statements_LowerWithPerlsMeaning(string source, string function, long argument, long expected) {
    var result = Import(source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    Assert.That(System.Convert.ToInt64(IrInterpreter.Call(result.Module!, function, [argument])), Is.EqualTo(expected));
  }

  [TestCase("use feature 'defer'; sub f { defer { 1 } return 1; }", "PLY1742")]
  [TestCase("sub f { my $x = 0; do { $x++ } while $x < 3; return $x; }", "PLY1710")]
  [TestCase("sub f { { 1 } continue { 2 } return 1; }", "PLY1710")]
  [TestCase("sub f { my $x = 0; while ($x) { } continue { } return 1; }", "PLY1710")]
  [TestCase("sub f { for my $x (1) { } continue { } return 1; }", "PLY1710")]
  [TestCase("sub f { for my ($k, $v) (1, 2) { } return 1; }", "PLY1710")]
  [TestCase("our $g; sub f { for $g (1 .. 2) { } return 1; }", "")]
  [TestCase("sub f { for my $x (1, 2) { } return 1; }", "PLY1710")]
  [TestCase("sub f { for (1 .. 2) { last (1); } return 1; }", "PLY1711")]
  [TestCase("sub f { my $x = do { my $y = 1; $y }; return $x; }", "PLY1702")]
  [TestCase("sub f { my $x = do { if (1) { 1 } }; return $x; }", "PLY1702")]
  [TestCase("sub f { my $x = eval { if (1) { 1 } }; return 1; }", "PLY1742")]
  [TestCase("sub f { my $x = eval { }; return 1; }", "")]
  [TestCase("sub f { my ($a) = @_; return $a < 1 < 2; }", "PLY1702")]
  [TestCase("sub f { my ($a) = @_; my $r = qr/x/; return 1; }", "PLY1722")]
  [TestCase("sub f { my ($a) = @_; return $a =~ s/a/b/; }", "PLY1722")]
  [TestCase("sub f { my ($a) = @_; return $a =~ tr/a/b/; }", "PLY1722")]
  [TestCase("sub f { my ($a) = @_; return $a =~ $a; }", "PLY1722")]
  [TestCase("sub f { my ($a) = @_; my $x = ($a = 1); return 1; }", "PLY1702")]
  [TestCase("sub f { my ($a) = @_; my $x = (1, 2); return 1; }", "PLY1702")]
  [TestCase("sub f { my ($a) = @_; my $x = local $a; return 1; }", "PLY1702")]
  [TestCase("sub f { my $x = <STDIN>; return 1; }", "PLY1702")]
  [TestCase("sub f { my $x = `ls`; return 1; }", "PLY1702")]
  [TestCase("sub f { my @a = (1); my @x = @a[0, 1]; return 1; }", "PLY1702")]
  [TestCase("sub f { my $x = (my $y); return 1; }", "PLY1702")]
  [TestCase("my $g = 1; sub f { return $g; }", "PLY1702")]
  [TestCase("my @g = (1); sub f { return $g[0]; }", "PLY1720")]
  [TestCase("my %g = (a => 1); sub f { return $g{a}; }", "PLY1741")]
  [TestCase("sub f { my %e = %ENV; return 1; }", "PLY1743")]
  [TestCase("sub f { my %e = %Foo::h; return 1; }", "PLY1743")]
  [TestCase("sub f { my $g = *STDOUT; return 1; }", "PLY1743")]
  [TestCase("sub f { my @x = @ENV; return 1; }", "PLY1743")]
  [TestCase("sub f { my @x = @Foo::a; return 1; }", "PLY1721")]
  [TestCase("sub f { return @_; }", "PLY1730")]
  [TestCase("sub f { my ($x) = @_; return -e $x; }", "PLY1702")]
  [TestCase("use integer; sub f { my $s = 'a'; $s++; return 1; }", "PLY1702")]
  [TestCase("sub f { my ($a) = @_; return $a ** 2; }", "PLY1702")]
  [TestCase("sub f { my ($a, $b) = @_; my $x = ($a .. $b); return 1; }", "PLY1702")]
  [TestCase("sub f { my ($a, $b) = @_; return $a ~~ $b; }", "PLY1702")]
  [TestCase("sub f { my ($a) = @_; my $x = $Foo::y; return 1; }", "PLY1743")]
  [TestCase("our $o; sub f { $o = 1; return 1; }", "PLY1702")]
  [TestCase("sub f { our $o = 1; return 1; }", "PLY1743")]
  [TestCase("sub f { my $s = 'a'; $s .= 1; return 1; }", "")]
  [TestCase("sub f { my ($a) = @_; my $x; ($x) = ($a); return 1; }", "")]
  [TestCase("sub f { my @a; my $n = 0; ($n, @a) = (1, 2); return 1; }", "PLY1702")]
  [TestCase("sub f { $Foo::x .= 'a'; return 1; }", "PLY1743")]
  [TestCase("sub f { my @x = (1); $x[0] .= 'a'; return 1; }", "PLY1702")]
  [TestCase("sub f { local $_ = 1; $_ .= 'a'; return 1; }", "PLY1702")]
  [TestCase("sub f { my ($r) = @_; my @x = @{$r}; return 1; }", "")]
  [TestCase("sub f { my ($r) = @_; my $n = ${ $r + 1 }; return 1; }", "PLY1721")]
  [TestCase("sub f { my ($r) = @_; my @x = @{ $r->[0] }; return 1; }", "PLY1721")]
  [TestCase("sub f { my ($r) = @_; my %x = %{ $r->[0] }; return 1; }", "PLY1721")]
  [TestCase("sub f { my ($r) = @_; my $x = &$r; return 1; }", "PLY1730")]
  [TestCase("sub f { my ($r) = @_; my $x = $$r[0] + $r->[0]{a}; return 1; }", "PLY1720")]
  [TestCase("sub f { my $x = @_[0]; return 1; }", "PLY1702")]
  [TestCase("sub f { my $x = $Foo::a[0]; return 1; }", "PLY1721")]
  [TestCase("sub f { my $x = $_[$_[0]]; return 1; }", "PLY1730")]
  [TestCase("sub f { my $x = f()->[0]; return 1; }", "PLY1720")]
  [TestCase("sub f { my %h = (a => 1); my @x = (1); my $y = $x[0] + $h{a}; $h{b} = 2; return 1; }", "")]
  [TestCase("sub f { my ($s) = @_; my $x = $s->[0]; $s->[1] = 2; return 1; }", "PLY1720")]
  [TestCase("sub f { my @a; pop; return 1; }", "PLY1720")]
  [TestCase("sub f { my @a; my $x = 1; push $x, 1; return 1; }", "PLY1720")]
  [TestCase("sub f { my @a; push @a; return 1; }", "PLY1720")]
  [TestCase("sub f { my ($a) = @_; my @r = (1) x 2; return 1; }", "PLY1720")]
  [TestCase("sub f { my ($a) = @_; my @r = qw(a) x 2; return 1; }", "PLY1720")]
  [TestCase("sub f { my ($b) = @_; my $x = ord(substr($b, 0, 1)); return $x; }", "", "f.b: seq<u8>")]
  [TestCase("sub f { my $x = undef(); return 1; }", "")]
  [TestCase("sub f { my %h; my @k = sort keys %h; return 1; }", "PLY1741")]
  [TestCase("sub f { my @a = sort { $a <=> $b } (1); return 1; }", "PLY1720")]
  [TestCase("sub f { die 'x'; }", "")]
  [TestCase("sub f { my ($a) = @_; my $x = 0; 1 && ($x = 4); return $x; }", "PLY1702")]
  [TestCase("sub f { my ($a) = @_; return (1 && $a) + (0 || $a) + (undef // $a); }", "PLY1702")]
  [TestCase("sub f { my $x = 1 + die; return 1; }", "PLY1742")]
  [TestCase("sub f { my $x = pow(2); return 1; }", "PLY1733")]
  [TestCase("sub f { my $x = lc 'a'; my $y = lc $x; return 1; }", "PLY1722")]
  [TestCase("sub f { my $x = time; return 1; }", "PLY1702")]
  [TestCase("sub f { my $f = 1; my $x = $f->(1); return 1; }", "PLY1730")]
  [TestCase("my %g; sub f { %g = (); return 1; }", "")]
  [TestCase("sub f { my $h = {}; return 1; }", "")]
  [TestCase("sub f { my $x = keys my %h; return 1; }", "PLY1702")]
  [TestCase("sub f { my %h; my @k = keys %h; return 1; }", "PLY1741")]
  [TestCase("sub f { my %h; my $x = exists $h{a}{b}; return 1; }", "PLY1720")]
  [TestCase("sub f { my %h; delete $h{a}; return 1; }", "")]
  [TestCase("sub f { my @a = (1); delete $a[0]; return 1; }", "PLY1720")]
  [TestCase("sub f { my $x = eval { 1; 2; 3 }; return 1; }", "")]
  [TestCase("sub f { my $c = 1; my $g = sub { my $d = sub { return $c; }; return $d->(); }; return $g->(); }", "")]
  [TestCase("sub f { my $c = 1; my $g = sub { my $c = 2; return $c; }; return $g->(); }", "")]
  [TestCase("sub f { my ($a) = @_; $a ? 1 : 2 }", "")]
  [TestCase("sub f { my ($a) = @_; $a ? return 1 : 2 }", "")]
  [TestCase("sub f { my ($a) = @_; $a ? die('x') : 2 }", "")]
  [TestCase("sub f { my ($a) = @_; my $x; $a ? ($x = 1) : 2 }", "PLY1702")]
  [TestCase("sub f { my ($a) = @_; $a ? ($a || 1) : 2 }", "")]
  [TestCase("sub f { my ($a) = @_; for (1) { $a ? next : 2 } return 1; }", "PLY1710")]
  [TestCase("sub f { my ($a) = @_; $a && 5 }", "")]
  [TestCase("sub f { my ($a) = @_; my $x = 0; $a || ($x = 2); return $x; }", "")]
  [TestCase("our $g; sub f { for our $g (1 .. 2) { } return 1; }", "")]
  [TestCase("sub f { my $s = 0; for my $i ((1, 2)) { $s += $i; } return $s; }", "PLY1710")]
  [TestCase("sub f { my @a = (1); @a .= 'x'; return 1; }", "PLY1722")]
  [TestCase("sub f { my %h; $h{a}{b} .= 'x'; return 1; }", "PLY1741")]
  [TestCase("sub f { my ($r) = @_; return scalar(@$r); }", "", "f.r: list<i64>")]
  [TestCase("sub f { my ($r) = @_; return scalar(@{$r}); }", "", "f.r: list<i64>")]
  [TestCase("sub f { my $r = \\$Foo::x; return 1; }", "PLY1721")]
  [TestCase("sub f { my ($r) = @_; return $#{$r}; }", "", "f.r: list<i64>")]
  [TestCase("sub f { my ($r) = @_; return $#$r; }", "", "f.r: list<i64>")]
  [TestCase("sub f { my $s = 'a'; my $x = $s->[0]; return 1; }", "PLY1720")]
  [TestCase("sub f { my ($a) = @_; return \"a${\\ 'b'}c\"; }", "PLY1721")]
  [TestCase("sub f { my ($a) = @_; return \"a@{[ 'b' ]}c\"; }", "PLY1743")]
  [TestCase("sub f { my ($a) = @_; return \"x\" . \"a$a\" . \"b\"; }", "PLY1702")]
  [TestCase("sub f { my ($a) = @_; return \"${a}b\" . 'c'; }", "PLY1702")]
  [TestCase("sub f { my @a = (1, 2); return \"@a[0]\"; }", "PLY1743")]
  [TestCase("sub f { my ($s) = @_; return substr($s, 1, 1); }", "PLY1702", "f.s: str")]
  [TestCase("sub f { my ($s) = @_; return substr($s, 1); }", "PLY1702", "f.s: str")]
  [TestCase("sub f { my ($s) = @_; return vec($s, 1, 1); }", "PLY1722", "f.s: str")]
  [TestCase("sub f { my $x = undef; return defined $x ? 1 : 0; }", "")]
  [TestCase("sub f { return undef; }", "")]
  [TestCase("sub f { my $x = pow(2, 3); return 1; }", "PLY1733")]
  [TestCase("sub f { my %h = (a => 1); return exists $h{a} ? 1 : 0; }", "")]
  [TestCase("sub f { my %h = (a => 1); my $n = keys %h; return $n; }", "")]
  [TestCase("sub f { my %h = (a => 1); my @v = values %h; return 1; }", "PLY1741")]
  [TestCase("sub f { my %h = (a => 1); my $n = values %h; return 1; }", "PLY1741")]
  [TestCase("sub f { my %h = (a => 1); my $n = each %h; return 1; }", "PLY1741")]
  [TestCase("sub f { my ($a) = @_; $a; }", "")]
  [TestCase("sub f { my ($a) = @_; 1; 2; }", "")]
  [TestCase("sub f { my ($a) = @_; \"x\"; return 1; }", "")]
  [TestCase("sub f { my ($a) = @_; 1 + 2; return 1; }", "")]
  [TestCase("sub f { my ($a) = @_; -1; return 1; }", "")]
  [TestCase("package P; sub new { my ($c) = @_; return bless { a => 1 }, $c; } sub a { my ($s) = @_; return $s->{a}; } package main; sub f { my $p = P->new; return $p->a; }", "")]
  [TestCase("package P; sub new { my ($c) = @_; my $s = bless {}, $c; return $s; } package main; sub f { my $p = P->new; return 1; }", "")]
  [TestCase("package P; sub new { my ($c, %args) = @_; return bless { %args }, $c; } package main; sub f { return 1; }", "")]
  [TestCase("package P; sub new { return bless {}, shift; } package main; sub f { return 1; }", "")]
  [TestCase("package P; sub new { my ($c) = @_; bless {}, $c } package main; sub f { return 1; }", "PLY1740")]
  [TestCase("package P; use parent -norequire, 'Q'; package Q; sub new { return bless {}, shift; } package main; sub f { return 1; }", "")]
  [TestCase("package P; our @ISA = qw(Q); package Q; sub new { my ($c) = @_; return bless {}, $c; } package main; sub f { return 1; }", "")]
  [TestCase("package P; sub new { my ($c) = @_; my $s = bless {}, $c; $s->{x}{y} = 1; return $s; } package main; sub f { return 1; }", "PLY1741")]
  [TestCase("package P; sub new { my ($c) = @_; my $s = bless { n => 0 }, $c; return $s; } sub inc { my ($s) = @_; $s->{n}++; return; } package main; sub f { my $p = P->new; $p->inc; return 1; }", "PLY1702")]
  [TestCase("sub f { my %h = (a => 1); $h{a}++; return $h{a}; }", "PLY1702")]
  [TestCase("sub f { my @a = (1); $a[0]++; return $a[0]; }", "PLY1702")]
  [TestCase("sub f { my ($r) = @_; $r->[0]++; return 1; }", "PLY1702", "f.r: list<i64>")]
  public void Forms_AreImportedOrRefused(string source, string code, string contracts = "") {
    var result = Import(source, contracts);
    if (code.Length == 0) {
      Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
      Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    } else {
      Assert.That(result.Diagnostics.Select(d => d.Code), Has.Some.EqualTo(code), string.Join("\n", result.Diagnostics));
    }
  }

  [Test]
  public void PackagesInBlocks_ExportsAndIsaFromQualifiedNames() {
    const string source = """
      package Foo {
        our @EXPORT_OK = ('a');
        sub a { return 1; }
      }
      @Foo::EXPORT_OK = ('b');
      sub Foo::b { return 2; }
      1;
      """;
    var result = Import(source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    Assert.That(result.Exports, Is.EquivalentTo(new[] { "Foo::a", "Foo::b" }));

    var isa = Import("package A; sub new { my ($c) = @_; return bless {}, $c; } package B; @B::ISA = ('A'); package main; sub f { return 1; }");
    Assert.That(isa.Diagnostics, Is.Empty, string.Join("\n", isa.Diagnostics));
  }

  [Test]
  public void MooseDefaults_OfEachKind() {
    const string source = """
      package Thing;
      use Moose;
      has [qw(a)] => (is => 'rw');
      has 'b' => (is => 'rw', default => sub { 1 });
      has 'c' => (is => 'rw', default => 2);
      has 'd' => (is => 'rw', default => 2.5);
      has 'e' => (is => 'rw', default => 'x');
      package main;
      sub f { my $t = Thing->new(c => 1); return 1; }
      """;
    var result = Import(source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
  }

  [Test]
  public void Truth_OfOptionalsListsAndArrays() {
    const string source = """
      sub f {
        my ($a) = @_;
        my $u;
        my @l = (1);
        my $r = [1];
        my $x = 0;
        $x++ if $u;
        $x++ if @l;
        $x++ if $r;
        return $x;
      }
      """;
    var result = Import(source, "f.return: i64");
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    Assert.That(System.Convert.ToInt64(IrInterpreter.Call(result.Module!, "f", [0L])), Is.EqualTo(2L));
  }

}
