# O0177 — Recover packed-field layouts of private storage under memory-ordering proofs

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#983](https://github.com/Hawkynt/Polypiler/issues/983). Once implemented it is switched off with `--disable-opt O0177`.

## What it does

Programs pack several small values into one word and unpack them at every use: two nibbles in a byte
(Cipher's `SplitNibbles`/`CombineNibbles`), four channels in a pixel, flags beside a counter. The
packing is a storage decision made for the original machine. A target with cheap memory wants separate
fields, a vector target wants planar storage, a 6502 wants bytes; none can choose while every access is
a shift and a mask on an anonymous integer. Recovering the logical layout — which bit ranges are fields
— is the precondition for PRD §4.4's separation of semantic type and storage representation, and it is
only sound for storage whose representation nobody else can observe and whose read-modify-write
sequences are not interleaved with other writes. PRD §19.2 (layout level), §24 S4
("representation/layout optimization under memory ordering proofs"). The original instructions are kept
as the fallback graph with a `RecoveredFrom` link, and the report says "proven equivalent to", never
"intended as" (PRD §19.1).

## Example

### Before

```
%old = Load.u8 %p                      ; %p points into the private array @cells
%keep = And.u8 %old, 0xF0
%ins = And.u8 %v, 0x0F
%new = Or.u8 %keep, %ins
Store.u8 %p, %new
; elsewhere:  %b = Load.u8 %q;  %hi = LShr.u8[strict] %b, 4;  %lo = And.u8 %b, 0x0F
```

### After

```
InsertField[layout=@cells.layout, field=lo] %p, (Trunc.u4 %v)
; elsewhere:  %hi = ExtractField[layout=@cells.layout, field=hi] %q
;             %lo = ExtractField[layout=@cells.layout, field=lo] %q
; @cells.layout = { lo: bits 0..3, hi: bits 4..7 }, representation=free
```

## When it applies

(1) *non-escape*: [#93](https://github.com/Hawkynt/Polypiler/issues/93) and escape analysis prove that no
pointer to the storage reaches an unknown call, another thread, a file, a device or a reinterpreting
cast; the storage is not volatile; (2) *complete access set*: every load and store that may touch the
storage is identified ([#82](https://github.com/Hawkynt/Polypiler/issues/82)); (3) *consistent partition*: the bit ranges used by all
extractions and insertions form one partition of the word — any two ranges are equal or disjoint, and
unused bits are provably written as a constant or never read; (4) *insert exactness*: the inserted
value is confined to its field, by an explicit mask or by a known-bits/range fact, so that no
insertion disturbs a neighbouring field — where the source lets a value spill into the neighbour, that
spill is the program's behaviour and the access is not an insertion; (5) *memory ordering*: for every
read-modify-write, no store to the same word (and no call that could perform one) lies between its
load and its store on any path, so replacing it by an atomic field insertion — which reads the word at
the store — changes nothing; (6) whole-word loads and stores that are not field accesses are pure
copies between cells of the same layout, or constants, which are decomposed into field values.

## How it is proven

`range+alias facts` for obligations 1, 2 and 5 (`AliasFact`, `EffectFact`),
`KnownBitsFact`/`RangeFact` for 4, and `smt-bitvector` per rewritten access (the extraction or
insertion on the layout equals the original shift-and-mask expression on the word); `exhaustive-8bit`
over all word values and inserted values for the nibble layout. A failed, unknown or timed-out
obligation (budget 5 s each) leaves the IR unchanged and reports `not applied: proof timeout` or the
failing precondition.

## When it must not fire

- Escape: the array is written to a file with one call. Its bytes are an external format; separating
  the nibbles would change the file.
- Unmasked insertion: `(old & 0xF0) | v` with `v = 0x1F` and `old = 0xA5` gives `0xBF` — the high
  nibble changes from `A` to `B`. As a field insertion of `lo = 0xF` the result would be `0xAF`.
- Intervening write: `t = cells[i]; cells[i] = 0x33; cells[i] = (t & 0xF0) | 5;` — the final store is
  built from the *old* word; an insertion performed on the current word would keep `0x3` in the high
  nibble.
- Overlapping ranges: bits 0–3 in one place and bits 2–5 in another are not fields of one layout.
- A reinterpreting access (the same bytes read as `u16`): the byte order and packing become
  observable.

## Switching it off

`--disable-opt O0177`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recover` after escape, provenance and alias analysis and after
[#558](https://github.com/Hawkynt/Polypiler/issues/558)-style canonicalization of single-bit accesses; enables storage re-layout, scalar
replacement of the fields and planar vectorization.
