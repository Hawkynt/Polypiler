use strict;
use warnings;

sub sum_to {
  my ($n) = @_;
  my $acc = 0;
  my $i = 1;
  while ($i <= $n) {
    $acc = $acc + $i;
    $i = $i + 1;
  }
  return $acc;
}

sub main {
  my $acc = 0;
  my $i = 1;
  while ($i <= 6) {
    $acc = $acc + $i;
    $i = $i + 1;
  }
  return $acc;
}

1;
