# O0367 — Test a signed remainder by a power of two against zero with a mask

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0367`.

## What it does

`if (n % 2 == 0)` on a signed `n` does not need the remainder's value, only whether it is zero — and that is
the same question as `(n & 1) == 0` for either sign. [#165](https://github.com/Hawkynt/Polypiler/issues/165) rewrites the *unsigned* remainder to a
mask and expands the *signed* one into a sign-biased shift sequence of five instructions, because the signed
remainder's value depends on the sign; the zero test does not. Catching the test first leaves one `And`. PRD
§4.8 (strength reduction).

## Example

### Before

```
%r = SRem.i32 %x, 8
%c = ICmp.eq.i32 %r, 0
Br %c, multiple, other
```

### After

```
%r = And.i32 %x, 7
%c = ICmp.eq.i32 %r, 0
Br %c, multiple, other
```

## When it applies

(1) The divisor is a literal `d` with `|d| = 2^k`, `1 ≤ k ≤ w − 1` (so the division can neither trap
for a zero divisor nor overflow for `MinValue / −1`). (2) Every user of the remainder is `ICmp.eq.<T> r, 0` or
`ICmp.ne.<T> r, 0`; one user of another kind — arithmetic, a comparison with a non-zero literal, an ordered
comparison, a store, a phi — refuses the rewrite for all users. (3) For a `DivMod`, only the `Rem` projection
is replaced; a `Quot` projection that is still used keeps the `DivMod` alive. (4) The original is listed first
in the [#155](https://github.com/Hawkynt/Polypiler/issues/155) query; the mask is taken only when it scores strictly lower.

## How it is proven

`exhaustive-8bit+smt-bitvector`: `(x rem ±2^k = 0) ⇔ (x and (2^k − 1) = 0)` for truncating, flooring and Euclidean remainders, all 256 operands and `k` in 1…7; the same obligation at 16, 32 and 64 bit by [#71](https://github.com/Hawkynt/Polypiler/issues/71) (5 s). On timeout the rule is not registered at that width (`not applied: proof timeout`).

## When it must not fire

- Comparison with 1: `SRem.i32 -3, 2` is −1, so `x % 2 == 1` is false; `And.i32 -3, 1` is 1 and the test would be true.
- Using the value: `SRem.i32 -7, 8` is −7 and `And.i32 -7, 7` is 1 — equal only in being non-zero.
- An ordered comparison: `x % 4 > 0` is false for `x = −3`; `(x & 3) > 0` is true.
- A divisor that is no power of two: `x % 6 == 0` has no mask.

## Targets

every target at the IR level, with a visible gain on machine-code backends and on targets without
an optimizing compiler behind them (PowerBASIC, 6502, tokenized BASIC), where the signed remainder is a
division or a five-instruction expansion. A mature target compiler performs the same reduction; its cost
profile prices `SRem` by a power of two no higher than the mask, the tie keeps the original, and generated
source still reads `n % 2 == 0`.

## Switching it off

`--disable-opt O0367`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` before [#165](https://github.com/Hawkynt/Polypiler/issues/165) in the same iteration (lower `Order`), so the sign-biased expansion is never built for a zero test; enables known-bits facts on the mask and recognition of bit tests.
