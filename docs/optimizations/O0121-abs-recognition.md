# O0121 — Recognize absolute value from sign-mask idioms and compare-select

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#555](https://github.com/Hawkynt/Polypiler/issues/555). Once implemented it is switched off with `--disable-opt O0121`.

## What it does

The branch-free absolute value `(x ^ m) - m` with `m = x >> (w-1)` appears in audio codecs, motion
estimation (the deinterlacer takes `abs` of every pixel difference) and compiler output. Vector units
have `PABSD`, every library has `abs`, and range analysis can only bound the result when it knows what
the three operations compute. The one input where the definition matters — the most negative value —
must come out exactly as the source produced it. PRD §4.5, §19.2, §24 S2.

## Example

### Before

```
%m = AShr.i32[strict] %x, 31
%t = Xor.i32 %x, %m
%r = Sub.i32 %t, %m
```

### After

```
%r = Abs.i32[wrap] %x
```

## When it applies

the mask is the arithmetic shift of the *same* value `x` by exactly `w-1`;
both uses of the mask are that one value; the arithmetic is wrapping (a checked `Sub`/`Neg` is
refused, because it would trap at the most negative value where the intrinsic wraps); in the
compare-select form the comparison is signed, against the constant 0, and the negated arm is `Neg(x)`
or `Sub(0, x)`.

## How it is proven

`exhaustive-8bit` (all 256 values × all forms) plus `smt-bitvector` at 16, 32 and 64
bit. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and reports
`not applied: proof timeout` or the failing precondition.

## When it must not fire

- Logical shift for the mask: `m = LShr(x, 31)` is 1 for negative `x`; with `x = 0xFFFFFFFE` (−2) the
  result is `(x ^ 1) - 1 = 0xFFFFFFFE`, not 2.
- Shift by `w-2`: with `x = 0x40000001` the mask `AShr(x, 30)` is 1 and the result is `(x ^ 1) - 1 =
  0x3FFFFFFF`, not `0x40000001`.
- Unsigned comparison in the select form: `ICmp.ult(x, 0)` is never true; the expression is the
  identity.
- Checked negation: at `x = 0x80000000` the source traps; `Abs[wrap]` would return `0x80000000`.

## Switching it off

`--disable-opt O0121`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after constant folding; enables range refinement (`Abs` is
non-negative except at the most negative input) and `PABS` selection.
