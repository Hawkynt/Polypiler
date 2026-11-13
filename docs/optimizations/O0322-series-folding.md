# O0322 — Fold constant-trip accumulations over the loop counter by evaluating their iterates

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0322`.

## What it does

`for i = 1 to 100: s = s + i` with constant bounds adds a number the compiler can know: 5050.
`OPT-LOOP-CLOSEDFORM` derives such totals algebraically, which limits it to affine terms and wrapping
contracts. With a constant trip count there is a second, more general route — PB-Compiler's: do not trust a
formula, *run the iterates* at compile time, so that signed compares, wrap-around, saturation and overflow
traps come out exactly as the loop would produce them. That covers sums of squares, XOR folds and checked
or saturating accumulations the algebra cannot. PRD §4.8 (partial evaluation).

## Example

### Before

```
head:
  %i = Phi.u16 [pre: 1], [body: %i2]
  %s = Phi.u16 [pre: %s0], [body: %s2]
  %c = ICmp.ult.u16 %i, 11
  Br %c, body, exit
body:
  %q  = Mul.u16[wrap] %i, %i
  %s2 = Add.u16[wrap] %s, %q
  %i2 = Add.u16[wrap] %i, 1
  Br head
exit:
  Ret.u16 %s
```

### After

```
head: ...                                // unchanged; dead once %s has no other user
exit:
  %r = Add.u16[wrap] %s0, 385            // 1 + 4 + 9 + … + 100
  Ret.u16 %r
```

## When it applies

(1) The loop has a single exit and `TripCountOf` is `Exact(T)` with `T ≤ 1 048 576`. (2) The
accumulator is a header phi updated exactly once per iteration on every path by `s ⊕ g(i)`; `g` depends
only on header phis with constant entry values and on constants, has no effect and cannot trap for any of
the `T` iterates (checked by evaluating it). (3) With a constant start the pass evaluates the accumulator
for all `T` iterations under the operation's contract; if an iteration traps, the rewrite does not fire —
the loop keeps the trap and its position. (4) With an unknown start, `⊕` must be `[wrap]` `Add`, `Mul`,
`And`, `Or` or `Xor` ([#122](https://github.com/Hawkynt/Polypiler/issues/122)) — associative, so that the fold of the terms can be combined with
the start afterwards; `[sat]` and `[trap]` with an unknown start are refused. (5) The value is used after
the loop at an exit position `OPT-LOOP-CLOSEDFORM` defines. (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the constant; it
always does unless the trip count is 0.

## How it is proven

`interpreter+exhaustive-8bit`: the constant is the evaluator's result for the unchanged iterates; combining a fold with an unknown start relies on associativity of the admitted `[wrap]` operations, checked exhaustively at 8 bit. No solver; a trip count above the bound blocks the rewrite.

## When it must not fire

- `Add.i16[trap Overflow]` summing 1 … 256 from 0: the partial sum 32 640 after 255 terms is fine, the 256th overflows — the loop traps in iteration 256 and must stay.
- `Add.u8[sat]` summing 1 … 30 from an unknown start: the total 465 does not fit `u8`; adding its wrapped value 209 to `s0 = 10` gives 219, the loop gives 255.
- The textbook `n(n+1)/2` for a 16-bit counter running to 400: the sum wraps (80 200 mod 65 536 = 14 664); evaluating the iterates reproduces the wrap, a formula in wider arithmetic does not.
- A term that reads `a[i]`: memory is not a constant.
- `for (u8 i = 1; i <= 255; i++)`: no finite trip count; evaluating "255 iterations" would be a guess.

## Targets

Every target: a constant replaces a loop, in source and in machine code, and the result is both
smaller and faster. No cost alternative ranks the loop better except for a trip count of 0, where nothing
changes. The evaluation cost is compile time only, bounded by the iterate limit.

## Switching it off

`--disable-opt O0322`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` directly after `OPT-LOOP-CLOSEDFORM` and before `OPT-LOOP-DEAD`; the folded constant feeds [#154](https://github.com/Hawkynt/Polypiler/issues/154) and [#164](https://github.com/Hawkynt/Polypiler/issues/164).
