# O0130 — Recognize multi-precision addition and subtraction with carry from limb idioms

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#560](https://github.com/Hawkynt/Polypiler/issues/560). Once implemented it is switched off with `--disable-opt O0130`.

## What it does

Big-number, 64-bit-on-32-bit and counter arithmetic propagate a carry by hand: `lo = al + bl; c = lo <
al; hi = ah + bh + c`. Cipher's `Add64_HL` derives the carry from a widened sum. Every CPU has
add-with-carry — `ADC` on x86 and 6502 alike — and C# has `Math.BigMul`/`UInt128`; PRD §4.5 lists
multiple-precision add/carry among the patterns, and PRD §23.4 demands tests where carries overlap,
because the textbook carry test is wrong as soon as a carry-in is added to the same sum.

## Example

### Before

```
%lo = Add.u32 %al, %bl
%c = ICmp.ult.u32 %lo, %al
%cz = ZExt.u32 %c
%t = Add.u32 %ah, %bh
%hi = Add.u32 %t, %cz
```

### After

```
%l = AddWithCarry.u32 %al, %bl, 0
%lo = Sum %l
%c = Carry %l
%h = AddWithCarry.u32 %ah, %bh, %c
%hi = Sum %h
```

## When it applies

carry-out: the tested sum is the wrapping `Add` of exactly the two limbs
and the test is one of the three accepted spellings, each proven equal to bit `w` of the exact sum;
carry-in: the incoming value is provably 0 or 1 (a recognized carry, a `ZExt` of a comparison, or a
known-bits fact) and it is added to the same sum; the carry-out of a sum *with* carry-in must be the
`Or` of both partial carries or the widened three-operand form — a single `s < a` test is refused; all
limbs of one chain have the same type. A limb whose carry-out is unused becomes `AddWithCarry` only if
its carry-in is a recognized carry (otherwise it is an ordinary `Add` and stays).

## How it is proven

`exhaustive-8bit` over all `(a, b, cin)` ∈ 256 × 256 × 2 for sum and carry of every
spelling, plus `smt-bitvector` at 16, 32 and 64 bit; for chains the obligation is per limb. A failed,
unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and reports `not applied:
proof timeout` or the failing precondition.

## When it must not fire

- Single test after a carry-in: `s = a + b + cin; c = s < a` with `a = 5`, `b = 0xFFFFFFFF`, `cin =
  1`: the exact sum is `2^32 + 5`, so the carry is 1, but `s = 5` and `s < a` is false.
- Non-strict test: `lo <= al` with `bl = 0` reports a carry that did not happen (`al = 5`: 5 ≤ 5).
- Signed comparison: `ICmp.slt(lo, al)` — `al = 0x7FFFFFFF`, `bl = 1` gives `lo = 0x80000000`,
  signed-less than `al`, yet no unsigned carry occurred.
- Carry-in that may exceed 1 (an arbitrary value added to the high limb): not an add-with-carry.
- Borrow tested after the subtraction against the wrong operand: `d < b` instead of `a < b`.

## Switching it off

`--disable-opt O0130`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after comparison canonicalization; enables `ADC`/`SBB`
chain selection on 8- and 16-bit targets, and wide-integer formation on 64-bit targets.
