# O0347 — Align a private array to the vector width and pad its storage to whole vectors

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0347`.

## What it does

A vector loop over an array needs a scalar peel loop when the base is not aligned to the vector width
and a scalar tail loop when the length is not a multiple of the lane count. Both disappear if the
compiler owns the storage: it aligns the base and rounds the *physical* extent up to whole vectors, so
the last partial vector can be processed as a full one over padding nobody else can see. PB-Compiler
implements both halves for private arrays (upstream O0325, `DataLayoutTransforms` and
`ArrayBaseAlignment`). PRD §4.8 (vector data path).

## Example

### Before

```
%a = Alloc.ptr<i16>[stack, zero] 1001             // 2 002 bytes, any alignment
```

### After

```
// profile: widest simd form 128 bit -> V = 16 bytes, L = 8 lanes of i16
%a = Alloc.ptr<i16>[stack, zero, align=16] 1008   // 2 016 bytes; logical length stays 1001
```

## When it applies

(1) The region is private: `EscapeOf` is `NoEscape` ([#82](https://github.com/Hawkynt/Polypiler/issues/82)), `LayoutDemandOf` is `Free` ([#338](https://github.com/Hawkynt/Polypiler/issues/338)), and it has no overlay or reinterpreting view. (2) *The logical extent stays the bound*: every access is through `ElemAddr` of the array, whose dimension remains `n` and still traps for an index ≥ `n` ([#111](https://github.com/Hawkynt/Polypiler/issues/111)), or is proven inside the first `n` elements (`inbounds #id`, or `IsInBounds` is `True` against the original extent, [#93](https://github.com/Hawkynt/Polypiler/issues/93)); a raw `[bounds]` access whose index is not proven would be checked against the enlarged region and blocks the rewrite. (3) Block operations (`Memory.Copy`, `Move`, `Fill`, `Equal`, …) on the region have counts proven ≤ `n`; they are not widened ([#189](https://github.com/Hawkynt/Polypiler/issues/189)). (4) The element size divides `V`, `V` is a power of two, and the count is a literal; `ceil(n / L) × L` is computed without overflow and stays within the profile's allocation limit, and the allocation cannot fail observably (the rule of [#500](https://github.com/Hawkynt/Polypiler/issues/500)). (5) Padding elements are zero before the first vector access: the region is `[zero]`, or a `Memory.Fill` of the padding follows the allocation. (6) Existing alignment facts stay true: the new `align` is the maximum of `V` and any declared alignment ([#83](https://github.com/Hawkynt/Polypiler/issues/83)). (7) *Trigger*: at least one counted loop with unit stride ([#350](https://github.com/Hawkynt/Polypiler/issues/350)) accesses the region, and `ICostModel.IsAvailable("simd")` is true. (8) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts fewer than `V` extra bytes per array under the objective; under `memory` it declines.

## How it is proven

`structural+range-facts+alias-facts`: no instruction can address an element at or beyond `n` except the vector accesses [#510](https://github.com/Hawkynt/Polypiler/issues/510) later proves against the physical extent, and padding is private zeroed storage. No solver, no timeout path.

## When it must not fire

- `Load.i16[bounds] (PtrAdd %a, %i)` with `i = 1003` and no proof: before, `IndexOutOfRange`; after, the region has 1 008 elements and the load returns padding.
- The array is passed to a routine that sums `ArrayLen` elements — fine — or to one that writes the region to a file by its allocated size: seven extra elements appear in the file.
- Unzeroed padding: a vector `max` reduction over the last block reads whatever the padding holds.
- `Memory.Equal` of two padded arrays widened to the physical count: differing padding makes equal arrays compare unequal.
- A 3-byte element type: no lane count divides the vector width.

## Targets and cost gate

Pays off only for targets with a vector form (`simd` in the profile): machine code with SSE/AVX/NEON, WebAssembly SIMD, and source targets whose backend emits explicit vector types. Scalar-only targets and the `memory` objective never pad. In generated source the declaration gains an alignment attribute and a rounded size; a target compiler cannot round an array's size itself, because it cannot know that nothing observes the tail.

## Switching it off

`--disable-opt O0347`; active at `--opt balanced` and `speed`, off at `--opt size`, `memory` and `none`.

## Interactions with other passes

runs in phase `specialize` as a module pass after [#501](https://github.com/Hawkynt/Polypiler/issues/501), `OPT-MEM-AOS2SOA` and `OPT-MEM-CONTRACT` have fixed which arrays exist and their element types, and before [#510](https://github.com/Hawkynt/Polypiler/issues/510), which then finds `IsAligned` and `IsInBounds` true for full-vector accesses over the tail; idempotent — a region whose count is already a multiple of `L` and whose alignment is ≥ `V` is left alone.
