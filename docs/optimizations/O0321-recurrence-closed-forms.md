# O0321 — Replace the exit value of linear, triangular and doubling recurrences by a closed form

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0321`.

## What it does

A loop that adds a constant `n` times has computed `start + n·step`; one that sums its own counter has
computed a triangular number; one that doubles a value has shifted it. Running the loop to find that out is
what unoptimized BASIC and C do, and after the closed form is substituted the loop is usually dead
(`OPT-LOOP-DEAD` removes it). In wrapping arithmetic the closed forms are exact for every trip count,
including across overflow; under a trapping or saturating contract they are not. PB-Compiler implements the
constant-step case (`RecurrenceClosedForm`) and plans the rest. PRD §4.8.

## Example

### Before

```
head:
  %i = Phi.u32 [pre: 0], [body: %i2]
  %s = Phi.u32 [pre: %s0], [body: %s2]
  %c = ICmp.ult.u32 %i, %n
  Br %c, body, exit
body:
  %s2 = Add.u32[wrap] %s, %i
  %i2 = Add.u32[wrap] %i, 1
  Br head
exit:
  Ret.u32 %s
```

### After

```
head: ...                                // unchanged; dead once %s has no other user
exit:
  %t1  = Sub.u32[wrap] %n, 1
  %odd = And.u32 %n, 1
  %ev  = ICmp.eq.u32 %odd, 0
  %p   = Select.u32 %ev, (LShr.u32[strict] %n, 1), %n
  %q   = Select.u32 %ev, %t1, (LShr.u32[strict] %t1, 1)
  %tri = Mul.u32[wrap] %p, %q              // n(n-1)/2 modulo 2^32
  %r   = Add.u32[wrap] %s0, %tri
  Ret.u32 %r
```

## When it applies

(1) The loop has a single exit and `TripCountOf` is `Exact` or `Symbolic`, with the count's true
value below `2^w` for the recurrence's width `w` ([#350](https://github.com/Hawkynt/Polypiler/issues/350), [#94](https://github.com/Hawkynt/Polypiler/issues/94)). (2) The recurrence is a
header phi updated exactly once per iteration, on every path, by the operation of its class. (3) Contract:
the update — and for the triangular class the addend's own recurrence — carries `[wrap]` ([#122](https://github.com/Hawkynt/Polypiler/issues/122)),
or carries `[trap …]`/`[undefined]` and [#94](https://github.com/Hawkynt/Polypiler/issues/94) proves it cannot overflow in any iteration (it then
behaves as `[wrap]`). `[sat]` and floating point are refused. (4) The closed form replaces a use after the
loop of the header phi when the exit is taken from the header after `T` body executions, and of the update
value when the exit is taken from the latch; any other exit position blocks. (5) The exit-block arithmetic
is `[wrap]`, `Select` and shifts — nothing that can trap, so a zero-trip execution is unaffected and yields
the start value. (6) Profit: the phi has no user inside the loop other than its update, or the closed form
folds to a constant; and [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the closed form to `LoopCost` over the trip count's range.

## How it is proven

`exhaustive-8bit+smt-bitvector+range-facts`: each class's closed form equals the iterated recurrence for all 8-bit inputs and trip counts; at 16, 32 and 64 bit the base case and the inductive step are discharged by the solver (5 s per obligation; on timeout `not applied: proof timeout`). The obligations are per class and width, not per loop.

## When it must not fire

- `f64`: adding 0.1 ten times gives 0.9999999999999999, `0.1 * 10` gives 1.0.
- `Add.i16[trap Overflow]` adding 1000 forty times: the source traps in iteration 33 (33 000 > 32 767); the closed form 40 000 either wraps or traps once, at the wrong place.
- `Add.u8[sat]` adding 100 four times: 100, 200, 255, 255 — the closed form gives 144 (400 mod 256).
- `n(n−1)/2` computed as `(n * (n − 1)) >> 1` in 32 bits for `n = 70 000`: the product 4 899 930 000 wraps to 604 962 704 before the shift and the result is 302 481 352; halving the even factor first gives the exact 2 449 965 000.
- A loop with a `break`: the number of updates is not the trip count.
- `x' = x * 3`: not a doubling; `3^T` has no shift form.

## Targets

Every target, source and machine code alike: even mature compilers apply these closed forms only
in their simplest shape, and no BASIC, assembler or bytecode target does. The generated source replaces a
loop by a formula, which is shorter and clearer. The exception is a symbolic trip count on a CPU without a
multiply: `k·T` is then a helper call, and for a proven small `T` the loop can be cheaper — [#155](https://github.com/Hawkynt/Polypiler/issues/155)
ranks `loop` (`LoopCost` at the midpoint of the trip-count range) against `closed form`, loop first.

## Switching it off

`--disable-opt O0321`; active at `--opt balanced`, `speed`, `size` and `memory` when the cost model approves; off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#156](https://github.com/Hawkynt/Polypiler/issues/156) and [#508](https://github.com/Hawkynt/Polypiler/issues/508) and before `OPT-LOOP-SERIES` and `OPT-LOOP-DEAD`; enables [#154](https://github.com/Hawkynt/Polypiler/issues/154) on the formula and `OPT-LOOP-COUNTDOWN` (a counter read only after the loop is no longer read at all).
