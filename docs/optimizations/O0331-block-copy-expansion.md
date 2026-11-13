# O0331 — Expand a small constant-size block copy into scalar load/store pairs

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0331`.

## What it does

Assigning one small record to another is a block copy of four or eight bytes. As a `Memory.Copy` it becomes
a call or a string instruction with set-up on every target that has no optimizing compiler behind it —
PowerBASIC, a 6502, an 8086 — while two word moves would do. PB-Compiler expands such copies into paired
scalar moves up to a per-CPU store budget (upstream O0015, pass `MemoryRoutineSpecialization.TryMemcpy`).
PRD §4.8 (operation form chosen by the cost model).

## Example

### Before

```
Memory.Copy.u8 %dst, %src, 7
```

### After

```
// profile lists LoadLE/StoreLE u32 and u16 as native: plan 4 + 2 + 1
%a  = LoadLE.u32[inbounds #1] %src
StoreLE.u32[inbounds #2] %dst, %a
%s4 = PtrAdd.ptr<u8> %src, 4
%d4 = PtrAdd.ptr<u8> %dst, 4
%b  = LoadLE.u16[inbounds #1] %s4
StoreLE.u16[inbounds #2] %d4, %b
%s6 = PtrAdd.ptr<u8> %src, 6
%d6 = PtrAdd.ptr<u8> %dst, 6
%c  = Load.u8[inbounds #1] %s6
Store.u8[inbounds #2] %d6, %c
```

## When it applies

(1) The count is a literal `n ≥ 1`; with element size `s` the copy moves `b = n × s` bytes. (2) Both ranges are proven accessible: `ValueFacts.IsInBounds(dst, b)` and `IsInBounds(src, b)` are `True` and neither provenance is `MayBeNull` ([#93](https://github.com/Hawkynt/Polypiler/issues/93)); the proofs are recorded in the function's proof table and the emitted accesses are `inbounds #id` ([#121](https://github.com/Hawkynt/Polypiler/issues/121)). Without them the operation's all-or-nothing bounds trap would become a partial write followed by a trap. (3) The ranges are proven disjoint: `Alias(dst, b, src, b)` is `NoAlias` ([#82](https://github.com/Hawkynt/Polypiler/issues/82)); `MayAlias` blocks, because the operation traps `OverlappingCopy` where paired moves would silently copy. (4) Neither region is volatile, a `Device` channel or accessed atomically. (5) Combined accesses use `LoadLE`/`StoreLE` of [#113](https://github.com/Hawkynt/Polypiler/issues/113) on a `u8` region only — both sides use the same byte order, so the pair moves the bytes unchanged on any host; a region with a wider element type is copied element by element in its own type. (6) The plan has at most `MaxPairs` pairs. (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the plan: `Choose` between the kept operation (its `operations` entry: native, sequence priced by `LoopCost(n)`, or helper priced by `HelperCost`) and the sum of the pairs' costs under the objective.

## How it is proven

`alias-facts+range-facts+exhaustive-plan`: with disjoint, accessible ranges a byte-wise copy in any order equals the block copy; the plan generator is checked exhaustively to cover each byte exactly once. No solver, no timeout path.

## When it must not fire

- `dst` is a 6-byte region and `n = 7`: the operation traps `IndexOutOfRange` and leaves `dst` untouched; the plan 4 + 2 + 1 writes six bytes and then traps.
- `src = p`, `dst = p + 2`, `n = 6`: the operation traps `OverlappingCopy`; paired moves produce a repeated pattern.
- A `u32` load through a pointer to a `u16` region is not a copy of two elements but a verifier error (`PLY0143`); wider element types are therefore never combined.
- A volatile source: the operation is already rejected (`PLY0258`); expanding it would invent the number and width of accesses.
- Seventeen pairs for a 17-element `u64` copy exceed `MaxPairs` however cheap the profile makes them.

## Targets and cost gate

Pays off on targets that execute what is emitted as written: generated PowerBASIC and other BASICs, 6502/Z80/8086 machine code, assembly. C, C++, Rust and the managed runtimes expand a constant-size `memcpy` or span copy themselves; their profiles price `Memory.Copy` as `native` with a low latency, [#155](https://github.com/Hawkynt/Polypiler/issues/155) then keeps the single readable call and the pass does nothing. The store budget of upstream (3 moves on an 8086, 4 on a 286/386, 6 on a 486, 8 on a Pentium, as PB-Compiler documents for its own code generator) is not a constant of this pass: it is whatever the profile's costs imply, bounded by `MaxPairs`.

## Switching it off

`--disable-opt O0331`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `specialize` after [#503](https://github.com/Hawkynt/Polypiler/issues/503), [#500](https://github.com/Hawkynt/Polypiler/issues/500) and [#504](https://github.com/Hawkynt/Polypiler/issues/504) had their chance to remove the copy; enables [#158](https://github.com/Hawkynt/Polypiler/issues/158) and [#504](https://github.com/Hawkynt/Polypiler/issues/504) on the individual scalars in a requested later sweep; `OPT-MEM-MEMSIZE` builds on its plan generator.
