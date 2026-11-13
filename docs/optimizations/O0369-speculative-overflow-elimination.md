# O0369 — Remove loop overflow checks behind one preheader range guard with the checked loop as fallback

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0369`.

## What it does

[#163](https://github.com/Hawkynt/Polypiler/issues/163) relaxes `[trap Overflow]` to `[wrap]` only when the ranges prove that the operation cannot
overflow. In `x = i + bias` with a loop counter `i` in `[0, 999]` and an unknown but loop-invariant `bias`, the
proof is one comparison away: `bias ≤ MaxValue − 999`. Testing that once in the preheader lets a copy of the
loop run without the per-iteration check, and keeping the original loop for the other case means the trap still
fires exactly where it did. PRD §4.6 (guarded paths keep a valid fallback), §4.8.

## Example

### Before

```
pre:
  Br head
head:
  %i = Phi.i32 [pre: 0], [body: %n]            // range [0, 1000]
  %c = ICmp.slt.i32 %i, 1000
  Br %c, body, exit
body:
  %x = Add.i32[trap Overflow] %i, %bias        // %i in [0, 999] here
  Call.void @use(%x)
  %n = Add.i32[wrap] %i, 1
  Br head
```

### After

```
pre:
  %g = ICmp.sle.i32 %bias, 2147482648          // MaxValue − 999
  Br %g, head.fast, head
head.fast:
  ...
body.fast:
  %x.f = Add.i32[wrap] %i.f, %bias
  Call.void @use(%x.f)
  ...
head:                                          // the original loop, unchanged
  ...
```

## When it applies

(1) The loop is a natural loop with one preheader edge, one latch and one exit block; no block of it
is a handler entry, and loop and preheader lie in one exception region. (2) A covered operation is
`Add`/`Sub.<T>[trap L]` with an operand `b` defined outside the loop and the other operand `v` with a range
`[lo, hi]` from [#94](https://github.com/Hawkynt/Polypiler/issues/94) at the instruction that is narrower than the type. (3) Its guard interval for
`b`, intersected with the type's range `[Tmin, Tmax]`: `v + b` → `[Tmin − lo, Tmax − hi]`; `v − b` →
`[hi − Tmax, lo − Tmin]`; `b − v` → `[Tmin + hi, Tmax + lo]`. Intervals of several operations on the same `b`
are intersected. The result must be non-empty and must not be the whole type (then no guard is needed and
[#163](https://github.com/Hawkynt/Polypiler/issues/163) applies). (4) The guard consists of comparisons of `b` with literals joined by `And.bool`;
it has no effect and cannot trap. (5) The clone has at most 64 instructions, [#492](https://github.com/Hawkynt/Polypiler/issues/492) grants the
reservation (`guardCount` = number of comparisons), and [#155](https://github.com/Hawkynt/Polypiler/issues/155) scores guard plus fast loop strictly
below the original over the trip count it estimates. (6) In the clone only the covered operations change
contract; every other trapping instruction stays as it is.

## How it is proven

`range-facts+smt-bitvector`: for each of the three forms and each width class, [#71](https://github.com/Hawkynt/Polypiler/issues/71) proves once that `v ∈ [lo, hi]` and `b` inside the guard interval imply the operation does not overflow (5 s per obligation); the fallback is the unmodified loop. On timeout the form is not registered (`not applied: proof timeout`).

## When it must not fire

- Guard `bias ≤ 2147482649` (one too large): at `i = 999` the sum is 2147483648; the fast loop wraps to −2147483648 where the source traps.
- Raising the trap in the preheader when the guard fails: the source performs 999 `@use` calls before trapping at `i = 999`.
- A `bias` loaded inside the loop: it may change after the guard was evaluated.
- Covering `Mul.i32[trap Overflow] %i, %k` with the addition's interval: `999 × 2147483` overflows although `999 + 2147483` does not.
- A zero-trip loop: the guard must not trap or have effects, because the source executes nothing.

## Targets

pays off where a `[trap]` operation costs more than its `[wrap]` form: every target on which
[#497](https://github.com/Hawkynt/Polypiler/issues/497) lowers the trap to an explicit test, and native checked arithmetic that blocks vectorization or
unrolling. The loop is duplicated, so the cost model decides; targets whose profile prices the checked and
the unchecked form equal keep one loop, and generated source is not duplicated there.

## Switching it off

`--disable-opt O0369`; active at `--opt balanced` and `speed`; off at `--opt size`, `memory` and `none` (the rewrite duplicates the loop).

## Interactions with other passes

runs in phase `specialize` before [#161](https://github.com/Hawkynt/Polypiler/issues/161); the fast loop leaves [#497](https://github.com/Hawkynt/Polypiler/issues/497) nothing to lower for the covered operations. [#514](https://github.com/Hawkynt/Polypiler/issues/514) has already run in `optimize`; a loop it versioned can be versioned again only within [#492](https://github.com/Hawkynt/Polypiler/issues/492).
