# O0126 — Recognize division and remainder by a constant from multiply-high sequences

<!-- status: implemented -->
> **Status: implemented and certificate-gated.** `O0126ReciprocalDivision` recovers unsigned and signed division and remainder by a constant from reciprocal multiplications of 16-, 32- and 64-bit dividends in the current structured IR. Every replacement requires an exact certificate. Disable it with `--disable-opt O0126`.

## What it does

Compilers and careful programmers replace `x / 10` by a multiplication with a magic reciprocal and a
shift; PB-Compiler's reciprocal division does it, and every lifted binary is full of it. In recovered
source the magic constants are unreadable, and a target with a different word size, a fast divider or
none at all needs the division back to choose its own lowering. The rule turns the sequence back into
`x / d` or `x % d`.

The divisor is derived from the multiplier and the shift and certified in exact integers; nothing is
looked up in a table of known magic numbers. The mathematics is Granlund and Montgomery, "Division by
Invariant Integers using Multiplication" (PLDI 1994) and Warren, *Hacker's Delight*, 2nd ed., ch. 10
(sourcing rung 3); no implementation is copied. The
[fixture provenance](../../tests/fixtures/recognize/o0126/README.md) pins the forward transformation.

## Before and after

Save this as `Divide.cs`:

```csharp
public static class Divide {
  public static uint Tenth(uint x) => (uint)(((ulong)x * 0xCCCCCCCDUL) >> 35);

  public static uint Last(uint x) {
    uint q = (uint)(((ulong)x * 0xCCCCCCCDUL) >> 35);
    return x - q * 10;
  }
}
```

```sh
polypiler translate Divide.cs --from csharp --to javascript --output out --explain-opt
```

In IR notation, before recognition:

```
%q = Truncate.u32 LShr.u64(Mul.u64(ZExt.u64 %x, 0xCCCCCCCD), 35)
%r = Sub.u32 %x, Mul.u32(%q, 10)
```

After:

```
%q = UDiv.u32 %x, 10
%r = URem.u32 %x, 10
```

The reports are
`O0126 applied: UDiv.u32 by 10 (M=0xCCCCCCCD, shift=35, e=2) from widened-multiply [proof: validated-rule]`
and `O0126 applied: URem.u32 by 10 from the quotient [proof: validated-rule]`.

## Matched forms

- **mul-high:** `MulHigh(x, M) >> s` — in C#, `Math.BigMul(x, M, out _) >> s` on 64-bit values.
- **widened-multiply:** the same high product written `(uint)(((ulong)x * M) >> j)`, then shifted
  further; a narrow operand multiplied in a wider type, `((uint)x16 * M) >> k`; and the signed
  `(int)(((long)x * M) >> k)`.
- **multiply:** an unsigned value multiplied in its own type, `(x * M) >> k`, which is a division only
  when the product cannot wrap — in practice a refusal, below.
- **add-back:** `t = MulHigh(x, M); (t + ((x - t) >> 1)) >> s`, the 33-bit reciprocal, whose effective
  multiplier is `2^w + M`.
- **signed:** the arithmetic high product shifted and corrected for negative dividends,
  `q - (x >> (w - 1))` or `q + (x >>> (w - 1))`; a narrow `short` corrected with `x >> 15` as C#
  writes it after promotion. The result truncates toward zero, as `x / d` does.
- **remainder:** `x - q * d` where `q` is one of the quotients above of the same `x`, or the division
  `x / d` the rule (or the source) already wrote. A target whose capability table prefers the remainder
  from the quotient (O0127) keeps that spelling, so the two rules do not undo each other.

Locals are followed through their stable definitions, so the high product, the add-back's `t` and the
quotient may each sit in a variable.

## The certificate

With the effective multiplier `M`, the total right shift `k` of the full product and the largest
dividend `X` (from its type, after promotion), the rule derives `d`, the integer nearest to `2^k / M`,
and `e = M·d − 2^k`.

