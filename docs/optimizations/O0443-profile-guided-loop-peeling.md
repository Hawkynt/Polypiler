# O0443 — Peel the dominant small trip count of a loop as guarded straight-line copies

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0443`.

## What it does

[#513](https://github.com/Hawkynt/Polypiler/issues/513) unrolls a loop whose trip count is a compile-time constant and guesses for the rest.
Many loops have a count that is not constant but nearly always the same small number — three components
per pixel, four bytes per word — and an average says nothing about that: half the calls with zero trips
and half with eight average four, and four is right for nobody. PB-Compiler records the trip-count
*distribution* and peels a dominant small count as guarded copies in front of the untouched loop (PB
O0272, implemented in `ProfileGuidedLoopOptimization`). Each copy repeats the real loop test, so the
profile is a hint only. [#513](https://github.com/Hawkynt/Polypiler/issues/513) itself lists "choosing the factor from profiles" as not planned;
this issue adds exactly that slice. PRD §4.8.

## Example

### Before

```
pre:
  Br head
head:
  %i = Phi.u32 [pre: 0], [body: %i.next]
  %c = ICmp.ult.u32 %i, %n
  CondBr %c, body, exit
body:
  Call.void @emit(%i)
  %i.next = Add.u32[wrap] %i, 1
  Br head
```

### After

```
pre:
  Br head.p0
head.p0:
  %c.p0 = ICmp.ult.u32 0, %n
  CondBr %c.p0, body.p0, head
body.p0:
  Call.void @emit(0)
  %i.next.p0 = Add.u32[wrap] 0, 1
  Br head.p1
head.p1:
  %c.p1 = ICmp.ult.u32 %i.next.p0, %n
  CondBr %c.p1, body.p1, head
…                                       // d copies
head:
  %i = Phi.u32 [head.p0: 0], [head.p1: %i.next.p0], …, [body.p<d-1>: %i.next.p<d-1>], [body: %i.next]
  %c = ICmp.ult.u32 %i, %n
  CondBr %c, body, exit
```

## When it applies

(1) The loop is a natural loop with one back edge, a preheader, and a header that ends in a `CondBr` with exactly one successor outside the loop; the header holds, besides phis, only effect-free non-trapping instructions ([#114](https://github.com/Hawkynt/Polypiler/issues/114)). (2) The profile's `trips` entry for the header has `invocations ≥ 32` and one trip count `d` with `1 ≤ d ≤ 8` and `count(d) × 100 ≥ 90 × invocations`. (3) `d × (instructions of the loop)` ≤ 96. (4) Copy `k` consists of a clone of the header's non-phi instructions with each header phi replaced by the value it would have at iteration `k` (the preheader input for `k = 0`, the previous copy's latch value otherwise), a `CondBr` whose loop-leaving side goes to the *original header*, and a clone of the body. (5) The original header gets one phi input per copy's test block and one from the last copy's body; the original loop is otherwise unchanged. (6) Early exits inside the body (`break`) are cloned with their original targets, whose phis get an input per copy. Instructions are cloned in order, so effects and traps keep their iteration order. **Without a profile** (`--profile-use` absent, or the function stale or unmeasured) the pass does nothing and writes no report entry: every count it asks `IProfileData` for is null, and null never satisfies a threshold. PRD §4.8: no translation needs a profile. **Determinism** (PRD §13.1): the decision is integer arithmetic on counts read through `IProfileData`, candidates are visited in module and block order, ties are broken by block number or name — the same input, compiler, profile and seed give the same output, and the profile's digest is in the manifest's command line (`OPT-PGO-COLLECT`).

## How it is proven

`structural`: the peeled prefix executes the first up-to-`d` iterations with the same tests, bodies and values the loop would have, and hands the loop exactly the state it would have reached. The histogram only chooses `d`. No solver, no timeout path.

## When it must not fire

- Peeling without repeating the test ("the profile says three"): a call with `n = 1` executes two iterations too many.
- Peeling the *average*: 50 % zero trips and 50 % eight average four, and a four-copy prefix is wasted on the first population and insufficient for the second; the rule requires one dominant count.
- Letting a failing test jump to the loop's exit instead of its header: correct only if nothing but the test decides the exit — the header's phis for values live after the loop would be skipped.
- A header with a trapping or effectful instruction before the test: cloning is still correct, but the pre-test form is not a loop this rule recognizes; it is left alone.
- Cloning the profile keys of the loop into the copies and peeling again in the next phase iteration: the prefix would double each time.

## Switching it off

`--disable-opt O0443`; active at `--opt speed`, and at `--opt balanced` for target profiles that do not declare `emits: source`; off at `size`, `memory` and `none`.

## Targets

every target at `--opt speed`: the trip-count distribution is knowledge no downstream compiler has. For source targets the peeled copies make the function longer and less like its original, so at `--opt balanced` the pass runs only for assembly and machine-code profiles; [#155](https://github.com/Hawkynt/Polypiler/issues/155)'s objective is the gate, together with the 96-instruction growth bound. The result is still reducible — every failing test jumps forward to the one header — so [#262](https://github.com/Hawkynt/Polypiler/issues/262) prints it as nested `if`s followed by the loop.

## Interactions with other passes

runs in phase `optimize` after [#508](https://github.com/Hawkynt/Polypiler/issues/508) and before [#513](https://github.com/Hawkynt/Polypiler/issues/513) (upstream's early loop slot); the copies are not given trip data, so neither this pass nor a later iteration peels them again. Enables [#154](https://github.com/Hawkynt/Polypiler/issues/154) and [#164](https://github.com/Hawkynt/Polypiler/issues/164) on the copies (the induction value is a constant in each). Invalidates the CFG.
