# O0430 — Emit no jump to the block that follows in the layout

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0430`.

## What it does

Machine code is a sequence, the IR is a graph. An emitter that writes each block with its terminator
as it stands emits a jump at the end of every `if` without `else`, of every loop body and of every block
whose successor happens to come next — two or three bytes and a taken transfer each, which on an 8086 or
6502 also empties the prefetch queue. PB-Compiler removes a jump to the immediately following instruction
in its assembler (PB O0230). For Polypiler the same rule needs something the planned machine backends do
not have yet: an explicit block order and, per block, the transfers that order requires. This issue
defines that contract — every other block-order issue of `OPT-CODELAYOUT` builds on it — and delivers
the first rule on it. PRD §4.8, §21.3.

## Example

### Before

```
order: entry, then, endif
entry:  CondBr %z, then, endif     plan: BranchJump(%z, taken=then, other=endif)
then:   Call.void @print(1)
        Br endif                   plan: Jump(endif)
endif:  Ret.void                   plan: AsIs
```

### After

```
order: entry, then, endif
entry:  CondBr %z, then, endif     plan: BranchJump(%z, taken=then, other=endif)   // then is next: left for O0421
then:   Call.void @print(1)
        Br endif                   plan: FallThrough(endif)
endif:  Ret.void                   plan: AsIs
```

## When it applies

The IR is not changed; only the plan is. (1) `Jump(X)` → `FallThrough(X)` iff `X` is the block at the next index of `Order`. (2) `BranchJump(c, negated, taken, other)` → `BranchFall(c, negated, taken, other)` iff `other` is at the next index. (3) The last block of `Order` never falls through. (4) An emitter places nothing between two consecutive blocks of a layout that is not executable as a no-op: tables and constants go to their own section. **Plan semantics** (normative for emitters and `LayoutModel`): `FallThrough(n)` emits nothing, control continues at `n`; `Jump(t)` an unconditional transfer; `BranchFall(c, negated, taken, next)` a conditional transfer to `taken` when `c ≠ negated`, else continue at `next`; `BranchJump(c, negated, taken, other)` the same followed by an unconditional transfer to `other`; `AsIs` for `Ret`, `Unreachable` and `Switch`, emitted as the backend lowers them. Phi copies stay on the edges the emitter already resolved; an edge that needs copies and loses its jump keeps the copies in front of the fall-through.

## How it is proven

`structural`: a jump to the instruction that follows transfers control where it would go anyway. No solver, no timeout path.

## When it must not fire

- Eliding by *function* block order after another pass reordered the layout: the block that follows in the IR is not the one that follows in the code.
- The last block of a function ending in `Br` to the first block of the *next* function's label by coincidence of addresses: fall-through off the end of a function is never planned.
- A jump table or literal pool emitted between two blocks: the fall-through would run into data.
- Removing the jump but also the phi copies that sat on that edge: the successor reads stale values.

## Switching it off

`--disable-opt O0430`; active at `--opt balanced`, `speed`, `size` and `memory`; at `--opt none` the identity layout with all jumps is emitted.

## Targets

assembly and machine-code backends only — the x86 NASM backend ([#931](https://github.com/Hawkynt/Polypiler/issues/931)) and the 6502 selector ([#1025](https://github.com/Hawkynt/Polypiler/issues/1025)) in this issue, the other `ISA-*` emitters as they adopt `BlockLayout`. A source backend prints the region tree of [#262](https://github.com/Hawkynt/Polypiler/issues/262) and never sees a block order, so the layout passes do not run for a profile that declares `emits: source`; nothing in generated source changes. Real-mode x86 through [#1046](https://github.com/Hawkynt/Polypiler/issues/1046) is excluded too: PB-Compiler's own assembler lays out and relaxes that code.

## Interactions with other passes

`leg.layout` runs last in phase `legalize`, after the legalization driver of [#259](https://github.com/Hawkynt/Polypiler/issues/259); O0430 runs after every pass that changes `Order` (`OPT-LAYOUT-PLACEMENT`, `OPT-LAYOUT-HOTCOLD`, `OPT-PGO-CHAIN`) and before `OPT-LAYOUT-INVERT`. The cost model is not asked: the rule removes an instruction and adds none.
