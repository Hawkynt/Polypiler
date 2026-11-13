# O0162 — Recognize one- and two-dimensional convolution and image kernels from tap-accumulation loop nests

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#562](https://github.com/Hawkynt/Polypiler/issues/562). Once implemented it is switched off with `--disable-opt O0162`.

## What it does

Filters slide a small kernel over a signal or an image: `out[i] = Σ_k in[i + k] · w[k]`, and its
two-dimensional form over rows and columns. MediaDroyd's deinterlacer applies vertical tap filters to
every pixel; CompressionWorkbench's ATRAC1 encoder runs windowed filters over a `kernel` array. PRD §4.3
names `Conv` among the recoverable operations and §4.5 names image kernels. A convolution is defined by
more than its loops: kernel orientation (correlation or flipped), border policy, tap accumulation order
— and an in-place filter is not a convolution at all, because later outputs read earlier ones. The
operations and their attributes are written exactly as [#355](https://github.com/Hawkynt/Polypiler/issues/355) and [#354](https://github.com/Hawkynt/Polypiler/issues/354) define
them; sequence operands are those of [#195](https://github.com/Hawkynt/Polypiler/issues/195) and [#197](https://github.com/Hawkynt/Polypiler/issues/197).

## Example

### Before

```
for i in [0, n - 2):
  %acc = 0
  for k in [0, 3):
    %x = Load.i32 in[i + k]
    %c = Load.i32 w[k]
    %t = Mul.i32 %x, %c
    %acc = Add.i32 %acc, %t
  Store out[i], %acc
```

### After

```
Conv1D.i32[kernel=3, stride=1, flipped=false, border=valid, order=any] %out, %in, %w, %n
```

## When it applies

(1) a perfect nest: output loops outside, tap loops inside, an accumulator
initialized per output position and stored once; (2) input index is `output·stride + tap` (or the
flipped form) in each dimension, kernel index is the tap; (3) the output range is exactly the `valid`
range for the kernel size and stride, or, in the `clamp-to-edge` form, every tap index is clamped to
the image, the output covers exactly `⌈N/S⌉` positions per axis and the offset of the first tap is
recorded as `origin`; (4) the output is disjoint from input and kernel ([#82](https://github.com/Hawkynt/Polypiler/issues/82)); the kernel is
loop-invariant during the nest; (5) all accesses proven in bounds or without trap; ordinary
non-volatile memory; no other effects; (6) element operations are multiply then add at the accumulator
type, wrapping for integers, separately rounded for floating point with the tap order recorded as
written (`dy` outer, `dx` inner for 2-D); widening before the multiply as in [#566](https://github.com/Hawkynt/Polypiler/issues/566).

## How it is proven

`loop-inductive` on the nest (invariant per completed output position: the stored
value equals the ordered tap sum), discharged by SMT for one symbolic tap step; alias, bounds and
effect facts as obligations; bounded `exhaustive` interpretation for signal lengths up to 5 and
kernels up to 3 taps (1-D) and 3×3 images with 2×2 kernels (2-D) over a small element range; floating
point confirmed bit-exactly. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR
unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- In-place filter: `in = [1, 2, 3, 4]`, centred taps `[1, 1, 1]` over positions 1 and 2. Out of place:
  `out[1] = 6`, `out[2] = 9`. In place: position 1 becomes 6, and position 2 then reads it: `6 + 3 + 4
  = 13`.
- Orientation: `in = [1, 2, 3, 4, 5]`, `w = [1, 0, −1]`: correlation gives `[−2, −2, −2]`, convolution
  (flipped kernel) gives `[2, 2, 2]`.
- Border: a nest that skips taps outside the image computes a zero-padded result; reporting it as
  `valid` or `clamp-to-edge` changes every border pixel.
- A kernel that the loop itself updates (adaptive filter): not loop-invariant.
- Tap order in floating point: summing taps in another order changes the low bits.

## Switching it off

`--disable-opt O0162`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after loop canonicalization and [#272](https://github.com/Hawkynt/Polypiler/issues/272) (O0123),
which makes clamped indices visible, and before the single-loop sequence rules; uses the inner-kernel
description of [#566](https://github.com/Hawkynt/Polypiler/issues/566); enables separable and vectorized lowering.
