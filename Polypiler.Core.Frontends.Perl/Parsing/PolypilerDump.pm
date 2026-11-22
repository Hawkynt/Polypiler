package B::PolypilerDump;

use strict;
use warnings;
use B ();
use JSON::PP qw(encode_json);

sub pad_value {
    my ($pad, $index) = @_;
    return $index > 0 && $pad ? $pad->ARRAYelt($index) : undef;
}

sub want_name {
    my ($flags) = @_;
    my $w = $flags & B::OPf_WANT;
    return 'void' if $w == B::OPf_WANT_VOID;
    return 'scalar' if $w == B::OPf_WANT_SCALAR;
    return 'list' if $w == B::OPf_WANT_LIST;
    return 'unknown';
}

sub constant {
    my ($op, $pad) = @_;
    my $value = $op->sv;
    if ($value->isa('B::SPECIAL')) {
        my $pad_val = pad_value($pad, $op->targ);
        if ($pad_val && $pad_val->isa('B::SV') && !$pad_val->isa('B::SPECIAL')) {
            $value = $pad_val;
        } else {
            my $sname = $B::specialsv_name[$$value] // '';
            return { kind => 'bool', value => '1' } if $sname eq '&PL_sv_yes';
            return { kind => 'bool', value => '0' } if $sname eq '&PL_sv_no';
            return { kind => 'undef', value => '' };
        }
    }
    return unless $value && $value->isa('B::SV');

    my $flags = $value->FLAGS;
    if ($flags & B::SVf_IOK) {
        my $number = $flags & B::SVf_IVisUV ? $value->UVX : $value->IVX;
        return { kind => 'integer', value => "$number" };
    }
    if ($flags & B::SVf_NOK) {
        return { kind => 'float', value => unpack('H*', pack('d<', $value->NVX)) };
    }
    if ($flags & B::SVf_POK) {
        my $pv = $value->PV;
        my $wide = ($pv =~ /[^\x00-\xFF]/) ? 1 : 0;
        return { kind => 'string', value => $pv, wide => $wide };
    }
    return { kind => 'undef', value => '' };
}

sub decode_multideref {
    my ($op, $cv, $pad) = @_;
    my @steps;
    my @aux = eval { $op->aux_list($cv) };
    return \@steps if $@ || !@aux;

    my $i = 0;
    while ($i < @aux) {
        my $actions = $aux[$i++];
        last unless defined $actions;
        # Unpack action byte stream (6-bit shifts or B::MDEREF constants)
        while ($actions) {
            my $act = $actions & B::MDEREF_ACTION_MASK();
            my $idx_kind = $actions & B::MDEREF_INDEX_MASK();
            $actions >>= B::MDEREF_SHIFT();
            if ($act == B::MDEREF_AV_padav_aelem() || $act == B::MDEREF_AV_padsv_vivify_rv2av_aelem()
                || $act == B::MDEREF_AV_gvav_aelem() || $act == B::MDEREF_AV_gvsv_vivify_rv2av_aelem()
                || $act == B::MDEREF_AV_pop_rv2av_aelem() || $act == B::MDEREF_AV_vivify_rv2av_aelem()) {
                my $step = { kind => 'array' };
                if ($act == B::MDEREF_AV_padav_aelem() || $act == B::MDEREF_AV_padsv_vivify_rv2av_aelem()) {
                    $step->{source} = $act == B::MDEREF_AV_padav_aelem() ? 'padav' : 'padsv';
                    $step->{target} = $aux[$i++];
                } elsif ($act == B::MDEREF_AV_gvav_aelem() || $act == B::MDEREF_AV_gvsv_vivify_rv2av_aelem()) {
                    my $gv = $aux[$i++];
                    $step->{source} = 'gv';
                    $step->{symbol} = ref($gv) && $gv->can('NAME') ? $gv->NAME : "$gv";
                } elsif ($act == B::MDEREF_AV_pop_rv2av_aelem()) {
                    $step->{source} = 'pop';
                } else {
                    $step->{source} = 'chain';
                }
                if ($idx_kind == B::MDEREF_INDEX_const()) {
                    my $k = $aux[$i++];
                    $step->{indexKind} = 'const';
                    $step->{indexValue} = ref($k) && $k->can('IVX') ? "" . $k->IVX : "$k";
                } elsif ($idx_kind == B::MDEREF_INDEX_padsv()) {
                    $step->{indexKind} = 'padsv';
                    $step->{indexTarget} = $aux[$i++];
                } elsif ($idx_kind == B::MDEREF_INDEX_gvsv()) {
                    my $k = $aux[$i++];
                    $step->{indexKind} = 'gvsv';
                    $step->{indexSymbol} = ref($k) && $k->can('NAME') ? $k->NAME : "$k";
                } else {
                    $step->{indexKind} = 'expr';
                }
                push @steps, $step;
            } elsif ($act == B::MDEREF_HV_padhv_helem() || $act == B::MDEREF_HV_padsv_vivify_rv2hv_helem()
                || $act == B::MDEREF_HV_gvhv_helem() || $act == B::MDEREF_HV_gvsv_vivify_rv2hv_helem()
                || $act == B::MDEREF_HV_pop_rv2hv_helem() || $act == B::MDEREF_HV_vivify_rv2hv_helem()) {
                my $step = { kind => 'hash' };
                if ($act == B::MDEREF_HV_padhv_helem() || $act == B::MDEREF_HV_padsv_vivify_rv2hv_helem()) {
                    $step->{source} = $act == B::MDEREF_HV_padhv_helem() ? 'padhv' : 'padsv';
                    $step->{target} = $aux[$i++];
                } elsif ($act == B::MDEREF_HV_gvhv_helem() || $act == B::MDEREF_HV_gvsv_vivify_rv2hv_helem()) {
                    my $gv = $aux[$i++];
                    $step->{source} = 'gv';
                    $step->{symbol} = ref($gv) && $gv->can('NAME') ? $gv->NAME : "$gv";
                } elsif ($act == B::MDEREF_HV_pop_rv2hv_helem()) {
                    $step->{source} = 'pop';
                } else {
                    $step->{source} = 'chain';
                }
                if ($idx_kind == B::MDEREF_INDEX_const()) {
                    my $k = $aux[$i++];
                    $step->{indexKind} = 'const';
                    $step->{indexValue} = ref($k) && $k->can('PV') ? $k->PV : "$k";
                } elsif ($idx_kind == B::MDEREF_INDEX_padsv()) {
                    $step->{indexKind} = 'padsv';
                    $step->{indexTarget} = $aux[$i++];
                } elsif ($idx_kind == B::MDEREF_INDEX_gvsv()) {
                    my $k = $aux[$i++];
                    $step->{indexKind} = 'gvsv';
                    $step->{indexSymbol} = ref($k) && $k->can('NAME') ? $k->NAME : "$k";
                } else {
                    $step->{indexKind} = 'expr';
                }
                push @steps, $step;
            } elsif ($act == B::MDEREF_reload()) {
                last;
            } else {
                last;
            }
        }
    }
    return \@steps;
}

