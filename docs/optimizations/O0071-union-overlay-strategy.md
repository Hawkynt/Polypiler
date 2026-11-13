# O0071 — Select and apply the union/overlay strategy each target documents

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#498](https://github.com/Hawkynt/Polypiler/issues/498). Once implemented it is switched off with `--disable-opt O0071`.

## What it does

Unions are how low-level code looks at the same bytes in two ways: a float and its bit pattern, a 128-bit
vector and its four 32-bit words, a packet header and a byte array. Every target spells that differently
and several forbid the obvious spelling: reading an inactive union member is undefined in C++, .NET allows
overlapping fields only for unmanaged types, JavaScript needs views over one `ArrayBuffer`, Perl needs
`pack`/`unpack`, BASIC dialects have `UNION` types or nothing. PRD §4.7 lists these emissions and adds
"where documented" and "honor C/C++ effective-type/aliasing and endian restrictions"; the design note
asks for "union UDTs where we can access separate vector parts".

## Example

### Before

```
// union { u32 x; u8 b[4]; } %u      source profile: little-endian
Store.u32 (Member %u, x), 0x11223344
%t = Load.u8 (Member %u, b), 3
Store.u8 (Member %u, b), 3, 0xFF
%y = Load.u32 (Member %u, x)
```

### After

```
// strategy byte-buffer: %u.bytes = 4 x u8
StoreLE.u32 %u.bytes, 0, 0x11223344
%t = Load.u8 %u.bytes, 3                 // 0x11
Store.u8 %u.bytes, 3, 0xFF
%y = LoadLE.u32 %u.bytes, 0              // 0xFF223344
```

## When it applies

Strategy preconditions: `c-union` — target language defines reading a member other than the last stored
(C99 and later; **not** C++); `memcpy` — any C or C++ profile; `bit-cast` — both members have equal size
and are trivially copyable, and only whole-member reads and writes occur (no partial update through
another member); `explicit-layout` — all members are unmanaged value types (no references) and the profile
has `Union.Overlay(explicit-layout)`; `rust-bytes` — members are plain-old-data, conversion by
`to_*_bytes`/`from_*_bytes` in the source byte order; `arraybuffer-views` — multi-byte access through a
`DataView`-style accessor with explicit endianness (typed-array views are allowed only when the profile
declares `byteOrder` equal to the source's); `pack-unpack` — whole-value or per-member conversion with
explicit-endian templates; `udt-overlay` — the dialect documents overlapping `UNION` members with the
needed element types. `byte-buffer` — always applicable when every member is a value type with a defined
byte representation ([#110](https://github.com/Hawkynt/Polypiler/issues/110) layout); it is refused for members holding references or
target-opaque objects (PLY0545). Partial overlap (a member narrower than the union) leaves the other bytes
unchanged on store. Padding bytes read through another member have the value last written to them, or the
profile's unspecified-value rule if never written. The layout (member offsets, size, alignment) comes from
[#76](https://github.com/Hawkynt/Polypiler/issues/76) and is not altered. Selection among applicable strategies is by [#155](https://github.com/Hawkynt/Polypiler/issues/155), ties by the
order the profile lists them.

## How it is proven

`structural+exhaustive-16bit`: every strategy is the byte-level definition of the overlay expressed with different operations; the 16-bit instances are checked exhaustively and the others against [#119](https://github.com/Hawkynt/Polypiler/issues/119). No solver, no timeout path.

## When it must not fire

- C++ output that reads `u.b[3]` after writing `u.x` through a `union`: undefined behaviour ([class.union]); legal C, illegal C++ — the strategy must be `memcpy` or `bit-cast`.
- A JavaScript `Uint32Array` view over the buffer on a big-endian host: `x = 0x11223344` puts `0x11` in byte 0, but the little-endian source expects `0x44`.
- `bit-cast` for a union that is updated through the byte member: a value-to-value cast cannot express `b[3] = 0xFF` followed by reading `x`.
- A .NET explicit-layout struct overlapping an object reference with an integer: rejected by the runtime (type-load failure).
- Storing the 1-byte member and assuming the other three bytes become zero: they keep their previous contents.

## Switching it off

`--disable-opt O0071`; `Kind = Required`: the rule runs at every `--opt` level including `none`. With it disabled the legalizer has no alternative for these operations on targets that need it and reports PLY0530.

## Interactions with other passes

runs in phase `legalize` under the driver of [#259](https://github.com/Hawkynt/Polypiler/issues/259) after [#496](https://github.com/Hawkynt/Polypiler/issues/496); emits `LoadLE/BE` for [#258](https://github.com/Hawkynt/Polypiler/issues/258) and `BitCast` for [#495](https://github.com/Hawkynt/Polypiler/issues/495); [#505](https://github.com/Hawkynt/Polypiler/issues/505) never touches overlay types.
