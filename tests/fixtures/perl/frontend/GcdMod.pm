package GcdMod;
use strict;
use warnings;
use Exporter 'import';
our @EXPORT_OK = qw(gcd);

sub gcd {
  my ($a, $b) = @_;
  while ($b != 0) {
    my $t = $a % $b;
    $a = $b;
    $b = $t;
  }
  return $a;
}

1;
