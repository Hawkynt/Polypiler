# O0431 — Store jump-table entries as narrow offsets or as indices into a shared address table

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0431`.

## What it does

A jump table with one full address per value is the fastest form and the largest: four bytes per
entry on a 32-bit target, although the arms usually lie within a few hundred bytes of each other and
several values often share an arm. PB-Compiler plans two cheaper encodings (PB O0247, split from PB
O0101): byte offsets from a base, and a byte index into a short table of the distinct addresses. Before
either can be measured, a machine backend has to emit a table at all — [#931](https://github.com/Hawkynt/Polypiler/issues/931) lowers `Switch`
as a compare chain, and `OPT-LAYOUT-JUMPTABLE` only marks the dispatch. This issue makes the x86 NASM
backend honour that mark and adds the two compressed encodings for size-oriented builds. PRD §4.8 (`--opt
size`), §21.3.

## Example

### Before

```
Switch.u16[dispatch=table] %i, other, [0: a], [1: a], [2: b], [3: b], [4: a], [5: c], [6: c], [7: a]

table:  dd a, a, b, b, a, c, c, a                 ; 8 × 4 = 32 bytes
```

### After

```
Switch.u16[dispatch=table, entries=compact] %i, other, [0: a], [1: a], [2: b], [3: b], [4: a], [5: c], [6: c], [7: a]

offset8:  db a-base, a-base, b-base, b-base, a-base, c-base, c-base, a-base      ; 8 bytes
index8:   db 0, 0, 1, 1, 0, 2, 2, 0   +   dd a, b, c                             ; 8 + 12 = 20 bytes
```

## When it applies

The meaning of the `Switch` is unchanged; `entries=compact` only permits the emitter to choose an encoding. With `span` entries, `W` the address size in bytes, `K` the number of distinct targets in the table (the default counts when a hole exists) and `D` an upper bound of the distance in bytes from `base` — the first table target in layout order — to the last one: `offset8` is legal iff `D ≤ 255`, `offset16` iff `D ≤ 65 535`, `index8` iff `K ≤ 256`. `D` is `15 ×` the number of instructions the selector emitted for the blocks from `base` up to the start of the last target block, 15 bytes being the longest x86 instruction; all table targets must be in the hot part of the same function. Table bytes: full `W × span`; `offset8` `span`; `offset16` `2 × span`; `index8` `span + W × K`. The emitter takes the legal form with the fewest table bytes, ties in the order full, `index8`, `offset16`, `offset8`. Dispatch sequences (32-bit): full `jmp [table + i*4]`; `offset8`/`offset16` zero-extending load of the entry, add of `base`, register-indirect jump; `index8` zero-extending load of the index, `jmp [addresses + j*4]`. Offsets are unsigned and never negative because `base` is the first target.

## How it is proven

`structural`: every encoding stores the same target for the same index; the emulator comparison over all indices is the check. No solver, no timeout path.

## When it must not fire

- Offsets measured from the *table* instead of a code label: table and code are in different sections and their distance is unknown until link time.
- A byte offset with a target 300 bytes after the base: the entry wraps to 44 and the dispatch lands in the middle of another arm.
- Sign-extending the loaded byte: offset 200 becomes −56.
- `K` counted without the default while a hole exists: the hole's index points past the address table.
- A cold target moved to the cold region by `OPT-LAYOUT-HOTCOLD`: its distance from the base is not bounded by the function's hot part.

## Switching it off

`--disable-opt O0431`; `entries=compact` is set at `--opt size` and `memory`; at `balanced` and `speed` tables keep full addresses (the compressed forms add a load or an add per dispatch); off at `--opt none`, where no table exists.

## Targets

the x86 NASM backend, and through it the encoder of [#935](https://github.com/Hawkynt/Polypiler/issues/935); nothing else. Source backends never emit a jump table of their own, so the attribute is never set for a profile that declares `emits: source`; real-mode x86 through [#1046](https://github.com/Hawkynt/Polypiler/issues/1046) uses PB-Compiler's tables. The objective of [#155](https://github.com/Hawkynt/Polypiler/issues/155) gates it: only the size-oriented objectives trade a dispatch instruction for table bytes.

## Interactions with other passes

the marking pass runs in phase `legalize` after `DispatchSelection` and `OPT-LAYOUT-TABLEGUARD`; the encoding is chosen at emission, after the block order of `OPT-LAYOUT-JUMPNEXT`'s layout is final. The baseline emission is what makes `OPT-LAYOUT-JUMPTABLE`, `OPT-LAYOUT-PERFECTHASH` and `OPT-LAYOUT-FSM` executable on x86.
