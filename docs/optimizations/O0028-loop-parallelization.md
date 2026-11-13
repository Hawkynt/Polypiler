# O0028 — Run provably independent loop iterations on multiple threads

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#958](https://github.com/Hawkynt/Polypiler/issues/958). Once implemented it is switched off with `--disable-opt O0028`.

## What it does

Image filters, per-line deinterlacing, block ciphers in counter mode: each output element depends only on
inputs, never on another iteration's output. Such loops scale with cores — "multi-threading … must be
heavily used in optimized code when available" — but only if independence is proven; a single shared
counter or an overlapping write turns the result into a race. And on a single-threaded target (6502,
classic BASIC, a browser's main thread) the loop must simply stay a loop. PRD §4.8, §8 P5, §17.1
(concurrency/happens-before is observable behaviour).

## Example

### Before

```
loop:                                         // i = 0 .. n-1
  %x = Load.u8[inbounds #1] %src, %i
  %y = Mul.u8[wrap] %x, 2
  Store.u8[inbounds #2] %dst, %i, %y
```

### After

```
ParallelFor[chunks=contiguous] %i in [0, %n) {
  %x = Load.u8[inbounds #1] %src, %i
  %y = Mul.u8[wrap] %x, 2
  Store.u8[inbounds #2] %dst, %i, %y
}
```

## When it applies

(1) The loop has a trip count computable before entry ([#94](https://github.com/Hawkynt/Polypiler/issues/94)) and a single exit (no `break`).
(2) The body is trap-free (accesses `[inbounds]` via [#502](https://github.com/Hawkynt/Polypiler/issues/502), no checked arithmetic, no possible
division by zero): with a trap, which iterations' stores are visible afterwards would depend on scheduling.
(3) The body's only effects are non-volatile element reads and writes ([#114](https://github.com/Hawkynt/Polypiler/issues/114)); no calls with
effects, no allocation with observable identity or `Runtime.State`, no I/O, no synchronization. (4) Every
store address is `base[g(i)]` with `g` proven injective over the index range (affine `a·i + b` with
`a ≠ 0` and no wrap, [#94](https://github.com/Hawkynt/Polypiler/issues/94)), and all stores of one iteration target elements no other iteration
stores to. (5) No load in iteration `i` may read an element stored by iteration `j ≠ i`: the loaded arrays
are proven distinct from the stored arrays ([#82](https://github.com/Hawkynt/Polypiler/issues/82) `NoAlias`), or the load index equals the same
iteration's store index. `MayAlias`/`Unknown` blocks the rewrite. (6) Every SSA value live across
iterations is the index only (no accumulators). (7) Memory accessed is ordinary program storage
([#121](https://github.com/Hawkynt/Polypiler/issues/121) classification), not device, volatile or thread-affine. (8) `Threads` is available and
[#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers `ThreadedCost`.

## How it is proven

`alias-facts+effect-facts+range-facts`: under (2)–(7) any two iterations commute (Bernstein's conditions), so every schedule produces the sequential result. No solver, no timeout path.

## When it must not fire

- `a[i] = a[i − 1] + 1`: iteration `i` reads what `i − 1` wrote.
- `a[i / 2] = i` for `i` in 0..3: sequentially the last writer wins (`a[0] = 1`, `a[1] = 3`); in parallel either writer may.
- `count = count + 1` inside the body: a lost-update race.
- `dst` overlapping `src` by one element.
- `Call.void @print(%i)` in the body: output order becomes scheduling-dependent.
- `c[i] = a[i] / b[i]` with `b[5] = 0`: sequentially elements 0–4 are stored before the trap; in parallel element 7 may already be stored.

## Switching it off

`--disable-opt O0028`; active at `--opt speed` and, when the cost model approves, `balanced`, off at `--opt none`.

## Interactions with other passes

runs in phase `specialize` after [#510](https://github.com/Hawkynt/Polypiler/issues/510) (chunks keep their vector bodies), [#502](https://github.com/Hawkynt/Polypiler/issues/502) and [#960](https://github.com/Hawkynt/Polypiler/issues/960); on targets without `Threads` it never fires.
