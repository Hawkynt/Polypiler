package Checksum;
use strict;
use warnings;
use Exporter qw(import);
our @EXPORT_OK = qw(adler32 mix_u32);

sub adler32 {
  my ($data) = @_;
  my $s1 = 1;
  my $s2 = 0;
  my $n = length($data);
  for my $i (0 .. $n - 1) {
    $s1 = ($s1 + ord(substr($data, $i, 1))) % 65521;
    $s2 = ($s2 + $s1) % 65521;
  }
  return ($s2 << 16) | $s1;
}

sub mix_u32 {
  use integer;
  my ($x, $y) = @_;
  my $z = (($x * 33) ^ $y) & 0xFFFFFFFF;
  return $z;
}

1;
