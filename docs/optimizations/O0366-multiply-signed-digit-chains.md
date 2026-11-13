# O0366 — Multiply by a constant of four to six signed digits as one threaded shift-add chain

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0366`.

## What it does

A multiplier with few set bits, or a run of them, is a short sum or difference of shifts: `83 = 64 + 16 + 4 − 1`.
[#165](https://github.com/Hawkynt/Polypiler/issues/165) emits such forms up to three terms, `OPT-PROP-MULCHAIN` handles constants that factor;
a prime such as 83 with four signed digits falls between the two and stays a multiplication. Threading the
digits through one accumulator — `r = (r << gap) ± x` per digit — needs one temporary, which is what makes it
usable on register-starved CPUs. PRD §4.8 (strength reduction; native instruction against sequence).

## Example

### Before

```
%r = Mul.u32[wrap] %x, 83               // 83 = 64 + 16 + 4 − 1
```

### After

```
%a = Shl.u32[strict] %x, 2
%b = Add.u32[wrap] %a, %x               // 5x
%c = Shl.u32[strict] %b, 2
%d = Add.u32[wrap] %c, %x               // 21x
%e = Shl.u32[strict] %d, 2
%r = Sub.u32[wrap] %e, %x               // 83x
```

## When it applies

(1) `Mul.<T>[wrap]` with one literal operand `c`, width `w` ∈ {8, 16, 32, 64}. (2) The non-adjacent
form of `c mod 2^w`, with a digit at position `w` discarded, has `D` non-zero digits, `4 ≤ D ≤ 6`. (3) The plan
is built from the top digit down with an accumulator `a` and a sign `σ` (the value is `σ·a`): `a = x`, `σ` the
sign of the top digit; for each further digit `d` after a gap `g`: with `σ = +`, `a = (a << g) + x` or
`(a << g) − x`; with `σ = −` and `d = −1`, `a = (a << g) + x` and `σ` stays; with `σ = −` and `d = +1`,
`a = x − (a << g)` and `σ` becomes `+`. A remaining `σ = −` appends one `Neg[wrap]`; trailing zero digits
append one `Shl`. (4) The plan multiplied out modulo `2^w` equals `c`, and its proof obligation holds. (5) The
planner's [#155](https://github.com/Hawkynt/Polypiler/issues/155) query scores the plan strictly below the multiplication and not above any other
offered plan; the multiplication is listed first.

## How it is proven

`exhaustive-16bit+smt-bitvector`: the plan of `c mod 2^16` over all 65 536 operands (and at 8 bit for 8-bit types), the emitted plan at 32 and 64 bit by [#71](https://github.com/Hawkynt/Polypiler/issues/71) (5 s per plan, cached per `(c, w)`). On timeout the multiplication stays (`not applied: proof timeout`).

## When it must not fire

- Plain binary digits instead of the non-adjacent form: `0xF7 = 247` has seven set bits but three signed digits (`256 − 8 − 1`); counting bits refuses a constant [#165](https://github.com/Hawkynt/Polypiler/issues/165) already handles and mis-sizes others.
- Keeping the digit at position `w`: for `c = −83` at `u32` the form is `2^32 − 64 − 16 − 4 + 1`; a step for `2^32` is a shift by the width, a `[strict]` verifier error.
- A sign slip in the negative thread: `−83x` built as `(21x << 2) − x` is `+83x`; the last step must be `x − (21x << 2)`.
- `Mul.i16[trap Overflow] x, 83` at `x = 394`: the product 32 702 fits, the accumulator `21x << 2` is 33 096 and leaves `i16` before the final subtraction; trapping multiplications are never decomposed.

## Targets

as `OPT-PROP-MULCHAIN`: only machine-code and assembly backends for CPUs with a slow or missing
multiply, and targets that would otherwise call a multiply helper; `--opt size` accepts the chain only where it
is smaller than the call. Targets with a fast multiply and every source target with an optimizing compiler
behind it keep `x * 83`.

## Switching it off

`--disable-opt O0366`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`; fires only on a strict cost win.

## Interactions with other passes

runs in phase `specialize` inside the planner of `OPT-PROP-MULCHAIN`, after the last `recognize` sweep and after [#165](https://github.com/Hawkynt/Polypiler/issues/165). With both `O0362` and `O0366` enabled the planner runs once and the cheaper plan wins, the factor chain on a tie; with `O0362` disabled only the threaded plans are offered.
