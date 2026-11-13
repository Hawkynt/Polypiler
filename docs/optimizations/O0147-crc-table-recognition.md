# O0147 — Recognize table-driven CRC loops as a parameterized CRC update

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#564](https://github.com/Hawkynt/Polypiler/issues/564). Once implemented it is switched off with `--disable-opt O0147`.

## What it does

Production CRC code uses a 256-entry table: `crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8)` —
CompressionWorkbench's `Crc32.Update` is that line. The table is the polynomial in disguise, and it is 1
KiB a 6502 target cannot afford and a target with a CRC instruction does not need. The recognition must
read the parameters out of the table and verify all 256 entries; one corrupted entry, or a table that
the program can modify, and it is no longer the CRC it resembles. PRD §4.5, §19.2 (algorithm level:
parametric semantics plus independent oracle).

## Example

### Before

```
loop:
  %i = Phi.u32 [0, entry], [%i2, body]
  %c = Phi.u32 [%c0, entry], [%c2, body]
  %more = ICmp.ult.u32 %i, %n
  CondBr %more, body, exit
body:
  %p = ElementAddr %data, %i
  %b = Load.u8 %p
  %bz = ZExt.u32 %b
  %x = Xor.u32 %c, %bz
  %ix = And.u32 %x, 0xFF
  %te = ElementAddr @table, %ix
  %t = Load.u32 %te
  %s = LShr.u32[strict] %c, 8
  %c2 = Xor.u32 %t, %s
  %i2 = Add.u32 %i, 1
  Br loop
exit:
  Ret %c
```

### After

```
%c = Crc.Update[w=32, poly=0x04C11DB7, reflected=true] %c0, %data, %n
Ret %c
```

## When it applies

(1) the table is a constant array of exactly 256 elements of the register
width, with a known initializer, and [#93](https://github.com/Hawkynt/Polypiler/issues/93) proves no store can reach it; (2) the index is
the low byte (reflected) or the top byte (normal) of the register XOR-ed with the data byte, masked to
8 bits; (3) the register is shifted by exactly 8 in the matching direction and XOR-ed with the table
entry; (4) *table check*: with `P` taken from `table[0x80]` (reflected) or `table[1]` (normal), every
entry `table[i]`, `i` = 0..255, equals the 8-step bitwise CRC of `i` with `P`; (5) the loop is a
counted unit-step loop over a byte span, the register its only loop-carried state, no store or call,
data proven readable or loads without trap, ordinary memory; (6) the register is confined to `w` bits.

## How it is proven

`exhaustive` table check over all 256 entries (complete: the byte step is determined
by the table), `smt-bitvector` for the identity "table step = 8 bitwise steps" given the checked
table, and `loop-inductive` for the byte loop; the table's immutability is an `AliasFact`/`EffectFact`
obligation. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- One corrupted entry: with `table[0x42] = 0x98D220BD` every message whose running index hits `0x42`
  differs in one bit from CRC-32; the 255 correct entries prove nothing.
- A mutable table: its contents at the time of the loop are not the initializer.
- Index without the 8-bit mask on a 32-bit register: reads far outside the table (or traps).
- Shift by 4 with a 256-entry table: not a byte step of any CRC.
- A correct CRC-32 table used with the *normal* step (`c << 8`, top-byte index): the loop computes
  something, but not the CRC that the table belongs to — refused by the table check for the normal
  form.

## Switching it off

`--disable-opt O0147`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after global constant propagation and after
[#563](https://github.com/Hawkynt/Polypiler/issues/563) (O0146), with which it shares the parameter recovery and the catalogue; before
[#282](https://github.com/Hawkynt/Polypiler/issues/282) (O0152). After the rewrite the table is dead unless used elsewhere.
