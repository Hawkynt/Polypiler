#!/usr/bin/env perl
# Evaluates the ADR 0005 input set with Perl's built-in operators and the POSIX module;
# a run-time error ("Illegal division by zero", "Can't take log of 0") is written as "trap".
use strict;
use warnings;
no warnings 'portable';   # 64-bit hexadecimal literals are intended
use POSIX ();

my %functions = (
  sin => sub { sin $_[0] }, cos => sub { cos $_[0] }, tan => sub { POSIX::tan($_[0]) },
  exp => sub { exp $_[0] }, log => sub { log $_[0] }, log2 => sub { POSIX::log2($_[0]) },
  log10 => sub { POSIX::log10($_[0]) }, pow => sub { $_[0] ** $_[1] }, atan2 => sub { atan2 $_[0], $_[1] },
  sinh => sub { POSIX::sinh($_[0]) }, cosh => sub { POSIX::cosh($_[0]) }, tanh => sub { POSIX::tanh($_[0]) },
  cbrt => sub { POSIX::cbrt($_[0]) }, hypot => sub { POSIX::hypot($_[0], $_[1]) },
  add => sub { $_[0] + $_[1] }, sub => sub { $_[0] - $_[1] }, mul => sub { $_[0] * $_[1] },
  div => sub { $_[0] / $_[1] }, sqrt => sub { sqrt $_[0] }, fma => sub { POSIX::fma($_[0], $_[1], $_[2]) },
);

while (my $line = <STDIN>) {
  my ($name, $class, @hex) = split ' ', $line;
  my @operands = map { unpack 'd<', pack 'Q<', hex $_ } @hex;
  my $result = eval { $functions{$name}->(@operands) };
  print defined $result ? sprintf("%016x\n", unpack 'Q<', pack 'd<', $result) : "trap\n";
}
