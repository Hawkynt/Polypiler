# O0447 — Duplicate a small join into its predecessors when the join is measured hot

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0447`.

## What it does

`OPT-LAYOUT-TAILDUP` decides from static signs which predecessors of a join are worth a copy: not
cold, not behind an unlikely edge. Those signs are often absent — both arms of a data-dependent `if` look
the same — and sometimes wrong. PB-Compiler plans the profile-driven counterpart (PB O0392): a small
shared block that is hot and reached from two places costs a jump from at least one of them, so it is
copied into the predecessors that actually execute, and into no others. It is the same trade as merging,
seen from the opposite temperature. PRD §4.8.

## Example

### Before

```
pos:   …  Br join            // edge count 600
neg:   …  Br join            // edge count 400
odd:   …  Br join            // edge count 0
join:  %v = Phi.i16 [pos: %a], [neg: %b], [odd: %c]      // count 1000, function entry 10 → Hot
       %w = And.i16 %v, 32767
       Br latch
```

### After

```
pos:   …  %w.pos = And.i16 %a, 32767
          Br latch
neg:   …  %w.neg = And.i16 %b, 32767
          Br latch
odd:   …  Br join
join:  %v = Phi.i16 [odd: %c]
       %w = And.i16 %v, 32767
       Br latch
```

## When it applies

(1) `IProfileData.TemperatureOf(J) = Hot`. (2) Rules 1, 3 and 4 of `OPT-LAYOUT-TAILDUP` hold (`J` is no entry block, loop header or unwind target; its values are used only inside it or through successor phis; copies substitute phis by the predecessor's inputs). (3) A predecessor `P` receives a copy iff it ends in `Br J` and `EdgeCount(P, J) > 0`, or — when that edge has no count of its own — `BlockCount(P) > 0`; at least one predecessor must qualify. (4) The cost test of `OPT-LAYOUT-TAILDUP`'s rule 5 passes with `n` the number of qualifying predecessors, balanced weights, `kept` listed first. (5) The target profile does not declare `emits: source`. (6) Copies inherit `J`'s profile key; `J` stays for the remaining predecessors or is removed when none is left. **Without a profile** (`--profile-use` absent, or the function stale or unmeasured) the pass does nothing and writes no report entry: every temperature is `Unknown`, and `Unknown` is neither hot nor cold. PRD §4.8: no translation needs a profile. **Determinism** (PRD §13.1): temperatures are integer comparisons on counts read through `IProfileData`, candidates are visited in reverse post-order and groups are formed in that order — the same input, compiler, profile and seed give the same output.

## How it is proven

`structural`, the proof of `OPT-LAYOUT-TAILDUP`: each path executes the join's instructions once on the values its phis selected. The counts decide only which predecessors get a copy. No solver, no timeout path.

## When it must not fire

- Copying into the predecessor with edge count 0 as well: bytes are added on a path that never ran.
- Removing the original join because "all hot predecessors have copies": the cold predecessor still branches to it.
- Duplicating a `Warm` join that runs once in a thousand calls because it is small: the growth lands in the hot region for no measured gain.
- Using the count of the join instead of the edge to select predecessors: every predecessor of a hot join would qualify, including the one that never runs.

## Switching it off

`--disable-opt O0447`; active at `--opt balanced` and `speed` when a profile is attached; off at `size`, `memory` and `none`.

## Targets

assembly and machine-code backends only (rule 5): in source output the copy is duplicated text that the target compiler could have produced itself and a reader has to compare. The gate is the cost test of [#155](https://github.com/Hawkynt/Polypiler/issues/155) under balanced weights, as in `OPT-LAYOUT-TAILDUP`; the profile narrows where it is asked.

## Interactions with other passes

runs in phase `optimize` directly before `OPT-LAYOUT-TAILDUP`; a join it handled is not offered to that pass again. With `OPT-PGO-TAILMERGE` enabled, that issue's policy decides for blocks that are both merge and duplication candidates. Invalidates the CFG.
