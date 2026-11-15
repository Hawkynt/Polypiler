# A module test with one assertion that cannot hold: prove has to report the failure.
use strict;
use warnings;
use Test::More tests => 1;

is(1 + 1, 3, 'one and one is three');
