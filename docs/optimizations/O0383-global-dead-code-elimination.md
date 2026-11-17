# O0383 — Remove unreferenced globals and the code only their initializers kept alive

> Implemented in the pass pipeline of `translate`. Disable with `--disable-opt O0383`.

## What it does

`DeadGlobalElimination` marks functions and globals in one graph. A dead global's initializer
does not keep code alive: an unused dispatch cell can disappear together with its factory and
handler. With [O0381](O0381-dead-function-elimination.md) disabled, every existing function is a
root, so a function can never be left naming deleted storage.

## Example

Readable structured tree sketches, as defined by ADR 0009:

### Before

```
global limit: i32 = 320
global table: u16[] = [1, 2, 3, 4]
global ops: fn() -> i32 = factory()
export main() -> i32 { return limit; }
private Unused() -> u16 { return table[0]; }
private factory() -> fn() -> i32 { return FuncRef area; }
private area() -> i32 { return 7; }
```

### After

```
global limit: i32 = 320
export main() -> i32 { return limit; }
```

With only O0383 enabled, `ops` disappears, but every function and both `limit` and `table` remain.

## When it applies

Roots include public functions, `OptimizerOptions.Roots`, the module initializer, retained class
and interface members, retained class static fields, and globals named by
`OptimizerOptions.GlobalRoots`. The latter lets a host or target profile protect external or
device storage. The current tree has no external/device/volatile region attribute on `IrGlobal`;
those properties must not be inferred from a name or from absence of ordinary readers.

The worklist follows direct calls, function references, global reads and global assignments,
including writes inside closures and references in untaken branches. A live global follows its
initializer's globals and functions. Surviving objects and their declaration order are preserved.
`ModuleReachability.Compute` exposes the resulting identity sets through `LiveFunctions` and
`LiveGlobals`. `ReachabilityOptions.AllFunctionsLive` protects all bodies without O0381;
`AllGlobalsLive` defaults to true for existing O0381-only callers.

Initializers execute eagerly. An unread initializer that may trap, have effects or fail to end
is therefore a root. The effect summary guards initializer calls; syntactic checks guard the
other nodes. Literal arrays and arrays of a fixed nonnegative length within `Int32.MaxValue`
are static data. Their removal does not model host resource exhaustion. Unknown runtime lengths,
division, checked arithmetic and other operations not proven removable stay. A global read in
an initializer is safe because the verifier requires its declaration to occur earlier.

## How it is proven

`structural`: the queue computes the closure of every retained root over code and data references.
An unmarked global cannot be read, written or named by a retained declaration, and deleting its
initializer has no modeled observable effect. With both sweeps enabled, code and data are deleted
atomically before verification, including dead cycles. No cost query, solver or timeout is needed.

## When it must not fire

- A live initializer names an earlier global: both must remain, even if only the outer value is read.
- A host/profile retains a global, or a retained class declares it as a static field.
- A surviving function writes a global but never reads it: this is not dead-store elimination.
- An unread initializer divides by zero or calls a possibly endless function: loading must still fail or not end.
- O0381 is disabled and a private function still names the global.

The verifier rejects self-referential and forward global initializers; the pass does not turn
invalid input into valid input. Unused record fields, table elements and the separate immutable
`IrModule.Constants` collection are outside this sweep.

## Switching it off

`--disable-opt O0383`; active at every non-`none` level (`balanced`, `speed`, `size` and `memory`
currently select the same default pipeline). `--opt none` keeps all code and data. Disabling O0383
makes every global initializer an O0381 root again.

## Interactions and reporting

Runs with O0381 at the end of each round, after inlining, propagation, folding, call evaluation,
merging, dead-code removal and unrolling. Marking ignores the dirty-function scope and is rebuilt
each time. O0381 removes obsolete `Implementations` entries; provenance history and foreign
declarations remain intact. The default pipeline checks the verifier after the atomic sweep.

Each deletion produces `O0383 applied: global @name removed (4 bytes): unreferenced [proof: structural]`.
The count is logical numeric payload (one byte for a bool), excluding headers, alignment and
target-dependent references. Non-numeric or unknown-length storage reports `unknown bytes`.
There are no refusal entries for retained globals.

## Verification and provenance

The seven acceptance scenarios exercise dead dispatch data, independent disabling, live initializer
dependencies, explicit external/device roots and eager initialization traps. Readable fixtures live
under `tests/fixtures/optimize/o0383/`; executable tree constructors are in the tests, not a text parser.
Unit tests also cover mutable writes, closures, retained classes, array limits, logical sizes, empty
modules and argument errors. Seed 383 generates 2,000 joint graphs, with and without the function
sweep: independent breadth-first search checks both survivor sets; eight inputs per exported entry
compare return values and the observable global-write trace. Trap cases compare module-load outcomes.
Source sizes are recorded for C#, JavaScript, Python and Rust.

Converted from PB-Compiler's `GlobalDce`, its tests and
[O0054](https://github.com/Hawkynt/PB-Compiler/blob/0d73acbea185a2597357d53f7ce9acb44758ba76/docs/optimizations/O0054-ir-global-dce.md)
at `0d73acbea185a2597357d53f7ce9acb44758ba76` (LGPL-3.0-or-later). Joint marking extends its
function-then-global use-count sweep to release code held only by dead initializers, including cycles.
See `Polypiler.Core.Optimize/THIRD_PARTY_NOTICES.md` and `spec/rewrites/O0383.yaml`.
