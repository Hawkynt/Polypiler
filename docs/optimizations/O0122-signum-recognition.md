# O0122 — Recognize signum from comparison-difference, shift-or and select idioms

<!-- status: implemented -->
> **Status: implemented and proof-gated.** `O0122Sign` recognizes the three-way sign of signed 8-, 16-, 32- and 64-bit integers in the current structured IR. Every replacement requires an SMT proof. Disable it with `--disable-opt O0122`.

## What it does

A three-way sign — −1, 0 or 1 — written as a difference of two comparisons, as
a shift-or of the sign mask and the negation, or as nested selections becomes
`Sign`, which the backends write as `Math.Sign`, `Math.sign` or the target's own
function. The smallest value is negative: `Sign.i32(0x80000000)` is −1.

The mathematical source is Warren, *Hacker's Delight*, 2nd ed., §2-8. No upstream
implementation is copied. The [fixture provenance](../../tests/fixtures/recognize/o0122/README.md)
pins the related forward idioms and the PowerBASIC `SGN` target spelling.

## Before and after

Save this as `Signum.cs`:

```csharp
public static class Signum {
  public static int F(int x) => (x > 0 ? 1 : 0) - (x < 0 ? 1 : 0);
}
```

```sh
polypiler translate Signum.cs --from csharp --to csharp --profile net10.0 --output out --explain-opt
```

In IR notation, before recognition:

```
%p = Select.i32 SGt(%x, 0), 1, 0
%n = Select.i32 SLt(%x, 0), 1, 0
%r = Sub.i32 %p, %n
```

After:

```
%r = Sign.i32 %x
```

The proof report includes
`O0122 applied: Sign.i32 from compare-difference [proof: smt-bitvector]`, and
the C# backend writes `global::System.Math.Sign(x)`.

## Matched forms and widths

- **compare-difference:** `(x > 0 ? 1 : 0) - (x < 0 ? 1 : 0)` — how C# writes
  `(x > 0) - (x < 0)` — with zero on either side of each comparison.
- **select:** nested selections of the constants −1, 0 and 1 under comparisons
  of `x` with zero, in any order and spelling: `x > 0 ? 1 : x < 0 ? -1 : 0`,
  `x < 0 ? -1 : x > 0 ? 1 : 0`, `x == 0 ? 0 : x > 0 ? 1 : -1`,
  `x != 0 ? (x >= 0 ? 1 : -1) : 0`, the nesting in either arm.
- **shift-or:** `(x >> (w - 1)) | (-x >>> (w - 1))` with the operands of `|` in
  either order: the first shift arithmetic, the second logical and of the
  negation (`-x`, `0 - x`, or, on a 32- or 64-bit `x`, anything whose weighted
  bits are the negation, such as `~x + 1`).

The first two forms are decided by one evaluation: an expression built of
integer constants, comparisons of one value with zero, selections and wrapping
sums and differences depends only on whether that value is negative, zero or
positive, so evaluating it for the three classes gives its whole meaning. It is
`Sign` exactly when it gives −1, 0 and 1.

The tree IR performs arithmetic at 32 or 64 bits; narrow values are promoted
and the result converted back explicitly. `(sbyte)((x > 0 ? 1 : 0) - (x < 0 ? 1 : 0))`
on a `short` becomes the sign converted to `sbyte`, with those conversions
included in the proof. The sign shift of a promoted narrow value may use its own
or the carrier's sign position. The value has no effects and cannot trap.

## Proof and library-host contract

`translate` supplies `SmtRecognitionProver`; a library host enables the rule with
`OptimizerOptions.Prover = new SmtRecognitionProver()`. The adapter encodes the
actual candidate, its conversions, comparisons (including `==` and `!=`) and
selections. Its independent definition of `Sign` is
`x > 0 ? 1 : x < 0 ? -1 : 0` by signed comparison; the sign of an unsigned value
is `x != 0 ? 1 : 0`. It does not reuse the recognizer's evaluation.

Each obligation has a five-second solver budget. Missing, unsupported, refuted,
unknown, unavailable or timed-out proofs leave the candidate unchanged, and
successful proof notes are published only after the optimizer commits the
replacement.

## When it must not fire

- The two-valued sign `(x >> 31) | 1`, which maps 0 to 1.
- An arithmetic shift of the negation, `(x >> 31) | (-x >> 31)`, which maps 5
  to −1; a logical shift of `x` instead of `-x`; a sign shift below `w - 1`.
- The swapped difference `(x < 0) - (x > 0)`, the negative sign.
- Unsigned comparisons: `ult(x, 0)` is never true, so the expression is `x != 0`.
- Comparisons of two different values, `(x > 0) - (y < 0)`; the three-way
  comparison `(a > b) - (a < b)` is not `Sign(a - b)` when the subtraction overflows.
- An operand that may trap or act; floating-point sign.
- A proof that cannot establish equivalence within its budget.

These are refusal notes, not source errors: `O0122 not applied: not equivalent to Sign`,
`O0122 not applied: unsigned comparison`, `O0122 not applied: operands differ`,
`O0122 not applied: operand may trap or have effects` and
`O0122 not applied: proof timeout`. A selection that does not separate the three
classes — a step on the way to a sign, or an unrelated choice — is silent.

## Verification and interaction

[SignRecognitionTests](../../Polypiler.Tests/Recognize/SignRecognitionTests.cs)
checks 13 spellings at all four widths, and `~x + 1` at 32 and 64 bits, on zero, ±1, ±5, maximum and minimum.
Every spelling is also checked exhaustively over all 256 signed 8-bit patterns,
both by the exported proof obligation and against the BCL's `Math.Sign`. At 16,
32 and 64 bits, every spelling compares 10,000 seeded inputs (seed 122) against
`Math.Sign` and the original IR: 390,000 wider-value comparisons plus 20,000 for `~x + 1`.

[Proof-adapter tests](../../Polypiler.Tests/Verification/Proofs/SmtRecognitionProverTests.cs)
check the signed and unsigned sign and equality comparisons. The
[acceptance feature](../../tests/features/recognize/rec-sign.feature) imports
executable source, verifies proof reports, minimum-value behaviour, the
counterexamples, disabling and timeout. The
[prototype fixture tests](../../Polypiler.Tests/Recognize/RecognitionFixtureTests.cs)
retain their C#/JavaScript differential checks and emission through every
profile of both backends.

The optimizer visits expressions before their operands, taking the largest
matched idiom first. Recognition runs before target legalization; target cost
selection remains a backend concern. `--opt none` and `--disable-opt O0122`
disable matching, proof requests and reports.
