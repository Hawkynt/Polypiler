# O0027 — Split reductions across threads when the combining operation is proven associative

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#959](https://github.com/Hawkynt/Polypiler/issues/959). Once implemented it is switched off with `--disable-opt O0027`.

## What it does

Checksums over megabytes, histogram totals, min/max scans: a reduction is the most common loop that looks
sequential and is not. If the combining operation is associative, the input can be cut into chunks, each
reduced on its own core, and the partial results combined in order. If it is not — floating-point addition,
overflow-checked addition — doing so changes results or exceptions. PRD §4.8 says exactly this: "parallel
reduction when associative".

## Example

### Before

```
%r = Reduce.u32[order=left] %seq, (λ(%a, %x): Xor.u32 %a, %x), 0
```

### After

```
%r = ParallelReduce.u32[chunks=ordered, identity=0] %seq, (λ(%a, %x): Xor.u32 %a, %x), 0
```

## When it applies

(1) The combining operation is proven associative on its type. (2) An identity element `e` with `e ⊕ x = x`
is known (so every chunk can start from `e` and the original `init` is combined first), or chunks start
from their first element and empty chunks are skipped. (3) The per-element function and the combiner have
no effects and cannot trap ([#80](https://github.com/Hawkynt/Polypiler/issues/80) summary with empty [#114](https://github.com/Hawkynt/Polypiler/issues/114) set and no `MayTrap`): an
effect would be reordered across threads and a trap in chunk 3 could surface before effects of chunk 1.
(4) The source sequence is not written during the reduction and reading its elements has no effects.
(5) `Threads` is available on the target and [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers `ThreadedCost` for the proven or assumed
element count. `[checked]`, signed `[sat]` and all floating-point combiners fail (1).

## How it is proven

`exhaustive-8bit+smt-bitvector` for associativity (built-in operations proven once; user combiners per function, 5 s budget, timeout → `not applied: proof timeout`), plus `effect-facts` for (3) and (4).

## When it must not fire

- `f64` sum of `[1e16, 1.0, -1e16, 1.0]`: sequentially 1.0; chunks `[1e16, 1.0]` and `[-1e16, 1.0]` give `1e16 + (−1e16) = 0.0`.
- `Add.i32[checked]` over `[2147483647, 1, −1]`: sequentially it traps at the second element; chunks `[2147483647]` and `[1, −1]` give 2147483647 without a trap.
- `Add.i8[sat]` over `[100, 100, −100]`: `(100 + 100) + (−100) = 127 − 100 = 27`, but `100 + (100 − 100) = 100`.
- `Sub.u32` as combiner: `(5 − 3) − 1 = 1`, `5 − (3 − 1) = 3`.
- A combiner that logs each call: the log order depends on thread scheduling.

## Switching it off

`--disable-opt O0027`; active at `--opt speed` and, when the cost model approves, `balanced`, off at `--opt none`.

## Interactions with other passes

runs in phase `specialize` after [#507](https://github.com/Hawkynt/Polypiler/issues/507) and [#510](https://github.com/Hawkynt/Polypiler/issues/510) (each chunk may itself be vectorized); the legalizer removes nothing: on a target without `Threads` the pass never fires, so no parallel construct exists.
