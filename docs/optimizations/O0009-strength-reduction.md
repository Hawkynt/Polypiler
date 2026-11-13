# O0009 — Replace multiplications, divisions and remainders by constants with cheaper exact forms

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#165](https://github.com/Hawkynt/Polypiler/issues/165). Once implemented it is switched off with `--disable-opt O0009`.

## What it does

Multiplication and especially division by a constant are the most expensive scalar operations a checksum or
codec performs, and on a 6502, a Z80 or in 8086 real mode there is no divide instruction worth using. Shifts,
masks and multiply-high sequences compute the same result exactly — if the signedness and rounding rules are
respected. PRD §4.8 (strength reduction), §23.5 (costs from the target profile).

## Example

### Before

```
%a = Mul.u32[wrap] %x, 8
%b = UDiv.u32 %x, 8
%c = URem.u32 %x, 8
%d = SDiv.i32[wrap] %y, 8
```

### After

```
%a = Shl.u32[strict] %x, 3
%b = LShr.u32[strict] %x, 3
%c = And.u32 %x, 7
%t = AShr.i32[strict] %y, 31
%u = LShr.i32[strict] %t, 29          // 7 when %y is negative, else 0
%v = Add.i32[wrap] %y, %u
%d = AShr.i32[strict] %v, 3
```

## When it applies

The constant operand is a literal `c ≠ 0`. `Mul` forms require the `[wrap]` contract, or a `[checked]`
multiplication for which [#94](https://github.com/Hawkynt/Polypiler/issues/94) proves the product fits (then it is first relaxed by
[#163](https://github.com/Hawkynt/Polypiler/issues/163)); a `[checked]` multiply that may overflow is not touched because `Shl` does not trap.
Unsigned division and remainder forms require an unsigned operation (`UDiv`, `URem`). Signed power-of-two
division uses the bias `(x >> (w−1)) >>> (w−k)` so that the quotient truncates toward zero; if the range
proves `x ≥ 0` the plain `AShr`/`LShr` is used. Magic division requires `0 < c < 2^w`, the constant pair
`(m, s)` computed by the Granlund–Montgomery algorithm, and an available widening multiply in the cost
profile; the obligation `∀x: UDiv(x, c) = mulhi(x, m) >> s` is proven per constant before the rewrite.
A division by the constant 1 or a multiplication by 0 or 1 belongs to [#154](https://github.com/Hawkynt/Polypiler/issues/154).

## How it is proven

`exhaustive-8bit+smt-bitvector` for the power-of-two and decomposition forms; `exhaustive-16bit+smt-bitvector` per magic constant. Solver budget 5 s per obligation; on timeout the original operation stays (`not applied: proof timeout`).

## When it must not fire

- `SDiv.i32 −7, 8` is 0 (truncation); `AShr.i32 −7, 3` is −1 — the unbiased shift is wrong for every negative non-multiple.
- `SRem.i32 −7, 8` is −7; `And.i32 −7, 7` is 1.
- `Mul.i32[checked] %x, 8` with `x = 0x10000000`: the multiplication traps, `Shl` returns −2147483648.
- Magic division with a constant one bit too short: `(x × 0xCCCCCCCC) >> 35` gives 0 for `x = 10`, the quotient is 1.
- `Mul x, 10` decomposed as `(x << 3) + (x << 1)` is exact under `[wrap]`; under `[sat]` the intermediate `x << 3` may clamp differently — `[sat]` multiplications are not decomposed.

## Switching it off

`--disable-opt O0009`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#163](https://github.com/Hawkynt/Polypiler/issues/163) (which supplies non-negativity and relaxed contracts) and before recognition re-runs; the legalizer may undo nothing — it sees only operations the profile prices.
