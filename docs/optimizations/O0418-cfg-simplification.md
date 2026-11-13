# O0418 — Canonicalize the CFG by removing trivial phis and forwarding blocks and merging straight-line chains

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0418`.

## What it does

Frontends and earlier passes leave scaffolding behind: an increment block of its own, a join that only
forwards, a phi whose inputs are all the same value, the closing block of an inner `if` that does nothing
but jump to the outer one. Every later analysis stops at these artificial block boundaries, the structured
region tree of [#262](https://github.com/Hawkynt/Polypiler/issues/262) gets empty regions, and a machine-code backend emits a jump for each. No
planned pass removes them: [#164](https://github.com/Hawkynt/Polypiler/issues/164) deletes unreachable blocks and folds constant branches, nothing
more. This pass is the CFG canonicalization the other control-flow rewrites of `OPT-CODELAYOUT` rely on.
It delivers two upstream documents that are one rewrite on a typed IR: PB-Compiler's CFG simplification
(PB O0052) and its nested-condition combining (PB O0096), which upstream itself implements in the same
pass. PRD §4.2 (canonical IR between passes), §4.8.

## Example

### Before

```
body:
  %s1 = Add.i16[wrap] %s0, %i
  Br inc
inc:                                   // only predecessor: body
  %i1 = Add.i16[wrap] %i, 1
  Br cond

left:                 right:
  Br bridge             Br bridge
bridge:
  %v = Phi.i16 [left: 10], [right: 20]
  Br exit
exit:
  %r = Phi.i16 [bridge: %v]
```

### After

```
body:
  %s1 = Add.i16[wrap] %s0, %i
  %i1 = Add.i16[wrap] %i, 1
  Br cond

left:                 right:
  Br exit               Br exit
exit:
  %r = Phi.i16 [left: 10], [right: 20]
```

## When it applies

**R1 (trivial phi).** `%p = Phi [..: v], [..: v], [..: %p]` → every use of `%p` becomes `v`; a phi with only self-references is left alone. **R2 (forwarding block).** Block `F` with successor `S` is removed iff (1) `F` contains only phis and `Br S`, `F` is not the entry block and `F ≠ S`; (2) every phi of `F` is used only as the `F` input of phis of `S`; (3) no predecessor `P` of `F` already has an edge to `S`, unless every phi of `S` would receive the same value over both edges; (4) `F` is not the target of an unwind edge or a handler entry, and `F` and `S` lie in the same protected region ([#115](https://github.com/Hawkynt/Polypiler/issues/115)); (5) `S` is not the header of a loop that `F` is outside of when `F` has two or more predecessors (`F` is the preheader the loop passes rely on), and `F → S` is not a back edge when `F` has two or more predecessors (the loop keeps its single latch). Predecessors may end in `Br`, `CondBr` or `Switch`; each edge is retargeted, and the phi input `[F: %x]` of `S` becomes `[P: x_P]` per predecessor `P`, where `x_P` is `%x` itself or, if `%x` is a phi of `F`, that phi's input for `P`. **R3 (merge).** `A` ending in `Br B` absorbs `B` iff `B`'s only predecessor is `A`, `B ≠ A`, `B` has no phi left after R1, `B` is no unwind target and both are in the same protected region; `B`'s instructions are appended in order and labels `B` in successor phis become `A`. Nothing is evaluated earlier, later or more often than before.

## How it is proven

`structural`: R1 substitutes a value by an equal one, R2 removes a block that executes no instruction, R3 concatenates two blocks that always execute consecutively. No solver, no timeout path.

## When it must not fire

- `p: CondBr %c, f, s` · `f: Br s` · `s: %r = Phi [p: 1], [f: 2]`: redirecting `p → f` to `s` leaves one predecessor label for two different values; the result would be 1 or 2 regardless of `%c`.
- `spin: Br spin`, entered from `entry`: the block forwards to itself; removing it would turn an endless loop into whatever follows.
- A bridge phi `%v` that an `Add` in `exit` also uses: after removal `%v` has no definition.
- A loop preheader with two predecessors: removing it gives the header two outside predecessors, [#508](https://github.com/Hawkynt/Polypiler/issues/508) re-creates the preheader in the next iteration and phase `optimize` never reaches its fixed point.
- An empty block that is the entry of a handler: its incoming edges are unwind edges, which no terminator names.
- Merging `B` into `A` when only `B` is inside a protected region: a trap of `B`'s division would reach a different handler.

## Switching it off

`--disable-opt O0418`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

every target and every kind of backend. A source backend gets fewer `Block` leaves and, for nested conditionals, one condition chain that [#262](https://github.com/Hawkynt/Polypiler/issues/262) can report as a `ShortCircuit` region; a machine-code or assembly backend emits one jump less per removed block. The cost model is not consulted: the pass never adds an instruction, a block or an edge, so no objective of [#155](https://github.com/Hawkynt/Polypiler/issues/155) can get worse. Blocks are only removed or fused, never duplicated or reordered, so a function that had a structured region tree keeps one (contracting a block with a single successor cannot create a second loop entry).

## Interactions with other passes

runs in phase `optimize` after [#164](https://github.com/Hawkynt/Polypiler/issues/164) and the loop passes and before [#156](https://github.com/Hawkynt/Polypiler/issues/156); enables `OPT-LAYOUT-JUMPTHREAD`, `OPT-LAYOUT-IFCONVERT` and `OPT-LAYOUT-IFCHAIN` (their patterns assume no forwarding blocks) and widens the blocks [#158](https://github.com/Hawkynt/Polypiler/issues/158) and [#504](https://github.com/Hawkynt/Polypiler/issues/504) work on. Reports `PreservedAnalyses.None` for a changed function's CFG-based analyses and calls `InvalidateCfg`.
