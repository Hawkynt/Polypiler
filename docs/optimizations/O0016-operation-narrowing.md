# O0016 — Execute operations at the narrowest width that preserves the result

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#161](https://github.com/Hawkynt/Polypiler/issues/161). Once implemented it is switched off with `--disable-opt O0016`.

## What it does

A source `long` that only ever holds 0…1000 costs four add-with-carry steps per addition on a 16-bit CPU and
a `BigInt` or a two-limb helper in JavaScript. PRD §4.4 separates the semantic type from the logical minimum
width and the execution width precisely so that "a public `long` may execute in 32-bit registers" when the
behaviour is equivalent. This pass performs that step for operations; storage is a separate issue.
PRD §4.8 (operation/type narrowing).

## Example

### Before

```
// facts: %a, %b in [0, 1000]
%s = Add.u64[wrap] %a, %b
%t = Mul.u32[wrap] %p, %q
%l = Trunc.u8 %t                // only use of %t
```

### After

```
%a16 = Trunc.u16 %a
%b16 = Trunc.u16 %b
%s16 = Add.u16[wrap] %a16, %b16
%s   = ZExt.u64 %s16
%p8  = Trunc.u8 %p
%q8  = Trunc.u8 %q
%l   = Mul.u8[wrap] %p8, %q8
```

## When it applies

Range form, target type `T` narrower than the operation's type: (1) every operand's range ([#94](https://github.com/Hawkynt/Polypiler/issues/94))
lies inside `T`; (2) the mathematically exact result range lies inside `T`; (3) `T` is unsigned if all
ranges are non-negative, otherwise signed, and the result is zero- or sign-extended accordingly; (4) for
division, remainder and right shifts, which are not low-bit closed, (1)–(3) suffice because all quantities
are exactly representable in `T`; (5) a `[checked]` or `[sat]` operation is narrowed only after
[#163](https://github.com/Hawkynt/Polypiler/issues/163) relaxed it to `[wrap]`.
Demanded-bits form: (1) [#89](https://github.com/Hawkynt/Polypiler/issues/89) demanded-bits query reports that all users of the result demand
only bits `0 … k−1`; (2) the operation is in the low-bit-closed list; (3) `Shl` additionally needs a constant
amount `< k`; (4) the contract is `[wrap]`. Right shifts, divisions, remainders, comparisons and rotates are
never narrowed by demanded bits.
The semantic type of the replaced value is unchanged for every user outside the narrowed expression.

## How it is proven

`range-facts+smt-bitvector` (range form), `smt-bitvector` (demanded form), plus `exhaustive-8bit` for the 16 → 8 instances. 5 s solver budget per obligation; on timeout the operation stays wide (`not applied: proof timeout`).

## When it must not fire

- `Add.u32 %a, %b` with `a`, `b` in `[0, 40000]` narrowed to `u16`: 40000 + 40000 = 80000 wraps to 14464.
- `Trunc.u8 (LShr.u32 %a, 4)` narrowed by demanded bits: for `a = 0x100` the correct result is 0x10; truncating first gives `LShr.u8 0x00, 4 = 0`.
- A value in `[−200, 100]`: it fits neither `i8` nor `u8`; the narrowest legal type is `i16`.
- `UDiv.u32 %a, %b` with only the low byte demanded: `0x100 / 2 = 0x80`, but `0x00 / 0x02 = 0` after truncation.
- `Mul.i32[checked] %a, %b` narrowed to `i16[checked]`: the narrow multiply traps at 32768 where the source would not.

## Switching it off

`--disable-opt O0016`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `specialize` after ranges are final and after [#163](https://github.com/Hawkynt/Polypiler/issues/163); enables [#511](https://github.com/Hawkynt/Polypiler/issues/511) and [#510](https://github.com/Hawkynt/Polypiler/issues/510) (more lanes per register), [#263](https://github.com/Hawkynt/Polypiler/issues/263) has less to legalize, and storage narrowing can follow.
