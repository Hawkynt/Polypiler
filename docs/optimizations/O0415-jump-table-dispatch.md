# O0415 — Dispatch a dense Switch through an index table

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0415`.

## What it does

A `Switch` over dense values is one bounds check and one indexed jump on any CPU; lowered as a
compare chain it is linear in the number of cases. The machine backends planned so far lower every
`Switch` as a chain ([#931](https://github.com/Hawkynt/Polypiler/issues/931) says so in its lowering table), and nothing decides between the
forms. PB-Compiler dispatches a dense `SELECT CASE` through a word table under three thresholds (PB
O0029). This issue brings that decision into the legalization phase, expressed on the IR so that it is
checked by the interpreter before any backend exists, and it creates the one place where all dispatch
forms of `OPT-CODELAYOUT` compete under the cost model. PRD §4.8 (cost model: native instruction vs
sequence), §21.3.

## Example

### Before

```
entry:
  Switch.i16 %k, other, [10: a], [11: b], [12: c], [13: d]
```

### After

```
entry:
  %k.idx = Sub.i16[wrap] %k, 10
  Switch.i16[dispatch=table] %k.idx, other, [0: a], [1: b], [2: c], [3: d]
```

## When it applies

With `min`/`max` the smallest and largest case value in the selector type's order and `span = max − min + 1` computed without overflow: (1) at least `MinCases = 4` cases; (2) `span ≤ MaxSpan = 256`; (3) `span ≤ MaxSpanPerCase × cases = 4 × cases`; (4) the profile has `dispatch.table`; (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) chooses `table` among the proposals. The rewrite subtracts `min` with `[wrap]` at the selector's full width (no instruction when `min = 0`) and rebases every case value; holes stay uncovered and therefore still select the default. Wrapping subtraction is a bijection on the type, so a selector below `min` becomes an index that, read unsigned, exceeds `span − 1`. **Emission contract** of `Switch.<t>[dispatch=table] %i, default, cases` with case values in `[0, span)`: compare `%i` *unsigned*, at its full width, with `span − 1`; above → `default`; otherwise transfer to entry `%i` of a table of `span` entries in which a hole holds `default`. **Costing** (`DispatchCosting`, worst case per dispatch): `chain(n)` = latency `n × Of(CondBr).Latency`, size `n × Of(CondBr).Size`; `table(span)` = latency `Of(Sub).Latency + table.latency`, size `Of(Sub).Size + table.size + span × table.entrySize`. The driver lists `chain` first, then the proposals in ascending strategy ID, so ties keep the chain.

## How it is proven

`structural`: subtraction of a constant modulo 2^N is a bijection, so value `v` matches case `c` before exactly when `v − min` matches `c − min` after. Checked with `exhaustive-8bit` and `exhaustive-16bit` on the fixtures; no solver, no timeout path.

## When it must not fire

- A *signed* bounds check: `k = 5`, `min = 10` gives index −5, which is ≤ 3 signed; the table is read 5 entries in front of its start.
- Cases {−32768, 32767} on `i16`: `max − min + 1 = 65 536` does not fit the type; computed in 16 bits the span is 0.
- A 32-bit selector narrowed to 16 bits before the check: `0x0001000A` reads as 10 and takes the first arm; the original falls to the default.
- A table without default entries in its holes: cases {10, 11, 13, 14} with `k = 12` jumps through an undefined entry.
- Subtracting with `[trap Overflow]`: `k = −32768`, `min = 10` traps where the original selected the default.

## Switching it off

`--disable-opt O0415`; active at `--opt balanced`, `speed`, `size` and `memory` when the cost model chooses the table, off at `--opt none`.

## Targets

only targets whose multi-way branches Polypiler lowers itself, which a target declares by a `dispatch` section in its `cost.yaml`: assembly and machine-code backends (x86 and x64 NASM, ARM, 6502, Z80) and source targets without an optimizing compiler behind them whose profile asks for it. A source target with a mature compiler — C, C++, C#, Rust, Go, Java, Kotlin — has no `dispatch` section, keeps the plain `Switch` and prints it as its own `switch`/`match`; nothing in its output changes. Real-mode x86 through [#1046](https://github.com/Hawkynt/Polypiler/issues/1046) is excluded as well: PB-Compiler's own selector chooses the dispatch there.

## Interactions with other passes

`DispatchSelection` runs in phase `legalize` after the legalization driver of [#259](https://github.com/Hawkynt/Polypiler/issues/259) (the capability set and wide-integer lowering are final) and before the structuring pass; it runs after `OPT-LAYOUT-IFCHAIN` by phase order. The strategies of `OPT-LAYOUT-DECISIONTREE`, `OPT-LAYOUT-BITTEST` and `OPT-LAYOUT-PERFECTHASH` compete in the same query; `OPT-LAYOUT-TABLEGUARD` refines a chosen table. Preserves the CFG (the terminator keeps its successors).
