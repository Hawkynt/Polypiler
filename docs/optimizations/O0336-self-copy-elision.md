# O0336 — Drop block moves and load/store pairs whose source and destination are the same location

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0336`.

## What it does

`rec = rec` is rare in hand-written code and routine after inlining and specialization, where the two
designators of an assignment become the same one. In PolyIR it arrives as an overlap-safe `Memory.Move`
of a location onto itself or as a record loaded and stored back. [#503](https://github.com/Hawkynt/Polypiler/issues/503) removes a
`Memory.Copy` only; a self-`Copy` of non-zero length traps by definition ([#189](https://github.com/Hawkynt/Polypiler/issues/189)), so the
forms that actually occur are the ones nobody removes. PB-Compiler documents the rewrite as UDT
self-copy elision (upstream O0215).

## Example

### Before

```
Memory.Move.u8 %r, %r, 6

%v = Load.Rec %p
Store.Rec %q, %v            // %q = FieldAddr/ElemAddr chain equal to %p
```

### After

```
// nothing

%v = Load.Rec %p            // left for OPT-DCE
```

## When it applies

(1) *Same location*: `%a` and `%b` are the same SSA value, or `Alias(a, bytes, b, bytes)` answers `MustAlias` with reason `same-location` ([#82](https://github.com/Hawkynt/Polypiler/issues/82)); `PartialAlias` and `MayAlias` block. (2) For `Memory.Move`/`Memory.CopyForward` of `n` elements: the operation's own traps must be impossible — `n` is the literal 0, or `IsInBounds(a, n × size)` is `True` and the provenance is not `MayBeNull` ([#93](https://github.com/Hawkynt/Polypiler/issues/93)); otherwise the bounds or null trap that precedes the move would be removed with it. (3) For the load/store pair: the load and the store have the same type and size, and `MemorySsa.ClobberingAccess` of the store's location, walked back from the store, reaches the memory state the load read ([#86](https://github.com/Hawkynt/Polypiler/issues/86)) — no instruction in between may write the location, so it still holds `%v`; the store cannot trap, because the load of the same address under the same access contract already succeeded. (4) The region is not volatile, not a `Device` channel, not atomic and not under a `Synchronize` effect ([#114](https://github.com/Hawkynt/Polypiler/issues/114), [#121](https://github.com/Hawkynt/Polypiler/issues/121)). (5) The copy is a plain bitwise operation of [#121](https://github.com/Hawkynt/Polypiler/issues/121)/[#110](https://github.com/Hawkynt/Polypiler/issues/110); a call that implements a user-defined assignment is not matched.

## How it is proven

`alias-facts+effect-facts`: writing to a location the bytes it already holds changes no state. No solver, no timeout path.

## When it must not fire

- A status register with write-one-to-clear bits: `%s = Load.u8[unchecked, volatile] %status; Store.u8[unchecked, volatile] %status, %s` reads 0x05 and writes it back, which acknowledges interrupts 0 and 2. Removing the store leaves them pending.
- `Memory.Move.u8 %p, %p, %n` with `n = 8` on a 6-byte region: the move traps `IndexOutOfRange`; deleting it hides the trap.
- `a[i] = a[j]` with `i` and `j` merely *possibly* equal: `MayAlias`, not a self-copy.
- `%v = Load %p; Call @f(); Store %p, %v` where `@f` writes the location: the store restores the old value and must stay.
- `Memory.Copy.u8 %p, %p, 4`: this is the trap `OverlappingCopy`, not a no-op; it is not matched here.

## Targets and cost gate

Every target. The rewrite only deletes instructions, so every cost component falls and the cost model is not consulted; in generated source a line `r = r;` disappears. Target compilers remove a self-assignment of a scalar local, but not a `memmove` of a block onto itself whose identity they cannot prove.

## Switching it off

`--disable-opt O0336`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#159](https://github.com/Hawkynt/Polypiler/issues/159) and [#158](https://github.com/Hawkynt/Polypiler/issues/158) (which make the two addresses one value) and after [#503](https://github.com/Hawkynt/Polypiler/issues/503); enables [#156](https://github.com/Hawkynt/Polypiler/issues/156) (the load) and [#504](https://github.com/Hawkynt/Polypiler/issues/504).
