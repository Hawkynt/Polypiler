# O0381 — Remove functions that are unreachable from the module's exports, tests and taken addresses

> Implemented in the pass pipeline of `translate`. Disable with `--disable-opt O0381`.

## What it does

`ModuleReachability.Compute` marks the functions reachable from retained roots. `DeadFunctionElimination`
removes every unmarked function in source order, including dead recursive cycles and helpers whose
last call was inlined. Live function objects and bodies stay unchanged. This implements PRD §4.8's
module dead-code removal on the structured tree IR.

## Example

### Before

```
func @Used() -> void { Call.void @print(1)  Ret.void }
func @Unused() -> void { Call.void @AlsoUnused()  Ret.void }
func @AlsoUnused() -> void { Call.void @Unused()  Ret.void }      // a dead cycle
func @main() -> void export "main" { Call.void @Used()  Ret.void }
```

### After

```
func @Used() -> void { Call.void @print(1)  Ret.void }
func @main() -> void export "main" { Call.void @Used()  Ret.void }
```

## When it applies

Roots are public functions, the additional entry points in `OptimizerOptions.Roots` (the CLI supplies
Rust's test functions), the module initializer, every function named by a retained class or interface,
and calls and function references in every global initializer. Retaining all type declarations means
retaining their constructors, methods, property accessors, overrides and abstract signatures.

A queue follows `IrCall` and `IrFunctionRef` nodes in each marked body, including nested lambdas and
untaken branches. Global initializers currently admit pure calls; a factory returning a function
reference keeps both the factory and its target. All globals remain live. `ReachabilityResult.Roots`
records the first root that reached each function. No use count, solver or cost query is needed.

## How it is proven

`structural`: an unmarked function cannot be entered by any execution that starts at a root, because every way to obtain its address or call it lies in code or data that is itself unmarked or was followed. No solver, no timeout path.

## When it must not fire

- "No users" instead of reachability: `@Unused` and `@AlsoUnused` call each other, each has a user, both are dead.
- A function referenced only by `FuncRef` in a global dispatch table: no `Call` names it, an indirect call reaches it.
- A test function listed in `OptimizerOptions.Roots`: nothing calls it, the test runner does.
- An exported function nothing in the module calls: the caller is outside.
- Deleting a function while a `FuncRef` to it remains in an unreachable block of a live function: the verifier rejects the dangling reference; unreachable blocks must be gone or their references followed.

## Targets

every target: less code to emit, compile and ship, most valuable on memory-bound targets (6502, DOS
`.COM`, tokenized BASIC) and for generated packages. `--opt none` keeps every function, so a translation that
must mirror the source file for review is still available.

## Switching it off

`--disable-opt O0381`; active at every non-`none` level the prototype accepts (`balanced`, `speed`,
`size` and `memory` select the same default pipeline). `--opt none` runs no pass.

## Interactions with other passes

Runs last in each round of `IrPassPipeline`, after inlining, propagation, folding, call evaluation,
merging, dead-code removal and unrolling. Reachability is recomputed over the whole module on every
sweep, regardless of the pipeline's dirty-function scope. Removed helper entries leave
`Implementations`; `Inlined`, `Clones` and `ScopeFunctions` keep their historical provenance so notices
for code copied into a live body survive. Foreign declarations remain unchanged.

The opt report names each removed function, its statement-plus-expression size and proof class
`structural`. There are no refusal entries for reachable functions.

## Verification and provenance

The acceptance scenarios of [#1153](https://github.com/Hawkynt/Polypiler/issues/1153) run on hand-built
tree IR; `tests/fixtures/optimize/o0381/*.pir` records those trees, rather than claiming a textual IR
parser. The print trace is an observable global write, and tests enter through explicit roots.
Unit tests cover initialization, indirect calls, closures, retained type members, switches, empty
modules, inlined notices and argument errors. Seed 381 generates 2,000 call graphs of 3–20 functions:
an independent breadth-first search over a textual edge list checks survivors; eight inputs per root
compare interpreter results, traps and global writes before and after removal.

Converted from PB-Compiler's `GlobalDce` and its tests at
`0d73acbea185a2597357d53f7ce9acb44758ba76` (LGPL-3.0-or-later), which describe dead procedures as
O0022 and fully inlined procedures as O0201. Marking roots also removes cycles the upstream use-count
sweep leaves alive. See `Polypiler.Core.Optimize/THIRD_PARTY_NOTICES.md` and `spec/rewrites/O0381.yaml`.
