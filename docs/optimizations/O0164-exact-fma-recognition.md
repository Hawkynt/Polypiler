# O0164 — Recognize fused multiply-add only where it is exactly equivalent or the source semantics are fused

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#568](https://github.com/Hawkynt/Polypiler/issues/568). Once implemented it is switched off with `--disable-opt O0164`.

## What it does

`a * b + c` computed as one fused operation rounds once; computed as written it rounds twice. On most
modern CPUs the fused form is faster and more accurate — and it is a *different result*, so PRD §19.3
makes the recognition legal only if the source had fused semantics or an explicit permissive mode was
selected, and PRD §22 requires the strict case to be a refused scenario. That leaves three legitimate
routes: the source profile contracts by definition, the user asked for it, or the product is provably
exact so that both evaluations round the same value once.

## Example

### Before

```
%p = FMul.f32 %a, %b
%r = FAdd.f32 %p, %c
```

### After

```
%r = Fma.f32[licence=exact] %a, %b, %c      ; only under one of the three licences
```

## When it applies

(1) both operations are in the same binary format (`f32` or `f64`) with
round-to-nearest-even and no extended intermediate precision in their profile; (2) the product's only
relevant use is the addition being rewritten — if the product has other users it stays alive for them
and the rewrite still requires a licence; (3) exactly one licence holds: *profile* (attribute on both
operations and the same expression in the source), *mode* (translation-wide setting), or *exact*; (4)
exactness licence: [#89](https://github.com/Hawkynt/Polypiler/issues/89)/[#94](https://github.com/Hawkynt/Polypiler/issues/94) facts bound the number of significant bits of `a`
and `b` so that `p_a + p_b ≤ p` (24 for `f32`, 53 for `f64`), and bound their exponents so that the
product neither overflows nor falls into the subnormal range; typical sources are values converted
from small integers and exact powers of two; (5) if the profile makes floating-point exception flags
observable, the exactness licence additionally requires that both evaluations raise the same flags —
true when the product is exact, because the only rounding is the final one in both — and the mode
licence is not available; (6) NaN payload propagation follows [#116](https://github.com/Hawkynt/Polypiler/issues/116)'s contract for both forms
or the rewrite is refused.

## How it is proven

`range+alias facts` for the licence (`KnownBitsFact`/`RangeFact` for exactness, a
profile fact otherwise) plus the exact-product lemma as a `ValidatedRule`, validated by `exhaustive`
evaluation over all binary16 operand pairs with at most 5 significant bits each and all `c` from a 4
096-value boundary set, comparing fused and unfused results bit for bit; an FP-theory `smt` obligation
is attempted for `f32` under the facts and recorded when it succeeds. A failed, unknown or timed-out
obligation (budget 5 s each) leaves the IR unchanged and reports `not applied: proof timeout` or the
failing precondition.

## When it must not fire

- The PRD §22 case: in `f32`, `a = b = 1 + 2^-12` (`0x3F800800`), `c = −(1 + 2^-11)`. Unfused: `a·b =
  1 + 2^-11 + 2^-24` rounds to `1 + 2^-11`, and the sum is `0.0`. Fused: the exact product is kept and
  the result is `2^-24 ≈ 5.9604645e-08`.
- x87: the product is held at 64-bit significand precision and rounded only when stored; neither the
  IEEE unfused nor the fused `f64` result describes it.
- A profile that licences contraction only inside one expression: `t = a * b; r = t + c;` in two
  statements must not be contracted.
- Exactness claimed from value ranges alone: `a, b < 4096` says nothing about significant bits — `a =
  4095.5` has 13.
- Underflow: `a = b = 2^-80` in `f32` are single-bit significands, but the product `2^-160`
  underflows; the unfused evaluation rounds the product to zero, the fused one adds the exact tiny
  product to `c`.

## Switching it off

`--disable-opt O0164`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after known-bits and range propagation and after
conversions from integers are visible; no other pass contracts floating point (PRD §4.1
`spec/rewrites/`); enables FMA instruction selection.
