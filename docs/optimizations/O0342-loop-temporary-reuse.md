# O0342 — Allocate a loop-body temporary of stable size once before the loop and reuse it

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0342`.

## What it does

A work buffer allocated and released inside a loop body is allocated and released once per iteration.
When its size does not change, one allocation before the loop serves every iteration: a thousand
allocator round trips become one. [#500](https://github.com/Hawkynt/Polypiler/issues/500) names "allocation hoisting out of loops" as not
planned and [#508](https://github.com/Hawkynt/Polypiler/issues/508) never hoists an instruction with an effect. PB-Compiler implements the rewrite
for fixed-size array temporaries (upstream O0290, pass `LoopTemporaryReuse`). PRD §4.8 (allocation
objective).

## Example

### Before

```
body:
  %t = Alloc.ptr<u32>[heap] 16
  ...                                  // stores to %t[0..15], then loads of them
  Release %t
  %n = Add.u32 %i, 1
  %c = ICmp.ult.u32 %n, %rounds
  Br %c, body, exit
```

### After

```
pre:
  %t = Alloc.ptr<u32>[heap] 16
  Br body
body:
  ...
  %n = Add.u32 %i, 1
  %c = ICmp.ult.u32 %n, %rounds
  Br %c, body, exit
exit:
  Release %t
```

## When it applies

For a loop `L` and an `Alloc` `X` with `Release` `R` in one block `B` of `L`: (1) the count of `X` is a literal or defined outside `L`; (2) `B` dominates the latch, so the buffer is needed in every iteration; (3) `EscapeOf` is `NoEscape` or `ArgEscape` ([#82](https://github.com/Hawkynt/Polypiler/issues/82)) and the value reaches no `Phi`: every use lies between `X` and `R` in `B`; (4) *no value crosses iterations*: every `Load` from the buffer is preceded in `B` by stores that `CompletelyOverwrites` its bytes ([#86](https://github.com/Hawkynt/Polypiler/issues/86)); if `X` is `[zero]` and a load is not so covered, a `Memory.Fill.T %t, 0, n` ([#189](https://github.com/Hawkynt/Polypiler/issues/189)) is inserted at the old position of `X`; if `X` is not `[zero]` an uncovered load blocks the rewrite; (5) *allocation timing*: either the allocation cannot fail observably — its count is a literal within the profile's allocation limit, or the profile declares allocation failure unobservable (the rule of [#500](https://github.com/Hawkynt/Polypiler/issues/500)), or the loop is entered only when its first iteration runs and no instruction with an observable effect or a possible trap precedes `X` in the iteration ([#115](https://github.com/Hawkynt/Polypiler/issues/115)) — then failing in the preheader is indistinguishable from failing in iteration one; (6) `R` is placed at the start of every exit block of `L`; exit blocks get dedicated predecessors first; no instruction between `X` and `R` can leave the loop, which holds because both are in one block; (7) no other release or resize of the region exists; (8) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the hoisted form: `LoopCost(trip)` over allocate + release against one allocate + release plus, where rule (4) inserted it, `LoopCost(trip)` over the fill — `trip` from [#350](https://github.com/Hawkynt/Polypiler/issues/350) or the profile's `unknownTripCount`; under the `memory` objective the longer lifetime of the buffer is weighed.

## How it is proven

`structural+alias-facts+effect-facts`: under (3) and (4) an iteration cannot tell a fresh region from a reused one. No solver, no timeout path.

## When it must not fire

- A `[zero]` buffer used as a histogram that each iteration only increments: reused without the fill, iteration 2 starts with iteration 1's counts — the result for input rows `[1, 1]` and `[1, 1]` is 2 and 4 instead of 2 and 2.
- `for (i = 0; i < n; i++) { t = alloc(big); … }` with `n = 0` and an allocation that cannot be satisfied: the source returns normally, the hoisted version traps `OutOfMemory`.
- A buffer whose address is stored into a list each iteration: every list entry must be a distinct region.
- `alloc(len(line))` with a different length per iteration: the count is not invariant.
- The body prints before allocating and the allocation fails: the source prints once, the hoisted version fails before printing.

## Targets and cost gate

Every target that allocates explicitly — it is as valuable in generated C# or Java (`new int[16]` leaves the loop) as in machine code, and target compilers do not hoist allocator calls. The decision is the cost model's: with a trip count proven to be 1, or with a fill that costs more than the allocator on the profile, it keeps the allocation in the body. In generated source the hoisted declaration sits directly before the loop, as hand-written code has it.

## Switching it off

`--disable-opt O0342`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#508](https://github.com/Hawkynt/Polypiler/issues/508) (invariant counts are already outside) and [#500](https://github.com/Hawkynt/Polypiler/issues/500), before [#513](https://github.com/Hawkynt/Polypiler/issues/513) and [#514](https://github.com/Hawkynt/Polypiler/issues/514) duplicate the body (a cloned lifetime is no longer one alloc/release pair); enables `OPT-MEM-STACKPROMOTE`, which requires the allocation to be outside loops.
