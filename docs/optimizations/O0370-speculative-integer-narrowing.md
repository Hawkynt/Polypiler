# O0370 — Run a loop's wide arithmetic at a narrower width behind a preheader range guard on loop-invariant inputs

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0370`.

## What it does

[#161](https://github.com/Hawkynt/Polypiler/issues/161) executes an operation at 16 bits when the ranges prove operands and result fit. In `k * i` with
a loop counter `i` in `[0, 99]` and an unknown loop-invariant `k`, nothing is proven — yet for every `k` in
`[−330, 330]` the product fits `i16`, and on a 16-bit or 8-bit CPU that is the difference between one
instruction and a helper call per iteration. One range test before the loop selects a narrowed copy; the wide
loop stays for everything else. O0016 names guarded narrowing as out of its scope; this is that issue for
loop-invariant inputs. PRD §4.4 (execution width apart from semantic type), §4.6.

## Example

### Before

```
pre:
  Br head
head:
  %i = Phi.i32 [pre: 0], [body: %n]
  %s = Phi.i32 [pre: 0], [body: %s2]
  %c = ICmp.slt.i32 %i, 100
  Br %c, body, exit
body:
  %m  = Mul.i32[wrap] %k, %i                   // %i in [0, 99], %k unknown
  %s2 = Add.i32[wrap] %s, %m
  %n  = Add.i32[wrap] %i, 1
  Br head
```

### After

```
pre:
  %g1 = ICmp.sge.i32 %k, -330
  %g2 = ICmp.sle.i32 %k, 330
  %g  = And.bool %g1, %g2
  Br %g, head.fast, head
body.fast:                                     // after O0016 on the guard-edge facts
  %k16 = Trunc.i16 %k
  %i16 = Trunc.i16 %i.f
  %m16 = Mul.i16[wrap] %k16, %i16
  %m.f = SExt.i32 %m16
  %s2.f = Add.i32[wrap] %s.f, %m.f
  ...
head:                                          // the original loop, unchanged
  ...
```

## When it applies

(1) Loop shape as in `OPT-PROP-SPECOVF` (one preheader edge, one latch, one exit block, one
exception region), at most 96 instructions. (2) A candidate operation has a loop-invariant operand `b` and
another operand `v` with a finite range `[lo, hi]` at the instruction; the narrow type `N` is one of the
candidate widths of [#161](https://github.com/Hawkynt/Polypiler/issues/161) accepted by the cost profile. (3) The guard interval of `b` is the largest
interval inside `N` such that operands and the exact result lie in `N`: comparison → `N` itself;
`v + b` → `[Nmin − lo, Nmax − hi]`; `v − b` → `[hi − Nmax, lo − Nmin]`; `b − v` → `[Nmin + hi, Nmax + lo]`;
`v × b` with `lo ≥ 0`, `hi > 0` → `[⌈Nmin / hi⌉, ⌊Nmax / hi⌋]`; a multiplication whose ranged operand can be
negative is refused. Intervals of all candidate operations on `b` are intersected; the result must contain 0
and 1 and must be wider than `b`'s static range allows to prove. (4) At most two guarded invariants, at most
four comparisons. (5) Under the guard facts O0016's range-form legality holds for at least one multiplication
or two other operations (asked through the query this issue exposes on O0016). (6) [#492](https://github.com/Hawkynt/Polypiler/issues/492) grants the
reservation and [#155](https://github.com/Hawkynt/Polypiler/issues/155) scores guard plus fast loop strictly below the original.

## How it is proven

`range-facts+smt-bitvector`: [#71](https://github.com/Hawkynt/Polypiler/issues/71) proves once per operation form and width pair that `v ∈ [lo, hi]` and `b` in the guard interval imply operands and exact result in `N` (5 s per obligation); the narrowing in the clone carries O0016's own proof. On timeout the form is not registered (`not applied: proof timeout`).

## When it must not fire

- Guarding only that the operands fit: `k = 1000`, `i = 99` are both `i16` values, the product 99 000 is not; at 16 bits it wraps to −32 072.
- Interval one too wide: `k = 331`, `i = 99` gives 32 769, one above `i16`'s maximum.
- A ranged factor that can be negative: `v` in `[−99, 99]` flips the bound; `⌊Nmax / hi⌋` alone admits `k = −331` with `v = 99`.
- A guard source loaded inside the loop: checking it per element costs more than narrowing saves and reads memory the source reads later.

## Targets

only targets whose cheap register width is below the operation's width: 8-bit and 16-bit CPUs
(6502, Z80, 8086), where a 32-bit multiply or compare is a helper or a multi-instruction sequence, and
[#263](https://github.com/Hawkynt/Polypiler/issues/263) targets for 64-bit operations. On 32/64-bit hosts and on source targets with native wide
integers the cost profile prices both widths equal and nothing is versioned.

## Switching it off

`--disable-opt O0370`; active at `--opt speed` only; off at `balanced`, `size`, `memory` and `none` (the rewrite spends code size for speed).

## Interactions with other passes

runs in phase `specialize` after `OPT-PROP-SPECOVF` and immediately before [#161](https://github.com/Hawkynt/Polypiler/issues/161), which narrows the fast clone in the same phase iteration; after versioning the preheader is conditional and the loop no longer matches, so the pass does not version its own output.
