# O0362 — Multiply by a constant that factors into 2^j ± 1 terms as a chain of shift-and-add steps under the cost model

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0362`.

## What it does

[#165](https://github.com/Hawkynt/Polypiler/issues/165) decomposes a constant multiplication only into at most three shifted terms. `x * 45` has four
signed digits and stays a multiplication — which on an 8086 is a 120-cycle instruction and on a 6502 a helper
call — although `45 = 3 × 15` needs two shift-and-add steps when the intermediate `15x` is reused. Which
constants are worth it depends entirely on the target, so the decision belongs to the cost model, not to a
pattern count. PRD §4.8 (strength reduction; native instruction against sequence against helper).

## Example

### Before

```
%r = Mul.u32[wrap] %x, 45
```

### After

```
%a = Shl.u32[strict] %x, 4
%t = Sub.u32[wrap] %a, %x              // 15x
%b = Shl.u32[strict] %t, 1
%r = Add.u32[wrap] %b, %t              // 45x
```

## When it applies

(1) The instruction is `Mul.<T>[wrap]` with one literal operand `c`, `c ∉ {0, 1}` and `T` an integer
type of width `w` ∈ {8, 16, 32, 64}. (2) Plan search, deterministic: strip the trailing zero bits of `c`
(one final `Shl`); for the odd rest `m`, `Chain(1) = x`; `Chain(m) = (x << j) ± x` when `m = 2^j ± 1`;
otherwise for `j` ascending from 1, first `2^j + 1` then `2^j − 1`, each divisor `f` of `m` gives
`t = Chain(m / f)` followed by `(t << j) ± t`; the plan with the fewest add/subtract steps wins, the first
found on a tie; more than 6 steps or no divisor means no plan. (3) The plan multiplied out modulo `2^w` equals
`c`. (4) The proof obligation of the plan holds. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) scores the plan strictly below the
multiplication under the active objective; the multiplication is listed first. A multiplication with other
contracts is refused: an intermediate step can leave the type where the product does not.

## How it is proven

`exhaustive-8bit+smt-bitvector`: all 256 values for the plan of `c mod 256`, and the emitted plan at its width by [#71](https://github.com/Hawkynt/Polypiler/issues/71) (5 s per plan; proven plans are cached per `(c, w)`). On timeout the multiplication stays (`not applied: proof timeout`).

## When it must not fire

- `Mul.i8[trap Overflow] x, 7` at `x = 18` is 126 and does not trap; the plan `(x << 3) − x` computes 144 first, which leaves `i8`.
- `Mul.u8[sat] x, 45` at `x = 6` saturates to 255; the wrapped chain gives 14.
- A chain that re-reads `x` after it was overwritten in a two-address lowering computes `16x − 15x`; the plan is SSA, the backend must not alias `%t` and `%x`.
- On a profile with a 3-unit multiply the 4-instruction chain is slower and larger; applying it unconditionally is a pessimization, not an error — the cost gate is part of the rule.

## Targets

only where a multiplication is slow or absent: machine-code and assembly backends for CPUs without
a fast multiplier (8086/286 class, 6502, Z80) and targets that would call a multiply helper. Upstream's cost
table (its numbers, not Polypiler measurements) prices a 16-bit multiply at 124 cycles on the 8086 and 4 on a
P6. Source targets with an optimizing compiler behind them price `Mul` as native and cheap, the tie rule keeps
the multiplication, and the generated source is unchanged.

## Switching it off

`--disable-opt O0362`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`; fires only on a strict cost win.

## Interactions with other passes

runs in phase `specialize`, after the last `recognize` sweep, so that [#572](https://github.com/Hawkynt/Polypiler/issues/572) never sees its output, and after [#165](https://github.com/Hawkynt/Polypiler/issues/165); `OPT-PROP-MULNAF` contributes further plans to the same choice.
