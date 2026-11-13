# O0425 — Dispatch a sparse Switch through a collision-free mask hash with key verification

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0425`.

## What it does

Scan codes, opcodes and message numbers are sparse but fixed sets: too wide for an index table, and a
decision tree still costs a logarithmic number of comparisons. If the low bits of the values are pairwise
different, masking the selector is a perfect hash: one `And`, one key comparison to reject everything
that is not a case, one indexed jump. PB-Compiler emits exactly this `AND`-mask family (PB O0100) and
documents why the multiply-shift and modulus families are not worth their multiply on its targets. PRD
§4.8.

## Example

### Before

```
entry:
  Switch.i16 %k, other, [8: a], [17: b], [250: c], [1003: d], [4100: e], [5005: f], [9006: g], [20007: h]
```

### After

```
global @f.hash.0: i16[8] = [8, 17, 250, 1003, 4100, 5005, 9006, 20007] [readonly]

entry:
  %k.h = And.i16 %k, 7
  %k.key = Load.i16[inbounds #1] @f.hash.0, %k.h       // #1 bounds: "0 <= %k.h", "%k.h < 8"
  %k.ok = ICmp.eq.i16 %k.key, %k
  CondBr %k.ok, entry.hit, other
entry.hit:
  Switch.i16[dispatch=table] %k.h, other, [0: a], [1: b], [2: c], [3: d], [4: e], [5: f], [6: g], [7: h]
```

## When it applies

(1) At least `MinCases = 8` distinct case values. (2) `b` is the smallest number in `[1, 8]` for which `v And (2^b − 1)` is pairwise different over all case values `v`; if none exists the strategy does not apply. (3) Slot `s` of the key table holds the case value whose masked value is `s`, and 0 for an empty slot; the inner `Switch` has a case `s` only for occupied slots, so an input that passes the verification on an empty slot (only the value 0 in slot 0 can) still reaches the default. (4) The verification compares the *whole* selector with the key, at the selector's width. (5) The load carries a bounds proof ([#502](https://github.com/Hawkynt/Polypiler/issues/502)'s `BoundsProof`, producer `O0425`) with the facts `0 ≤ h` and `h < 2^b`, which follow from the mask. (6) Phis of arms and default get the original block's input for the new predecessors. (7) The profile has `dispatch.table` and [#155](https://github.com/Hawkynt/Polypiler/issues/155) chooses `hash` = latency `Of(And) + Of(Load.<t>) + Of(CondBr) + table.latency`, size `Of(And) + Of(Load.<t>) + Of(CondBr) + table.size + 2 × 2^b × table.entrySize` (key table and jump table). (8) For a profile that declares `emits: source` a proposal is made only if the capability set has `ControlFlow.Goto` or `ControlFlow.LabelledBreak`: the default is reached from the failed verification and from the inner `Switch`, a merge node [#262](https://github.com/Hawkynt/Polypiler/issues/262) would otherwise print as a dispatch loop.

## How it is proven

`structural`: a member `v` hashes to the slot that holds `v`, passes the verification and takes its case; any other value either fails the verification or lands on an empty slot, both of which lead to the default. Checked with `exhaustive-8bit` and `exhaustive-16bit`; no solver, no timeout path.

## When it must not fire

- No verification: `k = 16` has the low bits of case 8 and would run arm `a`.
- Verification of the low word only on a 32-bit selector: `0x000103EB` matches the stored key 1003 and runs arm `d`.
- An empty slot whose jump entry was left undefined: a non-member hashing there jumps anywhere. Empty slots have no case and so select the default.
- Two case values with equal low bits accepted into one slot: one of the two arms becomes unreachable.
- Key 0 in an empty slot 0 with the inner `Switch` given a case 0: `k = 0` would run that arm although 0 is no case.

## Switching it off

`--disable-opt O0425`; active at `--opt balanced` and `speed` when the cost model chooses the hash; at `size` and `memory` only if the model does; off at `--opt none`.

## Targets

only targets whose multi-way branches Polypiler lowers itself, which a target declares by a `dispatch` section in its `cost.yaml` (`OPT-LAYOUT-JUMPTABLE`): assembly and machine-code backends (x86 and x64 NASM, ARM, 6502, Z80) and source targets without an optimizing compiler behind them whose profile asks for it. A source target with a mature compiler — C, C++, C#, Rust, Go, Java, Kotlin — has no `dispatch` section, keeps the plain `Switch` and prints it as its own `switch`/`match`; nothing in its output changes. Real-mode x86 through [#1046](https://github.com/Hawkynt/Polypiler/issues/1046) is excluded as well: PB-Compiler's own selector chooses the dispatch there. Because it needs an indexed jump it is unavailable on profiles without `dispatch.table`.

## Interactions with other passes

a strategy of `DispatchSelection` (`OPT-LAYOUT-JUMPTABLE`), phase `legalize`; competes with the tree of `OPT-LAYOUT-DECISIONTREE` in one cost query (upstream tries the hash first because it is constant-time; here the profile's numbers decide). The inner `Switch` is not offered to the strategies again. `OPT-LAYOUT-TABLEGUARD` then removes its bounds check. Changes the CFG and adds a global; invalidates both.
