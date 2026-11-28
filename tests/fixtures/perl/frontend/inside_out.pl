package Vault;
use strict;
use warnings;
use Scalar::Util qw(refaddr);

my %_secret;
my %label;

sub new {
  my ($class, $secret, $label) = @_;
  my $self = bless \(my $o), $class;
  $_secret{refaddr($self)} = $secret;
  $label{refaddr($self)} = $label;
  return $self;
}
sub reveal {
  my ($self, $mult) = @_;
  return $_secret{refaddr($self)} * $mult;
}
sub set_secret {
  my ($self, $v) = @_;
  $_secret{refaddr($self)} = $v;
  return;
}
sub DESTROY {
  my ($self) = @_;
  delete $_secret{refaddr($self)};
  delete $label{refaddr($self)};
}

package main;
sub test_vault {
  my ($v) = @_;
  my $box = Vault->new($v, 1);
  $box->set_secret($v + 1);
  return $box->reveal(2);
}
1;
