# O0344 — Order the fields of a private record by access frequency and padding

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0344`.

## What it does

Declaration order is the programmer's reading order, not the machine's: a 64-byte name in front of two
coordinates pushes the fields touched every frame to offset 64, beyond a short displacement and into a
second cache line; under natural alignment a badly ordered record also carries padding that a different
order would not need. C compilers must keep declaration order, so a C-family backend cannot leave this
to the target. PB-Compiler reorders private records (upstream O0321, in `DataLayoutTransforms`). PRD
§4.8 (storage form), §6.3.

## Example

### Before

```
type Entity = record { name: array<u8, 64>, x: i16, y: i16 }     // x at 64, y at 66, size 68
%xa = FieldAddr.ptr<i16> %e, x                                   // accessed in the frame loop
```

### After

```
type Entity = record { x: i16, y: i16, name: array<u8, 64> }     // x at 0, y at 2, size 68
%xa = FieldAddr.ptr<i16> %e, x
```

## When it applies

**The layout must not be observable.** All of the following hold, and any `Unknown` answer counts as a violation: (L1) the record's `layoutObservable` flag is false ([#110](https://github.com/Hawkynt/Polypiler/issues/110)); (L2) `IrModule.Layouts.ContractOf(type)` is `LayoutContractKind.None` — no explicit layout, packing pragma, interop export, foreign import or serialization contract ([#76](https://github.com/Hawkynt/Polypiler/issues/76)) — and `ValueFacts.LayoutDemandOf(site)` is `Free` for every allocation site holding the type, so no foreign boundary pins it ([#338](https://github.com/Hawkynt/Polypiler/issues/338)); (L3) the type is no member of a union and no instance is the operand of `OverlayView`, `BitCast`, `Reinterpret` or `ReinterpretSlice`; (L4) `EscapeOf(site)` is `NoEscape` for every such site ([#82](https://github.com/Hawkynt/Polypiler/issues/82)), no address of an instance or of a field reaches `PtrToInt`, an unknown or external call, a file, stream or `Device` channel; (L5) no `SizeOf`, `AlignOf` or `OffsetOf` of the type is used other than as an allocation size or inside element and field addressing of that same type. **And the order is not otherwise observable:** (1) no `Memory.Compare`, `Memory.Mismatch` or `Memory.Find` runs over the bytes of an instance — their results depend on which field comes first ([#189](https://github.com/Hawkynt/Polypiler/issues/189)); `Memory.Equal`, `Memory.Copy`, `Memory.Move` and `Memory.Fill` over whole instances are order-independent and allowed; (2) every `RecordNew` and every constant initializer is permuted to the new order; `ExtractField`, `InsertField` and `FieldAddr` name their field and need no change ([#110](https://github.com/Hawkynt/Polypiler/issues/110)); (3) the layout rule (`natural` or a frontend-chosen `packed(n)` without a contract) is kept and offsets are recomputed by `RecordLayout.Compute`; (4) no field is volatile or a `Device` register; (5) the record appears in no exported signature. **Selection:** `w(f)` is the access weight of field `f`; a field is *cold* when `4 × w(f) ≤ max w` (upstream's threshold) and *hot* otherwise. Candidates: `D` (declared), `H` (stable sort by descending weight), `A` (stable sort by descending alignment, then descending weight). `hotSpan(order)` is the offset of the end of the last hot field. Under the objectives `speed` and `balanced` the candidate with the smallest `hotSpan` wins, ties by smaller record size, then `D` before `H` before `A`; under `size` and `memory` the smallest record size wins, ties by smaller `hotSpan`, then the same order. If `D` wins nothing changes and nothing is reported as applied.

## How it is proven

`structural+alias-facts`: with the layout unobservable and every access by name, a permutation of the fields is a renaming of storage cells. No solver, no timeout path.

## When it must not fire

- `type R = record { a: u8, b: u8 }`, instances (1, 2) and (2, 1): `Memory.Compare.u8` answers −1 in declared order and 1 after swapping the fields.
- A memory-mapped register block `{ctrl: u8, status: u8, data: u16}` with an explicit layout: after reordering, a store to `ctrl` writes the data register.
- A record read from a file with one block read: the bytes on disk are in declared order.
- `OffsetOf(Entity, x)` = 64 stored in a table of field offsets used by a generic serializer.
- Two modules compiled separately that share the type: only one of them sees the reordering.

## Targets and cost gate

Pays off for machine-code and assembly backends and for C-family source targets, whose compilers are forbidden to reorder members — the profile lists the storage form `storage.field-order`. Runtimes that lay out objects themselves (the JVM, CLR classes with automatic layout, JavaScript engines) reorder on their own; their profiles do not list the form and the generated declarations keep the source's order, which is also the more readable choice. On a 6502 no displacement is shorter than another: its profile lists the form only so that the `memory` objective can remove padding. The form name `storage.field-order` and the `forms` list of `cost.yaml` that carries it are defined by `OPT-MEM-FORMS`.

## Switching it off

`--disable-opt O0344`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `specialize` as a module pass after `OPT-MEM-DEADFIELD` and before `OPT-MEM-HOTCOLD` (which reuses the weights) and [#505](https://github.com/Hawkynt/Polypiler/issues/505); must run before [#259](https://github.com/Hawkynt/Polypiler/issues/259) fixes target layouts.
