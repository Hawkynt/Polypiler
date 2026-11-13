# O0383 — Remove unreferenced globals of a module and the functions only their initializers kept alive

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0383`.

## What it does

`OPT-IPA-DEADFUNC` removes unreachable functions but treats every global as live. The data those functions
used stays behind: string constants of removed procedures, lookup tables of unused features, dispatch tables
nothing dispatches through — and a dispatch table's initializer in turn keeps its functions alive. Functions and
globals have to be swept as one graph. On a small target the unused tables are often larger than the unused
code. PRD §4.8 (DCE).

## Example

### Before

```
global @limit: u32[1] = [320] [readonly]
global @table: u16[4] = [1, 2, 3, 4] [readonly]                  // used only by @Unused
global @ops: fn<(i32) -> i32>[1] = [FuncRef @area] [readonly]    // used by nothing
func @area(%r: i32) -> i32 { ... }
func @Unused() -> u16 { %v = Load.u16 @table, 0  Ret.u16 %v }
func @main() -> void export "main" { %v = Load.u32 @limit, 0  Call.void @print(%v)  Ret.void }
```

### After

```
global @limit: u32[1] = [320] [readonly]
func @main() -> void export "main" { %v = Load.u32 @limit, 0  Call.void @print(%v)  Ret.void }
```

## When it applies

(1) Live functions are those of `OPT-IPA-DEADFUNC`'s marking when `O0381` is enabled, and all
functions of the module otherwise — a global is never removed while an existing function names it. (2) A
global is live iff it is named by an instruction operand of a live function, or its address or a view of it
occurs in the initializer of a live global, or its region has the lifetime class `external` or is declared by
the target profile as device memory ([#121](https://github.com/Hawkynt/Polypiler/issues/121)). (3) Marking runs to a fixed point over functions and
globals together: a live global's initializer marks the globals and functions it names. (4) A global that is
not live is deleted with its initializer. Volatility does not keep an unreferenced global: with no access
there is nothing to observe. Removal has no alternative form, so there is no cost query.

## How it is proven

`structural`: a global that no reachable code or data names cannot be read, written or have its address taken by any execution that starts at a root. No solver, no timeout path.

## When it must not fire

- A table referenced only from another table's initializer that is itself live: removing it leaves a dangling address.
- A global with lifetime class `external`: a foreign module reads it by name.
- Removing `@table` while `@Unused` still exists (O0381 disabled): the function now names a missing global.
- Treating a dispatch table's functions as roots regardless of the table's liveness: `@area` survives forever.

## Targets

every target: less data to emit. Decisive on memory-bound targets; for source targets it removes
unused constant tables from the generated file. `--opt none` keeps all data.

## Switching it off

`--disable-opt O0383`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

module pass in phase `optimize`, together with `OPT-IPA-DEADFUNC` at the end of each iteration; enabled further by `OPT-IPA-CONSTGLOBAL` and `OPT-IPA-LOCALGLOBAL`, which leave globals without references.
