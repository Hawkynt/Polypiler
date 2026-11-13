# O0203 — Name a recovered bit mapping that is a bit reversal

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#542](https://github.com/Hawkynt/Polypiler/issues/542). Once implemented it is switched off with `--disable-opt O0203`.

## What it does

Bit reversal is BitPerm's own showcase pattern (`PATTERN_REVERSE`), and its solver's answer on a CPU without
`RBIT` is four masked rotations. [#557](https://github.com/Hawkynt/Polypiler/issues/557) (O0105) recognises swap cascades and shift loops — such
as `reverseBits32` in Cipher's ZK-Crypt — but not that four-rotation form, nor a reversal that results from
composing steps. Those reach the IR as `BitPermute.u<w> %x, [w−1..0]` and are named here (PRD §20.3 step 6).

## Example

### Before

```
%r = BitPermute.u8 %x, [7,6,5,4,3,2,1,0]
```

### After

```
%r = ReverseBits.u8 %x
```

## When it applies

the operation is a `BitPermute` whose mapping `Describe`s as `ReverseBits` (any width [#81](https://github.com/Hawkynt/Polypiler/issues/81) lists) or `ReverseBitsInBytes` (width a multiple of 8, at least 16), which is emitted as `ReverseBitsInBytes.u<w> %x`.

## How it is proven

the shape test is an exact comparison of two finite lists; in addition the instance is
proven `exhaustive-<n>bit` (≤ 24 input bits) or `smt-bitvector` against the intrinsic's registered
decomposition. On a timeout the rewrite does not fire and the mapping stays as ground truth.

## When it must not fire

- Reversal of the low nibble only: `BitPermute.u8 %x, [3,2,1,0,4,5,6,7]` — `0xB4` gives `0xB2`, a full reversal `0x2D`.
- Reversal with the two middle bits not swapped: `BitPermute.u8 %x, [7,6,5,3,4,2,1,0]`.
- Reversal of an `n`-bit code inside a wider word (`ReverseBits` followed by a right shift) — that is a `BitRoute`; the shift stays, as in [#557](https://github.com/Hawkynt/Polypiler/issues/557).

## Switching it off

`--disable-opt O0203`; active at `--opt balanced`, `speed`, `size`, `memory`; off at `none`.

## Interactions with other passes

runs after [#549](https://github.com/Hawkynt/Polypiler/issues/549), [#544](https://github.com/Hawkynt/Polypiler/issues/544) and [#533](https://github.com/Hawkynt/Polypiler/issues/533) in the same sweep order
(ascending rule id is not enough: the rule declares them as predecessors); the named intrinsic is no
longer touched by [#530](https://github.com/Hawkynt/Polypiler/issues/530) unless the target lacks it.
