# O0324 — Parallelize prefix-scan loops in two passes over ordered chunks

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0324`.

## What it does

A running total — `t[i] = t[i-1] + a[i]` — is the textbook example of a loop that cannot be parallelized:
every element depends on the one before. It is nevertheless a *scan*, and a scan with an associative
operation splits: each worker reduces its slice, the slice totals are chained into offsets, and each worker
then scans its slice starting from its offset. [#958](https://github.com/Hawkynt/Polypiler/issues/958) rejects the loop (carried dependence) and
[#959](https://github.com/Hawkynt/Polypiler/issues/959) handles reductions, not scans. PB-Compiler implements the recognition and plans the
two-pass execution. PRD §4.8, §8 P5.

## Example

### Before

```
body:                                    // i = 1 .. n-1
  %im   = Sub.u32[wrap] %i, 1
  %prev = Load.u32[inbounds #1] (ElemAddr.ptr<u32>[inbounds #1] %t, %im)
  %x    = Load.u32[inbounds #2] (ElemAddr.ptr<u32>[inbounds #2] %a, %i)
  %next = Add.u32[wrap] %prev, %x
  Store.u32[inbounds #3] (ElemAddr.ptr<u32>[inbounds #3] %t, %i), %next
```

### After

```
// m = n - 1 elements; chunk c covers i in [1 + c*m/C, 1 + (c+1)*m/C)
%seed = Load.u32[inbounds #1] (ElemAddr.ptr<u32>[inbounds #1] %t, 0)
ParallelFor[chunks=contiguous] %c in [0, C) {          // pass 1: %r[c] = fold of a over chunk c, from 0
  ...
}
// sequential: %o[0] = %seed;  %o[c] = Add.u32[wrap] %o[c-1], %r[c-1]
ParallelFor[chunks=contiguous] %c in [0, C) {          // pass 2: scan chunk c starting from %o[c]
  // acc = %o[c];  for i in chunk c:  acc = Add.u32[wrap] acc, a[i];  Store t[i], acc
  ...
}
```

## When it applies

(1) The loop is a single-block counted loop with one exit and a trip count available before
entry ([#350](https://github.com/Hawkynt/Polypiler/issues/350)). (2) `UniformDependence` of `OPT-LOOP-INTERCHANGE` (depth 1) reports exactly one
loop-carried dependence, with distance 1, from the store of `t[i]` to the load of `t[i−1]`; any other
carried dependence, `Unknown` or `MayAlias` ([#82](https://github.com/Hawkynt/Polypiler/issues/82)) blocks. (3) The stored value is `prev ⊕ e` or
`e ⊕ prev`, where `e` does not depend on `t` and `⊕` is proven associative on its type; the identity of
`⊕` starts pass 1. (4) The body has no other effect and cannot trap ([#114](https://github.com/Hawkynt/Polypiler/issues/114); accesses `[inbounds]`).
(5) `a` and `t` are distinct (`NoAlias`) — pass 1 reads all of `a` before pass 2 has finished writing `t`.
(6) The chunk bounds are computed without overflow ([#94](https://github.com/Hawkynt/Polypiler/issues/94) or a wider type); chunks may be empty.
(7) The target has `Threads` ([#259](https://github.com/Hawkynt/Polypiler/issues/259)) and [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers two threaded passes
(`ThreadedCost` with twice the loop's work) to the sequential loop.

## How it is proven

`exhaustive-8bit+smt-bitvector` for associativity of the operation (shared with [#959](https://github.com/Hawkynt/Polypiler/issues/959); 5 s budget, timeout → `not applied: proof timeout`) plus `alias-facts+effect-facts`: with an associative `⊕`, `t[i] = seed ⊕ a[1] ⊕ … ⊕ a[i]` regroups into chunk offset and in-chunk prefix.

## When it must not fire

- `t[i] = t[i-1] - a[i]`: subtraction is not associative — `(10 − 1) − 2 = 7` but `10 − (1 − 2) = 11` — so chunk totals folded with it cannot be chained into offsets.
- `f64` running sum: the chunk totals round differently from the sequential prefixes.
- `Add.i32[trap Overflow]`: a prefix that overflows in the sequential loop may not overflow inside any chunk.
- `t[i] = t[i-1] + t[i-2]`: a second carried dependence — a recurrence of order two is not a scan.
- `a` aliases `t` shifted by one element: pass 1 would read values pass 2 is about to overwrite, in the wrong order.

## Targets

Only targets with the `Threads` capability and a `threads` entry in the cost profile; the cost
model decides from the element count's range, so short scans stay sequential. Never on single-threaded and
retro targets, where two passes are simply twice the work. The emitted source is considerably longer than
the loop; it is accepted at `--opt speed` only.

## Switching it off

`--disable-opt O0324`; active at `--opt speed` on targets with `Threads`; off at `none`, `balanced`, `size` and `memory`.

## Interactions with other passes

runs in phase `specialize` before [#958](https://github.com/Hawkynt/Polypiler/issues/958) (which would reject the loop) and uses its `ParallelFor`; the associativity proofs are those registered by [#959](https://github.com/Hawkynt/Polypiler/issues/959).
