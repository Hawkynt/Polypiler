# O0258 — Recover exceptions from manual error-code plumbing

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#950](https://github.com/Hawkynt/Polypiler/issues/950). Once implemented it is switched off with `--disable-opt O0258`.

## What it does

C code reports failure through a status return and passes results through pointers; every caller tests the
status and returns it upward. That is the status-propagation form the legalizer *produces* from exceptions
([#497](https://github.com/Hawkynt/Polypiler/issues/497)). PRD §33.1 O4 asks for the reverse with proof; §30.1 lists "error results/stack" as the
fallback representation of exceptions, which makes its recognition the inverse rule. Recovered, a C# or
Kotlin target gets methods that return their value and throw, instead of `out` parameters and status checks
on every line.

## Example

### Before

```
func @number(%s: ptr<u8>, %out: ptr<i32>) -> i32 {
entry: %d = Alloc.ptr<i32>[stack] 1
       %rc = Call.i32 @digit(%s, %d)
       Br (ICmp.ne.i32 %rc, 0), fail, ok
fail:  Ret.i32 %rc
ok:    Store.i32 %out, (Mul.i32[wrap] (Load.i32 %d), 10)
       Ret.i32 0 }
```

### After

```
func @number(%s: ptr<u8>) -> i32 {
entry: %d = Call.i32 @digit(%s)                ; a StatusError raised by @digit unwinds through @number
       Ret.i32 (Mul.i32[wrap] %d, 10) }
```

## When it applies

obligations (a)–(f) under *In scope*; plus: (g) a caller that propagates
returns exactly the callee's status, not a translation of it; a caller that maps the status to another
constant is a handler that rethrows a new `StatusError`; (h) code executed on the failure branch before
the return is kept as a cleanup region with the same effects; (i) the recovery is applied to a strongly
connected set of functions at once — either all functions of the set satisfy the obligations or none is
rewritten.

## How it is proven

`validated-rule+differential` with the listed fact obligations (reported as
`structural+interpreter` plus the fact classes): each candidate is re-lowered with O0247 and O0078 and the
result is compared with the original fragment on [#119](https://github.com/Hawkynt/Polypiler/issues/119) over the rule's input generator; any
difference, any `Unknown` fact or a budget expiry leaves the IR unchanged.

## When it must not fire

- A caller that ignores the status (`f(&x);` with the result unused): in the original, execution continues
  with a stale `x`; after recovery the exception would propagate and end the function.
- A caller that reads the out-parameter on the failure branch for a partial result.
- A callee that stores a partial result through `p` before returning non-zero, and a caller that later
  reads it.
- A status compared with `> 0` and `< 0` for warnings versus errors: the set is not a closed list of
  constants that all mean failure.

## Switching it off

`--disable-opt O0258`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt
none`; never required — with it disabled the low-level form is translated as it is.

## Interactions with other passes

runs in phase `recover` after [#952](https://github.com/Hawkynt/Polypiler/issues/952) (cleanup regions on failure branches may
already be scopes); enables native exception emission and removes out-parameters.
