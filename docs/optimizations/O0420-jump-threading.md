# O0420 — Retarget branches that reach an unconditional branch to its final destination

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0420`.

## What it does

A branch to a block that only branches again costs a second taken transfer and hides the real
destination from every pattern that looks at successors. `OPT-LAYOUT-SIMPLIFYCFG` removes such a block
only when *all* edges into it can be redirected; when one predecessor needs the block — it reaches the
destination a second way with a different phi value, or the block is an unwind target — every other
predecessor keeps the detour. PB-Compiler threads each jump individually (PB O0093) and then deletes the
hop nothing reaches any more. This issue does the same on CFG edges, where the phi inputs make the
legality explicit. PRD §4.8.

## Example

### Before

```
p1:
  CondBr %c1, hop, join
p2:
  Br hop
hop:                                   // must stay: p1 reaches join twice with different values
  Br join
join:
  %r = Phi.i16 [p1: 1], [hop: 2]
```

### After

```
p1:
  CondBr %c1, hop, join
p2:
  Br join
hop:
  Br join
join:
  %r = Phi.i16 [p1: 1], [hop: 2], [p2: 2]
```

## When it applies

Edge `P → F` is retargeted to `S` iff (1) `F` contains only phis and `Br S`, and `F ≠ S`; (2) each phi of `F` is used only as the `F` input of phis of `S`; the new input `[P: x]` of a phi of `S` is the value that flowed over `P → F → S` — the `F` input itself, or, if that is a phi of `F`, its input for `P`; (3) `P` has no other edge to `S`, or every phi of `S` receives the same value over both; the two edges then share one input; (4) `F` is not a loop header, `F → S` is not a back edge, and `S` is not the header of a loop that `F` is outside of — otherwise the loop would gain a second entry or lose its preheader or its single latch; (5) `P`, `F` and `S` lie in the same protected region and `P → F` is an ordinary terminator edge, not an unwind edge ([#115](https://github.com/Hawkynt/Polypiler/issues/115)); (6) following the chain from `F` visits no block twice and at most 8 blocks. A terminator whose successors have all become one block `X`, with one phi input per phi of `X`, is replaced by `Br X`; its condition or selector stays for [#156](https://github.com/Hawkynt/Polypiler/issues/156). A forwarding block is deleted when it has no predecessor left, is not the entry block and is not an unwind target.

## How it is proven

`structural`: the bypassed block executes no instruction, and the phi inputs added are the values that flowed over the old path. No solver, no timeout path.

## When it must not fire

- `a: Br b` · `b: Br a`, entered from `entry`: following the chain never ends. The pass must detect the cycle and leave the endless loop as it is.
- `p: CondBr %c, f, s` with `s: Phi [p: 1], [f: 2]`: threading `p → f` gives `p` two different inputs for one label.
- A header `h: Br body` with back edge `body → h` and two outside predecessors, one of which cannot be threaded: threading the other gives the loop the entries `h` and `body`; the graph becomes irreducible and a target without `goto` gets PLY0580 from [#262](https://github.com/Hawkynt/Polypiler/issues/262) or a dispatch loop it did not need.
- A forwarding block reached only by an unwind edge after threading: it has no predecessor in the terminators but is still live.
- Threading from inside a protected region to a destination outside while the hop was the region's exit block: allowed only when the hop itself has no instructions and lies in the same region as `P` — otherwise a region exit action would be skipped.

## Switching it off

`--disable-opt O0420`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

every target. The rewrite never adds an instruction or a block and removes a taken transfer on each threaded path, so the cost model of [#155](https://github.com/Hawkynt/Polypiler/issues/155) is not consulted. For source backends it only matters through the shape: both arms of a conditional end at the same block more often, which [#262](https://github.com/Hawkynt/Polypiler/issues/262) prints as `else if` chains instead of nested blocks. Rule (4) keeps every reducible function reducible, so no structured target loses its region tree.

## Interactions with other passes

runs in phase `optimize` directly after `OPT-LAYOUT-SIMPLIFYCFG`; enables another round of it (a block may now have a single predecessor) and [#156](https://github.com/Hawkynt/Polypiler/issues/156) (conditions of folded branches). Reports the CFG as changed and calls `InvalidateCfg`.
