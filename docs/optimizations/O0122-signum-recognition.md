# O0122 — Recognize signum from comparison-difference, shift-or and select idioms

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#579](https://github.com/Hawkynt/Polypiler/issues/579). Once implemented it is switched off with `--disable-opt O0122`.

## What it does

The three-way sign of an integer — −1, 0 or 1 — is the comparator result of every sort and the direction
term of every line-drawing loop. It is written as `(x > 0) - (x < 0)`, as `(x >> 31) | (-x >>> 31)`, or
as a nested conditional. C# has `Math.Sign`, Rust `signum`, PowerBASIC `SGN`; a backend can only use
them when the IR says `Sign`. The near-miss `(x >> 31) | 1`, which never returns 0, is the idiom a
careless matcher would take with it. PRD §4.5, §19.2.

## Example

### Before

```
%p = ICmp.sgt.i32 %x, 0
%n = ICmp.slt.i32 %x, 0
%pz = ZExt.i32 %p
%nz = ZExt.i32 %n
%r = Sub.i32 %pz, %nz
```

### After

```
%r = Sign.i32 %x
```

## When it applies

both comparisons are signed, against the constant 0, of the same value
`x`; in the shift-or form the first shift is arithmetic and the second logical, both by exactly `w-1`,
the second applied to the wrapping negation of the same `x`; in the select form the three results are
exactly 1, −1 and 0 for the three sign classes. The proof obligation `root(x) = Sign(x)` covers all
spellings.

## How it is proven

`exhaustive-8bit` (all 256 values × all forms) plus `smt-bitvector` at 16, 32 and 64
bit. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and reports
`not applied: proof timeout` or the failing precondition.

## When it must not fire

- `(x >> 31) | 1`: `x = 0` gives 1; `Sign(0)` is 0.
- Arithmetic shift on the negated operand: `(x >> 31) | (-x >> 31)` with `x = 5` gives `0 | 0xFFFFFFFF
  = −1`; `Sign(5)` is 1.
- Unsigned comparisons: `ICmp.ult(x, 0)` is never true, so the expression is `x != 0` (0 or 1).
- `AShr(x, w-1)` alone: −1 or 0, never 1.

## Switching it off

`--disable-opt O0122`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after constant folding and if-conversion; enables
comparator simplification and `SGN`/`Math.Sign` emission.
