# O0419 — Recognize a chain of exclusive equality tests on one value as a Switch

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0419`.

## What it does

`if k = 1 … elseif k = 2 … elseif k = 3 …` is a multi-way branch written without one. Line-numbered
BASIC, old C and most decompiled code spell it that way, and bytecode lifters produce nothing else. As a
chain it stays linear in every backend: a source backend prints an `else if` ladder where the target
language has `switch`, `match` or `SELECT CASE`, and a machine-code backend can never choose a table, a
tree or a bit test because it never sees the set of values. PB-Compiler recovers the dispatch in its IR
(PB O0067, pass `SwitchFormation`) and lets each target choose the representation afterwards. This issue
is that recovery for Polypiler's `Switch`. PRD §4.5 (recognition), §4.8.

## Example

### Before

```
t1:
  %e1 = ICmp.eq.i16 %k, 1
  CondBr %e1, one, t2
t2:
  %e2 = ICmp.eq.i16 %k, 2
  CondBr %e2, two, t3
t3:
  %e3 = ICmp.eq.i16 %k, 3
  CondBr %e3, three, other
```

### After

```
t1:
  Switch.i16 %k, other, [1: one], [2: two], [3: three]
```

## When it applies

(1) Every leaf compares the *same* SSA value `k` of an integer type `u<N>`/`i<N>`, N ≤ 64, with constants; interval leaves use the signedness of their own predicates. (2) Every chain block after the head has the preceding test block as its only predecessor, has no phi, is no unwind target, and contains only instructions that are effect-free, cannot trap ([#114](https://github.com/Hawkynt/Polypiler/issues/114), [#115](https://github.com/Hawkynt/Polypiler/issues/115)) and are used only inside that block. (3) The value sets are enumerated in chain order; a value already assigned keeps its first target. The chain is rewritten only if it yields at least 3 distinct values and at most 256 values in total. (4) For every arm target `X` and for the default, each phi of `X` has the same input from every chain block that branches to `X`; that input becomes the input from the head. (5) Case constants are the subject type's values (`ICmp.eq.i16 %k, -1` and a pattern written `0xFFFF` are the same case). The `Switch` lists the cases in ascending value order; chain order is not preserved because the tests are exclusive after rule 3.

## How it is proven

`structural+effect-facts`: for every value of `k` the first matching test of the chain and the `Switch` case select the same block, and the only instructions skipped are unobservable. Checked additionally with `exhaustive-8bit` and `exhaustive-16bit` on the fixtures; no solver, no timeout path.

## When it must not fire

- `if k = 1 then A elseif k = 1 then B`: the second test never fires; a `Switch` built from the later test would run `B`.
- A test block that also increments a counter: for `k = 3` the chain passes through it, the `Switch` jumps past it.
- A test block that divides before it compares: the chain traps for some `k`, the `Switch` does not.
- `if k = 1 … elseif j = 2 …`: two subjects; and `k` reloaded from memory for each test where a store may lie between the loads — two different SSA values.
- `k >= -2 AND k <= 1` on `i8` read with unsigned predicates: the interval is empty unsigned, {−2, −1, 0, 1} signed.
- A float subject: `-0.0 = 0.0` is true and `NaN = NaN` is false, which no case list expresses.
- A test block entered from a second predecessor: values arriving there skipped the earlier tests.

## Switching it off

`--disable-opt O0419`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

every target. A source backend prints one `switch`/`match`/`SELECT CASE` region of [#262](https://github.com/Hawkynt/Polypiler/issues/262) instead of an `else if` ladder, which is the readable form; a machine-code backend gets the value set its dispatch choices need. The cost model is not asked: a `Switch` that a target lowers as a compare chain is the same chain without the absorbed blocks, so no objective gets worse; only the order of the tests may change, which the report states.

## Interactions with other passes

runs in phase `optimize` after `OPT-LAYOUT-SHORTCIRCUIT` (an `Or` of equalities becomes chain blocks with a common target) and `OPT-LAYOUT-SIMPLIFYCFG`, and before [#156](https://github.com/Hawkynt/Polypiler/issues/156); upstream runs it after tail-recursion lowering, which [#512](https://github.com/Hawkynt/Polypiler/issues/512) gives here by its earlier order. Enables every dispatch choice of `OPT-CODELAYOUT` and `OPT-LAYOUT-FSM`. Invalidates the CFG.
