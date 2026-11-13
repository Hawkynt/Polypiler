# O0248 — Lower exception propagation to handler frames with a non-local jump

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#757](https://github.com/Hawkynt/Polypiler/issues/757). Once implemented it is switched off with `--disable-opt O0248`.

## What it does

Status-code propagation tests a result after every call that may raise. Targets with a structured jump — C's
`setjmp`/`longjmp`, BASIC's `ON ERROR GOTO` — can do without those tests: a handler registers itself and a
raise jumps to the innermost one. The assignment of the lowering strategy is a cost decision, and for BASIC
targets it is also the direction "exceptions into `ON ERROR`" of PRD §30.1's error-recovery row.
[#497](https://github.com/Hawkynt/Polypiler/issues/497) names these as emission styles it does not define.

## Example

### Before

```
@g(%a: i32, %b: i32) -> i32:
body: try(h)
       %r = Call.i32 @f(%a, %b) unwind h
       Ret.i32 %r
h:     %e = Landing.exception [DivideByZero]
       Ret.i32 -1
```

### After

```
@g(%a: i32, %b: i32) -> i32:
entry: Frame.Push h
       %r = Call.i32 @f(%a, %b)                 ; no status test: @f raises with Frame.Raise
       Frame.Pop
       Ret.i32 %r
h:     Frame.Pop
       %s = Load.u16 @ply_exc_status
       %m = ICmp.eq.u16 %s, 1                   ; DivideByZero
       Br %m, take, pass
take:  Ret.i32 -1
pass:  Frame.Raise
```

## When it applies

(1) Pushes and pops are balanced on every path: a frame pushed by a region is
popped exactly once, on normal exit, on entry to its handler, or by the raise that passes through it. (2)
Every SSA value that is defined after `Frame.Push` and read in the handler lives in a memory slot written
before any instruction that may raise. (3) A raise with an empty frame stack calls `ply_trap(status)`. (4)
The frame stack has the capacity the profile states (`maxHandlerDepth`, default 32); exceeding it traps
`StackOverflow`. (5) No frame is pushed for a function that neither handles nor cleans up.

## How it is proven

`structural+effect-facts`, identical in obligation to O0078: the effect log up to the
failure is unchanged and the same handler runs; validated by running every fixture under both strategies
with a raise forced at every site.

## When it must not fire

- Keeping a counter in an SSA value across the region: after a `longjmp` the handler would read the value
  from before the region (ISO C §7.13.2.1: non-volatile automatic objects are indeterminate).
- Leaving the frame pushed on a normal return: the next raise anywhere jumps into a dead frame.
- Running a `finally` only on the normal path because "the jump goes straight to the handler".
- PowerBASIC: leaving `ON ERROR GOTO` active while the handler runs — an error inside the handler would
  re-enter it.

## Switching it off

`--disable-opt O0248`; an alternative to O0078 on targets with `NonLocalJump`, considered at
every `--opt` level; disabled, status propagation is used.

## Interactions with other passes

runs after [#758](https://github.com/Hawkynt/Polypiler/issues/758) and instead of the propagation half of [#497](https://github.com/Hawkynt/Polypiler/issues/497);
requests the helper pair `ply_frame_push`/`ply_frame_raise` only on targets whose backend does not map the
operations natively.
