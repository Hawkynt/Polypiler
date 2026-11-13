# O0305 — Recognize exchange-sort loop nests over integer arrays as Sort

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0305`.

## What it does

A hand-written bubble sort is the commonest O(n²) loop nest in old BASIC and in teaching code, and every
target Polypiler emits has something better: `Array.Sort`, `qsort`, `sort.Ints`, PowerBASIC's `ARRAY SORT`,
or the merge-sort helper of [#196](https://github.com/Hawkynt/Polypiler/issues/196). PB-Compiler's idiom catalog names the shape; recognizing it
turns half a million compares for 1 000 elements into about ten thousand. PRD §4.5 (recognition), §19.3.
The linear-search row of the same upstream document is `OPT-LOOP-FIND`.

## Example

### Before

```
// i = 0 .. n-2, j = 0 .. n-2-i
inner.body:
  %p  = ElemAddr.ptr<i16>[inbounds #1] %a, %j
  %j1 = Add.u32[wrap] %j, 1
  %q  = ElemAddr.ptr<i16>[inbounds #2] %a, %j1
  %x  = Load.i16[inbounds #1] %p
  %y  = Load.i16[inbounds #2] %q
  %gt = ICmp.sgt.i16 %x, %y
  Br %gt, swap, next
swap:
  Store.i16[inbounds #1] %p, %y
  Store.i16[inbounds #2] %q, %x
  Br next
```

### After

```
%s  = SeqOf (SubSlice[bounds] (SliceOf %a), 0, %n)
%ys = Sort[unstable] %s, λ(%u, %v): ICmp.slt.i16 %u, %v
%t  = Materialize.darray<i16> %ys
Memory.Copy.i16 %a, %t, %n
```

## When it applies

(1) The nest matches one of the two shapes exactly; both counters are `AddRec` with step 1
([#350](https://github.com/Hawkynt/Polypiler/issues/350)) and the bound expressions `n − 2` and `n − 2 − i` are proven not to wrap ([#94](https://github.com/Hawkynt/Polypiler/issues/94)) —
for `n = 0` or `n = 1` in an unsigned type the source would loop over a huge range. (2) All four accesses
are in bounds by contract (`[inbounds]`/`[unchecked]`) or by the fact `n ≤ Length(a)`; a nest that can trap
part-way leaves a partially sorted array. (3) The array storage is ordinary, non-volatile memory and is not
aliased by `n`'s source or by anything else the nest reads ([#82](https://github.com/Hawkynt/Polypiler/issues/82)). (4) The exchange is guarded by a
strict comparison of the two loaded elements, signed or unsigned, and stores exactly the crossed values.
(5) No value defined in the nest is used after it. (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers `Sort` plus the copy over the
nest for the proven range of `n`.

## How it is proven

`structural+interpreter`: both shapes are Algorithm B without and with the bound shortcut, which sorts every input; for integer keys the sorted permutation is unique as a value sequence. The matcher is validated exhaustively on short arrays by the oracle. No solver, no timeout path.

## When it must not fire

- Unsigned elements compared with a signed predicate: `[200, 3, 100]` as `u8` sorts to `[3, 100, 200]`, with `ICmp.slt.i8` to `[200, 3, 100]` (200 is −56).
- `f64` elements `[+0.0, −0.0]`: the strict exchange leaves them in place; a library sort may return `[−0.0, +0.0]`.
- A nest whose inner loop runs to `n − 1` (one too far): it reads `a[n]`; the source traps or reads a neighbour, the sort does not.
- `n` unsigned and 0: `n − 2` wraps to 4294967294.
- The inner counter is read after the nest.

## Targets

Every target with enough room for the sort: source targets map `Sort` to their library call, which
is also the more readable output; machine-code targets call the merge-sort helper. On small machines the
helper and its `n`-element buffer can cost more than the nest saves — [#155](https://github.com/Hawkynt/Polypiler/issues/155) decides with `LoopCost`
of the nest (trip count from the range of `n`) against `HelperCost` plus the copy; under `--opt memory` the
buffer normally decides against the rewrite.

## Switching it off

`--disable-opt O0305`; active at `--opt balanced` and `speed` when the cost model approves; at `size` and `memory` only when the cost model approves under that objective; off at `--opt none`.

## Interactions with other passes

runs in phase `recognize` after [#582](https://github.com/Hawkynt/Polypiler/issues/582), which normalizes XOR swaps to the load/load/store/store form this rule matches; enables the backends' library-sort selection.
