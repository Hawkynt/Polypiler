# O0422 — Keep one copy of an instruction suffix shared by sibling blocks

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0422`.

## What it does

The arms of a conditional or a multi-way branch often end the same way: print the value, call the
cleanup, store the result. Each arm carries its own copy. On a small machine that is the code-size
difference between fitting and not fitting; in generated source it is the same three statements repeated
in every `case`. PB-Compiler plans the merge as PB O0095 (the congruence test it already uses for whole
procedures, applied backwards from the end of sibling arms). On SSA the congruence is exact — the same
operation on the same values, with differing inputs joined by a phi — and so is the question of when the
merge costs a jump. PRD §4.8 (`--opt size`), §21.3.

## Example

### Before

```
arm1:
  %v1 = Add.i16[wrap] %x, 10
  Call.void @print(%v1)
  Call.void @cleanup()
  Br done
arm2:
  %v2 = Add.i16[wrap] %x, 20
  Call.void @print(%v2)
  Call.void @cleanup()
  Br done
```

### After

```
arm1:
  %v1 = Add.i16[wrap] %x, 10
  Br arm1.tail
arm2:
  %v2 = Add.i16[wrap] %x, 20
  Br arm1.tail
arm1.tail:
  %v = Phi.i16 [arm1: %v1], [arm2: %v2]
  Call.void @print(%v)
  Call.void @cleanup()
  Br done
```

## When it applies

(1) *Congruent terminators*: same opcode and the same successor blocks in the same positions; a `CondBr` or `Switch` additionally needs a congruent condition operand. (2) *Congruent suffix*: at each position the instructions have the same opcode, type and attributes (overflow, shift, access and rounding contracts included) and no phis; each operand pair is the same value, or the two results of an earlier suffix position, or two different values — which become one phi of the tail, an input per group block. (3) A value defined in the suffix is used outside it only as that block's input of a phi in a successor; those inputs become the tail's value. (4) All group blocks lie in the same protected region and none is an unwind target ([#115](https://github.com/Hawkynt/Polypiler/issues/115)). (5) *Complete group* — the terminator is `Br J`, the group is exactly the predecessor set of `J`: merged whenever the suffix holds at least one instruction; the tail then has `J` as only successor and is fused with it by `OPT-LAYOUT-SIMPLIFYCFG`, so no path gains a transfer. (6) *Partial group*: merged only if the target profile does not declare `emits: source` and [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers `merged` (size − (n − 1) × suffix size + `Of(Br).Size`, latency + `Of(Br).Latency`) over `kept`. The longest congruent suffix is taken; groups are formed per successor set in reverse post-order of their first block, so the result is deterministic. The merged instruction keeps the source location of the first group block's copy.

## How it is proven

`structural`: each path executes the same instructions on the same values in the same order as before. No solver, no timeout path.

## When it must not fire

- `arm1: … Br done1` and `arm2: … Br done2` with equal suffixes: after a common tail there is one terminator, and one of the arms would continue at the wrong block.
- Suffixes `Call @print(%v1)` / `Call @log(%v2)`: same shape, different callee.
- `Add.i16[wrap] %a, %b` in one arm and `Add.i16[trap Overflow] %a, %b` in the other: the same opcode with different contracts; merged, one arm would trap or stop trapping.
- A suffix value used by an instruction that only one arm dominates (not through a phi): after the merge that use sees the value of the other arm as well.
- One arm inside a protected region, the other outside: the merged call's trap reaches the handler on one path only.
- Three of five `case` arms sharing a tail in a source target: the tail becomes a merge node inside the switch, which [#262](https://github.com/Hawkynt/Polypiler/issues/262) can only print with `goto` or a dispatch loop.

## Switching it off

`--disable-opt O0422`; complete groups at `--opt balanced`, `speed`, `size` and `memory`; partial groups at `--opt size` and `memory`, and at `balanced` when the cost model prefers them; never at `--opt speed`; off at `--opt none`.

## Targets

complete groups pay on every target: a source backend prints the shared statements once after the `if` or `switch`, a machine-code backend saves the bytes without a new jump. Partial groups are a size trade for a taken jump and are restricted to assembly and machine-code backends — 6502, Z80, 8086 and other targets with memory budgets — because in a structured source target they would turn a readable `switch` into `goto`s (rule 6). The cost model decides each partial merge.

## Interactions with other passes

runs in phase `optimize` after [#158](https://github.com/Hawkynt/Polypiler/issues/158) (canonical operand order makes more suffixes congruent) and before `OPT-LAYOUT-SIMPLIFYCFG`'s last run in the iteration; `OPT-PGO-TAILMERGE` overrides the cost decision per temperature when a profile is given. Invalidates the CFG.
