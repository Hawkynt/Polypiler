# O0066 — Generate guarded specializations with a valid fallback path

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#778](https://github.com/Hawkynt/Polypiler/issues/778). Once implemented it is switched off with `--disable-opt O0066`.

## What it does

An exported `function sum(a)` can be called with anything, yet nearly every real call passes a
`Uint8Array`. Proving that is impossible; checking it once at entry is cheap. A guarded specialization runs
the typed fast path when the check passes and the original dynamic semantics when it does not — PRD §4.6:
"guarded paths must keep a valid fallback". The fallback is what distinguishes a guard from an assumption:
no input may behave differently than before. PRD §8 P4.

## Example

### Before

```
@sum(%a: dyn) -> dyn:                 // exported; fact %a : FixedSequence(u8) [Guarded: IsUint8Array(%a)]
  ...dynamic loop: s = ToInt32(s + a[i]) ...
```

### After

```
@sum(%a: dyn) -> dyn:
entry:
  %g = Guard.IsUint8Array %a                  // effect-free
  Br %g, fast, fallback
fast:
  %r = Call.i32 @sum.fast.u8((Dyn.Unbox.seq<u8> %a))     // typed loop: Add.i32[wrap], Load.u8
  Ret.dyn (Dyn.Box.dyn %r)
fallback:
  %d = Call.dyn @sum.generic(%a)                  // the original body, unchanged
  Ret.dyn %d
```

## When it applies

(1) Every guard predicate is effect-free and cannot trap ([#80](https://github.com/Hawkynt/Polypiler/issues/80)): it must not invoke getters,
proxies, `__instancecheck__`, overloaded operators or tied `FETCH`; predicates are restricted to the
intrinsic tests the language rules register as such. (2) Stability: the property tested cannot change
during the fast version — immutable type tags (a typed array's kind, an exact built-in type) are stable;
properties of mutable contents ("all elements are integers") are stable only if the fast version contains
no instruction that can modify the object ([#114](https://github.com/Hawkynt/Polypiler/issues/114)) — otherwise no guarded version is made. (3) The
fast version is derived from the same IR under the guarded facts; whenever the guard holds, fast and
fallback are equivalent — this is the obligation the differential measurement checks. (4) The fallback is
the original body with full dynamic semantics; it is never weakened, removed or replaced by a trap.
(5) The target profile can express the fallback (has a dynamic representation for the parameter); if not,
the pass does nothing for that function. (6) Guards are evaluated in parameter order, each at most once per
call, before any effect of the function. (7) [#492](https://github.com/Hawkynt/Polypiler/issues/492) grants the version and [#155](https://github.com/Hawkynt/Polypiler/issues/155) estimates
the fast path cheaper including the guard.

## How it is proven

`effect-facts` for guard purity and stability; the equivalence of fast and fallback under the guard is inherited from the proofs of the specializing passes (O0060–O0065), whose facts are exactly the guard's conclusion. No solver, no timeout path.

## When it must not fire

- `sum([1.5, 2])`: the fallback computes `(0 + 1.5) | 0 = 1`, then `(1 + 2) | 0 = 3`; a guard that only tested `a.length` would run the byte loop.
- `sum(['a'])` is 0 in JavaScript (`'0a' | 0`); `sum([1, , 3])` is 3. Both must come from the fallback.
- A guard written as `Array.isArray(a) && a.every(Number.isInteger)` calls into user-observable machinery on proxies and costs O(n).
- Guarding "list of ints" in Python and then calling an unknown function that appends a string to the list inside the fast loop.
- Replacing the fallback by `throw new TypeError(...)`: callers that passed plain arrays used to get an answer.

## Switching it off

`--disable-opt O0066`; active at `--opt balanced` and `speed`, off at `none`, `size` and `memory`. With it off, `Guarded` facts are treated as `Dynamic`.

## Interactions with other passes

runs in phase `specialize` after [#253](https://github.com/Hawkynt/Polypiler/issues/253) and the language specializations (it invokes them on the fast clone); enables every typed optimization on exported dynamic entry points. [#254](https://github.com/Hawkynt/Polypiler/issues/254) and its siblings consume the established conditions.