sub decode_multiconcat {
    my ($op, $cv) = @_;
    my @aux = eval { $op->aux_list($cv) };
    return undef if $@ || @aux < 3;
    my $nargs = $aux[0];
    my $const_sv = $aux[1];
    my $plain = ref($const_sv) && $const_sv->can('PV') ? $const_sv->PV : (defined $const_sv ? "$const_sv" : '');
    my @lens = @aux[2 .. $#aux];
    return { nargs => $nargs + 0, template => $plain, lengths => [ map { $_ + 0 } @lens ] };
}

sub operation {
    my ($op, $cv, $pad) = @_;
    return unless $$op;

    my $name = $op->name;
    my $node = {
        name => $name,
        target => $op->targ,
        flags => $op->flags,
        private => $op->private,
        want => want_name($op->flags),
        children => [],
    };
    if ($name eq 'const') {
        $node->{constant} = constant($op, $pad);
    } elsif ($name eq 'gv' || $name eq 'gvsv' || $name eq 'aelemfast') {
        my $glob = $op->gv;
        $glob = pad_value($pad, $op->padix) if !$glob->isa('B::GV') && $op->can('padix');
        if ($glob && $glob->isa('B::GV')) {
            $node->{symbol} = $glob->NAME;
            $node->{stash} = $glob->STASH->NAME if $glob->STASH && $glob->STASH->isa('B::HV');
        }
    } elsif ($name eq 'method_named' || $name eq 'method_super') {
        my $method = $op->meth_sv;
        $method = pad_value($pad, $op->targ) if $method && $method->isa('B::SPECIAL');
        $node->{symbol} = $method->PV if $method && $method->isa('B::PV');
    } elsif ($name eq 'anoncode') {
        my $sv = $op->sv;
        $sv = pad_value($pad, $op->targ) if $sv->isa('B::SPECIAL');
        $node->{code} = code($sv) if $sv && $sv->isa('B::CV');
    } elsif ($name eq 'multideref') {
        $node->{deref} = decode_multideref($op, $cv, $pad);
        my $ex = eval { B::OPpMULTIDEREF_EXISTS() } || 0;
        my $del = eval { B::OPpMULTIDEREF_DELETE() } || 0;
        $node->{derefOp} = 'exists' if $ex && ($op->private & $ex);
        $node->{derefOp} = 'delete' if $del && ($op->private & $del);
    } elsif ($name eq 'multiconcat') {
        $node->{concat} = decode_multiconcat($op, $cv);
    } elsif ($name eq 'multiparam' || $name eq 'argcheck') {
        my @aux = eval { $op->aux_list($cv) };
        if (!$@ && @aux) {
            $node->{aux} = [ map { ref($_) ? "$_" : (defined $_ ? $_ + 0 : 0) } @aux ];
        }
    } elsif ($op->isa('B::COP')) {
        $node->{line} = $op->line;
        $node->{integer} = ($op->hints & 1) != 0 ? 1 : 0;
        $node->{label} = $op->label if defined $op->label && length($op->label);
        $node->{package} = $op->stashpv if defined $op->stashpv;
    } elsif ($op->isa('B::PVOP')) {
        my $pv = $op->pv;
        $node->{pv} = $pv if defined $pv;
    }

    if ($op->flags & B::OPf_KIDS) {
        for (my $child = $op->first; $$child; $child = $child->sibling) {
            push @{$node->{children}}, operation($child, $cv, $pad);
        }
    }
    return $node;
}

sub pad_names {
    my ($names) = @_;
    return [map { $$_ && $_->isa('B::PADNAME') ? $_->PV : undef } $names->ARRAY];
}

sub code {
    my ($cv) = @_;
    my ($names, $pad) = $cv->PADLIST->ARRAY;
    my $proto = $cv->FLAGS & B::SVf_POK ? $cv->PV : undef;
    return {
        lexicals => pad_names($names),
        prototype => $proto,
        body => operation($cv->ROOT, $cv, $pad),
    };
}

sub collect_file_packages {
    my ($op, $pkgs) = @_;
    return unless $op && $$op;
    if ($op->isa('B::COP') && defined $op->file && $op->file eq $0) {
        $pkgs->{$op->stashpv} = 1 if defined $op->stashpv;
    }
    if ($op->flags & B::OPf_KIDS) {
        for (my $child = $op->first; $$child; $child = $child->sibling) {
            collect_file_packages($child, $pkgs);
        }
    }
}

sub collect_packages {
    my ($prefix, $seen, $packages, $file_pkgs) = @_;
    return if $seen->{$prefix}++;
    no strict 'refs';
    my $stash = \%{"${prefix}::"};
    my %subs;
    for my $name (sort keys %$stash) {
        if ($name =~ /\A([A-Za-z_][A-Za-z0-9_]*)::\z/) {
            my $sub_pkg = $prefix eq 'main' ? $1 : "${prefix}::$1";
            next if $sub_pkg eq 'main' || $sub_pkg eq 'B' || $sub_pkg eq 'B::PolypilerDump'
                 || $sub_pkg eq 'JSON' || $sub_pkg eq 'JSON::PP' || $sub_pkg eq 'CORE'
                 || $sub_pkg eq 'Moose' || $sub_pkg eq 'Moose::Role';
            collect_packages($sub_pkg, $seen, $packages, $file_pkgs);
            next;
        }
        next unless $name =~ /\A[A-Za-z_][A-Za-z0-9_]*\z/;
        next if $B::PolypilerDump::GENERATED_SUBS{"${prefix}::$name"};
        my $subroutine = *{"${prefix}::$name"}{CODE} or next;
        my $cv = B::svref_2object($subroutine);
        next unless $cv->isa('B::CV');
        next unless defined $cv->FILE && $cv->FILE eq $0 && ${$cv->ROOT};
        $subs{$name} = code($cv);
    }
    if (%subs || $file_pkgs->{$prefix}) {
        my @export = @{"${prefix}::EXPORT"};
        my @export_ok = @{"${prefix}::EXPORT_OK"};
        my @isa = @{"${prefix}::ISA"};
        if (%subs || @export || @export_ok || @isa) {
            $packages->{$prefix} = {
                functions => \%subs,
                export => \@export,
                exportOk => \@export_ok,
                isa => \@isa,
            };
        }
    }
}

sub compile {
    return sub {
        my %file_pkgs = (main => 1);
        collect_file_packages(B::main_root(), \%file_pkgs);
        my %packages;
        collect_packages('main', {}, \%packages, \%file_pkgs);
        my %main_functions = $packages{main} ? %{$packages{main}{functions}} : ();
        my @begin = map { code($_) } grep { defined $_->FILE && $_->FILE eq $0 } B::begin_av()->ARRAY;
        my ($names, $pad) = B::main_cv()->PADLIST->ARRAY;
        print encode_json({
            wire => 1,
            functions => \%main_functions,
            packages => \%packages,
            moose => \%B::PolypilerDump::MOOSE,
            program => scalar(operation(B::main_root(), B::main_cv(), $pad)),
            programLexicals => pad_names($names),
            begin => \@begin,
            stacked => B::OPf_STACKED,
            targetMy => B::OPpTARGET_MY,
            iterReversed => eval { B::OPpITER_REVERSED } || 0,
        }), "\n";
    };
}

1;
