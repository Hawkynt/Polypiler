# O0408 — Replace a partial vector load by a full load and a lane mask only where the extra elements are proven readable

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0408`.

## What it does

The last block of a vector loop holds fewer elements than a vector. `VecLoadPartial` ([#381](https://github.com/Hawkynt/Polypiler/issues/381)) is the
honest way to read it — the missing lanes are zero and nothing behind the data is touched — but without a
native masked load it lowers to an element loop. A full-width load is one instruction; it is also a read of
memory the program never asked for. That read is harmless exactly when the bytes behind the data belong to
the same allocation and hold defined values, and a fault or a bounds violation otherwise. This rule turns
"usually works" into a checked precondition. PRD §4.8 (SIMD vectorization), §8 P3.

## Example

### Before

```
// %view = SubSlice of %buf (64 zero-initialized bytes), elements 0 .. 36; tail of a VF-16 loop at %i = 32
%v = VecLoadPartial.vec<u8, 16>[inbounds #1] %view, %i, %r     // %r = 5 lanes read, lanes 5 .. 15 are zero
```

### After

```
%f = VecLoad.vec<u8, 16>[inbounds #2] %buf, %i                 // proof #2: "%i + 16 <= 64", region of %buf, initialized [zero]
%m = ICmp.ult.vec<u8, 16> (VecMake.vec<u8, 16> 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15), (Broadcast.vec<u8, 16> (Trunc.u8 %r))
%v = Select.vec<u8, 16> %m, %f, (Broadcast.vec<u8, 16> 0)
```

## When it applies

(1) The address of the partial load derives from a region whose identity and extent are known at that point
([#93](https://github.com/Hawkynt/Polypiler/issues/93), the `owner` slot of [#121](https://github.com/Hawkynt/Polypiler/issues/121), bounds of [#123](https://github.com/Hawkynt/Polypiler/issues/123)); a view is traced to its
parent region. An address of unknown provenance blocks the rule. (2) `i + n ≤ extent(region)` is proven
([#94](https://github.com/Hawkynt/Polypiler/issues/94), [#349](https://github.com/Hawkynt/Polypiler/issues/349)) — the extent of the *allocation*, not the length of the view or the
trip count. (3) Every element of `[i + r, i + n)` is initialized on every path: the region was allocated
`[zero]`, is a global with an initializer, or the elements were stored before. (4) The region is not
`volatile` and not a device region ([#114](https://github.com/Hawkynt/Polypiler/issues/114)); reading it has no effect. (5) No other thread can be
writing the extra elements: the region is `NoEscape` or `ArgEscape` ([#82](https://github.com/Hawkynt/Polypiler/issues/82)) or the module is
declared single-threaded. Their values are discarded, but a racing read is not a defined operation on
every target. (6) The lanes `r … n−1` are replaced by zero through `Select.vec`, so the result equals
`VecLoadPartial` bit for bit; `0 ≤ r ≤ n`. (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prices full load plus compare and select below
the profile's cost of `VecLoadPartial`; a profile with a native masked load keeps the partial load.

## How it is proven

`range-facts+alias-facts+effect-facts+structural`: under (1)–(5) the full load is an in-bounds read of defined, unobserved bytes; under (6) its masked value is the partial load's value by the lane definitions of [#381](https://github.com/Hawkynt/Polypiler/issues/381). No solver, no timeout path.

## When it must not fire

- A 37-byte heap block that ends on the last byte of a mapped page: a 16-byte load at offset 32 reads 11 bytes of the next, unmapped page and faults.
- A buffer at offset 0xFFF8 of a 16-bit segment: a 16-byte load wraps to offset 0 or faults at the segment limit.
- A view of 37 elements whose parent has 64, of which only the first 37 were ever written and the allocation is not `[zero]`: the load reads undefined bytes; without the mask they would even flow into the result.
- The view's own length taken as the extent: `i + 16 ≤ 37` is false for the tail by construction; the proof must name the parent region.
- Bytes behind the data that are a memory-mapped device register: the read acknowledges an interrupt.
- Applying the same idea to `VecStorePartial`: the full store overwrites eleven bytes the program still needs.

## Switching it off

`--disable-opt O0408`; active at `--opt balanced` and `speed`, off at `--opt none`, `size` and `memory`.

## Targets

targets without a native masked load: SSE/AVX2 and NEON machine code, SWAR profiles, and vector APIs that load from an array at an index (.NET, Java) when the parent array is known. On profiles with native masked loads (AVX-512, SVE, RVV) the partial load is already one instruction and the cost model keeps it. For C, C++ and Rust source the partial form's element loop is kept unless the profile prices the full load lower; where it is emitted, the full load indexes the parent array and never forms a pointer past a view.

## Interactions with other passes

runs in phase `optimize` after `OPT-VEC-MASKTAIL` (which creates the partial loads) and after [#502](https://github.com/Hawkynt/Polypiler/issues/502), whose proof records it extends; the result is ordinary vector code for [#499](https://github.com/Hawkynt/Polypiler/issues/499); [#158](https://github.com/Hawkynt/Polypiler/issues/158) may merge the full load with an identical earlier one.
