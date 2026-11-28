package Account;
use Moose;
has '_balance' => (is => 'rw', isa => 'Int', default => 0);
sub deposit {
  use integer;
  my ($self, $amt) = @_;
  $self->_balance($self->_balance() + $amt);
  return;
}
sub balance {
  my ($self) = @_;
  return $self->_balance();
}
__PACKAGE__->meta->make_immutable;

package main;
sub run_account {
  my ($start, $add) = @_;
  my $acc = Account->new(_balance => $start);
  $acc->deposit($add);
  return $acc->balance();
}
1;
