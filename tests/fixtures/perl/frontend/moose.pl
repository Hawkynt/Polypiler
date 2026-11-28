package BaseRole;
use Moose::Role;
requires 'base_val';

package ExtendedRole;
use Moose::Role;
with 'BaseRole';
requires 'ext_val';

package RichItem;
use Moose;
has 'ratio' => (is => 'rw', isa => 'Num', default => 1.5);
has 'tag' => (is => 'ro', isa => 'Str', reader => 'get_tag', writer => '_set_tag', default => 'ok');
has 'flag' => (is => 'rw', isa => 'Bool', default => 1);
has 'inferred_f' => (is => 'rw', default => 2.5);
has 'inferred_s' => (is => 'rw', default => 'hi');
has 'inferred_i' => (is => 'bare', default => 3);
sub _const_zero { return 0; }
sub touch { my ($self) = @_; $self->flag(1); }
sub base_val { my ($self) = @_; return $self->flag() ? 10 : 0; }
sub ext_val { my ($self) = @_; $self->_set_tag('done'); return 32; }
with 'ExtendedRole';
__PACKAGE__->meta->make_immutable;

package BaseBox;
use strict;
use warnings;
sub new { my ($class, $x) = @_; return bless { x => $x, _hidden => 1 }, $class; }
sub get_x { my ($self) = @_; return $self->{x}; }

package SubBox;
use strict;
use warnings;
our @ISA = ('BaseBox');
sub doubled { my ($self) = @_; return $self->get_x() * 2; }

package BoxQual;
use strict;
use warnings;
use Scalar::Util ();
my %_f_pin;
sub new {
  my ($class, $pin) = @_;
  my $self = bless \(my $o), $class;
  $_f_pin{Scalar::Util::refaddr($self)} = $pin;
  return $self;
}
sub pin {
  my ($self) = @_;
  return $_f_pin{Scalar::Util::refaddr($self)};
}
sub DESTROY { my ($self) = @_; delete $_f_pin{Scalar::Util::refaddr($self)}; }

package main;
sub call_role {
  my ($r) = @_;
  return $r->base_val() + $r->ext_val();
}
sub run_all {
  my ($x) = @_;
  my $item = RichItem->new(1.0, 'a', 1, 2.0, 'b', 3);
  $item->touch();
  my $item2 = RichItem->new(flag => 1);
  my $sub = SubBox->new($x);
  my $bq = BoxQual->new(10);
  return call_role($item) + $sub->doubled() + $bq->pin() + ($item2->get_tag() eq '' ? 0 : 0);
}
1;
