# O0204 — Name a recovered bit routing that is a compress or an expand

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#545](https://github.com/Hawkynt/Polypiler/issues/545). Once implemented it is switched off with `--disable-opt O0204`.

## What it does

Gathering scattered bits into a field and spreading a field over scattered positions are `PEXT` and `PDEP`
(PRD §20.3 step 6: `BitCompress`, `BitExpand`; §23.3 lists compress/expand among the recovery cases).
[#574](https://github.com/Hawkynt/Polypiler/issues/574) (O0107) turns the mask-driven loops into `ParallelBitExtract`/`ParallelBitDeposit`; code
that spells the same thing with a constant mask as shifts and masks — a Morton de-interleave, a field
gather — reaches the IR as a `BitRoute`. Naming it lets a BMI2 target use one instruction while
[#530](https://github.com/Hawkynt/Polypiler/issues/530) still re-synthesizes it where the instruction is missing.

## Example

### Before

```
%r = BitRoute.u8 %x, [1,3,5,7,=0,=0,=0,=0]
```

### After

```
%r = ParallelBitExtract.u8 %x, 0xAA
```

## When it applies

the mapping is square at a width the intrinsics list (8, 16, 32, 64) and
`Describe`s as `Compress(mask)` — the inputs in `mask`, in ascending order, feed outputs `0 … k−1`, every
other output is `=0`, and `mask` is not `2^k − 1` — or as `Expand(mask)`, the mirror image, emitted as
`ParallelBitDeposit`. No entry is inverted, duplicated or `=1`.

## How it is proven

exact list comparison for the shape, plus `exhaustive-<n>bit` (≤ 24 input bits) or
`smt-bitvector` against the intrinsic's registered decomposition; on a timeout the rewrite does not fire.

## When it must not fire

- Order not preserved: `BitRoute.u8 %x, [3,1,5,7,=0,=0,=0,=0]` — `x = 0x08` gives `0x01`; `ParallelBitExtract(x, 0xAA)` gives `0x02`.
- A duplicated bit: `BitRoute.u8 %x, [1,1,5,7,=0,=0,=0,=0]` — extract cannot copy a bit twice (PRD §20.7).
- Fill that is not zero: `BitRoute.u8 %x, [1,3,5,7,=1,=0,=0,=0]`.
- A contiguous low mask: `BitRoute.u8 %x, [0,1,2,3,=0,=0,=0,=0]` is `And(x, 0x0F)`.

## Switching it off

`--disable-opt O0204`; active at `--opt balanced`, `speed`, `size`, `memory`; off at `none`.

## Interactions with other passes

runs after [#549](https://github.com/Hawkynt/Polypiler/issues/549), [#548](https://github.com/Hawkynt/Polypiler/issues/548) and [#544](https://github.com/Hawkynt/Polypiler/issues/544); [#530](https://github.com/Hawkynt/Polypiler/issues/530) re-synthesizes the
intrinsic when the target's cost table has no `pext`/`pdep`.
