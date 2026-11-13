# O0320 — Evaluate the first iterations of a loop at compile time and start the run-time loop after them

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0320`.

## What it does

`acc = 1; for i = 1 to n: acc = acc * i` (a factorial) cannot be folded — `n` is unknown — but every state the loop passes
through is a constant: 1, 1, 2, 6, 24 … The loop's result for small `n` is a table lookup, and for large `n`
the loop can start where the table ends. [#162](https://github.com/Hawkynt/Polypiler/issues/162) evaluates calls whose arguments are all
constant and stops here because one input is not. PB-Compiler plans the prefix evaluation and names the trap
it sets: the guard for trip counts shorter than the evaluated prefix. PRD §4.8 (partial evaluation).

## Example

### Before

```
head:
  %i   = Phi.u32 [pre: 0], [body: %i2]
  %acc = Phi.u32 [pre: 1], [body: %acc2]
  %c   = ICmp.ult.u32 %i, %n
  Br %c, body, exit
body:
  %i2   = Add.u32[wrap] %i, 1
  %acc2 = Mul.u32[wrap] %acc, %i2
  Br head
exit:
  Ret.u32 %acc
```

### After

```
// global @o0320.prefix: u32[5] = [1, 1, 2, 6, 24] [readonly]        (N = 4 in this listing)
// proofs { #1 bounds: "%n <= 4" }
pre:
  %short = ICmp.ule.u32 %n, 4
  Br %short, table, head
table:
  %t = Load.u32[inbounds #1] (ElemAddr.ptr<u32>[inbounds #1] @o0320.prefix, %n)
  Br exit
head:
  %i   = Phi.u32 [pre: 4],  [body: %i2]
  %acc = Phi.u32 [pre: 24], [body: %acc2]
  %c   = ICmp.ult.u32 %i, %n
  Br %c, body, exit
body:
  ...
exit:
  %r = Phi.u32 [table: %t], [head: %acc]
  Ret.u32 %r
```

## When it applies

(1) The loop has a single exit and `TripCountOf` is `Symbolic(v, offset)` ([#350](https://github.com/Hawkynt/Polypiler/issues/350)): the
number of iterations is `v + offset`, proven not to wrap ([#94](https://github.com/Hawkynt/Polypiler/issues/94)). (2) Every header phi has a
constant entry value, and every instruction of the loop other than the exit comparison has only header
phis, loop-defined values and constants as operands; it has no effect ([#114](https://github.com/Hawkynt/Polypiler/issues/114)) and its calls are
pure and deterministic by [#162](https://github.com/Hawkynt/Polypiler/issues/162)'s rule. (3) The prefix is evaluated iteration by iteration;
it ends at `N`, at the budget, or before the first iteration in which an instruction traps — the loop then
starts at that iteration and traps exactly as before. (4) The table holds, for each `t` in `0 … N`, the
value every live-out has when the loop exits after `t` iterations; the guard compares the *trip count* with
`N`, and the table access carries a bounds proof from that guard ([#121](https://github.com/Hawkynt/Polypiler/issues/121)). (5) The loop's entry
values become the state after `N` iterations. (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the result: one compare and, for
short counts, one load, against `N` iterations saved, with the table in the size and memory terms.

## How it is proven

`interpreter+structural+range-facts`: the table entries and the new entry state are the evaluator's results for the unchanged loop body; the guard partitions the trip counts exactly. No solver; an evaluation that exceeds the budget shortens the prefix.

## When it must not fire

- No guard: for `n = 2` the loop is entered at `i = 4` with `acc = 24`, the condition `4 < 2` fails at once and the function returns 24 instead of 2.
- A guard on `n` where the loop runs `n + 1` times (`for i = 0 to n`): every table index is off by one.
- `acc = acc * i + x` with `x` a parameter: the states are not constants; nothing can be evaluated.
- `Mul.u32[trap Overflow]`: 13! does not fit 32 bits — the prefix must stop at 12 iterations and leave the trapping iteration to the run-time loop.
- A body that prints `acc`: the prefix's output would vanish.

## Targets

Every target: a table lookup beats `n` multiplications on a 6502 as on an x86-64, and no target
compiler performs this rewrite. The table costs data and the guard code, so [#155](https://github.com/Hawkynt/Polypiler/issues/155) decides — on
memory-starved targets under `size` or `memory` the pass is off. The generated source gains a constant
array and an `if`; the loop below it is unchanged apart from its start values, which stays readable.

## Switching it off

`--disable-opt O0320`; active at `--opt speed` (N ≤ 16) and, when the cost model approves, `balanced` (N ≤ 8); off at `size`, `memory` and `none`.

## Interactions with other passes

runs in phase `optimize` after `OPT-LOOP-CLOSEDFORM`, `OPT-LOOP-SERIES` and [#162](https://github.com/Hawkynt/Polypiler/issues/162) (which take every loop they can), and before [#513](https://github.com/Hawkynt/Polypiler/issues/513); [#164](https://github.com/Hawkynt/Polypiler/issues/164) removes the loop when the range proves the guard always true.
