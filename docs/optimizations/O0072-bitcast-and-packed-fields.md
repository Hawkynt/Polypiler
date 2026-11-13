# O0072 — Legalize BitCast, Reinterpret, subvector access and PackedField

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#495](https://github.com/Hawkynt/Polypiler/issues/495). Once implemented it is switched off with `--disable-opt O0072`.

## What it does

"Give me the bits of this float", "treat these bytes as words", "bits 3 to 7 of this register": PRD §4.7
models them as `BitCast`, `Reinterpret`, subvector extraction/insertion and `PackedField`. Targets differ
wildly — `BitConverter.SingleToUInt32Bits`, `f32::to_bits`, a scratch `ArrayBuffer`, `pack`/`unpack`,
`CVS`/`MKS$` in BASIC, nothing at all on a 6502 — and bit-field layout (bit order, straddling storage
units, signedness) is the most implementation-defined corner of C. Fast inverse square roots, hash
finalizers on doubles and every hardware-register definition depend on this being exact.

## Example

### Before

```
%b = BitCast.u32 %f                                   // %f : f32
%k = PackedField.Load.i8[offset=2, width=3] %unit     // %unit : u8, signed 3-bit field
```

### After

```
// BitCast, alternative helper on a target without a native primitive:
%b = Call.u32 @ply_f32_to_bits(%f)
// PackedField:
%s = LShr.u8[strict] %unit, 2
%m = And.u8 %s, 7
%x = Shl.u8[strict] %m, 5
%k = AShr.i8[strict] (BitCast.i8 %x), 5               // sign-extend the 3-bit value
```

## When it applies

`BitCast`: source and destination have the same bit width (the verifier guarantees it). Alternatives:
`native` — the profile lists `Native(BitCast.<from>.<to>)`; `memory` — the profile can store one type and
load the other from the same bytes (needs [#498](https://github.com/Hawkynt/Polypiler/issues/498)-style storage); `helper` — integer/float
arithmetic that composes or decomposes sign, exponent and significand exactly, including subnormals,
infinities and signed zero. NaN payloads: `native` and `memory` preserve all bits; the `helper` cannot
observe a payload through arithmetic, so it is allowed only when the source profile's NaN policy is not
`payload-observable`; targets whose float type canonicalizes NaNs when a value is moved (the profile flag
`nanCanonicalizing`) lose the same guarantee for *every* alternative that passes the value through a float
variable — under `payload-observable` that is PLY0548. `Reinterpret`: total byte length must be a multiple
of the new element size or the trailing bytes stay inaccessible exactly as in the source; byte order as in
[#258](https://github.com/Hawkynt/Polypiler/issues/258). `PackedField` with bit offset `o`, width `w` in a storage unit of `u` bits, bit 0 = least
significant bit of the unit as numbered by the layout contract ([#76](https://github.com/Hawkynt/Polypiler/issues/76) converts MSB-first layouts):
read = `(unit >> o) & (2^w − 1)`, sign-extended when the field is signed; write = `(unit & ~(mask << o)) |
((value & mask) << o)`; `o + w > u` straddles into the next unit in the unit byte order given by the
contract. Volatile storage units: the read-modify-write of a field write is one load and one store of the
unit, in that order, exactly once.

## How it is proven

`exhaustive-16bit+smt-bitvector` for packed fields; `interpreter` cross-check against .NET primitives for `BitCast` helpers (the helper is integer arithmetic on IEEE fields). Solver budget 5 s per obligation; on timeout the straddling form falls back to byte-wise composition (`not applied: proof timeout` for the unit-wise form).

## When it must not fire

- `(u32)f` as a numeric conversion instead of a bit cast: 1.0 becomes 1, not 0x3F800000.
- A signed 3-bit field holding `101`: zero-extended it reads 5, correctly it is −3.
- A field write that forgets to clear: unit `0xB5`, field offset 2 width 3, new value 2 — `Or` alone leaves `0xBD`; the correct unit is `0xA9`.
- A field straddling two bytes read from the first byte only: offset 6, width 4 over bytes `C0 03` (little-endian unit `0x03C0`) is `0xF`, the first byte alone gives `0x3`.
- Round-tripping a signalling NaN `0x7FA00001` through a JavaScript number may return `0x7FE00001` or the canonical NaN.
- MSB-first bit numbering (many C ABIs on big-endian targets) treated as LSB-first.

## Switching it off

`--disable-opt O0072`; `Kind = Required`: the rule runs at every `--opt` level including `none`. With it disabled the legalizer has no alternative for these operations on targets that need it and reports PLY0530.

## Interactions with other passes

runs in phase `legalize` under the driver of [#259](https://github.com/Hawkynt/Polypiler/issues/259); [#498](https://github.com/Hawkynt/Polypiler/issues/498) emits `BitCast` for whole-value overlays; helpers join [#260](https://github.com/Hawkynt/Polypiler/issues/260); afterwards [#163](https://github.com/Hawkynt/Polypiler/issues/163)-style cleanup removes redundant masks.
