# O0395 — Version an indirect call into a guarded direct call to a statically likely target with the indirect call as fallback

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0395`.

## What it does

`OPT-IPA-DEVIRT` resolves an indirect call only when the complete target set is proven to be one function.
Often the set is not provable — the procedure variable is assigned once here but could be reassigned somewhere
the compiler cannot see — yet one target is overwhelmingly likely. Testing for that target and calling it
directly, with the unchanged indirect call as the other arm, costs a comparison and makes the likely path
inlinable; a wrong guess changes speed only, never which function runs. PRD §4.6 (guarded paths keep a valid
fallback), §4.8.

## Example

### Before

```
%r = CallIndirect.i32 %f(21)                // %f: fn<(i32) -> i32>, target set not complete
```

### After

```
  %g = (comparison of %f with FuncRef @Double — see Blocking questions)
  Br %g, direct, indirect
direct:
  %r1 = Call.i32 @Double(21)
  Br join
indirect:
  %r2 = CallIndirect.i32 %f(21)
  Br join
join:
  %r = Phi.i32 [direct: %r1], [indirect: %r2]
```

## When it applies

(1) `CodeTargets.Of(%f)` is not a complete non-null singleton (otherwise `OPT-IPA-DEVIRT` applies
and no guard is wanted). (2) Exactly one candidate exists under the heuristic in scope: a function with the
indirect call's parameter and return types whose `FuncRef` occurs in the calling function; failing that,
exactly one such function module-wide; two or more are a refusal. (3) The guard is an equality test of two
code references, free of effects and traps, true only when `%f` designates the candidate. (4) The original
`CallIndirect` moves unchanged into the fallback arm, with its trap contract (a null callee still traps there);
arguments are evaluated once, before the guard. (5) Instructions after the call move to the join block and
successor phis are repaired. (6) The call and the new blocks lie in one exception region. (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155)
scores comparison plus direct call strictly below the indirect call for the speed objective.

## How it is proven

`structural`: on the true arm the callee value designates the candidate, so the direct call enters the function the indirect call would have entered; on the false arm the original call runs. No solver, no timeout path.

## When it must not fire

- Replacing the call outright by the direct call: when the variable was reassigned, another function must run.
- Evaluating an argument with effects in both arms: it runs twice on one path or in a different order.
- A guard that compares function *identity* loosely (same body, different function): two functions with equal bodies but different addresses are different callees for a program that compares them.
- A null callee: the guard is false and the fallback must still trap with `NullReference`.
- Versioning the fallback again on the next pass run: the code doubles each iteration.

## Targets

targets with native code pointers at `--opt speed`, where a compare-and-branch is cheaper than an
indirect call and the direct path can be inlined. On targets that lower code references to an index and a
dispatch trampoline the comparison is an integer test and the gain is larger. The call site is duplicated, so
the rewrite is off at every other level.

## Switching it off

`--disable-opt O0395`; active at `--opt speed` only; off at `balanced`, `size`, `memory` and `none`.

## Interactions with other passes

module pass in phase `optimize` after `OPT-IPA-DEVIRT` and before [#159](https://github.com/Hawkynt/Polypiler/issues/159), which can then inline the direct arm; the fallback keeps the conservative effects of an indirect call for [#160](https://github.com/Hawkynt/Polypiler/issues/160).
