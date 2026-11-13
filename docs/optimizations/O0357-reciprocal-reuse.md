# O0357 — Multiply by the exact reciprocal of a power-of-two float divisor, once per loop for an invariant one

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0357`.

## What it does

A floating-point division costs several times a multiplication on every CPU and far more on a software
or coprocessor implementation. Dividing repeatedly by the same value invites computing its reciprocal
once — but `x / d` and `x × (1/d)` differ in the last bit for almost every divisor, and PolyIR evaluates floating-point operations as written: `docs/adr/0002-numeric-contracts.md` (decision 6) admits no fast-math, reassociation or reciprocal flag, `docs/adr/0005-floating-point-tolerance.md` reserves the `fast` profile without a licence, and PRD §4.1 forbids unproven reassociation. One family of
divisors is exempt by arithmetic, not by licence: for a power of two the reciprocal is exact and both
forms round the same real number. PB-Compiler's reciprocal reuse (upstream O0338) has exactly this
"strict exact reciprocal" tier next to its fast-math tier; this issue borrows the strict tier and its
loop form.

## Example

### Before

```
%a = FDiv.f64[rne] %x, 8.0

// %d = IntToFloat.f64[rne] (Shl.u32[mask] 1, %n), defined before the loop
body:
  %q = FDiv.f64[rne] %v, %d
```

### After

```
%a = FMul.f64[rne] %x, 0.125

pre:
  %r = FDiv.f64[rne] 1.0, %d          // exact
  Br body
body:
  %q = FMul.f64[rne] %v, %r
```

## When it applies

(1) *Exact reciprocal*: `ExactReciprocal.Prove(d)` is `True`: `d` is finite, non-zero, `|d| = 2^k`, and `2^−k` is representable in the format — `−1023 ≤ k ≤ 1023` for `f64`, `−127 ≤ k ≤ 127` for `f32` (subnormal powers of two qualify as reciprocals; `k = −1074` does not, its reciprocal overflows) ([#116](https://github.com/Hawkynt/Polypiler/issues/116)). For `IntToFloat` of `Shl 1, n` the shift contract must exclude a zero result — `[strict]` or `[mask]`, never `[unbounded]` — and the conversion of a single set bit is exact for every width up to 64 ([#117](https://github.com/Hawkynt/Polypiler/issues/117), [#118](https://github.com/Hawkynt/Polypiler/issues/118)). (2) The division and the multiplication use the same format and the same rounding mode `m`; then both are the correctly rounded value of the same real number `x × 2^−k` for every `x`, including subnormal results, overflow, ±0 (the sign is the product of the signs either way), ±∞ and NaN (the payload of `x` is propagated by either operation). (3) Trap attributes of the `FDiv` (`[trap DivideByZero]`, `[trap InvalidOperation]`) are dropped: with `d` finite and non-zero neither `x / d` nor `x × (1/d)` can raise them. (4) Form 2: `d` is defined outside the loop ([#128](https://github.com/Hawkynt/Polypiler/issues/128)); the reciprocal is placed in the preheader, which [#508](https://github.com/Hawkynt/Polypiler/issues/508) creates if missing; all divisions by `d` in the loop with rounding mode `m` and the same format are rewritten. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the multiplication: form 1 compares `Of(FMul)` with `Of(FDiv)` and requires `ICostModel.IsAvailable("float.reciprocal-multiply")`; form 2 compares `LoopCost(trip)` over the division with one division plus `LoopCost(trip)` over the multiplication, `trip` from [#350](https://github.com/Hawkynt/Polypiler/issues/350) or the profile's `unknownTripCount`.

## How it is proven

`ieee-exact`: for `d = ±2^k` with representable reciprocal, `fl_m(x / d) = fl_m(x × 2^−k) = fl_m(x × fl(1/d))` because `fl(1/d) = 2^−k` exactly and IEEE 754 defines both operations as the correctly rounded exact result; verified exhaustively for `f32` on four divisors and by differential testing for `f64`. No solver, no timeout path.

## When it must not fire

- `d = 3.0`: `5.0 / 3.0` is 1.6666666666666667 (0x3FFAAAAAAAAAAAAB), `5.0 × (1.0 / 3.0)` is 1.6666666666666665 (0x3FFAAAAAAAAAAAAA).
- `d = 10.0`: `3.0 / 10.0` is 0.3, `3.0 × 0.1` is 0.30000000000000004.
- `d = 2^−1074` (the smallest subnormal, 5e−324): `d / d` is 1.0, but `1/d` overflows to +∞ and `d × ∞` is +∞.
- `d = IntToFloat (Shl.u32[unbounded] 1, n)` with `n = 32`: `d = 0.0`, and `x / 0.0` is ±∞ or NaN while the shift made no power of two.
- A divisor that is a power of two only on one path of a `Phi` (`2.0` or `3.0`): the proof fails for the whole value.
- Different rounding modes: `FDiv[rtz]` replaced by `FMul[rne]` rounds differently for subnormal results.

## Targets and cost gate

Pays off on targets where the emitted division is executed as a division: machine-code and assembly backends, software floating point on 8- and 16-bit CPUs, x87-class coprocessors, generated BASIC. Their profiles list the form `float.reciprocal-multiply`. C, C++, Rust and the managed JIT compilers perform the constant power-of-two case themselves — it is exact, so they are allowed to — and a profile for them does not list the form: `x / 8.0` stays in the generated source as written. Form 2 is useful on every target, since no target compiler can prove a run-time divisor to be a power of two. PB-Compiler's break-even table for x87 generations (reuse from 4, 3, 2 and 2 divisions on 8087, 80387, 80486 and Pentium class) is that project's cost data and belongs in the corresponding profiles, not in this pass. The form name `float.reciprocal-multiply` and the `forms` list of `cost.yaml` that carries it are defined by `OPT-MEM-FORMS`.

## Switching it off

`--disable-opt O0357`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#508](https://github.com/Hawkynt/Polypiler/issues/508) (invariant divisors are already outside the loop) and after `OPT-RED-COMMONDENOM` (which shares reciprocals inside a block); enables [#506](https://github.com/Hawkynt/Polypiler/issues/506) on targets with `fp-contract=on` and [#510](https://github.com/Hawkynt/Polypiler/issues/510) (vector multiply instead of divide).
