use strict; use warnings; no warnings qw(numeric portable overflow);
sub t { my $f = shift; my $r = eval { $f->() }; defined $r ? $r : ($@ =~ /^(.*?) at /)[0] }
my ($imax, $one, $zero, $m1) = (9223372036854775807, 1, 0, -1);
print "IV_MAX + 1: ", $imax + $one, "  IV_MAX * 2: ", $imax * 2, "  -IV_MAX - 2: ", -$imax - 2, "  2147483647 + 1: ", 2147483647 + $one, "\n";
print "UV_MAX + 1: ", 18446744073709551615 + $one, "\n";
{ use integer; print "use integer: IV_MAX + 1: ", $imax + $one, "  IV_MAX * 2: ", $imax * 2, "\n"; }
print "1 << 32: ", $one << 32, "  1 << 63: ", $one << 63, "  1 << 64: ", $one << 64, "  1 << 65: ", $one << 65, "  8 >> 64: ", 8 >> (64 * $one), "  1 << -1: ", $one << $m1, "  8 >> -1: ", 8 >> $m1, "  -8 >> 1: ", -8 >> $one, "\n";
{ use integer; print "use integer: -8 >> 1: ", -8 >> $one, "  -8 >> 64: ", -8 >> (64 * $one), "  1 << 64: ", $one << 64, "\n"; }
print "7 / 0: ", t(sub { 7 / $zero }), "  7 % 0: ", t(sub { 7 % $zero }), "\n";
print "MIN / -1: ", (-$imax - 1) / $m1, "  MIN % -1: ", (-$imax - 1) % $m1, "\n";
print "-7 / 3: ", -7 / (3 * $one), "  int(-7 / 3): ", int(-7 / (3 * $one)), "  -7 % 3: ", -7 % (3 * $one), "  7 % -3: ", 7 % (-3 * $one), "\n";
{ use integer; print "use integer: -7 / 3: ", -7 / (3 * $one), "  -7 % 3: ", -7 % (3 * $one), "  7 % -3: ", 7 % (-3 * $one), "\n"; }
print "int(3e9): ", int(3e9 * $one), "  int(-3e9): ", int(-3e9 * $one), "  int(1e30): ", int(1e30 * $one), "  int(NaN): ", int("nan" + 0), "  int(Inf): ", int(9**9**9), "  int(2.7): ", int(2.7 * $one), "  int(-2.7): ", int(-2.7 * $one), "\n";
print "1e30 | 0: ", (1e30 * $one) | 0, "  -1 | 0: ", $m1 | 0, "  -1.5 | 0: ", (-1.5 * $one) | 0, "  NaN | 0: ", ("nan" + 0) | 0, "\n";
print "sprintf('%.0f', 2.5): ", sprintf('%.0f', 2.5), "  sprintf('%.0f', 3.5): ", sprintf('%.0f', 3.5), "\n";
