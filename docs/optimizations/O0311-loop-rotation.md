# O0311 — Rotate top-tested loops into a zero-trip guard and a bottom-tested loop

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0311`.

## What it does

A top-tested loop executes two branches per iteration — the exit test and the jump back — and its body is
not guaranteed to run, which is why [#508](https://github.com/Hawkynt/Polypiler/issues/508) may hoist a possibly trapping instruction only out of a loop
"in rotated form". Testing the zero-trip case once in a guard and repeating the test at the bottom removes
the jump and gives every later loop pass a body that dominates the exit. PB-Compiler ships this as
`MachineLoopRotation` and describes it twice: as guard hoisting (O0116) and as the rotation third of O0062;
both are this one rewrite. PRD §4.8.

## Example

### Before

```
pre:
  Br head
head:
  %i = Phi.u32 [pre: 1], [body: %i2]
  %s = Phi.u32 [pre: 0], [body: %s2]
  %c = ICmp.ule.u32 %i, %n
  Br %c, body, exit
body:
  %s2 = Add.u32[wrap] %s, %i
  %i2 = Add.u32[wrap] %i, 1
  Br head
exit:
  Ret.u32 %s
```

### After

```
pre:
  %g = ICmp.ule.u32 1, %n
  Br %g, body, exit
body:
  %i  = Phi.u32 [pre: 1], [body: %i2]
  %s  = Phi.u32 [pre: 0], [body: %s2]
  %s2 = Add.u32[wrap] %s, %i
  %i2 = Add.u32[wrap] %i, 1
  %c  = ICmp.ule.u32 %i2, %n
  Br %c, body, exit
exit:
  %r = Phi.u32 [pre: 0], [body: %s2]
  Ret.u32 %r
```

## When it applies

(1) The loop has one latch; the header ends in a conditional branch with one successor inside
and one outside the loop, and no other block of the loop exits. (2) The header holds at most 16 non-phi
instructions. Nothing else is required of them: a top-tested loop with `N` iterations evaluates its header
`N + 1` times in the order `H B H B … H`, and the rotated loop evaluates guard, body, bottom test in exactly
that order and number — so header instructions with effects or possible traps keep their position in the
trace ([#114](https://github.com/Hawkynt/Polypiler/issues/114)). (3) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the duplicated header under the objective. (4) On an
`emits: source` profile ([#262](https://github.com/Hawkynt/Polypiler/issues/262)) the rewrite is offered at `--opt speed` only.

## How it is proven

`structural`: the dynamic instruction sequence is unchanged; only the block that holds each evaluation of the header differs. No solver, no timeout path.

## When it must not fire

- A rotation without the guard: `n = 0` executes the body once.
- A body use of a header-defined value without the new phi: the first iteration would read the latch copy, which has not executed yet.
- `while (next(&x)) { … }` with the call in the header: it must still be called `N + 1` times — a rotation that drops the guard call, or evaluates it twice on entry, changes the stream position.
- A loop with a `break`: the body no longer dominates every exit, and passes relying on rotated form would be misled.
- Two back edges (`continue` in the middle): copying the test into one latch leaves the other path untested.

## Targets

Machine-code and assembly backends at every optimizing level except `size`/`memory` where the
cost model refuses the extra compare: one branch per iteration is saved on every CPU. For source-emitting
targets the rotated loop structures as `IfThen` + `Loop[PostTest]` ([#262](https://github.com/Hawkynt/Polypiler/issues/262)) and prints as
`if (c) do { … } while (c)`; mature target compilers rotate on their own, so those profiles are rotated only
at `--opt speed`, where the user asked for the form that lets [#508](https://github.com/Hawkynt/Polypiler/issues/508) hoist more. [#155](https://github.com/Hawkynt/Polypiler/issues/155) ranks
`top-tested` against `rotated` (one unconditional branch less per iteration, one copy of the header more in
size), original first.

## Switching it off

`--disable-opt O0311`; active at `--opt balanced` and `speed` on profiles that are not `emits: source`, and at `size` and `memory` there when the cost model approves; at `--opt speed` only on `emits: source` profiles; off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` before [#508](https://github.com/Hawkynt/Polypiler/issues/508), `OPT-LOOP-DEADSTORE`, [#513](https://github.com/Hawkynt/Polypiler/issues/513) and [#510](https://github.com/Hawkynt/Polypiler/issues/510); `OPT-LOOP-COUNTDOWN` requires it; [#164](https://github.com/Hawkynt/Polypiler/issues/164) removes a guard whose condition is constant.
