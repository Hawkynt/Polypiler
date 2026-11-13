# O0428 — Move cold blocks out of the function body into a cold region

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0428`.

## What it does

`OPT-LAYOUT-PLACEMENT` puts cold blocks at the end of their function. They still sit between that
function's hot code and the next function's: diagnostics, abort paths and string references occupy the
bytes a tight loop and its callee would otherwise share. PB-Compiler plans to relocate such blocks behind
the procedure or into a separate region of the image (PB O0105). On the layout contract this is one more
field — where the cold part starts — plus the rule that nothing may fall through across the boundary.
PRD §4.8, §21.3.

## Example

### Before

```
module order:  f.entry, f.loop, f.next, f.fail | g.entry, g.work, g.fail
f.fail and g.fail are cold (end in Unreachable)
```

### After

```
hot region:    f.entry, f.loop, f.next, g.entry, g.work
cold region:   f.fail, g.fail
ColdStart(f) = 3, ColdStart(g) = 2
```

## When it applies

(1) The blocks from `ColdStart` to the end of `Order` are exactly the function's cold blocks (`BranchProbability.IsCold`); a function whose cold blocks are not a tail of its order is first reordered by `OPT-LAYOUT-PLACEMENT`'s rule and otherwise left unsplit. (2) The entry block is never cold-placed. (3) Plans are rebuilt with the boundary as an end of sequence: the last hot block and the last cold block never fall through. (4) The cold part gets a local label `<function>.cold`; no symbol other than branch targets refers into it. (5) Objective ([#155](https://github.com/Hawkynt/Polypiler/issues/155)'s `Objective`): at `speed` every function with at least one cold block is split; at `balanced` only when the split adds no instruction — every cold block ends in `Ret`, `Unreachable` or a `Jump` it needed anyway, and no hot block fell through into a cold one; at `size` and `memory` never, because a 6502 branch into the cold region is relaxed from 2 to 5 bytes.

## How it is proven

`structural`: the order is a legal `BlockLayout` order and the plans contain no fall-through across the boundary. No solver, no timeout path.

## When it must not fire

- A hot block that falls through into the first cold block: after the move it runs into the next function's entry.
- A cold block that continues into hot code — possible once coldness comes from measured counts, an error arm that logs and carries on: moved without a jump, it runs into another function's cold block.
- Splitting under `--opt size` on a 6502: each `BNE fail` becomes `BEQ +3 / JMP fail`, three bytes more per guard, for no saving.
- Placing the entry block in the cold region because a function always traps: its symbol would no longer label its first byte.

## Switching it off

`--disable-opt O0428`; active at `--opt speed`, and at `--opt balanced` for functions whose split adds no instruction; off at `size`, `memory` and `none`.

## Targets

assembly and machine-code backends only — the x86 NASM backend ([#931](https://github.com/Hawkynt/Polypiler/issues/931)) and the 6502 selector ([#1025](https://github.com/Hawkynt/Polypiler/issues/1025)) in this issue's tests, the other `ISA-*` emitters as they adopt `BlockLayout`. A source backend prints the region tree of [#262](https://github.com/Hawkynt/Polypiler/issues/262) and never sees a block order, so the pass does not run for a profile that declares `emits: source`; nothing in generated source changes. Real-mode x86 through [#1046](https://github.com/Hawkynt/Polypiler/issues/1046) is excluded too: PB-Compiler's own assembler lays out that code.

## Interactions with other passes

runs in phase `legalize` after `OPT-LAYOUT-PLACEMENT` and before `OPT-LAYOUT-JUMPNEXT` and `OPT-LAYOUT-INVERT`, which then see the boundary; relaxation in the encoders runs afterwards. With a profile, `OPT-PGO-CHAIN` and `OPT-PGO-FALLTHROUGH` run before it and leave the measured-cold blocks at the end of the order, where this pass finds them.
