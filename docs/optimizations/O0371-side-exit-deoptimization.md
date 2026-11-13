# O0371 — Leave a speculatively narrowed loop through a side exit that resumes the general loop mid-iteration

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0371`.

## What it does

`OPT-PROP-SPECNARROW` can guard only values known before the loop. When the unbounded value is read *in* the
loop — an array element that is small in all but a few cases — the assumption can only be tested per iteration,
and it can fail in the middle of one, after part of the iteration's work is done. Restarting the general loop at
its header would repeat that work. The side exit enters a copy of the *rest* of the current iteration, seeded
with the values computed so far, and continues in the general loop from the next iteration on. PRD §4.6
(guarded paths must keep a valid fallback).

## Example

### Before

```
head:
  %i = Phi.u32 [pre: 0], [latch: %n]
  %s = Phi.i32 [pre: 0], [latch: %s2]
  %c = ICmp.ult.u32 %i, %len
  Br %c, body, exit
body:
  Call.void @tick()
  %v  = Load.i32 %a, %i
  %p  = Mul.i32[wrap] %v, 3
  %s2 = Add.i32[wrap] %s, %p
  Br latch
latch:
  %n = Add.u32[wrap] %i, 1
  Br head
```

### After

```
pre:
  Br head.fast
body.fast:
  Call.void @tick()
  %v.f = Load.i32 %a, %i.f
  %g1  = ICmp.sge.i32 %v.f, -10922
  %g2  = ICmp.sle.i32 %v.f, 10922
  %g   = And.bool %g1, %g2
  Br %g, rest.fast, resume
rest.fast:                                     // narrowed by O0016: Mul.i16
  ...
  Br latch.fast
resume:                                        // rest of this iteration, general width
  %p.r  = Mul.i32[wrap] %v.f, 3
  %s2.r = Add.i32[wrap] %s.f, %p.r
  %n.r  = Add.u32[wrap] %i.f, 1
  Br head
head:                                          // general loop; phis gain [resume: %n.r], [resume: %s2.r]
  ...
```

## When it applies

(1) The loop has one entry, one latch, and its only exit is the header test; ignoring the back edge
its body is acyclic; no block of it is a handler entry and it lies in one exception region. (2) The guarded
value `v` is defined in a block that dominates the latch, so the guard executes in every iteration. (3) The
guard interval of `v` and the profitability rule are those of `OPT-PROP-SPECNARROW`, with `v` in the place
of the invariant and the other operands ranged or literal. (4) The block of `v` is split directly after `v`;
everything before the split belongs to the *prefix* and is executed exactly once per iteration by whichever
version runs it; the resume suffix contains only instructions after the split. (5) Every value of the fast
clone that the suffix or the general header needs is defined in the prefix or in the fast header and
therefore dominates the side exit; the suffix is seeded by substitution, the general header's phis receive
the suffix's latch values. (6) Values live after the loop are merged at the exit from the fast and the general
header. (7) Fast loop plus resume suffix have at most 192 instructions; [#492](https://github.com/Hawkynt/Polypiler/issues/492) grants the reservation
with one guard; [#155](https://github.com/Hawkynt/Polypiler/issues/155) scores fast body plus the per-iteration test strictly below the general body.
(8) The general loop is reachable only through the side exit and is otherwise unchanged.

## How it is proven

`structural+range-facts`: prefix, guard and suffix together execute exactly the instructions of one general iteration, in order, on the same values; later iterations run the unmodified loop; the narrowing in the fast remainder carries O0016's proof under the guard-edge fact. No solver in this pass; a refused shape leaves the loop untouched.

## When it must not fire

- Re-entering the general loop at its header after a failed guard: `@tick()` of the failing iteration runs twice.
- A guard in a block that does not dominate the latch: iterations that skip it would run the narrow remainder unchecked.
- A loop with a `break`: the fast clone's second exit needs its own state merge; the primitive declines.
- Seeding the suffix with the general loop's values instead of the fast clone's: the accumulator restarts from its initial value.
- A resume suffix that includes the load of `v`: a volatile element is read twice in one iteration.

## Targets

as `OPT-PROP-SPECNARROW`: only targets whose cheap register width is below the operation's width.
The loop body exists up to three times, so the rewrite is confined to `--opt speed` and to profiles on which
the narrow form is cheaper by more than the per-iteration test; source targets with native wide integers never
see it.

## Switching it off

`--disable-opt O0371`; active at `--opt speed` only; off at `balanced`, `size`, `memory` and `none`.

## Interactions with other passes

runs in phase `specialize` after `OPT-PROP-SPECNARROW` (which takes loops whose guard can sit in the preheader) and before [#161](https://github.com/Hawkynt/Polypiler/issues/161); the fast loop, free of wide operations, is open to the other passes of the phase.
