# O0065 — Specialize Perl scalars: undef, numeric/string duality, references and tied variables

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#779](https://github.com/Hawkynt/Polypiler/issues/779). Once implemented it is switched off with `--disable-opt O0065`.

## What it does

A Perl scalar is a number and a string at once, may be `undef`, may be a reference, and may be *tied* so
that every read and write runs user code. Numeric code in Perl leans on all of it: `"10" + 1`, integers
that silently become floats on overflow, `undef` counting as 0. A typed target needs to know which scalars
are plain integers, which are floats, which are text — and must be told when the answer is "it depends".
PRD §4.6 (Perl: `undef`, numeric/string conversion, references, tie/magic), §8 P3 ("typed numeric/array subset"), §3.3 Tier B: the Perl-as-source pairs need this proven-only typing in P3. Nothing here speculates: guarded specialization of unproven parameters is P4 work in [#778](https://github.com/Hawkynt/Polypiler/issues/778).

## Example

### Before

```
// my $sum = 0;  $sum = ($sum + $byte) % 65521;      facts: $byte : Integer([0, 255]) [Proven]
%t = Dyn.Add %sum, %byte
%s = Dyn.Mod %t, 65521
```

### After

```
%t = Add.i64[wrap] %sum, %byte            // range [0, 65775], no overflow possible
%s = URem.i64 %t, 65521                   // both operands non-negative: floor modulo = remainder
```

## When it applies

(1) Facts come from [#253](https://github.com/Hawkynt/Polypiler/issues/253) with the context already resolved by [#493](https://github.com/Hawkynt/Polypiler/issues/493). A scalar is
`Integer` only if every reaching definition is: an integer literal, an integer-valued operation on
`Integer` operands whose exact result range ([#94](https://github.com/Hawkynt/Polypiler/issues/94)) fits the profile's native integer (`ivsize`),
`int(...)`, `length`, an array count, or a `Nullable` of those. (2) Overflow: without `use integer`, a
result outside the signed range but inside the unsigned range stays an (unsigned) integer, beyond that it
becomes `f64` — the representation is then `Number`, not `Integer`, unless ranges exclude it. (3) `/`
without `use integer` is `Div.f64` even on integers (`7 / 2` = 3.5) unless the consumer is `int(...)` and
both operands are non-negative, where it becomes integer division. (4) `%` without `use integer`: result
has the sign of the right operand (`-7 % 3` = 2, `7 % -3` = −2) → floor modulo; with `use integer` →
truncating remainder as the platform's C compiler defines it (`-7 % 3` = −1), recorded in the profile. (5)
`undef` in numeric context is 0 and in string context `""`; if the profile declares warnings observable,
the specialized code still emits the warning text or the scalar stays dynamic. (6) A scalar is `Text` only
if never used numerically; mixed use keeps it dynamic unless all values are literals (then each use is
folded by the prefix rule). `==` compares numerically, `eq` as strings; they are never interchanged.
(7) `++` on a scalar that may hold a non-numeric string is the magic string increment and stays dynamic.
(8) A variable is plain storage only if no `tie` on it (or on a container it may belong to) is reachable
and it is not one of Perl's magical variables; otherwise each access keeps its call and effects
([#114](https://github.com/Hawkynt/Polypiler/issues/114)).

## How it is proven

`range-facts+effect-facts`: representation follows from the definitions' facts and Perl's documented numeric model; validated against `perl`. No solver, no timeout path.

## When it must not fire

- `9223372036854775807 + 1` is 9223372036854775808 (held as an unsigned integer) and `18446744073709551615 + 1` is 1.84467440737096e+19 (a float); under `use integer` the first is −9223372036854775808.
- `7 / 2` is 3.5; `-7 / 2` under `use integer` is −3.
- `-7 % 3` is 2 and `7 % -3` is −2; under `use integer` `-7 % 3` is −1.
- `"10abc" + 1` is 11, `"3 apples" * 2` is 6, `"abc" == 0` is true; `"1.0" == "1"` is true but `"1.0" eq "1"` is false.
- `my $u; $u + 1` is 1 (and warns under `use warnings`).
- `my $s = "Az"; $s++` gives `"Ba"`; `"zz"` → `"aaa"`; `"a9"` → `"b0"` — not a numeric increment.
- A tied scalar whose `FETCH` logs: two reads are two calls.

## Switching it off

`--disable-opt O0065`; `Kind = Required`, so it runs at every level including `--opt none`. With it disabled Perl scalars stay dynamic and a typed target reports PLY0500 or PLY0508.

## Interactions with other passes

runs in phase `specialize` after [#493](https://github.com/Hawkynt/Polypiler/issues/493) and [#253](https://github.com/Hawkynt/Polypiler/issues/253); enables all integer and float passes on Perl sources.
