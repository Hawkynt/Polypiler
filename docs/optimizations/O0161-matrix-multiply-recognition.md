# O0161 — Recognize matrix multiplication from triple-nested multiply-accumulate loops

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#570](https://github.com/Hawkynt/Polypiler/issues/570). Once implemented it is switched off with `--disable-opt O0161`.

## What it does

The triple loop `c[i][j] += a[i][k] * b[k][j]` is the textbook matrix product and the worst way to run
it: no blocking, no vectorization, three nested bounds checks. PRD §4.3 lists `MatMul` among the
operations that stay recognizable and may be recovered from scalar loops. The recovery has to record
what the loops actually did — whether the destination was cleared or accumulated into, in which order
each element's sum was formed — and it has to refuse look-alikes: Cipher's `MatrixMultiply4x4` has
exactly this shape but multiplies in GF(2^8) and adds with XOR. The operations and their attributes are
written exactly as [#355](https://github.com/Hawkynt/Polypiler/issues/355) and [#354](https://github.com/Hawkynt/Polypiler/issues/354) define them; sequence operands are those of
[#195](https://github.com/Hawkynt/Polypiler/issues/195) and [#197](https://github.com/Hawkynt/Polypiler/issues/197).

## Example

### Before

```
for i in [0, n): for j in [0, p):
  Store c[i,j], 0
  for k in [0, m):
    %x = Load.i32 a[i,k]
    %y = Load.i32 b[k,j]
    %t = Mul.i32 %x, %y
    %o = Load.i32 c[i,j]
    %s = Add.i32 %o, %t
    Store c[i,j], %s
```

### After

```
MatMul.i32[rows=%n, inner=%m, cols=%p, order=any] %c, %a, %b
```

## When it applies

(1) a perfect nest of three counted unit-step loops with loop-invariant
bounds; the only statements are the multiply-accumulate (and the clearing store between the second and
third loop in forms that have it); (2) the three accesses are `a[i,k]`, `b[k,j]`, `c[i,j]` under one
consistent assignment of the loop variables to the roles row `i`, column `j`, inner `k` — with affine
flat indices the row lengths must equal the corresponding loop bounds; (3) `c` is disjoint from `a`
and `b` over the accessed ranges ([#82](https://github.com/Hawkynt/Polypiler/issues/82)); (4) all accesses proven in bounds for the full
ranges, or carrying no trap; ordinary non-volatile memory; no other effects; (5) the element
operations are exactly `Mul` then `Add` (wrapping) or `FMul` then `FAdd` (separately rounded) at the
element type; (6) clearing covers exactly the destination elements written, before their first
accumulation; (7) floating point: for each `(i, j)` the additions happen in the `k` order of the
source — which is the direction of the `k` loop regardless of how the three loops are nested — and
that order is recorded.

## How it is proven

`loop-inductive` on the nest (invariant per completed `(i, j)`: `c[i,j]` equals the
ordered sum over `k`), discharged by SMT for one symbolic inner step; alias, bounds and effect facts
as obligations; bounded `exhaustive` interpretation for all dimension triples up to 2×2×2 over a small
element range; floating point confirmed bit-exactly by differential execution. A failed, unknown or
timed-out obligation (budget 5 s each) leaves the IR unchanged and reports `not applied: proof
timeout` or the failing precondition.

## When it must not fire

- Another algebra: `MatrixMultiply4x4` computes `result[i] ^= GF256Mul(matrix[i][j], column[j])`; over
  the integers the first row of `[[2,3],[1,1]] · [1,1]` is 5, in GF(2^8) it is `2 ^ 3 = 1`.
- In-place product: with `c = a`, row `i` of `a` is overwritten while later `j` iterations still read
  it.
- Uncleared destination taken as `MatMul`: existing contents are part of the result (`MatMulAdd`).
- Triangular bound (`k < i`): a different operation on the same access pattern.
- Mismatched roles (`b[j,k]` instead of `b[k,j]`): the product with the transpose.
- Floating-point order: a `k` loop running downward sums in the opposite order and gives different
  low-order bits.

## Switching it off

`--disable-opt O0161`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after loop canonicalization and before the single-loop
sequence rules claim the inner loop (the engine tries nest rules on the outermost loop first); enables
loop interchange, blocking and library selection downstream.
