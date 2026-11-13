# O0384 — Remove parameters no path in the callee reads from the signature and from every call site

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0384`.

## What it does

`OPT-IPA-CONSTARG` replaces a parameter's uses by a literal and deliberately leaves the signature alone, so
every call still computes, pushes and passes a value nobody reads; general helpers carry context parameters
that only some variants use. Removing the parameter saves the argument at every call and often the computation
behind it, and it shortens the signature a reader of the generated code sees. It changes the function's
interface, which is only legal when the module owns every caller. PRD §4.8 (interprocedural passes, DCE).

## Example

### Before

```
func @Draw(%mode: i32, %x: i32) -> void {          // %mode has no use
  Call.void @print_text(%x)
  Ret.void
}
func @main() -> void export "main" {
  %m = Call.i32 @next_mode()                        // has effects
  Call.void @Draw(%m, 10)
  Call.void @Draw(1, 20)
  Ret.void
}
```

### After

```
func @Draw(%x: i32) -> void {
  Call.void @print_text(%x)
  Ret.void
}
func @main() -> void export "main" {
  %m = Call.i32 @next_mode()                        // still called
  Call.void @Draw(10)
  Call.void @Draw(20)
  Ret.void
}
```

## When it applies

(1) The function is owned (`ModuleOwnership` of `OPT-IPA-CONSTARG`): not exported, not a `test`
function, no `FuncRef` names it. (2) It is not variadic, and the parameter does not carry the `receiver`
attribute ([#125](https://github.com/Hawkynt/Polypiler/issues/125)). (3) The parameter is dead: starting from "all parameters of owned functions are
dead", a parameter becomes live when it has a use other than as a call argument at a position still considered
dead; the fixed point is the answer. (4) Every call site passes exactly as many arguments as the current
signature has parameters. (5) At each call site only the operand is removed; the instruction that produced it
remains, in place. (6) Parameter attributes and source locations of the remaining parameters are kept;
proof-table entries and facts that mention a removed parameter are dropped. The rewrite removes work at every
call and adds none, so [#155](https://github.com/Hawkynt/Polypiler/issues/155) is not expected to refuse; the original is still listed first.

## How it is proven

`structural`: a value that no path reads cannot influence any result or effect of the callee; removing its transport changes nothing, and the argument's own evaluation is untouched. No solver, no timeout path.

## When it must not fire

- Deleting the argument's computation along with the operand: `@next_mode()` has effects and must still be called once, in the same position.
- An exported function: an outside caller still passes two arguments.
- A function referenced by `FuncRef`: an indirect call uses the old signature.
- A parameter used only by a `[trap]`-capable instruction whose result is unused: it is read; whether the instruction can go is [#156](https://github.com/Hawkynt/Polypiler/issues/156)'s decision, made first.
- A parameter forwarded in recursion and also read once: live; treating every recursive forward as dead removes it.

## Targets

every target: one argument less per call in machine code, and a shorter signature and call in
generated source. The functions affected are internal, so no public interface of a generated package
changes.

## Switching it off

`--disable-opt O0384`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

module pass in phase `optimize` after `OPT-IPA-CONSTARG` and the per-function [#156](https://github.com/Hawkynt/Polypiler/issues/156) of the iteration; enables [#156](https://github.com/Hawkynt/Polypiler/issues/156) of argument computations, `OPT-IPA-ICF` (signatures become equal) and [#159](https://github.com/Hawkynt/Polypiler/issues/159) (smaller call overhead in its cost).
