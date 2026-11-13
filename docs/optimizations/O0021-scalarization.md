# O0021 — Scalarize logical vector operations lane by lane

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#509](https://github.com/Hawkynt/Polypiler/issues/509). Once implemented it is switched off with `--disable-opt O0021`.

## What it does

A source written against `Vector128<T>` or an AVX intrinsic must still translate to PowerBASIC, Perl or a
6502 — targets with no vectors at all — and a logical `Vector4096<byte>` must run where the widest register
is 8 bits. The pure-software path "gets emitted along the way": every vector operation has a lane-by-lane
definition, and this pass applies it. It is also the escape hatch when the cost model finds a vector form
slower than scalars. PRD §4.8 (scalarization), §8 P5 ("codegen fallback").

## Example

### Before

```
%v = Add.vec<4 x u32>[wrap] %a, %b
%r = Reduce.Add.vec<4 x f64> %f              // defined sequential
```

### After

```
%a0 = Extract.u32 %a, 0   ...   %b3 = Extract.u32 %b, 3
%s0 = Add.u32[wrap] %a0, %b0   ...   %s3 = Add.u32[wrap] %a3, %b3
%v  = Build.vec<4 x u32> %s0, %s1, %s2, %s3
%t0 = Add.f64 %f0, %f1
%t1 = Add.f64 %t0, %f2
%r  = Add.f64 %t1, %f3
```

## When it applies

Scalarization is semantics-preserving by definition when each lane's scalar operation is the operation the
vector form is defined by, so the conditions are about fidelity: (1) the scalar operation carries the same
overflow, shift, saturation and rounding contract as the lanes; lane shift amounts follow the vector
operation's rule for amounts ≥ lane width, not the scalar default. (2) Lanes with a trapping contract are
evaluated in ascending lane order so the first trapping lane decides the trap, as [#381](https://github.com/Hawkynt/Polypiler/issues/381) specifies.
(3) A horizontal floating-point reduction is folded in exactly the specified association ([#116](https://github.com/Hawkynt/Polypiler/issues/116)
operations, no re-association). (4) Comparison results use the vector's mask encoding (all-ones or zero
lane of the lane width) until consumed by a select. (5) Loads and stores keep ascending address order and
the vector's alignment and overlap contract (a store of the whole vector that overlaps its own source loads
all lanes first). The pass runs when the target profile lacks the capability ([#499](https://github.com/Hawkynt/Polypiler/issues/499) request) or
when [#155](https://github.com/Hawkynt/Polypiler/issues/155) prices the scalar form lower.

## How it is proven

`structural+exhaustive-16bit`: definitional unfolding; the 2 × u8 instances are checked exhaustively against scalar primitives. No solver, no timeout path.

## When it must not fire

- `Reduce.Add` over `[1e16, 1.0, -1e16, 1.0]` in `f64`: sequentially `((1e16 + 1) − 1e16) + 1 = 1.0`; pairwise `(1e16 + 1) + (−1e16 + 1) = 0.0`. The fold order is part of the operation.
- A lane-wise `Shl.vec<4 x u32>` by 32 defined to yield 0 scalarized to `Shl.u32[mask]` yields the unshifted value.
- A comparison mask scalarized to booleans (1 instead of 0xFFFFFFFF) breaks a later `And` with the mask.
- `Store.vec %p+1, (Load.vec %p)` scalarized as interleaved load/store pairs smears lane 0 across the whole vector.
- `Add.vec<16 x u8>[sat]` scalarized with wrapping byte adds: 200 + 100 = 44 instead of 255.

## Switching it off

`--disable-opt O0021`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `legalize` on request of [#499](https://github.com/Hawkynt/Polypiler/issues/499), and in `optimize` when the cost model rejects a vector form; followed by [#154](https://github.com/Hawkynt/Polypiler/issues/154), [#158](https://github.com/Hawkynt/Polypiler/issues/158), [#156](https://github.com/Hawkynt/Polypiler/issues/156) and [#500](https://github.com/Hawkynt/Polypiler/issues/500) (the `Build`/`Extract` pairs vanish).
