# O0123 — Recognize clamp from nested minimum/maximum and compare-select chains

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#272](https://github.com/Hawkynt/Polypiler/issues/272). Once implemented it is switched off with `--disable-opt O0123`.

## What it does

Limiting a value to a range is written `Math.min(Math.max(x, lo), hi)`, the same with the calls swapped,
or as a conditional chain — CompressionWorkbench's `VectorIcon.Clamp` is `v < 0 ? 0 : v > 255 ? 255 :
v`. Pixel code clamps on every channel. `Clamp` as one operation gives C# `Math.Clamp`, Rust `clamp`,
saturating packs in SIMD, and a result range that removes later bounds checks. `SClamp`/`UClamp` are
defined as the min-of-max nesting; the other nesting and the conditional chains differ from it when the
bounds are crossed, so recognizing those needs `lo ≤ hi`. PRD §4.5, §8 P2 ("clamp").

## Example

### Before

```
%a = SMax.i32 %x, 0
%r = SMin.i32 %a, 255
```

### After

```
%r = SClamp.i32 %x, 0, 255
```

## When it applies

one value `x` is limited from below by `lo` and from above by `hi`; both
limits use the same signedness; for the min-of-max nesting nothing more is needed — it is the
definition of the operation, total also for crossed bounds — except that a *constant* pair with `lo >
hi` is rejected by [#101](https://github.com/Hawkynt/Polypiler/issues/101) (`PLY0261`) and is therefore left alone; for the max-of-min
nesting and the select chains `lo ≤ hi` in that signedness is proven — by comparing constants, or by a
relational range fact for variable bounds; in the select form the selected values are exactly `lo`,
`hi` and `x` and the two comparisons test `x` against those same bounds (strict or non-strict
comparisons are equivalent because the selected value equals the bound). The inner `Min`/`Max` may
have other users and then stays.

## How it is proven

`exhaustive-8bit` over all `(x, lo, hi)` for the min-of-max nesting and over all
triples with `lo ≤ hi` for the max-of-min nesting and the select form (signed and unsigned) plus
`smt-bitvector` at 16, 32 and 64 bit under the assumption `lo ≤ hi`; that assumption is a `RangeFact`
obligation. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Crossed bounds: `lo = 10`, `hi = 5`, `x = 7`: `Min(Max(7, 10), 5)` is 5 but `Max(Min(7, 5), 10)` is
  10 — the nestings disagree. `SClamp(7, 10, 5)` is 5 by definition, so only the min-of-max nesting
  may be rewritten without `lo ≤ hi`.
- Mixed signedness: `UMin(SMax(x, 0), 255)` equals a clamp here only because the lower bound is 0;
  `UMin(SMax(x, -1), 255)` with `x = -1` gives 255.
- Different values: `Min(Max(x, lo), hi)` where the inner call takes `y`.
- Select chain testing another value (`y > hi ? hi : x`).

## Switching it off

`--disable-opt O0123`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after range propagation and after [#157](https://github.com/Hawkynt/Polypiler/issues/157)'s
if-conversion; the result range `[lo, hi]` feeds bounds-check removal and storage narrowing.
