# O0107 — Recognize parallel bit extract and deposit from mask-driven loops

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#574](https://github.com/Hawkynt/Polypiler/issues/574). Once implemented it is switched off with `--disable-opt O0107`.

## What it does

Gathering the bits selected by a mask into the low end of a word (extract) and spreading low bits to the
positions of a mask (deposit) are the building blocks of bit-field packing, move generators and
BitPerm's `bmi2` solutions. Without BMI2 they are written as a loop over the mask's set bits. PRD §4.3
requires `ParallelBitExtract` to stay recognizable until lowering and to be recoverable from scalar
loops; PRD §24 S2 lists PEXT/PDEP among the bit hacks to recover.

## Example

### Before

```
loop:                                   ; mask-walk extract, entered from entry
  %m = Phi.u32 [%mask, entry], [%m2, latch]
  %k = Phi.u32 [0, entry], [%k2, latch]
  %r = Phi.u32 [0, entry], [%r2, latch]
  %z = ICmp.eq.u32 %m, 0
  CondBr %z, exit, body
body:
  %n = Neg.u32 %m
  %low = And.u32 %m, %n                 ; lowest set bit of the mask
  %hit = And.u32 %x, %low
  %set = ICmp.ne.u32 %hit, 0
  %one = Shl.u32[strict] 1, %k
  %or = Or.u32 %r, %one
  %r2 = Select.u32 %set, %or, %r
  Br latch
latch:
  %m1 = Sub.u32 %m, 1
  %m2 = And.u32 %m, %m1
  %k2 = Add.u32 %k, 1
  Br loop
exit:
  Ret %r
```

### After

```
%r = ParallelBitExtract.u32 %x, %mask
Ret %r
```

## When it applies

natural loop with one exit and a pure body; `x` and `mask` are
loop-invariant values of the same type `u<w>` (or `i<w>` with logical shifts only); the accumulator
starts at the constant 0 and the output counter `k` at 0; the constant 1 shifted by `k` or `i` has the
full width `w`; no loop value other than the accumulator's exit value is used outside. In the position
form the trip count is exactly `w`. `k` never reaches `w` in a `[strict]` shift because it counts set
mask bits already consumed — this is part of the equivalence proof, not an assumption.

## How it is proven

`exhaustive-8bit` over all 65 536 pairs `(x, mask)` for each loop form, plus
`smt-bitvector` at 16 and 32 bit with the loop unrolled `w` times. At 64 bit the unrolled obligation
is attempted with the same budget; if it times out the rule does not fire at 64 bit and says so. A
failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and reports `not
applied: proof timeout` or the failing precondition.

## When it must not fire

- Position loop that shifts by `i` instead of `k`: it computes `x & mask`. With `x = 0xB2`, `mask =
  0xF0` it gives `0xB0`; the extract gives `0x0B`.
- A 32-bit `1` shifted and then zero-extended into a `u64` accumulator: bits 32..63 can never be set.
- Accumulator starting at a non-zero value: extra bits are OR-ed in that the intrinsic does not
  produce.
- A body that stores each extracted bit to memory: the writes are observable.

## Switching it off

`--disable-opt O0107`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after loop canonicalization and if-conversion of
[#157](https://github.com/Hawkynt/Polypiler/issues/157) (branch and `Select` spellings both reach the rule); enables BMI2 selection and the
permutation synthesizer.
