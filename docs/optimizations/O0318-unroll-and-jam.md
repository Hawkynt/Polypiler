# O0318 — Unroll the outer loop of a nest and fuse the copies of the inner loop

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0318`.

## What it does

In `for i: for j: s += a[i][j] * b[j]` the vector `b` is read once per outer iteration — 10 000 loads for 100
distinct values in a 100 × 100 nest. Unrolling the *outer* loop by two and fusing the two copies of the inner
loop reads each `b[j]` once for two rows and halves the inner loop's overhead per element. [#513](https://github.com/Hawkynt/Polypiler/issues/513)
lists unroll-and-jam as "not planned yet". The jam interleaves iterations of neighbouring outer iterations,
so it carries the legality condition of an interchange, and it reorders a reduction. PB-Compiler plans it
with both obligations. PRD §4.8.

## Example

### Before

```
// i = 0 .. n-1, j = 0 .. m-1
inner.body:
  %x  = Load.u32[inbounds #1] (ElemAddr.ptr<u32>[inbounds #1] %a, %i, %j)
  %y  = Load.u32[inbounds #2] (ElemAddr.ptr<u32>[inbounds #2] %b, %j)
  %p  = Mul.u32[wrap] %x, %y
  %s2 = Add.u32[wrap] %s, %p
```

### After

```
// i = 0, 2, .. while i + 1 < n;  j = 0 .. m-1;  then the original nest for the last row if n is odd
inner.body:
  %y  = Load.u32[inbounds #2] (ElemAddr.ptr<u32>[inbounds #2] %b, %j)            // once for two rows
  %x0 = Load.u32[inbounds #1] (ElemAddr.ptr<u32>[inbounds #1] %a, %i, %j)
  %p0 = Mul.u32[wrap] %x0, %y
  %s1 = Add.u32[wrap] %s, %p0
  %x1 = Load.u32[inbounds #1] (ElemAddr.ptr<u32>[inbounds #1] %a, %i1, %j)       // %i1 = %i + 1
  %p1 = Mul.u32[wrap] %x1, %y
  %s2 = Add.u32[wrap] %s1, %p1
```

## When it applies

(1) The nest satisfies conditions (1), (2) and (4) of `OPT-LOOP-INTERCHANGE`: perfect,
rectangular, no effects or traps beyond non-volatile element accesses, distinct arrays `NoAlias`. (2) The
jam moves iteration `(i + r, j1)` in front of `(i, j2)` for `0 < r < u` and `j1 < j2`. `UniformDependence`
must return distance vectors, and none may have `1 ≤ d_outer ≤ u − 1` together with `d_inner < 0`.
`Unknown` blocks. (3) Values carried through the nest are reductions `s' = s ⊕ e` used nowhere else in the
nest, with `⊕` a `[wrap]` `Add`, `Mul`, `And`, `Or` or `Xor` ([#122](https://github.com/Hawkynt/Polypiler/issues/122)) proven associative and
commutative: the jam changes the order in which the terms are combined. Any other carried value blocks.
(4) The outer trip count is available before the nest ([#350](https://github.com/Hawkynt/Polypiler/issues/350)); the jammed nest covers `trips − trips
mod u` outer iterations and the remainder nest the rest, computed without overflow ([#94](https://github.com/Hawkynt/Polypiler/issues/94)).
(5) Size: the jammed body has at most 256 instructions. (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the jammed form: it saves
`(u − 1) / u` of the loads and pure computations that do not depend on the outer counter and of the inner
loop's overhead, and costs `u` bodies plus the remainder in size. On an `emits: source` profile
([#262](https://github.com/Hawkynt/Polypiler/issues/262)) the rewrite is offered at `--opt speed` only.

## How it is proven

`structural+alias-facts+effect-facts` for the reordering under (2), `exhaustive-8bit+smt-bitvector` for associativity and commutativity of the reduction operations (5 s budget, timeout → `not applied: proof timeout`).

## When it must not fire

- `a[i][j] = a[i-1][j+1] + 1`: distance `(1, −1)`; jammed by 2, `a[i+1][j]` is computed before `a[i][j+1]`, which it reads.
- An `f64` dot-product reduction: the jam adds the terms in the order row 0, row 1, row 0, row 1 … instead of all of row 0 first; the rounded sum differs.
- An odd outer trip count without the remainder: the last row is skipped or a row past the end is read.
- `trips − trips mod 2` computed as `(n + 1) / 2 * 2` for `n` at the type maximum: the sum wraps.
- An inner bound `j < i`: the copies for `i` and `i + 1` have different inner trip counts and cannot share one loop.

## Targets

Machine-code backends first — the saved loads and loop overhead are real on every CPU, and the
cost model weighs them against `u` bodies of code, so 8-bit targets with tight memory usually decline. On
mature source targets the transformation is occasionally done by the target compiler at its highest level
and the jammed source is markedly harder to read; `emits: source` profiles therefore get it at `--opt
speed` only, where it also serves targets without an optimizer.

## Switching it off

`--disable-opt O0318`; active at `--opt speed`, and at `balanced` on profiles that are not `emits: source` when the cost model approves; off at `size`, `memory` and `none`.

## Interactions with other passes

runs in phase `optimize` after `OPT-LOOP-INTERCHANGE` and [#508](https://github.com/Hawkynt/Polypiler/issues/508) (which has already hoisted what is invariant in both loops) and before [#510](https://github.com/Hawkynt/Polypiler/issues/510) and [#513](https://github.com/Hawkynt/Polypiler/issues/513); [#158](https://github.com/Hawkynt/Polypiler/issues/158) cleans up what the copies still have in common.
