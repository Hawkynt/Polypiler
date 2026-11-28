use strict;
use warnings;
sub sum_array {
  my ($arr) = @_;
  my $total = 0;
  for my $i (0 .. scalar(@$arr) - 1) {
    $total += $arr->[$i];
  }
  return $total;
}
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
sub read_le32 {
  my ($buf) = @_;
  my ($v) = unpack('V', $buf);
  return $v;
}
1;
