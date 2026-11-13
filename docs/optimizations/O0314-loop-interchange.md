# O0314 — Interchange two perfectly nested loops when no dependence is reversed and the inner stride shrinks

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0314`.

## What it does

A nest that walks a column-major array row by row touches memory a whole column apart on every inner step;
the same nest with its loops swapped walks adjacent elements. Translated programs get this wrong
systematically, because the source language's array layout and the target's differ (PowerBASIC and Fortran
are column-major, C and C# row-major). Swapping the loops reorders the iterations, so it is legal only when
no dependence between two iterations is reversed. PB-Compiler implements a conservative two-level
interchange; this issue converts it and its dependence test. PRD §4.8, §19.4 (affine analysis for iteration).

## Example

### Before

```
// %a: darray<u16>[rank=2, order=column], 100 x 100; the first subscript varies fastest in memory
outer.head:                              // i = 0 .. 99
  %i = Phi.u32 [pre: 0], [outer.latch: %i2]
  ...
inner.head:                              // j = 0 .. 99
  %j = Phi.u32 [outer.head: 0], [inner.body: %j2]
  ...
inner.body:
  Store.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] %a, %i, %j), 1    // inner step moves 100 elements
  %j2 = Add.u32[wrap] %j, 1
  Br inner.head
```

### After

```
outer.head:                              // j = 0 .. 99
  %j = Phi.u32 [pre: 0], [outer.latch: %j2]
  ...
inner.head:                              // i = 0 .. 99
  %i = Phi.u32 [outer.head: 0], [inner.body: %i2]
  ...
inner.body:
  Store.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] %a, %i, %j), 1    // inner step moves 1 element
  %i2 = Add.u32[wrap] %i, 1
  Br inner.head
```

## When it applies

(1) The nest is perfect and rectangular as in scope; both trip counts are `Exact` or `Symbolic`
([#350](https://github.com/Hawkynt/Polypiler/issues/350)) and no subscript expression wraps over the iteration space ([#94](https://github.com/Hawkynt/Polypiler/issues/94)). (2) The body has
no effect other than non-volatile element loads and stores ([#114](https://github.com/Hawkynt/Polypiler/issues/114), [#80](https://github.com/Hawkynt/Polypiler/issues/80)) and no
instruction that may trap ([#115](https://github.com/Hawkynt/Polypiler/issues/115)) — the swap reorders iterations, so every effect and every trap
position would move. (3) No SSA value defined in the nest is used outside it, except the two counters under
the exit-value rule in scope. (4) Accesses to different arrays are `NoAlias` ([#82](https://github.com/Hawkynt/Polypiler/issues/82)) or both loads.
(5) For accesses to the same array `UniformDependence` returns distance vectors, each normalized to be
lexicographically positive; the swap is legal iff no vector has `d_outer > 0` and `d_inner < 0`. `Unknown`
blocks. (6) Profit: the sum over all accesses of the absolute element distance per step of the inner
counter is strictly smaller after the swap — strictly, so that the pass cannot swap back on the next sweep —
and [#155](https://github.com/Hawkynt/Polypiler/issues/155) does not object.

## How it is proven

`structural+alias-facts+effect-facts+range-facts`: with uniform distances, a permutation of the loops preserves the order of every dependent iteration pair iff every distance vector stays lexicographically positive. No solver, no timeout path.

## When it must not fire

- `a[i][j] = a[i-1][j+1]` for `i` in 1..2, `j` in 0..1 on `[[1,2,3],[4,5,6],[7,8,9]]`: as written the result is `[[1,2,3],[2,3,6],[3,6,9]]`; with `j` outermost `a[2][0]` reads `a[1][1]` before it is written and becomes 5 instead of 3. Distance `(1, −1)`.
- `b` may alias `a` in `b[i][j] = a[i][j] + 1` (two pointer parameters): no distance can be computed.
- `a[i][j] = a[j][i]`: the subscripts are not uniform; the dependence distance varies over the space.
- A body that prints each element: the output order is the iteration order.
- A body with `x / d`: a trap at `(i, j) = (0, 5)` happens after 5 stores as written and after 500 after the swap (extent 100).
- `for j in 0..i`: the inner bound depends on the outer counter; the swapped nest has a different iteration set.

## Targets

Every target with a memory hierarchy or a stepped-pointer addressing advantage — in practice all
machine-code targets from the 80486 and 68030 upward, and every source target (C, C#, Rust, Java compilers
do not interchange loops at their default optimization levels). On cacheless 8-bit machines the contiguous
order still helps `OPT-LOOP-ADDRIV` (step 1 instead of a row length). The generated source has the same
shape with the two `for` lines swapped. The stride rule is the profitability test; [#155](https://github.com/Hawkynt/Polypiler/issues/155) is asked
with `as written` and `interchanged` (original first) and may veto on a profile where the changed stride has
no price.

## Switching it off

`--disable-opt O0314`; active at `--opt balanced` and `speed`; off at `size`, `memory` and `none` (the swap neither shrinks code nor memory).

## Interactions with other passes

runs in phase `optimize` after [#502](https://github.com/Hawkynt/Polypiler/issues/502) (trap-free accesses) and [#508](https://github.com/Hawkynt/Polypiler/issues/508), before [#510](https://github.com/Hawkynt/Polypiler/issues/510), `OPT-LOOP-ADDRIV` and `OPT-LOOP-TILE`; `OPT-LOOP-DISTRIBUTE`, `OPT-LOOP-TILE`, `OPT-LOOP-SKEW` and `OPT-LOOP-UNROLLJAM` use `UniformDependence`.
