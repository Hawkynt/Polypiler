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
