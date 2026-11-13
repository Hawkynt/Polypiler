# O0007 — Replace conservative call effects by proven callee summaries

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#160](https://github.com/Hawkynt/Polypiler/issues/160). Once implemented it is switched off with `--disable-opt O0007`.

## What it does

A call is the widest effect in the IR: without knowledge of the callee it may read and write everything, so
every fact about memory dies at it and nothing moves across it. Most callees in algorithm code do far less —
they read one buffer, or nothing. PRD §4.8 lists interprocedural effects as a required pass, and §17.2 demands
that effects be precise data dependencies rather than "giant barriers".

## Example

### Before

```
@sum(%p: ptr<u8>, %n: u32) -> u32:        // loop reading %p[0 .. %n-1]
  ...
@top(%buf: ptr<u8>, %out: ptr<u32>):
  %s = Call.u32 @sum(%buf, 16)   // effects: Memory.Unknown, External.Call
  Store.u32 %out, %s
```

### After

```
@top(%buf: ptr<u8>, %out: ptr<u32>):
  %s = Call.u32 @sum(%buf, 16)   // effects: Memory.Read(region(%buf)), MayTrap
  Store.u32 %out, %s
```

## When it applies

A call's annotation is replaced only when the call is direct and the callee's summary is complete. A summary
is the join of the effects of all instructions of the function, where a nested direct call contributes its
callee's summary and a nested indirect or external call contributes its own annotation unchanged. Within an
SCC all members start at the empty summary and are iterated to the least fixed point (the lattice is finite:
effect classes × {parameter regions, global regions, `Unknown`}). Effects on allocations that do not outlive
the callee are dropped from the summary. A function containing an instruction with an unmodelled effect
(inline assembly without a contract, PRD §17.4) has the summary `Unknown` and its callers are not rewritten.
`MayTrap` is set when any reachable instruction may trap; `MayDiverge` when the function contains a loop or
recursion not proven finite.

## How it is proven

`effect-facts`: the summary is an over-approximation by construction (join of over-approximations); the soundness measurement below checks it empirically. No solver, no timeout path.

## When it must not fire

- A callee that writes through a pointer parameter that the caller passes pointing at a global: the summary must name the *argument's* region at the call site, not "parameter 0".
- A callee that calls an unknown external function: its summary keeps `External.Call`; marking it pure because its own instructions are pure is wrong.
- Mutual recursion `even`/`odd` where only `odd` writes a global: both summaries must contain the write.
- A pure function that divides: without `MayTrap`, [#156](https://github.com/Hawkynt/Polypiler/issues/156) would delete an unused call and with it a division by zero.
- A pure function with an unbounded loop: without `MayDiverge` an unused call could be deleted and a hang turned into progress.

## Switching it off

`--disable-opt O0007`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs first in phase `optimize`, before [#159](https://github.com/Hawkynt/Polypiler/issues/159), and again after inlining changed bodies; enables [#162](https://github.com/Hawkynt/Polypiler/issues/162), [#158](https://github.com/Hawkynt/Polypiler/issues/158) across calls, [#156](https://github.com/Hawkynt/Polypiler/issues/156) of unused pure calls, [#508](https://github.com/Hawkynt/Polypiler/issues/508) and [#502](https://github.com/Hawkynt/Polypiler/issues/502).
