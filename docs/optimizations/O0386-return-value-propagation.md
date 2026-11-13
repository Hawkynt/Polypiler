# O0386 — Replace the result of a call to a function that returns the same constant on every path by that constant

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0386`.

## What it does

Facts flow out of a function as well as in. A function that returns the same constant on every path — a
version number, a capability flag, a status that a specialization made constant — is that constant at every
call, even when its body has effects and [#162](https://github.com/Hawkynt/Polypiler/issues/162) therefore cannot evaluate it. [#164](https://github.com/Hawkynt/Polypiler/issues/164) names
constants across function boundaries as not planned; [#87](https://github.com/Hawkynt/Polypiler/issues/87) carries the result's *range* to
call sites, which decides integer comparisons, but the literal never replaces the call's result and
non-integer results gain nothing. PRD §4.8 (constant/partial evaluation, interprocedural effects).

## Example

### Before

```
func @version() -> u32 {
  Call.void @log(1)                       // has effects
  Ret.u32 3
}
func @main() -> u32 export "main" {
  %v = Call.u32 @version()
  %c = ICmp.uge.u32 %v, 2
  Br %c, modern, legacy
  ...
}
```

### After

```
func @main() -> u32 export "main" {
  %v = Call.u32 @version()                // stays: @log must run
  %c = ICmp.uge.u32 3, 2                  // folds; the legacy arm becomes unreachable
  Br %c, modern, legacy
  ...
}
```

## When it applies

(1) The callee's body is in the module and the call is a direct `Call`. Ownership is *not* required:
the fact is a property of the body, and it holds for callers inside the module whoever else calls the
function. (2) The callee has at least one `Ret` with a value; every such `Ret` returns the same literal by
bits, or the result of a direct self-call. A function that never returns (only traps or diverges) offers no
constant. (3) Only uses of the call's result are replaced; the call, its arguments and their order stay. (4)
A call through `CallIndirect`, to an external declaration, or to a function with a `resumable` attribute is
not rewritten. [#155](https://github.com/Hawkynt/Polypiler/issues/155) is asked with the result read listed first, as in `OPT-IPA-CONSTARG`.

## How it is proven

`structural`: every return of the callee delivers the literal, by induction over the recursion depth for self-calls; a call that returns at all therefore returns it. No solver, no timeout path.

## When it must not fire

- Deleting the call because its result is known: `@log(1)` no longer runs.
- A function with one path returning 3 and one returning `%x` that is 3 in every test: not a literal on every path.
- A function whose only `Ret` returns its own recursive result: it never produces a value of its own; there is nothing to propagate.
- Results `0.0` and `-0.0` on two paths: equal by comparison, different by bits.
- A call that traps before returning: the replaced uses are never reached, which is why only uses, not the trap, are touched.

## Targets

every target: comparisons on the result fold and dead arms disappear from machine code and generated
source. The callee and its signature are unchanged, so exported functions keep their behaviour for outside
callers.

## Switching it off

`--disable-opt O0386`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

module pass in phase `optimize`, in the same fixed point as `OPT-IPA-CONSTARG` and before the per-function [#164](https://github.com/Hawkynt/Polypiler/issues/164) of the iteration; enables SCCP, [#156](https://github.com/Hawkynt/Polypiler/issues/156) of calls that turn out pure and unused, and `OPT-IPA-DEADFUNC`.
