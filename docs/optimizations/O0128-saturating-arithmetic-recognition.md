# O0128 — Recognize saturating addition and subtraction from overflow-test and widen-clamp idioms

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#576](https://github.com/Hawkynt/Polypiler/issues/576). Once implemented it is switched off with `--disable-opt O0128`.

## What it does

Pixel and audio code adds with a ceiling: `s = a + b; if (s < a) s = 255`, or widen, add, clamp and
narrow. SIMD has `PADDUSB`/`PADDSW`, Rust `saturating_add`, and PRD §4.5 lists saturating idioms among
the typed SSA patterns to recognize. The overflow test is the delicate part — `s < a` is right, `s <= a`
saturates whenever `b` is zero — so the rule proves each candidate instead of trusting its shape.

## Example

### Before

```
%s = Add.u8 %a, %b
%o = ICmp.ult.u8 %s, %a
%r = Select.u8 %o, 255, %s
```

### After

```
%r = UAddSat.u8 %a, %b
```

## When it applies

overflow-test forms: `s` is the wrapping sum (difference) of exactly the
two values `a`, `b`; the comparison is unsigned, strict for addition (`s < a` or `s < b`) and tests
`a` against `b` for subtraction; the saturation constant is the type's maximum (addition) or 0
(subtraction). Widen-clamp-narrow: both operands have the narrow type `T` and are extended by the
extension matching the clamp's signedness to a type at least one bit wider than `T` (so the wide sum
cannot wrap); the clamp bounds are exactly `T`'s minimum and maximum; the truncation returns to `T`.
Every form is confirmed by the equivalence obligation against the intrinsic.

## How it is proven

`exhaustive-8bit` over all 65 536 operand pairs for every form and all four
intrinsics, plus `smt-bitvector` at 16, 32 and 64 bit. A failed, unknown or timed-out obligation
(budget 5 s each) leaves the IR unchanged and reports `not applied: proof timeout` or the failing
precondition.

## When it must not fire

- Non-strict overflow test: `s <= a` with `a = 5`, `b = 0` gives 255; `UAddSat(5, 0)` is 5.
- Clamp bound 254: `200 + 100` gives 254, the intrinsic gives 255.
- Signed bounds over zero-extended operands: `a = b = 0x80` as `u8` sum to 256 and clamp to 127, while
  `SAddSat.i8(-128, -128)` is −128.
- Extension to the same width (no headroom): `Add.u8` cannot exceed 255, so the clamp never fires and
  the result wraps.
- Subtract with a signed comparison on unsigned data: `a = 0x80`, `b = 1` compares as −128 < 1 and
  yields 0 instead of `0x7F`.

## Switching it off

`--disable-opt O0128`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after [#272](https://github.com/Hawkynt/Polypiler/issues/272) (O0123), which produces the `Clamp`
the widen form matches, and after if-conversion; enables saturating SIMD selection.
