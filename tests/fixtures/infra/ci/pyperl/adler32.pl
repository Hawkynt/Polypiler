# Prints the Adler-32 checksum of "abc"; Adler32.pm is found through -I.
use strict;
use warnings;
use Adler32 'adler32';

print adler32('abc'), "\n";
