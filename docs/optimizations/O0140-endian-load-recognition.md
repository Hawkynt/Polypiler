# O0140 — Recognize little- and big-endian loads from byte loads combined by shifts

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#275](https://github.com/Hawkynt/Polypiler/issues/275). Once implemented it is switched off with `--disable-opt O0140`.

## What it does

Portable code reads a multi-byte integer one byte at a time: `b[0] | b[1] << 8 | b[2] << 16 | b[3] <<
24`. Cipher's `Pack32LE`/`Pack32BE` do it for every block, CompressionWorkbench's CRC-32 slicing loop
and GRZip do it inline. The four loads, three shifts and three ORs are one `MOV` on a little-endian
machine, one `BinaryPrimitives.ReadUInt32LittleEndian` in C#, one `u32::from_le_bytes` in Rust. PRD §4.5
("endian loads"), §4.7 (`LoadLE/BE`), §8 P2. The idiom's standing defect — sign-extended bytes in
languages whose byte type is signed — is the mandatory counterexample.

## Example

### Before

```
%b0 = Load.u8 %p
%p1 = PtrAdd %p, 1
%b1 = Load.u8 %p1
%p2 = PtrAdd %p, 2
%b2 = Load.u8 %p2
%p3 = PtrAdd %p, 3
%b3 = Load.u8 %p3
%z0 = ZExt.u32 %b0
%z1 = ZExt.u32 %b1
%z2 = ZExt.u32 %b2
%z3 = ZExt.u32 %b3
%s1 = Shl.u32[strict] %z1, 8
%s2 = Shl.u32[strict] %z2, 16
%s3 = Shl.u32[strict] %z3, 24
%o1 = Or.u32 %z0, %s1
%o2 = Or.u32 %o1, %s2
%r  = Or.u32 %o2, %s3
```

### After

```
%r = LoadLE.u32 %p
```

## When it applies

(1) the `w/8` loads are byte loads from one base with consecutive constant
offsets `k … k + w/8 - 1`; (2) each byte reaches the result zero-extended — a sign extension is
accepted only if a later mask removes the fill, which the byte-provenance check decides; (3) each byte
occupies one distinct byte position and the positions are monotone in the address (ascending:
little-endian, descending: big-endian); (4) all loads observe the same memory state: no store, call or
other write that [#82](https://github.com/Hawkynt/Polypiler/issues/82) cannot prove disjoint from the `w/8` bytes lies between the first and
the last load; (5) the bytes are ordinary memory — not volatile, not device registers, where the
number and order of reads is observable ([#80](https://github.com/Hawkynt/Polypiler/issues/80)); (6) bounds: the loads share one bounds
contract; with a trapping contract the same trap is raised by `LoadLE` and nothing observable
separates the original loads, with the `default-zero` contract (JavaScript arrays) a range fact must
prove all `w/8` indices in bounds. The byte loads may have other users and then stay.

## How it is proven

`range+alias facts` (obligations 4–6 as `AliasFact`, `EffectFact`, `RangeFact`) plus
`smt-bitvector` over `w/8` symbolic bytes; the 16-bit instance is additionally checked by `exhaustive`
enumeration of both bytes (65 536 cases). A failed, unknown or timed-out obligation (budget 5 s each)
leaves the IR unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Sign-extended bytes (Java `byte`, C `char`): bytes `80 00 00 00` give `SExt(0x80) | 0 | 0 | 0 =
  0xFFFFFF80`; `LoadLE.u32` gives `0x00000080`.
- A gap: offsets 0, 1, 2, 4 read five bytes' worth of address range and skip one.
- A store between the loads that may alias byte 2: the original reads the new byte, a single load
  placed at the first position reads the old one.
- JavaScript out-of-range read: `data[i + 3]` past the end is `undefined`, coerced to 0, and the
  expression yields the three valid bytes; a `LoadLE` would raise.
- Volatile source: four reads of a device register are four bus cycles.

## Switching it off

`--disable-opt O0140`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after address canonicalization (constant offsets folded
into `PtrAdd`) and alias analysis; enables [#559](https://github.com/Hawkynt/Polypiler/issues/559) folding and word-wide processing of block
ciphers.
