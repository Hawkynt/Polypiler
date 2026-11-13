# O0019 — Execute small logical vectors as SWAR operations in one integer register

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#511](https://github.com/Hawkynt/Polypiler/issues/511). Once implemented it is switched off with `--disable-opt O0019`.

## What it does

Many targets have no SIMD at all — .NET Framework 2.0, PowerBASIC, plain JavaScript, Perl — but every one of
them has integer registers, and eight bytes fit a 64-bit register. SIMD-within-a-register processes them in
one operation if carries are kept from crossing lane borders. The design requirement is that logical vectors
of 16, 32 and 64 bits have the same interface as 128-bit and wider ones, and that "clever register usage" is
a lowering choice, not a different API. PRD §4.8 (SWAR), §8 P3.

## Example

### Before

```
%s = Add.vec<4 x u8>[wrap] %a, %b
```

### After

```
// %a, %b reinterpreted as u32; H = 0x80808080, L = 0x7F7F7F7F
%al = And.u32 %a, 0x7F7F7F7F
%bl = And.u32 %b, 0x7F7F7F7F
%t  = Add.u32[wrap] %al, %bl
%x  = Xor.u32 %a, %b
%h  = And.u32 %x, 0x80808080
%s  = Xor.u32 %t, %h
```

## When it applies

With lane width `l`, lane count `n`, register width `w = n × l`, `H` = the constant with the top bit of each
lane set and `L = ~H`: bitwise operations need no condition. `Add[wrap]`: `((a & L) + (b & L)) ^ ((a ^ b) &
H)`. `Sub[wrap]`: `((a | H) − (b & L)) ^ ((a ^ ~b) & H)`. `Shl` by constant `k < l`: `(a << k) & M` with `M`
= each lane's low `k` bits cleared; `LShr` by `k < l`: `(a >> k) & M'` with each lane's high `k` bits
cleared. Only the `[wrap]` contract qualifies; `[checked]` and `[sat]` lanes are not SWAR-lowered. The
vector's memory form must be loaded and stored with the same byte order so lane positions are consistent;
lane-order-sensitive operations (extract, insert, shuffle) use the lane index × `l` as bit offset under
little-endian lane numbering, as [#381](https://github.com/Hawkynt/Polypiler/issues/381) defines. The profile must price the form below the
alternatives ([#155](https://github.com/Hawkynt/Polypiler/issues/155) data-path decision).

## How it is proven

`exhaustive-16bit+smt-bitvector`: each formula is exhaustive for two 8-bit lanes and proven by [#71](https://github.com/Hawkynt/Polypiler/issues/71) for the other shapes with a 5 s budget; a timeout removes that shape from the rule table (`not applied: proof timeout`).

## When it must not fire

- Plain register addition: lanes `FF + 01` carry into the next byte — `0x01FF7F80 + 0x01010180 = 0x03008100`, lane-wise it is `0x02008000`.
- Plain register subtraction borrows across lanes: lane `00 − 01` must give `FF` without decrementing its neighbour.
- A left shift without the mask moves the top bit of one lane into the bottom bit of the next: `0x01FF7F80 << 1 = 0x03FEFF00`, lane-wise `0x02FEFE00`.
- Saturating lane addition `200 + 100` must give 255 per lane; the wrap formula gives 44.
- Loading with one byte order and storing with the other reverses the lanes.

## Switching it off

`--disable-opt O0019`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `legalize`, invoked by [#499](https://github.com/Hawkynt/Polypiler/issues/499) for vectors the target executes in an integer register, and directly when [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers `swar` for a 16/32/64-bit logical vector; afterwards [#154](https://github.com/Hawkynt/Polypiler/issues/154) and [#158](https://github.com/Hawkynt/Polypiler/issues/158) clean up the masks.
