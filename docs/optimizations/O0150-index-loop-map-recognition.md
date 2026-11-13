# O0150 — Recognize element-wise index loops as Map

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#281](https://github.com/Hawkynt/Polypiler/issues/281). Once implemented it is switched off with `--disable-opt O0150`.

## What it does

Most loops in the corpus transform one array into another element by element: Cipher's
`XorArrayWithByte`, `FastXorWords32` and `FastSubBytes` are `for (i = 0; i < n; i++) result[i] =
f(input[i])`. As a loop each carries its own induction variable, bounds checks and store; as `Map` it
can be fused with its neighbours, vectorized, or printed as `Select`, `map` or a comprehension in the
target. An element-wise loop is a `Map` only if no iteration can see what another one wrote. PRD §4.3
(`Map` stays recognizable and may be recovered from scalar loops), §4.5 (index loops), §8 P2. Sequence
operations are written exactly as [#195](https://github.com/Hawkynt/Polypiler/issues/195), [#194](https://github.com/Hawkynt/Polypiler/issues/194) and [#197](https://github.com/Hawkynt/Polypiler/issues/197) define them
on top of [#201](https://github.com/Hawkynt/Polypiler/issues/201); this issue adds no notation of its own.

## Example

### Before

```
loop:
  %i = Phi.u32 [0, entry], [%i2, body]
  %c = ICmp.ult.u32 %i, %n
  CondBr %c, body, exit
body:
  %ps = ElementAddr %src, %i
  %x = Load.u32 %ps
  %m = Mul.u32 %x, 2
  %y = Add.u32 %m, 1
  %pd = ElementAddr %dst, %i
  Store.u32 %pd, %y
  %i2 = Add.u32 %i, 1
  Br loop
```

### After

```
Map.u32[order=any] %dst, %src, %n, λ(%x): Add.u32 (Mul.u32 %x, 2), 1
```

## When it applies

(1) a natural loop with one exit, induction from 0 with step 1, a
loop-invariant bound evaluated once, no wrap of the induction variable; (2) exactly one store per
iteration, to `dst + i`, and no other effect ([#80](https://github.com/Hawkynt/Polypiler/issues/80): calls in the body must be proven pure);
(3) no loop-carried dependence: the only loop-carried SSA value is the index, and no load in the body
can read an element stored by another iteration — sources are disjoint from the destination
([#82](https://github.com/Hawkynt/Polypiler/issues/82)) or are the destination at index `i` exactly; (4) every access is proven in bounds for
the whole domain before the loop starts, or the accesses carry no bounds trap; otherwise the loop's
partial work before a trap could not be reproduced; (5) all spans are ordinary, non-volatile memory;
(6) no value defined in the loop is used after it; (7) if the body can trap (division, checked
arithmetic, a bounds-checked table lookup without proof), the result is marked `order=sequential`,
which obliges every later pass to keep element order and to stop at the first trap.

## How it is proven

`loop-inductive` (invariant: after `i` iterations `dst[0..i)` holds λ applied to the
original source elements, everything else unchanged), discharged by SMT for one symbolic iteration
under the alias facts; alias, bounds and effect preconditions as fact obligations; bounded
`exhaustive` interpretation for `n` ∈ 0..3 over 8-bit elements for the fixtures' bodies. A failed,
unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and reports `not applied:
proof timeout` or the failing precondition.

## When it must not fire

- Loop-carried dependence: `dst[i] = src[i] + dst[i-1]` is a prefix sum; with `src = 1 2 3` and
  `dst[-1]` taken as 0 it yields `1 3 6`, while an element-wise map of the original contents yields `1
  2 3` plus stale values.
- Shifted overlap: `dst = src + 1` makes iteration `i` read what iteration `i-1` wrote.
- A body with effects: a call that logs or counts is executed `n` times in order; `Map[order=any]`
  promises neither.
- Early exit: a `break` on a sentinel processes a data-dependent prefix.
- A trapping body under `order=any`: with a division by `src[i]`, a vectorized map would evaluate
  elements past the first zero.

## Switching it off

`--disable-opt O0150`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after loop canonicalization and after [#271](https://github.com/Hawkynt/Polypiler/issues/271),
[#273](https://github.com/Hawkynt/Polypiler/issues/273), [#277](https://github.com/Hawkynt/Polypiler/issues/277) (lower IDs claim their special cases first); enables map fusion,
bounds-check removal and vectorization.
