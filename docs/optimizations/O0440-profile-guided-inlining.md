# O0440 — Give each direct call site an inlining budget from its measured execution count

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0440`.

## What it does

[#159](https://github.com/Hawkynt/Polypiler/issues/159) prices a call by the callee's body and the target's call cost. It cannot know that one
call runs a million times in a loop and another once at start-up, so it inlines the start-up helper that
happens to be small and declines the hot one that is slightly too large. PB-Compiler makes the budget a
property of the call site: an edge count earns instructions of budget, a measured zero earns none (PB
O0269, implemented in its `Inliner`). The downstream compiler of a source target cannot make this
decision — it never sees the profile — so it has to be made here. PRD §4.8.

## Example

### Before

```
hot:                                    // BlockCount(hot) = 200
  %p = Call.u32 @pixel(%x, %y)          // @pixel: 100 instructions, 2 parameters
setup:                                  // BlockCount(setup) = 1
  %q = Call.u32 @pixel(%a, %b)
```

### After

```
hot:
  …the 100 instructions of @pixel, parameters bound to %x and %y…
setup:
  %q = Call.u32 @pixel(%a, %b)          // withheld: budget 0 < 100
```

## When it applies

Legality is entirely [#159](https://github.com/Hawkynt/Polypiler/issues/159)'s; this issue adds a profitability answer. For a direct call in block `b` with `count = IProfileData.BlockCount(b)`: no count → no answer. Otherwise `payoff = 2 + argumentCount`; `weighted = count × payoff` with saturation at 2^64 − 1; `budget = min(hardCap, weighted / growthPenalty)` with integer division; the advisor answers *inline* iff the callee's current instruction count ≤ `budget`, else *withhold*. `growthPenalty`/`hardCap` are 8/512 at `--opt balanced` and 4/1024 at `--opt speed`. At `--opt size` and `memory` the advisor only withholds (it answers *withhold* for a count of 0 and nothing otherwise). An *inline* answer is still subject to every precondition and growth limit of O0006; a *withhold* answer is not applied to a module-private callee with a single call site. **Without a profile** (`--profile-use` absent, or the function stale or unmeasured) the pass does nothing and writes no report entry: every count it asks `IProfileData` for is null, and null never satisfies a threshold. PRD §4.8: no translation needs a profile. **Determinism** (PRD §13.1): the decision is integer arithmetic on counts read through `IProfileData`, candidates are visited in module and block order, ties are broken by block number — the same input, compiler, profile and seed give the same output, and the profile's digest is in the manifest's command line (`OPT-PGO-COLLECT`).

## How it is proven

`structural`, inherited from O0006: the advisor selects among inlinings that pass is allowed to perform; it performs none itself. No solver, no timeout path.

## When it must not fire

- Letting a count of 2^64 − 1 multiply without saturation: the product wraps to a small number and the hottest call is withheld; or, with the cap applied before the division, the cap is lost.
- A hot call to a `noinline` callee, a recursive callee or one that reads its frame: inlining it changes behaviour however hot it is.
- Treating "no count" as "count 0": after a stale profile every call would be withheld and the build would be slower than without any profile.
- Reading the count of the *callee's entry* instead of the call's block: both sites of the example would be judged alike.

## Switching it off

`--disable-opt O0440`; active at `--opt balanced` and `speed` (both directions) and at `size` and `memory` (withholding only); off at `--opt none`.

## Targets

every target, source and machine code alike: the decision needs the profile, which no downstream compiler has, and inlining is already a rewrite [#159](https://github.com/Hawkynt/Polypiler/issues/159) makes for all of them under [#155](https://github.com/Hawkynt/Polypiler/issues/155). The budget counts IR instructions, as upstream's does, so it is target-neutral; where a source profile's cost model already refuses inlining for readability, the advisor's *inline* answer is not consulted — the cost model stays the outer gate.

## Interactions with other passes

consulted inside [#159](https://github.com/Hawkynt/Polypiler/issues/159)'s module pass (first in phase `optimize`, after `prof.anchor` and the attachment of `OPT-PGO-COLLECT`); runs after `OPT-PGO-ICP` by pass order so that promoted direct calls are candidates. Inlined blocks keep the callee's keys through `ProfileKeys.Inherit`.
