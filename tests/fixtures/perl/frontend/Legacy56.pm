package Legacy56;
use 5.006;
use strict;
use warnings;
use Exporter ();
our @ISA = qw(Exporter);
our @EXPORT_OK = qw(bump);
use constant STEP => 7;

sub bump {
  my ($x) = @_;
  return $x + STEP;
}
1;
