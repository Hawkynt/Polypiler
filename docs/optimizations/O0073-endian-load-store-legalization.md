# O0073 — Legalize LoadLE/LoadBE and StoreLE/StoreBE per target byte order and alignment

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#258](https://github.com/Hawkynt/Polypiler/issues/258). Once implemented it is switched off with `--disable-opt O0073`.

## What it does

Every hash, cipher and file format reads multi-byte integers in a *specified* byte order. The IR keeps that
intent as `LoadLE`/`LoadBE`/`StoreLE`/`StoreBE` (PRD §4.7); a target then needs the cheapest correct
spelling: a plain typed load where its own order matches, a load plus byte swap where it does not, and a
composition from single bytes where it has neither typed loads nor a known order — JavaScript number
arrays, BASIC strings, portable C. Getting this wrong produces code that passes on the developer's machine
and fails on the other endianness. PRD §8 P2 lists endian loads among the first recognition rules; this is
their lowering.

## Example

### Before

```
%v = LoadBE.u32 %p, 0
```

### After

```
// alternative compose
%b0 = Load.u8 %p, 0
%b1 = Load.u8 %p, 1
%b2 = Load.u8 %p, 2
%b3 = Load.u8 %p, 3
%w0 = Shl.u32[strict] (ZExt.u32 %b0), 24
%w1 = Shl.u32[strict] (ZExt.u32 %b1), 16
%w2 = Shl.u32[strict] (ZExt.u32 %b2), 8
%o1 = Or.u32 %w0, %w1
%o2 = Or.u32 %o1, %w2
%v  = Or.u32 %o2, (ZExt.u32 %b3)
// alternative native-swap (little-endian target):  %t = Load.u32 %p, 0 ; %v = ReverseBytes.u32 %t
// alternative native (big-endian target):          %v = Load.u32 %p, 0
```

## When it applies

`native`: the profile declares `byteOrder` equal to the requested order, and either declares unaligned
access legal for the type or the address is proven aligned ([#121](https://github.com/Hawkynt/Polypiler/issues/121) alignment fact); the accessed
memory is addressable as that integer type on the target (not an array of bytes in a language without
reinterpretation — that case is `compose`). `native-swap`: as `native` for the opposite order, plus
`ByteSwap` or `Native(ReverseBytes.<t>)`. `compose`: bytes are loaded in ascending address order,
**zero-extended** to the result type (or to a wider intermediate) before shifting, and combined with `Or`;
a signed result is produced by composing unsigned and reinterpreting at full width. Stores extract bytes
with logical shifts and truncation and write them in ascending address order. Device and volatile memory
([#114](https://github.com/Hawkynt/Polypiler/issues/114) `Device.*`, volatile): an access is never split or reordered — only `native` or
`native-swap` with the same access width qualify; otherwise PLY0550. Bounds: the composed form accesses
exactly the same byte range `[p, p + size)`.

## How it is proven

`exhaustive-16bit+smt-bitvector`: each alternative is proven equal to the definition for 16 bit exhaustively and for 32/64 bit by [#71](https://github.com/Hawkynt/Polypiler/issues/71) (5 s budget per obligation; on timeout that alternative is removed for that width and the next one is tried).

## When it must not fire

- Sign extension: composing from `i8` bytes, byte `0x80` becomes `0xFFFFFF80` and the `Or` destroys the upper bytes — `LoadLE.u32` of `80 00 00 00` must be `0x00000080`.
- Shifting a byte before widening: `b3 << 24` computed in 8 or 16 bits is 0.
- A plain typed load on a little-endian host for `LoadBE`: bytes `12 34 56 78` read as `0x78563412` instead of `0x12345678`.
- An unaligned 32-bit load on a target that traps on misalignment.
- `StoreBE.u16` to a hardware register pair split into two byte writes: the device sees an intermediate value.

## Switching it off

`--disable-opt O0073`; `Kind = Required`: the rule runs at every `--opt` level including `none`. With it disabled the legalizer has no alternative for these operations on targets that need it and reports PLY0530.

## Interactions with other passes

runs in phase `legalize` under the driver of [#259](https://github.com/Hawkynt/Polypiler/issues/259); [#496](https://github.com/Hawkynt/Polypiler/issues/496) and [#498](https://github.com/Hawkynt/Polypiler/issues/498) produce `LoadLE/BE` for byte-buffer emulation and rely on this rule; afterwards [#154](https://github.com/Hawkynt/Polypiler/issues/154) and [#156](https://github.com/Hawkynt/Polypiler/issues/156) clean up.
