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
