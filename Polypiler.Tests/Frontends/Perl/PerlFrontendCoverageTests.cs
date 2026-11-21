using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Perl;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Perl;

/// <summary>Comprehensive coverage tests exercising all constructs, edge cases and refusals of <c>Polypiler.Core.Frontends.Perl</c>.</summary>
[TestFixture]
public sealed class PerlFrontendCoverageTests {

  private static PerlImportResult ImportRaw(string source, string contracts = "", IReadOnlySet<string>? exportFilter = null)
    => PerlFrontend.Import("cov.pl", source, new PerlImportOptions { Contracts = contracts, ExportFilter = exportFilter });

  [Test]
  public void AllContractTypes_ScalarOps_Shifts_Logic_AndCoreCalls() {
    const string src = """
      package Cov;
      use strict;
      use warnings;
      use Exporter qw(import);
      use List::Util qw(sum sum0 min max);
      use POSIX qw(floor ceil);
      use Carp qw(croak);
      our @EXPORT = qw(all_scalars);

      sub helper {
        use integer;
        my ($x) = @_;
        return $x + 1;
      }

      sub all_scalars {
        my ($i8, $u8, $i16, $u16, $i32, $u32, $i64, $u64, $f32, $f64, $b, $s, $opt, $rec) = @_;
        my $u;
        my $notb = !$b;
        my $xorv = ($i32 xor $u32);
        my $andv = $i32 && $u32;
        my $orv = $i32 || $u32;
        my $neg = -$f64;
        my $ineg = do { use integer; -$i32 };
        my $comp = ~$u32;
        my $icomp = do { use integer; ~$i32 };
        my $absv = abs($i32) + abs($f64);
        my $sq = sqrt($f64);
        my $nc = $i32 <=> $i32;
        my $inc = do { use integer; $i32 <=> $i32 };
        my $shl = $u32 << 2;
        my $shr = $u32 >> 1;
        my $ishr = do { use integer; $i32 >> 1 };
        my $fl = floor($f64) + ceil($f64);
        my $sm = sum($i32, 1, 2) + sum0();
        my $d = do { use integer; $i32 / 2 };
        my $m = do { use integer; $i32 % 3 };
        my $m2 = $i32 % 3;
        my $wa = wantarray ? 1 : 0;
        my $h = helper($i32);
        my $f = 1.5;
        my $bigu = 18446744073709551615;
        my $c = 0;
        $c++;
        ++$c;
        $c--;
        --$c;
        croak("bad") if $i32 == -999999;
        return $rec->{w} + $c + $wa + $h;
      }
      1;
      """;
    const string contracts = """
      # comment line
      Cov::all_scalars.i8: i8
      Cov::all_scalars.u8: u8
      Cov::all_scalars.i16: i16
      Cov::all_scalars.u16: u16
      Cov::all_scalars.i32: i32
      Cov::all_scalars.u32: u32
      Cov::all_scalars.i64: i64
      Cov::all_scalars.u64: u64
      Cov::all_scalars.f32: f32
      Cov::all_scalars.f64: f64
      Cov::all_scalars.b: bool
      Cov::all_scalars.s: str
      Cov::all_scalars.opt: optional<i32>
      Cov::all_scalars.rec: record{ w: i32, h: i32 }
      Cov::all_scalars.context: list
      Cov::all_scalars.return: i32
      Cov::helper.x: i32
      Cov::helper.return: i32
      """;
    var res = ImportRaw(src, contracts, new HashSet<string> { "all_scalars" });
    Assert.That(res.Diagnostics, Is.Empty, string.Join("\n", res.Diagnostics));
    Assert.That(res.Exports, Is.EqualTo(new[] { "Cov::all_scalars" }));
    Assert.That(IrVerifier.Verify(res.Module!), Is.Empty);
  }

