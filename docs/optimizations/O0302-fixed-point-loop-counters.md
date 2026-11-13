# O0302 — Run float loop counters on a power-of-two grid as scaled integers

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0302`.

## What it does

`FOR t = 0 TO 1 STEP 0.25` is ordinary BASIC, and `for (x = 0.0; x <= 1.0; x += 0.125)` is ordinary C. The
counter is a float, so every iteration pays a float add and a float compare — a soft-float call each on a
6502, a Z80 or an 8086 without a coprocessor. When start and step lie on a common power-of-two grid and the
values stay inside the format's exact-integer window, every iterate is exactly representable: the loop can
count in integers and build the float only where the body reads it, bit for bit the same. PRD §4.8, §17.3
(float exactness), `docs/adr/0005-floating-point-tolerance.md` (this rewrite is bit-exact, tolerance 0).

## Example

### Before

```
head:
  %t = Phi.f32 [pre: 0.0], [body: %t2]
  %c = FCmp.ole.f32 %t, 1.0
  Br %c, body, exit
body:
  Call.void @emit(%t)
  %t2 = FAdd.f32[rne] %t, 0.25
  Br head
```

### After

```
head:
  %k = Phi.i32 [pre: 0], [body: %k2]
  %c = ICmp.sle.i32 %k, 4
  Br %c, body, exit
body:
  %f = IntToFloat.f32[rne] %k
  %t = FMul.f32[rne] %f, 0.25
  Call.void @emit(%t)
  %k2 = Add.i32[wrap] %k, 1
  Br head
```

## When it applies

With precision `p` (24 for `f32`, 53 for `f64`): (1) start, step and bound are finite constants,
`step ≠ 0`, and the start is not negative zero (the materialized first iterate would be `+0.0`).
(2) A scale `k ≤ 30` exists with `start·2^k` and `step·2^k` integral. (3) With `T` the trip count computed in
exact rational arithmetic from start, step, bound and predicate, `T + 1 ≤ 1 048 576` and every scaled
iterate `start·2^k + n·step·2^k`, `n = 0 … T`, has magnitude at most `2^p` and fits `i32` (`i64` for `f64`):
then each iterate is representable, each `FAdd` of the original is exact, and `IntToFloat` and the multiply
by `2^-k` are exact. (4) The update is `[rne]`, `[rtz]`, `[rup]` or `[rna]`; `[rdn]` is refused because an
exact zero sum is `−0.0` there. (5) The counter phi has no other definition in the loop and the loop's only
exit is the counter test. (6) The pass evaluates both iterate sequences for `n = 0 … T` and compares bit
patterns; any difference blocks the rewrite. (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the integer form.

## How it is proven

`structural+interpreter`: representability gives exactness of every operation (IEEE 754 §5.4.1), and the pass confirms it by evaluating all `T + 1` iterates at compile time. A trip count above the budget blocks the rewrite; there is no solver.

## When it must not fire

- `for (t = 0; t <= 1.0; t += 0.1)` in `f64`: the source runs 11 times and its last value is 0.9999999999999999; a counter `i / 10` would end on 1.0.
- Start `−0.0`, step 1: the first iterate is `−0.0`, and `1 / t` is `−∞`; `IntToFloat(0)` is `+0.0`.
- `f32` start 16777214, step 1, bound 16777220: 16777217 is not representable, the source adds 1 to 16777216 forever; an integer counter would terminate.
- `FAdd.f32[rdn] %t, -0.5` from 0.5: the exact sum 0 is `−0.0` under round-down.
- A body that assigns the counter (`if (…) t = 0.75`): it is not an induction variable.

## Targets

Every target, with the largest effect where float arithmetic is a library call: 6502, Z80, 8080,
8086 without 8087, tokenized BASIC with MBF-less integer loops. On targets with a fast FPU the loop control
becomes cheaper but a body that reads the counter pays `IntToFloat` and a multiply; [#155](https://github.com/Hawkynt/Polypiler/issues/155) decides with
the alternatives `float counter` (`FAdd` + `FCmp` per iteration) and `integer counter` (`Add` + `ICmp` per
iteration, plus `IntToFloat` + `FMul` per iteration if the body reads the value), listed in that order so
that a tie keeps the source's form. Generated source stays idiomatic (`for k in 0..4: t = k * 0.25`).

## Switching it off

`--disable-opt O0302`; active at `--opt balanced`, `speed`, `size` and `memory` when the cost model approves; off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#154](https://github.com/Hawkynt/Polypiler/issues/154) (constant bounds) and [#264](https://github.com/Hawkynt/Polypiler/issues/264) (which takes integral float webs first); enables [#350](https://github.com/Hawkynt/Polypiler/issues/350) trip counts, [#513](https://github.com/Hawkynt/Polypiler/issues/513) and every loop pass that needs an integer induction variable.
