# O0160 — Recognize dot products from multiply-accumulate reductions

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#566](https://github.com/Hawkynt/Polypiler/issues/566). Once implemented it is switched off with `--disable-opt O0160`.

## What it does

`sum += a[i] * b[i]` is the inner loop of every FIR filter, correlation, norm and matrix product:
CompressionWorkbench's ATRAC1 encoder has `sum += input[n] * kernel[kernelOffset + n]`, its RA-144 codec
`sum += (uint)(data[i] * data[i])`. A named `DotProduct` gives SIMD multiply-add (`PMADDWD`, `VDPPS`),
library kernels and a compact loop on small CPUs. What must not be lost is how the source computed it:
wrapping integers may be summed in any order, floating-point sums may not, and a product that was
rounded before the add is not a fused multiply-add. PRD §4.5 (matrix kernels), §19.3 (float). The
operations and their attributes are written exactly as [#355](https://github.com/Hawkynt/Polypiler/issues/355) and [#354](https://github.com/Hawkynt/Polypiler/issues/354) define
them; sequence operands are those of [#195](https://github.com/Hawkynt/Polypiler/issues/195) and [#197](https://github.com/Hawkynt/Polypiler/issues/197).

## Example

### Before

```
%m = Map.i32[order=any] %a, %b, %n, λ(%x, %y): Mul.i32 %x, %y
%s = Reduce.i32[order=any, associative=true] %m, %n, 0, λ(%acc, %p): Add.i32 %acc, %p
```

### After

```
%s = DotProduct.i32[order=any] %a, %b, %n, 0
```

## When it applies

(1) the `Reduce`'s only input is the `Map`, and the `Map` has no other
user; (2) the `Map` body is exactly the product of its two element operands, optionally after
identical-signedness extensions of both operands to the accumulator type; the `Reduce` body is exactly
the sum of accumulator and element; (3) integer: both operations are wrapping → `order=any`; (4)
floating point: `FMul` then `FAdd` in the source's format with the source's rounding, each rounded
separately → `order=sequential, fused=false`; the rewrite is a renaming that keeps both attributes, so
it is bit-exact by construction; (5) a widening product must be widened *before* the multiplication;
(6) no checked, saturating or trapping arithmetic.

## How it is proven

`smt-bitvector` for the integer forms on one symbolic step (the `DotProduct` step
function equals multiply-then-add at the element and accumulator widths) with `exhaustive-8bit` at `i8
→ i16`; for floating point the obligation is structural (`ValidatedRule`: operation-for-operation
identity with recorded order and `fused=false`), confirmed by bit-exact differential execution. A
failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and reports `not
applied: proof timeout` or the failing precondition.

## When it must not fire

- Order in floating point: `a = [16777216, 1, −16777216]`, `b = [1, 1, 1]` in `f32` gives 0 in source
  order and 1 if the first and last products are added first; `order=any` would permit the latter.
- Fusion: with `a = b = [1 + 2^-12]` in `f32` and an initial accumulator of `−(1 + 2^-11)`,
  multiply-then-add gives 0 and a fused multiply-add gives `2^-24`; `fused=false` must survive.
- Narrow product: `i16` elements 300 and 300 multiplied at 16 bits wrap to 24464 before being
  extended; a widening dot product would add 90000.
- Checked sum: the overflow trap position depends on the order of the additions.
- A `Map` with a second user: the products themselves are needed, so they cannot be folded away.

## Switching it off

`--disable-opt O0160`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after [#281](https://github.com/Hawkynt/Polypiler/issues/281) (O0150) and [#282](https://github.com/Hawkynt/Polypiler/issues/282)
(O0152) have produced the two operations; enables [#570](https://github.com/Hawkynt/Polypiler/issues/570) and [#562](https://github.com/Hawkynt/Polypiler/issues/562) to describe their
inner kernels and SIMD multiply-add selection.