  [Test]
  public void ControlFlow_Arrays_Strings_Pack_Hashes_Closures_TryFinally_AndTestMore() {
    const string src = """
      use strict;
      use warnings;
      use feature qw(try);
      no warnings qw(experimental::try);

      sub control_and_mem {
        my ($arr, $bytes, $s, $h, $n) = @_;
        my @list = (1, 2, 3);
        push @list, 4;
        unshift @list, 0;
        my $popped = pop @list;
        shift @list;
        my @empty;
        my %empty_h;
        my %local_h = (a => 1, b => 2);
        $local_h{c} = 3;
        my $has_a = exists $local_h{a};
        my $del_b = delete $local_h{b};
        my $kcnt = scalar(keys %local_h);
        my $last_idx = $#$arr;
        my $neg_el = $arr->[-1];
        $arr->[0] = 5;
        my $aref = \@list;
        my $lit = [10, 20];
        my $sum = 0;
        for my $v (@$arr) {
          $v += 1;
          $sum += $v;
        }
        for (0 .. 2) {
          $sum += $_;
        }
        {
          $sum += 1;
        }
        my $s2 = $s . "!" . ($s x 2);
        my $interp = "val=$n!";
        my $sub1 = substr($s, 0);
        my $sub2 = substr($s, 0, 1);
        my $o = ord($s);
        my $ch = chr(65);
        my $vb = vec($bytes, 0, 8);
        my $vs = vec($s, 0, 8);
        my $pk = pack('C', $n);
        my $u1 = unpack('C*', $bytes);
        my $u2 = unpack('C', $bytes);
        my $u3 = unpack('N', $bytes);
        my $u4 = unpack('n', $bytes);
        my $u5 = unpack('v', $bytes);
        my $u6 = unpack('Q<', $bytes);
        my $u7 = unpack('q>', $bytes);
        my $u8 = unpack('s<', $bytes);
        my $u9 = unpack('l>', $bytes);
        my $cmp = ($s eq $s2) || ($s ne $s2) || ($s lt $s2) || ($s le $s2) || ($s gt $s2) || ($s ge $s2) || (($s cmp $s2) == 0);
        my @words = ("x", "y");
        my $jn = join("-", @words);
        my $jn2 = join("-", "x", $s);
        my $r = { x => 1, y => 2 };
        $r->{x} = 10;
        my $exr = exists $r->{x};
        my $exm = exists $h->{"k"};
        $h->{"k"} = 7;
        my $hm = $h->{"k"};
        my $k_local = 3;
        my $adder = sub {
          use integer;
          my ($a) = @_;
          return $a + $k_local;
        };
        my $called = $adder->(5);
        my $out = 0;
        try {
          die "boom" if $n < 0;
          $out = $called;
        } catch ($e) {
          $out = -1;
        } finally {
          $out += 1;
        }
        return $out;
      }
      1;
      """;
    const string contracts = """
      control_and_mem.arr: seq<i32>
      control_and_mem.bytes: seq<u8>
      control_and_mem.s: str
      control_and_mem.h: map<str,i32>
      control_and_mem.n: i32
      control_and_mem.return: i32
      """;
    var res = ImportRaw(src, contracts);
    Assert.That(res.Diagnostics, Is.Empty, string.Join("\n", res.Diagnostics));
    Assert.That(IrVerifier.Verify(res.Module!), Is.Empty);

    // Test::More file import
    const string testMore = """
      use strict;
      use warnings;
      use Test::More;
      ok(1 == 1, "one");
      is(2 + 2, 4, "four");
      cmp_ok(3, '==', 3, "three");
      done_testing();
      """;
    var tm = ImportRaw(testMore);
    Assert.That(tm.Diagnostics, Is.Empty, string.Join("\n", tm.Diagnostics));
    Assert.That(tm.TestFunctions, Has.Count.EqualTo(1));
  }

