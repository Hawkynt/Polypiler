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
