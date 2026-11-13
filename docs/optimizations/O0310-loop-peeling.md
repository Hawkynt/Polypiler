# O0310 — Peel the first iteration of a loop when that decides a branch in both copies

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0310`.

## What it does

`if (i == 0) … else …` inside a loop, a `first` flag that is true once, a "previous element" that does not
exist yet: the branch is taken in the first iteration and never again, but it is tested every time and it
keeps the body from being straight-line code for [#510](https://github.com/Hawkynt/Polypiler/issues/510) and `OPT-LOOP-ADDRIV`. Executing the first
iteration in front of the loop decides the branch in both places. [#513](https://github.com/Hawkynt/Polypiler/issues/513) lists peeling as "not
planned yet"; PB-Compiler's document O0115 plans it and names the two obligations — the remaining loop must
provably not take the branch, and a loop that runs zero times must not execute the peeled copy. PRD §4.8.

## Example

### Before

```
head:
  %i = Phi.u32 [pre: 0], [latch: %i2]
  %c = ICmp.ult.u32 %i, %n
  Br %c, body, exit
body:
  %z = ICmp.eq.u32 %i, 0
  Br %z, first, rest
first:
  Store.i16[inbounds #1] (ElemAddr.ptr<i16>[inbounds #1] %d, %i), 0
  Br latch
rest:
  %p = Sub.u32[wrap] %i, 1
  %x = Load.i16[inbounds #2] (ElemAddr.ptr<i16>[inbounds #2] %a, %i)
  %y = Load.i16[inbounds #3] (ElemAddr.ptr<i16>[inbounds #3] %a, %p)
  %v = Sub.i16[wrap] %x, %y
  Store.i16[inbounds #1] (ElemAddr.ptr<i16>[inbounds #1] %d, %i), %v
  Br latch
latch:
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

### After

```
pre:
  %c0 = ICmp.ult.u32 0, %n
  Br %c0, peel, exit
peel:
  Store.i16[inbounds #1] (ElemAddr.ptr<i16>[inbounds #1] %d, 0), 0
  Br head
head:
  %i = Phi.u32 [peel: 1], [latch: %i2]
  %c = ICmp.ult.u32 %i, %n
  Br %c, rest, exit
rest:
  ...                                   // unchanged, no test of %i against 0
latch:
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

## When it applies

(1) The loop is a natural loop with a single latch and at most 64 instructions. (2) The peeled
copy contains the header's exit test: when the loop would run zero times the copy leaves through the same
exit and executes nothing else — no proof of a positive trip count is needed and none is assumed. (3) The
copy executes the first iteration's instructions in their original order and the loop continues with the
second; no instruction is reordered, so effects and traps are unchanged without any condition on the body.
(4) The trigger branch must fold in the remaining loop by facts, not by pattern: for a comparison of the
counter with its start value, [#94](https://github.com/Hawkynt/Polypiler/issues/94) must show that the counter of the remaining loop never takes
that value again — which fails when the counter can wrap around to it. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the added
copy under the objective.

## How it is proven

`structural+range-facts`: peeling is unrolling by one with the exit test kept; the folded branch is justified by the range of the counter in each copy. No solver, no timeout path.

## When it must not fire

- An unguarded peel: `d[0] = 0` stored in front of `for (i = 1; i < n; i++)` with `n = 0` writes an element the source never touches.
- `for (u8 i = 0; ; i++) { if (i == 0) reset(); … }` (no upper bound): `i` is 0 again in iteration 256; the remaining loop must keep the test.
- A branch on `i == 0` whose taken arm also runs for `i == 5` (`i == 0 || i == 5`): it folds in the copy but not in the loop — no gain, no peel.
- A `first` flag that the body can set back to true: its latch value is not a constant.
- A loop of 200 instructions at `--opt size`: the copy costs more than the branch.

## Targets

Every target. Mature compilers peel only on heuristics or profiles, and the peeled source
(`d[0] = 0; for (i = 1; …)`) is how a programmer would write it by hand. The copy grows the code, so
[#155](https://github.com/Hawkynt/Polypiler/issues/155) ranks `peeled` (one extra body, minus the folded branch per iteration by `LoopCost`) against
`original`, original first; under `size` and `memory` the pass is off.

## Switching it off

`--disable-opt O0310`; active at `--opt speed` and, when the cost model approves, `balanced`; off at `size`, `memory` and `none`.

## Interactions with other passes

runs in phase `optimize` after [#508](https://github.com/Hawkynt/Polypiler/issues/508) and [#514](https://github.com/Hawkynt/Polypiler/issues/514) (branches invariant in every iteration are not its business) and before [#502](https://github.com/Hawkynt/Polypiler/issues/502), [#510](https://github.com/Hawkynt/Polypiler/issues/510) and [#513](https://github.com/Hawkynt/Polypiler/issues/513), which profit from the branch-free body.
