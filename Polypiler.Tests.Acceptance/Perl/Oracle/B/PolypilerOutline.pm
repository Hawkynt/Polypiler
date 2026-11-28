package B::PolypilerOutline;

# The oracle of the Perl frontend's parser, run by the layer test only: a compiler backend
# (perl -MO=PolypilerOutline,OUT FILE) that writes, once perl has compiled FILE, the outline the
# frontend gives of the same file - the subs FILE defines, by qualified name, and the pad of every
# compilation unit: its lexicals in order of declaration and each anonymous sub's pad nested in
# braces where perl allocated it. Lexicals a unit only captures from an enclosing one are not its own.

use strict;
use warnings;
use B ();

sub pad {
    my ($cv) = @_;
    my ($names, $values) = $cv->PADLIST->ARRAY;
    my @names = $names->ARRAY;
    my @values = $values->ARRAY;
    my $out = '';
    for my $i (1 .. $#names) {
        my $name = $names[$i];
        next unless $$name && $name->isa('B::PADNAME');
        my $pv = $name->PV;
        next unless defined $pv && length $pv > 1 || defined $pv && $pv eq '&';
        next if $name->FLAGS & B::PADNAMEf_OUTER();
        if ($pv eq '&') {
            my $value = $values[$i];
            $out .= ' {' . pad($value) . ' }' if $value && $value->isa('B::CV') && ${$value->ROOT};
            next;
        }
        my $kind = $name->FLAGS & B::PADNAMEf_OUR() ? 'our' : $name->FLAGS & B::PADNAMEf_STATE() ? 'state' : 'my';
        $out .= " $kind $pv";
    }
    return $out;
}

sub subs {
    my ($stash, $seen, $subs) = @_;
    no strict 'refs';
    return if $seen->{$stash}++;
    for my $key (sort keys %{"${stash}::"}) {
        if ($key =~ /\A(.*)::\z/) {
            next if $stash eq 'main' && $1 eq 'main';
            subs($stash eq 'main' ? $1 : "${stash}::$1", $seen, $subs);
            next;
        }
        my $entry = ${"${stash}::"}{$key};
        my $code = ref $entry eq 'CODE' ? $entry : ref \$entry eq 'GLOB' ? *{$entry}{CODE} : undef;
        next unless $code;
        my $cv = B::svref_2object($code);
        next unless $cv->isa('B::CV') && !$cv->XSUB && ${$cv->ROOT};
        next if $cv->CvFLAGS & B::CVf_ANON();
        next unless defined $cv->FILE && $cv->FILE eq $0;
        my $gv = $cv->GV;
        next unless $gv && $gv->isa('B::GV');
        $subs->{$gv->STASH->NAME . '::' . $gv->NAME} = $cv;
    }
}

sub compile {
    my ($out) = @_;
    return sub {
        return unless ${^GLOBAL_PHASE} eq "CHECK" && ${B::main_root()};
        my %subs;
        subs('main', {}, \%subs);
        open my $fh, '>:utf8', $out or die "$out: $!";
        print $fh 'main:', pad(B::main_cv()), "\n";
        print $fh "sub $_:", pad($subs{$_}), "\n" for sort keys %subs;
        close $fh;
    };
}

1;
