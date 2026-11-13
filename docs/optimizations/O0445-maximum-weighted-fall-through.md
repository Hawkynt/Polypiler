# O0445 — Choose the block order with the larger execution-weighted fall-through

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0445`.

## What it does

`OPT-PGO-CHAIN` is a heuristic: it follows the hottest edge out of each block and never looks at the
edges *into* a block. In a loop that costs dearly — the chain enters the loop over an edge taken once and
thereby gives up the back edge taken a thousand times. PB-Compiler states the layout problem as an
objective instead (PB O0365): maximize the execution-weighted number of fall-through edges, and report
that number, so that a layout can be judged rather than assumed. The exact problem is NP-hard; this issue
adds the objective as a reported metric, a second construction that joins chains from the heaviest edge
down, and the rule that the order with the larger metric wins. PRD §4.8.

## Example

### Before

```
counts: entry → H 1, H → B1 600, H → B2 400, B1 → L 600, B2 → L 400, L → H 999, L → exit 1     (total 3001)
order of O0444: entry, H, B1, L, exit, B2          fall-through 1 + 600 + 600 + 1 = 1202
```

### After

```
order: entry, B2, L, H, B1, exit                   fall-through 400 + 999 + 600 = 1999
```

## When it applies

Any order with the entry first is legal; the pass chooses between two. (1) The function qualifies under rule 1 of `OPT-PGO-CHAIN` (`LayoutWeights`). (2) Every block starts as its own chain. Edges with weight above 0 are visited in descending weight, ties by the reverse-post-order numbers of source, then target. An edge `X → Y` joins two chains iff `X` is the last block of its chain, `Y` is the first block of a *different* chain and `Y` is not the entry. (3) The chains are concatenated: the entry's chain first; then, repeatedly, the remaining chain with the largest total weight of edges from already placed blocks into it, ties by the lowest reverse-post-order number of its head; chains made only of count-0 blocks last. (4) `FallThroughWeight` is computed for the merged order and for the current order; the merged order replaces the current one only if its value is strictly larger. (5) Plans are rebuilt by `BlockLayout.WithOrder`. **Without a profile** the pass does nothing and writes no report entry. **Determinism** (PRD §13.1): weights are the integer counts of `IProfileData`, sums saturate at 2^64 − 1, every choice among equal weights is made by reverse-post-order number, and no unordered collection is iterated — the same input, compiler, profile and seed give the same image.

## How it is proven

`structural`: both candidate orders are legal layouts of the same function. No solver, no timeout path.

## When it must not fire

- Counting an edge as a fall-through because its blocks are in the same chain: only *adjacent* blocks fall through.
- Joining a chain in front of the entry because an edge into the entry (a loop back to it) is heavy: the entry must stay first.
- Replacing the order on equal metric: the layout would flip between builds whose only difference is the visiting order.
- Counting the fall-through of a `CondBr` twice when both successors are placed next to it in different candidate orders: one successor at most can follow.

## Switching it off

`--disable-opt O0445`; active at `--opt balanced`, `speed`, `size` and `memory` when a profile is attached; off at `--opt none`.

## Targets

assembly and machine-code backends only, for the reasons given in `OPT-PGO-CHAIN`: a source backend has no block order, and the pass cannot run for a profile that declares `emits: source`. No cost query: the metric is the decision, and it is the quantity the target pays for — on a 6502 or 8086 every fall-through is a taken transfer and a prefetch refill avoided.

## Interactions with other passes

runs in phase `legalize` directly after `OPT-PGO-CHAIN` and before `OPT-LAYOUT-HOTCOLD`, `OPT-LAYOUT-JUMPNEXT` and `OPT-LAYOUT-INVERT`. With O0444 disabled it compares against the static order of `OPT-LAYOUT-PLACEMENT`.
