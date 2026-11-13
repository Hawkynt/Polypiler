# O0441 — Clone a function for an argument value that dominates at run time and guard the clone

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0441`.

## What it does

`transform(mode, data)` is called with `mode = 1` in 97 % of its executions, but not always, so no
constant propagation can use it. A clone in which `mode` *is* 1 loses whole branches and constant tables;
a single comparison at the call site chooses between the clone and the original. PB-Compiler implements
this as value-profile specialization (PB O0270): unlike interprocedural constant propagation it needs
only that one value dominates at run time, and it stays correct when the profile is wrong, because the
guard tests the actual value. PRD §4.8.

## Example

### Before

```
caller:
  %r = Call.u32 @transform(%mode, %data)          // values of parameter 0: total 100, value 1 × 97
```

### After

```
caller:
  %mode.is = ICmp.eq.u32 %mode, 1
  CondBr %mode.is, caller.fast, caller.slow
caller.fast:
  %r.fast = Call.u32 @transform.value.0.0x1(%mode, %data)     // clone: uses of parameter 0 are the constant 1
  Br caller.cont
caller.slow:
  %r.slow = Call.u32 @transform(%mode, %data)
  Br caller.cont
caller.cont:
  %r = Phi.u32 [caller.fast: %r.fast], [caller.slow: %r.slow]
```

## When it applies

(1) The profile's `values` section has an entry for (function, parameter) with `count × 100 ≥ 90 × total` for one value, computed in 128-bit integers; `total` is the function's entry count. (2) The callee has a body, at most 64 instructions, is not `noinline`-protected against cloning by a frame or stack-trace dependency (the refusals of [#159](https://github.com/Hawkynt/Polypiler/issues/159) apply to cloning as well), and [#492](https://github.com/Hawkynt/Polypiler/issues/492) grants one more version. (3) The clone keeps the signature; inside it every use of the parameter is replaced by the constant. Calls inside a clone are not guarded again. (4) The guard compares the *actual* argument with the constant: `ICmp.eq` for integers and `bool`; for `f32`/`f64` an `ICmp.eq` of the `BitCast` bit patterns ([#116](https://github.com/Hawkynt/Polypiler/issues/116)), so `+0.0` does not select a clone made for `−0.0` and a NaN selects only the clone made for its exact payload; for a pointer, comparison with null. (5) The guard is evaluated after the arguments and before the call, adds no effect and cannot trap. (6) The result phi joins both calls; a `void` call needs none. **Without a profile** (`--profile-use` absent, or the function stale or unmeasured) the pass does nothing and writes no report entry: every count it asks `IProfileData` for is null, and null never satisfies a threshold. PRD §4.8: no translation needs a profile. **Determinism** (PRD §13.1): the decision is integer arithmetic on counts read through `IProfileData`, candidates are visited in module and block order, ties are broken by block number — the same input, compiler, profile and seed give the same output, and the profile's digest is in the manifest's command line (`OPT-PGO-COLLECT`).

## How it is proven

`structural`: when the guard holds, the clone computes on the same value the original would have read from the parameter; otherwise the original runs. The profile selects *which* value is cloned and has no part in correctness. No solver, no timeout path.

## When it must not fire

- Guarding a float with `FCmp.oeq`: `−0.0 == +0.0` is true, so a clone specialized for `+0.0` would run for `−0.0` and `1 / x` inside it would give `+∞` instead of `−∞`.
- A NaN value: `FCmp.oeq` is never true (the clone would be dead), and two NaNs with different payloads are different values for `BitCast`-based code.
- Calling the clone without a guard because "the profile says 97 %": the other 3 % run code specialized for the wrong value.
- Dropping the parameter from the clone's signature and forgetting an external caller: it still passes the argument.
- Replacing the parameter by the constant in the *original*: every other mode is broken.

## Switching it off

`--disable-opt O0441`; active at `--opt balanced` and `speed`; off at `size`, `memory` and `none` (it adds a function and a guard per call site).

## Targets

every target, source and machine code: no downstream compiler has the value histogram. In generated source it appears as one additional function per specialization and an `if` at the call sites; [#492](https://github.com/Hawkynt/Polypiler/issues/492) bounds the number of versions and [#155](https://github.com/Hawkynt/Polypiler/issues/155)'s objective keeps it off the size-oriented levels, so a source target that is built for readability at `--opt size` never gets clones.

## Interactions with other passes

module pass in phase `optimize`, after [#159](https://github.com/Hawkynt/Polypiler/issues/159) (a call that was inlined needs no clone) and before [#164](https://github.com/Hawkynt/Polypiler/issues/164) and [#154](https://github.com/Hawkynt/Polypiler/issues/154), which fold the clone's now-constant branches in the same phase iteration. The clone's blocks inherit the original's profile keys. Adds functions; invalidates the call graph.
