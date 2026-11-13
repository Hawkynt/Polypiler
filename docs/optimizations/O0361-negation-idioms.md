# O0361 — Canonicalize subtraction from zero and multiplication by minus one to negation and cancel double negation

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0361`.

## What it does

Unary minus reaches the IR in three spellings — `0 - x`, `x * -1` and `-x` — depending on the frontend, and
doubled when a macro or an inlined helper negates twice. Value numbering then sees three different values,
recognition rules that match a negation miss two of them, and a machine backend loads a zero and subtracts where
one negate instruction exists. One spelling, and none where two cancel, fixes all three. PRD §4.8 (strength
reduction), §4.5 (recognition needs canonical operands).

## Example

### Before

```
%a = Sub.i32[wrap] 0, %x
%b = Mul.i32[trap Overflow] %x, -1
%n = Neg.i32[wrap] %x
%c = Neg.i32[wrap] %n
%d = FSub.f64[rne] -0.0, %y            // %y proven not NaN
```

### After

```
%a = Neg.i32[wrap] %x
%b = Neg.i32[trap Overflow] %x
%n = Neg.i32[wrap] %x                  // kept while it has other users
                                       // uses of %c read %x
%d = FNeg.f64 %y
```

## When it applies

Per rule, with the facts taken at the instruction through [#88](https://github.com/Hawkynt/Polypiler/issues/88):
N1 needs only the literal 0 as the *left* operand and equal contracts; the trap label is copied.
N2 needs the literal with all bits set; on an unsigned type the contract must be `[wrap]`.
N3 under a non-wrapping contract needs `x ≠ MinValue` from [#94](https://github.com/Hawkynt/Polypiler/issues/94); on unsigned types only `[wrap]`.
N4 needs the literal −0.0 (not +0.0), a rounding mode other than `rdn`, and `CanBeNaN = false` for `x` from
[#347](https://github.com/Hawkynt/Polypiler/issues/347). N5 needs the literal −1.0 and `CanBeNaN = false`. N6 has no precondition: `FNeg` is a
bit operation. The inner instruction of N3 and N6 stays when it has other users; only the outer one is
replaced. For N1, N2, N4 and N5 the original is listed first in the [#155](https://github.com/Hawkynt/Polypiler/issues/155) query, so a profile that
prices `Neg` above the original keeps the original.

## How it is proven

`exhaustive-8bit+smt-bitvector`: every integer rule × contract × signedness over all 8-bit values, the same obligations at 16, 32 and 64 bit and the floating-point rules on `f32` by [#71](https://github.com/Hawkynt/Polypiler/issues/71) (5 s per obligation). A rule whose obligation fails or times out is not registered for that type (`not applied: proof timeout`).

## When it must not fire

- `FSub.f64[rne] 0.0, x` at `x = +0.0` is +0.0 (0x0000000000000000); `FNeg` gives −0.0 (0x8000000000000000), and `FDiv 1.0, …` turns that into +∞ against −∞.
- `FSub.f64[rdn] -0.0, x` at `x = −0.0` is −0.0, because an exact zero sum rounds to −0 toward −∞; `FNeg(−0.0)` is +0.0.
- NaN: `FSub -0.0, x` with `x = 0x7FF8000000000000` returns that NaN; `FNeg` returns 0xFFF8000000000000.
- `Mul.u8[trap Overflow] x, 255` at `x = 1` is 255 without a trap; `Neg.u8[trap Overflow] 1` traps.
- `Neg.i8[trap Overflow] (Neg.i8[trap Overflow] x)` at `x = −128` traps; `x` does not.
- `Neg.i8[sat] (Neg.i8[sat] -128)` is −127, not −128.

## Targets

every target. Source backends print `-x` instead of `0 - x`, machine backends select their negate
instruction; the rewrite never adds an instruction. A target without a native negate gets it back as a
subtraction from [#256](https://github.com/Hawkynt/Polypiler/issues/256), which is why the cost query can refuse N1 there.

## Switching it off

`--disable-opt O0361`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#154](https://github.com/Hawkynt/Polypiler/issues/154) and before value numbering in the same iteration; enables [#158](https://github.com/Hawkynt/Polypiler/issues/158) (one spelling), the recognition rules that match `Neg`, and [#156](https://github.com/Hawkynt/Polypiler/issues/156) of the inner negation.
