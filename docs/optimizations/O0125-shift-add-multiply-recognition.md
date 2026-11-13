# O0125 — Recognize multiplication by a constant from shift-add and shift-subtract chains

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#572](https://github.com/Hawkynt/Polypiler/issues/572). Once implemented it is switched off with `--disable-opt O0125`.

## What it does

`(x << 3) + (x << 1)` is `x * 10` written for a CPU whose multiplier was slow or absent; PB-Compiler's
multiply decomposition produces such chains on purpose. On a modern target the multiply is one cycle and
the chain hides the constant from strength reduction, from `LEA` selection and from the reader. PRD
§19.3 names this recovery and its limit: the algebra holds for wrapping arithmetic, not for checked
overflow or C's undefined signed overflow — and never for `|` in place of `+`, where carries are lost.

## Example

### Before

```
%a = Shl.u32[strict] %x, 3
%b = Shl.u32[strict] %x, 1
%r = Add.u32 %a, %b
```

### After

```
%r = Mul.u32 %x, 10
```

## When it applies

every operation in the expression is wrapping (two's complement modulo
`2^w`): an `Add`, `Sub`, `Neg` or `Shl` carrying a checked-overflow, no-signed-wrap or
undefined-overflow contract from its source profile blocks the rewrite; all shift amounts are
constants below `w`; the only non-constant leaf is `x`; the resulting coefficient is not 0, 1 or a
power of two (those are left to constant folding and to the shift form). Sub-expressions with other
users stay alive.

## How it is proven

`exhaustive-8bit` on the same expression shape at `u8` plus `smt-bitvector` at the
matched width against `Mul(x, c)`. A failed, unknown or timed-out obligation (budget 5 s each) leaves
the IR unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- `Or` as combiner: `(x << 3) | (x << 1)` with `x = 5` is `40 | 10 = 42`; `5 * 10` is 50. The bits of
  the two terms overlap and `Or` drops the carry.
- Checked addition (`checked((x << 3) + (x << 1))` in C#): for `x = 0x10000000` the shifts wrap
  silently to `0x80000000` and `0x20000000` and the checked add of those does not overflow, whereas
  `checked(x * 10)` throws. The trap behaviour differs, so the rewrite is refused.
- C signed overflow: with an undefined-overflow `Add`, the source's behaviour for overflowing inputs
  is undefined and may not be replaced by defined wrapping without the profile saying so.
- Logical right shift in the chain (`(x << 3) + (x >> 1)`): not linear in `x`.

## Switching it off

`--disable-opt O0125`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after constant folding and before strength reduction;
enables [#981](https://github.com/Hawkynt/Polypiler/issues/981) to see equal multiplications in interleaved chains, and `LEA`/multiply
selection.
