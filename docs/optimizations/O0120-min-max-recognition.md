# O0120 — Recognize minimum and maximum from branchless mask selection and compare-select

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#571](https://github.com/Hawkynt/Polypiler/issues/571). Once implemented it is switched off with `--disable-opt O0120`.

## What it does

Branch-free minimum and maximum — `y ^ ((x ^ y) & -(x < y))` and the subtract-and-sign-mask variant —
were written to avoid mispredicted branches on CPUs without conditional moves. Today they hide
`Min`/`Max` from range analysis, from `Math.Min`, from SIMD `PMINSD` and from a 6502 backend that would
rather compare and branch. PRD §19.3 allows the recovery only if sign extension, comparison, overflow
and evaluation count match; the subtract variant is wrong whenever the subtraction overflows. PRD §24
S2.

## Example

### Before

```
%c = ICmp.slt.i32 %x, %y
%z = ZExt.i32 %c
%m = Neg.i32 %z
%d = Xor.i32 %x, %y
%a = And.i32 %d, %m
%r = Xor.i32 %y, %a
```

### After

```
%r = SMin.i32 %x, %y
```

## When it applies

`x` and `y` are SSA values of the same integer type (each is therefore
evaluated once in both versions); mask-select: the mask is exactly `Neg(ZExt(c))` or `SExt(c)` of a
comparison of the *same* two values, and the xor structure selects one of them for each outcome — the
proof decides which intrinsic, including the swapped-comparison spellings; subtract-sign: range facts
must show `Sub(x, y)` does not overflow in the signed sense (`-2^(w-1) ≤ x - y ≤ 2^(w-1) - 1`), and
the shift is arithmetic by exactly `w-1`; compare-select: both selected values are the compared
values. Wrapping arithmetic only; a `Sub` under a checked profile is refused.

## How it is proven

`exhaustive-8bit` over all 65 536 operand pairs for the mask-select and
compare-select forms; `smt-bitvector` at 16, 32 and 64 bit. The subtract-sign form adds a `RangeFact`
obligation for the non-overflow of `x - y`, and its equivalence is proven under that assumption. A
failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and reports `not
applied: proof timeout` or the failing precondition.

## When it must not fire

- Subtract-sign with overflow: `x = 0x7FFFFFFF`, `y = 0xFFFFFFFF` (−1): `x - y` wraps to `0x80000000`,
  the mask is all ones and the result is `0x7FFFFFFF`; `SMin` is −1.
- Signedness: for `x = 5`, `y = 0xFFFFFFFD`, `ICmp.slt` selects `0xFFFFFFFD` (−3) but `ICmp.ult`
  selects 5 — the comparison decides between `SMin` and `UMin`, never the operand types alone.
- Mask from a comparison of other values (`-(x < z)`): a select on an unrelated condition, not a
  minimum.
- Mask from `LShr(d, w-1)` (0 or 1 instead of 0 or −1) in the subtract-sign form: only bit 0 of `d` is
  added.

## Switching it off

`--disable-opt O0120`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after range propagation and if-conversion; enables
[#272](https://github.com/Hawkynt/Polypiler/issues/272) on nested results, range refinement of the result, and `Select`-to-blend vectorization.
