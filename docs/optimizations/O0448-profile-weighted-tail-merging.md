# O0448 — Decide between merging, keeping and duplicating shared tails by measured temperature

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0448`.

## What it does

Tail merging and tail duplication are the same trade seen from two sides: a jump against the bytes
of a copy. `OPT-LAYOUT-TAILMERGE` and `OPT-LAYOUT-TAILDUP` each answer it with the static cost model,
`OPT-PGO-COLDDEDUP` and `OPT-PGO-HOTDUP` each with a temperature — four passes that can look at the
same blocks and, in a phase that iterates to a fixed point, undo each other: the static pair is kept apart by a mark of its own, the measured passes are not. PB-Compiler names the
remedy in PB O0399: one policy, driven by weight, and a single arbiter for merge versus duplicate so that
the passes cannot fight across recompiles. This issue is that arbiter. It transforms nothing itself; it
makes the four passes ask one question and remember the answer. PRD §4.2 (controlled fixed point), §4.8.

## Example

### Before

```
tails t1, t2 (identical suffix, both count 0)            → O0422 at --opt speed: kept (a jump is never worth it)
join  j   (count 1000, entry 10; preds jump to it)       → O0422 at --opt size: would merge more into it
```

### After

```
tails t1, t2: policy Merge      → merged by O0446, recorded; O0429/O0447 skip the tail
join  j:      policy Duplicate  → duplicated by O0447, recorded; O0422/O0446 skip its copies
```

## When it applies

The policy changes which legal transformation is performed, never whether one is legal. `ITailPolicy.Decide(candidate)` returns, for a merge candidate (a group of blocks with a congruent suffix) or a duplication candidate (a join and its predecessors): (1) `Merge` if every block of the candidate has temperature `Cold`; (2) `Duplicate` if the join — for a merge candidate, any block of the group — is `Hot`; a merge candidate then answers `Keep` to the merging passes; (3) `Static` in every other case, including any `Unknown` block. A merging pass proceeds on `Merge` or on `Static` plus its own rule; a duplicating pass on `Duplicate` plus its own cost test or on `Static` plus its own rule. (4) Every applied merge or duplication is recorded under the profile keys of the blocks involved; `Decide` returns `Keep` for a candidate containing a recorded key of the opposite kind. Blocks without a key (created later) are recorded by label for the current phase. (5) The record lives for the compilation of the module and is not written to the profile. **Without a profile** every temperature is `Unknown`, every answer is `Static`, nothing is recorded differently from today, and the four passes behave exactly as their own issues specify (PRD §4.8). **Determinism** (PRD §13.1): answers depend only on temperatures and on the record, which is filled in pass order.

## How it is proven

`structural`: the policy performs no rewrite; each transformation it permits carries the proof of the pass that performs it. No solver, no timeout path.

## When it must not fire

- Without the record: `OPT-PGO-COLDDEDUP` merges two measured-cold tails; `OPT-LAYOUT-TAILDUP`, whose static rule sees nothing cold in them, copies the merged tail back into both predecessors; the copies are congruent and cold, so they are merged again — phase `optimize` runs to its iteration cap (PLY0352).
- Answering `Merge` for a group with one `Unknown` block: after a partly stale profile, hot code of the changed function would be merged for size.
- Letting `Duplicate` bypass the duplicating pass's cost test: a hot join of forty instructions would be copied into every predecessor.
- Persisting the record in the profile file: the next build would inherit decisions about blocks that no longer exist.

## Switching it off

`--disable-opt O0448`; active at `--opt balanced`, `speed`, `size` and `memory` when a profile is attached; off at `--opt none`. With `--disable-opt O0448` the four passes decide independently, as before this issue.

## Targets

every target the four passes run for: merging of complete groups reaches source targets, partial merging and all duplication only assembly and machine-code backends, exactly as `OPT-LAYOUT-TAILMERGE` and `OPT-LAYOUT-TAILDUP` state. The policy adds no target rule of its own; where the cost model of [#155](https://github.com/Hawkynt/Polypiler/issues/155) was the gate it stays the gate for `Static` and `Duplicate` answers, and only the `Merge` answer for measured-cold code overrides it.

## Interactions with other passes

a service, not a scheduled pass: registered with [#157](https://github.com/Hawkynt/Polypiler/issues/157) under its ID so that the switch and the report work, and consulted in phase `optimize` by `OPT-LAYOUT-TAILMERGE`, `OPT-PGO-COLDDEDUP`, `OPT-PGO-HOTDUP` and `OPT-LAYOUT-TAILDUP` in that pass order.
