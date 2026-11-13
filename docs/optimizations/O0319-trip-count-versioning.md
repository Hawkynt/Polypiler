# O0319 — Select between loop versions by a run-time test of the trip count

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0319`.

## What it does

A vectorized or unrolled loop has a fixed cost before its first element — setup, alignment, the remainder
dispatch — that a three-element call never earns back, while a 4 000-element call wants exactly that form.
When the trip count is only known at run time, both callers go through the same code, and the cost model
has to pick one form for an unknown count (its `unknownTripCount`). Emitting both and choosing with one
compare serves both. PB-Compiler plans this as run-time trip-count versioning; it is the profitability twin
of the legality-driven `OPT-LOOP-VERSION`. PRD §4.8, §23.5.

## Example

### Before

```
pre:
  Br head
head:                                    // i = 0 .. n-1, n unknown
  ...
body:
  %x = Load.u8[inbounds #1] (ElemAddr.ptr<u8>[inbounds #1] %a, %i)
  %y = Add.u8[wrap] %x, 1
  Store.u8[inbounds #2] (ElemAddr.ptr<u8>[inbounds #2] %c, %i), %y
  ...
```

### After

```
pre:
  %short = ICmp.ult.u32 %n, 16
  Br %short, short.head, long.head
short.head:                              // identical loop; fact n < 16
  ...
long.head:                               // identical loop; fact n >= 16
  ...
exit:                                    // phis join the two versions' live-out values
```

## When it applies

(1) The loop has a preheader, a single exit and a trip count `Symbolic(v, offset)` whose value
is available in the preheader ([#350](https://github.com/Hawkynt/Polypiler/issues/350)). (2) The guard is a pure comparison of that value with a
constant; both successors are exact clones, so whichever is chosen computes what the original computed —
the rewrite needs no fact about the body. (3) Values live after the loop are joined by phis at the exit.
(4) The proven range of the trip count ([#94](https://github.com/Hawkynt/Polypiler/issues/94)) contains values on both sides of `n0`; otherwise one
clone would be dead. (5) The loop has at most 64 instructions and has not been versioned before (a loop
whose preheader already ends in this pass's guard is skipped). (6) The profile offers a blocked form
(`simd` or `swar`), the crossover search finds `n0`, and the open question below is settled so that the
long clone is in fact transformed.

## How it is proven

`structural`: a two-way branch between identical copies. No solver, no timeout path.

## When it must not fire

- A guard computed from the bound instead of the trip count (`i <= n` loops run `n + 1` times): at `n = n0 − 1` the wrong version is chosen — harmless for results, but the crossover is off by one; with an inclusive bound at the type maximum `n + 1` wraps to 0.
- Versioning a loop that [#510](https://github.com/Hawkynt/Polypiler/issues/510) then refuses for a legality reason: two identical loops and a compare — larger and slower than the original.
- Versioning again on the next sweep: each clone would be split once more.
- A proven range `n ≤ 8` with `n0 = 16`: the long clone is unreachable.
- `--opt size`: the loop exists twice.

## Targets

Targets with a blocked form in their cost profile (`simd` or `swar`): 32- and 64-bit machine
targets, WebAssembly SIMD, managed targets with vector types. Not targets without one — 6502, Z80,
tokenized BASIC — where there is nothing to select between. On mature source targets the target compiler
versions its own vector loops by trip count; there the rewrite duplicates source the reader has to wade
through, so `emits: source` profiles get it at `--opt speed` only.

## Switching it off

`--disable-opt O0319`; active at `--opt speed`, and at `balanced` on profiles that are not `emits: source` when the cost model approves; off at `size`, `memory` and `none`.

## Interactions with other passes

runs in phase `optimize` after [#508](https://github.com/Hawkynt/Polypiler/issues/508) and [#514](https://github.com/Hawkynt/Polypiler/issues/514) and before [#510](https://github.com/Hawkynt/Polypiler/issues/510) and [#513](https://github.com/Hawkynt/Polypiler/issues/513), which transform the two clones according to their range facts.
