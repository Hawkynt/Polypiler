# O0416 — Branch on And/Or/Not trees of pure comparisons through a chain of conditional branches

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0416`.

## What it does

Languages whose `AND`/`OR` are bitwise — PowerBASIC, classic BASICs, Pascal without `{$B-}` — and every
frontend that imports `a & b` on booleans hand the optimizer a branch on a materialized truth value: both
comparisons are computed, turned into values, combined, and tested again. On a target without a
flag-to-register instruction that is several branches of truth-value machinery per operand, and the
second comparison runs even when the first already decided. PB-Compiler lowers such a condition to a
chain of conditional branches (PB O0032). Restated for the IR it is a CFG rewrite, and one with a trap:
skipping the second operand is only invisible when evaluating it could neither trap nor have an effect.
PRD §4.8, §17.1 (evaluation order and traps are observable).

## Example

### Before

```
entry:
  %a = ICmp.sgt.i16 %x, 0
  %b = ICmp.slt.i16 %x, 100
  %c = And.bool %a, %b
  CondBr %c, then, end
```

### After

```
entry:
  %a = ICmp.sgt.i16 %x, 0
  CondBr %a, entry.1, end
entry.1:
  %b = ICmp.slt.i16 %x, 100
  CondBr %b, then, end
```

## When it applies

(1) The condition of the `CondBr` is an `And.bool`, `Or.bool` or `Not.bool` whose interior nodes have no use outside the tree. (2) Every leaf except the first is *sinkable*: the instructions that compute it and are used by nothing else are effect-free ([#114](https://github.com/Hawkynt/Polypiler/issues/114): no call with effects, no store, no volatile or device read) and cannot trap ([#115](https://github.com/Hawkynt/Polypiler/issues/115)) — a division, a checked conversion or a `[bounds]` load qualifies only with a fact from [#94](https://github.com/Hawkynt/Polypiler/issues/94) that excludes the trap. If one leaf is not sinkable the tree is left alone. (3) Translation, with `T`/`F` the targets: `And(l, r)` → test `l`, false to `F`, true to the test of `r`; `Or(l, r)` → test `l`, true to `T`, false to the test of `r`; `Not(n)` → `n` with `T` and `F` exchanged. Leaves are tested in operand order. (4) Each phi of `T` and `F` receives, for every new predecessor, the input it had for the original block. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the chain: alternatives `materialized` = Σ `Of(leaf)` + Σ `Of(And.bool|Or.bool|Not.bool)` + `Of(CondBr)` and `chain` = (number of leaves) × `Of(CondBr)`, where a comparison whose only use is the `CondBr` of its own block is priced by the `CondBr` entry alone (compare-and-branch) and other leaf computations are added to both alternatives.

## How it is proven

`structural+effect-facts` (plus `range-facts` where a trap is excluded): the chain reaches `T` exactly when the tree is true, and the only evaluations it skips are unobservable. No solver, no timeout path.

## When it must not fire

- `IF x <> 0 AND 100 \ x > 3` with `x = 0`: both operands are evaluated in the source, so it traps `DivideByZero`; a chain that skips the division returns normally.
- `IF ready AND Poll()` where `Poll` reads and clears a device status register: the chain skips the read when `ready` is false.
- `IF x AND 3`: `And.i16`, not `And.bool` — the result is a mask, and `x = 4` is false although both operands are non-zero.
- `flag = (x > 0) AND (x < 100) : IF flag …` with `flag` stored: the `And.bool` has a second use; splitting the branch would leave the value to be computed anyway.
- `a(i) > 0 AND i < n` with a `[bounds]` load and `i = n`: the source traps `IndexOutOfRange` before it looks at `i < n`.

## Switching it off

`--disable-opt O0416`; active at `--opt balanced`, `speed`, `size` and `memory` when the cost model prefers the chain, off at `--opt none`.

## Targets

pays most on targets where a truth value has to be built with branches — 8086 without `SETcc`, 6502, Z80, tokenized BASIC — and on PowerBASIC as a target, where the chain prints as nested `IF`. For source targets with short-circuit operators the chain prints as `&&`, `and then`, `AndAlso`, which is the idiomatic spelling of a pure condition, and [#262](https://github.com/Hawkynt/Polypiler/issues/262) reports it as one `ShortCircuit` region (its rule 5 holds by precondition 2). The gate is the target's `cost.yaml`: a profile that prices `CondBr` above a materialized comparison — a wide out-of-order CPU — keeps the single branch, and no source gets a chain its profile did not ask for.

## Interactions with other passes

runs in phase `optimize` after [#158](https://github.com/Hawkynt/Polypiler/issues/158) and [#163](https://github.com/Hawkynt/Polypiler/issues/163) (shared and decided comparisons are gone) and before `OPT-LAYOUT-IFCHAIN`; enables `OPT-LAYOUT-IFCHAIN` and `OPT-LAYOUT-BITTEST` on `Or` trees of equality tests and tighter branch-refined ranges in the later test blocks. Invalidates the CFG of a changed function.
