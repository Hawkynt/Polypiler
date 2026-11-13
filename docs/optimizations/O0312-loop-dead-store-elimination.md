# O0312 — Sink a store that every next iteration overwrites unread to the loop exit

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0312`.

## What it does

`last = a[i]` in a loop writes `last` a thousand times and only the final value can ever be seen. When `last`
is a local, SSA construction already removes the stores; when it is a global, a field, a by-reference
parameter or an array element with a fixed index, the store stays in the loop and keeps the value out of a
register. [#504](https://github.com/Hawkynt/Polypiler/issues/504) cannot remove it — on the path that leaves the loop the store is live. Storing once
after the loop is equivalent if nothing can observe the intermediate contents. PB-Compiler plans this as
loop dead-store elimination. PRD §4.8, §17.2.

## Example

### Before

```
body:
  %i  = Phi.u32 [pre: 0], [body: %i2]
  %x  = Load.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] %a, %i)
  Store.u16[inbounds #2] @last, %x
  %i2 = Add.u32[wrap] %i, 1
  %c  = ICmp.ult.u32 %i2, %n
  Br %c, body, exit
exit:
  Ret.void
```

### After

```
body:
  %i  = Phi.u32 [pre: 0], [body: %i2]
  %x  = Load.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] %a, %i)
  %i2 = Add.u32[wrap] %i, 1
  %c  = ICmp.ult.u32 %i2, %n
  Br %c, body, exit
exit:
  Store.u16[inbounds #2] @last, %x
  Ret.void
```

## When it applies

(1) The loop is bottom-tested with one latch whose branch is the only exit, so the exit is
reached only after at least one complete iteration. (2) The store's address is loop-invariant, its block
dominates the latch, and the store itself cannot trap (`[inbounds]`/`[unchecked]`, or a fact). (3) No
instruction of the loop may read the stored location: no load that may alias it ([#82](https://github.com/Hawkynt/Polypiler/issues/82); `MayAlias`
and `Unknown` block), no call whose summary may read it ([#80](https://github.com/Hawkynt/Polypiler/issues/80)), no other store that partially
overlaps it. (4) No instruction of the loop may trap ([#115](https://github.com/Hawkynt/Polypiler/issues/115), facts from [#94](https://github.com/Hawkynt/Polypiler/issues/94)) or unwind:
after a trap in iteration `k` the location would hold the value of iteration `k − 1` in the source and its
old value after sinking. (5) The location is ordinary program storage ([#121](https://github.com/Hawkynt/Polypiler/issues/121) classification) — not
volatile, device, or shared with another thread. (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the rewrite; on an `emits: source`
profile ([#262](https://github.com/Hawkynt/Polypiler/issues/262)) it is offered at `--opt speed` only.

## How it is proven

`structural+alias-facts+effect-facts+range-facts`: under (3)–(5) the location's contents between two iterations are unobservable, and (1)–(2) make the last stored value the one stored at the exit. No solver, no timeout path.

## When it must not fire

- `prev` stored each iteration and loaded at the start of the next (`d[i] = a[i] − prev`): the intermediate value is the point.
- The loop calls `log()` which prints the global: each call sees the current value.
- `x / d` later in the body traps in iteration 3: the source leaves `last = a[2]`; after sinking it still has its old value.
- `*p = a[i]` where `p` may point into `a`: the load of `a[i]` may read what the previous iteration stored.
- A top-tested loop with `n = 0`: the source stores nothing; an unconditional store after the loop writes garbage.
- A memory-mapped data register written once per byte: every store is output.

## Targets

Machine-code and assembly backends, where the store is a memory write per iteration and the
value can stay in a register instead. On source targets the rewrite introduces a carried temporary; mature
C-family compilers promote such locations themselves when they can prove the same facts, so `emits: source`
profiles get it at `--opt speed` only — where it also helps targets without an optimizer (PowerBASIC,
tokenized BASIC). [#155](https://github.com/Hawkynt/Polypiler/issues/155) ranks `store per iteration` (`LoopCost` with the store) against `store at
exit`, original first.

## Switching it off

`--disable-opt O0312`; active at `--opt balanced`, `speed`, `size` and `memory` on profiles that are not `emits: source`; at `--opt speed` only on `emits: source` profiles; off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after `OPT-LOOP-ROTATE`, [#502](https://github.com/Hawkynt/Polypiler/issues/502) (trap-free accesses) and [#160](https://github.com/Hawkynt/Polypiler/issues/160) (call summaries), before [#510](https://github.com/Hawkynt/Polypiler/issues/510) and `OPT-LOOP-DEAD` (a loop whose only effect was the store may now be empty or closable).
