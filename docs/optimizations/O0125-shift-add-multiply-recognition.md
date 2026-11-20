# O0125 — Recognize multiplication by a constant from shift-add and shift-subtract chains

<!-- status: implemented -->
> **Status: implemented and proof-gated.** `O0125ShiftAddMultiply` recognizes multiplication by a constant from wrapping shift-add and shift-subtract chains at 8, 16, 32 and 64 bits in the current structured IR. Every replacement requires an SMT proof. Disable it with `--disable-opt O0125`.

## What it does

Multiplication by a constant is often written as a sum of shifted copies. The
rule reads such an expression as `c · x` modulo `2^w` and replaces it by one
multiplication, which the backends write as `x * c`.

The identities are ring arithmetic modulo `2^w` (sourcing rung 3); no upstream
implementation is copied. The [fixture provenance](../../tests/fixtures/recognize/o0125/README.md)
pins the forward decomposition whose output this rule recovers.

## Before and after

Save this as `Multiply.cs`:

```csharp
public static class Multiply {
  public static uint Ten(uint x) => (x << 3) + (x << 1);
}
```

```sh
polypiler translate Multiply.cs --from csharp --to csharp --profile net10.0 --output out --explain-opt
```

In IR notation, before recognition:

```
%r = Add.u32 Shl.u32(%x, 3), Shl.u32(%x, 1)
```

After:

```
%r = Mul.u32 %x, 10
```

The proof report is `O0125 applied: Mul.u32 by 10 from 2 shift/add terms [proof: smt-bitvector]`,
and the C# backend writes `unchecked(x * 10u)`.

## Matched forms and widths

The expression is read as a linear form in one value `x`:

- wrapping `+` and `-` add and subtract coefficients; `-x` (and `0 - x`) negates;
- `<<` by a constant scales by `2^k`, the count taken modulo the width as the
  tree IR's shifts take it; `*` by a constant scales by that constant;
- conversions between the signed and unsigned type of one width change no bit;
- a stable local stands for its definition (cycles are cut off).

Every other node is a leaf and must be the one value `x`, which must occur at
least twice: `(x << 3) + (x << 1)` (10), `(x << 4) - x` (15),
`((x << 2) + x) << 1` (10), `x + x + x` (3), `(x * 5) + (x << 1)` (7),
`x - (x << 2)` (−3, written `0xFFFFFFFD` at `u32`). Negative coefficients, minus
one among them, are rewritten. A coefficient of 0, 1 or a power of two is left to
constant folding and the shift form.

Values of 8 and 16 bits are shifted and added after the frontend widens them to
32 bits; the chain is matched there and the product converted back, which wraps
exactly as the original does: for a `byte` `x = 200`, `(byte)(x * 10)` is 208.

## Proof and library-host contract

`translate` supplies `SmtRecognitionProver`; a library host enables the rule with
`OptimizerOptions.Prover = new SmtRecognitionProver()`. The adapter encodes the
actual chain as written. It encodes a multiplication by a constant `c` by its
definition modulo `2^w`: the sum of the other operand shifted to each set bit of
`c` — or the negation of that sum for `-c` when `-c` has fewer set bits. The
expansion is taken from the constant alone, not from the recognizer's reading
of the chain. A bit-blasted 64-bit multiplier costs Z3 several seconds, near the
budget under load; the expansion proves the same 64-bit four-term chain in
about 0.2 seconds.

Each obligation has a five-second solver budget. Missing, unsupported, refuted,
unknown, unavailable or timed-out proofs leave the candidate unchanged — a chain
whose proof does not finish in time stays as written — and successful proof
notes are published only after the optimizer commits the replacement.

## When it must not fire

- `|` or `^` in place of `+`: `(x << 3) | (x << 1)` is 42 for `x = 5`, not 50:
  `O0125 not applied: combiner is not Add or Sub`.
- A checked addition, subtraction, negation or multiplication: the product would
  trap on other inputs than the chain does:
  `O0125 not applied: non-wrapping arithmetic`. The work order's
  undefined-overflow (C profile) addition has no counterpart in the tree IR,
  whose arithmetic either wraps or carries the checked contract.
- Two values with at least two scaled terms, `(x << 3) + (y << 1)`:
  `O0125 not applied: more than one variable`.
- A shift by a variable amount, `(x << n) + x`:
  `O0125 not applied: non-constant shift amount`.
- A coefficient of 0, 1 or a power of two, `(x << 1) - x`, `x - x`,
  `(x << 1) + (x << 1)`: `O0125 not applied: coefficient is trivial`.
- A value that may trap or act: `O0125 not applied: operand may trap or have effects`.
- Silent: a single shift or multiplication, a right shift or mask in the chain
  (not linear), an additive constant, `| ` or `^` between unscaled values, and
  two values with fewer than two scaled terms (`x + y`, `(hi << 16) | lo`).

## Verification and interaction

[ShiftAddMultiplyRecognitionTests](../../Polypiler.Tests/Recognize/ShiftAddMultiplyRecognitionTests.cs)
checks 12 spellings, signed and unsigned, at all four widths, on eight values
including 0, 1, all ones and both signed extremes, against the BCL's wrapping
product. Every 8-bit spelling is checked exhaustively over all 256 values, both
by the exported proof obligation and against the BCL. At 16, 32 and 64 bits,
every spelling compares 10,000 seeded inputs (seed 125): 720,000 comparisons.

[Proof-adapter tests](../../Polypiler.Tests/Verification/Proofs/SmtRecognitionProverTests.cs)
check the product encoding for positive, negative, zero and wrong
coefficients and for a constant on either side. The
[acceptance feature](../../tests/features/recognize/rec-mulconst.feature)
imports executable source, verifies proof reports, wrapping, the
counterexamples, disabling and timeout. The
[prototype fixture tests](../../Polypiler.Tests/Recognize/RecognitionFixtureTests.cs)
retain their C#/JavaScript differential checks and emission through every
profile of both backends.

The optimizer visits expressions before their operands, taking the largest
matched chain first; if a chain's proof fails, a smaller chain inside it may
still be proved. Choosing between the multiplication and the shift-add spelling
per target (O0009 and the backends) is not part of this rule.
`--opt none` and `--disable-opt O0125` disable matching, proof requests and
reports.
