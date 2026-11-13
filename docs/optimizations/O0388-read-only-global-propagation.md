# O0388 — Fold reads of a global that is never written to its initializer

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0388`.

## What it does

A module-level variable that is initialized once and never assigned is a constant, whatever it was declared
as — BASIC and C programs are full of `SHARED`/`static` "configuration" variables that nothing changes. As a
variable every use is a memory read and every expression on it is general: `y * ScreenWidth` is a
multiplication by an unknown. Proving that no write exists turns the reads into the literal, and the folds
cascade. [#162](https://github.com/Hawkynt/Polypiler/issues/162) and `OPT-PROP-LOOPEVAL` also produce constant tables whose constant-index loads
nobody folds yet. PRD §4.8 (constant/partial evaluation).

## Example

### Before

```
global @width: u32[1] = [320]

func @plot(%x: u32, %y: u32) -> u32 {
  %w = Load.u32 @width, 0
  %o = Mul.u32[wrap] %y, %w
  %r = Add.u32[wrap] %o, %x
  Ret.u32 %r
}
```

### After

```
global @width: u32[1] = [320] [readonly]

func @plot(%x: u32, %y: u32) -> u32 {
  %o = Mul.u32[wrap] %y, 320
  %r = Add.u32[wrap] %o, %x
  Ret.u32 %r
}
```

## When it applies

Marking: (1) the global has lifetime class `static`, an initializer, no `volatile` attribute, is not a
device region and is not overlaid ([#121](https://github.com/Hawkynt/Polypiler/issues/121)); (2) every occurrence of the global in the module — in
functions and in other globals' initializers — is the address operand of a `Load`, or of an element-address
computation whose result is used only that way; any other occurrence (the address passed to a call, stored,
compared, returned, used by `Store`, `Copy`, `Fill` or `Move` as destination) leaves it unmarked, because
writes would then not all be visible ([#82](https://github.com/Hawkynt/Polypiler/issues/82): the address does not escape). Folding: (3) the global is
`[readonly]`; (4) the load's index is a literal inside the extent and its type is the element type; (5) the
access is not volatile. The literal is the initializer's element. A literal operand in place of a load is not
expected to cost more on any profile; the load is still listed first in the [#155](https://github.com/Hawkynt/Polypiler/issues/155) query.

## How it is proven

`alias-facts+effect-facts`: with every use of the address enumerated and each a read, no execution writes the region, so each element holds its initializer for the whole run. No solver, no timeout path.

## When it must not fire

- The address passed to an external function: it may store through it.
- A store on a rarely executed path — an error handler, a branch behind a debug flag: it is still a write.
- A volatile global that hardware or another thread changes: reads are observable and must stay reads.
- A region overlaid with another type: the bytes are written through the other view.
- A load of a different type than the element (`u8` from a `u32` global): the literal element is not the loaded value.

## Targets

every target: reads become literals, the multiplication above becomes a constant multiplication
that [#165](https://github.com/Hawkynt/Polypiler/issues/165) can reduce, and the `[readonly]` mark lets backends place the data in ROM or as a
constant. Generated source shows a constant where the source program had a variable nobody assigns.

## Switching it off

`--disable-opt O0388`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

module pass in phase `optimize` after `OPT-IPA-DEADFUNC` removed dead writers and before the per-function [#154](https://github.com/Hawkynt/Polypiler/issues/154) and [#164](https://github.com/Hawkynt/Polypiler/issues/164); enables [#165](https://github.com/Hawkynt/Polypiler/issues/165), `OPT-IPA-DEADGLOBAL` (a scalar global with no loads left) and devirtualization of calls loaded from constant tables.
