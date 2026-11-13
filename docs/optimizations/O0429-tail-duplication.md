# O0429 — Duplicate a small join block into the predecessors that jump to it

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0429`.

## What it does

Two arms of a conditional that meet in a small join pay a taken jump to get there, and everything in
the join is computed on a phi although each arm knows the concrete value. PB-Compiler plans to form traces
along likely paths and to duplicate the small joins that interrupt them into their predecessors (PB
O0106), so that each arm becomes straight-line code. On SSA the duplication is mechanical — each copy
takes its own predecessor's phi inputs — and its price is code size, which the cost model can weigh.
PRD §4.8.

## Example

### Before

```
pos:
  %s1 = Add.i16[wrap] %s, %a
  Br join
neg:
  %s2 = Sub.i16[wrap] %s, %a
  Br join
join:
  %s3 = Phi.i16 [pos: %s1], [neg: %s2]
  %s4 = And.i16 %s3, 32767
  Br latch
latch:
  %sn = Phi.i16 [join: %s4], [skip: %s]
```

### After

```
pos:
  %s1 = Add.i16[wrap] %s, %a
  %s4.pos = And.i16 %s1, 32767
  Br latch
neg:
  %s2 = Sub.i16[wrap] %s, %a
  %s4.neg = And.i16 %s2, 32767
  Br latch
latch:
  %sn = Phi.i16 [pos: %s4.pos], [neg: %s4.neg], [skip: %s]
```

## When it applies

(1) `J` is not the entry block, not a loop header and not an unwind target; `J` and the duplicating predecessors lie in the same protected region ([#115](https://github.com/Hawkynt/Polypiler/issues/115)). (2) A predecessor `P` duplicates iff it ends in `Br J`, `BranchProbability.IsCold(P)` is false and the edge `P → J` is not `Unlikely` (`OPT-LAYOUT-PLACEMENT`); at least two predecessors must qualify. (3) Every value defined in `J` is used only inside `J` or as the `J` input of a phi in a successor of `J`. (4) In the copy for `P` each phi of `J` is replaced by its input for `P`, the other instructions are cloned in order with fresh names `<name>.<P>`, and the terminator is cloned; each successor phi gets an input per copy. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155), always with the balanced weights, prefers `duplicated` (latency 0, size `n × S`) over `kept` (latency `Of(Br).Latency`, size `S + n × Of(Br).Size`), where `S` is the summed size of `J`'s instructions including its terminator and `n` the number of duplicating predecessors; `kept` is listed first, so a tie keeps the join. (6) The target profile does not declare `emits: source`. (7) Every block that received a copy is entered, with the label of `J`, in the function's `DuplicatedInto` table; `OPT-LAYOUT-TAILMERGE` forms no group from blocks that are all entered for the same join (`suffix was duplicated`), so the two passes cannot undo each other inside the phase's fixed-point loop.

## How it is proven

`structural`: each path executes the instructions of `J` once, after its own predecessor, on the values the phis selected. No solver, no timeout path.

## When it must not fire

- `J` is a loop header: copying it into the preheader and the latch rotates the loop; done for only one of them it leaves a loop with two entries.
- A value of `J` used two blocks further down, not through a phi: after duplication two definitions reach that use and neither dominates it.
- Copying the phi itself instead of substituting the predecessor's input: the copy in `pos` would have a phi with a single, wrong-labelled input.
- Duplicating into a cold predecessor: the error path grows and nothing that runs gets faster.
- A join inside a protected region duplicated into a predecessor outside it: a trap in the copy reaches a different handler.

## Switching it off

`--disable-opt O0429`; active at `--opt balanced` and `speed`, in both cases under the balanced weights; off at `size`, `memory` and `none`.

## Targets

assembly and machine-code backends only. The pass runs only for a target profile that does not declare `emits: source`: in generated source the same statements would simply appear twice, a mature target compiler makes this trade itself, and the readable form is the join. Real-mode x86 through [#1046](https://github.com/Hawkynt/Polypiler/issues/1046) is excluded as well, because PB-Compiler's own pipeline owns that code after selection.

## Interactions with other passes

runs in phase `optimize` after the loop passes and before the iteration's last `OPT-LAYOUT-SIMPLIFYCFG`, [#154](https://github.com/Hawkynt/Polypiler/issues/154), [#158](https://github.com/Hawkynt/Polypiler/issues/158) and [#156](https://github.com/Hawkynt/Polypiler/issues/156), which fold the copies; `OPT-LAYOUT-TAILMERGE` does not merge the copies back (rule 7); for partial groups the two cost tests are complementary anyway — merging is preferred exactly when `(n − 1) × S'` exceeds `4 × Of(Br).Latency + Of(Br).Size` for the suffix size `S'` without its terminator, duplication exactly when it is below. With a profile, `OPT-PGO-TAILMERGE` arbitrates between all merging and duplicating passes. Invalidates the CFG.
