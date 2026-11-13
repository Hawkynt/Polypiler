# O0127 — Fuse a division and a remainder of the same operands into DivMod

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#274](https://github.com/Hawkynt/Polypiler/issues/274). Once implemented it is switched off with `--disable-opt O0127`.

## What it does

Digit extraction, radix conversion and index splitting compute `x / y` and `x % y` side by side —
CompressionWorkbench prints `versionNeeded / 10` and `versionNeeded % 10` in one expression. Hardware
dividers produce both results at once, C# has `Math.DivRem`, Python `divmod`, and on a CPU without a
divider one software routine can return both. PRD §4.3 lists `DivMod` among the operations that stay
recognizable; PRD §8 P2 asks for "integer `DivMod` where safe" — safe meaning that no division is
executed that the source would not have executed, and that signedness and rounding of the pair agree.

## Example

### Before

```
%q = UDiv.u32 %x, %y
%r = URem.u32 %x, %y
```

### After

```
%d = DivMod.u32[trunc] %x, %y
%q = Quot %d
%r = Rem %d
```

## When it applies

both instructions take the *same* SSA values `x` and `y` in the same
operand positions; they agree in signedness, in rounding mode and in trap contract (division by zero,
and the most-negative-by-minus-one case for signed types, each either trapping in both or defined in
both, as the source profiles of the two instructions state); one instruction dominates the other, and
the `DivMod` is placed at the dominating one — the shared contract becomes the attributes of the
`DivMod` as [#99](https://github.com/Hawkynt/Polypiler/issues/99) defines them (`[trunc]`, `[floor]` or the Euclidean mode; `ovf=wrap|trap`;
`zero=trap|zero`) — so a division with these operands was executed there anyway and any trap occurs at
the same point as before; nothing is moved across an effect. If the dominated instruction is the one
that trapped in the source while the dominating one was defined (possible only with differing
contracts), the pair is refused by the contract check.

## How it is proven

`exhaustive-8bit` over all `(x, y)` with `y ≠ 0` for each signedness and rounding
mode (quotient and remainder projections against the separate operations) plus `smt-bitvector` at 16,
32 and 64 bit; dominance and the identical trap contract are structural facts checked by the rule and
recorded as `EffectFact` obligations. A failed, unknown or timed-out obligation (budget 5 s each)
leaves the IR unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Mixed signedness: `UDiv.u32` and `SRem.i32` on `x = 0xFFFFFFF9`, `y = 2`: the unsigned quotient is
  `0x7FFFFFFC` and the signed remainder is −1; no single `DivMod` yields both (the unsigned remainder
  is 1).
- Mixed rounding: truncating `-17 / 5 = -3` with flooring `-17 mod 5 = 3`; `DivMod[trunc]` would give
  remainder −2.
- Sibling branches: `if (c) q = x / y; else r = x % y;` — fusing requires a division before the
  branch; when `c` is false and the profile makes only `/` trap on zero, a trap would appear on a path
  that had none.
- Different divisor values that happen to be equal at run time (`y` and a reloaded `y2`): not the same
  SSA value, so not provably the same division.

## Switching it off

`--disable-opt O0127`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after global value numbering (so equal operands are the
same SSA value) and after [#565](https://github.com/Hawkynt/Polypiler/issues/565) where present; enables single-division lowering.
