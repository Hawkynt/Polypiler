# O0396 — Run calls proven independent by their effect summaries concurrently

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0396`.

## What it does

`ProcessLeft(data); ProcessRight(data);` — two calls that touch disjoint halves of a buffer and print nothing —
can run at the same time. [#958](https://github.com/Hawkynt/Polypiler/issues/958) parallelizes loop iterations and names task graphs as not planned;
the effect summaries of [#87](https://github.com/Hawkynt/Polypiler/issues/87) already say which memory each call reads and writes, which is
most of the analysis. What is missing is everything after the proof: a way to say "these calls may run
concurrently" in the IR, and a target that can do it. PRD §4.8 (threads as a cost-model alternative), §17.1
(concurrency and happens-before are observable).

## Example

### Before

```
Call.void @ProcessLeft(%data, %half)       // writes region(%data)[0, half)
Call.void @ProcessRight(%data, %half)      // writes region(%data)[half, 2·half)
```

### After

```
(a concurrent invocation of the two calls followed by a join — the IR construct is the open question below)
```

## When it applies

For every pair of calls `A`, `B` of the run, from the instantiated summaries of
[#87](https://github.com/Hawkynt/Polypiler/issues/87): (1) `Writes(A) ∩ (Reads(B) ∪ Writes(B)) = ∅` and symmetrically, with regions and
byte ranges compared through [#82](https://github.com/Hawkynt/Polypiler/issues/82); `Unknown` or `Everything` on either side is a dependence; (2)
neither call has an effect outside memory — no external call, device, file, console, clock or allocation
whose order is observable ([#114](https://github.com/Hawkynt/Polypiler/issues/114)); (3) neither may trap (`MayTrap = false`) nor diverge
(`MayDiverge = false`): a trap of `A` must not be preceded by effects of `B` that the sequential program
would not have performed; (4) no result of `A` is an argument of `B`; (5) the run lies in one block and one
exception region; (6) the target capability graph offers `Threads` ([#259](https://github.com/Hawkynt/Polypiler/issues/259)) and [#155](https://github.com/Hawkynt/Polypiler/issues/155)
prefers `ThreadedCost` over the sequential cost.

## How it is proven

`effect-facts+alias-facts`: two calls whose effects are confined to memory, write disjoint locations, read nothing the other writes, and can neither trap nor diverge commute, so every interleaving equals the sequential order. No solver, no timeout path.

## When it must not fire

- Two calls that each print one line: memory-disjoint, but the order of the lines is observable.
- A callee that may trap: under concurrency the other call's writes may or may not have happened when the trap is observed.
- Halves of one buffer with an off-by-one bound: element `half` is written by both.
- A callee whose summary is `Unknown` because it calls through a function pointer.
- Two calls that both allocate: the addresses they receive depend on the order.

## Targets

only targets with threads in their capability set — hosted runtimes such as .NET, the JVM, Rust or
C with a threading library. Retro and freestanding targets never see the rewrite. Generated source changes
shape visibly (a parallel invoke instead of two statements), so the rewrite is opt-in by level.

## Switching it off

`--disable-opt O0396`; active at `--opt speed` only and only on targets with `Threads`; off at `balanced`, `size`, `memory` and `none`.

## Interactions with other passes

runs in phase `specialize`, where parallel regions are legal (`docs/adr/0001-ir-stage-invariants.md`), after [#160](https://github.com/Hawkynt/Polypiler/issues/160) and [#159](https://github.com/Hawkynt/Polypiler/issues/159); competes with nothing — a call that was inlined is no longer a task.
