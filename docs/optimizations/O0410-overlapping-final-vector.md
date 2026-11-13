# O0410 — Cover the remainder of a vector loop with one final vector that overlaps processed elements when recomputation is idempotent

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0410`.

## What it does

On targets without masked memory operations the remainder of a vector loop is a scalar loop of up to
`VF − 1` iterations. If the array holds at least one full vector, a single extra vector iteration placed at
`n − VF` covers the remainder — it recomputes a few elements that were already done. That is free of charge
for `c[i] = a[i] + b[i]` and wrong for `a[i] = a[i] + 1`: the rewrite stands or falls with idempotence.
PRD §4.8 (SIMD vectorization), §8 P3.

## Example

### Before

```
vloop:                                        // i = 0, 4, .. while i + 4 <= n            (output of O0020, VF 4)
  %vx = VecLoad.vec<u16, 4>[inbounds #1] %a, %i
  %vy = VecLoad.vec<u16, 4>[inbounds #2] %b, %i
  %vz = Add.vec<u16, 4>[wrap] %vx, %vy
  VecStore.vec<u16, 4>[inbounds #3] %c, %i, %vz
tail:                                         // scalar loop for the remaining n mod 4 elements
```

### After

```
entry:
  %big = ICmp.uge.uptr %n, 4
  Br %big, vloop, tail                        // fewer than 4 elements: the scalar loop does everything
vloop:                                        // unchanged
rest:
  %done = ICmp.eq.uptr %i, %n
  Br %done, exit, last
last:                                         // n = 10: vectors at 0-3, 4-7, then 6-9 (elements 6 and 7 a second time)
  %j  = Sub.uptr %n, 4
  %lx = VecLoad.vec<u16, 4>[inbounds #4] %a, %j
  %ly = VecLoad.vec<u16, 4>[inbounds #5] %b, %j
  %lz = Add.vec<u16, 4>[wrap] %lx, %ly
  VecStore.vec<u16, 4>[inbounds #6] %c, %j, %lz
```

## When it applies

(1) The function contains the vector loop of [#510](https://github.com/Hawkynt/Polypiler/issues/510) and its scalar remainder loop, entered from the
vector loop's exit (after this rule has run it is not, so the rule does not apply twice). (2) The final vector is
placed at `j = n − VF` and executed only when `n ≥ VF`; then `[j, j + VF) ⊆ [0, n)` and its accesses get
proof records derived from the scalar loop's bounds facts ([#94](https://github.com/Hawkynt/Polypiler/issues/94), [#350](https://github.com/Hawkynt/Polypiler/issues/350)). `n − VF` is
computed without wrap because of the dominating test. (3) **Idempotence of stores:** every stored region
is `NoAlias` with every region the body loads from ([#82](https://github.com/Hawkynt/Polypiler/issues/82)) — `MustAlias` with distance 0, which
O0020 accepts, is refused here — so the overlapped elements are recomputed from unchanged inputs and the
second store writes the value the first one wrote. (4) **Idempotence of reductions:** only operators with
`op(op(s, x), x) = op(s, x)` are admitted — `And`, `Or` and the four integer minima and maxima; a loop
with an `Add`, `Xor` or counting reduction is refused. (5) The regions are not volatile (already required
by O0020): a second store of the same value is unobservable. (6) Lane-wise floating-point operations are
admitted: the recomputation is the same IEEE operation on the same operands and gives the same bits.
(7) `VectorTail.Choose` (`OPT-VEC-MASKTAIL`) is asked with the three alternatives; this rule applies only
when *overlapping final vector* wins.

## How it is proven

`alias-facts+range-facts+structural`: with inputs unchanged by the loop, element `k` receives the same value from any vector iteration that covers it; the admitted reductions absorb repeated operands. No solver, no timeout path.

## When it must not fire

- `a[i] = a[i] ^ 0xFF` with `n = 10`, `VF = 4`: elements 6 and 7 are inverted twice and end up unchanged.
- `a[i] = a[i] + 1`: elements 6 and 7 are incremented by 2.
- `s = s + a[i]` over `1 … 10`: the scalar sum is 55; with elements 6 and 7 (values 7 and 8) added again it is 70.
- `n = 3` with `VF = 4`: `n − 4` wraps to a huge index, or reads one element before the array.
- `c` overlapping `a` at a distance the alias analysis cannot exclude: the first store changes an input of the recomputation.
- A store to a device register: writing twice is two device operations.

## Switching it off

`--disable-opt O0410`; active at `--opt balanced` and `speed`, off at `--opt none`, `size` and `memory`.

## Targets

targets with cheap unaligned vector accesses and no lane masking: SSE/AVX2 and NEON machine code, WebAssembly SIMD, the .NET and Java vector APIs. With native masking the masked tail usually wins the cost query; on SWAR and scalar profiles there is no vector loop at all. Generated C, C++ and Rust source keeps the scalar remainder loop, because the target compiler picks its own tail strategy and an overlapping index is harder to read than a loop.

## Interactions with other passes

runs in phase `optimize` after `OPT-VEC-MASKTAIL` (order O0409, O0410) on loops whose remainder is still scalar; the final vector's accesses are unaligned even after `OPT-VEC-ALIGNPEEL`, which [#499](https://github.com/Hawkynt/Polypiler/issues/499) learns from the absence of an alignment fact.
