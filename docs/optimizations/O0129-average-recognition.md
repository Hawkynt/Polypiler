# O0129 — Recognize overflow-free average from and-xor-shift and widen-add-shift idioms

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#556](https://github.com/Hawkynt/Polypiler/issues/556). Once implemented it is switched off with `--disable-opt O0129`.

## What it does

The midpoint of two values without overflow — `(a & b) + ((a ^ b) >> 1)` — is how a binary search avoids
the classic `(lo + hi) / 2` defect and how a deinterlacer blends two lines; MediaDroyd's
`Deinterlacer.kt` writes `(c + e) shr 1` on widened samples throughout. SIMD has `PAVGB` (rounding up)
and ARM `UHADD`/`URHADD`; picking them requires knowing the rounding direction, and recognizing the
naive same-width `(a + b) >> 1` requires knowing it cannot overflow. PRD §4.5, §19.2.

## Example

### Before

```
%n = And.u8 %a, %b
%x = Xor.u8 %a, %b
%h = LShr.u8[strict] %x, 1
%r = Add.u8 %n, %h
```

### After

```
%r = UAverageFloor.u8 %a, %b
```

## When it applies

the and/or and the xor take the same two values; the shift is by exactly 1
and its kind (logical or arithmetic) fixes unsigned or signed; widen forms: both operands extended
with the matching extension to at least one more bit, added (plus the constant 1 for ceiling), shifted
by 1 and truncated back; same-width form: range facts prove `a + b` does not wrap in the operand type;
offset form: a fact `a ≤ b` in the shift's signedness. The equivalence obligation against the
intrinsic (under the stated fact, where one is needed) decides.

## How it is proven

`exhaustive-8bit` over all 65 536 pairs for each form and each of the four
intrinsics, plus `smt-bitvector` at 16, 32 and 64 bit; the no-wrap and ordering facts are `RangeFact`
obligations. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Same-width add-shift with wrap: `u8`, `a = b = 0x80`: `(a + b) >> 1` is 0; the average is `0x80`.
- Offset form with `a > b`: `u8`, `a = 200`, `b = 100`: `b - a` wraps to 156, half is 78, `a + 78`
  wraps to 22; the average is 150.
- Rounding direction: floor of (1, 2) is 1, ceiling is 2 — `PAVGB` computes the ceiling, so the two
  intrinsics must not be confused.
- Signed data with a logical shift in the and-xor form: `i8`, `a = -1`, `b = 1` (`0xFF`, `0x01`) gives
  `1 + (0xFE >>> 1) = 0x80` (−128); the signed average is 0.

## Switching it off

`--disable-opt O0129`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after range propagation; enables averaging SIMD selection
and midpoint simplification in search loops.
