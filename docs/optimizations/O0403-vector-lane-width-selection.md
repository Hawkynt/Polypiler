# O0403 — Vectorize mixed-width element loops at the element width when ranges prove it and by widening lanes otherwise

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0403`.

## What it does

Most source languages compute byte arithmetic in a wider integer: `c[i] = (a[i] + b[i] + 1) >> 1` arrives as
zero-extensions, 32-bit operations and a truncation. [#510](https://github.com/Hawkynt/Polypiler/issues/510) needs one lane type per body and refuses
such a loop, although it has two correct vector forms: sixteen byte lanes per 128 bits when no intermediate
value needs more than a byte, or eight 16-bit lanes after widening when one does. Which form is legal is a
value-range question per lane; which is cheaper is the cost model's. PRD §4.8 (SIMD vectorization), §8 P3.

## Example

### Before

```
loop:                                         // i = 0 .. n-1, rounding average of two u8 arrays
  %x  = Load.u8[inbounds #1] %a, %i
  %y  = Load.u8[inbounds #2] %b, %i
  %xw = ZExt.u32 %x
  %yw = ZExt.u32 %y
  %s  = Add.u32[wrap] %xw, %yw
  %s1 = Add.u32[wrap] %s, 1                   // range [1, 511]: needs 9 bits
  %h  = LShr.u32[strict] %s1, 1
  %z  = Trunc.u8 %h
  Store.u8[inbounds #3] %c, %i, %z
```

### After

```
vloop:                                        // i = 0, 16, .. ; widening form: two vectors of eight u16 lanes
  %va = VecLoad.vec<u8, 16>[inbounds #4] %a, %i
  %vb = VecLoad.vec<u8, 16>[inbounds #5] %b, %i
  %ua = Shuffle.vec<u8, 16>[8, 9, 10, 11, 12, 13, 14, 15, 0, 1, 2, 3, 4, 5, 6, 7] %va
  %ub = Shuffle.vec<u8, 16>[8, 9, 10, 11, 12, 13, 14, 15, 0, 1, 2, 3, 4, 5, 6, 7] %vb
  %al = VecWiden.vec<u16, 8>[zero] %va        // lanes 0 .. 7
  %ah = VecWiden.vec<u16, 8>[zero] %ua        // lanes 8 .. 15
  %bl = VecWiden.vec<u16, 8>[zero] %vb
  %bh = VecWiden.vec<u16, 8>[zero] %ub
  %hl = LShr.vec<u16, 8>[strict] (Add.vec<u16, 8>[wrap] (Add.vec<u16, 8>[wrap] %al, %bl), (Broadcast.vec<u16, 8> 1)), 1
  %hh = LShr.vec<u16, 8>[strict] (Add.vec<u16, 8>[wrap] (Add.vec<u16, 8>[wrap] %ah, %bh), (Broadcast.vec<u16, 8> 1)), 1
  %zl = VecNarrow.vec<u8, 8>[wrap] %hl
  %zh = VecNarrow.vec<u8, 8>[wrap] %hh
  %vz = VecConcat.vec<u8, 16> %zl, %zh
  VecStore.vec<u8, 16>[inbounds #6] %c, %i, %vz
tail:                                         // the original scalar loop
// packed-narrow form, chosen for "c[i] = (a[i] >> 1) + (b[i] >> 1)" (sum <= 254):
//   %vz = Add.vec<u8, 16>[wrap] (LShr.vec<u8, 16>[strict] %va, 1), (LShr.vec<u8, 16>[strict] %vb, 1)
```

## When it applies

All conditions of [#510](https://github.com/Hawkynt/Polypiler/issues/510) apart from the single lane type, and: (1) loads and stores of the body have one
element width `E`; every other value is an integer of at most 64 bits. (2) For each value `v` of the body
the lane width `L(v)` is the smallest of `E`, `2E`, `4E` for which one form of [#161](https://github.com/Hawkynt/Polypiler/issues/161) holds: the
*range form* — the operands and the exact mathematical result of the operation fit that width in the
signedness the operation uses, with the loaded elements ranging over their full type unless
[#94](https://github.com/Hawkynt/Polypiler/issues/94) knows less — or the *demanded-bits form* — only the low `k ≤ L` bits of `v` are demanded
([#85](https://github.com/Hawkynt/Polypiler/issues/85), [#89](https://github.com/Hawkynt/Polypiler/issues/89)) and the operation is closed on low bits (`Add`, `Sub`, `Mul`, `And`,
`Or`, `Xor`, `Not`, `Neg`, `Shl`). Right shifts, divisions, comparisons, minima and maxima qualify only by
the range form. (3) The body lane width is `L = max L(v)`. `L = E` allows the packed-narrow form; `L = 2E`
or `4E` the widening form; a larger need blocks the rewrite. (4) Extensions keep their kind:
`ZExt` → `VecWiden[zero]`, `SExt` → `VecWiden[sign]`. (5) The store's `Trunc` becomes `VecNarrow[wrap]`;
`[sat]` is never introduced. (6) Shift contracts are re-stated at the lane width: a `[strict]` shift by a
constant `k` needs `k < L`. (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155) chooses among the legal forms and the scalar loop;
`BlockedCost` of the widening form counts the widen and narrow steps and half (or a quarter) of the lanes
per operation. (8) The scalar remainder loop this rule leaves behind is marked `vector-remainder` in its loop metadata and is never a candidate of this rule again.

## How it is proven

`range-facts+exhaustive-16bit+structural`: under (2) every lane computes the same low `L` bits as the wide scalar operation, and extensions and wrapping truncation commute with that; each fixture body is additionally enumerated over all `u8` operand pairs by [#70](https://github.com/Hawkynt/Polypiler/issues/70). No solver, no timeout path.

## When it must not fire

- The rounding average at byte lanes: `a = 255, b = 255` gives `(255 + 255 + 1) mod 256 = 255`, shifted 127; the source gives 255. With `a = 200, b = 100` the byte form gives 22, the source 150.
- `if (a[i] + b[i] > 255)` at byte lanes: the wrapped sum is never above 255.
- Sign instead of zero extension: byte 200 widened with `[sign]` is −56.
- `VecNarrow[sat]` in place of the source's truncation: 300 becomes 255, the source stores 44.
- `(a[i] << 4) >> 4` in 32 bits is the identity for a byte; at byte lanes the left shift drops the high nibble: 0xAB becomes 0x0B.
- A lane-wise `UDiv` narrowed by demanded bits: division is not closed on low bits (`300 / 3 = 100`, but `(300 mod 256) / 3 = 14`).

## Switching it off

`--disable-opt O0403`; active at `--opt balanced` and `speed`, off at `--opt none`, `size` and `memory`.

## Targets

every target that executes logical vectors natively or through a vector API; the gain is the lane count (double or quadruple for the narrow form). For C, C++ and Rust the scalar loop is kept, since GCC and LLVM choose lane widths themselves; for targets without an optimizing compiler behind them (PowerBASIC, assembly, .NET and JVM vector APIs) this is the only place the choice is made. SWAR profiles benefit from the narrow form only ([#511](https://github.com/Hawkynt/Polypiler/issues/511) has no widening).

## Interactions with other passes

runs in phase `optimize` as part of the vectorization group, after [#161](https://github.com/Hawkynt/Polypiler/issues/161) and directly after [#510](https://github.com/Hawkynt/Polypiler/issues/510) (it picks up loops O0020 reported as `operation ZExt has no vector form`); feeds [#499](https://github.com/Hawkynt/Polypiler/issues/499); `OPT-VEC-MASKSELECT` and `OPT-VEC-INTERLEAVE` reuse the lane-width map for their bodies.
