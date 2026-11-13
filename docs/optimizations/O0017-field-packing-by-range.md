# O0017 — Narrow and pack record fields to their proven value range

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#505](https://github.com/Hawkynt/Polypiler/issues/505). Once implemented it is switched off with `--disable-opt O0017`.

## What it does

A record field declared `INTEGER` that only ever holds 0…7 occupies sixteen bits per instance; a 64 × 64
tile map of two such fields is 16 KiB of which five bits per record matter. On a C64 or in a DOS segment
that is the difference between fitting and not. PRD §4.8 (storage/field packing), §6.3 "Narrow
representations only when unobservable"; PB-Compiler's O0323 is the working precedent.

## Example

### Before

```
// type Tile { kind: i16, flags: i16 }; every store: kind in [0, 7], flags in [0, 3]; @map: [4096 x Tile], private
Store.i16 (Field @map[%i], kind), %k
%f = Load.i16 (Field @map[%i], flags)
```

### After

```
// type Tile.packed { bits: u8 }  — kind = bits 0..2, flags = bits 3..4
%o  = Load.u8 @map.packed[%i]
%c  = And.u8 %o, 0xF8
%k8 = Trunc.u8 %k
%n  = Or.u8 %c, %k8
Store.u8 @map.packed[%i], %n
%b  = Load.u8 @map.packed[%i]
%s  = LShr.u8[strict] %b, 3
%m  = And.u8 %s, 3
%f  = ZExt.i16 %m
```

## When it applies

(1) The record type is private to the module and none of its instances escapes: no address of an instance or
field is passed to an unknown call, stored where external code can reach it, converted to an integer or
byte view, written to a file or device as a block ([#82](https://github.com/Hawkynt/Polypiler/issues/82) escape; `Unknown` = escapes). (2)
[#76](https://github.com/Hawkynt/Polypiler/issues/76) reports no layout contract (explicit layout, packing pragma, interop export, serialization)
for the type, and the type is in no union/overlay ([#110](https://github.com/Hawkynt/Polypiler/issues/110)). (3) For each packed field, the join
of [#94](https://github.com/Hawkynt/Polypiler/issues/94) ranges of **every** store to that field in the module, joined with the initial value
(zero for zero-initialized storage), fits the chosen slice; one store with an unknown range leaves that
field at its declared width. (4) Signed ranges use the minimal two's-complement width and are sign-extended
on load. (5) No field is volatile, atomic or accessed from more than one thread (a read-modify-write of a
shared byte would race). (6) Block copies of whole records ([#121](https://github.com/Hawkynt/Polypiler/issues/121) `Memory.Copy`) are retargeted to
the packed size; copies of partial records block packing. (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the trade under the
objective (`memory` weighs storage, `speed` weighs the extra shift/mask).

## How it is proven

`range-facts+alias-facts+exhaustive-8bit`: ranges prove every stored value representable; the extract/insert identities are proven exhaustively for all byte contents. No solver, no timeout path.

## When it must not fire

- One store `tile.kind = userInput` with unknown range: values above 7 would be truncated.
- `@map` passed to an external `fwrite`: the file layout changes (PRD §6.3 'address of a public field escapes').
- A store that forgets to mask: writing `kind = 5` with `Or` only, over an old `kind = 2`, yields 7.
- A signed field with range [−2, 1] stored in 2 bits and zero-extended on load reads −2 back as 2.
- Two threads updating `kind` and `flags` of the same record: independent 16-bit stores become racing byte read-modify-writes.

## Switching it off

`--disable-opt O0017`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `specialize` as a module pass after ranges, escape facts and [#161](https://github.com/Hawkynt/Polypiler/issues/161); enables smaller copies and better cache use; must run before [#259](https://github.com/Hawkynt/Polypiler/issues/259) fixes target layouts.
