# O0317 — Skew a two-level stencil nest so that its inner loop carries no dependence

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0317`.

## What it does

A relaxation or blur step computes each element from its upper and its left neighbour: the inner loop
depends on its own previous iteration and the outer loop on its previous row, so neither can run in
parallel. The elements on one anti-diagonal, however, are independent of each other. Re-indexing the nest by
`w = i + j` and making `w` the outer loop turns the diagonal wavefront into the inner loop, which
[#958](https://github.com/Hawkynt/Polypiler/issues/958) can then run on threads. PB-Compiler plans skewing as the most analysis-heavy loop
transformation and notes that it only pays where a parallel or vector step follows. PRD §4.8, §19.4.

## Example

### Before

```
// i = 1 .. 2, j = 1 .. 2
inner.body:
  %im = Sub.u32[wrap] %i, 1
  %jm = Sub.u32[wrap] %j, 1
  %u  = Load.u32[inbounds #1] (ElemAddr.ptr<u32>[inbounds #1] %g, %im, %j)
  %l  = Load.u32[inbounds #2] (ElemAddr.ptr<u32>[inbounds #2] %g, %i, %jm)
  %v  = Add.u32[wrap] %u, %l
  Store.u32[inbounds #3] (ElemAddr.ptr<u32>[inbounds #3] %g, %i, %j), %v
```

### After

```
// w = 2 .. 4
//   %lo = UMax.u32 1, (Sub.u32[wrap] %w, 2)
//   %hi = UMin.u32 2, (Sub.u32[wrap] %w, 1)
//   i = %lo .. %hi          (iterations of this loop are independent)
inner.body:
  %j  = Sub.u32[wrap] %w, %i
  %im = Sub.u32[wrap] %i, 1
  %jm = Sub.u32[wrap] %j, 1
  %u  = Load.u32[inbounds #1] (ElemAddr.ptr<u32>[inbounds #1] %g, %im, %j)
  %l  = Load.u32[inbounds #2] (ElemAddr.ptr<u32>[inbounds #2] %g, %i, %jm)
  %v  = Add.u32[wrap] %u, %l
  Store.u32[inbounds #3] (ElemAddr.ptr<u32>[inbounds #3] %g, %i, %j), %v
```

## When it applies

(1) The nest satisfies conditions (1)–(4) of `OPT-LOOP-INTERCHANGE` and both steps are 1.
(2) `UniformDependence` returns distance vectors, at least one with `d_i = 0` (otherwise the inner loop is
already dependence-free and there is nothing to do), and every vector satisfies `d_i + d_j ≥ 1`: the sink of
every dependence then lies on a strictly later wavefront `w = i + j` than its source, so iterating `w`
ascending preserves every dependent pair and no dependence connects two iterations of one wavefront.
`Unknown` blocks. (3) The new bounds `w − hi_j + 1`, `w − lo_j` and `hi_i + hi_j − 2` are proven not to
wrap in the counter type ([#94](https://github.com/Hawkynt/Polypiler/issues/94)), or are computed one width up. (4) The target has `Threads`
([#259](https://github.com/Hawkynt/Polypiler/issues/259)) and [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers `ThreadedCost` over the wavefronts to the sequential nest.

## How it is proven

`structural+alias-facts+effect-facts+range-facts`: the mapping `(i, j) → (i + j, i)` is a bijection of the iteration space onto the skewed space; with all skewed distances `(d_i + d_j, d_i)` having a first component ≥ 1, lexicographic order of the new nest preserves every dependence and the inner loop carries none. No solver, no timeout path.

## When it must not fire

- `g[i][j] = g[i-1][j+1] + g[i][j-1]`: the vector `(1, −1)` has `d_i + d_j = 0` — source and sink lie on the same wavefront, and the inner loop would read an element another of its iterations writes.
- A naive wavefront with the full rectangle as inner range (`i = lo_i … hi_i − 1` for every `w`): `j = w − i` leaves the array.
- `w − hi_j + 1` computed in `u32` for `w < hi_j − 1`: it wraps to a huge value and `UMax` selects it — the wavefront is empty.
- A body that calls `log(i, j)`: the call order changes from row-major to wavefront order.
- A target without threads: the skewed nest computes the same values with more arithmetic per iteration and non-contiguous accesses.

## Targets

Only targets with the `Threads` capability and a `threads` entry in the cost profile — hosted
native, managed and WebAssembly-threads targets. On everything else, including every retro target, the
skewed nest is slower than the original (diagonal accesses, extra bound arithmetic) and the pass reports
`target has no threads`. The emitted source is harder to read than the original nest; that is accepted only
because it is the form the parallel loop needs, and only at `--opt speed`.

## Switching it off

`--disable-opt O0317`; active at `--opt speed` on targets with `Threads`; off at `none`, `balanced`, `size` and `memory`.

## Interactions with other passes

runs in phase `specialize` immediately before [#958](https://github.com/Hawkynt/Polypiler/issues/958), which parallelizes the inner loop under its own preconditions; if it declines, the skewed nest stays sequential and correct.
