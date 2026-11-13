# O0426 — Drop the bounds check of a table dispatch whose index range is already proven

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0426`.

## What it does

Every table dispatch starts with a bounds check. Often the check repeats what the program already
established: the index was masked with `And 7`, it is the class id of a classifier table, or the dispatch
sits inside the arm of an outer dispatch on the same value that admitted only `0…7`. PB-Compiler plans to
share such range checks between adjacent and nested dispatches (PB O0101, the half of that document that
its later split PB O0247 does not cover). With range facts as first-class analysis results the rule is
more general and has a proof object: the check is dropped exactly when the fact implies it. PRD §4.4
(facts), §4.8.

## Example

### Before

```
entry:
  %i = And.u16 %x, 7
  Switch.u16[dispatch=table] %i, other, [0: a], [1: b], [2: c], [3: d], [4: e], [5: f], [6: g], [7: h]
```

### After

```
proofs { #1 range: "%i <= 7" }

entry:
  %i = And.u16 %x, 7
  Switch.u16[dispatch=table, inrange #1] %i, other, [0: a], [1: b], [2: c], [3: d], [4: e], [5: f], [6: g], [7: h]
```

## When it applies

(1) The terminator is a `Switch` with `dispatch=table`; `span` is its largest case value plus one. (2) [#94](https://github.com/Hawkynt/Polypiler/issues/94) reports, at the program point of the terminator, an unsigned range of the index contained in `[0, span − 1]`; `Unknown` or a wider range leaves the check in place. (3) Every fact used has evidence other than `assumed`. (4) The facts are stored as a `ProofRecord` of kind `range` with producer `O0426`; the record lists each fact with its defining instruction or branch. (5) [#71](https://github.com/Hawkynt/Polypiler/issues/71) proves `facts ⇒ index ≤u span − 1` within 5 s. **Emission contract** for `[dispatch=table, inrange #id]`: transfer through entry `index` of the table without a comparison; holes still hold the default. The meaning of the `Switch` for the interpreter is unchanged. No cost query: the form removes instructions and adds none.

## How it is proven

`range-facts+smt-bitvector`: each record is discharged by [#71](https://github.com/Hawkynt/Polypiler/issues/71) with a 5 s budget; on timeout the check stays (`not applied: proof timeout`).

## When it must not fire

- `%i = And.u16 %x, 15` in front of a table of 8: the range is `[0, 15]`; index 12 jumps through memory behind the table.
- A fact about the selector *before* rebasing (`10 ≤ k ≤ 13`) applied to another value than the index: the proven value is not the one that indexes.
- A signed range `[−1, 7]`: read unsigned, −1 is 65 535.
- A comparison `i ≤ 7` on one path into a join, and the dispatch after the join: the fact does not hold at the terminator.
- A range promised by the caller's documentation (`--assume`): if the caller breaks it, an unchecked dispatch jumps to an arbitrary address instead of the default arm.

## Switching it off

`--disable-opt O0426`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

only targets whose multi-way branches Polypiler lowers itself, which a target declares by a `dispatch` section in its `cost.yaml` (`OPT-LAYOUT-JUMPTABLE`): assembly and machine-code backends (x86 and x64 NASM, ARM, 6502, Z80) and source targets without an optimizing compiler behind them whose profile asks for it. A source target with a mature compiler — C, C++, C#, Rust, Go, Java, Kotlin — has no `dispatch` section, keeps the plain `Switch` and prints it as its own `switch`/`match`; nothing in its output changes. Real-mode x86 through [#1046](https://github.com/Hawkynt/Polypiler/issues/1046) is excluded as well: PB-Compiler's own selector chooses the dispatch there. The attribute is read only by backends that emit the table themselves; where no table was chosen there is nothing to drop.

## Interactions with other passes

runs in phase `legalize` directly after `DispatchSelection` of `OPT-LAYOUT-JUMPTABLE`, on its tables, on the inner dispatch of `OPT-LAYOUT-PERFECTHASH` (index masked to the slot count) and on the class dispatch of `OPT-LAYOUT-FSM` when that became a table. Preserves the CFG and all analyses.
