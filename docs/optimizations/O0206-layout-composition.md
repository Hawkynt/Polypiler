# O0206 — Compose consecutive layout steps into one bit mapping

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#544](https://github.com/Hawkynt/Polypiler/issues/544). Once implemented it is switched off with `--disable-opt O0206`.

## What it does

PRD §20.4: "compose adjacent layout changes"; PRD §20.3 step 6 keeps the exact mapping as ground truth.
Real code changes layout in steps — swap the bytes, then rotate; extract with one mask, deposit with
another (BitPerm's own BMI2 solution). Each step is already a named operation, so no expression rule
fires; only their composition shows that together they are a single permutation, perhaps a simple one.
PRD §23.3 lists "layout composition" among the recovery cases to test.

## Example

### Before

```
%a = ReverseBytes.u32 %x
%r = RotateLeft.u32 %a, 16
```

### After

```
%r = BitPermute.u32 %x, [8..15,0..7,24..31,16..23]
```

## When it applies

both operations are from the set {`BitPermute`, `BitRoute`, `RotateLeft`,
`RotateRight`, `ReverseBytes`, `ReverseBits`, `ReverseBitsInBytes`, `ParallelBitExtract`, `ParallelBitDeposit`,
`ByteShuffle`, `Shl`, `LShr`, `And`, `ZeroExtend`, `Truncate`} with constant parameters; at least one of the two is
not a plain `Shl`/`LShr`/`And`/`ZeroExtend`/`Truncate`; `%a` has exactly one use; the mappings chain
(`inner.OutputWidth = outer.InputWidth`). The replacement is `Compose(outer, inner)`: `BitPermute` when
bijective, else `BitRoute`. All operations are pure, so no effect lies between the two.

## How it is proven

`exhaustive-<n>bit` (≤ 24 input bits) or `smt-bitvector` on the pair against the
registered decomposition of the replacement; on a timeout the pair stays.

## When it must not fire

- Wrong order: rotate-left-3 then reverse is `[4,3,2,1,0,7,6,5]` (`0xB4` → `0xA5`); reverse then rotate-left-3 is `[2,1,0,7,6,5,4,3]` (`0xB4` → `0x69`).
- Inner value used twice: composing would keep `%a` alive and add an operation.
- A store between the steps: `Store p, %a; %b = Load p; %r = RotateLeft %b, 16` is not a pair of this rule.
- Inverting a routing to "undo" it: `BitRoute[0,1,2,3,0,1,2,3]` then `And 0x0F` composes to `[0,1,2,3,=0,=0,=0,=0]`, not to the identity.

## Switching it off

`--disable-opt O0206`; active at `--opt balanced`, `speed`, `size`, `memory`; off at `none`.

## Interactions with other passes

runs after [#549](https://github.com/Hawkynt/Polypiler/issues/549), [#548](https://github.com/Hawkynt/Polypiler/issues/548) and [#546](https://github.com/Hawkynt/Polypiler/issues/546); before [#533](https://github.com/Hawkynt/Polypiler/issues/533) and the naming
rules, which may give the composed mapping a simple name.
