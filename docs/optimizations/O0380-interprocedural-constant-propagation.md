# O0380 — Replace a parameter that receives the same constant at every call site by that constant in the callee

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0380`.

## What it does

A helper called as `Draw(1, x)` from every call site tests `mode == 1` on every call and carries the code for
all other modes. [#164](https://github.com/Hawkynt/Polypiler/issues/164) stops at the function boundary and names constants across calls as not planned;
[#159](https://github.com/Hawkynt/Polypiler/issues/159) exposes them only where it inlines. [#87](https://github.com/Hawkynt/Polypiler/issues/87) joins parameter *ranges* over call
sites, which decides integer comparisons but leaves floats, booleans, null and code references untouched and
never puts the literal itself into the callee. Substituting the literal lets every per-function pass see it.
PRD §4.8 (constant/partial evaluation, SCCP).

## Example

### Before

```
func @Draw(%mode: i32, %x: i32) -> void {
  %c = ICmp.eq.i32 %mode, 1
  Br %c, text, gfx
text:
  Call.void @print_text(%x)
  Ret.void
gfx:
  Call.void @print_gfx(%x)
  Ret.void
}
func @main() -> void export "main" {
  Call.void @Draw(1, 10)
  Call.void @Draw(1, 20)
  Ret.void
}
```

### After

```
func @Draw(%mode: i32, %x: i32) -> void {      // signature unchanged
  %c = ICmp.eq.i32 1, 1                        // folds; the gfx arm becomes unreachable
  Br %c, text, gfx
  ...
}
```

## When it applies

(1) The callee is owned (`ModuleOwnership.IsOwned`): not exported, not a `test` function, no `FuncRef`
names it — so every call is a direct `Call` the pass can enumerate. (2) It has at least one call site in a
function that is itself reachable; a function without call sites is left to `OPT-IPA-DEADFUNC`. (3) At every
call site the argument for parameter `p` is a literal of `p`'s type and all those literals are equal by bits
(so `+0.0` and `-0.0` differ, and two NaNs are equal only with the same payload), or the argument is the
callee's own `p` in a recursive call. (4) What is replaced is the value the parameter arrives with: every use
of the SSA value `p`. (5) The signature, the call sites and the argument expressions are untouched, so
evaluation order and effects of the arguments are unchanged. [#155](https://github.com/Hawkynt/Polypiler/issues/155) is asked with the parameter read
listed first; a literal that the target must materialize at higher cost than a parameter read is refused.

## How it is proven

`structural`: every execution of the callee starts at one of the enumerated call sites, and each passes the literal; the substitution replaces a value by an equal one. No solver, no timeout path.

## When it must not fire

- An exported function called with 1 from inside the module: an outside caller passes 2.
- A function whose address is stored in a table: the indirect call passes whatever the table's user chooses.
- Call sites passing `0.0` and `-0.0`: equal under `==`, different under `1 / x`.
- A third call site added by inlining a caller: the pass must enumerate call sites of the module as it is now, not a cached census.
- Mutual recursion where `f` passes its parameter to `g` and `g` passes a different literal back to `f`.

## Targets

every target: the folded branches disappear from machine code and from generated source alike. The
signature is unchanged, so no target sees an interface change; a mature target compiler performs the same
propagation only when it can see the whole program, which a generated library usually prevents.

## Switching it off

`--disable-opt O0380`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

module pass, runs first in phase `optimize` after [#160](https://github.com/Hawkynt/Polypiler/issues/160) and before the per-function [#164](https://github.com/Hawkynt/Polypiler/issues/164) of the same iteration; iterates with `OPT-IPA-RETCONST`; enables SCCP, [#156](https://github.com/Hawkynt/Polypiler/issues/156), `OPT-IPA-DEADPARAM` and, for code-reference parameters, `OPT-IPA-DEVIRT`.
