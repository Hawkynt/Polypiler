# O0126 — Recognize division and remainder by a constant from multiply-high sequences

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#565](https://github.com/Hawkynt/Polypiler/issues/565). Once implemented it is switched off with `--disable-opt O0126`.

## What it does

Compilers and careful programmers replace `x / 10` by a multiplication with a magic reciprocal and a
shift; PB-Compiler's reciprocal division does it, and every lifted x86 or ARM binary is full of it. In
recovered source the magic constants are unreadable, and a target with a different word size, or with a
fast divider, or with none at all, needs the division back to choose its own lowering. The sequence is
exact only for specific constant pairs, so the rule must derive the divisor and certify it rather than
trust a table. PRD §19.2 (binary architecture level: "multiply/divide sequences"), §19.3.

## Example

### Before

```
%h = MulHigh.u32 %x, 0xCCCCCCCD
%q = LShr.u32[strict] %h, 3
```

### After

```
%q = UDiv.u32 %x, 10
```

## When it applies

let `k` be the total right shift of the full product (`w + s`, plus 1 in
the add-back form, where the effective multiplier is `2^w + M`), `X` the largest possible value of `x`
(`2^w - 1`, or the bound from the operand type or a range fact), and `d = round(2^k / M_eff)`. The
rewrite to `UDiv(x, d)` requires `2 ≤ d ≤ (X+1)/2`, `e = M_eff·d - 2^k ≥ 0` and `e · x' < 2^k`, where
`x'` is the largest value `≤ X` congruent to `d - 1` modulo `d` — this is necessary and sufficient for
`floor(M_eff·x / 2^k) = floor(x / d)` on `0..X`. In the narrow-operand form additionally `M · X <
2^(2h)` (the multiply does not wrap). Signed form: `0 < e` and `e · 2^(w-1) ≤ 2^k` (the Hacker's
Delight condition), the correction term is exactly the sign bit of `x`, and the result is truncating
division. Remainder: the subtracted product uses the same `x` and the constant `d` of an already
recognized quotient; wrapping arithmetic. The certificate is evaluated in exact integers; nothing is
looked up in a table of known magics.

## How it is proven

`validated-rule+differential`: the certificate is a checked precondition of a rule
validated exhaustively at `w = 16` (every divisor, every accepted `(M, s)`, all 65 536 inputs) and by
`smt-bitvector` at 16 bit; at 32 and 64 bit the inequality itself is the proof and is recorded in the
report with its numbers. It is never reported as `smt-bitvector` at widths where no solver run
happened. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Magic off by one: `M = 0xCCCCCCCC`, `s = 3` gives `e = -8 < 0`; `x = 10` yields `(0xCCCCCCCC · 10)
  >> 35 = 0`, not 1.
- A 16-bit trick on a 32-bit value: `(x * 0xCCCD) >> 19` with `x = 81920` wraps the 32-bit product to
  16384 and yields 0; `81920 / 10` is 8192. Valid only when `x ≤ 65535` is proven.
- Signed sequence without the sign correction: `x = -17` gives `floor(-17·M / 2^34) = -2`; truncating
  division gives −1.
- Remainder with a different multiplier: `x - q * 9` where `q = x / 10`.

## Switching it off

`--disable-opt O0126`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after [#581](https://github.com/Hawkynt/Polypiler/issues/581) (O0131) has produced `MulHigh` from
half-word products and after range propagation; enables [#274](https://github.com/Hawkynt/Polypiler/issues/274) and re-lowering of the division
for the target's width.
