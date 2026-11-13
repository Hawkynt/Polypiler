# O0008 — Simplify operations decided by range and known-bit facts

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#163](https://github.com/Hawkynt/Polypiler/issues/163). Once implemented it is switched off with `--disable-opt O0008`.

## What it does

Dynamic and legacy sources guard everything: `& 0xFF` after a value that is already a byte, `if (i < n)`
inside a loop that already runs to `n`, checked additions that cannot overflow. The analyses know these
facts; this pass is what turns a fact into less code. It is also what makes overflow checks disappear from
C# `checked` code and PowerBASIC range checks from generated output. PRD §4.4, §4.8 ("range/known-bit
propagation"), §8 P1.

## Example

### Before

```
// facts: %x in [0, 15]; %a, %b in [0, 1000]
%m = And.u32 %x, 255
%c = ICmp.ult.u32 %m, 16
%s = Add.i32[checked] %a, %b
```

### After

```
%c = true                       // %m replaced by %x, comparison decided
%s = Add.i32[wrap] %a, %b
```

## When it applies

Every rewrite quotes the fact it uses, and the fact must hold **at the instruction**: [#88](https://github.com/Hawkynt/Polypiler/issues/88) is
queried for the value at that program point, so a fact established by a dominating branch is usable only in
the region that branch dominates. A query answering `Unknown` blocks the rewrite. Per rule:
comparisons — the two ranges are disjoint in the direction decided, or equal singletons; `And` — `known-zero(x)
⊇ ~m`; `Or` — `known-one(x) ⊇ m`; `URem` — `max(x) < c`; extension round trip — `range(x)` fits the narrow
type under the extension's signedness; `[checked]` → `[wrap]` — the mathematically exact result range of the
operation over the operand ranges lies inside the result type; `[mask]` → `[strict]` — `0 ≤ min(n)` and
`max(n) ≤ width − 1`; `[sat]` → `[wrap]` as for `[checked]`.

## How it is proven

`range-facts+smt-bitvector`: each rule is proven once per width class (8, 16, 32, 64) as a parametric obligation by [#71](https://github.com/Hawkynt/Polypiler/issues/71) with a 5 s budget; a timeout disables the rule at that width (`not applied: proof timeout`).

## When it must not fire

- `ICmp.ult.u32 %x, 16` with `x` in `[0, 16]`: 16 is not below 16 — the boundary value decides.
- `Add.i32[checked] %a, %b` with `a` in `[0, 2147483647]`, `b` in `[0, 1]`: the sum reaches 2147483648 and the trap is real.
- A fact `x < 16` from `if (x < 16) { … }` used after the join of both arms: outside the guarded region `x` may be 4000000000.
- `And.u32 %x, 0xFF` with `x` in `[0, 256]`: `256 & 0xFF = 0`, not 256.
- Signedness: `x` in `[−1, 1]` as `i8` zero-extended is 255 for −1; `ZExt(Trunc)` round trip is not the identity, `SExt` is.
- `Shl.u32[mask] %x, %n` with `n` in `[0, 32]`: at 32 the masked shift yields `x`, the strict shift is a verifier error.

## Switching it off

`--disable-opt O0008`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#164](https://github.com/Hawkynt/Polypiler/issues/164) and [#158](https://github.com/Hawkynt/Polypiler/issues/158); its contract relaxations enable [#156](https://github.com/Hawkynt/Polypiler/issues/156) of formerly trapping instructions, [#165](https://github.com/Hawkynt/Polypiler/issues/165), [#161](https://github.com/Hawkynt/Polypiler/issues/161) and the recognition rules that require `[strict]` shifts. It invalidates facts only for the values it replaces.
