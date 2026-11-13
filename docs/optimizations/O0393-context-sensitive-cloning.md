# O0393 — Clone a function for a caller whose calls agree on constant arguments that the other callers do not share

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0393`.

## What it does

Interprocedural facts are joined over all callers, so one imprecise caller destroys the precision for
everybody. `OPT-IPA-CLONECONST` helps when most *call sites* agree; it does not help when one caller — the
inner loop — always passes the same constants and many other callers pass anything. Cloning by caller
identity gives that caller its own copy, in which its constants are literals and its ranges are no longer
joined with the others'. PRD §4.6 (specialization budget), §4.8.

## Example

### Before

```
func @Draw(%x: i32, %y: i32, %mode: i32) -> void { ...30 instructions... }
func @hot() -> void {                       // two calls, both with y = 100 and mode = 1
  Call.void @Draw(%i, 100, 1)
  Call.void @Draw(%j, 100, 1)
}
func @cold(%ux: i32, %uy: i32, %m: i32) -> void {   // three calls with arbitrary arguments
  Call.void @Draw(%ux, %uy, %m)
  ...
}
```

### After

```
func @Draw(%x: i32, %y: i32, %mode: i32) -> void { ...unchanged... }
func @Draw$c1(%x: i32) -> void { ...the body with %y = 100 and %mode = 1... }
func @hot() -> void {
  Call.void @Draw$c1(%i)
  Call.void @Draw$c1(%j)
}
```

## When it applies

(1) The callee is owned (`ModuleOwnership` of `OPT-IPA-CONSTARG`) and not variadic. (2) For a
caller `C`, the agreed set is the set of parameter positions at which all of `C`'s direct calls pass the same
literal (by bits); positions at which every direct call of the module passes that literal are removed from it;
`C` is eligible when the set is not empty. (3) Eligible callers are ranked by `|agreed set| × |call sites of
C|`; the pass clones for callers in rank order while the function has fewer than 3 clones and the module's
cloned instructions stay within 1 024. If every caller of the function is eligible, the last in rank keeps
the original. (4) The clone `@<name>$c<n>` is internal, has the agreed parameters bound and removed; `C`'s
calls are rebound with the remaining arguments in order. (5) A direct self-call inside the clone targets the
original function. (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) scores the cloned module strictly below the original under the speed
objective.

## How it is proven

`structural`: every call rebound to the clone passed exactly the literals the clone substituted; all other calls are untouched. No solver, no timeout path.

## When it must not fire

- A caller with the calls `Draw(i, 100, 1)` and `Draw(j, 100, 2)` cloned for `mode = 1` because only its first call was inspected: the second call enters a body specialized for the wrong mode. The agreed set is taken over all of the caller's calls.
- A recursive call in the clone retargeted to the clone: it passes `mode − 1` and enters a body specialized for `mode = 1`.
- A caller whose two calls pass 1 and 2: no agreed parameter.
- Cloning for every caller of a function with twenty callers: twenty copies; the limits refuse the seventeen lowest-ranked.
- An exported callee cloned for an internal caller while an outside caller also exists: harmless by itself, but excluded here so that every cloning rule has the same boundary.

## Targets

every target at `--opt speed`: the specialized copy folds its tests and can be inlined where the
general body was too large. The rewrite spends code size deliberately; the limits bound it, and at every other
level it is off, so generated source keeps one function per source function.

## Switching it off

`--disable-opt O0393`; active at `--opt speed` only; off at `balanced`, `size`, `memory` and `none`.

## Interactions with other passes

module pass in phase `optimize` after [#159](https://github.com/Hawkynt/Polypiler/issues/159) has inlined what is below its budget and after `OPT-IPA-CLONECONST`; the clones are folded by [#164](https://github.com/Hawkynt/Polypiler/issues/164) in the next iteration and reconsidered by [#159](https://github.com/Hawkynt/Polypiler/issues/159), which may now accept them.
