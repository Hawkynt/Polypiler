# O0202 — Name a recovered bit mapping that is a byte reversal

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#543](https://github.com/Hawkynt/Polypiler/issues/543). Once implemented it is switched off with `--disable-opt O0202`.

## What it does

PRD §24 Q1 requires reverse-recognizing a byte swap. [#559](https://github.com/Hawkynt/Polypiler/issues/559) (O0106) matches the shift-and-mask
expression and the byte loop directly and hands "other byte permutations" to this area. What it does not
see is a byte reversal that only appears once steps are combined — a rotate by 16 followed by a swap of
the bytes in each half, a BitPerm mapping imported as data — and those arrive as a `BitPermute`.

## Example

### Before

```
%r = BitPermute.u32 %x, [24..31,16..23,8..15,0..7]
```

### After

```
%r = ReverseBytes.u32 %x
```

## When it applies

the operation is a `BitPermute` whose mapping `Describe`s as `ReverseBytes` (width 24, 32, 64 or 128; at 16 bits the shape is `Rotation(8)` and [#547](https://github.com/Hawkynt/Polypiler/issues/547) applies).

## How it is proven

the shape test is an exact comparison of two finite lists; in addition the instance is
proven `exhaustive-<n>bit` (≤ 24 input bits) or `smt-bitvector` against the intrinsic's registered
decomposition. On a timeout the rewrite does not fire and the mapping stays as ground truth.

## When it must not fire

- Bytes swapped inside 16-bit words only: `BitPermute.u32 %x, [8..15,0..7,24..31,16..23]` — `0x12345678` gives `0x34127856`, a byte reversal `0x78563412`.
- Byte reversal with one byte bit-reversed.
- Three of four bytes reversed and the fourth zeroed (`BitRoute`): not a permutation at all.

## Switching it off

`--disable-opt O0202`; active at `--opt balanced`, `speed`, `size`, `memory`; off at `none`.

## Interactions with other passes

runs after [#549](https://github.com/Hawkynt/Polypiler/issues/549), [#544](https://github.com/Hawkynt/Polypiler/issues/544) and [#533](https://github.com/Hawkynt/Polypiler/issues/533) in the same sweep order
(ascending rule id is not enough: the rule declares them as predecessors); the named intrinsic is no
longer touched by [#530](https://github.com/Hawkynt/Polypiler/issues/530) unless the target lacks it.
