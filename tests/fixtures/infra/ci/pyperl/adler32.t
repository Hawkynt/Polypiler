# The module test of Adler32.pm: exactly one test, run by prove.
use strict;
use warnings;
use Test::More tests => 1;
use Adler32 'adler32';

is_deeply([map { adler32($_) } '', 'a', 'abc'], [1, 6422626, 38600999], 'checksums of RFC 1950 section 8.2');
