use strict;
use warnings;
use constant {
  FLAG_A => 1,
  FLAG_B => 2,
};
sub flags {
  return FLAG_A | FLAG_B;
}
sub coalesce {
  my ($x) = @_;
  return $x // 99;
}
sub is_set {
  my ($x) = @_;
  return defined($x) ? 1 : 0;
}
sub plane_area {
  my ($w, $h) = @_;
  my $p = { width => $w, height => $h };
  return $p->{width} * $p->{height};
}
sub safe_div {
  my ($a, $b) = @_;
  my $r = 0;
  eval {
    die "zero" if $b == 0;
    $r = int($a / $b);
  };
  if ($@) {
    return -1;
  }
  return $r;
}
1;