  [Test]
  public void AllRefusals_ReportExpectedDiagnosticCodes() {
    var cases = new (string Source, string Contracts, string ExpectedCode)[] {
      ("use bigint;\nsub f { return 1; }", "", "PLY1702"),
      ("use parent 'Exporter';\nsub f { return 1; }", "", "PLY1740"),
      ("our @ISA = ('Foo');\nsub f { return 1; }", "", "PLY1740"),
      ("sub f { my ($x) = @_; return $x->foo(); }", "f.x: i32\nf.return: i32", "PLY1740"),
      ("sub AUTOLOAD { return 1; }", "", "PLY1730"),
      ("sub f ($) { return 1; }", "", "PLY1730"),
      ("BEGIN { my $x = 1; }\nsub f { return 1; }", "", "PLY1730"),
      ("print 1;\nsub f { return 1; }", "", "PLY1730"),
      ("my $x = 1;", "", "PLY1730"),
      ("sub f { $_[0] = 1; return 1; }", "", "PLY1730"),
      ("sub f { my ($x) = @_; return sin($x); }", "f.x: f64\nf.return: f64", "PLY1702"),
      ("sub f { my ($x) = @_; return $x ** 2; }", "f.x: f64\nf.return: f64", "PLY1702"),
      ("sub f { my ($x) = @_; return $x =~ /a/; }", "f.x: str\nf.return: bool", "PLY1722"),
      ("sub f { my ($x) = @_; return sprintf('%d', $x); }", "f.x: i32\nf.return: str", "PLY1722"),
      ("sub f { my ($a) = @_; splice(@$a, 0, 1); return 1; }", "f.a: list<i32>\nf.return: i32", "PLY1720"),
      ("sub f { my ($a) = @_; my @r = reverse @$a; return 1; }", "f.a: list<i32>\nf.return: i32", "PLY1720"),
      ("sub f { my ($a) = @_; my @s = sort @$a; return 1; }", "f.a: list<i32>\nf.return: i32", "PLY1720"),
      ("sub f { my ($x) = @_; return ref($x); }", "f.x: i32\nf.return: str", "PLY1721"),
      ("sub f { warn 'hi'; return 1; }", "", "PLY1742"),
      ("sub f { exit(0); }", "", "PLY1742"),
      ("sub f { return $ENV{PATH}; }", "f.return: str", "PLY1743"),
      ("sub f { return $!; }", "f.return: str", "PLY1743"),
      ("sub f { for my $c ('a' .. 'z') { } return 1; }", "", "PLY1710"),
      ("sub f { my ($x) = @_; return substr($x, 0, 1, 'z'); }", "f.x: str\nf.return: str", "PLY1722"),
      ("sub f { my ($t, $b) = @_; return unpack($t, $b); }", "f.t: str\nf.b: seq<u8>\nf.return: u32", "PLY1723"),
      ("sub f { my ($b) = @_; return unpack('', $b); }", "f.b: seq<u8>\nf.return: u32", "PLY1723"),
      ("sub f { my $r = \\&f; return 1; }", "", "PLY1732"),
      ("sub f { return unknown_sub(1); }", "", "PLY1733"),
      ("sub f { my ($x) = @_; my $r = $x; return $r; }", "f.x: record{ bad }\nf.return: i32", "PLY1700"),
    };

    foreach (var (source, contracts, expectedCode) in cases) {
      var res = ImportRaw(source, contracts);
      Assert.That(res.Diagnostics.Select(d => d.Code), Has.Some.EqualTo(expectedCode),
        $"Source: {source}\nActual: {string.Join("; ", res.Diagnostics)}");
    }
  }

