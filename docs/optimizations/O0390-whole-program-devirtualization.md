# O0390 — Resolve an indirect call whose complete target set has exactly one member to a direct call

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0390`.

## What it does

Callbacks, strategy parameters and procedure-pointer variables are called through `CallIndirect`, and an
indirect call is opaque to everything: [#160](https://github.com/Hawkynt/Polypiler/issues/160) gives it unknown effects, [#159](https://github.com/Hawkynt/Polypiler/issues/159) cannot inline
it, and the function whose address was taken counts as reachable from anywhere. Very often only one function
can ever arrive at the call. When the *complete* set of possible targets is known and has one member, the call
is a direct call — no guard needed. [#755](https://github.com/Hawkynt/Polypiler/issues/755) does this for virtual and interface dispatch from receiver
types; this issue does it for plain code references from value flow. PRD §4.8 (inlining, interprocedural
effects).

## Example

### Before

```
func @Double(%v: i32) -> i32 { %r = Mul.i32[wrap] %v, 2  Ret.i32 %r }
func @apply(%f: fn<(i32) -> i32>, %v: i32) -> i32 {
  %r = CallIndirect.i32 %f(%v)
  Ret.i32 %r
}
func @main() -> i32 export "main" {
  %r = Call.i32 @apply((FuncRef.fn<(i32) -> i32> @Double), 21)
  Ret.i32 %r
}
```

### After

```
func @apply(%f: fn<(i32) -> i32>, %v: i32) -> i32 {
  %r = Call.i32 @Double(%v)                 // %f is now unused; O0384 removes it
  Ret.i32 %r
}
```

## When it applies

(1) The callee operand's target set from `CodeTargets` is `{F}`: complete, one function, `null`
excluded. Completeness rules: a parameter contributes only if its function is owned (`ModuleOwnership` of
`OPT-IPA-CONSTARG`), so that every call site is visible; a local cell contributes only if [#82](https://github.com/Hawkynt/Polypiler/issues/82)
proves its address does not escape, every access is a load or store of the `fn` type, and a store dominates
the load (an uninitialized cell is not a known target); a cyclic flow that never reaches a `FuncRef` is
`Unknown`. (2) `F`'s parameter types and return type equal the indirect call's (the `fn` type guarantees it;
the pass re-checks and refuses on a mismatch introduced by a cast). (3) Arguments, their order, trap contract
and attributes of the call are carried over. (4) The value computation of `%f` is left in place; it is pure. A
direct call is not expected to cost more than an indirect one; the indirect form is still listed first in the
[#155](https://github.com/Hawkynt/Polypiler/issues/155) query.

## How it is proven

`structural`: the target set over-approximates the functions that can reach the call by enumeration of every flow into it; with one non-null member every execution calls that member. No solver, no timeout path.

## When it must not fire

- One address-taken function in the module taken as proof: a pointer from outside can name a function the module never mentions.
- A parameter of an exported function: an outside caller passes another callback.
- A `Select` between `FuncRef @Double` and `null`: the source traps on the null path; a direct call would not.
- A local cell read before any store: its content is not a known function.
- A cell whose address was passed to a callee that may store another function into it.

## Targets

every target: a direct call is cheaper than an indirect one, it can be inlined, and on targets
without code pointers it removes a `code-index` trampoline dispatch. In generated source a callback parameter
that is always the same function turns into a plain call.

## Switching it off

`--disable-opt O0390`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

module pass in phase `optimize` after `OPT-IPA-LOCALGLOBAL` and `OPT-IPA-CONSTGLOBAL` (which expose private cells and constant tables) and before `OPT-IPA-CONSTARG` and [#159](https://github.com/Hawkynt/Polypiler/issues/159), so resolved calls become ordinary call-graph edges at once; enables [#160](https://github.com/Hawkynt/Polypiler/issues/160) precision, `OPT-IPA-DEADPARAM` and `OPT-IPA-DEADFUNC`.
