# O0348 — Store a private two-dimensional array in the order its loops traverse it

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0348`.

## What it does

Source languages fix how a two-dimensional array is stored — PowerBASIC and Fortran column by column,
C and C# row by row — and programs are written in whatever loop order reads naturally. When the inner
loop runs along the dimension that is *not* contiguous, every access jumps a whole row or column: cache
misses on a PC, and no vectorization anywhere. Where the loop order cannot be changed (the algorithm or
its output order fixes it), the storage order can, if nobody else sees the storage. PB-Compiler
implements this (upstream O0327, in `DataLayoutTransforms`). PRD §4.8 (storage form).

## Example

### Before

```
%img = ArrayNew.darray<i16>[rank=2, order=column] 200, 320      // first index varies fastest
// for y in 0..199: for x in 0..319:
%e = ElemAddr.ptr<i16>[bounds] %img, %y, %x                    // consecutive x: 200 elements apart
Store.i16 %e, 0
```

### After

```
%img = ArrayNew.darray<i16>[rank=2, order=row] 200, 320         // last index varies fastest
%e = ElemAddr.ptr<i16>[bounds] %img, %y, %x                    // consecutive x: 1 element apart
Store.i16 %e, 0
```

## When it applies

(1) The array is private: `EscapeOf` is `NoEscape` ([#82](https://github.com/Hawkynt/Polypiler/issues/82)) and `LayoutDemandOf` is `Free` ([#338](https://github.com/Hawkynt/Polypiler/issues/338)). (2) *Every use is order-independent*: `ElemAddr` with both indices whose result is used only by `Load`/`Store` of the element; `ArrayLen`; `Release`; a `Memory.Fill` of the whole array with one value. Anything that depends on which elements are neighbours blocks: a row or column slice ([#205](https://github.com/Hawkynt/Polypiler/issues/205)), a flat or reinterpreting view ([#375](https://github.com/Hawkynt/Polypiler/issues/375)), `PtrAdd` on an element address, an element address that is stored, compared or passed to a call, and `Memory.Copy`, `Move`, `Equal`, `Compare`, `Mismatch` or `Find` over the storage ([#189](https://github.com/Hawkynt/Polypiler/issues/189)) — except a whole-array `Memory.Copy` between two arrays that are flipped together. (3) The array is not resized. (4) Bounds behaviour is unchanged by construction: `ElemAddr` checks each index against its own dimension ([#111](https://github.com/Hawkynt/Polypiler/issues/111)), independent of the order. (5) *Census*: for each innermost counted loop ([#350](https://github.com/Hawkynt/Polypiler/issues/350)) that accesses the array with one index an `AddRec` of that loop with step ±1 and the other index invariant in it, the loop's weight (the product of the trip counts of the loop and its enclosing counted loops, `unknownTripCount` where unknown) is credited to the order that makes the varying index the fastest. The array is flipped iff the other order's total is strictly greater than the current order's; a tie keeps the declared order. (6) `ICostModel.IsAvailable("storage.transposed")` is true ([#155](https://github.com/Hawkynt/Polypiler/issues/155)).

## How it is proven

`structural+alias-facts`: both storage orders are bijections between index pairs and cells, and under (2) every access names its cell by index pair. No solver, no timeout path.

## When it must not fire

- A 320 × 200 array that mirrors the VGA mode 13h frame buffer and is block-copied to it: the copy sends columns where rows belong.
- `BSAVE`-style output of the array's storage to a file, or a `ReinterpretSlice` of it for a checksum.
- A slice of row `y` handed to a routine: under the other order the row is not contiguous and the slice cannot exist.
- `PtrAdd %e, 1` to reach 'the next element in the row': after the flip it is the next element in the column.
- Two loops of equal weight, one row-wise and one column-wise: flipping helps one and hurts the other; the declared order stays.

## Targets and cost gate

Pays off on every target that stores the array linearly — machine code, C-family source, BASICs — and target compilers do not do it: they may interchange loops when dependences allow, never reorder a data structure. The profile lists the storage form `storage.transposed`. A backend whose language fixes the order of its native multi-dimensional arrays realizes the flipped order by declaring the dimensions in swapped sequence and swapping the indices at each access, which changes how accesses read (`img(x, y)` for the source's `img(y, x)`); that is why the pass is not active at `--opt size` and why a tie never flips. The form name `storage.transposed` and the `forms` list of `cost.yaml` that carries it are defined by `OPT-MEM-FORMS`.

## Switching it off

`--disable-opt O0348`; active at `--opt balanced` and `speed`, off at `--opt size`, `memory` and `none`.

## Interactions with other passes

runs in phase `specialize` as a module pass after loop canonicalization and [#508](https://github.com/Hawkynt/Polypiler/issues/508) have settled loop structure, before [#510](https://github.com/Hawkynt/Polypiler/issues/510) (which needs unit-stride accesses) and `OPT-MEM-ARRAYPAD`; must run before [#259](https://github.com/Hawkynt/Polypiler/issues/259) fixes target representations.
