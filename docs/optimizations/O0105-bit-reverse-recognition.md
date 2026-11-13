# O0105 — Recognize bit reversal from swap cascades and shift loops

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#557](https://github.com/Hawkynt/Polypiler/issues/557). Once implemented it is switched off with `--disable-opt O0105`.

## What it does

Deflate and other LSB-first codecs reverse Huffman codes, and CRC code reflects registers; both write
the reversal as a cascade of masked swaps (bits, pairs, nibbles, bytes, halves) or as a loop that shifts
one bit out and one bit in. ARM has `RBIT`, Rust `reverse_bits`, and a table is best on an 8-bit CPU —
none of which a backend can choose while the reversal is fifteen operations. PRD §4.5, §19.2, §24 S2
("bit reversal").

## Example

### Before

```
%a1 = LShr.u32[strict] %x, 1
%a2 = And.u32 %a1, 0x55555555
%a3 = And.u32 %x, 0x55555555
%a4 = Shl.u32[strict] %a3, 1
%a  = Or.u32 %a2, %a4            ; swap adjacent bits
...                              ; the same step with shift 2 / mask 0x33333333 giving %b,
...                              ; shift 4 / mask 0x0F0F0F0F giving %c, shift 8 / mask 0x00FF00FF giving %d
%e1 = LShr.u32[strict] %d, 16
%e2 = Shl.u32[strict] %d, 16
%r  = Or.u32 %e1, %e2            ; swap halves
```

### After

```
%r = ReverseBits.u32 %x
```

## When it applies

cascade: `%r` is the root of a pure DAG of at most `8·log2 w` instructions
over exactly one non-constant leaf `x` of type `u<w>` or `i<w>`, built from `And`, `Or`, `Xor`, `Add`,
`Shl`, `LShr`, `AShr`, `RotateLeft`, `RotateRight` and constants, containing the constant `0x55…` of
width `w`; the obligation `root(x) = ReverseBits.u<w>(x)` holds for all `x`. Loop: natural loop, one
exit, exactly `w` iterations, pure body, `r` starts at a constant (any start value is shifted out
after `w` iterations), no loop value escapes except the exit value of `r`; the shift of `v` may be
logical or arithmetic (only bit 0 is read and `w` iterations consume exactly the original bits) — the
proof decides.

## How it is proven

`exhaustive-8bit` for both forms at width 8 plus `smt-bitvector` at 16, 32 and 64 bit
(the loop unrolled `w` times). A failed, unknown or timed-out obligation (budget 5 s each) leaves the
IR unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Cascade without the halves swap: `x = 0x00000001` gives `0x00008000`; `ReverseBits.u32` gives
  `0x80000000`.
- Loop with 16 iterations over `u32`: `x = 0x00010000` gives 0; the reversal gives `0x00008000`.
- Loop that shifts the accumulator right (`r = (r >> 1) | ((v & 1) << 31)`) while also shifting `v`
  right: it rebuilds `x` unchanged — `x = 0x00000001` gives `0x00000001`.
- A step with mismatched mask and shift (`shift 2`, mask `0x55555555`): not a permutation at all — `x
  = 0x00000003` loses a bit.

## Switching it off

`--disable-opt O0105`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after [#279](https://github.com/Hawkynt/Polypiler/issues/279) (O0100), which may already have
turned the last step into a rotation; the gate accepts both spellings. Enables the bit-permutation
recovery of the `permutation` area and native `RBIT` selection.
