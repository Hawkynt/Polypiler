# O0381 — Remove functions that are unreachable from the module's exports, tests and taken addresses

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0381`.

## What it does

After the middle end has run, a module carries functions nothing can reach: helpers of features the program
does not use, and every function [#159](https://github.com/Hawkynt/Polypiler/issues/159) inlined at all of its call sites. [#156](https://github.com/Hawkynt/Polypiler/issues/156) and [#159](https://github.com/Hawkynt/Polypiler/issues/159)
both name their removal as not planned. Left in, they are emitted, compiled and shipped — on a 64 KB target
that is the difference between fitting and not — and inlining a small helper becomes a size *loss* instead of
a win. PRD §4.8 (DCE, inlining).

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

A function is deleted iff it is not marked. Marking: (1) roots are all functions with an `export`
attribute and all `test` functions ([#125](https://github.com/Hawkynt/Polypiler/issues/125)); (2) a marked function marks every function it names in a
`Call` and in a `FuncRef` ([#480](https://github.com/Hawkynt/Polypiler/issues/480)), in all of its blocks; (3) a `FuncRef` in the initializer of any
global marks its function — every global counts as live in this issue. External declarations without a body
are not functions of the module and are neither marked nor deleted. Nothing else is considered: a non-exported
function cannot be named from outside the module. Removal has no alternative form, so there is no cost
query.

## How it is proven

`structural`: an unmarked function cannot be entered by any execution that starts at a root, because every way to obtain its address or call it lies in code or data that is itself unmarked or was followed. No solver, no timeout path.

## When it must not fire

- "No users" instead of reachability: `@Unused` and `@AlsoUnused` call each other, each has a user, both are dead.
- A function referenced only by `FuncRef` in a global dispatch table: no `Call` names it, an indirect call reaches it.
- A `test` function: nothing calls it, the test runner does.
- An exported function nothing in the module calls: the caller is outside.
- Deleting a function while a `FuncRef` to it remains in an unreachable block of a live function: the verifier rejects the dangling reference; unreachable blocks must be gone or their references followed.

## Targets

every target: less code to emit, compile and ship, most valuable on memory-bound targets (6502, DOS
`.COM`, tokenized BASIC) and for generated packages. `--opt none` keeps every function, so a translation that
must mirror the source file for review is still available.

## Switching it off

`--disable-opt O0381`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

module pass in phase `optimize`, last in each iteration, after [#159](https://github.com/Hawkynt/Polypiler/issues/159) and [#164](https://github.com/Hawkynt/Polypiler/issues/164); enables `OPT-IPA-CONSTARG` and `OPT-IPA-DEADPARAM` (fewer call sites to agree) and makes inlining a size win.
