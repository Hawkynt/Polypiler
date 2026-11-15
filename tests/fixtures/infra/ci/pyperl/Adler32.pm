# Adler-32 as RFC 1950 section 8.2 defines it: two sums modulo 65521, packed as (b << 16) | a.
package Adler32;

use strict;
use warnings;
use Exporter 'import';

our @EXPORT_OK = ('adler32');

use constant MOD_ADLER => 65521;

sub adler32 {
  my ($data) = @_;
  my ($a, $b) = (1, 0);
  for my $byte (unpack 'C*', $data) {
    $a = ($a + $byte) % MOD_ADLER;
    $b = ($b + $a) % MOD_ADLER;
  }
  return ($b << 16) | $a;
}

1;