  [Test]
  public void RemainingFrontendBranches_Control_Memory_Pack_Eval_Scalars_AndRefusals() {
    const string src = """
      package Sub::Pkg;
      use strict;
      use warnings;
      use feature qw(try);
      no warnings qw(experimental::try);

      sub void_sub {
        my ($n) = @_;
        return if $n == 0;
        return 1 if $n == 1;
        my $x = $n + 1;
      }

      sub implicit_ret {
        my ($n) = @_;
        $n + 1;
      }

      sub bare_ret_opt {
        my ($n) = @_;
        return if $n < 0;
        return $n;
      }

      sub bare_ret_int {
        my ($n) = @_;
        return if $n < 0;
        return $n;
      }

      sub helper_arr {
        return [10, 20];
      }

      sub helper_hash {
        my %m = (k => 42);
        return \%m;
      }

      sub remaining_ops {
        my ($arr, $bytes, $s, $h, $n, $u8, $u32, $opt, $rec1, $rec2, $mat) = @_;
        my @list = (1, 2, 3);
        my $e0 = $list[0];
        $list[0] = 9;
        for my $v (@list) {
          $v++;
        }
        my $sc_l = scalar(@list);
        my $sc_a = scalar(@$arr);
        my %lh = (a => 1, b => 2);
        my $sc_h = scalar(%lh);
        my $href = \%lh;
        my $sref = \$n;
        my $sd = $$sref;
        $arr->[0] += 1;
        my $expr_idx = $arr->[$n + 1];
        my $mval = $mat->[0]->[1];
        $mat->[0]->[1] = 2;
        my $a_el = helper_arr()->[0];
        my $h_el = helper_hash()->{k};
        my $h_ex = exists helper_hash()->{k};
        my $h_del = delete helper_hash()->{k};
        my $ch = chr($n);
        my $slen = length($s);
        my $c2 = $s . "x";
        my $ob0 = ord(substr($bytes, 0, 1));
        my $uc = unpack(' c ', $bytes);
        my $us = unpack('S<', $bytes);
        my $ul = unpack('L>', $bytes);
        my $o1 = $n > 0 ? $n : undef;
        my $o2 = $n > 0 ? undef : $n;
        my $dor_nonopt = $n // 5;
        my $int_i = int($n);
        my $op_l = $opt + 1;
        my $op_r = 1 + $opt;
        my $le_b = $n <= 5;
        my $req = ($rec1 == $rec2) || ($rec1 != $rec2);
        my $ush = $u32 << $u8;
        my $def_rec = defined($rec1);
        my $def_n = defined($n);
        my $dup_r = { x => 1, x => 2 };
        my $miss = $dup_r->{missing};
        my $dup = 1;
        {
          my $dup = 2;
          $dup += 1;
        }
        for (@$arr) {
          $_ = $_ + 1;
          $_ += 2;
        }
        my $acc = 0;
        OUTER: for my $i (0 .. 3) {
          for my $j (0 .. 3) {
            next OUTER if $j == 1;
            $acc += $i + $j;
          }
        }
        try {
          die "err" if $n < 0;
        } catch ($e) {
          die if $n < -10;
        }
        my $ev = eval {
          my $t = $n + 1;
          $t;
        };
        my $no_ret_cl = sub {
          use integer;
          my ($a) = @_;
          my $b = $a + 1;
        };
        my $cr = $no_ret_cl->(1);
        use integer;
        return $acc + $e0 + $sc_l + $sc_a + $sc_h + $sd + $expr_idx + $mval + $a_el + $slen + $ob0 + $int_i + $cr;
      }
      1;
      """;
    const string contracts = """
      Sub::Pkg::void_sub.n: i32
      Sub::Pkg::void_sub.return: void
      Sub::Pkg::implicit_ret.n: i32
      Sub::Pkg::implicit_ret.return: i32
      Sub::Pkg::bare_ret_opt.n: i32
      Sub::Pkg::bare_ret_opt.return: optional<i32>
      Sub::Pkg::bare_ret_int.n: i32
      Sub::Pkg::bare_ret_int.return: i32
      Sub::Pkg::helper_arr.return: seq<i32>
      Sub::Pkg::helper_hash.return: map<str,i32>
      Sub::Pkg::remaining_ops.arr: seq<i32>
      Sub::Pkg::remaining_ops.bytes: seq<u8>
      Sub::Pkg::remaining_ops.s: str
      Sub::Pkg::remaining_ops.h: map<str,i32>
      Sub::Pkg::remaining_ops.n: i32
      Sub::Pkg::remaining_ops.u8: u8
      Sub::Pkg::remaining_ops.u32: u32
      Sub::Pkg::remaining_ops.opt: optional<i32>
      Sub::Pkg::remaining_ops.rec1: record{ x: i32 }
      Sub::Pkg::remaining_ops.rec2: record{ x: i32 }
      Sub::Pkg::remaining_ops.mat: list<list<i32>>
      Sub::Pkg::remaining_ops.return: i32
      """;
    var res = ImportRaw(src, contracts);
    Assert.That(res.Diagnostics, Is.Empty, string.Join("\n", res.Diagnostics));
    Assert.That(IrVerifier.Verify(res.Module!), Is.Empty);

    // Remaining refusal & edge-case coverage
    var refusals = new (string Source, string Contracts, string ExpectedCode)[] {
      ("sub f { last; return 1; }", "", "PLY1711"),
      ("sub f { my ($n) = @_; for my $v ($n) { } return 1; }", "f.n: i32\nf.return: i32", "PLY1710"),
      ("sub f { my ($s) = @_; my $r = reverse $s; return 1; }", "f.s: str\nf.return: i32", "PLY1720"),
      ("sub f { my ($x) = @_; return ~$x; }", "f.x: f64\nf.return: i64", "PLY1702"),
      ("sub f { my ($s) = @_; return -$s; }", "f.s: str\nf.return: i64", "PLY1702"),
      ("sub f { my ($s) = @_; return int($s); }", "f.s: str\nf.return: i64", "PLY1702"),
      ("sub f { my ($s) = @_; return abs($s); }", "f.s: str\nf.return: i64", "PLY1702"),
      ("sub f { my ($s) = @_; $s++; return 1; }", "f.s: str\nf.return: i32", "PLY1702"),
      ("sub f { my ($x) = @_; return $x & 1; }", "f.x: f64\nf.return: i64", "PLY1702"),
      ("sub f { my ($s) = @_; return $s <=> 1; }", "f.s: str\nf.return: i64", "PLY1702"),
      ("sub f { my ($x) = @_; return $x << 1; }", "f.x: f64\nf.return: i64", "PLY1702"),
      ("sub f { my ($s) = @_; return $s + 1; }", "f.s: str\nf.return: i64", "PLY1702"),
      ("sub f { my $r = { a => 1, 'orphan' }; return 1; }", "", "PLY1741"),
      ("sub f { my ($s) = @_; my $r = { $s => 1 }; return 1; }", "f.s: str\nf.return: i32", "PLY1741"),
      ("sub f { my $fn = sub { return 1; }; $fn = sub { return 2; }; return 1; }", "", "PLY1732"),
      ("sub f { my ($r) = @_; die $r; }", "f.r: record{ x: i32 }\nf.return: void", "PLY1742"),
      ("sub f { return $::unknown_g; }", "f.return: i32", "PLY1743"),
      ("sub f { my ($s) = @_; return pack($s, 1); }", "f.s: str\nf.return: seq<u8>", "PLY1723"),
      ("sub f { my ($n) = @_; return pack('V', $n); }", "f.n: u32\nf.return: seq<u8>", "PLY1723"),
      ("sub f { my ($b) = @_; return unpack('x2 C', $b); }", "f.b: seq<u8>\nf.return: u8", "PLY1723"),
      ("sub f { my ($s) = @_; return vec($s, 0, 4); }", "f.s: str\nf.return: u8", "PLY1722"),
      ("sub f { my ($n) = @_; return ord($n); }", "f.n: i32\nf.return: i32", "PLY1722"),
      ("sub f { my ($n) = @_; return length($n); }", "f.n: i32\nf.return: i32", "PLY1722"),
      ("sub f { my ($n) = @_; return $n x 2; }", "f.n: i32\nf.return: str", "PLY1720"),
      ("sub f { my ($n) = @_; return substr($n, 0, 1); }", "f.n: i32\nf.return: str", "PLY1722"),
      ("sub f { my ($n, $s) = @_; return join($n, $s, $s); }", "f.n: i32\nf.s: str\nf.return: str", "PLY1720"),
      ("sub f { my $n = @_; return $n; }", "f.return: i32", "PLY1730"),
      ("sub f { my ($n) = @_; return ${$n + 1}; }", "f.n: i32\nf.return: i32", "PLY1721"),
      ("sub f { my ($h) = @_; my $r = $h; return $r->{a}; }", "f.h: i32\nf.return: i32", "PLY1741"),
      ("sub f { my ($h) = @_; $h->{a} = 1; return 1; }", "f.h: i32\nf.return: i32", "PLY1741"),
      ("sub f { my ($n) = @_; return $n->[0]; }", "f.n: i32\nf.return: i32", "PLY1720"),
      ("sub f { my ($r) = @_; $r->{nope} = 1; return 1; }", "f.r: record{ x: i32 }\nf.return: i32", "PLY1741"),
      ("sub f { my ($s) = @_; return $s; }", "f.s: str\nf.return: i32", "PLY1702"),
      ("my %h; tie %h, 'Foo';\nsub f { return 1; }", "", "PLY1743"),
    };
    foreach (var (source, c, expectedCode) in refusals) {
      var r = ImportRaw(source, c);
      Assert.That(r.Diagnostics.Select(d => d.Code), Has.Some.EqualTo(expectedCode),
        $"Source: {source}\nActual: {string.Join("; ", r.Diagnostics)}");
    }
  }

