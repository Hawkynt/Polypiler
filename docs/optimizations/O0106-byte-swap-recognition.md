# O0106 — Recognize byte swap from shift-and-mask expressions and byte loops

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#559](https://github.com/Hawkynt/Polypiler/issues/559). Once implemented it is switched off with `--disable-opt O0106`.

## What it does

Endianness conversion is written as four shifted and masked copies of a word OR-ed together, or as a
loop moving one byte per iteration — CityHash's `bswap64` in Cipher does the latter. x86 has `BSWAP`, C#
`BinaryPrimitives.ReverseEndianness`, Rust `swap_bytes`; PRD §17.4 names `ReverseBytes32` as the
recovery target for the assembly spelling, and the source spelling must reach the same operation. PRD
§4.5, §19.2, §22 (bit swap scenario).

## Example

### Before

```
%a = LShr.u32[strict] %x, 24
%b1 = LShr.u32[strict] %x, 8
%b = And.u32 %b1, 0x0000FF00
%c1 = Shl.u32[strict] %x, 8
%c = And.u32 %c1, 0x00FF0000
%d = Shl.u32[strict] %x, 24
%t = Or.u32 %a, %b
%u = Or.u32 %c, %d
%r = Or.u32 %t, %u
```

### After

```
%r = ReverseBytes.u32 %x
```

## When it applies

expression form: by symbolic byte provenance each of the `w/8` output
bytes of the root is exactly one input byte of the single leaf `x`, output byte `i` coming from input
byte `w/8 - 1 - i`, with no bit set from anywhere else (sign fill, overlapping terms); the combiner
may be `Or`, `Xor` or `Add` because the terms are disjoint. Loop form: natural loop, one exit, exactly
`w/8` iterations, pure body, accumulator starting at the constant 0, no escape. Both are confirmed by
the obligation `root(x) = ReverseBytes.u<w>(x)`.

## How it is proven

`smt-bitvector` at 32 and 64 bit (the loop unrolled `w/8` times), preceded by the
structural byte-provenance check; an 8-bit exhaustive proof does not exist for a byte permutation and
is not claimed. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged
and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Arithmetic shift without mask: `Or(AShr.i32 x, 24, …)` with `x = 0x80000000` gives `0xFFFFFF80`; the
  byte swap gives `0x00000080`.
- Word swap: terms `(x >> 16) | (x << 16)` — every byte moves, but to the wrong place: `0x12345678`
  becomes `0x56781234`, not `0x78563412`. That is a rotation and belongs to O0100.
- A missing mask on a middle term (`x >> 8` without `& 0xFF00`): bytes overlap and OR together.
- Loop with 4 iterations over `u64`: only the low four bytes are reversed into the low half.

## Switching it off

`--disable-opt O0106`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after [#279](https://github.com/Hawkynt/Polypiler/issues/279) (O0100); enables [#275](https://github.com/Hawkynt/Polypiler/issues/275)
to fold `ReverseBytes(LoadLE …)` into `LoadBE`, and native byte-swap selection.
