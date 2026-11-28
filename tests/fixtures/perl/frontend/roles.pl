package Measurable;
use Moose::Role;
requires 'measure';

package BaseItem;
use Moose;
has 'weight' => (is => 'ro', isa => 'Int', default => 0);
__PACKAGE__->meta->make_immutable;

package Parcel;
use Moose;
extends 'BaseItem';
has '_secret_fee' => (is => 'rw', isa => 'Int', default => 5);
has 'extra' => (is => 'rw', isa => 'Int', default => 0);

sub measure {
  use integer;
  my ($self) = @_;
  return $self->weight() + $self->_secret_fee() + $self->extra();
}

sub bump_extra {
  use integer;
  my ($self, $delta) = @_;
  $self->extra($self->extra() + $delta);
  return;
}

with 'Measurable';
__PACKAGE__->meta->make_immutable;

package main;
sub run_moose {
  use integer;
  my ($w, $e) = @_;
  my $p = Parcel->new(weight => $w, _secret_fee => 10, extra => $e);
  $p->bump_extra(2);
  my $d = $p->does('Measurable') ? 1 : 0;
  return $p->measure() + $d;
}
1;
