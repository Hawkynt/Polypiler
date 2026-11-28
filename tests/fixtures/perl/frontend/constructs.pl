use strict;
use warnings;
use feature qw(try);
no warnings qw(experimental::try);

sub control_and_mem {
  my ($arr, $bytes, $s, $h, $n) = @_;
  my @list = (1, 2, 3);
  push @list, 4;
  unshift @list, 0;
  my $popped = pop @list;
  shift @list;
  my @empty;
  my %empty_h;
  my %local_h = (a => 1, b => 2);
  $local_h{c} = 3;
  my $has_a = exists $local_h{a};
  my $del_b = delete $local_h{b};
  my $kcnt = scalar(keys %local_h);
  my $last_idx = $#$arr;
  my $neg_el = $arr->[-1];
  $arr->[0] = 5;
  my $aref = \@list;
  my $lit = [10, 20];
  my $sum = 0;
  for my $v (@$arr) {
    $v += 1;
    $sum += $v;
  }
  for (0 .. 2) {
    $sum += $_;
  }
  {
    $sum += 1;
  }
  my $s2 = $s . "!" . ($s x 2);
  my $interp = "val=$n!";
  my $sub1 = substr($s, 0);
  my $sub2 = substr($s, 0, 1);
  my $o = ord($s);
  my $ch = chr(65);
  my $vb = vec($bytes, 0, 8);
  my $vs = vec($s, 0, 8);
  my $pk = pack('C', $n);
  my $u1 = unpack('C*', $bytes);
  my $u2 = unpack('C', $bytes);
  my $u3 = unpack('N', $bytes);
  my $u4 = unpack('n', $bytes);
  my $u5 = unpack('v', $bytes);
  my $u6 = unpack('Q<', $bytes);
  my $u7 = unpack('q>', $bytes);
  my $u8 = unpack('s<', $bytes);
  my $u9 = unpack('l>', $bytes);
  my $cmp = ($s eq $s2) || ($s ne $s2) || ($s lt $s2) || ($s le $s2) || ($s gt $s2) || ($s ge $s2) || (($s cmp $s2) == 0);
  my @words = ("x", "y");
  my $jn = join("-", @words);
  my $jn2 = join("-", "x", $s);
  my $r = { x => 1, y => 2 };
  $r->{x} = 10;
  my $exr = exists $r->{x};
  my $exm = exists $h->{"k"};
  $h->{"k"} = 7;
  my $hm = $h->{"k"};
  my $k_local = 3;
  my $adder = sub {
    use integer;
    my ($a) = @_;
    return $a + $k_local;
  };
  my $called = $adder->(5);
  my $out = 0;
  try {
    die "boom" if $n < 0;
    $out = $called;
  } catch ($e) {
    $out = -1;
  } finally {
    $out += 1;
  }
  return $out;
}
1;
