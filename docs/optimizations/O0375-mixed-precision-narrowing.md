# O0375 — Execute a binary32 operation that was widened to binary64 and rounded back directly in binary32

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0375`.

## What it does

C promotes `float` operands in many contexts, JavaScript computes everything in doubles and wraps the result in
`Math.fround`, BASIC dialects widen singles before every operation. The IR then holds
`convert-to-f32(op-f64(convert-to-f64 a, convert-to-f64 b))` — two conversions and a double-precision operation
for what is one single-precision operation. For one basic operation this is not an approximation: binary64 is
wide enough that rounding twice gives the correctly rounded binary32 result. [#161](https://github.com/Hawkynt/Polypiler/issues/161) and [#264](https://github.com/Hawkynt/Polypiler/issues/264)
exclude float narrowing because it changes rounding in general; this is the case where it provably does not.
PRD §4.4, §4.8 (operation narrowing), §4.1.

## Example

### Before

```
%aw = FloatConvert.f64[rne] %a            // %a, %b: f32
%bw = FloatConvert.f64[rne] %b
%w  = FAdd.f64[rne] %aw, %bw
%r  = FloatConvert.f32[rne] %w
```

### After

```
%r  = FAdd.f32[rne] %a, %b
```

## When it applies

(1) The outer instruction is `FloatConvert.f32[rne]` without a range-policy trap; its operand is
`FAdd`, `FSub`, `FMul` or `FDiv` at `f64` with `[rne]`; both operands of that operation are widening
`FloatConvert.f64` of `f32` values, or one is an `f64` literal whose value is exactly an `f32` value. (2) The
wide operation has no user other than the outer conversion. (3) NaN: the module's contract is
`nan(canonical)`, or [#347](https://github.com/Hawkynt/Polypiler/issues/347) gives `CanBeNaN = false` for both operands and excludes an invalid
operation (`∞ − ∞`, `0 × ∞`, `0 / 0`, `∞ / ∞`); under `nan(propagate)` payloads would otherwise pass through
two conversions in one form and none in the other. (4) A `[trap <label>]` contract on the wide `FDiv` is
copied to the narrow one; widening is exact, so the trapping conditions are the same. (5) The format pair is
`f32` through `f64`: the wide significand must have at least `2 × 24 + 2 = 50` bits. (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) scores
the narrow operation strictly below the three instructions; the original is listed first.

## How it is proven

`smt-bitvector`: for each of the four operations [#71](https://github.com/Hawkynt/Polypiler/issues/71) proves in the floating-point theory that both forms return the same `f32` bits for all non-NaN results, and the same class for NaN results (60 s per operation). An operation whose proof times out is not registered (`not applied: proof timeout`); the rational-arithmetic measurement below runs regardless.

## When it must not fire

- A chain: `convert-f32((a + b) + c)` in `f64` with `a = 16777216.0`, `b = 1.0`, `c = 1.0` is 16777218.0 (0x4B800001); two `f32` additions give 16777216.0 (0x4B800000), because the first sum rounds.
- A wide intermediate with a second user: narrowing the operation changes the `f64` value that user reads.
- `f64` operands that did not come from `f32`: `0.1 + 0.2` in `f64` rounded to `f32` is not the sum of two `f32` values.
- `f64` arithmetic routed through an 80-bit format: 64 bits of significand do not make double rounding innocuous.
- Under `nan(propagate)`, a NaN operand with a payload: the wide form quiets and converts it twice.

## Targets

every target with an `f32` type: two conversions and a wide operation become one narrow operation,
which halves the work on soft-float targets and doubles the lanes for vectorization. Targets without `f32`
arithmetic of their own (JavaScript) emit the narrow operation as their rounded wide form again, so nothing is
lost. Mature target compilers perform this rewrite themselves; generated source gets shorter, `(float)((double)a
+ (double)b)` becoming `a + b`.

## Switching it off

`--disable-opt O0375`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#158](https://github.com/Hawkynt/Polypiler/issues/158) (so both conversions of one value are one node); enables [#156](https://github.com/Hawkynt/Polypiler/issues/156) of the widening conversions, [#510](https://github.com/Hawkynt/Polypiler/issues/510) at `f32` lanes and [#506](https://github.com/Hawkynt/Polypiler/issues/506) on the narrow operations.
