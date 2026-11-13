# O0404 — Vectorize loop bodies with a per-element conditional as a lane mask and select

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0404`.

## What it does

`if (a[i] > b[i]) c[i] = a[i]; else c[i] = b[i];` is the most common reason a simple loop is not
vectorized: the body branches, and [#510](https://github.com/Hawkynt/Polypiler/issues/510) accepts only straight-line bodies. A per-element branch
does not vectorize as a branch; it vectorizes as a mask — compare all lanes, compute both arms, select per
lane. That is legal only when both arms may be executed for every element, which is exactly what a branch
is often there to prevent. PRD §4.8 (SIMD vectorization), §8 P3.

## Example

### Before

```
loop:                                         // i = 0 .. n-1 over i16 arrays
  %x = Load.i16[inbounds #1] %a, %i
  %y = Load.i16[inbounds #2] %b, %i
  %g = ICmp.sgt.i16 %x, %y
  Br %g, then, else
then:
  Store.i16[inbounds #3] %c, %i, %x
  Br latch
else:
  Store.i16[inbounds #3] %c, %i, %y
  Br latch
```

### After

```
vloop:                                        // i = 0, 8, .. while i + 8 <= n
  %vx = VecLoad.vec<i16, 8>[inbounds #4] %a, %i
  %vy = VecLoad.vec<i16, 8>[inbounds #5] %b, %i
  %m  = ICmp.sgt.vec<i16, 8> %vx, %vy         // mask<8>, same predicate and signedness
  %vz = Select.vec<i16, 8> %m, %vx, %vy
  VecStore.vec<i16, 8>[inbounds #6] %c, %i, %vz
tail:                                         // the original branching loop
```

## When it applies

All conditions of [#510](https://github.com/Hawkynt/Polypiler/issues/510) other than the straight-line body, and: (1) the conditional region is a diamond
or triangle whose blocks have no other predecessors and whose exits rejoin inside the iteration.
(2) **Both arms are speculable:** every instruction in an arm is free of effects and cannot trap for any
element, *without using the branch condition as a fact* — a division whose divisor is non-zero only on the
taken path, an access whose `[inbounds #k]` proof lists the branch condition among its facts
([#121](https://github.com/Hawkynt/Polypiler/issues/121) proof table), and `[trap …]` arithmetic are not speculable ([#115](https://github.com/Hawkynt/Polypiler/issues/115), [#94](https://github.com/Hawkynt/Polypiler/issues/94)).
(3) The comparison is lifted with the same predicate: `ICmp.slt` stays signed, `ICmp.ult` unsigned,
`FCmp` keeps its ordered or unordered predicate ([#116](https://github.com/Hawkynt/Polypiler/issues/116)); the mask lane is that comparison's `bool`
([#112](https://github.com/Hawkynt/Polypiler/issues/112)). (4) A conditional value becomes `Select.vec` with the arms in source order; no arm is
re-associated or contracted. (5) A two-armed store must address the same element with the same contract
in both arms. (6) A one-armed store writes the old value back in lanes where the mask is false; this needs
the stored region to be non-volatile, readable at the stored element, and unobserved by anything else
during the loop (`NoEscape`/`ArgEscape` per [#82](https://github.com/Hawkynt/Polypiler/issues/82), or a module declared single-threaded). (7) A
conditional reduction is admitted for `[wrap]` integer `Add` (and for counting); the neutral element 0 is
selected in false lanes. Floating-point conditional sums stay scalar like every float reduction.
(8) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prices compare, both arms and select below the scalar loop. (9) The scalar remainder loop this rule leaves behind is marked `vector-remainder` in its loop metadata and is never a candidate of this rule again.

## How it is proven

`effect-facts+range-facts+alias-facts+structural`: with speculable arms, computing both and selecting per lane yields the value the taken arm would have produced; stores of unchanged values are unobservable under (6); the remaining argument is O0020's. No solver, no timeout path.

## When it must not fire

- `if (b[i] != 0) c[i] = a[i] / b[i]` with `b = [2, 0]`: the source stores `c[0]` and skips element 1; a vector divide traps on lane 1.
- `if (i < len2) c[i] = t[i]` where the proof of `t[i]` is the branch itself: loading the whole vector reads `t` out of bounds.
- Signedness: `u8` 200 > 100 is true; lifted as a signed compare it is −56 > 100, false.
- Float maximum: `a = NaN, b = 1.0` — `a > b` is false, the source stores 1.0; with `a = 1.0, b = NaN` it stores NaN. A lane-wise maximum instruction that returns the non-NaN operand, or always its second operand, gets one of the two wrong. With `a = +0.0, b = −0.0` the source stores −0.0, a maximum +0.0.
- `if (a[i] < 0) a[i] = Neg.i16[trap Overflow] a[i]` with `a[i] = −32768`: speculating the negation is fine only for the `[wrap]` contract.
- A one-armed store into a buffer another thread reads and updates: writing the old value back overwrites the other thread's update.
- `if (a[i] == k) s = s + w[i]` in `f64`: lane accumulators re-associate the sum.

## Switching it off

`--disable-opt O0404`; active at `--opt balanced` and `speed`, off at `--opt none`, `size` and `memory`.

## Targets

every target that executes logical vectors with compares and selects natively or through a vector API. On SWAR-only profiles lane compares are scalarized ([#511](https://github.com/Hawkynt/Polypiler/issues/511) excludes them) and the cost model refuses. C, C++ and Rust output keeps the branching loop: GCC and LLVM if-convert and vectorize it themselves, and explicit mask code would be less readable there for no gain.

## Interactions with other passes

runs in phase `optimize` in the vectorization group, directly after [#510](https://github.com/Hawkynt/Polypiler/issues/510) (it handles loops O0020 reported with a branching body) and after [#502](https://github.com/Hawkynt/Polypiler/issues/502), whose proof records tell it which accesses depend on the branch; uses the lane-width map of `OPT-VEC-LANEWIDTH` for mixed-width bodies; output goes to [#499](https://github.com/Hawkynt/Polypiler/issues/499).
