package Box56;
use 5.006;
use strict;
use warnings;
my %_pin;
sub new {
  my ($class, $pin) = @_;
  my $self = bless \(my $o), $class;
  $_pin{"$self"} = $pin;
  return $self;
}
sub check {
  my ($self) = @_;
  return $_pin{"$self"};
}
sub DESTROY { my ($self) = @_; delete $_pin{"$self"}; }

package BoxFH;
use strict;
use warnings;
use Hash::Util::FieldHash qw(fieldhash);
fieldhash my %_code;
sub new {
  my ($class, $code) = @_;
  my $self = bless \(my $o), $class;
  $_code{$self} = $code;
  return $self;
}
sub get_code {
  my ($self) = @_;
  return $_code{$self};
}

package main;
sub sum_boxes {
  my ($a, $b) = @_;
  my $b1 = Box56->new($a);
  my $b2 = BoxFH->new($b);
  return $b1->check() + $b2->get_code();
}
1;
