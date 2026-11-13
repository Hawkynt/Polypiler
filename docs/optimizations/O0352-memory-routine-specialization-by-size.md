# O0352 — Choose straight-line stores, the block operation or the runtime routine for fills and moves by their known size

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0352`.

## What it does

One memory routine is wrong for every size. Clearing five bytes through a call costs more than three
stores; clearing five kilobytes with stores is absurd. `OPT-MEM-COPYEXPAND` makes that choice for
non-overlapping copies; fills and overlap-safe moves still always reach the target as an operation.
PB-Compiler chooses between straight-line accesses, a block move and the runtime routine by known size
(upstream O0339, pass `MemoryRoutineSpecialization`, of which upstream O0015 is the copy half). PRD §4.8
(operation form: native, sequence, helper).

## Example

### Before

```
Memory.Fill.u8 %dst, 0x5A, 7
Memory.Move.u8 %d2, %s2, 6
```

### After

```
// profile: StoreLE/LoadLE u32 and u16 native
StoreLE.u32[inbounds #1] %dst, 0x5A5A5A5A
StoreLE.u16[inbounds #1] (PtrAdd.ptr<u8> %dst, 4), 0x5A5A
Store.u8[inbounds #1] (PtrAdd.ptr<u8> %dst, 6), 0x5A
%a = LoadLE.u32[inbounds #2] %s2
%b = LoadLE.u16[inbounds #2] (PtrAdd.ptr<u8> %s2, 4)
StoreLE.u32[inbounds #3] %d2, %a
StoreLE.u16[inbounds #3] (PtrAdd.ptr<u8> %d2, 4), %b
```

## When it applies

(1) The count is a literal or a value whose [#94](https://github.com/Hawkynt/Polypiler/issues/94) range is a single number `n`; `n = 0` deletes the operation, which touches and checks nothing ([#189](https://github.com/Hawkynt/Polypiler/issues/189)). (2) Every range the operation accesses is proven accessible: `IsInBounds` is `True` for `n × size` bytes and the provenance is not `MayBeNull` ([#93](https://github.com/Hawkynt/Polypiler/issues/93)) — the operations check everything before writing anything, and a sequence of stores cannot. (3) No region is volatile, a `Device` channel or atomic ([#121](https://github.com/Hawkynt/Polypiler/issues/121)). (4) *Fill*: with a constant value on a `u8` region the stored word is `v × 0x01…01` at the chosen width, written with `StoreLE` ([#113](https://github.com/Hawkynt/Polypiler/issues/113)) so that the bytes are the same on any host; with any other element type, or a value that is not a constant, each element gets its own `Store.T`. (5) *Move*: every load of the plan precedes every store — only then does an overlapping destination see the original source bytes; the plan therefore keeps as many values live as it has pairs. (6) At most `MaxPairs` stores (fill) or pairs (move). (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the straight-line form to the kept operation as its profile entry prices it (`native`, `sequence` by `LoopCost(n)`, `helper` by `HelperCost`); for a move the live values count as latency on profiles that say so.

## How it is proven

`range-facts+exhaustive-8bit+exhaustive-plan`: the splat identity is checked for all byte values, the move plan for all sizes and overlap distances up to the ceiling. No solver, no timeout path.

## When it must not fire

- A move expanded pairwise like a copy: bytes `01 02 03 04 05 06 07 08`, `Memory.Move` of 6 bytes from offset 0 to offset 2 must give `01 02 01 02 03 04 05 06`; load/store pairs of width 2 give `01 02 01 02 01 02 01 02`.
- `Memory.Fill.u8 %dst, 0x5A, 7` into a 6-byte region: the operation traps and writes nothing; three stores write six bytes and then trap.
- A splat stored in host byte order for the pattern `0x12` is harmless, but the same code path for a `u16` fill value `0x1234` combined into `0x12341234` is byte-order dependent — wider element types are never combined.
- A run-time fill byte widened with `v × 0x01010101`: correct, but it adds a multiply the profile may not have; it is out of scope, not silently chosen.
- A fill of a memory-mapped display buffer declared `Device`: the number and width of bus writes is the behaviour.

## Targets and cost gate

Same reasoning as `OPT-MEM-COPYEXPAND`: the straight-line form wins on targets that execute what is emitted as written — generated BASIC, 6502, Z80, 8086 — and loses on targets whose compiler or runtime expands a constant-size `memset`/`memmove` itself, whose profiles price the operation as cheap and native; there the single call stays in the generated source. The three forms of the title are three profile entries: a profile lists `Memory.Fill`/`Memory.Move` as `native` (a block instruction), `sequence` (an inline loop) or `helper` (a runtime routine), and this pass adds the straight-line alternative below the size where it is cheaper. PB-Compiler's own thresholds (4 fill stores on an 8086, 8 on a 386, 16 on a Pentium; four byte stores for a run-time value) are that project's measurements for its code generator and are not copied into the pass.

## Switching it off

`--disable-opt O0352`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `specialize` directly after `OPT-MEM-COPYEXPAND`, after [#504](https://github.com/Hawkynt/Polypiler/issues/504) and `OPT-MEM-SELFCOPY` removed the fills and moves that are dead; enables [#504](https://github.com/Hawkynt/Polypiler/issues/504) and [#158](https://github.com/Hawkynt/Polypiler/issues/158) on the individual stores in a requested later sweep.
