# O0333 — Carry the overlapping part of a sliding-window load across iterations instead of reloading it

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0333`.

## What it does

Filters, delta coders and rolling hashes read a window that slides by one element per iteration:
`d[i] = a[i] - a[i-1]` loads every element twice, and a rolling four-byte window loads three of its four
bytes again each time. [#158](https://github.com/Hawkynt/Polypiler/issues/158) cannot see the redundancy because the two loads are in different
iterations. PB-Compiler plans this as overlapping load combining (upstream O0138). PRD §4.8 (redundant
memory traffic).

## Example

### Before

```
body:
  %i   = Phi.u32 [pre: 1], [body: %n]
  %cur = Load.i16[bounds] (ElemAddr %a, %i)
  %im1 = Sub.u32 %i, 1
  %prv = Load.i16[bounds] (ElemAddr %a, %im1)
  %d   = Sub.i16[wrap] %cur, %prv
  Store.i16[bounds] (ElemAddr %dst, %i), %d
  %n   = Add.u32 %i, 1
  %c   = ICmp.ult.u32 %n, %len
  Br %c, body, exit
```

### After

```
pre:
  %p0  = Load.i16[bounds] (ElemAddr %a, 0)
  Br body
body:
  %i   = Phi.u32 [pre: 1], [body: %n]
  %prv = Phi.i16 [pre: %p0], [body: %cur]
  %cur = Load.i16[bounds] (ElemAddr %a, %i)
  %d   = Sub.i16[wrap] %cur, %prv
  Store.i16[bounds] (ElemAddr %dst, %i), %d
  ...
```

## When it applies

For a loop `L` and loads `A` (the *leading* load) and `B` (the *trailing* load): (1) [#350](https://github.com/Hawkynt/Polypiler/issues/350) gives both addresses as `AddRec` of `L` with the same constant step and starts that differ by exactly `d` steps, `1 ≤ d ≤ 4`, without wrap (`NoUnsignedWrap` or `NoSignedWrap`), into the same region; form 2 additionally needs step 1 byte and a `u8` region; (2) both loads have the same type, access contract and byte order and are not volatile; (3) `A` executes in every iteration (its block dominates the latch), so the carried value exists; (4) *no intervening write*: no store, call or unknown-memory write inside `L` may alias the element between `A`'s read of it and `B`'s read of it `d` iterations later — `MemorySsa.ClobberingAccess` for `B` followed around the back edge must not find a def for which [#82](https://github.com/Hawkynt/Polypiler/issues/82) answers `MayAlias` ([#86](https://github.com/Hawkynt/Polypiler/issues/86)); (5) the region is not released or resized in `L`; (6) *priming*: the loads `B` performs in the first `d` iterations move to the preheader; each must either be proven unable to trap there (`IsInBounds` is `True`, [#93](https://github.com/Hawkynt/Polypiler/issues/93)) or, for `d = 1`, the loop is entered only when its first iteration runs and `B` executed in that iteration before any instruction with an observable effect ([#115](https://github.com/Hawkynt/Polypiler/issues/115)) — otherwise a trap would appear in a zero-trip execution or move in front of an effect; (7) in later iterations the removed load cannot have trapped: the same address was loaded successfully `d` iterations earlier and (5) holds; (8) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts one more loop-carried value against one load per iteration.

## How it is proven

`scev-facts+alias-facts+effect-facts`, plus `exhaustive-16bit+smt-bitvector` for form 2 (`W' = Or(LShr(W, 8), Shl(ZeroExtend(b), 8k-8))` for `LoadLE`, `W' = Or(Shl(W, 8), ZeroExtend(b))` for `LoadBE`). A solver timeout (5 s per obligation) leaves the loop unchanged and reports `not applied: proof timeout`.

## When it must not fire

- In place: `a[i] = a[i] - a[i-1]` over `a = [1, 3, 6]`. Reloading gives `a[1] = 2`, then `a[2] = 6 - 2 = 4`: `[1, 2, 4]`. Carrying the old `a[1] = 3` gives `a[2] = 3`: `[1, 2, 3]`.
- `dst` may alias `a` and no fact says otherwise: the same failure, undetected without the alias query.
- A top-tested loop with `len = 1` over an empty array: the source loads nothing; an unconditional priming load of `a[0]` traps `IndexOutOfRange`.
- Form 2 with an arithmetic right shift on a signed window: `0x80332211 >> 8` fills with ones and corrupts the top byte.
- A call in the body that may write the array (no summary): the carried value is stale.
- A volatile window (a FIFO data register): each load pops a different value.

## Targets and cost gate

Every target. In generated source the result is the idiom a programmer writes by hand (`prev = cur` at the end of the body), and target compilers perform this only at their highest levels (predictive commoning) and never across a possible alias they cannot disprove, whereas PolyIR's alias and bounds facts come from the source language. The cost model decides the trade of one load for one loop-carried value: on a profile where an indexed load is cheaper than keeping a value alive across the iteration it answers `reload`, and the loop is left alone.

## Switching it off

`--disable-opt O0333`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#508](https://github.com/Hawkynt/Polypiler/issues/508) and [#158](https://github.com/Hawkynt/Polypiler/issues/158); enables `OPT-MEM-CONTRACT` (no in-loop loads of the window remain), [#156](https://github.com/Hawkynt/Polypiler/issues/156) (the index arithmetic of the trailing load) and [#510](https://github.com/Hawkynt/Polypiler/issues/510) on the simplified body.
