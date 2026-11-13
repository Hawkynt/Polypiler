# O0102 — Recognize population count from the SWAR mask-and-add sequence

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#575](https://github.com/Hawkynt/Polypiler/issues/575). Once implemented it is switched off with `--disable-opt O0102`.

## What it does

The branch-free population count — subtract the shifted odd bits, add bit pairs, add nibbles, multiply
by `0x01010101` and take the top byte — is what programmers and compilers write when no `POPCNT` is
available. On a target that has the instruction it is twelve operations for one; on a 6502 it is a
32-bit multiply for what a table does better. Recovering `PopCount` lets the target choose. PRD §4.5,
§19.2 (expression level), §24 S2.

## Example

### Before

```
%a = LShr.u32[strict] %x, 1
%b = And.u32 %a, 0x55555555
%c = Sub.u32 %x, %b
%d = And.u32 %c, 0x33333333
%e = LShr.u32[strict] %c, 2
%f = And.u32 %e, 0x33333333
%g = Add.u32 %d, %f
%h = LShr.u32[strict] %g, 4
%i = Add.u32 %g, %h
%j = And.u32 %i, 0x0F0F0F0F
%k = Mul.u32 %j, 0x01010101
%r = LShr.u32[strict] %k, 24
```

### After

```
%r = PopCount.u32 %x
```

## When it applies

structural gate: `%r` is the root of a pure expression DAG of at most 24
instructions over exactly one non-constant leaf `x` of type `u<w>` or `i<w>`, `w` ∈ {16, 32, 64},
built only from `And`, `Add`, `Sub`, `Mul`, `LShr`, `AShr` and constants, and containing the constant
`0x55…` of width `w`. Decision: the SMT obligation `root(x) = ZExt(PopCount.u<w>(x))` holds for all
`x`. Nothing else is assumed — in particular an arithmetic shift is accepted exactly when the proof
shows the following mask discards the sign fill. Intermediate values with other users stay alive.

## How it is proven

`smt-bitvector` at the matched width (16, 32 or 64). The 8-bit exhaustive evaluator
does not apply to the rewrite itself (the constants are width-specific); it is used on the 16-bit
instance narrowed to its two bytes as a cross-check of the SMT encoding. A failed, unknown or
timed-out obligation (budget 5 s each) leaves the IR unchanged and reports `not applied: proof
timeout` or the failing precondition.

## When it must not fire

- Wrong mask: with `0x33333332` in place of the second `0x33333333`, `x = 0x00000001` gives 0,
  `PopCount` gives 1.
- 32-bit masks on a `u64` value: `x = 0x0000000100000000` gives 0, `PopCount.u64` gives 1.
- Final shift 16 instead of 24: `x = 0x00000001` gives 0x0101.
- A second variable in the DAG (`(x >> 1) & y`): not a function of one value.

## Switching it off

`--disable-opt O0102`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after constant folding; the rule is tried before
instruction combining may merge the masks; enables [#573](https://github.com/Hawkynt/Polypiler/issues/573) and native population-count
selection.
