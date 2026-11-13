# O0334 — Remove record fields that no code reads, together with the stores into them

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0334`.

## What it does

A record field that is written at creation and never read — a debug identifier, a back pointer of a
feature that was not ported — costs storage in every instance and a store at every write. For an array
of 10 000 records one unread 32-bit field is 40 000 bytes, on a DOS target most of a segment.
`OPT-MEM-DEADGLOBAL` removes whole globals; the same argument holds per field (upstream O0163), provided
nobody can see the record's bytes. PRD §4.8 (storage), §6.3 "narrow representations only when
unobservable".

## Example

### Before

```
type Particle = record { x: i16, y: i16, debugId: i32 }      // natural layout: 8 bytes
global @p: Particle[10000]
%e = ElemAddr.ptr<Particle>[bounds] @p, %i
%f = FieldAddr.ptr<i32> %e, debugId
Store.i32 %f, %id
%g = FieldAddr.ptr<i16> %e, x
%x = Load.i16 %g
```

### After

```
type Particle = record { x: i16, y: i16 }                    // 4 bytes
global @p: Particle[10000]
%e = ElemAddr.ptr<Particle>[bounds] @p, %i
%g = FieldAddr.ptr<i16> %e, x
%x = Load.i16 %g
```

## When it applies

**The layout must not be observable.** All of the following hold, and any `Unknown` answer counts as a violation: (L1) the record's `layoutObservable` flag is false ([#110](https://github.com/Hawkynt/Polypiler/issues/110)); (L2) `IrModule.Layouts.ContractOf(type)` is `LayoutContractKind.None` — no explicit layout, packing pragma, interop export, foreign import or serialization contract ([#76](https://github.com/Hawkynt/Polypiler/issues/76)) — and `ValueFacts.LayoutDemandOf(site)` is `Free` for every allocation site holding the type, so no foreign boundary pins it ([#338](https://github.com/Hawkynt/Polypiler/issues/338)); (L3) the type is no member of a union and no instance is the operand of `OverlayView`, `BitCast`, `Reinterpret` or `ReinterpretSlice`; (L4) `EscapeOf(site)` is `NoEscape` for every such site ([#82](https://github.com/Hawkynt/Polypiler/issues/82)), no address of an instance or of a field reaches `PtrToInt`, an unknown or external call, a file, stream or `Device` channel; (L5) no `SizeOf`, `AlignOf` or `OffsetOf` of the type is used other than as an allocation size or inside element and field addressing of that same type. **And the field is dead:** (1) no instruction of the module reads field `f`: no `Load` through `FieldAddr …, f`, no `ExtractField …, f`, no `PackedLoad`, and no operation that reads the record's bytes as data — `Memory.Equal`, `Memory.Compare`, `Memory.Mismatch`, `Memory.Find` over instances ([#189](https://github.com/Hawkynt/Polypiler/issues/189)) or a record passed by value to a call outside the module; whole-record `Load`/`Store`/`Memory.Copy`/`Memory.Move` between instances of the same type are not reads of `f` (they move a dead field into a dead field); (2) every `FieldAddr …, f` is used only as the address of a `Store`; (3) each removed store is proven unable to trap by itself: its base pointer is proven non-null ([#91](https://github.com/Hawkynt/Polypiler/issues/91)) or lies in a region without the `null-trap` contract; the `ElemAddr[bounds]` that computes the element address is a separate instruction and keeps its trap; (4) the stored values are left alone for [#156](https://github.com/Hawkynt/Polypiler/issues/156); (5) at least one field remains; (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the storage change — it always does unless the removal leaves the size unchanged because of padding, in which case the field is still removed for its stores.

## How it is proven

`structural+alias-facts`: a field nobody reads, in a record whose bytes nobody sees, has no observable content. No solver, no timeout path.

## When it must not fire

- `Call.void @fwrite(@p, 80000)` to an external function: the file gets 4-byte records instead of 8-byte ones (`layoutObservable` is true for the type).
- `%same = Memory.Equal.u8 (bytes of %a), (bytes of %b), 8`: the comparison reads `debugId`; removing it changes the result for records that differ only there.
- An overlay view `OverlayView.ptr<u32> %e, raw` on a union containing `Particle`: the bytes are read through another type.
- `OffsetOf(Particle, y)` printed by the program, or `SizeOf(Particle)` used as a file seek distance.
- A store through a possibly null record pointer under `null-trap`: the store's `NullReference` trap would vanish.
- A record exported in a public signature: its fields are part of the interface (PRD §4.4).

## Targets and cost gate

Every target: the record declaration loses a member and the code loses assignments, which reads better in generated source as well, and no target compiler may change a record's field set. The storage alternatives (`fields kept` against `field removed`) go through [#155](https://github.com/Hawkynt/Polypiler/issues/155) so that the report carries the byte counts; under every objective the smaller record wins.

## Switching it off

`--disable-opt O0334`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `specialize` as a module pass before `OPT-MEM-FIELDORDER`, `OPT-MEM-HOTCOLD`, `OPT-MEM-AOS2SOA` and [#505](https://github.com/Hawkynt/Polypiler/issues/505), which then see only live fields; enables [#156](https://github.com/Hawkynt/Polypiler/issues/156) (stored values) and smaller copies; must run before [#259](https://github.com/Hawkynt/Polypiler/issues/259) fixes target layouts.
