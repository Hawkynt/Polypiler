# O0003 — Sparse conditional constant propagation

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#164](https://github.com/Hawkynt/Polypiler/issues/164). Once implemented it is switched off with `--disable-opt O0003`.

## What it does

Constants hide behind control flow: a `debug` flag that is `false`, a block size chosen by a switch on a
constant mode, a phi whose only executable input is a literal. Plain folding cannot see through branches;
SCCP propagates constants and reachability together and removes the code that can never run. PRD §4.8 (SCCP),
§8 P1.

## Example

### Before

```
entry:
  %c = ICmp.eq.u32 4, 4
  Br %c, then, else
then:
  Br join
else:
  Call.void @log(7)            // has External.Call effect, but is unreachable
  Br join
join:
  %p = Phi.u32 [then: 1], [else: 2]
  Ret.u32 %p
```

### After

```
entry:
  Ret.u32 1
```

## When it applies

A value is replaced by a literal only when its lattice cell is `Constant` at the fixed point; a block is
deleted only when no executable edge reaches it. Cells start at `Top`, function parameters, loads, calls and
every instruction `TryFold` refuses are `Bottom`. A phi meets only the operands arriving over executable
edges. A conditional branch on `Constant` marks one successor edge executable; on `Bottom` both. The entry
block is always executable. An instruction with an effect ([#114](https://github.com/Hawkynt/Polypiler/issues/114)) in an executable block is never
removed even if its result cell is `Constant`. An undefined value is `Bottom`, never "any convenient constant".

## How it is proven

`structural+interpreter`: the lattice is finite-height and transfer functions are monotone (cells only descend), so the fixed point is the standard conservative solution; each constant comes from the O0001 evaluator. No solver, no timeout path.

## When it must not fire

- A reachable block containing `Call.void @log(7)` whose result is unused: the call must stay — only *unreachable* effects may disappear.
- `%p = Phi [a: 1], [b: 2]` with both edges executable is `Bottom`, not 1.
- `%d = SDiv.i32[checked] 1, 0` in an executable block: the cell is `Bottom` and the trap stays.
- A loop `while (true)` without effects: folding the back-edge condition must not delete the loop; non-termination is observable (PRD §17.1).
- An uninitialized local read on one path: treating it as equal to the other path's constant invents undefined behaviour the source profile may not have.

## Switching it off

`--disable-opt O0003`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#154](https://github.com/Hawkynt/Polypiler/issues/154) and inlining; enables [#156](https://github.com/Hawkynt/Polypiler/issues/156), branch-free recognition patterns and tighter range facts. Preserves no CFG-based analysis when it deletes a block.
