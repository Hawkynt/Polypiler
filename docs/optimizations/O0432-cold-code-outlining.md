# O0432 — Extract terminal cold regions of a function into a cold helper

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0432`.

## What it does

A parser that starts with `if input is empty: print usage, print hint, return` carries that
diagnostic in its body for every call. The body is larger than the work it does, which is what keeps
[#159](https://github.com/Hawkynt/Polypiler/issues/159) from inlining it into its hot caller. `OPT-LAYOUT-HOTCOLD` moves such blocks to another
address; it does not make the function smaller. PB-Compiler extracts terminal cold arms into a separate
helper that is never inlined back (PB O0275, implemented there as `ColdCodeOutlining`), restricted to the
shape that is provable without live-out analysis: a single-entry region that never rejoins the hot path.
PRD §4.8.

## Example

### Before

```
func @parse(%s: ptr<u8>, %len: u32) -> i32 {
entry:
  %e = ICmp.eq.u32 %len, 0
  CondBr %e, empty, work
empty:
  Call.void @print(@msg.empty)
  Call.void @print(@msg.usage)
  Ret.i32 -1
work:
  …
}
```

### After

```
func @parse(%s: ptr<u8>, %len: u32) -> i32 {
entry:
  %e = ICmp.eq.u32 %len, 0
  CondBr %e, empty, work
empty:
  %r = Call.i32 @parse.cold.0()
  Ret.i32 %r
work:
  …
}
func @parse.cold.0() -> i32 [noinline] {
entry:
  Call.void @print(@msg.empty)
  Call.void @print(@msg.usage)
  Ret.i32 -1
}
```

## When it applies

(1) The region head `R` is the target of an edge that `BranchProbability` (`OPT-LAYOUT-PLACEMENT`) classes `Unlikely`, and that edge is `R`'s only incoming edge. (2) The region is the set of blocks dominated by `R`; every block in it ends in `Br`/`CondBr`/`Switch` to region blocks, in `Ret` or in `Unreachable` — no edge leaves the region. (3) Values defined outside and used inside are the live-ins: at most 4, each passed by value as a parameter in order of first use; values defined inside have no user outside by construction. (4) The region holds at least 3 instructions (the replacement is a call and a return). (5) No region block is an unwind target and the region lies in no protected region; the function has no handler armed at `R` ([#115](https://github.com/Hawkynt/Polypiler/issues/115)). (6) The region contains no instruction that reads the function's own frame or stack trace and no stack allocation whose address is live-in to it by reference from an enclosing scope that the helper's return would not end ([#80](https://github.com/Hawkynt/Polypiler/issues/80): the same refusals [#159](https://github.com/Hawkynt/Polypiler/issues/159) reports as `callee reads its frame` and `stack trace observable`). (7) The helper is module-private, carries `noinline`, and returns the function's return type; a region whose paths all end in `Unreachable` yields a helper that does not return, called before an `Unreachable`. (8) The target profile does not declare `emits: source` and the objective is `speed`.

## How it is proven

`structural+effect-facts`: the helper executes the region's instructions unchanged on the same values, and control never returned from the region to the function. No solver, no timeout path.

## When it must not fire

- An arm that logs and then *continues* with the normal path: it has a successor outside the region; extracted naively, the function would return where it used to go on.
- A region that sets a variable read after the join: a live-out, which a by-value call cannot deliver.
- A language in which the stack trace of a raised error is observable: the outlined `raise` shows one more frame.
- Letting the next inlining pass inline the helper again: the function is as large as before and the pass repeats forever.
- Five live-ins: the argument transfers on the call cost more than the region saved; upstream caps the boundary at four.
- A two-instruction region: the replacement (call and return) is not smaller than what it replaces.

## Switching it off

`--disable-opt O0432`; active at `--opt speed` only; off at `balanced`, `size`, `memory` and `none`.

## Targets

assembly and machine-code backends only: the pass does not run for a profile that declares `emits: source`, where it would add a helper function nobody wrote and where the target compiler outlines cold paths itself. The gate is the objective of [#155](https://github.com/Hawkynt/Polypiler/issues/155) — a call and a return are added, so only `speed` accepts it — together with the upstream floors of rule 3 and 4; the gain it buys, a callee small enough for [#159](https://github.com/Hawkynt/Polypiler/issues/159), is decided by that pass's own cost query afterwards.

## Interactions with other passes

module pass in phase `optimize` with `Order` directly before [#159](https://github.com/Hawkynt/Polypiler/issues/159), so that the shrunken function is what the inliner prices; the `noinline` mark keeps the inliner from undoing it. `OPT-LAYOUT-HOTCOLD` later places the helper's call block in the cold region. Adds functions; invalidates the call graph and the changed functions' CFGs.
