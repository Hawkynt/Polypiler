# 18446744073709551615 is the largest unsigned native integer; one more does not fit, so perl
# computes it in floating point (perlnumber, perlop). Under the core pragma bigint it stays exact.
use strict;
use warnings;

print 18446744073709551615 + 1, "\n";
{
  use bigint;
  print 18446744073709551615 + 1, "\n";
}
