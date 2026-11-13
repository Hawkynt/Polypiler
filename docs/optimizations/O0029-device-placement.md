# O0029 — Place proven-pure tensor kernels on GPU or NPU when available, with a CPU fallback

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#957](https://github.com/Hawkynt/Polypiler/issues/957). Once implemented it is switched off with `--disable-opt O0029`.

## What it does

Large tensor kernels — convolutions, matrix products, per-pixel maps over frames — are what GPUs and NPUs are
for. PRD §4.8 lists "optional device placement" and §8 P5 "explicit CPU/GPU/NPU capability planning"; §21.3
models `GPU` and `NPU` as capabilities. Placement must be a *plan* the compiler can justify: the kernel is
pure, the device computes the same values, the transfer is worth it, and when the device is missing — at
compile time or at run time — the CPU form is used.

## Example

### Before

```
ParallelFor[chunks=contiguous] %i in [0, 1048576) {
  %x = Load.u8[inbounds #1] %src, %i
  %y = Xor.u8 %x, 90
  Store.u8[inbounds #2] %dst, %i, %y
}
```

### After

```
Placed[device=gpu, fallback=cpu, in=%src, out=%dst] {
  ParallelFor[chunks=contiguous] %i in [0, 1048576) { ... unchanged ... }
}
```

## When it applies

(1) The region is already proven independent and effect-free: it is a `ParallelFor` from [#958](https://github.com/Hawkynt/Polypiler/issues/958), or
a tensor operation of [#380](https://github.com/Hawkynt/Polypiler/issues/380) whose kernel is pure by definition ([#80](https://github.com/Hawkynt/Polypiler/issues/80)). (2) Shapes and
the element count are known before the region starts; all inputs and outputs are whole arrays or contiguous
ranges of program storage that the region alone accesses while it runs. (3) Every operation in the region
is listed in the device profile for its element type, and for floating-point types the profile marks that
operation `exact` (IEEE 754 correctly rounded in the format, [#116](https://github.com/Hawkynt/Polypiler/issues/116)); integer and bitwise operations
with `[wrap]` or `[sat]` contracts are exact by definition. Transcendental functions, FMA-contracting
devices and denormal-flushing devices make an operation not `exact`. (4) The capability graph of
[#259](https://github.com/Hawkynt/Polypiler/issues/259) offers `GPU` (or `NPU`) for the target profile. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the device:
`transfer + elements × perElement` below the CPU or threaded cost. (6) The CPU form is kept as fallback;
the placed node has no semantics of its own beyond "execute the contained region".

## How it is proven

`effect-facts` (inherited independence and purity) plus the device profile's exactness table; no value-level proof is needed because the region's definition is unchanged. No solver, no timeout path.

## When it must not fire

- A `f32` convolution on a GPU that fuses multiply-add or flushes subnormals to zero: results differ in the last bit from the CPU definition.
- A kernel that calls `Sin.f32`: device math libraries are not correctly rounded.
- A 64-element kernel: the transfer costs more than the work.
- A region whose output array is also read by another thread while it runs.
- A target profile without `GPU`: a placed node there would be an unexecutable construct.

## Switching it off

`--disable-opt O0029`; active at `--opt speed`, off at `--opt none`.

## Interactions with other passes

runs last in phase `specialize`, after [#958](https://github.com/Hawkynt/Polypiler/issues/958), [#959](https://github.com/Hawkynt/Polypiler/issues/959) and [#960](https://github.com/Hawkynt/Polypiler/issues/960); the backend reads the plan. On targets without `GPU`/`NPU` it never fires.
