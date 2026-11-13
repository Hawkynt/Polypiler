# O0345 — Split the rarely accessed fields of a private record array into a parallel cold array

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0345`.

## What it does

A record with two coordinates and a 128-byte description is 132 bytes of which four are touched per
frame. An array of 401 of them is 52 932 bytes — almost a whole 64 KiB segment — while the hot part
alone is 1 604. Splitting the cold fields into a second array indexed by the same element number keeps
the hot records dense in the cache and, on segmented or 8-bit targets, lets several times as many
entities fit at all. PB-Compiler implements this for private record arrays (upstream O0322, in
`DataLayoutTransforms`). PRD §4.8 (storage form), §6.3.

## Example

### Before

```
type Entity = record { x: i16, y: i16, description: array<u8, 128> }   // 132 bytes
global @e: Entity[401]                                                 // 52 932 bytes
%p  = ElemAddr.ptr<Entity>[bounds] @e, %i
%xa = FieldAddr.ptr<i16> %p, x
%da = FieldAddr.ptr<array<u8, 128>> %p, description
```

### After

```
type Entity.hot  = record { x: i16, y: i16 }                  [split=hot]    // 4 bytes
type Entity.cold = record { description: array<u8, 128> }     [split=cold]   // 128 bytes
global @e.hot:  Entity.hot[401]                                             // 1 604 bytes
global @e.cold: Entity.cold[401]                                            // 51 328 bytes
%ph = ElemAddr.ptr<Entity.hot>[bounds] @e.hot, %i
%xa = FieldAddr.ptr<i16> %ph, x
%pc = ElemAddr.ptr<Entity.cold>[bounds] @e.cold, %i
%da = FieldAddr.ptr<array<u8, 128>> %pc, description
```

## When it applies

**The layout must not be observable.** All of the following hold, and any `Unknown` answer counts as a violation: (L1) the record's `layoutObservable` flag is false ([#110](https://github.com/Hawkynt/Polypiler/issues/110)); (L2) `IrModule.Layouts.ContractOf(type)` is `LayoutContractKind.None` — no explicit layout, packing pragma, interop export, foreign import or serialization contract ([#76](https://github.com/Hawkynt/Polypiler/issues/76)) — and `ValueFacts.LayoutDemandOf(site)` is `Free` for every allocation site holding the type, so no foreign boundary pins it ([#338](https://github.com/Hawkynt/Polypiler/issues/338)); (L3) the type is no member of a union and no instance is the operand of `OverlayView`, `BitCast`, `Reinterpret` or `ReinterpretSlice`; (L4) `EscapeOf(site)` is `NoEscape` for every such site ([#82](https://github.com/Hawkynt/Polypiler/issues/82)), no address of an instance or of a field reaches `PtrToInt`, an unknown or external call, a file, stream or `Device` channel; (L5) no `SizeOf`, `AlignOf` or `OffsetOf` of the type is used other than as an allocation size or inside element and field addressing of that same type, applied to the record type and to the array's allocation site. **And every use of the array has a split equivalent** — the use rules (1)–(6) of `OPT-MEM-AOS2SOA` hold with "field array" read as "the hot or the cold array": fixed count, no resizing; element addresses used only by `FieldAddr` + access or by whole-element `Load`/`Store`; whole-element `Memory.Copy`/`Move`/`Fill` between two arrays split the same way; `Memory.Equal` as a conjunction of the two halves; `Memory.Compare`/`Mismatch`/`Find` over instances block; no volatile or atomic field; unchanged bounds contracts; no additional allocation that could fail observably. Unlike there, fields may be of any type, since each field stays inside a record. **Split decision:** (7) at least one field is cold and at least one is hot under the weights of `OPT-MEM-FIELDORDER`; with all weights equal nothing is cold; (8) inside each half the fields keep their relative declared order; (9) `ICostModel.IsAvailable("storage.hot-cold")` is true and [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the alternative under the objective — under `memory` it compares the padded sizes of the two halves with the padded size of the whole.

## How it is proven

`structural+alias-facts`: `(array, i, field)` maps one-to-one to `(hot or cold array, i, field)`. No solver, no timeout path.

## When it must not fire

- `Call.void @fwrite(@e, 52932)`: the file would contain 401 four-byte records followed by nothing the reader expects.
- `%p = ElemAddr @e, %i` handed to a function that takes `ptr<Entity>`: no contiguous `Entity` exists.
- Assuming a memory gain: a packed record `{a: u8, b: u8}` split into two one-byte records saves nothing and adds an array; only the padding of a natural layout (`{a: u32, b: u8}`: 8 bytes whole, 4 + 1 split) makes the split smaller. The `memory` objective compares computed sizes.
- A sort that compares elements with `Memory.Compare`.
- An overlay that views the element as raw bytes.

## Targets and cost gate

Pays off where the hot set must fit a small unit: a 64 KiB segment on DOS, a 256-byte page on a 6502, a cache line everywhere else — and no target compiler does it, since it changes a data structure. The profile lists the storage form `storage.hot-cold`. It changes how generated declarations read (two parallel arrays), so it is off at `--opt size`, and profiles of targets whose runtime allocates each record as its own object (JavaScript, Python, Java classes) do not list the form: splitting there would add objects, not locality. The form name `storage.hot-cold` and the `forms` list of `cost.yaml` that carries it are defined by `OPT-MEM-FORMS`.

## Switching it off

`--disable-opt O0345`; active at `--opt balanced`, `speed` and `memory`, off at `--opt size` and `none`.

## Interactions with other passes

runs in phase `specialize` as a module pass after `OPT-MEM-DEADFIELD` and `OPT-MEM-FIELDORDER` and immediately before `OPT-MEM-AOS2SOA`, which leaves `split=hot`/`split=cold` types alone and stays available for arrays this pass declined; before [#505](https://github.com/Hawkynt/Polypiler/issues/505) and [#259](https://github.com/Hawkynt/Polypiler/issues/259).
