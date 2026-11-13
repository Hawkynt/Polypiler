# O0409 — Run the remainder of a vector loop as one vector iteration with partial loads and stores

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0409`.

## What it does

[#510](https://github.com/Hawkynt/Polypiler/issues/510) keeps the original scalar loop for the `n mod VF` elements a vector loop leaves over. For
short arrays that remainder is most of the work, and it is a second copy of the body with its own branch.
Targets with masked memory operations can run it as one more vector iteration in which only the remaining
lanes are read and written. [#381](https://github.com/Hawkynt/Polypiler/issues/381) already defines the operations for that — `VecLoadPartial` and
`VecStorePartial`; what is missing is the rewrite and the conditions under which dead lanes may take part
in the body. PRD §4.8 (SIMD vectorization), §8 P3.

## Example

### Before

```
vloop:                                        // i = 0, 16, .. while i + 16 <= n          (output of O0020)
  %vx = VecLoad.vec<u8, 16>[inbounds #1] %a, %i
  %vy = VecLoad.vec<u8, 16>[inbounds #2] %b, %i
  %vz = Xor.vec<u8, 16> %vx, %vy
  VecStore.vec<u8, 16>[inbounds #3] %c, %i, %vz
tail:                                         // scalar loop for the remaining n mod 16 elements
```

### After

```
vloop:                                        // unchanged
rest:
  %r  = Sub.uptr %n, %i                       // 0 .. 15
  %z  = ICmp.eq.uptr %r, 0
  Br %z, exit, mtail
mtail:                                        // one iteration, lanes 0 .. r-1 only
  %px = VecLoadPartial.vec<u8, 16>[inbounds #1] %a, %i, %r
  %py = VecLoadPartial.vec<u8, 16>[inbounds #2] %b, %i, %r
  %pz = Xor.vec<u8, 16> %px, %py
  VecStorePartial.vec<u8, 16>[inbounds #3] %c, %i, %pz, %r
```

## When it applies

(1) The function contains the vector loop and scalar remainder loop of [#510](https://github.com/Hawkynt/Polypiler/issues/510). (2) Partial accesses touch
`[i, i + r)` with `i + r = n`, a subset of what the scalar tail touched, so its `[inbounds]` proofs carry
over unchanged. (3) Dead lanes hold zero after `VecLoadPartial`; every operation of the body must be
trap-free **for zero operands as well**: bodies containing `UDiv`, `SDiv`, `URem`, `SRem` or any operation
whose freedom from traps was proven from a fact about the loaded elements are refused ([#115](https://github.com/Hawkynt/Polypiler/issues/115),
[#94](https://github.com/Hawkynt/Polypiler/issues/94)). (4) Every store becomes `VecStorePartial` with the same count; no lane ≥ `r` is written
and no full-width store appears in the tail. (5) A reduction accumulator is merged through
`Select.vec` with the first-`r` mask (`ICmp.ult` of the lane-index vector against `r`), so dead lanes
keep the accumulator whatever the operator; zero-filled lanes are never assumed neutral. (6) `r = 0`
skips the iteration. (7) Floating-point lane operations are allowed as in O0020; results of dead lanes
are discarded and float reductions do not occur. (8) [#155](https://github.com/Hawkynt/Polypiler/issues/155) is asked once with the alternatives
*scalar tail*, *masked tail* and *overlapping final vector*; this rule applies only when *masked tail*
wins — in practice when the profile lists partial loads and stores as native.

## How it is proven

`structural+effect-facts`: live lanes compute what the scalar tail computes (O0020's lane argument), dead lanes are neither read from memory, stored, nor merged into a result, and by (3) cannot trap. No solver, no timeout path.

## When it must not fire

- A masked store emulated as load, blend, store of 16 bytes with `r = 5`: it reads and rewrites eleven bytes behind the array — out of bounds, and a lost update if another thread changed them in between.
- `s = s + a[i] + 1` with zero-filled dead lanes added in: `n = 17`, `VF = 16` gives one live and fifteen dead lanes in the tail, and the sum is 15 too large.
- An `And` reduction with zero-filled lanes: the result is 0 whatever the data.
- `c[i] = 100 / a[i]` with all elements proven non-zero: the dead lanes divide by zero.
- `r` computed in the lane type for `VF = 64` and compared signed: 0x80 lanes and above wrap; the comparison must be unsigned and `r < VF` must fit the lane type.
- Running the masked iteration for `r = 0` on a target whose masked load faults on an unmapped address even with an all-false mask.

## Switching it off

`--disable-opt O0409`; active at `--opt balanced` and `speed`, off at `--opt none`, `size` and `memory`.

## Targets

targets with native lane masking: AVX-512, SVE and RVV machine code and the vector APIs that expose masked loads and stores. Everywhere else a partial load or store lowers to an element loop, which is the scalar tail again with extra steps, and the cost model keeps the form of O0020. Generated C, C++ and Rust source keeps the scalar remainder loop; the target compiler chooses its own tail strategy.

## Interactions with other passes

runs in phase `optimize` in the vectorization group after [#510](https://github.com/Hawkynt/Polypiler/issues/510) and the versioning rules; shares its cost query with `OPT-VEC-OVERLAPTAIL` (order O0409, then O0410; each applies only when its own alternative wins); `OPT-VEC-OVERREAD` may afterwards widen the partial loads; [#499](https://github.com/Hawkynt/Polypiler/issues/499) lowers the partial operations.
