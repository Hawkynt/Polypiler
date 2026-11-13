# O0022 — Contract multiply-add to FMA only when the result is provably identical

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#506](https://github.com/Hawkynt/Polypiler/issues/506). Once implemented it is switched off with `--disable-opt O0022`.

## What it does

A fused multiply-add rounds once where `a * b + c` rounds twice. Compilers that contract silently change
results between targets — the classic source of "works on x64, differs on ARM" in numeric code — and a
translator that does so breaks bit-exact verification outright. PRD §4.8 demands "exact FMA handling", §4.1
forbids unproven floating-point rewrites, and the design rule is: never contract unless exact. The reverse
also holds: a source `Fma` must never be split.

## Example

### Before

```
// facts: %i, %j in [0, 1000] (i32)
%a = SIToFP.f64 %i
%b = SIToFP.f64 %j
%p = Mul.f64 %a, %b
%r = Add.f64 %p, %c
```

### After

```
%a = SIToFP.f64 %i
%b = SIToFP.f64 %j
%r = Fma.f64 %a, %b, %c
```

## When it applies

Contraction requires all of: (1) the product `a × b` is exactly representable in the format for every
possible operand value, so `Mul` rounds nothing and `Fma(a, b, c)` equals `Add(Mul(a, b), c)` bit for bit.
Accepted proofs of (1): both operands are proven integer-valued with `|a| × |b| < 2^p` (`p` = 53 for
`f64`, 24 for `f32`), from conversion instructions and [#94](https://github.com/Hawkynt/Polypiler/issues/94) intervals; or one operand is a literal
power of two `2^k` and the other's magnitude range guarantees the product neither overflows nor becomes
subnormal. Float-domain facts come through [#88](https://github.com/Hawkynt/Polypiler/issues/88); `Unknown` blocks the rewrite. (2) The `Mul` has
no other user (otherwise it stays and nothing is saved). (3) Both operations use round-to-nearest-even in
the same format ([#116](https://github.com/Hawkynt/Polypiler/issues/116)), and status flags are unobservable in the profile (the inexact flag of the
multiply is never raised under (1), so this is only needed for the add, which behaves identically).
(4) The cost profile offers `Fma` natively and [#155](https://github.com/Hawkynt/Polypiler/issues/155) prices it below multiply + add. NaN operands:
under (1) operands are finite, so no NaN payload question arises.

## How it is proven

`range-facts` (exact-product condition) with the lemma "exact product ⇒ fused = separate" proven once by [#71](https://github.com/Hawkynt/Polypiler/issues/71) in the floating-point theory for `f32` (10 s budget) and by the rational-arithmetic test for `f64`. If the lemma proof times out the pass registers no rule and reports `not applied: proof timeout`.

## When it must not fire

- `a = b = 1 + 2^-27` (0x3FF0000002000000), `c = −(1 + 2^-26)` (0xBFF0000004000000): the exact product is `1 + 2^-26 + 2^-54`; separately it rounds to `1 + 2^-26` and the sum is `0.0`; fused the result is `2^-54` (0x3C90000000000000).
- `Mul.f64 %x, 2.0` then `Add %c`: for `x = 1.7e308` the product overflows to +∞ and the sum stays +∞; fused with `c = −1.7e308` the result is the finite `1.7e308`. A power-of-two factor is exact only inside the range.
- Integer-valued operands with product ≥ 2^53: `94906267 × 94906267 = 9007199515875289 > 2^53` is not representable.
- Splitting a source `Fma(a, b, c)` into `a * b + c` on a target without the instruction gives the first counterexample in reverse.

## Switching it off

`--disable-opt O0022`; active at `--opt balanced` and `speed`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after range analysis and [#154](https://github.com/Hawkynt/Polypiler/issues/154); [#259](https://github.com/Hawkynt/Polypiler/issues/259) later maps `Fma` to the native instruction or the exact helper. No pass may run after it that re-creates a separate multiply-add from `Fma`.
