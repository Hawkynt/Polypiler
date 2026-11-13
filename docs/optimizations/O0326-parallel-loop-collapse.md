# O0326 — Collapse a rectangular loop nest into one iteration space that can be divided among workers

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0326`.

## What it does

A 4 × 4 000 nest parallelized at its outer loop occupies four workers on a sixteen-core target; the same
nest as one loop of 16 000 iterations occupies all of them. [#958](https://github.com/Hawkynt/Polypiler/issues/958) parallelizes one loop and sees
only the outer trip count. Flattening the nest — `k = i·M + j` — gives it enough iterations to divide.
PB-Compiler plans this as parallel loop collapse and names the three obligations: rectangular bounds,
independence across the whole space, and index arithmetic that does not eat the gain. PRD §4.8, §8 P5.

## Example

### Before

```
// %a: darray<u32>[rank=2, order=row], 4 x 4;  y = 0 .. 3, x = 0 .. 3
inner.body:
  %v = Add.u32[wrap] (Mul.u32[wrap] %y, 10), %x
  Store.u32[inbounds #1] (ElemAddr.ptr<u32>[inbounds #1] %a, %y, %x), %v
```

### After

```
// k = 0 .. 15
pre:
  %base = ElemAddr.ptr<u32>[inbounds #1] %a, 0, 0
body:
  %y, %x = DivMod.u32[trunc, trap DivideByZero] %k, 4      // divisor is the constant 4: cannot trap
  %v = Add.u32[wrap] (Mul.u32[wrap] %y, 10), %x
  Store.u32[inbounds #1] (PtrAdd.ptr<u32>[unchecked] %base, %k), %v
```

## When it applies

(1) The nest is perfect and rectangular with unit steps; the outer trip count is `Exact(N)`,
the inner `Exact(M)` or `Symbolic` with `M ≥ 1` proven ([#350](https://github.com/Hawkynt/Polypiler/issues/350), [#94](https://github.com/Hawkynt/Polypiler/issues/94)) — the division needs a
non-zero divisor and the flat base address a first element. (2) `N·M` does not overflow the collapsed
counter's type (proven, or the counter is widened). (3) A flat access requires the array's storage order
and extents to make consecutive `(i, j)` adjacent: the inner counter is the fastest-varying subscript and
covers that dimension's whole extent ([#111](https://github.com/Hawkynt/Polypiler/issues/111) descriptor), so that element `(i, j)` is `k` elements
after the first ([#123](https://github.com/Hawkynt/Polypiler/issues/123): the stepped address stays inside the allocation). (4) Every store of the
body is flat; loads are flat or use the recomputed counters. (5) The body has no effect other than
non-volatile element accesses, cannot trap, carries no value between iterations, and its loads do not read
arrays it stores to except at the same element ([#114](https://github.com/Hawkynt/Polypiler/issues/114), [#82](https://github.com/Hawkynt/Polypiler/issues/82)) — the structural
preconditions of [#958](https://github.com/Hawkynt/Polypiler/issues/958), checked here so that the collapse is not made in vain. (6) The target has
`Threads` ([#259](https://github.com/Hawkynt/Polypiler/issues/259)), `N` is below the profile's thread count and `N·M` at least that count, and
[#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers `ThreadedCost(N·M·body + DivMod, threads)` to `ThreadedCost(N·M·body, N)` — the nest
parallelized at its outer loop with only `N` workers busy.

## How it is proven

`structural+range-facts+alias-facts+effect-facts`: `k ↦ (k / M, k mod M)` is a bijection from `[0, N·M)` onto the rectangle that preserves lexicographic order, so the sequential collapsed loop is the original nest; the flat address equals the two-subscript address under (3). No solver, no timeout path.

## When it must not fire

- A triangular nest (`x < y`): `k / M` and `k mod M` enumerate a rectangle, not the triangle.
- `u8` counters and a 16 × 16 nest: `N·M = 256` wraps to 0 and the collapsed loop runs zero times.
- A statement between the loops (`rowsum = 0` per row): it has no place in the collapsed body.
- A flat access on a column-major array with the outer counter as the fastest subscript: element `(y, x)` is not `k` elements from the start.
- An inner loop over half a row (`M` smaller than the row length): consecutive rows are not adjacent in `k`.
- `M = 0` at run time: `k / 0`.

## Targets

Only targets with the `Threads` capability whose profile has more threads than the outer loop
has iterations. Nowhere else: on a sequential target the collapsed loop is the nest plus a division. The
emitted source trades two readable loops for one with index arithmetic, accepted at `--opt speed` only and
only when the parallel loop that follows needs it.

## Switching it off

`--disable-opt O0326`; active at `--opt speed` on targets with `Threads`; off at `none`, `balanced`, `size` and `memory`.

## Interactions with other passes

runs in phase `specialize` immediately before [#958](https://github.com/Hawkynt/Polypiler/issues/958); [#165](https://github.com/Hawkynt/Polypiler/issues/165) turns the `DivMod` by a power-of-two `M` into a shift and a mask.
