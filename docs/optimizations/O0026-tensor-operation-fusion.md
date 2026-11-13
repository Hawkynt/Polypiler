# O0026 — Fuse transposes and element-wise tensor operations without materializing intermediates

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#960](https://github.com/Hawkynt/Polypiler/issues/960). Once implemented it is switched off with `--disable-opt O0026`.

## What it does

Matrix code written for clarity materializes everything: `transpose(transpose(A))`, `transpose(A) · B`,
`scale(offset(A))`. Each intermediate is a full copy of the tensor — megabytes for an image — and a full
extra pass over memory. All three are removable without touching a single floating-point rounding, as long
as the order in which each output element is accumulated stays what the source specified. PRD §4.8 ("fusion
of transposes/tensor operations"), §8 P5.

## Example

### Before

```
%t = Transpose.tensor<f64>[2,2] %A, [1, 0]
%c = MatMul.tensor<f64> %t, %B
```

### After

```
%c = MatMul.tensor<f64>[transposeA] %A, %B       // c[i][j] = sum over k ascending of A[k][i] * B[k][j]
```

## When it applies

(1) The intermediate tensor has one consumer and does not escape ([#82](https://github.com/Hawkynt/Polypiler/issues/82)). (2) Its source is not
written between the point where the intermediate was defined and the consumer: `Transpose` and `Map` are
value operations in the source, so the fused consumer must read the same element values the copy would have
held. (3) Permutation composition: with `X = Transpose(A, p)` meaning axis `i` of `X` is axis `p[i]` of `A`,
`Transpose(X, q)` has axis `i` = axis `p[q[i]]` of `A`. (4) For `MatMul` the per-element accumulation
order defined by [#346](https://github.com/Hawkynt/Polypiler/issues/346) (index `k` ascending, one multiply and one add per term, no FMA) is kept
exactly; only the address computation of the operand changes. (5) For `Map` chains the stage functions obey
rule (3) of [#507](https://github.com/Hawkynt/Polypiler/issues/507) ([#80](https://github.com/Hawkynt/Polypiler/issues/80)): at most one stage has effects or may trap. (6) Shape checks
that the unfused operations would perform and trap on are performed in the same order before the fused
kernel runs. (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the fused form (strided reads can be slower than a materialized copy
for very large operands under `speed`; the model decides).

## How it is proven

`structural+alias-facts+effect-facts`: transposition and element-wise mapping only rename which element is read; every arithmetic operation and its order are unchanged. No solver, no timeout path.

## When it must not fire

- `T = transpose(A); A[0][1] = 9; C = T · B`: the source's `T` holds the old value; a strided read of `A` sees 9.
- Fusing `MatMul(Transpose(A), B)` by iterating `k` in descending order (convenient for the stride) changes floating-point sums.
- `Transpose(Transpose(A, [1,2,0]), [1,2,0])` is not the identity: the composition is `[2,0,1]`.
- `Map(Map(A, f), g)` where `f` and `g` both log: element-wise interleaving reorders the log.
- A shape mismatch in the second operation: the source fails after the first operation's effects; the fused kernel must not fail before them.

## Switching it off

`--disable-opt O0026`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after tensor recognition and [#160](https://github.com/Hawkynt/Polypiler/issues/160); enables [#500](https://github.com/Hawkynt/Polypiler/issues/500), [#958](https://github.com/Hawkynt/Polypiler/issues/958) and [#957](https://github.com/Hawkynt/Polypiler/issues/957) on the fused kernels.
