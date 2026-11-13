# O0433 — Classify a byte through a 256-entry class table and dispatch over class ids

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0433`.

## What it does

Lexers, tokenizers and codecs classify bytes: digit, letter, separator, everything else. Written as
tests, and recovered by `OPT-LAYOUT-IFCHAIN` as one `Switch`, that is dozens of case values leading to a
handful of blocks. On a machine with an indexed load but no cheap indexed jump — a 6502, a BASIC with
arrays — the wide dispatch is a long compare chain, while a 256-byte table turns the classification into
one load followed by a dispatch over three or four class ids. PB-Compiler compiles dense byte classifiers
this way (PB O0336, `FsmCompilation`) and builds the table by evaluating the dispatch for all 256 inputs,
which is at the same time its proof. PRD §4.8.

## Example

### Before

```
entry:
  Switch.u8 %c, other, [48: digit], … [57: digit], [65: upper], … [90: upper], [97: lower], … [122: lower]
```

### After

```
global @f.class.0: u8[256] = [0, …, 1 at 48…57, 2 at 65…90, 3 at 97…122, …] [readonly]

entry:
  %c.idx = ZeroExtend.u16 %c
  %c.cls = Load.u8[inbounds #1] @f.class.0, %c.idx        // #1 bounds: "%c.idx <= 255"
  Switch.u8 %c.cls, other, [1: digit], [2: upper], [3: lower]
```

## When it applies

(1) The selector type is `u8` or `i8`; an `i8` selector is reinterpreted with `BitCast.u8` before the zero extension, so −1 indexes entry 255. (2) `named ≥ 16`, `classes ≤ 16` and `named ≥ 2 × classes`, where `named` is the number of case values and `classes` the number of distinct successor blocks including the default. (3) Table entry `p` is the class of the block the original `Switch` selects for bit pattern `p`; the default block is class 0 and is the default of the compact `Switch`, which has one case per other class. (4) The load carries a bounds proof record (producer `O0433`) with the fact that a zero-extended byte is at most 255. (5) Phis of the successors are unchanged: the block and its successor set stay the same. (6) The target's `cost.yaml` has `dispatch.classTable` (`latency`, `size`: the indexed byte load and the 256-byte table) and [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers `classTable` = `dispatch.classTable` + the cheapest form of the compact `Switch` over `direct` = the cheapest form of the original, both priced with `DispatchCosting` of `OPT-LAYOUT-JUMPTABLE`.

## How it is proven

`exhaustive-8bit`: the table is the function being replaced, tabulated over its whole domain; the test recomputes it independently. No solver, no timeout path.

## When it must not fire

- An `i8` selector sign-extended into the index: −1 becomes index −1 instead of 255 and reads in front of the table.
- Numbering classes in block storage order: the same module compiled twice with blocks in another order yields another table; the numbering must come from case values.
- A 16-bit selector whose cases happen to lie below 256: value 0x0130 would index entry 0x30 if truncated and be classified as a digit.
- Applying it to five named values: the 256-byte table replaces five comparisons and costs more than it saves (upstream's reason for the thresholds).
- Dropping the default from the compact `Switch` because "every class has a case": class 0 must still reach the default block.

## Switching it off

`--disable-opt O0433`; active at `--opt balanced` and `speed` when the cost model prefers the table; at `size` and `memory` only if the model does (256 bytes rarely pay); off at `--opt none`.

## Targets

only targets whose multi-way branches Polypiler lowers itself, which a target declares by a `dispatch` section in its `cost.yaml` (`OPT-LAYOUT-JUMPTABLE`): assembly and machine-code backends (x86 and x64 NASM, ARM, 6502, Z80) and source targets without an optimizing compiler behind them whose profile asks for it. A source target with a mature compiler — C, C++, C#, Rust, Go, Java, Kotlin — has no `dispatch` section, keeps the plain `Switch` and prints it as its own `switch`/`match`; nothing in its output changes. Real-mode x86 through [#1046](https://github.com/Hawkynt/Polypiler/issues/1046) is excluded as well: PB-Compiler's own selector chooses the dispatch there. It pays most where an indexed load is cheap and an indexed jump is not (6502, Z80, BASIC with arrays); on a CPU with cheap indexed jumps the direct table of `OPT-LAYOUT-JUMPTABLE` usually wins and the cost model keeps this rewrite off.

## Interactions with other passes

runs in phase `legalize` before `DispatchSelection` of `OPT-LAYOUT-JUMPTABLE`, which then lowers the compact `Switch`; `OPT-LAYOUT-TABLEGUARD` may drop that dispatch's bounds check (class ids are bounded by the table contents only, so normally it stays). Adds a global; preserves the CFG.
