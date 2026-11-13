# O0423 — Dispatch a sparse Switch through a balanced tree of comparisons

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0423`.

## What it does

A `Switch` whose values are spread too far for a table falls back to a compare chain: linear in the
number of cases, and the last case pays for all the others. A binary search over the sorted case values
needs no table and a logarithmic number of comparisons. PB-Compiler emits such a tree for sparse
`SELECT CASE` statements with at least eight values (PB O0098). On the IR the tree is ordinary `ICmp` and
`CondBr` instructions, so the reference interpreter proves it before a backend sees it. PRD §4.8.

## Example

### Before

```
entry:
  Switch.i16 %k, other, [1: a], [17: b], [250: c], [1000: d], [4096: e], [5000: f], [9000: g], [20000: h]
```

### After

```
entry:
  %lt = ICmp.slt.i16 %k, 4096
  CondBr %lt, entry.L, entry.R
entry.L:                               // {1, 17, 250, 1000}
  %lt.L = ICmp.slt.i16 %k, 250
  CondBr %lt.L, entry.LL, entry.LR
entry.LL:                              // {1, 17}: equality chain, then other
  ...
entry.LR:                              // {250, 1000}
  %eq = ICmp.eq.i16 %k, 250
  CondBr %eq, c, entry.LR.1
entry.LR.1:
  %eq.1 = ICmp.eq.i16 %k, 1000
  CondBr %eq.1, d, other
```

## When it applies

(1) At least `MinCases = 8` distinct case values. (2) `tree(S)` for the ascending list `S`: with at most 2 values, an equality test per value in ascending order, the last false edge to the default; otherwise `m = S[|S| / 2]` (integer division) and `ICmp.slt %k, m` for a signed selector or `ICmp.ult %k, m` for an unsigned one, the true edge to `tree(values < m)`, the false edge to `tree(values ≥ m)`. (3) All comparisons use the selector's own type and full width. (4) Every phi of an arm block and of the default receives, for each new predecessor block, the input it had for the original block. (5) The profile has a `dispatch` section and [#155](https://github.com/Hawkynt/Polypiler/issues/155) chooses `tree`: latency `depth × Of(CondBr).Latency` with `depth` the largest number of tests on any path, size `tests × Of(CondBr).Size` with `tests` the number of test blocks; for 8 values `depth = 4` and `tests = 11`. (6) For a profile that declares `emits: source` the strategy makes a proposal only if the capability set has `ControlFlow.Goto` or `ControlFlow.LabelledBreak`: the default and every arm shared by several values become merge nodes reached from several leaves, which [#262](https://github.com/Hawkynt/Polypiler/issues/262) would otherwise have to print as a dispatch loop.

## How it is proven

`structural`: each interior node splits the remaining candidates into the values below and not below the median, so a value reaches the leaf that tests it, and a value in no case reaches the default. Checked with `exhaustive-8bit` and `exhaustive-16bit`; no solver, no timeout path.

## When it must not fire

- Unsigned cases {1, 40000} on `u16` compared with `slt`: 40000 is negative as a signed number and is searched in the wrong half.
- A 32-bit selector compared in 16 bits: `0x00010064` reads as 100 and reaches the arm of case 100 (upstream guards exactly this for `LONG` subjects).
- A leaf that jumps to the nearest arm without an equality test: `k = 300` falls between 250 and 1000 and must reach the default.
- Splitting at the median with `≤` on one side and `<` on the other inconsistently: the median value is tested in neither half.

## Switching it off

`--disable-opt O0423`; active at `--opt balanced` and `speed` when the cost model chooses the tree; at `--opt size` and `memory` only if the model does (the tree has more branches than the chain, so it normally does not); off at `--opt none`.

## Targets

only targets whose multi-way branches Polypiler lowers itself, which a target declares by a `dispatch` section in its `cost.yaml` (`OPT-LAYOUT-JUMPTABLE`): assembly and machine-code backends (x86 and x64 NASM, ARM, 6502, Z80) and source targets without an optimizing compiler behind them whose profile asks for it. A source target with a mature compiler — C, C++, C#, Rust, Go, Java, Kotlin — has no `dispatch` section, keeps the plain `Switch` and prints it as its own `switch`/`match`; nothing in its output changes. Real-mode x86 through [#1046](https://github.com/Hawkynt/Polypiler/issues/1046) is excluded as well: PB-Compiler's own selector chooses the dispatch there. In a source target that opts in, the tree prints as nested `if`s, which is less readable than a `switch`; that is the profile author's trade and the reason the section is absent by default.

## Interactions with other passes

a strategy of `DispatchSelection` (`OPT-LAYOUT-JUMPTABLE`), phase `legalize`; competes with the table, the perfect hash and the bit test in one cost query, so a dense set still becomes a table. Changes the CFG and invalidates it.
