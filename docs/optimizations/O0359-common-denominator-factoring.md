# O0359 — Divide once and multiply by the exact reciprocal when several float divisions share a power-of-two divisor

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0359`.

## What it does

Normalizing a vector divides each component by the same scale: `x / d`, `y / d`, `z / d` are three
divisions where one reciprocal and three multiplications would do. PB-Compiler factors the common
denominator under its fast-math mode (upstream O0345), since in general `x × (1/d)` differs from
`x / d` in the last bit — and PolyIR evaluates floating-point operations as written: `docs/adr/0002-numeric-contracts.md` (decision 6) admits no fast-math, reassociation or reciprocal flag, `docs/adr/0005-floating-point-tolerance.md` reserves the `fast` profile without a licence, and PRD §4.1 forbids unproven reassociation. `OPT-RED-RECIPREUSE` establishes the one case that
needs no licence: a divisor proven to be a power of two with a representable reciprocal. This issue
applies that proof to several divisions by one run-time divisor outside the loop form handled
there.

## Example

### Before

```
// %d = IntToFloat.f64[rne] (Shl.u32[mask] 1, %n)
%nx = FDiv.f64[rne] %x, %d
%ny = FDiv.f64[rne] %y, %d
%nz = FDiv.f64[rne] %z, %d
```

### After

```
%r  = FDiv.f64[rne] 1.0, %d            // exact
%nx = FMul.f64[rne] %x, %r
%ny = FMul.f64[rne] %y, %r
%nz = FMul.f64[rne] %z, %r
```

## When it applies

(1) All divisions of a group have the same SSA divisor `%d`, the same format and the same rounding mode `m` ([#116](https://github.com/Hawkynt/Polypiler/issues/116)); the group's first member dominates the others ([#128](https://github.com/Hawkynt/Polypiler/issues/128)). (2) `ExactReciprocal.Prove(%d)` is `True` at the first member (`OPT-RED-RECIPREUSE`): `%d` is `±2^k`, finite, non-zero, with a representable reciprocal. Then `1.0 / %d` is exact and each `x_i × r` is the correctly rounded value of the same real number as `x_i / %d`, for every `x_i` including zeros, subnormal results, infinities and NaN payloads. (3) The reciprocal is placed immediately before the first member, in its block: a division by `%d` executed there anyway, so nothing is computed on a path that did not divide by `%d`. (4) Trap attributes of the rewritten `FDiv`s are dropped; with `%d` finite and non-zero they cannot fire. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the group form: `n × Of(FDiv)` against `Of(FDiv) + n × Of(FMul)` under the objective; a group of two is rewritten only where the profile makes a division more than twice as expensive as a multiplication, and a group whose first member already is `1.0 / %d` costs no extra division.

## How it is proven

`ieee-exact`, the proof class of `OPT-RED-RECIPREUSE`: with an exact reciprocal both forms round the same real number in the same mode. No solver, no timeout path.

## When it must not fire

- `d = 3.0` at run time: `5.0 / d` is 1.6666666666666667 and `7.0 / d` is 2.3333333333333335; with `r = 1.0 / d` the products are 1.6666666666666665 and 2.333333333333333 — both components change.
- Two divisors that are equal numbers but different SSA values the compiler has not proven equal: grouping them assumes a fact nobody established.
- `(a / d) + (b / d)` → `(a + b) / d`: with `a = b = 2^1023` and `d = 4` the left side is finite and `a + b` overflows.
- Divisions in two sibling branches with no division before the branch: a reciprocal placed above would execute where the source divided by nothing — harmless for an exact reciprocal, but it is work the source did not do, and the rule refuses it.
- A divisor that is zero on some path (`Shl[unbounded]`): `x / 0.0` is ±∞ or NaN and the proof fails.

## Targets and cost gate

Useful on every target, because no target compiler can prove a run-time divisor to be a power of two and therefore none will perform this rewrite on the emitted code; the gain is largest where division is slow (software floating point, x87-class coprocessors, generated BASIC). The cost model decides per profile from which group size the reciprocal pays; PB-Compiler's own break-even numbers per x87 generation are that project's cost data and belong in profiles. In generated source the result reads as a programmer would write it: `inv = 1 / d` followed by multiplications.

## Switching it off

`--disable-opt O0359`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#158](https://github.com/Hawkynt/Polypiler/issues/158) (equal divisors are one value) and before [#508](https://github.com/Hawkynt/Polypiler/issues/508) and `OPT-RED-RECIPREUSE`: a shared `1.0 / %d` inside a loop is pure and cannot trap, so [#508](https://github.com/Hawkynt/Polypiler/issues/508) hoists it without further help; enables [#506](https://github.com/Hawkynt/Polypiler/issues/506) and [#510](https://github.com/Hawkynt/Polypiler/issues/510) on the multiplications.
