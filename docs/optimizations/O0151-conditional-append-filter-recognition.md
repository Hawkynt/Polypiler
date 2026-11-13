# O0151 — Recognize conditional-append loops as Filter

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#280](https://github.com/Hawkynt/Polypiler/issues/280). Once implemented it is switched off with `--disable-opt O0151`.

## What it does

Selecting the elements that satisfy a predicate is written as a loop with a second cursor: `for (i = 0;
i < n; i++) if (p(src[i])) out[k++] = src[i]`. That is `Where`, `filter`, `grep`, a comprehension with
`if`. Recognized as `Filter`, the selection can be fused with a following map or reduction and printed
in the target's own idiom; left as a loop, the data-dependent output cursor blocks every later loop
optimization. PRD §4.3, §4.5, §8 P2. Sequence operations are written exactly as [#195](https://github.com/Hawkynt/Polypiler/issues/195),
[#194](https://github.com/Hawkynt/Polypiler/issues/194) and [#197](https://github.com/Hawkynt/Polypiler/issues/197) define them on top of [#201](https://github.com/Hawkynt/Polypiler/issues/201); this issue adds no
notation of its own.

## Example

### Before

```
loop:
  %i = Phi.u32 [0, entry], [%i2, latch]
  %k = Phi.u32 [0, entry], [%k3, latch]
  %c = ICmp.ult.u32 %i, %n
  CondBr %c, body, exit
body:
  %ps = ElementAddr %src, %i
  %x = Load.u32 %ps
  %keep = ICmp.ugt.u32 %x, 6
  CondBr %keep, take, latch
take:
  %po = ElementAddr %out, %k
  Store.u32 %po, %x
  %k2 = Add.u32 %k, 1
  Br latch
latch:
  %k3 = Phi.u32 [%k2, take], [%k, body]
  %i2 = Add.u32 %i, 1
  Br loop
exit:
  Ret %k
```

### After

```
%f = Filter.u32[order=any] %out, %src, %n, λ(%x): ICmp.ugt.u32 %x, 6
%k = Count %f
Ret %k
```

## When it applies

(1) natural loop, one exit, unit step from 0, loop-invariant bound, no
wrap; (2) exactly two loop-carried values: the index and the output cursor `k`; `k` is incremented by
1 exactly on the path that stores, and is used only as the store index and after the loop; (3) the
predicate and the stored value are pure functions of the element at `i`, invariant values and `i`
([#80](https://github.com/Hawkynt/Polypiler/issues/80)); (4) the output is disjoint from the source and from everything the predicate reads
([#82](https://github.com/Hawkynt/Polypiler/issues/82)); (5) bounds: the source is proven readable for `n` elements and the output writable
for `n` elements (the worst case), or the stores carry no trap; (6) ordinary, non-volatile memory; (7)
a trapping predicate or value function gives `order=sequential`.

## How it is proven

`loop-inductive` (invariant: after `i` iterations `out[0..k)` holds, in order,
exactly the elements of `src[0..i)` that satisfy the predicate, and `k` is their number), discharged
by SMT for one symbolic iteration on both paths; fact obligations for alias, bounds and effects;
bounded `exhaustive` interpretation for `n` ∈ 0..3 over 8-bit elements. A failed, unknown or timed-out
obligation (budget 5 s each) leaves the IR unchanged and reports `not applied: proof timeout` or the
failing precondition.

## When it must not fire

- Predicate with a side effect: a predicate that increments a call counter runs `n` times in order;
  `Filter[order=any]` makes no such promise, and a fused pipeline could call it a different number of
  times.
- In-place compaction: with `out = src` the loop overwrites source elements it has already passed (`k
  ≤ i` always holds, so it never reads its own output), but the source is modified, which the contract
  of `Filter` (source unchanged, output disjoint) excludes.
- Cursor observed: storing `k` to memory every iteration exposes the intermediate counts.
- Output too small: 3 elements pass but the output holds 2 — the loop writes 2 and traps at the third.
- Cursor incremented on both paths: that is a map with gaps, not a filter.

## Switching it off

`--disable-opt O0151`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after [#281](https://github.com/Hawkynt/Polypiler/issues/281) (O0150) has declined the loop (it
has a second loop-carried value); enables filter-map-reduce fusion.
