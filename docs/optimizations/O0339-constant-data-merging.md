# O0339 — Share identical read-only data blocks and overlap blocks with a common prefix or suffix

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0339`.

## What it does

`OPT-MEM-LITPOOL` packs code-unit literals. The same argument holds for every other block of
initialized read-only data — lookup tables, `DATA` pools, coefficient arrays — which translated programs
often contain twice, once per source module, or with one table being the tail of another. PB-Compiler
merges such blocks program-wide: exact duplicates, containment and prefix/suffix overlap (upstream O0285,
pass `ConstantDataMerging`). PRD §4.8 (size and memory objectives).

## Example

### Before

```
global @sq:  u16[8] = [0, 1, 4, 9, 16, 25, 36, 49] [readonly]
global @sq2: u16[8] = [0, 1, 4, 9, 16, 25, 36, 49] [readonly]
global @hi:  u16[4] = [25, 36, 49, 64]             [readonly]
%a = Load.u16[inbounds #1] (PtrAdd.ptr<u16> @sq2, %i)     // #1 bounds: "0 <= %i", "%i < 8"
%b = Load.u16[inbounds #2] (PtrAdd.ptr<u16> @hi, %j)      // #2 bounds: "0 <= %j", "%j < 4" 
```

### After

```
global @sq: u16[9] = [0, 1, 4, 9, 16, 25, 36, 49, 64] [readonly]
%a  = Load.u16[inbounds #1] (PtrAdd.ptr<u16> @sq, %i)
%h  = PtrAdd.ptr<u16> @sq, 5
%b  = Load.u16[inbounds #2] (PtrAdd.ptr<u16> %h, %j)
```

## When it applies

The use and identity rules (3)–(5) of `OPT-MEM-LITPOOL` apply to every global whose extent or address changes, through its `ReadOnlyPlacement` walk: length-addressed uses only, every access proven inside the *original* extent ([#93](https://github.com/Hawkynt/Polypiler/issues/93) or an `inbounds` proof), address identity unobservable ([#82](https://github.com/Hawkynt/Polypiler/issues/82)). An exact duplicate keeps its extent, so for it only the identity rule applies. In addition: (1) both globals are `[readonly]`, static, non-volatile, of the same element type and address space ([#121](https://github.com/Hawkynt/Polypiler/issues/121)); (2) elements are compared as bit patterns — for floats `+0.0` and `−0.0` differ and NaN payloads must match ([#116](https://github.com/Hawkynt/Polypiler/issues/116)); (3) offsets are whole elements, so the natural alignment of the element type holds automatically; a global with an explicit `align=a` is placed only at a byte offset that is a multiple of `a`, the merged global carries the largest `align` of its parts, and a pointer's alignment fact must not become false ([#83](https://github.com/Hawkynt/Polypiler/issues/83): `IsAligned` facts derived from the old global are re-derived); (4) containment and overlap need `ICostModel.IsAvailable("data.overlap")`; exact duplicates need no profile entry ([#155](https://github.com/Hawkynt/Polypiler/issues/155)); (5) a pair is merged only if it saves at least one element.

## How it is proven

`structural+alias-facts+range-facts`: every element read through an old name is read at the same value from the merged block, and no access can reach an element outside its old extent. No solver, no timeout path.

## When it must not fire

- `Load.u16[bounds] (PtrAdd @sq, %i)` with `i = 8`: before, the access is outside the 8 elements of `@sq` and traps `IndexOutOfRange`; after the merge element 8 exists and the load returns 64. A global with an unproven access is therefore not extended.
- `f64` tables `[0.0, 1.0]` and `[-0.0, 1.0]` are numerically equal and bitwise different: `1.0 / table[0]` is `+∞` in one and `−∞` in the other.
- A table with `align=16` for vector loads placed at byte offset 10 of another: aligned loads from it fault or lose their `IsAligned` proof.
- `PtrCmp[eq] @sq, @sq2` is false before and true after.
- A `u16` table `[0x0201, 0x0403]` and a `u8` table `[1, 2, 3, 4]` have equal bytes on a little-endian target and different bytes on a big-endian one; element types are never mixed.

## Targets and cost gate

Exact duplicates are merged for every target: generated source then declares one constant table instead of two, which is both smaller and easier to read, and no target compiler may merge two named, externally visible arrays itself. Containment and prefix/suffix overlap pay off only where Polypiler lays out the image (machine code, assembly, 8-bit and DOS targets, tokenized BASIC) — the profile says so by listing `data.overlap`; elsewhere a table expressed as "offset 5 of another table" would cost readability for bytes the target toolchain may merge anyway at link time. The form name `data.overlap` and the `forms` list of `cost.yaml` that carries it are defined by `OPT-MEM-FORMS`.

## Switching it off

`--disable-opt O0339`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `specialize` as a module pass after `OPT-MEM-LITPOOL`, after `OPT-MEM-LUTGEN` and `OPT-MEM-LUTELIM` have settled which tables exist, and after `OPT-MEM-DEADGLOBAL`; must run before [#259](https://github.com/Hawkynt/Polypiler/issues/259) assigns addresses.
