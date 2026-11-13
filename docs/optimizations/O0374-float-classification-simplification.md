# O0374 — Fold floating-point comparisons and class tests decided by sign, finiteness and NaN facts

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0374`.

## What it does

Translated numeric code is full of defensive tests: `if (x != x)` for NaN, `if (d < 0)` before a square root,
`isfinite` before a division. Where the value came from an integer conversion, a square root of a non-negative
value or a clamped range, the answer is already known. [#163](https://github.com/Hawkynt/Polypiler/issues/163) folds integer comparisons from range
facts; [#347](https://github.com/Hawkynt/Polypiler/issues/347) computes the float facts and names "the simplifications themselves" as someone
else's work. This is that pass. It is exact: it replaces a comparison by its proven outcome and never a float
value by another. PRD §4.4 (float domains), §4.8 (range propagation).

## Example

### Before

```
%f = IntToFloat.f64[rne] %b               // %b: u8
%c = FCmp.olt.f64 %f, 0.0
%s = Sqrt.f64 %f
%u = FCmp.uno.f64 %s, %s
%q = FMul.f64[rne] %y, %y                 // %y: any f64
%n = FCmp.olt.f64 %q, 0.0
%p = FCmp.oge.f64 %q, 0.0
```

### After

```
%f = IntToFloat.f64[rne] %b
%c = false                                // F1: %f in [0, 255], not NaN
%s = Sqrt.f64 %f
%u = false                                // F1: %s cannot be NaN
%q = FMul.f64[rne] %y, %y
%n = false                                // F2: never negative; NaN compares false as well
%p = FCmp.oge.f64 %q, 0.0                 // kept: false for NaN, true otherwise
```

## When it applies

Every fold quotes the fact it used, taken at the comparison through [#88](https://github.com/Hawkynt/Polypiler/issues/88) (so branch
refinement applies). F1 fires on a `True`/`False` answer of the analysis' decider. F2 evaluates the predicate
over the operands' class sets and intervals with NaN removed; if the result is the same for all remaining
pairs and equals the predicate's value on NaN (false for `oeq one olt ole ogt oge ord`, true for
`ueq une ult ule ugt uge uno`), the comparison folds; if no non-NaN value remains the comparison folds to its
NaN value. Zeros compare equal regardless of sign, so a zero-containing interval never decides a strict
comparison with zero. F3 needs the class set from the analysis. An `Unknown` fact, a format other than `f32`
and `f64`, or a comparison with a trap contract refuses. Folding removes an instruction, so [#155](https://github.com/Hawkynt/Polypiler/issues/155) is
not expected to refuse; the original is still listed first.

## How it is proven

`range-facts+smt-bitvector`: the table predicate × class relation × interval relation → outcome is proven for `f32` by [#71](https://github.com/Hawkynt/Polypiler/issues/71)'s floating-point theory (10 s) and applies unchanged to `f64` (the predicates do not depend on the format); each fold then needs only its facts. If the table proof times out the pass registers no rule (`not applied: proof timeout`).

## When it must not fire

- `FCmp.oeq.f64 x, x` folded to true without a NaN fact: false for `x = NaN`.
- `FCmp.oge.f64 (y*y), 0.0` folded to true: false for `y = NaN`. The mirrored `olt` is safely false.
- `FCmp.ogt.f64 x, 0.0` from the interval `[0, 5]`: false for both zeros.
- `IntToFloat.f32[rne]` of the `u32` value 4294967295 is 4294967296.0; a fact `< 2^32` built without outward rounding folds `FCmp.olt f, 4294967296.0` to true wrongly.
- `FCmp.olt.f64 x, +∞` folded to true for a merely NaN-free `x`: false for `x = +∞`.

## Targets

every target: tests and the dead arms behind them vanish from machine code and from generated source
alike. Mature target compilers perform only the literal-operand subset, because they lack the range facts of
the integer side; the rewrite removes code and cannot make source less readable.

## Switching it off

`--disable-opt O0374`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` together with [#163](https://github.com/Hawkynt/Polypiler/issues/163), after [#164](https://github.com/Hawkynt/Polypiler/issues/164) and [#158](https://github.com/Hawkynt/Polypiler/issues/158); enables [#164](https://github.com/Hawkynt/Polypiler/issues/164) on the folded branches, [#156](https://github.com/Hawkynt/Polypiler/issues/156) and [#264](https://github.com/Hawkynt/Polypiler/issues/264) (a web freed of NaN tests).
