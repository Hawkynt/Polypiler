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
