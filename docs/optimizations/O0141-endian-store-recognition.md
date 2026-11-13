# O0141 — Recognize little- and big-endian stores from shifted byte stores

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#567](https://github.com/Hawkynt/Polypiler/issues/567). Once implemented it is switched off with `--disable-opt O0141`.

## What it does

The mirror image of the byte-assembly load: a word is written as `out[0] = x; out[1] = x >> 8; out[2] =
x >> 16; out[3] = x >> 24`. Cipher's `Unpack32LE`/`Unpack32BE` and every hash finalization do it. As
four stores it blocks `BinaryPrimitives.WriteUInt32LittleEndian`, `to_le_bytes` and a single `MOV`.
Stores are harder than loads: each byte store is observable to anything that reads between them, and a
bounds trap in the middle leaves a half-written word. PRD §4.5, §4.7 (`StoreLE/BE`).

## Example

### Before

```
%t0 = Trunc.u8 %x
Store.u8 %p, %t0
%s1 = LShr.u32[strict] %x, 8
%t1 = Trunc.u8 %s1
%p1 = PtrAdd %p, 1
Store.u8 %p1, %t1
%s2 = LShr.u32[strict] %x, 16
%t2 = Trunc.u8 %s2
%p2 = PtrAdd %p, 2
Store.u8 %p2, %t2
%s3 = LShr.u32[strict] %x, 24
%t3 = Trunc.u8 %s3
%p3 = PtrAdd %p, 3
Store.u8 %p3, %t3
```

### After

```
StoreLE.u32 %p, %x
```

## When it applies

(1) `w/8` byte stores to consecutive offsets of one base, all in one basic
block; (2) the stored bytes are the `w/8` distinct bytes of one SSA value `x` of width `w`, in
ascending (little-endian) or descending (big-endian) significance; (3) between the first and the last
store there is no load, call or other instruction that may read any of the bytes ([#82](https://github.com/Hawkynt/Polypiler/issues/82)), so
the intermediate, partially written state is unobservable; (4) the destination is ordinary memory —
not volatile, not a device register, not executable memory ([#80](https://github.com/Hawkynt/Polypiler/issues/80)); (5) bounds: all `w/8`
addresses are proven in bounds, or the stores carry no bounds trap — a trapping store in the middle
would leave earlier bytes written; (6) the destination bytes do not overlap the storage `x` was loaded
from in a way the original order made visible (covered by 3 when `x` is an SSA value).

## How it is proven

`range+alias facts` (obligations 3–5) plus `smt-bitvector` over a symbolic word and a
symbolic `w/8`-byte memory window; the 16-bit instance by `exhaustive` enumeration. A failed, unknown
or timed-out obligation (budget 5 s each) leaves the IR unchanged and reports `not applied: proof
timeout` or the failing precondition.

## When it must not fire

- Half-written word on a trap: with a trapping bounds check and a 3-byte buffer the original writes
  three bytes and then raises; `StoreLE` raises before writing anything.
- A reader between the stores: a load of `p[1]` after the first store sees the new low byte with the
  old second byte.
- Device memory: four byte writes to a memory-mapped register are four bus writes in a defined order;
  one word write is not the same I/O trace (PRD §17.5).
- Duplicate byte: offset 2 receives `x >> 8`; for `x = 0x12345678` the bytes are `78 56 56 12`.
- Bytes from two values (`x` for the low half, `y` for the high half): two 16-bit stores at most, not
  one 32-bit store.

## Switching it off

`--disable-opt O0141`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after address canonicalization, before dead-store
elimination; enables store-to-load forwarding with [#275](https://github.com/Hawkynt/Polypiler/issues/275) and word-wide serialization.
