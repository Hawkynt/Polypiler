# O0364 — Execute a loop with fully known inputs at compile time and emit its final state as data

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0364`.

## What it does

Programs build their lookup tables at start-up: sixteen squares, a CRC table, a sine ramp. The loop that fills
the table has no input that is not a literal, so its result is known when compiling — but [#162](https://github.com/Hawkynt/Polypiler/issues/162)
evaluates only *calls* with constant arguments, and [#513](https://github.com/Hawkynt/Polypiler/issues/513) with [#164](https://github.com/Hawkynt/Polypiler/issues/164) folds only loops small
enough to unroll and never the stores. On a 6502 or in tokenized BASIC the fill loop costs seconds at every
start; as data it costs nothing. PRD §4.8 (constant/partial evaluation).

## Example

### Before

```
pre:
  %t = Alloc.ptr<u16>[stack] 16
  Br head
head:
  %i = Phi.u16 [pre: 0], [body: %n]
  %c = ICmp.ult.u16 %i, 16
  Br %c, body, exit
body:
  %s = Mul.u16[wrap] %i, %i
  Store.u16 %t, %i, %s
  %n = Add.u16[wrap] %i, 1
  Br head
exit:
  %v = Load.u16 %t, %k
```

### After

```
global @f$t.init: u16[16] = [0, 1, 4, 9, 16, 25, 36, 49, 64, 81, 100, 121, 144, 169, 196, 225] [readonly]

pre:
  %t = Alloc.ptr<u16>[stack] 16
  Copy.u16 %t, @f$t.init, 16
  Br exit
exit:
  %v = Load.u16 %t, %k                 // reads @f$t.init directly when %t is never written again
```

## When it applies

All of: (1) the loop is a natural loop with one preheader, and every SSA value live into it is a
literal after [#164](https://github.com/Hawkynt/Polypiler/issues/164); (2) [#80](https://github.com/Hawkynt/Polypiler/issues/80) reports no effect in the loop other than `Memory.Write` to
regions of the set `R` and `Memory.Read` of `R` and of `[readonly]` globals; calls are allowed only to
functions it proves pure and deterministic; (3) every region of `R` is allocated in this function with a
constant extent, [#82](https://github.com/Hawkynt/Polypiler/issues/82) proves it does not escape before the loop exit, and it is neither volatile nor
device memory; (4) the interpreter runs the loop from the preheader state to its exit within the step budget,
without a trap, without reading an element of `R` that the function has not written (unless the allocation is
specified zero-initialized), and with exact-foldable float operations only; (5) the bytes written total at most
65 536; (6) elements of `R` the loop did not write keep their prior state: the `Copy` covers exactly the
written prefix or the pass refuses a sparse write set; (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155) scores the data form strictly below
the loop (`LoopCost(trip)` against the `Copy` and the data bytes); the loop is listed first.

## How it is proven

`interpreter+effect-facts+alias-facts`: the emitted state is the loop's own semantics executed once on its only possible input; the facts guarantee that nothing else can observe or change the regions meanwhile. Budget exhaustion leaves the loop in place (`not applied: step budget exceeded`).

## When it must not fire

- `Store.u8 %t, %i, Mul.u8[trap Overflow] %i, %i` for `i` up to 31: the source stores 16 squares and traps at `i = 16`; emitting a table would lose the trap and invent 16 values.
- A loop that passes `%t` to `@observe` in its body: the callee sees the half-filled table.
- A loop that reads `t[i + 1]` before writing it on a region that is not zero-initialized: the value is not defined by the program.
- `while (true)` with known inputs: it never reaches the exit; termination is observable (PRD §17.1).
- A table of 40 000 `u32` elements: 160 000 bytes of initialized data replace a ten-instruction loop.

## Targets

every target, most of all interpreted and slow ones (tokenized BASIC gets `DATA`, a 6502 image gets
a ROM table). Under `--opt size` and `--opt memory` the cost model compares the data bytes with the loop and
keeps the loop for large tables. A mature target compiler does not evaluate such loops across a store reliably,
and a table literal in generated source is at least as readable as the loop that built it.

## Switching it off

`--disable-opt O0364`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`; always subject to the cost decision.

## Interactions with other passes

runs in phase `optimize` after [#164](https://github.com/Hawkynt/Polypiler/issues/164) and [#162](https://github.com/Hawkynt/Polypiler/issues/162); enables `OPT-IPA-CONSTGLOBAL` (loads from the table fold), [#156](https://github.com/Hawkynt/Polypiler/issues/156) and table recognition; a loop it refuses is still open to [#513](https://github.com/Hawkynt/Polypiler/issues/513).
