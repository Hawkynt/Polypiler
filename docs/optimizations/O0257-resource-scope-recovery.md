# O0257 — Recover resource scopes from acquire/cleanup paths and try/finally disposal

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#952](https://github.com/Hawkynt/Polypiler/issues/952). Once implemented it is switched off with `--disable-opt O0257`.

## What it does

PRD §25.1 (backward): "Recognize compiler-generated `try/finally` disposal and related patterns from C#
source, MSIL or lifted code only if acquisition and disposal semantics match … `try/finally` containing
`Dispose()` is not automatically C# `using`." §30.4 (RES-001) lists what must be proven: acquisition,
exactly-once disposal, null behaviour, normal and exceptional exits, cleanup order and cleanup errors. §28
asks for separate positive and negative proof tests for recovered `try/finally`. This is the rediscovery of
`using` blocks for deterministic disposal.

## Example

### Before

```
       %r = Call.ref<Res> @Res.new(1)
       Br body
body: try(fin)
       Call.void @Log("body")
       Br after
fin:   %e = Landing.exception []
       %n = PtrIsNull.bool %r
       Br %n, rethrow, disp
disp:  Call.void @Res.Dispose(%r)
       Br rethrow
rethrow: Resume %e
after: ; the same null test and Dispose call on the normal path
```

### After

```
       %r = Call.ref<Res> @Res.new(1)
       %s = Scope.Enter.scope %r, @Res.Dispose [policy=csharp-using]
body: try(cleanup)
       Call.void @Log("body")
       Scope.Exit %s
```

## When it applies

obligations (a)–(f) under *In scope*, discharged as alias and effect facts and
by path enumeration over the function's control flow (at most 4 096 paths; beyond that the rule does not
fire); plus: (g) the normal-path and exceptional-path releases are the same call with the same operand;
(h) the release function is the one the preset's `dispose` attribute names for the resource's shape.

## How it is proven

`validated-rule+differential` with the listed fact obligations (reported as
`structural+interpreter` plus the fact classes): each candidate is re-lowered with O0250 and O0251 and the
result is compared with the original fragment on [#119](https://github.com/Hawkynt/Polypiler/issues/119) over the rule's input generator; any
difference, any `Unknown` fact or a budget expiry leaves the IR unchanged.

## When it must not fire

- `finally { r.Dispose(); Log("done"); }`: turning it into `using` drops or reorders the log call.
- `r` reassigned inside the `try` (`r = Reopen(r)`): `using` would dispose the first value; the source
  disposes the second.
- `finally { if (ok) r.Dispose(); }`: disposal is conditional; a scope always releases.
- C ladder in which the success path returns `p` to the caller and only the error path frees it:
  recovering a scope would free memory the caller uses.

## Switching it off

`--disable-opt O0257`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt
none`; never required — with it disabled the low-level form is translated as it is.

## Interactions with other passes

runs in phase `recover` after control-flow structuring; enables native `using`/RAII
emission through [#767](https://github.com/Hawkynt/Polypiler/issues/767)'s native match.
