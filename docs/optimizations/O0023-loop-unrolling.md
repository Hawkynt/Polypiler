# O0023 — Unroll counted loops under the cost model

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#513](https://github.com/Hawkynt/Polypiler/issues/513). Once implemented it is switched off with `--disable-opt O0023`.

## What it does

A four-round mixing loop, a loop over the eight bits of a byte, a 16-element block copy: with a known small
trip count the loop counter, compare and branch are pure overhead and hide constants from folding. On the
other hand an unrolled loop is exactly what a 6502 or a size-optimized build cannot afford. PRD §4.8 names
"unrolled vs loop" as a cost-model decision; §23.5 requires the cost to come from the target profile.

## Example

### Before

```
head:
  %i = Phi.u32 [pre: 0], [body: %i2]
  %s = Phi.u32 [pre: 0], [body: %s2]
  %c = ICmp.ult.u32 %i, 4
  Br %c, body, exit
body:
  %x  = Load.u32[inbounds #1] %a, %i
  %s2 = Add.u32[wrap] %s, %x
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

### After

```
%x0 = Load.u32[inbounds #1] %a, 0
%s1 = Add.u32[wrap] 0, %x0
%x1 = Load.u32[inbounds #1] %a, 1
%s2 = Add.u32[wrap] %s1, %x1
%x2 = Load.u32[inbounds #1] %a, 2
%s3 = Add.u32[wrap] %s2, %x2
%x3 = Load.u32[inbounds #1] %a, 3
%s4 = Add.u32[wrap] %s3, %x3          // exit value of %s
```

## When it applies

(1) The loop is a natural loop with a single back edge whose trip count is proven by [#94](https://github.com/Hawkynt/Polypiler/issues/94) scalar
evolution **including wrap-around**: the count is the number of times the body executes under the induction
variable's own width and contract; a loop whose exit condition can never become true has no trip count and
is not unrolled. (2) Full unrolling needs a literal count `t ≥ 0`; `t = 0` deletes the body (the header's
effects, if any, stay). (3) Partial unrolling needs the count as an SSA expression available before the
loop; the unrolled loop runs `count / f` times and the remainder loop `count mod f` times, computed without
overflow. (4) Copies are placed in iteration order, so effects, traps and early exits occur exactly as
before; nothing is reordered and no legality condition on the body's effects is needed. (5) The size limit
(256 instructions after unrolling) and the [#155](https://github.com/Hawkynt/Polypiler/issues/155) loop-form decision hold; with `--opt size` or
`memory` only `t = 1` passes.

## How it is proven

`structural+range-facts`: unrolling is repetition of the body in order; the only proof obligation is the trip count. No solver, no timeout path.

## When it must not fire

- `for (u8 i = 0; i <= 255; i++)`: the condition is always true; "256 iterations" is wrong — the loop does not terminate.
- `for (i = 0; i != n; i += 2)` with odd `n`: `i` skips `n` and continues until it wraps.
- A top-tested loop with `n = 0` partially unrolled without the pre-check executes one block of four iterations.
- `count / 4` computed as `(n + 3) / 4` overflows for `n` near the type maximum.
- An early `break` in iteration 2 of 4: copies 3 and 4 must not execute.

## Switching it off

`--disable-opt O0023`; active at `--opt balanced` and `speed`; at `--opt size` and `memory` only loops with a proven trip count of 1 are unrolled; off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#510](https://github.com/Hawkynt/Polypiler/issues/510) and [#508](https://github.com/Hawkynt/Polypiler/issues/508); enables [#154](https://github.com/Hawkynt/Polypiler/issues/154), [#164](https://github.com/Hawkynt/Polypiler/issues/164) and [#500](https://github.com/Hawkynt/Polypiler/issues/500) on the flattened body.
