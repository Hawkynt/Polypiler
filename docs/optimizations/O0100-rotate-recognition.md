# O0100 — Recognize rotate-left and rotate-right from shift pairs

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#279](https://github.com/Hawkynt/Polypiler/issues/279). Once implemented it is switched off with `--disable-opt O0100`.

## What it does

Hash functions and ciphers rotate constantly, and almost no source language has a rotate operator, so
the rotation arrives as two shifts joined by `|`, `^` or `+`. Left as shifts, every backend emits three
operations where C# has `BitOperations.RotateLeft`, Rust has `rotate_left` and x86 has `ROL`. Recovering
the rotation once in the middle-end gives every backend the choice. PRD §4.5 (recognition), §8 P2
("first canonical recognition rules: rotations"), §19.3.

## Example

### Before

```
%a = Shl.u32[strict] %x, 8
%b = LShr.u32[strict] %x, 24
%r = Or.u32 %a, %b
```

### After

```
%r = RotateLeft.u32 %x, 8
```

## When it applies

with width `w` ∈ {8, 16, 32, 64}, both shifts take the *same* SSA value
`x` of type `u<w>` (or `i<w>` with a logical right shift), and one of:
1. constants `n`, `m` with `n + m = w` and `0 < n < w`;
2. variable `n` and `m = Sub(w, n)` with a range fact `1 ≤ n ≤ w-1` on both `[strict]` shifts;
3. both shifts `[mask]`, amounts `n` and `Sub(w, n)` (or `Neg(n)`), any `n` — at `n ≡ 0` both
   operands equal `x` and `Or` yields `x`, a rotation by zero.
The combiner is `Or`; `Xor` and `Add` qualify only under form 1 or 2, where the shifted halves have
no bit in common. `%a` and `%b` may have other users; they stay alive and only `%r` is rewritten.
The direction is `RotateLeft` by the left-shift amount; it is emitted as `RotateRight` by `w-n` when
the source's *right* shift amount is the simpler expression.

## How it is proven

`exhaustive-8bit` (all 256 values × all amounts × three combiners × three forms)
plus `smt-bitvector` at 16, 32 and 64 bit per form. A solver timeout (budget 5 s per obligation)
leaves the IR unchanged and reports `not applied: proof timeout`.

## When it must not fire

- Arithmetic right shift: `Shl.i32 x, 8 | AShr.i32 x, 24` with `x = 0x80000000` gives `0xFFFFFF80`,
  the rotation gives `0x00000080`.
- Amounts that do not sum to the width: `n = 3`, `m = 30` on `u32`.
- `Xor`/`Add` combiner in form 3 at `n = 0`: `x ^ x = 0` and `x + x = 2x`, neither is `x`.
- Different operands: `(x << 8) | (y >> 24)`.

## Switching it off

`--disable-opt O0100`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs after constant folding and range propagation ([#157](https://github.com/Hawkynt/Polypiler/issues/157) phase
`recognize`); enables instruction selection of native rotates and the bit-permutation recovery in
the `permutation` area.
