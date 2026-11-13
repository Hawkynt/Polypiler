# O0165 — Fuse sine and cosine of the same argument into SinCos

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#788](https://github.com/Hawkynt/Polypiler/issues/788). Once implemented it is switched off with `--disable-opt O0165`.

## What it does

Rotations, FFT twiddle factors and polygon generators need the sine and the cosine of the same angle:
CompressionWorkbench's `VectorIcon` computes `cx + r * Math.Cos(a), cy + r * Math.Sin(a)`, its AMR-NB
encoder rotates by `Math.Cos(phase)` and `Math.Sin(phase)`. Computing both together shares the argument
reduction — `Math.SinCos` in .NET, `sincos` in glibc, `FSINCOS` on x87, one CORDIC run on a small CPU.
PRD §4.3 lists `SinCos` among the operations that stay recognizable. The fusion is the floating-point
sibling of `DivMod`: same argument, same precision, both evaluated — and the pair must mean exactly the
two separate results.

## Example

### Before

```
%s = Sin.f64 %x
%c = Cos.f64 %x
```

### After

```
%sc = SinCos.f64 %x
%s = SinOf %sc
%c = CosOf %sc
```

## When it applies

(1) both intrinsics take the *same* SSA value; (2) same format and the
same `accuracy` attribute as [#655](https://github.com/Hawkynt/Polypiler/issues/655) defines it (`ulp(n)`, `cr` or `library(<name>)`;
omitted when it is the default) — the `SinCos` carries that one contract, and its projections `SinOf`
and `CosOf` are by definition the values `Sin` and `Cos` have under it; (3) one dominates the other
and the fused operation is placed at the dominating one, so no evaluation is added on any path; (4)
the calls are effect-free in their source profile ([#80](https://github.com/Hawkynt/Polypiler/issues/80)): no `errno`, no observable
floating-point exception flags beyond what both forms raise identically — otherwise refused; (5)
non-finite arguments: both forms return NaN for ±∞ and NaN by [#648](https://github.com/Hawkynt/Polypiler/issues/648)'s definition.

## How it is proven

`ValidatedRule` — the identity holds by the definition of the projections; dominance,
same-argument and effect preconditions are checked structurally (`EffectFact`); confirmed by bit-exact
differential interpretation over 10 000 arguments including 0, ±π/2 as doubles, huge arguments,
subnormals, ±∞ and NaN. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR
unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Different precision: `Sin.f32(x)` and `Cos.f64((double)x)` — a fused `f64` pair rounds the sine
  differently from the `f32` call.
- Arguments equal in value but not in identity (`x` and a reload of `x` that another thread or an
  intervening store may have changed).
- A target `sincos` with a different last-bit result than the target's separate `sin` and `cos`:
  allowed only if the accuracy contract admits both; under `accuracy=library(<name>)` the backend must
  emit the separate calls unless that library's own `sincos` is the named one. Recognition does not
  decide this — which is why the contract travels with the node.
- x87 `FSINCOS` in lifted code: for `|x| ≥ 2^63` it leaves the operand unchanged and sets a flag; it
  is not `SinCos` for those arguments. That is the lifter's concern and a reason this rule only
  consumes intrinsics.
- Sibling branches: fusing would evaluate a transcendental function on a path that had none.

## Switching it off

`--disable-opt O0165`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after global value numbering (equal arguments become one
SSA value); enables single-call lowering and CORDIC sharing on targets without a floating-point unit.