  [Test]
  public void DynamicHelemAelem_Defaults_Truth_Bounds_Contracts_AndCrossPackageNaming() {
    const string src = """
      use v5.36;
      use strict;
      use warnings;
      use Carp qw(croak);
      use List::Util qw(min max);

      package PkgA;
      sub dup_name {
        my ($x) = @_;
        return main::bool_to_i32($x);
      }

      package PkgB;
      sub dup_name {
        my ($x) = @_;
        return $x;
      }

      package main;

      sub bare_str { my ($n) = @_; return if $n < 0; return "ok"; }
      sub bare_bool { my ($n) = @_; return if $n < 0; return $n; }
      sub bare_f64 { my ($n) = @_; return if $n < 0; return 1.5; }
      sub bare_list { my ($n) = @_; return if $n < 0; my @a = (1); return \@a; }
      sub bare_map { my ($n) = @_; return if $n < 0; my %m = (a => 1); return \%m; }
      sub bare_opt_to_i32 { my ($opt) = @_; return $opt; }
      sub bool_to_i32 { my ($n) = @_; return $n == 0; }
      sub array_param_sub { my ($x, @rest) = @_; return $x; }
      sub empty_sub { }

      sub dynamic_and_bounds {
        my ($h, $a, $s, $n, $f, $u32, $u64, $opt, $rec, $strs) = @_;
        my $qual_call = PkgA::dup_name($n);
        my $main_call = bool_to_i32($n);
        my $ord_sub2 = ord(substr($s, $n, 2));
        my @list = (1, 2, 3);
        my $v = $h->{$s . "x"};
        $h->{$s . "x"} = 1;
        my $e = exists $h->{$s . "x"};
        my $d = delete $h->{$s . "x"};
        $a->[$n + 1] = 2;
        $list[$n + 1] = 2;
        @list = (4, 5);
        my %hm = (a => 1);
        %hm = (b => 2);
        my @empty_init = ();
        my %empty_hinit = ();
        my $empty_al = [];
        my ($p1, $p2) = (1);
        my $jn_arr = join(",", @$strs);
        my $cat_dyn = $s . $s;
        my $b_t = (1 == 1) ? 1 : 0;
        my $b_f = (1 == 2) ? 1 : 0;
        for (@$strs) {
          my $hv = $h->{$_};
        }
        my $mm = min($n, 1, 2) + max($n, 1, 2);
        my $mf = $n > 0 ? $f : $n;
        my $mu = $n > 0 ? $u64 : $u32;
        my $t1 = @list ? 1 : 0;
        my $t2 = @$a ? 1 : 0;
        my $t2b = $a ? 1 : 0;
        my $t3 = $rec ? 1 : 0;
        my $t4 = $opt ? 1 : 0;
        my $t5 = $s ? 1 : 0;
        for my $fi (0.5 .. 3.5) {
          $t1 += 0;
        }
        for my $el (@list) {
          if ($n > 0) {
            $el = 9;
          }
        }
        my $ev_str = eval { my $tmp = $s; $tmp };
        croak() if $n == -777;
        croak($n) if $n == -888;
        die $n if $n == -999;
        my $wa = wantarray ? 1 : 0;
        return $t1 + $t2 + $t3 + $t4 + $t5 + $wa;
      }
      1;
      """;
    const string contracts = """
      PkgA::dup_name.x: i32
      PkgA::dup_name.return: i32
      PkgB::dup_name.x: i32
      PkgB::dup_name.return: i32
      main::bare_str.n: i32
      main::bare_str.return: str
      bare_bool.n: i32
      bare_bool.return: bool
      bare_f64.n: i32
      bare_f64.return: f64
      bare_list.n: i32
      bare_list.return: list<i32>
      bare_map.n: i32
      bare_map.return: map<str,i32>
      bare_opt_to_i32.opt: nullable(i32)
      bare_opt_to_i32.return: i32
      bool_to_i32.n: i32
      bool_to_i32.return: i32
      empty_sub.return: void
      dynamic_and_bounds.h: map<str,i32>
      dynamic_and_bounds.a: seq<i32>
      dynamic_and_bounds.s: str
      dynamic_and_bounds.n: i32
      dynamic_and_bounds.f: f64
      dynamic_and_bounds.u32: u32
      dynamic_and_bounds.u64: u64
      dynamic_and_bounds.opt: optional<str>
      dynamic_and_bounds.rec: nullable(record{ x: i32 })
      dynamic_and_bounds.strs: seq<str>
      dynamic_and_bounds.context: scalar
      dynamic_and_bounds.return: i32
      """;
    var res = ImportRaw(src, contracts);
    Assert.That(res.Diagnostics, Is.Empty, string.Join("\n", res.Diagnostics));
    Assert.That(IrVerifier.Verify(res.Module!), Is.Empty);

    // Test::More string is(), plan(), folded bool true/false, non-assertion statement, and syntax error with line number
    const string tmSrc = """
      use strict;
      use warnings;
      use Test::More;
      plan(tests => 3);
      my $tmp = 1;
      ok(1 == 1, "yes");
      ok(1 == 2, "no");
      is("abc", "abc", "str eq");
      diag("note");
      """;
    var tm = ImportRaw(tmSrc);
    Assert.That(tm.Diagnostics, Is.Empty, string.Join("\n", tm.Diagnostics));

    var synErr = ImportRaw("use strict;\nmy $x = ;\n");
    Assert.That(synErr.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1700"));

    // Additional targeted refusals
    var moreRefusals = new (string Source, string Contracts, string ExpectedCode)[] {
      ("sub f { my ($s, $n) = @_; return $n > 0 ? $s : $n; }", "f.s: str\nf.n: i32\nf.return: i32", "PLY1702"),
      ("sub f { my ($u, $n) = @_; return $n > 0 ? $u : $n; }", "f.u: u64\nf.n: i32\nf.return: i64", "PLY1702"),
      ("sub f { my ($s, $f) = @_; return $s > 0 ? $f : $s; }", "f.s: str\nf.f: f64\nf.return: f64", "PLY1702"),
      ("sub f { my ($s, $f) = @_; return $f + $s; }", "f.s: str\nf.f: f64\nf.return: f64", "PLY1702"),
      ("sub f { my ($s, $n) = @_; return vec($s, 0, $n); }", "f.s: str\nf.n: i32\nf.return: u8", "PLY1722"),
      ("sub f { my ($b) = @_; return unpack('C2', $b); }", "f.b: seq<u8>\nf.return: u8", "PLY1723"),
      ("sub f { my ($b) = @_; return unpack('s', $b); }", "f.b: seq<u8>\nf.return: i16", "PLY1723"),
      ("sub f { my ($x) = @_; return $x; }", "f.x: map<str>\nf.return: i32", "PLY1700"),
      ("sub f { my ($x) = @_; return $x; }", "f.x: bogus_type\nf.return: i32", "PLY1700"),
      ("sub f { my ($x) = @_; return exists $x->{$x . 'a'}; }", "f.x: i32\nf.return: bool", "PLY1720"),
      ("sub f { my ($x) = @_; return delete $x->{$x . 'a'}; }", "f.x: i32\nf.return: bool", "PLY1720"),
      ("sub f { my ($x) = @_; return $x->{$x . 'a'}; }", "f.x: i32\nf.return: i32", "PLY1741"),
      ("sub f { my ($x) = @_; $x->{$x . 'a'} = 1; return 1; }", "f.x: i32\nf.return: i32", "PLY1741"),
      ("sub f { my ($x) = @_; return $x->(); }", "f.x: i32\nf.return: i32", "PLY1730"),
      ("sub f { my ($x) = @_; return keys %$x; }", "f.x: map<str,i32>\nf.return: i32", "PLY1741"),
      ("sub f { my ($a) = @_; push @$a, 1; return 1; }", "f.a: seq<i32>\nf.return: i32", "PLY1720"),
      ("sub f { my ($n) = @_; return \\($n + 1); }", "f.n: i32\nf.return: i32", "PLY1721"),
      ("sub f { return @ARGV; }", "f.return: i32", "PLY1743"),
      ("sub f { return @::other_arr; }", "f.return: i32", "PLY1721"),
      ("sub f { my ($s) = @_; use integer; return $s + 1; }", "f.s: str\nf.return: i64", "PLY1702"),
      ("sub f { my ($a, $b) = @_; return $a * $b; }", "f.a: i64\nf.b: i64\nf.return: i64", "PLY1702"),
      ("sub f { return \"\\x{100}\"; }", "f.return: str", "PLY1722"),
      ("sub f { return $_; }", "f.return: i32", "PLY1710"),
      ("sub f { return $::other_global; }", "f.return: i32", "PLY1743"),
      ("sub f { last OUTER; }", "f.return: i32", "PLY1711"),
    };
    foreach (var (source, c, expectedCode) in moreRefusals) {
      var r = ImportRaw(source, c);
      Assert.That(r.Diagnostics.Select(d => d.Code), Has.Some.EqualTo(expectedCode),
        $"Source: {source}\nActual: {string.Join("; ", r.Diagnostics)}");
    }

    // Extra valid scalar/loop/contract edge cases
    const string extraValid = """
      package Extra::Pkg;
      use strict;
      use warnings;

      sub extras {
        my ($arr, $n, $rec) = @_;
        my $u = 18446744073709551615;
        my $e = $@;
        my $o1 = $n > 0 ? undef : $n;
        my $o2 = $n > 0 ? $n : undef;
        my $ex = exists $rec->{missing};
        for (@$arr) {
          $_ += 1;
        }
        return $o1 // ($o2 // 0);
      }
      """;
    const string extraContracts = """
      Extra::Pkg::extras.arr: list<i32>
      Extra::Pkg::extras.n: i32
      Extra::Pkg::extras.rec: record{ x: i32 }
      Extra::Pkg::extras.context: scalar
      Extra::Pkg::extras.return: i32
      """;
    var ex = ImportRaw(extraValid, extraContracts);
    Assert.That(ex.Diagnostics, Is.Empty, string.Join("\n", ex.Diagnostics));
    Assert.That(IrVerifier.Verify(ex.Module!), Is.Empty);
  }

}
