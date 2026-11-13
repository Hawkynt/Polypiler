# O0156 — Normalize Perl map and grep into the shared iteration IR

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#785](https://github.com/Hawkynt/Polypiler/issues/785). Once implemented it is switched off with `--disable-opt O0156`.

## What it does

Perl's `map { … } @list` and `grep { … } @list` are its element pipelines, and they are further from the
textbook operations than they look: `$_` is an alias of the element, so a block that assigns to it
rewrites the source array; a `map` block returns a list, so one element may produce none or several; and
both operators return a count in scalar context. Without normalization a Perl kernel reaches other
targets as closure calls over dynamically typed scalars. PRD §4.5 (Perl `map/grep`), §4.6 (context), §8
P4, §17.3. Calls are matched as Source Semantic IR `SourceCall` nodes carrying the resolved library
identity and signature — never by method name text. The result consists of the sequence operations of
[#201](https://github.com/Hawkynt/Polypiler/issues/201) (`SeqOf`, `Filter.seq`, `Map.seq`, `Reduce[order: left | assoc]`, `Zip`, `Materialize`,
`SeqLen`), written as that issue defines them.

## Example

### Before

```
%g = SourceCall "perl:grep(BLOCK, LIST)[list]" %xs, λ($_): FCmp.ogt.f64 $_, 6.0
%m = SourceCall "perl:map(BLOCK, LIST)[list]" %g, λ($_): FMul.f64 $_, 2.0
%s = SourceCall "List::Util::sum0(LIST)" %m
```

### After

```
%s0 = SeqOf %xs
%f = Filter.seq<i64> %s0, λ(%x): ICmp.sgt.i64 %x, 6
%m = Map.seq<i64> %f, λ(%x): Mul.i64 %x, 2
%s = Reduce.i64[order: left] %m, λ(%a, %x): Add.i64[perl-iv] %a, %x, 0
```

## When it applies

(1) the operator is the built-in `map`/`grep`, or the function resolves to
core `List::Util`; (2) the block reads `$_` but never writes it (no assignment, no `s///`, `tr///`,
`chomp`, no reference taken) — because `$_` aliases the source element; (3) the block is pure and, for
`map`, provably yields exactly one scalar in list context; (4) the calling context of the operator is
known (list or scalar) and selects the result form; (5) the element representation is the one
[#253](https://github.com/Hawkynt/Polypiler/issues/253) proved: the After IR above shows the case where the elements are proven integers within
the native integer range, so comparison and multiplication are integer operations and addition carries
Perl's overflow rule (`perl-iv`: promotion to a floating-point value on overflow, as the frontend's
numeric profile defines it); with only a numeric proof the operations stay `f64`; (6) the list is an
array or range without tie magic.

## How it is proven

`validated-rule+differential`: each mapping is a registry entry in the sense of PRD
§17.3 (library identity, version range, signature, semantics, throws, effects) with checked
preconditions; purity and single-enumeration are `EffectFact` obligations; equivalence is established
by differential execution against the real runtime on the fixtures and on generated inputs. No SMT
claim is made. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Aliasing: `map { $_ *= 2 } @xs` doubles every element of `@xs` itself; after it `@xs = (5, 12)` is
  `(10, 24)`. A pure `Map` leaves `@xs` unchanged.
- List-returning block: `map { ($_, $_ * 2) } (1, 2)` is `(1, 2, 2, 4)` — four elements from two.
- Scalar context: `my $n = grep { $_ > 6 } @xs` is the number 3, not a list.
- String comparison: `grep { $_ gt '6' } ('10', '9')` keeps only `'9'` (string order); numeric `>`
  keeps both.
- Empty list: `sum()` is `undef`, `sum0()` is 0.

## Switching it off

`--disable-opt O0156`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` on Source Semantic IR after context resolution in the
frontend and after de-dynamicization facts are available; produces IR equal, under the correspondence
that [#195](https://github.com/Hawkynt/Polypiler/issues/195), [#194](https://github.com/Hawkynt/Polypiler/issues/194) and [#197](https://github.com/Hawkynt/Polypiler/issues/197) state between their span forms and the
sequence forms of [#201](https://github.com/Hawkynt/Polypiler/issues/201), to what the index-loop rules produce for the same computation.
