# O0201 — Name a recovered bit mapping that is a rotation

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#547](https://github.com/Hawkynt/Polypiler/issues/547). Once implemented it is switched off with `--disable-opt O0201`.

## What it does

PRD §24 Q1 requires reverse-recognizing a rotate, and PRD §20.3 step 6 asks to "recognize higher-level ops"
while keeping the mapping as ground truth. [#279](https://github.com/Hawkynt/Polypiler/issues/279) finds the two-shift spelling; a rotation written
with masks, in three or more pieces, or arising when two layout steps are composed reaches the IR as a
`BitPermute`. Naming it lets C# print `BitOperations.RotateLeft` and a 6502 use `ROL`.

## Example

### Before

```
%r = BitPermute.u8 %x, [4,5,6,7,0,1,2,3]
```

### After

```
%r = RotateLeft.u8 %x, 4
```

## When it applies

the operation is a `BitPermute` whose mapping `Describe`s as `Rotation(k)`; [#107](https://github.com/Hawkynt/Polypiler/issues/107) lists the width. Emits `RotateLeft` by `k` when `k ≤ w/2`, else `RotateRight` by `w − k`.

## How it is proven

the shape test is an exact comparison of two finite lists; in addition the instance is
proven `exhaustive-<n>bit` (≤ 24 input bits) or `smt-bitvector` against the intrinsic's registered
decomposition. On a timeout the rewrite does not fire and the mapping stays as ground truth.

## When it must not fire

- One entry off: `BitRoute.u8 %x, [4,5,6,7,0,1,2,2]` — bit 7 reads input 2 instead of 3; `x = 0x08` rotates to `0x80`, the mapping gives `0x00`.
- An inverted bit: `BitRoute.u8 %x, [4,5,6,7,0,1,2,~3]` — `x = 0x00` must give `0x80`, a rotation gives `0x00`.
- A rotation of one lane only: `BitPermute.u16 %x, [4,5,6,7,0,1,2,3,8..15]` rotates the low byte; `x = 0x12B4` gives `0x124B`, `RotateLeft.u16 x, 4` gives `0x2B41`.

## Switching it off

`--disable-opt O0201`; active at `--opt balanced`, `speed`, `size`, `memory`; off at `none`.

## Interactions with other passes

runs after [#549](https://github.com/Hawkynt/Polypiler/issues/549), [#544](https://github.com/Hawkynt/Polypiler/issues/544) and [#533](https://github.com/Hawkynt/Polypiler/issues/533) in the same sweep order
(ascending rule id is not enough: the rule declares them as predecessors); the named intrinsic is no
longer touched by [#530](https://github.com/Hawkynt/Polypiler/issues/530) unless the target lacks it.
