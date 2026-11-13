# O0076 — Legalize object sizes, indices and pointers to 16-bit segment constraints

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#257](https://github.com/Hawkynt/Polypiler/issues/257). Once implemented it is switched off with `--disable-opt O0076`.

## What it does

In 16-bit real mode — PowerBASIC for DOS, x86-16 output — an offset is 16 bits and a segment holds 64 KiB.
Code written with `int` indices and megabyte arrays in mind either does not fit or silently wraps: an offset
`0xFFF0 + 0x20` is `0x0010` in the same segment. PRD §8 P2 names "PB integer precision and 16-bit index
constraints; reject unsupported features explicitly". The legalizer must narrow indices where ranges prove
it safe, switch to huge-pointer arithmetic where the profile has it, and otherwise say exactly which object
is too big.

## Example

### Before

```
// @big: [80000 x u8]; %i : i32 in [0, 79999]
%v = Load.u8[inbounds #1] @big, %i
```

### After

```
// profile seg-huge; @big placed at linear base B = segment(@big) * 16 + offset(@big)
%lin = Add.u32[wrap] B, (ZExt.u32 %i)            // 32-bit arithmetic, legalized further by O0075
%seg = Trunc.u16 (LShr.u32[strict] %lin, 4)
%off = Trunc.u16 (And.u32 %lin, 15)
%v   = Load.u8 [%seg : %off]
```

## When it applies

Narrowing (non-huge object of byte size `S ≤ maxObjectBytes`, element size `e`): the index `i` must be
proven by [#94](https://github.com/Hawkynt/Polypiler/issues/94) to satisfy `0 ≤ i` and `i × e + e ≤ S`; then `Trunc.u16 i` and 16-bit offset
arithmetic `offset(base) + i × e` cannot wrap provided `offset(base) + S ≤ 65536`, which placement
guarantees for every non-huge object. An index without that proof on a non-huge object keeps its
bounds check ([#121](https://github.com/Hawkynt/Polypiler/issues/121) `[bounds]`), performed at the original width before truncation. Huge objects:
all address arithmetic is done on the 20-bit linear address in 32 bits, then normalized: `seg = linear >>
4`, `off = linear & 15`; pointer comparison and difference use linear addresses. A far (non-huge) pointer
([#123](https://github.com/Hawkynt/Polypiler/issues/123) provenance: one object) may only be offset within its object; pointer arithmetic whose
result range is not proven inside the object, or that crosses objects, requires `huge` or is PLY0561.
Objects are never split across segments.

## How it is proven

`range-facts+smt-bitvector`: narrowing follows from the index range; the normalization identity and its inverse are proven by [#71](https://github.com/Hawkynt/Polypiler/issues/71) (5 s budget; on timeout the huge alternative is unavailable and PLY0560 applies).

## When it must not fire

- Offset wrap: far pointer `2000:FFF0` plus `0x20` gives `2000:0010` (linear `0x20010`); the intended address is linear `0x30010`.
- Truncating an unproven 32-bit index: `i = 65536` becomes 0 and reads element 0 instead of trapping.
- `i × e` computed in 16 bits: `i = 40000`, `e = 2` wraps to 14464.
- Comparing two huge pointers by segment then offset without normalization: `2000:0010` and `2001:0000` are the same address.
- An 80 000-byte array on a profile without `huge` emitted anyway: indices above 65535 alias the beginning.

## Switching it off

`--disable-opt O0076`; `Kind = Required`: the rule runs at every `--opt` level including `none`. With it disabled the legalizer has no alternative for these operations on targets that need it and reports PLY0530. Size diagnostics PLY0560–PLY0563 are not affected by the switch.

## Interactions with other passes

runs in phase `legalize` under the driver of [#259](https://github.com/Hawkynt/Polypiler/issues/259), before [#263](https://github.com/Hawkynt/Polypiler/issues/263) (which lowers the 32-bit linear arithmetic to 16-bit limbs) and after [#501](https://github.com/Hawkynt/Polypiler/issues/501)/[#505](https://github.com/Hawkynt/Polypiler/issues/505) have shrunk objects.
