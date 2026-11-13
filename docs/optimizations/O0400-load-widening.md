# O0400 — Widen adjacent element loads of a straight-line group into one vector load when every lane stays packed

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0400`.

## What it does

Checksum and hashing code is often unrolled by hand, and [#513](https://github.com/Hawkynt/Polypiler/issues/513) unrolls what [#510](https://github.com/Hawkynt/Polypiler/issues/510) could not
vectorize: the body then loads `p[i]`, `p[i+1]`, `p[i+2]`, `p[i+3]` one element at a time and folds them into
an accumulator. One load of the whole group saves three memory accesses — but only if the lanes are then
processed packed; taking each lane back out of the word costs more than the loads it saved. This rewrite
therefore fires only when every loaded lane is consumed by a reduction that has a packed form. PRD §4.8
(SIMD vectorization, SWAR), §8 P3.

## Example

### Before

```
%x0 = Load.u8[inbounds #1] %p, %i            // one block; %i1 = %i + 1, %i2 = %i + 2, %i3 = %i + 3
%x1 = Load.u8[inbounds #2] %p, %i1
%x2 = Load.u8[inbounds #3] %p, %i2
%x3 = Load.u8[inbounds #4] %p, %i3
%w0 = ZExt.u32 %x0
%w1 = ZExt.u32 %x1
%w2 = ZExt.u32 %x2
%w3 = ZExt.u32 %x3
%s1 = Add.u32[wrap] %s, %w0
%s2 = Add.u32[wrap] %s1, %w1
%s3 = Add.u32[wrap] %s2, %w2
%s4 = Add.u32[wrap] %s3, %w3
```

### After

```
%v  = VecLoad.vec<u8, 4>[inbounds #5] %p, %i  // proof #5: the union of #1 .. #4
%h  = VecCast.vec<u16, 2> %v                  // lane j = byte 2j | byte 2j+1 << 8
%ev = And.vec<u16, 2> %h, (Broadcast.vec<u16, 2> 0x00FF)
%od = LShr.vec<u16, 2>[strict] %h, 8
%pr = Add.vec<u16, 2>[wrap] %ev, %od          // each lane <= 510
%t  = VecReduce.u16[add, wrap] %pr            // <= 1020, cannot wrap
%tw = ZExt.u32 %t
%s4 = Add.u32[wrap] %s, %tw
```

## When it applies

With group size `n` and lane type `T`: (1) all `n` loads are in one basic block, read the same region with the
same address space and element type, and their indices are `i + k` for `k = 0 … n−1` with **no wrap** of the
index arithmetic — proven by [#350](https://github.com/Hawkynt/Polypiler/issues/350)/[#94](https://github.com/Hawkynt/Polypiler/issues/94) (`i + n − 1` does not exceed the index type's
maximum); consecutive `[inbounds]` proofs alone do not prove adjacency. (2) No load is `volatile` and the
region is not a device region ([#114](https://github.com/Hawkynt/Polypiler/issues/114)). (3) Between the first load and each later load of the group
there is no store, call or `Memory.Unknown` write that may alias `p[i … i+n−1]` ([#82](https://github.com/Hawkynt/Polypiler/issues/82); `MayAlias`
blocks). (4) Either every load is `[inbounds #k]` — the vector load then carries one proof record that is
the union of the `n` ranges — or all are `[bounds]` and no instruction with an observable effect or another
trap source lies between the first and the last load, so a bounds trap of a later lane merely moves in
front of pure instructions. `[unchecked]` loads are widened under the same no-effect condition. (5) Every
use of every loaded lane lies inside one packed consumer of the scope list; a single use outside it blocks
the group. (6) The reduction operator is associative and commutative on the lane type: `And`, `Or`, `Xor`,
`Add[wrap]`, the four integer minima/maxima. `Add[trap …]`, `Add[undefined]` and every floating-point
operation are refused. (7) For consumer 2 the lanes are `u8`, the extensions are `ZExt`, `n ≤ 64` (so the
`u16` partial sum is at most 64 × 255 = 16 320) and the accumulator addition is `[wrap]`. (8) [#155](https://github.com/Hawkynt/Polypiler/issues/155)
prices the packed form (`swar` or `simd` of that width) below `n` scalar loads plus the scalar fold.

## How it is proven

`structural+alias-facts+range-facts+exhaustive-16bit`: the vector load reads exactly the bytes the `n` loads read, lane 0 at the lowest address ([#381](https://github.com/Hawkynt/Polypiler/issues/381)); each reduction and the pairwise sum are proven against the scalar fold for two 8-bit lanes by [#70](https://github.com/Hawkynt/Polypiler/issues/70), and `n` lanes follow by associativity. No solver, no timeout path.

## When it must not fire

- A `u8` index with `i = 254` in a 256-element table: the loads read elements 254, 255, 0 and 1; a vector load at 254 reads 254 … 257.
- `x0 = p[i]; p[i+1] = x0; x1 = p[i+1]` with `p = [5, 9]`: the source reads `x1 = 5`; a load of both bytes placed at the first load reads 9.
- An `f64` sum `s + x0 + x1 + x2 + x3` with `s = 1e16` and all `x = 1.0`: sequentially every addition rounds back to 1e16; `s + (x0 + x1 + x2 + x3)` is 10000000000000004.
- `Add.u8[trap Overflow]` over `[200, 100, 0, 0]` traps at the second addition; a packed sum has no place for that trap.
- Four reads of a volatile status register become one 32-bit read: a different number and width of device accesses.
- Lane `x2` is also stored to another array: it must be extracted again (`shift`, `mask`), which costs more than the load it replaced.
- Sign-extended lanes in consumer 2: byte 0x80 contributes −128, the pairwise unsigned form adds 128.

## Switching it off

`--disable-opt O0400`; active at `--opt balanced` and `speed`, off at `--opt none`, `size` and `memory`.

## Targets

machine-code and assembly backends, and source targets whose compiler does not pack straight-line code itself (PowerBASIC, .NET and JVM JITs, WebAssembly without SIMD). C, C++ and Rust output is left as the scalar group because GCC and LLVM perform this packing; the cost profile of such a target prices the packed form above the scalar one, so the generated source keeps its readable shape. On 8-bit targets no logical width fits a register and the rule never fires.

## Interactions with other passes

runs in phase `optimize` after [#510](https://github.com/Hawkynt/Polypiler/issues/510) and [#513](https://github.com/Hawkynt/Polypiler/issues/513) (order O0020, O0023, O0400), so only groups the vectorizer left behind are seen; the vectors it creates are executed by [#511](https://github.com/Hawkynt/Polypiler/issues/511) or legalized by [#499](https://github.com/Hawkynt/Polypiler/issues/499).
