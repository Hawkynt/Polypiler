# O0250 — Lower policy-carrying resource scopes to native constructs or explicit cleanup regions

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#767](https://github.com/Hawkynt/Polypiler/issues/767). Once implemented it is switched off with `--disable-opt O0250`.

## What it does

PRD §25.1 (forward): a resource scope is lowered "to target-native C# `using`, C++ RAII, Rust
ownership/`Drop` where equivalent, or explicit cleanup CFG and generated helpers for BASIC/C/assembly", and
"Rust's `Drop` on scope exit is not automatically equivalent to C# exception-initiated disposal". §30.4
(RES-001): backends without destructors generate explicit control-flow cleanup. The scope opcodes exist
([#124](https://github.com/Hawkynt/Polypiler/issues/124)) and carry dialect policies ([#479](https://github.com/Hawkynt/Polypiler/issues/479)); this rule decides, per scope and
target, whether a native construct means the same, and writes the policy out as instructions when it does
not.

## Example

### Before

```
%r = Call.ref<Res> @Res.new(1)
%s = Scope.Enter.scope %r, @Res.Dispose [policy=csharp-using]
body: try(cleanup)
       Call.void @Log("body")
       Scope.Exit %s
```

### After

```
%r = Call.ref<Res> @Res.new(1)
%s = Scope.Enter.scope void, @k$1 [null=call, onCleanupThrow=replace]      ; policy-free region
body: try(cleanup)
       Call.void @Log("body")
       Scope.Exit %s
func @k$1() -> void {                                                       ; reads %r from its frame slot
entry: %n = PtrIsNull.bool %r
       Br %n, done, call
call:  Call.void @Res.Dispose(%r)
       Br done
done:  Ret.void }
```

## When it applies

(1) `MapNative` requires equality of `null`, `onCleanupThrow`, `acquire`,
`dispose`, `suppressed`, `swallow` and `kind` between the scope and the target's native policy; one
difference selects expansion. (2) The expansion releases exactly the resource value captured at
`Scope.Enter` (the copy under `acquire=copy`). (3) The verifier rule of [#124](https://github.com/Hawkynt/Polypiler/issues/124) — exactly one
release per path, innermost first — holds after the rewrite. (4) The cleanup call keeps its effects and
position relative to the body's effects; it is never merged with another scope's cleanup. (5) The
cleanup-failure sequence implements the scope's policy and no other.

## How it is proven

`structural+interpreter`: validated by interpreting the policy-carrying scope and the
expanded region on every fixture, with a failure forced in the acquisition, the body, the cleanup and
both.

## When it must not fire

- Mapping a C# `using` to a Rust `Drop` scope without checking the policy: when body and `Dispose` both
  fail, C# propagates the cleanup's exception; a panic in `drop` during unwinding aborts the Rust process.
- Disposing the current value of the variable instead of the acquired one: a `struct` resource reassigned
  in the body would dispose the wrong value (C# disposes the copy).
- Calling the cleanup on null under `null=skip`: `using (Res r = null)` must run the body and no
  `Dispose`.
- Entering the scope before the acquisition finished: a constructor that throws would trigger `Dispose` on
  a non-existent object.

## Switching it off

`--disable-opt O0250`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(resource-scope)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs before [#759](https://github.com/Hawkynt/Polypiler/issues/759) and the exception lowering; after [#470](https://github.com/Hawkynt/Polypiler/issues/470) decided
how the resource reference is represented.
