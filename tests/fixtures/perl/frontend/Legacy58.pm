package Legacy58;
use 5.008;
use strict;
use warnings;
use base 'Exporter';
our @EXPORT_OK = qw(combine);
use constant { BASE => 10, MULT => 3 };

sub combine {
  my ($x) = @_;
  return ($x + BASE) * MULT;
}
1;
