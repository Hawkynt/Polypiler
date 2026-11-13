# O0365 — Keep value facts per incoming path by duplicating a small join region where a later test is decided per path

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0365`.

## What it does

At a merge the analyses join their facts: `x` in `[0, 9]` on one path and `[100, 109]` on the other becomes
`[0, 109]`, and a later `x < 50` is no longer decidable although it is decided on each path. [#188](https://github.com/Hawkynt/Polypiler/issues/188)
and [#94](https://github.com/Hawkynt/Polypiler/issues/94) name path duplication as the consumer's job; `OPT-PROP-PHITHREAD` covers only joins that
hold nothing but the branch. This pass duplicates the few instructions between the merge and the test, so each
copy lives under one path's fact and the test folds in it. PRD §4.4 (path-sensitive facts), §4.8.

## Example

### Before

```
p1:
  Br join                                // x = 5
p2:
  Br join                                // x = 100
join:
  %x = Phi.i32 [p1: 5], [p2: 100]
  Call.void @log(%x)
  %c = ICmp.slt.i32 %x, 50
  Br %c, low, high
```

### After

```
p1:
  Br join.p1
p2:
  Br join
join.p1:
  Call.void @log(5)
  Br low
join:
  Call.void @log(100)
  Br high
```

## When it applies

(1) `J` is not a loop header and no edge into `J` is a back edge. (2) `R` is a forward chain from `J`
in which every block after `J` has the previous block as its only predecessor; it ends at the block holding
the test; at most 4 blocks and 32 instructions; all blocks lie in one exception region ([#115](https://github.com/Hawkynt/Polypiler/issues/115)). (3)
The test is `ICmp.<pred>.<T> x, literal` (or `x` itself for `bool`) feeding a `Br`. (4) For an incoming edge
`e`, the fact of `x`'s incoming value at the end of the predecessor decides the test to true or false; edges
with the same outcome form a group. (5) At least two groups differ, or one group is decided and another
undecided. (6) Every value defined in `R` and used outside it is used only in phis of `R`'s successors or in
blocks reached through one exit block that had `R`'s last block as its only predecessor; that exit gets a
merge phi. Anything else refuses. (7) The original region serves the undecided edges, or the largest decided
group when there are none; each other group gets one clone, at most 3. (8) Instructions of `R` are copied,
never hoisted or dropped: every path still executes each of them exactly once. (9) [#155](https://github.com/Hawkynt/Polypiler/issues/155) scores the
duplicated form strictly below the original under the active objective (one branch saved per decided path
against the copied instructions).

## How it is proven

`structural+range-facts`: a copy is entered only from edges whose fact decides the test, so folding the test there is the deciding rule of [#163](https://github.com/Hawkynt/Polypiler/issues/163) applied under that edge's fact; the copy is otherwise identical. No solver, no timeout path.

## When it must not fire

- Hoisting `Call.void @log(%x)` above the merge instead of copying it executes it before the predecessors' own effects.
- A loop header as `J`: the copy entered from the preheader has no back edge, the loop is left after one iteration.
- An edge with `x` in `[0, 60]` against `x < 50`: the fact overlaps the test; folding it for that edge is wrong for `x = 55`.
- A value defined in `R` and used after two different exits: one merge phi cannot dominate both uses.
- A region of 40 instructions with three decided groups: 80 added instructions for one saved branch.

## Targets

every target that executes branches; the gain is one conditional branch per decided path against
copied code, so the cost model decides and `--opt size` accepts only splits that shrink the function. For
source targets the result prints as two straight `if` arms instead of a flag test; where the structured form
would need a jump the profile's branch cost refuses the split.

## Switching it off

`--disable-opt O0365`; active at `--opt balanced` and `speed`; at `--opt size` and `memory` only when the copies remove at least as many instructions as they add; off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#164](https://github.com/Hawkynt/Polypiler/issues/164), [#163](https://github.com/Hawkynt/Polypiler/issues/163) and `OPT-PROP-PHITHREAD`; enables SCCP and DCE in the copies. Invalidates the CFG analyses of the function.
