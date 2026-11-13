# O0343 — Store a private array of records as one array per field

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0343`.

## What it does

An array of records interleaves its fields, so a loop that updates only `x` from `vx` strides by the
whole record and drags `y` and `vy` through memory with it; on a 6502 it also pays a multiplication by
the record size for every access. Stored as one array per field, each field is contiguous — the layout
that vectorizes and the layout an 8-bit programmer writes by hand. PB-Compiler implements the
transposition for private record arrays (upstream O0320, in `DataLayoutTransforms`). PRD §4.8 (storage
form), §6.3 "narrow representations only when unobservable".

## Example

### Before

```
type Particle = record { x: i16, y: i16, vx: i16, vy: i16 }
global @p: Particle[10000]
%e  = ElemAddr.ptr<Particle>[bounds] @p, %i
%xa = FieldAddr.ptr<i16> %e, x
%va = FieldAddr.ptr<i16> %e, vx
%x  = Load.i16 %xa
%v  = Load.i16 %va
%s  = Add.i16[wrap] %x, %v
Store.i16 %xa, %s
```

### After

```
global @p.x:  i16[10000]
global @p.y:  i16[10000]
global @p.vx: i16[10000]
global @p.vy: i16[10000]
%xa = ElemAddr.ptr<i16>[bounds] @p.x, %i
%va = ElemAddr.ptr<i16>[bounds] @p.vx, %i
%x  = Load.i16 %xa
%v  = Load.i16 %va
%s  = Add.i16[wrap] %x, %v
Store.i16 %xa, %s
```

## When it applies

**The layout must not be observable.** All of the following hold, and any `Unknown` answer counts as a violation: (L1) the record's `layoutObservable` flag is false ([#110](https://github.com/Hawkynt/Polypiler/issues/110)); (L2) `IrModule.Layouts.ContractOf(type)` is `LayoutContractKind.None` — no explicit layout, packing pragma, interop export, foreign import or serialization contract ([#76](https://github.com/Hawkynt/Polypiler/issues/76)) — and `ValueFacts.LayoutDemandOf(site)` is `Free` for every allocation site holding the type, so no foreign boundary pins it ([#338](https://github.com/Hawkynt/Polypiler/issues/338)); (L3) the type is no member of a union and no instance is the operand of `OverlayView`, `BitCast`, `Reinterpret` or `ReinterpretSlice`; (L4) `EscapeOf(site)` is `NoEscape` for every such site ([#82](https://github.com/Hawkynt/Polypiler/issues/82)), no address of an instance or of a field reaches `PtrToInt`, an unknown or external call, a file, stream or `Device` channel; (L5) no `SizeOf`, `AlignOf` or `OffsetOf` of the type is used other than as an allocation size or inside element and field addressing of that same type, applied to the record type *and* to the array's allocation site. **And every use of the array has a field-wise equivalent:** (1) the element count is a literal or a value that never changes after allocation; the array is never resized; (2) every `ElemAddr` on the array is used only by `FieldAddr` followed by `Load`/`Store` of the field, or by a whole-element `Load`/`Store` of the record type; an element address that is stored, passed to a call, compared, merged by a `Phi` with another element address or advanced by `PtrAdd` blocks the rewrite — a `ptr<Particle>` has no meaning afterwards; a field address is used only by `Load`/`Store` ([#111](https://github.com/Hawkynt/Polypiler/issues/111), [#110](https://github.com/Hawkynt/Polypiler/issues/110)); (3) block operations are whole elements of two arrays of this type with the same transposition decision: `Memory.Copy`, `Memory.Move`, `Memory.Fill`; `Memory.Equal` is rewritten to the conjunction over the fields; `Memory.Compare`, `Memory.Mismatch` and `Memory.Find` over instances block, their result depends on the byte order of the interleaved layout ([#189](https://github.com/Hawkynt/Polypiler/issues/189)); (4) every field is a scalar and none is volatile or atomic; (5) the bounds contract of each `ElemAddr` carries over unchanged — the field arrays have the same count and lower bound, so the same index traps; (6) a heap array becomes one allocation per field only if allocation failure is unobservable (the rule of [#500](https://github.com/Hawkynt/Polypiler/issues/500)); (7) *trigger*: at least one counted loop ([#350](https://github.com/Hawkynt/Polypiler/issues/350)) accesses, per element it visits, a strict subset of the record's fields, and no loop performs a whole-element operation on the array; (8) `ICostModel.IsAvailable("storage.soa")` is true and [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the storage alternative under the objective.

## How it is proven

`structural+alias-facts`: with every access expressed per field and per index, `(array, i, field)` maps one-to-one to `(field array, i)`. No solver, no timeout path.

## When it must not fire

- `Call.void @fwrite(@p, 80000)`: the file receives all `x` values first instead of interleaved records.
- `%q = ElemAddr @p, %i` passed to `@update(ptr<Particle>)` in another module: there is no record at that address any more.
- A byte view `ReinterpretSlice.slice<u8> @p` used for a checksum over the array: the byte sequence changes.
- `Memory.Compare` of two elements to sort particles: lexicographic order over interleaved bytes is not the order over field arrays.
- `SizeOf(Particle) × n` used as the size of a network packet.
- An array resized with the source language's `REDIM PRESERVE`: four reallocations can fail independently.

## Targets and cost gate

Pays off where a vectorizer follows ([#510](https://github.com/Hawkynt/Polypiler/issues/510), or the target's own compiler behind a C-family source backend) and on index-register machines: a 6502 addresses `x[i]` directly with an 8-bit index and needs a multiply for `p[i].x`. Target compilers never perform this transformation — it changes a data structure — so it is not redundant on any source target; it does change how the generated declarations read (four parallel arrays instead of one array of records), which is why it is not active at `--opt size` and why a profile must list the storage form `storage.soa`. The form name `storage.soa` and the `forms` list of `cost.yaml` that carries it are defined by `OPT-MEM-FORMS`.

## Switching it off

`--disable-opt O0343`; active at `--opt balanced`, `speed` and `memory`, off at `--opt size` and `none`.

## Interactions with other passes

runs in phase `specialize` as a module pass after `OPT-MEM-DEADFIELD` and `OPT-MEM-HOTCOLD` — a record type carrying the attribute `split=hot` or `split=cold` set by the latter is a decided grouping and is not transposed again — and before [#505](https://github.com/Hawkynt/Polypiler/issues/505), [#501](https://github.com/Hawkynt/Polypiler/issues/501) (which then pack the field arrays) and [#510](https://github.com/Hawkynt/Polypiler/issues/510); must run before [#259](https://github.com/Hawkynt/Polypiler/issues/259) fixes target layouts.
