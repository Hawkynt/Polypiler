# O0020 — Vectorize element-wise loops into logical vector operations

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#510](https://github.com/Hawkynt/Polypiler/issues/510). Once implemented it is switched off with `--disable-opt O0020`.

## What it does

"SIMD and multi-threading may not be supported for each target, but must be heavily used in optimized code
when available": the vectorizer is where that requirement starts. It turns a proven-independent
element-wise loop into operations on *logical* vectors — 16 to 4096 bits wide, all with one interface — and
leaves the question of AVX2, NEON, a polyfill, SWAR or scalar code to legalization. Vectorizing in the
middle-end means one proof serves every backend. PRD §4.8 (SIMD vectorization), §8 P3.

## Example

### Before

```
loop:                                     // i = 0 .. n-1, all accesses [inbounds]
  %x = Load.u8[inbounds #1] %a, %i
  %y = Load.u8[inbounds #2] %b, %i
  %z = Xor.u8 %x, %y
  Store.u8[inbounds #3] %c, %i, %z
```

### After

```
vloop:                                    // i = 0, 16, .. while i + 16 <= n
  %vx = Load.vec<16 x u8> %a, %i
  %vy = Load.vec<16 x u8> %b, %i
  %vz = Xor.vec<16 x u8> %vx, %vy
  Store.vec<16 x u8> %c, %i, %vz
tail:                                     // the original scalar loop for the remaining n mod 16 elements
```

## When it applies

(1) The loop is innermost, has one back edge, one exit, and a trip count `n` computable before the loop
([#94](https://github.com/Hawkynt/Polypiler/issues/94) scalar evolution; the induction variable does not wrap). (2) Every instruction in the body is
trap-free: memory accesses are `[inbounds]` (from [#502](https://github.com/Hawkynt/Polypiler/issues/502)), arithmetic is `[wrap]` or `[sat]`,
divisions are excluded unless the divisor is proven non-zero; a body that can trap is not vectorized because
the trap would have to occur after exactly the preceding iterations' stores. (3) The body has no effect
other than non-volatile `Memory.Read`/`Memory.Write` of array elements ([#114](https://github.com/Hawkynt/Polypiler/issues/114)). (4) No
loop-carried dependence: for every store `S[i]` and every access `T[i + d]` to possibly the same array in
the body, [#82](https://github.com/Hawkynt/Polypiler/issues/82) proves the arrays distinct, or `d = 0` with the load preceding the store in the
iteration. `MayAlias`/`Unknown` between a stored and a loaded array blocks the rewrite. (5) Scalars live
across iterations are only the induction variable and recognized integer reductions. (6) Floating-point
lane-wise operations are allowed (each lane is the same IEEE operation); no contraction, no
re-association. (7) Every operation has a lane-wise form in [#381](https://github.com/Hawkynt/Polypiler/issues/381) with identical per-lane semantics
(shift contracts, saturation, signedness). (8) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prices the blocked form below the scalar loop
for the proven or assumed trip count.

## How it is proven

`alias-facts+effect-facts+range-facts`: with no loop-carried dependence and no traps or other effects, executing VF iterations' loads before their stores is indistinguishable from sequential execution. Reductions rely on associativity and commutativity of the listed `[wrap]` integer operations, proven once by [#119](https://github.com/Hawkynt/Polypiler/issues/119)-independent exhaustive 8-bit enumeration. No solver, no timeout path.

## When it must not fire

- `a[i + 1] = a[i] + 1` on `[1, 0, 0, 0]`: sequentially `[1, 2, 3, 4]`; with VF = 4 all loads happen first and the result is `[1, 2, 1, 1]`.
- `c` overlapping `a` shifted by one element (`c = a + 1`): the same dependence through aliasing.
- `sum += a[i]` in `f64` over `[1e16, 1.0, -1e16, 1.0]`: sequentially 1.0; with two accumulator lanes `(1e16 − 1e16) + (1.0 + 1.0) = 2.0`.
- `c[i] = a[i] / b[i]` with `b[5] = 0`: the scalar loop stores elements 0–4 and then traps; a vector divide traps before storing them or not at all.
- `Add.i32[checked]` in the body: same trap-ordering problem.
- A `u8` induction variable with `n = 300`: the loop never terminates as written; assuming 300 iterations is wrong.

## Switching it off

`--disable-opt O0020`; active at `--opt balanced` and `speed`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` (late) after [#502](https://github.com/Hawkynt/Polypiler/issues/502), [#508](https://github.com/Hawkynt/Polypiler/issues/508), [#507](https://github.com/Hawkynt/Polypiler/issues/507) and [#161](https://github.com/Hawkynt/Polypiler/issues/161); the vectors it creates are consumed by [#499](https://github.com/Hawkynt/Polypiler/issues/499). [#513](https://github.com/Hawkynt/Polypiler/issues/513) must not unroll a loop first that this pass could vectorize (ordering: O0020 before O0023).
