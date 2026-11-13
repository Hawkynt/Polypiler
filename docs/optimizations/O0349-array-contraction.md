# O0349 — Contract an array of which only a sliding window is read to that many scalars

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0349`.

## What it does

A running sum written as `t[i] = t[i-1] + a[i]` with only `t[n-1]` used afterwards allocates and fills
a whole array to carry one value from each iteration to the next; a Fibonacci-style recurrence carries
two. [#500](https://github.com/Hawkynt/Polypiler/issues/500) replaces arrays by scalars only when every index is a literal. When each element
is read only a fixed distance after it is written, the array contracts to that many loop-carried
values and its storage disappears. PB-Compiler implements this for sliding-window recurrences
(upstream O0329, in `DataLayoutTransforms`). PRD §4.8.

## Example

### Before

```
%t = Alloc.ptr<i32>[heap] 1000
Store.i32 (ElemAddr %t, 0), %a0
body:                                              // i = 1 .. 999
  %i  = Phi.u32 [pre: 1], [body: %n]
  %p  = Load.i32 (ElemAddr %t, (Sub.u32 %i, 1))
  %ai = Load.i32 (ElemAddr %a, %i)
  %s  = Add.i32[wrap] %p, %ai
  Store.i32 (ElemAddr %t, %i), %s
  %n  = Add.u32 %i, 1
  %c  = ICmp.ult.u32 %n, 1000
  Br %c, body, exit
exit:
  %r = Load.i32 (ElemAddr %t, 999)
  Release %t
```

### After

```
body:
  %i  = Phi.u32 [pre: 1], [body: %n]
  %p  = Phi.i32 [pre: %a0], [body: %s]
  %ai = Load.i32 (ElemAddr %a, %i)
  %s  = Add.i32[wrap] %p, %ai
  %n  = Add.u32 %i, 1
  %c  = ICmp.ult.u32 %n, 1000
  Br %c, body, exit
exit:
  %r = Phi.i32 [body: %s]
```

## When it applies

For an array `t` and a loop `L`: (1) `t` is private — `EscapeOf` is `NoEscape` ([#82](https://github.com/Hawkynt/Polypiler/issues/82)) — one-dimensional, of a scalar element type, never resized, and every access uses that element type ([#111](https://github.com/Hawkynt/Polypiler/issues/111), [#121](https://github.com/Hawkynt/Polypiler/issues/121)); (2) `L` has an exact trip count `T` and contains exactly one store to `t`, at index `AddRec(start, +1)` without wrap, in a block that dominates the latch ([#350](https://github.com/Hawkynt/Polypiler/issues/350)); (3) every load of `t` inside `L` is at index `write − d` with a constant `1 ≤ d ≤ K`, `K ≤ 4`, or is a load of the element just written (forwarded from the store), and no other instruction in `L` may write `t` ([#86](https://github.com/Hawkynt/Polypiler/issues/86)); (4) *seeds*: before `L`, for every `d` in `1 … K`, a store to the constant index `start − d` dominates the loop; a missing seed blocks; (5) *final window*: every use of `t` after `L` is a load at a constant index in `[start + T − K, start + T − 1]`, or `ArrayLen`, or `Release`; with `T = 0` those indices are seed indices; (6) there is no other use of `t` anywhere — no read before the loop except of seeds already stored, no block operation, no call; (7) every removed store and load is proven in bounds — all indices `start − K … start + T − 1` lie inside the array's extent ([#94](https://github.com/Hawkynt/Polypiler/issues/94) on the literal bounds) — otherwise an `IndexOutOfRange` trap would disappear with the array; (8) the allocation cannot fail observably (the rule of [#500](https://github.com/Hawkynt/Polypiler/issues/500)).

## How it is proven

`scev-facts+alias-facts+structural`: by (2)–(6) the value read at `t[i − d]` in iteration `i` is the value stored in iteration `i − d` or the seed, which is what the `d`-th `Phi` of the chain holds. No solver, no timeout path.

## When it must not fire

- `print t[500]` after the loop: element 500 was overwritten in the scalar version long ago.
- `t[i] = t[i-1] + t[i-3]` with seeds for `t[0]` and `t[1]` only: `t[-1]`, read in the first iteration at distance 3 from index 2, has no seed.
- A loop over `i = 1 … 1000` on a 1 000-element array: the store to `t[1000]` traps `IndexOutOfRange`; contracted, the loop runs to completion.
- A second loop that sums the whole array afterwards.
- Loads of `t` as `i16` halves of `i32` elements: the window values are not the stored values.

## Targets and cost gate

Every target: an array, its allocation and one store per iteration disappear and `K ≤ 4` scalars take their place, which lowers every cost component, so the cost model is not consulted. In generated source the result is the running-total loop a programmer would have written. Target compilers do not remove a heap array whose elements are all written.

## Switching it off

`--disable-opt O0349`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after `OPT-MEM-LOADCARRY` (which may already have replaced the in-loop loads) and [#508](https://github.com/Hawkynt/Polypiler/issues/508); enables [#156](https://github.com/Hawkynt/Polypiler/issues/156) of the index arithmetic, closed-form and reduction recognition on the now scalar recurrence ([#282](https://github.com/Hawkynt/Polypiler/issues/282)) and [#510](https://github.com/Hawkynt/Polypiler/issues/510).
