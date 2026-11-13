# O0018 — Pack array elements into bytes or bit fields by proven range

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#501](https://github.com/Hawkynt/Polypiler/issues/501). Once implemented it is switched off with `--disable-opt O0018`.

## What it does

JavaScript and Python have no byte: a lookup table of values 0…255 arrives as an array of numbers and would
be emitted as 32- or 64-bit elements; a flag array arrives as full words. Packed, an S-box is 256 bytes
instead of 2 KiB and a sieve is one bit per entry. PRD §4.8 (byte/bit packing), §6.3 "Internal wide integer
stores values 0 through 15 … may pack values into four-bit fields if profitable".

## Example

### Before

```
// @t: [4 x i64], private; every stored value in [0, 15]
Store.i64 @t[%i], %v
%x = Load.i64 @t[%j]
```

### After

```
// @t.packed: [2 x u8], element i in byte i >> 1, bits (i & 1) * 4 .. +3
%bi = LShr.u32[strict] %i, 1
%sh = Shl.u32[strict] (And.u32 %i, 1), 2
%o  = Load.u8 @t.packed[%bi]
%cl = And.u8 %o, (Not.u8 (Shl.u8[strict] 15, %sh))
%n  = Or.u8 %cl, (Shl.u8[strict] (Trunc.u8 %v), %sh)
Store.u8 @t.packed[%bi], %n
%b  = Load.u8 @t.packed[(LShr.u32[strict] %j, 1)]
%x  = ZExt.i64 (And.u8 (LShr.u8[strict] %b, (Shl.u32[strict] (And.u32 %j, 1), 2)), 15)
```

## When it applies

(1) The array is private and does not escape; no element address is taken or retained ([#82](https://github.com/Hawkynt/Polypiler/issues/82);
`Unknown` = escapes); no layout contract from [#76](https://github.com/Hawkynt/Polypiler/issues/76); no reinterpreting view of it exists
([#121](https://github.com/Hawkynt/Polypiler/issues/121)). (2) The join of the ranges ([#94](https://github.com/Hawkynt/Polypiler/issues/94)) of every stored value, of every initializer
element and of the default fill value fits the chosen element representation; signed ranges are packed
only at byte granularity or wider (bit fields are unsigned). (3) Index bounds behaviour is unchanged: the
logical length stays the bound; the packed byte count is `ceil(n × k / 8)`. (4) For bit packing the array
is not written by more than one thread and no access is atomic or volatile. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the
trade under the objective; at `--opt memory` packing wins whenever it saves storage.

## How it is proven

`range-facts+alias-facts+exhaustive-8bit`. No solver, no timeout path.

## When it must not fire

- One store of an unproven value: `t[i] = x` with `x = 16` would corrupt the neighbouring nibble or be truncated.
- A store without clearing first: old nibble 0xF, new value 9, `Or` alone leaves 0xF.
- The array is passed to an unknown function expecting 64-bit elements.
- Index bound: a packed array of 2 bytes holds 4 nibbles; an access at index 4 must still trap as before, not read byte 2.
- Negative values in a 4-bit field: −1 stored as 0xF reads back as 15.

## Switching it off

`--disable-opt O0018`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `specialize` as a module pass after ranges and escape facts are final, after [#161](https://github.com/Hawkynt/Polypiler/issues/161); enables [#511](https://github.com/Hawkynt/Polypiler/issues/511) and [#510](https://github.com/Hawkynt/Polypiler/issues/510) over byte arrays (more lanes), and [#956](https://github.com/Hawkynt/Polypiler/issues/956) ceilings are easier to meet.
