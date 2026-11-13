# O0104 — Recognize trailing zero count and floor-log2 from de Bruijn multiply-and-lookup

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#583](https://github.com/Hawkynt/Polypiler/issues/583). Once implemented it is switched off with `--disable-opt O0104`.

## What it does

The fastest portable bit scan isolates the lowest set bit, multiplies by a de Bruijn constant and
indexes a small table. Chess engines, allocators and entropy coders ship it; so do compilers for CPUs
without a bit-scan instruction. The table and the multiply are dead weight on a target with `TZCNT`, and
a 32-bit multiply is the wrong tool on an 8-bit CPU. The idiom also differs from the intrinsic at zero,
where it returns the table's first entry. PRD §4.5, §19.2 (expression and algorithm level).

## Example

### Before

```
%n = Neg.u32 %x
%l = And.u32 %x, %n
%m = Mul.u32 %l, 0x077CB531
%i = LShr.u32[strict] %m, 27
%p = ElementAddr @table, %i
%r = Load.u8 %p
```

### After

```
%t = TrailingZeroCount.u32 %x
%z = ICmp.eq.u32 %x, 0
%r8 = Trunc.u8 %t
%r = Select.u8 %z, 0, %r8      ; 0 is table[0]
```

## When it applies

(1) the load's base is a constant array of exactly `w` elements whose
initializer is known and for which [#93](https://github.com/Hawkynt/Polypiler/issues/93) proves that no store can reach it; (2) the index
is `LShr(Mul(l, C), w - log2 w)` with constant `C`; (3) `l` is `And(x, Neg(x))` (trailing-zero form)
or the smear of `x` followed by the form's own post-step (`l - (l >>> 1)` or none, as the table
requires); (4) *table check*: for every `i` in `0..w-1`, `table[((2^i * C) mod 2^w) >>> (w - log2 w)]
== i` for the trailing-zero form, and the analogous check with `2^(i+1) - 1` for the smear form. The
check is complete because `l` takes only these `w` values for non-zero `x`. (5) For `x = 0` the
original reads `table[0]`; the replacement reproduces that value by a `Select` unless a fact `x ≠ 0`
removes the case.

## How it is proven

`exhaustive` over the `w` reachable indices (the table check) plus `smt-bitvector`
for step 3 (`x & -x` is zero or a power of two; the smear is `2^(k+1) - 1`) and for the zero case; the
provenance of the table is an `AliasFact`/`EffectFact` obligation. A failed, unknown or timed-out
obligation (budget 5 s each) leaves the IR unchanged and reports `not applied: proof timeout` or the
failing precondition.

## When it must not fire

- Zero input: the Before IR returns `table[0] = 0` for `x = 0`; `TrailingZeroCount.u32 0` is 32. A
  rewrite without the `Select` is wrong.
- A table with one wrong entry (index 2 holds 27): `x = 0x10000000` yields 27, the intrinsic 28.
- A table that is written elsewhere: its contents at the time of the load are unknown.
- `x & (x - 1)` instead of `x & -x` (clears the lowest bit instead of isolating it): `x = 0x00000001`
  gives index 0 → 0, but `x = 0x00000003` gives `table[(2 * C) >>> 27] = 1`, not 0.

## Switching it off

`--disable-opt O0104`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after global constant propagation has attached
initializers to constant arrays; after the rewrite the table is dead and is removed by global
dead-code elimination of [#157](https://github.com/Hawkynt/Polypiler/issues/157).
