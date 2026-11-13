# O0163 — Recognize linear interpolation idioms with their exact evaluation form

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#569](https://github.com/Hawkynt/Polypiler/issues/569). Once implemented it is switched off with `--disable-opt O0163`.

## What it does

Blending two values by a weight — `a + (b - a) * t`, or `a * (1 - t) + b * t` — is in every animation,
colour ramp, resampler and frame interpolator: CompressionWorkbench's ratio bar interpolates points with
the first form, MediaDroyd mixes two frames by a position between 0 and a fixed maximum. Graphics
targets have `mix`/`lerp`, C++ has `std::lerp`, and a vectorizer wants to see one operation. The two
spellings are different functions in floating point: only the weighted form returns exactly `b` at `t =
1`, only the delta form is one multiplication. Recognition must therefore name the form, not merge the
two. PRD §4.5, §19.3 (float).

## Example

### Before

```
%d = FSub.f32 %b, %a
%m = FMul.f32 %d, %t
%r = FAdd.f32 %a, %m
```

### After

```
%r = Lerp.f32[form=delta] %a, %b, %t
```

## When it applies

(1) delta form: the subtrahend of the difference and the first addend are
the same SSA value `a`; weighted form: the two weights are `t` and `1 - t` (or `2^s - t`) of the same
SSA value `t`; (2) every operation is in the same format and rounding mode, unfused, and has no other
user that would require the intermediate (intermediates with other users stay alive; the root is still
rewritten); (3) the intrinsic is *defined* as the recorded operation sequence, so the rewrite is exact
by construction — including NaN propagation, signed zeros, overflow to infinity and the inexact
results at `t = 1` of the delta form; (4) integer forms: the operations are wrapping at their recorded
widths; the rule does not require that the product fits — it records the widths so that the intrinsic
reproduces the source, wrap included; a range fact that the product fits is attached when available
and lets later passes use a wider or vector primitive.

## How it is proven

`ValidatedRule` structural identity for floating point (operation-for-operation,
confirmed by bit-exact differential execution); `smt-bitvector` for the integer forms at the recorded
widths with `exhaustive-8bit` for the all-`u8` form over all `(a, b, t)` sampled exhaustively in `a`,
`b` with 16 values of `t`. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR
unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Forms differ at the endpoint: in `f32` with `a = 100000000.0`, `b = 1.0`, `t = 1.0` the delta form
  gives `0.0` (the difference rounds to `−100000000.0`), the weighted form gives `1.0`. Substituting
  one for the other, or a library `lerp` that guarantees exact endpoints, changes the result.
- Narrow fixed point: at `u8`, `a = 200`, `b = 100`, `t = 128`, shift 8: `b - a` wraps to 156, `156 ·
  128` wraps to 0, and the result is 200; the mathematically intended blend is 150. The intrinsic must
  reproduce 200, not repair it.
- Two different weights: `a * (1 - t) + b * u` is a general weighted sum.
- Fused evaluation: computing `a + (b - a) * t` with a fused multiply-add changes the rounding of the
  product.

## Switching it off

`--disable-opt O0163`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after constant folding; never runs after a pass that has
contracted or reassociated floating point (none does in strict mode); enables blend vectorization and
`mix`/`lerp` emission where a backend proves its primitive identical.
