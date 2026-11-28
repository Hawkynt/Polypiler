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
