# The oracle of the Perl frontend's expression parser, run by the layer test only: reads one
# expression per line from standard input, compiles each as the value of a scalar assignment and
# writes the shape of perl's op tree for it, one line each - every op as (name child ...), package
# scalars as $name, constants as their value - or "error: ..." where perl refuses the expression.

use strict;
use warnings;
use B ();

binmode STDOUT, ':utf8';
binmode STDIN, ':utf8';

sub shape {
    my ($op) = @_;
    my $name = $op->name;
    my @kids;
    if ($op->flags & B::OPf_KIDS) {
        for (my $kid = $op->first; $$kid; $kid = $kid->sibling) {
            next if $kid->name eq 'pushmark';
            next if $kid->name eq 'null' && !($kid->flags & B::OPf_KIDS);
            push @kids, shape($kid);
        }
    }
    if ($name eq 'null') {
        return @kids == 1 ? $kids[0] : '(' . join(' ', 'list', @kids) . ')';
    }
    if ($name eq 'gvsv') {
        return '$' . $op->gv->NAME;
    }
    if ($name eq 'gv') {
        return '*' . $op->gv->NAME;
    }
    if ($name eq 'rv2av' && @kids == 1 && $kids[0] =~ /\A\*(.*)\z/) {
        return '@' . $1;
    }
    if ($name eq 'rv2hv' && @kids == 1 && $kids[0] =~ /\A\*(.*)\z/) {
        return '%' . $1;
    }
    if ($name eq 'const') {
        my $sv = $op->sv;
        return $sv->isa('B::SPECIAL') ? 'special' : $sv->FLAGS & B::SVf_POK ? "'" . $sv->PV . "'" : $sv->FLAGS & B::SVf_IOK ? $sv->IV : $sv->NV;
    }
    return @kids ? '(' . join(' ', $name, @kids) . ')' : $name;
}

no strict 'refs';
while (my $line = <STDIN>) {
    chomp $line;
    next unless length $line;
    my $code = eval "no strict; no warnings; sub { \$Polypiler::Oracle::result = ($line); }";
    if (!$code) {
        my $error = $@;
        $error =~ s/\s+/ /g;
        print "error: $error\n";
        next;
    }
    my $root = B::svref_2object($code)->ROOT;
    my $assign;
    my @todo = ($root);
    while (my $op = shift @todo) {
        if ($op->name eq 'sassign') {
            $assign = $op;
            last;
        }
        if ($op->flags & B::OPf_KIDS) {
            for (my $kid = $op->first; $$kid; $kid = $kid->sibling) {
                push @todo, $kid;
            }
        }
    }
    print $assign ? shape($assign->first) . "\n" : "error: no assignment\n";
}
