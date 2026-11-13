# O0338 — Sink the stores of all branch arms to their join as one store

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0338`.

## What it does

`if (c) *p = a; else *p = b;` stores on both paths. Merged at the join it is one store of a value chosen
by the condition: smaller, and both arms become free of effects, which is what turns the diamond into a
`Select`. It is the store counterpart of if-conversion and of [#158](https://github.com/Hawkynt/Polypiler/issues/158), which merges equal values but
not stores of different values to one place. PB-Compiler plans it as store sinking (upstream O0253).
PRD §4.8.

## Example

### Before

```
then:
  Store.i32 %p, %a
  Br join
else:
  Store.i32 %q, %b          // %q MustAlias %p
  Br join
join:
  ...
```

### After

```
then:
  Br join
else:
  Br join
join:
  %m = Phi.i32 [then: %a], [else: %b]
  Store.i32 %p, %m
  ...
```

## When it applies

For a join `J` with predecessors `P1 … Pk`, `k ≥ 2`: (1) every `Pi` ends in an unconditional branch to `J` and contains a store `Si` such that all `Si` have the same type, size and access contract and pairwise `MustAlias` addresses ([#82](https://github.com/Hawkynt/Polypiler/issues/82)); (2) one address value dominating `J` exists — the common address, defined in a dominator of `J`; (3) *nothing observes the gap*: between `Si` and the end of `Pi` there is no instruction that may read the location (`MemorySsa`: no `MemoryUse` or `MemoryDef` for which the alias query is not `NoAlias`, no call with a read, write or unknown effect on the region, [#86](https://github.com/Hawkynt/Polypiler/issues/86)), no instruction that may trap or throw ([#115](https://github.com/Hawkynt/Polypiler/issues/115)) and no other instruction with an observable effect ([#114](https://github.com/Hawkynt/Polypiler/issues/114)) — a handler or the caller would otherwise see the location before it is written, or a trapping store would move behind an effect; (4) the location is not volatile, not a `Device` channel, not atomic, and no `Synchronize` effect lies between `Si` and `J`; (5) `J` has no predecessor other than `P1 … Pk`.

## How it is proven

`alias-facts+effect-facts+structural`: on every path the same value reaches the same location before any instruction that could read it or leave the function. No solver, no timeout path.

## When it must not fire

- One-armed: `if (c) *p = 1;`. Sinking needs `*p = c ? 1 : *p`, a write on the path that had none — a fault if `p` points to read-only memory when `c` is false, a lost update if another thread writes `*p`.
- `then: *p = a; log(*p);` — the call reads the location before the join; with the store sunk it logs the old value.
- `else: *p = b; x = 10 / d;` with `d = 0`: the source traps with `*p = b` already visible to a handler; sunk, the handler sees the old value.
- `*p` is a device data register: two different writes on two paths are two different effects, and their position relative to other device accesses in the arm matters.
- The arms store to `a[i]` and `a[j]` with `i`, `j` possibly different: not the same location.

## Targets and cost gate

Every target. One store replaces `k`, and nothing is added but a `Phi`; no cost component rises, so the cost model is not consulted. In generated source the result reads `*p = c ? a : b` (or the target's equivalent) once the diamond is converted, which is at least as readable as the two assignments; target compilers do the same only when their own alias analysis proves both addresses equal.

## Switching it off

`--disable-opt O0338`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#158](https://github.com/Hawkynt/Polypiler/issues/158) (which unifies the two address computations) and before [#504](https://github.com/Hawkynt/Polypiler/issues/504); enables if-conversion of the now effect-free arms, [#504](https://github.com/Hawkynt/Polypiler/issues/504) and [#510](https://github.com/Hawkynt/Polypiler/issues/510).
