# O0001 — Fold operations on constants and integer neutral/absorbing elements

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#154](https://github.com/Hawkynt/Polypiler/issues/154). Once implemented it is switched off with `--disable-opt O0001`.

## What it does

Frontends produce constant expressions constantly — table sizes, masks built from shifts, `x * 1` left by
macro expansion — and every later pass (SCCP, range analysis, recognition) works better on literals. Folding
must reproduce the *source* arithmetic exactly: the width, the overflow contract, the rounding. A folder that
computes in the host's `long` or `double` turns into a silent miscompilation. PRD §4.8 (constant evaluation),
§8 P1 exit gate ("target-neutral scalar arithmetic can be interpreted and constant-folded").

## Example

### Before

```
%a = Shl.u32[strict] 255, 8
%b = Or.u32 %a, 255
%c = Mul.u32[wrap] %x, 1
%r = Add.u32[wrap] %c, %b
```

### After

```
%r = Add.u32[wrap] %x, 65535
```

## When it applies

1. All-constant folding: every operand is a literal of the instruction's operand type, the operation is in
   the in-scope list, and evaluating it under its contract yields a value (not a trap). The result literal has
   the instruction's result type; integer results are reduced to the type's width, `[sat]` results clamped.
2. A `[checked]` operation whose evaluation overflows, a division or remainder by constant zero, a
   `[checked]` signed `MinValue / -1`, and a `[strict]` shift with a constant amount ≥ width are **not**
   folded; the instruction stays.
3. Float folding requires the instruction's rounding mode to be round-to-nearest-even and the source profile
   to declare floating-point status flags unobservable. A result that is NaN is folded only when the profile's
   NaN policy is `canonical`; the literal is then the format's canonical quiet NaN.
4. Identities apply to integer types only and only with the constant in the position listed (`0 - x` is not
   `x`; `1 / x` is not `x`). `x*0` and `x&0` fold although `x` is discarded because integer operands are pure
   values; the instruction defining `x` is left for [#156](https://github.com/Hawkynt/Polypiler/issues/156).

## How it is proven

`exhaustive-8bit` for every integer operation × contract and every identity via [#70](https://github.com/Hawkynt/Polypiler/issues/70);
`interpreter` for wider integers and floats (the fold is the operation's defining evaluator in [#118](https://github.com/Hawkynt/Polypiler/issues/118) /
[#116](https://github.com/Hawkynt/Polypiler/issues/116), cross-checked against the oracle above). There is no solver call, so no timeout path.

## When it must not fire

- `Add.i32[checked] 2147483647, 1` — folding to −2147483648 erases the overflow trap.
- `Add.u8[wrap] 200, 100` folded in a wider host type gives 300; the correct result is 44.
- `Add.f64 %x, 0.0` → `%x` is wrong for `x = −0.0` (the sum is `+0.0`).
- `Mul.f64 %x, 0.0` → `0.0` is wrong for `x = NaN` (NaN), `x = ∞` (NaN) and `x = −1.0` (`−0.0`).
- `Add.f32 16777216.0, 1.0` computed in double and narrowed late is still 16777216.0, but `Add.f32 0.1, 0.2` computed in double gives a different bit pattern than the `f32` sum — fold in the instruction's own format.
- `SDiv.i32[wrap] −7, 2` is −3 (truncation), not −4 (floor); `SRem.i32 −7, 2` is −1.

## Switching it off

`--disable-opt O0001`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `canonicalize` and again in `optimize` after [#164](https://github.com/Hawkynt/Polypiler/issues/164) and inlining expose constants; enables SCCP, range analysis and every recognition rule that matches literals.
