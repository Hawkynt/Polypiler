package Base;
use strict;
use warnings;
sub new {
  my ($class, $val) = @_;
  return bless { val => $val }, $class;
}
sub score {
  my ($self) = @_;
  return $self->{val};
}

package Derived;
use strict;
use warnings;
our @ISA = ('Base');
sub new {
  my ($class, $val, $bonus) = @_;
  my $self = bless { val => $val, bonus => $bonus }, $class;
  return $self;
}
sub score {
  my ($self) = @_;
  return $self->SUPER::score() + $self->{bonus};
}

package main;
sub run_blessed {
  my ($a, $b) = @_;
  my $d = Derived->new($a, $b);
  my $is_base = $d->isa('Base') ? 1 : 0;
  return $d->score() + $is_base;
}
1;
