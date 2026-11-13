# O0300 — Delete counted loops that are proven finite and have no observable effect

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0300`.

## What it does

Once closed forms have replaced what a loop computed (`OPT-LOOP-CLOSEDFORM`, `OPT-LOOP-SERIES`), the loop
itself is still there: a counter that counts, a compare, a back edge, and nothing anybody reads. [#156](https://github.com/Hawkynt/Polypiler/issues/156)
deliberately leaves it ("termination is observable", PRD §17.1) because it cannot tell a loop that ends from
one that does not. With a trip count proven by [#350](https://github.com/Hawkynt/Polypiler/issues/350) that objection is gone: a loop that provably ends
and has no effect can be replaced by a jump to its exit. PRD §4.8 (dead code), §17.1 (termination).
PB-Compiler describes the rewrite twice — as the empty-loop idiom and as the pass that collects what the
closed-form pass leaves behind; both documents describe one pass and are delivered by this one issue.

## Example

### Before

```
pre:
  Br head
head:
  %i = Phi.u32 [pre: 0], [body: %i2]
  %c = ICmp.ult.u32 %i, 1000
  Br %c, body, exit
body:
  %i2 = Add.u32[wrap] %i, 1
  Br head
exit:
  Ret.u32 7
```

### After

```
pre:
  Br exit
exit:
  Ret.u32 7
```

## When it applies

Loop `L` is deleted iff (1) `TripCountOf(L)` is `Exact`, `Symbolic` or `Max` ([#350](https://github.com/Hawkynt/Polypiler/issues/350)) —
`Unknown` blocks the rewrite, because a loop that may run forever must keep running; (2) `L` has exactly one
exit edge; (3) every instruction of `L` has an empty effect set ([#114](https://github.com/Hawkynt/Polypiler/issues/114), [#80](https://github.com/Hawkynt/Polypiler/issues/80) for calls)
— no store, no call with effects, no load of volatile or device memory — and cannot trap ([#115](https://github.com/Hawkynt/Polypiler/issues/115)):
an instruction that may trap is accepted only with a fact that excludes the trap ([#94](https://github.com/Hawkynt/Polypiler/issues/94): divisor
non-zero, index in bounds, no overflow under a `[trap]` contract); (4) no value defined in `L` is used
outside `L`, including by a phi of the exit block; (5) the selected level is `speed`.

## How it is proven

`structural+effect-facts+range-facts`: a terminating computation without effects, traps or live results is unobservable. No solver, no timeout path.

## When it must not fire

- `for (u8 i = 0; i <= 255; i++) {}`: the condition is always true; deleting the loop turns a hang into a return.
- `for (i = 0; i < n; i++) { q = a / b; }` with `q` unused and `b = 0`: the source traps in the first iteration.
- `for (i = 0; i < 1000; i++) {} return i;`: the counter is read afterwards; the loop may go only after its exit value 1000 has been substituted.
- A loop polling a volatile status register without using the value: every read is an effect.
- A delay loop on a 6502 or DOS target at `--opt size` or `balanced`: its only purpose is the time it takes.

## Targets

Every target: the deleted loop costs time and code everywhere, and no target compiler is trusted to
remove it (a BASIC interpreter, an assembler and PowerBASIC do not). Generated source gets shorter, never
less readable. The gate is the level, not [#155](https://github.com/Hawkynt/Polypiler/issues/155) — deletion has no cost alternative to rank; the
restriction to `speed` exists because on retro targets an empty counted loop is a deliberate delay
(upstream gates it on `$OPTIMIZE SPEED` for the same reason).

## Switching it off

`--disable-opt O0300`; active at `--opt speed` only; off at `none`, `balanced`, `size` and `memory`.

## Interactions with other passes

runs in phase `optimize` after `OPT-LOOP-CLOSEDFORM`, `OPT-LOOP-SERIES` and [#156](https://github.com/Hawkynt/Polypiler/issues/156) (which empty the body); enables [#164](https://github.com/Hawkynt/Polypiler/issues/164) block merging around the removed loop.
