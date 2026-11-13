# O0109 — Recognize sign extension from shift pairs, xor-subtract and compare-select idioms

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#580](https://github.com/Hawkynt/Polypiler/issues/580). Once implemented it is switched off with `--disable-opt O0109`.

## What it does

Languages without narrow signed types sign-extend by hand: `(x << 24) >> 24`, `(x ^ 0x80) - 0x80`, or
Cipher's `ToSByte`, which masks to a byte and subtracts 256 when the value exceeds 127. Each is a
truncation followed by a sign extension — a single `MOVSX`, a cast in C#, a `CBW` on the 8086 — and
bit-field decoders use the same idiom for odd widths. PRD §4.4 (logical minimum width), §4.5, §19.2.

## Example

### Before

```
%a = Shl.i32[strict] %x, 24
%r = AShr.i32[strict] %a, 24
```

### After

```
%t = Trunc.i8 %x
%r = SExt.i32 %t
```

## When it applies

shift pair: both amounts are the same constant `w-k`, the second shift is
arithmetic. Xor-subtract and compare-select: the constants are exactly `2^(k-1)` (and `2^k`, `2^(k-1)
- 1`) for one `k`, and the operand is provably below `2^k` — by an explicit `And` with `2^k - 1`, or
by a known-bits or range fact; the comparison is consistent with the operand's range (signed or
unsigned compare both hold once `0 ≤ y < 2^k`). Wrapping subtraction; a `Sub` marked checked in the
source profile is not rewritten.

## How it is proven

`exhaustive-8bit` (`w = 8`, every `k` in 1..7, three forms) plus `smt-bitvector` at
16, 32 and 64 bit for the matched `k`; the bound on the operand is a `KnownBitsFact` or `RangeFact`
obligation when no explicit mask is present. A failed, unknown or timed-out obligation (budget 5 s
each) leaves the IR unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Logical second shift: `LShr(Shl(x, 24), 24)` with `x = 0x000000FF` gives `0x000000FF`; the sign
  extension gives `0xFFFFFFFF`.
- Xor-subtract without mask or fact, `k = 8`: `x = 0x00000100` gives `(0x100 ^ 0x80) - 0x80 =
  0x00000100`; `SExt(Trunc.i8 x)` gives 0.
- Uneven shifts `Shl 24`, `AShr 16`: the result is the sign-extended byte times 256.
- Compare-select with the wrong threshold (`y > 128`): `y = 0x80` stays 128 instead of becoming −128.

## Switching it off

`--disable-opt O0109`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after known-bits and range propagation; enables storage
narrowing and cast emission in the backends.
