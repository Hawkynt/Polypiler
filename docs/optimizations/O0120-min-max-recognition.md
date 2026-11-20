# O0120 — Recognize minimum and maximum from branchless mask selection and compare-select

<!-- status: implemented -->
> **Status: implemented and proof-gated.** `O0120MinMax` recognizes the signed and unsigned minimum and maximum of 8-, 16-, 32- and 64-bit integers in the current structured IR. Every replacement requires an SMT proof. Disable it with `--disable-opt O0120`.

## What it does

The smaller or the larger of two integers, selected by a comparison, by a
branchless mask, or by the sign of their difference, becomes `Min` or `Max` of
the IR, which the backends write as `Math.Min`/`Math.Max` or the target's own
function. The comparison's signedness decides between the signed and the
unsigned operation.

The mathematical sources are Warren, *Hacker's Delight*, 2nd ed., §2-19, and
Anderson, *Bit Twiddling Hacks* (public domain). No upstream implementation is
copied. The [fixture provenance](../../tests/fixtures/recognize/o0120/README.md)
pins the forward branchless selection and the target spelling.

## Before and after

Save this as `Extremes.cs`:

```csharp
public static class Extremes {
  public static int Min(int x, int y) => y ^ ((x ^ y) & -(x < y ? 1 : 0));

  public static int Bounded(ushort x, ushort y) {
    int d = x - y;
    return y + (d & (d >> 31));
  }
}
```

```sh
polypiler translate Extremes.cs --from csharp --to csharp --profile net10.0 --output out --explain-opt
```

In IR notation, before recognition:

```
%c = Select.i32 SLt(%x, %y), 1, 0
%r = Xor.i32 %y, And.i32(Xor.i32(%x, %y), Neg.i32 %c)
```

After:

```
%r = SMin.i32 %x, %y
```

The proof reports are `O0120 applied: SMin.i32 from mask-select [proof: smt-bitvector]`
and `O0120 applied: SMin.i32 from subtract-sign [proof: smt-bitvector]`; the C#
backend writes `global::System.Math.Min(x, y)` and `global::System.Math.Min((int)x, (int)y)`.

## Matched forms and widths

- **compare-select:** `a < b ? a : b` and every mirrored form: `<=`, `>`, `>=`,
  the operands of the comparison swapped, the branches swapped. Equal operands
  give the same value either way.
- **mask-select:** `b ^ ((a ^ b) & mask)` with the mask `-(a < b ? 1 : 0)`,
  `0 - (a < b ? 1 : 0)` or `a < b ? -1 : 0`, the operands of each `^` and `&` in
  either order. Where the comparison holds it selects the other operand,
  elsewhere the kept one; keeping `a` gives the maximum.
- **subtract-sign:** `b + (d & (d >> (w - 1)))` for the minimum and
  `a - (d & (d >> (w - 1)))` for the maximum, with `d = a - b`, also through a
  stable local. The shift is arithmetic by exactly `w - 1`.

The tree IR performs arithmetic at 32 or 64 bits; narrow values are promoted.
Conversions that keep every value — a byte compared as an `int` — are looked
through on the compared operands and on the selected ones, since they keep the
order as well. Every operand has no effect and cannot trap: the replacement
evaluates each once.

## Proof and library-host contract

`translate` supplies `SmtRecognitionProver`; a library host enables the rule with
`OptimizerOptions.Prover = new SmtRecognitionProver()`. The adapter encodes the
actual candidate with its conversions, comparisons and selections. Its
independent definition of `Min`/`Max` is the selection by a comparison in the
operands' signedness; it does not reuse the recognizer's matching.

The subtract-sign form is correct only where `a - b` cannot overflow. That
fact is not looked up: the proof of the actual candidate establishes it from the
conversions the frontend made. Operands widened from 16 bits, as in
`Bounded`, cannot overflow a 32-bit difference, and the proof holds. On unbounded
operands the solver finds the overflow — `x = 0x7FFFFFFF`, `y = -1` gives
`0x7FFFFFFF` where the minimum is −1 — and the rule reports
`O0120 not applied: subtraction may overflow`. `RuleContext.Confirm` reports a
refuted proof under the rule's own open condition this way; a timeout or an
unknown proof keeps its own report.

Each obligation has a five-second solver budget. Missing, unsupported, refuted,
unknown, unavailable or timed-out proofs leave the candidate unchanged, and
successful proof notes are published only after the optimizer commits the
replacement.

## When it must not fire

- The subtract-sign form where the subtraction may overflow.
- A mask of a comparison of other values, `y ^ ((x ^ y) & -(x < k))`, or of a
  comparison in the other signedness, `(uint)x < (uint)y` on `int` operands:
  `O0120 not applied: mask is not derived from the compared operands`.
- A logical sign shift `(uint)d >> 31`, which is 0 or 1, or a shift below
  `w - 1`: `O0120 not applied: not equivalent to Min or Max`.
- A checked subtraction: `O0120 not applied: checked arithmetic`.
- An operand that may trap or act: `O0120 not applied: operand may trap or have effects`.
- Floating-point operands, where NaN and the signed zeros make the forms differ
  from `Math.Min`; a comparison for equality; branches that are not the compared
  operands. These are silent.

## Verification and interaction

[MinMaxRecognitionTests](../../Polypiler.Tests/Recognize/MinMaxRecognitionTests.cs)
checks 12 compare-select and mask-select spellings, signed and unsigned, at all
four widths, and three subtract-sign spellings at 8 and 16 bits, on every pair
of nine edge values including both extremes. Every 8-bit spelling is checked
exhaustively over all 65,536 operand pairs, both by the exported proof
obligation and against the BCL's `Math.Min`/`Math.Max`. At 16, 32 and 64 bits,
every spelling compares 10,000 seeded pairs (seed 120) against the BCL and the
original IR: 780,000 wider-value comparisons.

[Proof-adapter tests](../../Polypiler.Tests/Verification/Proofs/SmtRecognitionProverTests.cs)
check the definitions, signedness and the refutation report. The
[acceptance feature](../../tests/features/recognize/rec-minmax.feature) imports
executable source, verifies proof reports, the extremes, the counterexamples,
disabling and timeout. The
[prototype fixture tests](../../Polypiler.Tests/Recognize/RecognitionFixtureTests.cs)
retain their C#/JavaScript differential checks and emission through every
profile of both backends.

The optimizer visits expressions before their operands, taking the largest
matched idiom first. O0123 recognizes a compare-select chain whose inner
selection this rule's `Select` orders. Target cost selection remains a backend
concern. `--opt none` and `--disable-opt O0120` disable matching, proof
requests and reports.
