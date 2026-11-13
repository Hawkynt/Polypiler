# O0325 — Compute the pure stage of a loop in parallel blocks ahead of its ordered effect stage

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0325`.

## What it does

`for i: v = parse(x[i]); emit(v)` spends its time in `parse` and must call `emit` in order. [#958](https://github.com/Hawkynt/Polypiler/issues/958)
refuses the loop because of the call with effects, and `OPT-LOOP-DISTRIBUTE` refuses it because the value
crosses the split without an element to carry it. Giving the value a buffer solves both: compute a block of
`parse` results in parallel, then run `emit` over the block in order. PB-Compiler plans this as pipeline
parallelization and states the constraint: only work that is not itself observable may be reordered.
PRD §4.8, §8 P5.

## Example

### Before

```
body:                                    // i = 0 .. n-1
  %x = Load.u32[inbounds #1] (ElemAddr.ptr<u32>[inbounds #1] %src, %i)
  %v = Call.u32 @parse(%x)               // pure, trap-free, expensive
  Call.void @emit(%v)                    // ordered effect
  %i2 = Add.u32[wrap] %i, 1
  ...
```

### After

```
%buf = Alloc.ptr<u32>[stack] B
// base = 0, B, 2B, ... while base < n;  %len = UMin.u32 B, (Sub.u32[wrap] %n, %base)
ParallelFor[chunks=contiguous] %k in [0, %len) {
  %x = Load.u32[inbounds #1] (ElemAddr.ptr<u32>[inbounds #1] %src, (Add.u32[wrap] %base, %k))
  %v = Call.u32 @parse(%x)
  Store.u32[inbounds #2] (ElemAddr.ptr<u32>[inbounds #2] %buf, %k), %v
}
// k = 0 .. %len - 1, sequential:
%w = Load.u32[inbounds #2] (ElemAddr.ptr<u32>[inbounds #2] %buf, %k)
Call.void @emit(%w)
```

## When it applies

(1) The loop is a single-block counted loop with one exit and a trip count available before
entry ([#350](https://github.com/Hawkynt/Polypiler/issues/350)). (2) The pure stage has no effect ([#114](https://github.com/Hawkynt/Polypiler/issues/114), [#80](https://github.com/Hawkynt/Polypiler/issues/80) for calls: pure and
deterministic), cannot trap ([#115](https://github.com/Hawkynt/Polypiler/issues/115)) and reads only non-volatile memory ([#121](https://github.com/Hawkynt/Polypiler/issues/121)): within a
block it runs for later iterations before the effect stage of earlier ones, so a trap or effect in it would
move in front of them. (3) Nothing the effect stage may write — directly or through its calls
([#80](https://github.com/Hawkynt/Polypiler/issues/80), [#82](https://github.com/Hawkynt/Polypiler/issues/82)) — is read by the pure stage; `Unknown` blocks. (4) The effect stage uses
the pure stage's results only through the values that are buffered (at most four) and uses no other value
the pure stage defines. (5) The effect stage runs in the original order with the original values; it may
have any effect and may trap — a trap in iteration `k` happens after the effects of iterations before `k`,
as before, and the unused buffered results of later iterations are unobservable. (6) The buffers are
private stack storage of `B` elements. (7) The target has `Threads` ([#259](https://github.com/Hawkynt/Polypiler/issues/259)) and [#155](https://github.com/Hawkynt/Polypiler/issues/155)
prefers `ThreadedCost` of the pure stage per block plus the sequential effect stage to the original loop.

## How it is proven

`structural+alias-facts+effect-facts`: under (2)–(4) the pure stage of any iteration commutes with every other action in its block, so computing it early and in any order is unobservable; the effect stage is unchanged. No solver, no timeout path.

## When it must not fire

- `v = lut[x[i]]; lut[v & 15] = v`: the effect stage writes the table the pure stage reads; computing a block ahead uses stale entries.
- `v = 1000 / x[i]; emit(v)` with `x[2] = 0`: the source emits two values and then traps; the block form traps in the parallel stage before anything is emitted.
- A pure stage that reads a volatile timer: its value depends on when it runs.
- `emit` may `break` out of the loop on a full buffer: the block has already consumed inputs the source never touched — harmless only when the pure stage is truly pure, and the exit bookkeeping is not planned.
- A producer `raw = read(i)` with I/O in front of the pure stage: reading a whole block first reorders reads against writes.

## Targets

Only targets with the `Threads` capability and a `threads` entry in the cost profile, and only
where the pure stage is expensive enough to outweigh thread hand-off per block — the cost model decides and
picks `B`. Never on single-threaded and retro targets. The emitted source is longer and carries a buffer;
it is accepted at `--opt speed` only.

## Switching it off

`--disable-opt O0325`; active at `--opt speed` on targets with `Threads`; off at `none`, `balanced`, `size` and `memory`.

## Interactions with other passes

runs in phase `specialize` after `OPT-LOOP-DISTRIBUTE` and before [#958](https://github.com/Hawkynt/Polypiler/issues/958); uses [#958](https://github.com/Hawkynt/Polypiler/issues/958)'s `ParallelFor`; [#159](https://github.com/Hawkynt/Polypiler/issues/159) and [#160](https://github.com/Hawkynt/Polypiler/issues/160) must have run so that the pure stage's calls have summaries.
