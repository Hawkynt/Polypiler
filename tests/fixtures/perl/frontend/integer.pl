use strict;
use warnings;
sub add_i32 {
  use integer;
  my ($a, $b) = @_;
  return ($a + $b) & 0xffffffff;
}
sub shift_and_bit {
  use integer;
  my ($x, $n) = @_;
  return (($x << ($n & 31)) ^ ($x >> 1)) | (~$x & 0xff);
}
sub mix_float {
  my ($a, $b) = @_;
  return ($a + $b) * 0.5;
}
sub compare_ops {
  my ($a, $b) = @_;
  return ($a < $b ? 1 : ($a == $b ? 0 : -1));
}
1;
