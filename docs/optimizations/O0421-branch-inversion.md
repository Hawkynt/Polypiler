# O0421 — Invert a conditional branch so that the following block falls through

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0421`.

## What it does

`if c then A else B` laid out as branch, `A`, `B` needs the branch to skip `A` when `c` is *false*; an
emitter that always branches on the true outcome writes a branch to `A` followed by a jump over it. More
generally, whenever the *true* successor of a `CondBr` is the next block of the layout, the condition has
to be inverted for the block to fall through, and when neither successor is next it matters which of the
two gets the conditional branch. PB-Compiler plans this per-site choice as PB O0094. On Polypiler's
layout contract it is a flag in the terminator plan, and its one real trap is floating point: the
inverse of "less than" is not "greater or equal". PRD §4.8, ADR 0005 (NaN).

## Example

### Before

```
order: entry, then, endif
entry:  CondBr %z, then, endif     plan: BranchJump(%z, negated=false, taken=then, other=endif)
then:   …                          plan: FallThrough(endif)
```

### After

```
order: entry, then, endif
entry:  CondBr %z, then, endif     plan: BranchFall(%z, negated=true, taken=endif, next=then)
then:   …                          plan: FallThrough(endif)
```

## When it applies

(1) *Forced case*: in `BranchJump(c, n, T, F)` the block `T` is next in `Order` → `BranchFall(c, ¬n, F, T)`. (2) *Free case*: neither `T` nor `F` is next; if the edge to `F` is `Likely` in `BranchProbability` (`OPT-LAYOUT-PLACEMENT`) → `BranchJump(c, ¬n, F, T)`; otherwise unchanged. (3) A plan with `Negated = true` means "transfer when `c` is false". An emitter must realize exactly that: for a materialized `bool` by testing for zero; for a fused integer comparison by the complementary predicate (`eq`/`ne`, `ult`/`uge`, `ule`/`ugt`, `slt`/`sge`, `sle`/`sgt`); for a fused floating-point comparison ([#116](https://github.com/Hawkynt/Polypiler/issues/116)) by the complement *including the unordered case* (`olt` ↔ `uge`, `oeq` ↔ `une`, …). An emitter that has no single branch for the complement materializes the `bool` and tests it. (4) The IR is unchanged.

## How it is proven

`structural`: the plan transfers to `F` exactly when `c` is false and continues at `T` otherwise, which is the meaning of `CondBr c, T, F`. No solver, no timeout path.

## When it must not fire

- `FCmp.olt.f64 %x, %y` inverted as "greater or equal": with `x = NaN` the comparison is false, so control must go to `F`; "ordered ≥" is false as well and the code falls into `T`.
- Exchanging the targets without negating, or negating without exchanging: every input takes the wrong arm.
- Inverting in the IR and in the plan: the two negations cancel in the emitter.
- In the free case, giving the conditional branch to the unlikely successor: the common path then pays a not-taken branch *and* a jump.

## Switching it off

`--disable-opt O0421`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

assembly and machine-code backends only — the x86 NASM backend ([#931](https://github.com/Hawkynt/Polypiler/issues/931)) and the 6502 selector ([#1025](https://github.com/Hawkynt/Polypiler/issues/1025)) here. Source backends print conditions from the region tree, where [#262](https://github.com/Hawkynt/Polypiler/issues/262)'s `IfThen.Negated` already covers the analogous choice; a profile with `emits: source` has no layout and this pass does not run. The cost model is not asked: the forced case removes a jump, the free case changes no instruction count.

## Interactions with other passes

runs in phase `legalize` after `OPT-LAYOUT-JUMPNEXT` on the final order, and once more after it (the forced case creates the fall-through that O0430 would otherwise have to find). Uses `BranchProbability` of `OPT-LAYOUT-PLACEMENT` when that analysis is available for the function; with a profile, `OPT-PGO-CHAIN` supplies measured edge classes through the same query.
