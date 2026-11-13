# O0368 — Version a reconverged region per branch-established fact so that a later guard folds in the copy

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0368`.

## What it does

`if (x < 256) … else …` followed, after the arms rejoin, by `if (x < 128)`: on the path where `x ≥ 256` the
second test is known to be false, but at the join the analyses forget which arm was taken. Where
`OPT-PROP-PATHFACTS` keeps the facts of a *phi's inputs* apart, this pass keeps apart the facts a dominating
*branch* established about a value that is not a phi at all: it copies the reconverged region for the arm whose
fact makes a later guard constant and leaves the other arm on the general code. PRD §4.4, §4.8.

## Example

### Before

```
entry:
  %c = ICmp.ult.u32 %x, 256
  Br %c, small, large
small:
  Br join
large:
  Br join
join:
  %t = Phi.u32 [small: 1], [large: 2]
  %d = ICmp.ult.u32 %x, 128
  Br %d, print, done
```

### After

```
small:
  Br join                                // x < 256 does not decide x < 128
large:
  Br join.large
join:
  %t = Phi.u32 [small: 1]
  %d = ICmp.ult.u32 %x, 128
  Br %d, print, done
join.large:
  Br done                                // x ≥ 256: the test is false
```

## When it applies

(1) `J` is not a loop header, has no back edge, and `R` satisfies the region rules of
`OPT-PROP-PATHFACTS` (single entry, forward chain, 4 blocks, 32 instructions, one exception region). (2)
The later condition `H` is `ICmp.<pred>.<T> x, literal`, a low-bit mask test of `x`, or the SSA condition of
`G` reused by a `Br` or `Select`. (3) For a predecessor `P` of `J`, the facade's decider answers `H` at the end
of `P` with true or false; the fact is the edge refinement of `G` and may come from a range (`x < 256` implies
`x < 512`, `x ≥ 256` refutes `x < 128`) or from known bits (a zero low nibble implies a zero low three bits).
An unsigned predicate is decided only when the known interval is non-negative. (4) A copy is made only for a
group of predecessors that share one decided outcome; undecided predecessors stay on the original. (5)
Values escaping `R` follow the single-exit merge rule of `RegionCloner`; phis of `J` keep in each version
exactly the predecessors that reach it. (6) At most 3 copies per region. (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155) scores the versioned
form strictly below the original under the active objective.

## How it is proven

`structural+range-facts`: the copy is reachable only from predecessors at whose end the fact holds, and nothing in a single-entry forward chain can change `x`; the fold is the deciding rule of [#163](https://github.com/Hawkynt/Polypiler/issues/163) under that fact. No solver, no timeout path.

## When it must not fire

- A third predecessor of the join that bypassed the first test: routing it into the copy applies `x ≥ 256` to a path that never established it.
- An interior block of the region with a side entry: the copy becomes reachable under facts it was not built for.
- `x < 256` taken as proof of `x < 128`: at `x = 200` the first is true and the second false.
- A signed fact used for an unsigned test: `x > −1` (signed) says nothing about `x <u 128` for `x = 0x80000000`.
- Reverse mask implication: a value that is not a multiple of 8 can still be a multiple of 4; only the implication from the wider mask to the narrower one holds.

## Targets

every target that executes branches. The gain is one test per decided path against the copied
instructions; the cost model decides, and `--opt size` accepts only versions that shrink the function. Mature
target compilers do not version blocks ahead of time, so the rewrite is not redundant there; in generated
source it prints as the second `if` moved into the arm that still needs it.

## Switching it off

`--disable-opt O0368`; active at `--opt balanced` and `speed`; at `--opt size` and `memory` only when the copies remove at least as many instructions as they add; off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#163](https://github.com/Hawkynt/Polypiler/issues/163) and `OPT-PROP-PATHFACTS`; enables [#164](https://github.com/Hawkynt/Polypiler/issues/164) and [#156](https://github.com/Hawkynt/Polypiler/issues/156) in the copies and further narrowing where a version carries a tighter range.
