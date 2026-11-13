# O0444 — Order blocks by chaining along the most frequently executed edge

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0444`.

## What it does

`OPT-LAYOUT-PLACEMENT` can tell an error arm from a loop body, but for an ordinary `if` whose
condition is data it has no signal and keeps the arms in source order. A profile knows: this arm ran 990
times, that one 10. PB-Compiler plans the classic use of that knowledge (PB O0364): start at the entry,
follow the hottest outgoing edge, and when the chain ends start again from the hottest block not yet
placed — the common path becomes one straight run of bytes and the rare edges become the taken branches.
PB-Compiler's profile-guided code layout pass already implements the adjacency idea on its IR, which this
issue converts. PRD §4.8.

## Example

### Before

```
order (static, both edges Even): entry, a, join, b
counts: entry → a 10, entry → b 990, a → join 10, b → join 990
```

### After

```
order: entry, b, join, a
fall-through weight: 990 + 990 = 1980 of 2000   (before: 10 + 10 = 20)
```

## When it applies

Any order with the entry first is a legal `BlockLayout` order; the pass only chooses one. (1) A function qualifies iff every reachable block has a count and every edge has a weight: `EdgeCount` if known; otherwise the count of the source block if the edge is its only outgoing edge, or of the target block if the edge is its only incoming edge; otherwise the function does not qualify. (2) Place the entry. From the last placed block `B`, take the unplaced successor with the largest weight above 0, ties by canonical successor order; repeat until none exists. (3) Start the next chain at the unplaced block with the largest count above 0, ties by lowest reverse-post-order number, and continue as in (2). (4) Blocks with count 0 follow in reverse post-order; they are the cold tail `OPT-LAYOUT-HOTCOLD` may split off. (5) Plans are rebuilt by `BlockLayout.WithOrder`. (6) For a qualifying function the layout phase's edge classes are measured: of the two edges of a `CondBr`, the heavier is `Likely`, the lighter `Unlikely`, equal weights `Even`; a block is cold iff its count is 0. **Without a profile** (`--profile-use` absent, or the function stale or not fully measured) the pass does nothing and writes no report entry; the static layout of `OPT-LAYOUT-PLACEMENT` stands. PRD §4.8: no translation needs a profile. **Determinism** (PRD §13.1): weights are the integer counts of `IProfileData`, sums saturate at 2^64 − 1, every choice among equal weights is made by reverse-post-order number, and no unordered collection is iterated — the same input, compiler, profile and seed give the same image.

## How it is proven

`structural`: a block order with the entry first, executed by the terminator plans, is behaviourally the function. No solver, no timeout path.

## When it must not fire

- Treating an edge without a count as weight 0: after inlining, half the edges are new; the pass would declare hot code cold and move it behind everything else. Unknown is not zero.
- Chaining into a block that is already placed: it cannot be placed twice; the chain ends there.
- Starting at the hottest block instead of the entry: the function's symbol must label its first byte.
- Choosing between two successors of equal weight by dictionary order: two builds with the same profile differ.
- Applying a profile whose fingerprint does not match: the counts describe other blocks; `OPT-PGO-COLLECT` already withholds them.

## Switching it off

`--disable-opt O0444`; active at `--opt balanced`, `speed`, `size` and `memory` when a profile is attached; off at `--opt none`.

## Targets

assembly and machine-code backends only — the x86 NASM backend ([#931](https://github.com/Hawkynt/Polypiler/issues/931)) and the 6502 selector ([#1025](https://github.com/Hawkynt/Polypiler/issues/1025)) in this issue's tests. A profile that declares `emits: source` has no `BlockLayout`, so the pass cannot run there and generated source is unaffected by a profile's block order. A profile is collected on a source target (`OPT-PGO-COLLECT`) and applied here to a machine target of the same module, because the counts belong to the IR. Real-mode x86 through [#1046](https://github.com/Hawkynt/Polypiler/issues/1046) is excluded.

## Interactions with other passes

runs in phase `legalize` after `OPT-LAYOUT-PLACEMENT` (whose order it replaces for qualifying functions) and before `OPT-LAYOUT-HOTCOLD`, `OPT-LAYOUT-JUMPNEXT` and `OPT-LAYOUT-INVERT`; `OPT-PGO-FALLTHROUGH` runs directly after it and may improve the order.
