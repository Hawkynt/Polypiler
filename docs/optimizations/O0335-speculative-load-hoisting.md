# O0335 — Hoist a conditional load in front of its branch when it is proven unable to fault

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0335`.

## What it does

`if (mask[i]) b[i] = a[i] * 2` keeps the load of `a[i]` inside a branch, and a branch in a loop body is
what stops if-conversion and vectorization. The load may run unconditionally if it cannot fault and
nobody can notice the extra read. A target compiler can rarely prove that; PolyIR can, because bounds,
liveness and initialization are facts of the source language. PB-Compiler plans this as speculative
load hoisting (upstream O0173) and names the fault model as the hard part. PRD §4.8, §17.1 (trap
equivalence).

## Example

### Before

```
body:
  %m = Load.u8[inbounds #1] (ElemAddr %mask, %i)
  %t = ICmp.ne.u8 %m, 0
  Br %t, then, latch
then:
  %v = Load.i16[bounds] (ElemAddr %a, %i)
  %w = Mul.i16[wrap] %v, 2
  Store.i16[bounds] (ElemAddr %b, %i), %w
  Br latch
```

### After

```
body:
  %m = Load.u8[inbounds #1] (ElemAddr %mask, %i)
  %t = ICmp.ne.u8 %m, 0
  %v = Load.i16[inbounds #2] (ElemAddr %a, %i)      // proofs { #2 bounds: "0 <= %i", "%i < 100" }
  Br %t, then, latch
then:
  %w = Mul.i16[wrap] %v, 2
  Store.i16[bounds] (ElemAddr %b, %i), %w
  Br latch
```

## When it applies

The load `X` moves from `T` to the end of its sole predecessor `H` iff (1) *it cannot fault there*: `ValueFacts.IsInBounds(address, size, end of H)` is `True` ([#93](https://github.com/Hawkynt/Polypiler/issues/93)), the provenance is not `MayBeNull` or [#91](https://github.com/Hawkynt/Polypiler/issues/91) proves the base non-null, and `ValueFacts.IsLive(site, end of H)` is `True` for every site of the provenance ([#348](https://github.com/Hawkynt/Polypiler/issues/348)); the moved access carries the proof as `inbounds #id` ([#121](https://github.com/Hawkynt/Polypiler/issues/121)); (2) *it reads initialized memory*: every site is a global, an `Alloc` with `[zero]`, or [#86](https://github.com/Hawkynt/Polypiler/issues/86) shows on every path to the end of `H` a store that `CompletelyOverwrites` the element or an earlier load of it — reading memory that was never written aborts the reference interpreter and is undefined in several source languages; (3) the region is not volatile, not a `Device` channel, not accessed atomically, and no instruction of the function carries a `Synchronize` effect on it ([#114](https://github.com/Hawkynt/Polypiler/issues/114)): an extra read of a device register is an effect, an extra read of memory another thread writes is a data race the source did not have; (4) every operand of the address chain is available in `H`, and each moved address instruction is itself proven unable to trap ([#115](https://github.com/Hawkynt/Polypiler/issues/115)); (5) between the new and the old position there is only the branch, so no write can intervene; (6) `T` and `H` are in the same innermost loop and the loop's element type has a vector form on the target: `ICostModel.IsAvailable("simd")` ([#155](https://github.com/Hawkynt/Polypiler/issues/155)) — without one the extra load on the not-taken path buys nothing and the rewrite does not fire.

## How it is proven

`range-facts+alias-facts+effect-facts`: a load that cannot trap, reads initialized, non-volatile, unshared memory and whose result is used only where the original load was executed has no observable effect on the other path. No solver, no timeout path.

## When it must not fire

- `if (i < n) x = a[i]`: the branch *is* the bounds check; hoisting the load reads `a[n]` and traps `IndexOutOfRange` where the source did not.
- `if (p != null) x = p.value` with `p = null`: the hoisted load traps `NullReference`.
- `if (ready) x = Load.u8[unchecked, volatile] %uart_data`: reading the data register consumes a byte from the FIFO even when `ready` is false.
- `if (valid[i]) x = buf[i]` where `buf` is uninitialized beyond the valid prefix: the source never reads garbage, the hoisted load does.
- A buffer released on the path where the condition is false: `IsLive` is `False` at the new position.
- A lock-protected field read only under `if (owner == me)`: the unconditional read races with the writer.

## Targets and cost gate

Pays off only on targets whose profile offers a vector form for the loop (`simd` in `cost.yaml`): there the hoisted load is what lets [#510](https://github.com/Hawkynt/Polypiler/issues/510) turn the body into masked operations. On scalar-only targets — 6502, tokenized BASIC, PowerBASIC for DOS — and at `--opt size` the pass does nothing, and generated source keeps the load where the programmer wrote it. A target compiler behind a source backend cannot do this itself: it sees neither the bounds proof nor the initialization fact.

## Switching it off

`--disable-opt O0335`; active at `--opt balanced` and `speed`, off at `--opt size`, `memory` and `none`.

## Interactions with other passes

runs in phase `optimize` after [#508](https://github.com/Hawkynt/Polypiler/issues/508) and [#502](https://github.com/Hawkynt/Polypiler/issues/502) (which produce the in-bounds proofs) and before [#510](https://github.com/Hawkynt/Polypiler/issues/510) in `specialize`; enables if-conversion and [#158](https://github.com/Hawkynt/Polypiler/issues/158) of the now dominating load.
