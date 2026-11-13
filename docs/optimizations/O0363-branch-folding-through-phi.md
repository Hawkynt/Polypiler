# O0363 — Thread predecessors through a join that only branches on a phi with a known input per edge

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0363`.

## What it does

`if (c > 0) mode = 1; else mode = 0;` followed by `if (mode) …` reaches the IR as a join block that holds a phi
and immediately branches on it. Each predecessor already knows which way that branch goes; executing the phi
and the test is pure overhead, and in generated source it shows as a flag variable that exists only to be
tested once. [#164](https://github.com/Hawkynt/Polypiler/issues/164) cannot remove it because the phi is not constant — it is constant *per edge*. PRD §4.8
(SCCP and its follow-ups), §17.1.

## Example

### Before

```
p1:
  Br join
p2:
  Br join
join:
  %m = Phi.i32 [p1: 1], [p2: 0]
  %c = ICmp.ne.i32 %m, 0
  Br %c, a, b
a:
  %ra = Phi.i32 [join: %m]
  ...
```

### After

```
p1:
  Br a
p2:
  Br b
a:
  %ra = Phi.i32 [p1: 1]
  ...
```

## When it applies

For the edge `P → B`: (1) `P` ends in an unconditional `Br B`; (2) `B` holds only phis, optionally
one comparison `ICmp.<pred>.<T> φ, literal` with the terminator as its only user, and the terminator; (3) the
terminator's condition is one of `B`'s phis or that comparison; (4) the value arriving from `P` is a literal,
so the condition folds to a known successor `S` through [#154](https://github.com/Hawkynt/Polypiler/issues/154)'s evaluator as used by [#164](https://github.com/Hawkynt/Polypiler/issues/164);
(5) `S` is neither `P` nor `B`, and `P` is not already a predecessor of `S` with different phi inputs; (6)
every phi of `S` with an incoming value from `B` can be translated: a phi of `B` becomes its value from `P`,
any other value already dominates `P`; (7) the phis of `B` have no users outside `B` and the phis of its
successors; (8) `B` is not a loop header and `P → B` is not a back edge; (9) `P`, `B` and `S` lie in the same
exception region ([#115](https://github.com/Hawkynt/Polypiler/issues/115)). The rewrite removes one executed branch and adds no instruction; both forms
are still offered to [#155](https://github.com/Hawkynt/Polypiler/issues/155), the original first.

## How it is proven

`structural`: on the edge from `P` the join computes nothing but the selection of `S`, and the values `S` receives are the ones the two-edge path delivered. No solver, no timeout path.

## When it must not fire

- A join that holds `Call.void @log(%m)` before the branch: bypassing it drops the call on that path.
- A phi of the join that is used further down: after `P` jumps to `S` directly, the join no longer dominates that use.
- A loop header: threading its entry edge past it creates a second entry into the loop.
- A successor that already has `P` as predecessor with another phi input: the phi is keyed by predecessor block and cannot hold two values for it.
- A comparison in the join with a second user: folding it for one edge leaves the other user without a definition.

## Targets

every target: one conditional branch less per execution in machine code, and in source output the
flag variable and its test vanish because the two `if` statements merge. The rewrite never adds code, so no
cost profile is expected to refuse it; a mature target compiler performs the same threading, and the generated
source is shorter, not less readable.

## Switching it off

`--disable-opt O0363`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#164](https://github.com/Hawkynt/Polypiler/issues/164) (which makes incoming values literal); enables [#156](https://github.com/Hawkynt/Polypiler/issues/156) of the phi's inputs and further SCCP in the successors. Invalidates the CFG analyses of the function.
