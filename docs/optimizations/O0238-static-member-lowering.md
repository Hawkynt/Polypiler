# O0238 — Lower static members to module storage with ordered or guarded initialization

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#481](https://github.com/Hawkynt/Polypiler/issues/481). Once implemented it is switched off with `--disable-opt O0238`.

## What it does

A static field is module storage plus a rule for *when* its initializer runs. C initializes globals with
constants only; PowerBASIC has no initializers at all. PRD §30.1 row "Static/global/thread-local members":
storage lifetime, initialization order and lazy initialization are the semantics; the fallback is module
storage with initialization guards. §32 lists "static initialization order" among the object tests.

## Example

### Before

```
shape A [kind=class, staticInit=first-access-precise] { static X: i32; init @A.cctor }
%x = Static.Load.i32 A::X
```

### After

```
region @A.X: i32 [zero]
region @A.__init: u8 [zero]
%g = Load.u8 @A.__init
%n = ICmp.eq.u8 %g, 0
Br %n, run, ready
run:   Store.u8 @A.__init, 1
       Call.void @A.cctor()
       Store.u8 @A.__init, 2
       Br ready
ready: %x = Load.i32 @A.X
```

## When it applies

(1) `eager` and the eager form of `first-access` require that the initializer
cannot trap and has no effect ([#114](https://github.com/Hawkynt/Polypiler/issues/114)) other than writing statics of its own shape; otherwise the
guard form is used. (2) A guard check is omitted only where another check of the same shape, or the
shape's own initializer, dominates the access in the same function. (3) The guard is set to 1 before the
initializer is called and to 2 after it returns; an initializer that traps leaves the guard at 1 and the
trap propagates. (4) Initializers of different shapes are never reordered relative to effectful code under
`first-access-precise`.

## How it is proven

`structural+effect-facts`: guards encode the source's initialization trigger; eager
placement rests on the effect facts of (1); validated by interpreting both forms on every fixture.

## When it must not fire

- Running all initializers eagerly in declaration order for mutually dependent classes: `A.X = B.Y + 1`,
  `B.Y = A.X + 1`, first access `A.X` gives 2 and 1 in C#; eager declaration order gives 1 and 2.
- Setting the guard to done only after the initializer returns: the cyclic access re-enters the
  initializer forever.
- Hoisting a guard check out of a loop that may run zero times: the initializer's console output appears
  although no access happened.
- Initializing a static whose initializer reads the clock at module start instead of first access.

## Switching it off

`--disable-opt O0238`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(static-storage)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs after [#470](https://github.com/Hawkynt/Polypiler/issues/470) and [#472](https://github.com/Hawkynt/Polypiler/issues/472); dominated-check elimination is part of
the rule; the remaining loads and stores are ordinary memory operations for later passes.
