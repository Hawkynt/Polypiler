# O0316 — Iterate fully permutable loop nests in cache-sized tiles

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0316`.

## What it does

A transpose, a matrix product or a 2-D filter touches one operand along the storage order and another
across it; whichever loop is innermost, one of them misses the cache on every access once the matrix is
larger than the cache. Processing the nest in blocks small enough that both operands stay resident fixes
that without changing what is computed. PB-Compiler lists tiling for completeness — pointless on a cacheless
8086, relevant from the 486 on — and names the two things it needs: a dependence proof and a per-target tile
size. Polypiler's targets include current CPUs, where this is one of the larger loop wins. PRD §4.8, §23.5.

## Example

### Before

```
// i = 0 .. 255, j = 0 .. 255
inner.body:
  %x = Load.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] %a, %i, %j)
  Store.u16[inbounds #2] (ElemAddr.ptr<u16>[inbounds #2] %b, %j, %i), %x
```

### After

```
// ii = 0, 16, .. 240;  jj = 0, 16, .. 240
//   %ie = UMin.u32 (Add.u32[wrap] %ii, 16), 256
//   %je = UMin.u32 (Add.u32[wrap] %jj, 16), 256
//   i = ii .. %ie - 1;  j = jj .. %je - 1
inner.body:
  %x = Load.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] %a, %i, %j)
  Store.u16[inbounds #2] (ElemAddr.ptr<u16>[inbounds #2] %b, %j, %i), %x
```

## When it applies

(1) The nest satisfies conditions (1)–(4) of `OPT-LOOP-INTERCHANGE`: perfect, rectangular,
no effects or traps beyond non-volatile element accesses, nothing escaping, distinct arrays `NoAlias`.
(2) `UniformDependence` returns distance vectors and every component of every vector is ≥ 0; then any
interleaving of the two loops that keeps each loop's own order — which tiling is — preserves all dependent
pairs. `Unknown` blocks. (3) `ii + T` and `jj + T` are proven not to wrap in the counter type
([#94](https://github.com/Hawkynt/Polypiler/issues/94)), or the bounds are computed one width up. (4) Both trip counts are `Exact` or `Symbolic`
([#350](https://github.com/Hawkynt/Polypiler/issues/350)) and the nest's footprint — distinct elements touched, from extents and element sizes —
exceeds the target's cache parameter; a nest that fits is left alone. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the tiled
form.

## How it is proven

`structural+alias-facts+effect-facts+range-facts`: strip-mining is always legal; interchanging the inner tile loop with the outer element loop is legal for a fully permutable nest (all distance components non-negative). No solver, no timeout path.

## When it must not fire

- `a[i][j] = a[i-1][j+1] + 1`: distance `(1, −1)`; an iteration at the right edge of a tile reads `a[i-1][j+1]`, which belongs to the next tile and has not been written yet.
- `ii + 16` in a `u8` counter at `ii = 240`: 256 wraps to 0 and `UMin` returns 0 — the last tile is skipped.
- An extent that is not a multiple of the tile edge without the `UMin`: the last tile reads past the array.
- A body that prints: the output order changes.
- A 16 × 16 nest on a target with a 32 KB cache: nothing to gain, four loops instead of two.

## Targets

Machine-code and source targets whose cost profile describes a data cache — every 32- and 64-bit
CPU since the 80486, and the managed and native source targets that run on them (no mainstream compiler
tiles at its default level). Never targets without a cache: 6502, Z80, 8080, 8086, 80286, 68000, tokenized
BASIC. The generated source grows from two loops to four; that is the price, and why the pass is tied to
`--opt speed`. How the cache parameter reaches the pass is the open question below.

## Switching it off

`--disable-opt O0316`; active at `--opt speed` on targets with a cache parameter; off at `none`, `balanced`, `size` and `memory`.

## Interactions with other passes

runs in phase `specialize` after `OPT-LOOP-INTERCHANGE` (which picks the better loop order first) and after [#510](https://github.com/Hawkynt/Polypiler/issues/510) (the inner loop stays the vector loop); `OPT-LOOP-SKEW` can make a nest permutable for it.
