# O0006 — Inline direct calls under the cost model

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#159](https://github.com/Hawkynt/Polypiler/issues/159). Once implemented it is switched off with `--disable-opt O0006`.

## What it does

Algorithm libraries are written as layers of tiny helpers — Cipher's `OpCodes.RotL32`, `Pack32BE`, a
`clamp` — and almost every recognition rule and range fact stops at a call boundary. Inlining is what lets the
rotate inside `RotL32` be seen next to its constant amount. It is also the main source of code growth, so it
must obey the cost model and a hard growth limit. PRD §4.8 (inlining).

## Example

### Before

```
@f(%x: u32) -> u32:
  %m = Mul.u32[wrap] %x, 2
  %r = Add.u32[wrap] %m, 1
  Ret.u32 %r
@g(%a: u32) -> u32:
  %p = Call.u32 @f(%a)
  %q = Call.u32 @f(3)
  %s = Add.u32[wrap] %p, %q
  Ret.u32 %s
```

### After

```
@g(%a: u32) -> u32:
  %m1 = Mul.u32[wrap] %a, 2
  %p  = Add.u32[wrap] %m1, 1
  %m2 = Mul.u32[wrap] 3, 2
  %q  = Add.u32[wrap] %m2, 1        // folds to 7 in the next O0001 run
  %s  = Add.u32[wrap] %p, %q
  Ret.u32 %s
```

## When it applies

All of: (1) the call is direct, the callee body is present and is not marked `noinline` by its frontend;
(2) caller and callee are not in the same call-graph strongly connected component; (3) the callee does not
read its own frame identity (variadic argument access, address of a parameter slot escaping, `Stack` effect
on return addresses — [#114](https://github.com/Hawkynt/Polypiler/issues/114)); (4) the callee has the same floating-point environment and overflow
defaults as the caller; (5) if the source profile makes stack traces observable, the callee cannot raise
([#115](https://github.com/Hawkynt/Polypiler/issues/115)); (6) the callee's exception regions can be nested inside the caller's region at the call
site without changing which handler catches which exception; (7) the growth limits hold after the inline.
Arguments are SSA values evaluated before the call, so argument evaluation order and their traps are
unchanged by construction; callee returns become branches to a continuation block with a phi.

## How it is proven

`structural+effect-facts`: substitution of a function body for its call (β-reduction on SSA) with the side conditions above. No solver, no timeout path; exceeding a limit is `not applied`.

## When it must not fire

- `@fact(n) = n * @fact(n-1)`: inlining a recursive function into itself never terminates and changes nothing semantically — refused by rule 2.
- A callee that reads variadic arguments or returns the address of a parameter slot: the frame it refers to no longer exists.
- A callee that throws when stack traces are observable (C# `Exception.StackTrace` printed by the program): the frame disappears from the trace.
- A callee compiled with checked arithmetic inlined into an unchecked caller, if the instructions did not carry their own contract — they do; the test proves the `[checked]` survives.
- A callee with a `try` region whose handler also covers the call's continuation after naive splicing: an exception from the caller's later code would be caught by the callee's handler.

## Switching it off

`--disable-opt O0006`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs first in phase `optimize` (module pass, bottom-up over the call graph in deterministic order: callees before callers, ties by function name); enables [#154](https://github.com/Hawkynt/Polypiler/issues/154), [#164](https://github.com/Hawkynt/Polypiler/issues/164), [#158](https://github.com/Hawkynt/Polypiler/issues/158), range propagation and every recognition rule.
