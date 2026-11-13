# O0124 — Recognize an exchange of two memory cells from the three-xor sequence

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#582](https://github.com/Hawkynt/Polypiler/issues/582). Once implemented it is switched off with `--disable-opt O0124`.

## What it does

Exchanging two variables without a temporary — `a ^= b; b ^= a; a ^= b` — saved a register on machines
that had few. On SSA values the three XORs fold away by themselves; on memory cells they do not, and
there the idiom has a famous defect: when both operands are the same cell, it zeroes it. PRD §19.3
allows the recovery to `Swap` only when aliasing, volatility, distinct addresses and intermediate writes
permit it, and PRD §22 makes the same-address case a mandatory negative fixture.

## Example

### Before

```
%a0 = Load.u32 %p
%b0 = Load.u32 %q
%a1 = Xor.u32 %a0, %b0
Store.u32 %p, %a1
%b1 = Load.u32 %q
%a2 = Load.u32 %p
%b2 = Xor.u32 %b1, %a2
Store.u32 %q, %b2
%a3 = Load.u32 %p
%b3 = Load.u32 %q
%a4 = Xor.u32 %a3, %b3
Store.u32 %p, %a4
```

### After

```
%a0 = Load.u32 %p
%b0 = Load.u32 %q
Store.u32 %p, %b0
Store.u32 %q, %a0
```

## When it applies

(1) [#82](https://github.com/Hawkynt/Polypiler/issues/82) proves `p` and `q` `NoAlias` for the accessed width —
distinct and non-overlapping, not merely unequal; (2) [#80](https://github.com/Hawkynt/Polypiler/issues/80) classifies both cells as
ordinary program memory: not volatile, not device or memory-mapped, not shared with another thread
without synchronization; (3) nothing between the first load and the last store can read or write
either cell — no call, no other store that may alias, no trap that would expose the intermediate state
(a load that may fault is fine only if it is one of the pattern's own loads of `p`/`q`); (4) the loads
inside the pattern read the values just stored, so the sequence is the textbook one and not three
unrelated xors. The intermediate values `%a1`, `%b2` must have no user outside the pattern.

## How it is proven

`range+alias facts` (obligations 1–3 as `AliasFact` and `EffectFact`) plus
`exhaustive-8bit` over all 65 536 value pairs of two distinct cells and `smt-bitvector` at 16, 32 and
64 bit on the two-cell model. A failed, unknown or timed-out obligation (budget 5 s each) leaves the
IR unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Same cell: with `q = p` holding `0x00001234`, the XOR sequence leaves 0; an exchange leaves
  `0x00001234`.
- Partial overlap: `u32` cells at `p` and `p + 2` — the stores corrupt each other's bytes.
- Volatile or device cell: the three intermediate stores are observable; an exchange writes each cell
  once.
- A call between the statements that may read `p`: it sees `a ^ b`.
- Array elements `a[i]`, `a[j]` with no fact on `i`, `j`: a Fisher–Yates shuffle swapping an element
  with itself zeroes it in the original.

## Switching it off

`--disable-opt O0124`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after alias analysis and before dead-store elimination,
which would otherwise remove the first store to `p` and destroy the pattern; enables register
promotion of the two cells.
