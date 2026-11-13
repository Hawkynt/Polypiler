# O0402 — Vectorize strided channel loops by de-interleaving whole groups with constant shuffles

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0402`.

## What it does

Pixels, stereo samples and coordinate pairs are stored interleaved, so a loop over one channel walks its
array with a stride. [#510](https://github.com/Hawkynt/Polypiler/issues/510) requires unit stride and leaves such loops scalar. They vectorize by
loading whole groups, separating the channels with constant shuffles, computing per channel and
interleaving again — a rewrite that is correct only if the vector loop reads and writes nothing the scalar
loop did not own, and profitable only if the arithmetic outweighs the shuffles. PRD §4.8 (SIMD
vectorization), §8 P3.

## Example

### Before

```
loop:                                         // i = 0 .. n-1; %p holds n pairs (L, R) of u8
  %k  = Shl.u32[strict] %i, 1
  %l  = Load.u8[inbounds #1] %p, %k
  %k1 = Add.u32 %k, 1
  %r  = Load.u8[inbounds #2] %p, %k1
  %m  = Add.u8[wrap] %l, %r
  Store.u8[inbounds #3] %p, %k, %m            // L = L + R; R is only read
```

### After

```
vloop:                                        // i = 0, 4, .. while i + 4 <= n: four pairs = eight bytes per step
  %k  = Shl.u32[strict] %i, 1
  %k4 = Add.u32 %k, 4
  %v0 = VecLoad.vec<u8, 4>[inbounds #4] %p, %k              // L0 R0 L1 R1
  %v1 = VecLoad.vec<u8, 4>[inbounds #5] %p, %k4             // L2 R2 L3 R3
  %vl = Shuffle.vec<u8, 4>[0, 2, 4, 6] %v0, %v1             // L0 L1 L2 L3   (indices 4..7 name lanes of %v1)
  %vr = Shuffle.vec<u8, 4>[1, 3, 5, 7] %v0, %v1             // R0 R1 R2 R3
  %vm = Add.vec<u8, 4>[wrap] %vl, %vr
  %o0 = Shuffle.vec<u8, 4>[0, 4, 1, 5] %vm, %vr             // M0 R0 M1 R1
  %o1 = Shuffle.vec<u8, 4>[2, 6, 3, 7] %vm, %vr             // M2 R2 M3 R3
  VecStore.vec<u8, 4>[inbounds #4] %p, %k, %o0
  VecStore.vec<u8, 4>[inbounds #5] %p, %k4, %o1
tail:                                         // the original loop for the remaining n mod 4 pairs
```

## When it applies

All conditions of [#510](https://github.com/Hawkynt/Polypiler/issues/510) except unit stride, and: (1) every access of the body to the interleaved region
has the evolution `AddRec(c, s)` with the same constant `s` ∈ {2, 3, 4} and `0 ≤ c < s` ([#350](https://github.com/Hawkynt/Polypiler/issues/350), no
wrap); accesses to other regions are unit stride or loop-invariant. (2) **Whole groups are in bounds:**
`s × n ≤ extent(region)` is proven ([#94](https://github.com/Hawkynt/Polypiler/issues/94), [#349](https://github.com/Hawkynt/Polypiler/issues/349), or the element is a record so the
region holds whole groups by construction). The scalar loop's own proofs cover only the channels it
touches. (3) Dependence between groups: a store to channel `c` in group `i` and an access to channel `c'`
in group `i + d` conflict only for `d = 0`; any index of the form `s × (i + d) + c'` with `d ≠ 0` blocks the
rewrite, as does `MayAlias` with another stored region ([#82](https://github.com/Hawkynt/Polypiler/issues/82)). (4) If the body stores to the
region, every channel it does not store is written back with the value just loaded. That is allowed only
when the region is not `volatile`, the function's effect facts show no concurrent access to it (the
region is `NoEscape` or `ArgEscape`, or the module is declared single-threaded; [#114](https://github.com/Hawkynt/Polypiler/issues/114)), and the
region is writable as a whole. If the body only reads the region, nothing is stored. (5) Lane type and
`VF` give logical widths for which the profile lists constant `Shuffle`; a 3-channel de-interleave is
composed of two-source shuffles from the table. (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155): `BlockedCost` including `2 × s` shuffles
per block (fewer where channels are unused) is below the scalar loop. (7) The scalar remainder loop this rule leaves behind is marked `vector-remainder` in its loop metadata and is never a candidate of this rule again.

## How it is proven

`alias-facts+effect-facts+range-facts+structural`: lane `j` of channel `c` is element `s × (i + j) + c` in both forms; with whole groups in bounds, no cross-group dependence and no observer of the unchanged channels, O0020's argument applies per channel. The shuffle tables are checked against that index formula by the interpreter for every (`s`, `VF`). No solver, no timeout path.

## When it must not fire

- A buffer of 3 × n − 2 bytes whose last pixel has only its red byte, and a loop over channel 0: the scalar loop reads `3(n−1)`, the group load reads two bytes past the end.
- Another thread updates the R samples while this loop computes `L = L + R`: the scalar loop never writes R; the vector loop stores the R values it loaded and loses the update.
- `p[2i + 2] = p[2i] + p[2i + 1]` on `[1, 1, 0, 0, 0, 0]`: sequentially `[1, 1, 2, 0, 2, 0]`; with all loads first the last pair gets `0 + 0`.
- A body mixing `p[2i]` and `p[3i]`: no common group exists.
- `p[3i] = 255 − p[3i]` alone on a profile where a shuffle costs as much as three scalar steps: three de-interleaves and three re-interleaves per block are slower than the loop.
- Two-source `Shuffle` indices taken modulo the lane count instead of selecting the second source: the R channel becomes a copy of L.

## Switching it off

`--disable-opt O0402`; active at `--opt balanced` and `speed`, off at `--opt none`, `size` and `memory`.

## Targets

pays where constant shuffles are native — machine-code backends with SIMD and source targets with a vector API (.NET `Vector128.Shuffle`, Java Vector API, WebAssembly SIMD). On SWAR-only and scalar profiles the shuffles are lane moves and the cost model refuses. For C, C++ and Rust the scalar loop is emitted: GCC and LLVM vectorize interleaved accesses themselves and a loop of explicit shuffles would be less readable for no gain.

## Interactions with other passes

runs in phase `optimize` directly after [#510](https://github.com/Hawkynt/Polypiler/issues/510) (it handles loops O0020 reported as non-unit stride) and after [#502](https://github.com/Hawkynt/Polypiler/issues/502); its vectors go to [#499](https://github.com/Hawkynt/Polypiler/issues/499); `OPT-VEC-LANEWIDTH` may widen the channel arithmetic; [#513](https://github.com/Hawkynt/Polypiler/issues/513) must not unroll a loop this pass can vectorize.
