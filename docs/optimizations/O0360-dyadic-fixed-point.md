# O0360 — Run float webs with a proven power-of-two scale in scaled integers

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0360`.

## What it does

Interpolation accumulators, `STEP 0.25` loops and `* 0.5` chains are written in floating point although every
value they ever hold is an integer multiple of one power of two. [#264](https://github.com/Hawkynt/Polypiler/issues/264) turns float webs into
integers only when the values are integral; a web stepping by 0.25 stays in `f64`, and on a target without a
floating-point unit every iteration then pays a soft-float add and a conversion. Holding the web as the integer
`value × 2^S` is exact for power-of-two scales and removes both. PRD §4.4 (logical minimum width apart from the
semantic type), §4.8 (operation/type narrowing).

## Example

### Before

```
head:
  %x = Phi.f64 [pre: 0.0], [body: %x2]
  %i = Phi.u32 [pre: 1], [body: %i2]
  %c = ICmp.ule.u32 %i, 100
  Br %c, body, exit
body:
  %x2 = FAdd.f64[rne] %x, 0.25
  %y  = FloatToInt.i32[rtz, trap Overflow] %x2
  Call.void @plot(%i, %y)
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

### After

```
head:
  %x = Phi.u32 [pre: 0], [body: %x2]            // holds x × 4, range [0, 100]
  ...
body:
  %x2 = Add.u32[wrap] %x, 1                     // 0.25 × 4
  %q  = LShr.u32[strict] %x2, 2                 // truncation of a non-negative value
  %y  = BitCast.i32 %q
  Call.void @plot(%i, %y)
```

## When it applies

A web is rewritten as a whole or not at all. With `p` = 53 for `f64`, 24 for `f32`:
1. *Scale.* Every member has a scale `s ≥ 0` such that `value × 2^s` is an integer: a constant `k × 2^-s`
   (smallest such `s`), `IntToFloat` → 0, `FAdd`/`FSub`/`Phi`/`Select` → the maximum of the operands,
   `FNeg`/`FAbs` → unchanged, `FMul` by a constant `k × 2^-j` → `s + j`, `FMul` by a member of scale 0 →
   unchanged, `FDiv` by `2^j` → `s + j`. The inference is a fixed point over the web, at most 8 rounds; the web
   scale `S` is the maximum and `1 ≤ S ≤ 30`.
2. *Exactness.* For every member the interval of [#347](https://github.com/Hawkynt/Polypiler/issues/347) (fed by [#94](https://github.com/Hawkynt/Polypiler/issues/94) through
   `IntToFloat` and loop bounds) satisfies `max(|lo|, |hi|) × 2^S ≤ 2^p`. Then no operation of the web rounded,
   so the integer `value × 2^S` carries the same real number.
3. *No special values.* No member can be NaN or infinite (implied by 2), and no member that leaves the web
   through an exact `IntToFloat` can be −0.
4. *Boundaries.* Only the conversions in scope leave the web; a `FloatToInt` in another rounding mode, or
   with a range policy whose trap could fire for the proven interval, refuses the web.
5. *Type.* The scaled intervals of all members fit one of `i32`, `u32`, `i64`, `u64`; the operations use
   `[wrap]` because rule 2 excludes overflow.
6. [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the integer form; the float form is listed first, so a tie keeps it.

## How it is proven

`range-facts+smt-bitvector`: the lemma "operands and result within the exactness bound ⇒ the `[rne]`
result equals the real result" is proven once per operation for `f32` by [#71](https://github.com/Hawkynt/Polypiler/issues/71) (10 s budget) and checked
for `f64` against exact rational arithmetic in the test; each web then needs only its range facts. If the lemma
proof times out the pass registers no rule (`not applied: proof timeout`).

## When it must not fire

- Step 0.1 in `f64`: ten additions give 0.9999999999999999, whose truncation is 0; a web scaled by 10 would hold exactly 1.
- Accumulation past the exactness bound: `FAdd.f64[rne] 2251799813685248.0, 0.25` (2^51) returns 2^51 unchanged, the integer web keeps counting.
- Truncation of a negative value: −1.25 truncates to −1; `AShr` of the scaled −5 by 2 is −2. The boundary must divide toward zero.
- Negative zero: `FMul.f64[rne] %x, -1.0` at `x = 0.0` is −0.0 and `FDiv 1.0, …` of it is −∞; an integer zero has no sign.
- A product of two members of scale 2 has scale 4; holding it at scale 2 drops two fraction bits.

## Targets

pays off where a float operation costs more than an integer one: targets without a floating-point
unit (6502, Z80, 8086 without 8087), soft-float helpers and tokenized BASIC integer variables. On targets with
native `f64` arithmetic the cost profile prices `FAdd` at or below `Add` plus the boundary conversions and the
web stays floating point, so generated source keeps `x += 0.25`. No mature target compiler performs this
rewrite itself, because it needs the range proof.

## Switching it off

`--disable-opt O0360`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`; always subject to the cost decision.

## Interactions with other passes

runs in phase `specialize` directly after [#264](https://github.com/Hawkynt/Polypiler/issues/264) and before [#161](https://github.com/Hawkynt/Polypiler/issues/161), which narrows the new integer web further; a `recognize` sweep cannot be requested from `specialize`, so the integer idioms it exposes are picked up only by passes of the same phase.