- **Unsigned:** `2 ≤ d ≤ (X + 1) / 2`, `e ≥ 0`, `e·x' < 2^k` for the largest `x' ≤ X` with remainder
  `d − 1`, and `e·X < 2^k·(d − X mod d)`. Together they are necessary and sufficient for
  `⌊M·x / 2^k⌋ = ⌊x / d⌋` on `0..X`.
- **Signed:** `2 ≤ d ≤ 2^(w−1)`, `0 < M < 2^(w−1)`, `0 < e` and `e·2^(w−1) ≤ 2^k`; then the corrected
  quotient is the truncating `x / d` for every `x`, the smallest value included.
- **No wrap:** the full product must fit the type it is computed in — the signed part of it where the
  shift is arithmetic — and a truncated high product must fit its narrower type.

The certificate's numbers are in the report. It is the proof, proof class `validated-rule`: the rule is
validated exhaustively at 16 bits by its tests, and a solver run is not part of rewriting. A 32-bit
division by a constant does not finish within the five-second solver budget, a 64-bit high product
would need 128 bits, and even at 16 bits Z3 needs several seconds under load. The work order's
`proof timeout` refusal therefore has no counterpart: no solver is consulted, and the rule works
without a host prover.

## When it must not fire

- An off-by-one magic, `(x * 0xCCCCCCCC) >> 35`: `x = 10` gives 0.
  `O0126 not applied: reciprocal is not exact for the operand range`.
- A 16-bit trick on a 32-bit value, `(x * 0xCCCD) >> 19` with `x = 81920`, wraps to 0; the same trick
  in C#'s signed `int` arithmetic on a `ushort` wraps too. `O0126 not applied: multiplication may wrap`.
- A derived divisor above half the range, where only quotients 0 and 1 remain:
  `O0126 not applied: divisor out of range`.
- The signed sequence without its correction: `x = -17` gives −2, not −1.
  `O0126 not applied: missing sign correction` — also reported for a quotient held in a variable that
  is corrected in a later statement, since each expression is judged alone.
- `x - q * 9` after `q = x / 10`: `O0126 not applied: remainder uses another divisor`.
- An operand that may trap or act: `O0126 not applied: operand may trap or have effects`.
- A multiplier that is not near a reciprocal (`|e| > d`), or a bare high product shifted by exactly the
  dividend's width — the add-back's `t` — is not reported at all.
- Out of scope: a division by a power of two written as shifts with its signed fix-up, exact division
  by a modular inverse, signed reciprocals with a negative multiplier (the "add `x`" form), fusing the
  quotient and remainder into one `DivMod` (#274) and range facts other than the operand's type.

## Verification

[ReciprocalDivisionRecognitionTests](../../Polypiler.Tests/Recognize/ReciprocalDivisionRecognitionTests.cs)
recovers every fixture form and compares it with `BigInteger` division on the edges (0, d − 1, d, d + 1,
the largest `x'`, the extremes, −17) and 10,000 seeded inputs (seed 126) each. Its validation runs the
rule on 11,268 unsigned `(d, k, M)` cases at 16 bits — every divisor up to 300 and a geometric selection
up to 32,768, every shift from 16 to 33, the rounded-down and rounded-up multiplier — and requires the
rule to rewrite exactly those that divide all 65,536 inputs correctly (3,859 of them); and 2,330 signed
cases, of which the 469 accepted truncate correctly on all 65,536 inputs and every certified one is
accepted. The 16-bit fixture is also proved by the exhaustive prover over the exported obligation and by
Z3 with a longer budget. The [acceptance feature](../../tests/features/recognize/rec-divconst.feature)
checks the forms, the counterexamples and the switch on executable source.

## Switching it off

`--disable-opt O0126`; on at `--opt default`, off at `--opt none`.

## Interactions

Runs in the pass pipeline of `translate` with the other rules in the order of their numbers. The
recovered remainder and O0127's remainder-from-quotient read each other's output; the target's
capability table decides which spelling stays.
