# O0253 — Lower coroutines with sent values, failure and cancellation to resumable state machines

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#754](https://github.com/Hawkynt/Polypiler/issues/754). Once implemented it is switched off with `--disable-opt O0253`.

## What it does

A coroutine is more than a generator: values travel in on resume, a failure can leave through the resumer,
and a cancellation enters at the suspension point and unwinds through `finally`. PRD §30.3 (COR-001) lists
`Receive`, `Cancel`, `Dispose` and exceptional unwinding as separate events and requires that the lowering
"preserve local captures, re-entry, ordering and cleanup across every suspension point"; §31 requires each
"yield, resume, failure and disposal transition" of a Kotlin coroutine to match in Perl.

## Example

### Before

```
func @acc() -> void resumable=coroutine yields i32 receives i32 {
entry: Br loop
loop:  %t = Phi.i32 [entry: 0], [loop: %u]
       %x = Suspend.Yield.i32 %t                 ; x = yield total
       %u = Add.i32[wrap] %t, %x
       Br loop }
```

### After

```
type Gen_acc = record { state: u16, t: i32 }
func @acc$next(%g: ptr<Gen_acc>, %sent: i32) -> step<i32, void> {
entry: Switch.u16 (Load.u16 (FieldAddr.ptr<u16> %g, state)), bad, [0: start], [1: resume1]
start: Store.i32 (FieldAddr.ptr<i32> %g, t), 0
       Br yield
resume1: Store.i32 (FieldAddr.ptr<i32> %g, t), (Add.i32[wrap] (Load.i32 (FieldAddr.ptr<i32> %g, t)), %sent)
       Br yield
yield: Store.u16 (FieldAddr.ptr<u16> %g, state), 1
       Ret (Step.Yielded (Load.i32 (FieldAddr.ptr<i32> %g, t)))
bad:   Trap.InvalidOperation }
```

## When it applies

(1) All preconditions of O0252. (2) The value read by `Suspend.Yield` after
re-entry is the argument of the call that re-entered; the argument of the first call is ignored and must
be the type's zero (Python requires `None` there — a non-zero first send is diagnosed where the source
dialect forbids it). (3) A handler region spanning a suspension point is entered again on re-entry before
the code after the yield runs. (4) `$cancel` under `exception(<label>)` raises at the suspension point,
inside every region that enclosed it; if the body yields again during cancellation the dialect's rule
applies (Python: `RuntimeError`). (5) No path runs a cleanup for a coroutine that was merely dropped.

## How it is proven

`structural+interpreter`: validated by interpreting the resumable function and the
lowered form on every fixture, comparing event traces for resume, send, failure, cancel and abandonment.

## When it must not fire

- Running pending `finally` blocks when the state record goes out of scope: a Kotlin `sequence` iterator
  that is dropped after one element never runs its `finally`; the translation would print an extra line.
- Implementing Python `close()` as "mark finished": the `finally` around the `yield` must run and may
  observe `GeneratorExit`.
- Delivering a sent value one step late — returning it from the *next* yield.
- Leaving the handler region on suspension and not re-entering it: an exception after the resume would
  escape a `try` that textually encloses it.

## Switching it off

`--disable-opt O0253`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(coroutine)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

extends [#760](https://github.com/Hawkynt/Polypiler/issues/760) and runs with it; uses the handler lowering of
[#758](https://github.com/Hawkynt/Polypiler/issues/758) for failure and cancellation by exception.
