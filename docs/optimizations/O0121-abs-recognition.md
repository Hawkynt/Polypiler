# O0121 — Recognize wrapping absolute value

<!-- status: implemented -->
> **Status: implemented and proof-gated.** `O0121AbsWrap` recognizes signed 8-, 16-, 32- and 64-bit wrapping magnitude in the current structured IR. Every replacement requires an SMT proof. Disable it with `--disable-opt O0121`.

## What it does

A signed magnitude written as a sign-mask calculation or a comparison becomes
`AbsWrap`. The smallest value maps to itself: `AbsWrap.i32(0x80000000)` is
`0x80000000`. It never becomes the trapping `Abs`.

The mathematical source is Warren, *Hacker's Delight*, 2nd ed., §2-4. No upstream
implementation is copied. The [fixture provenance](../../tests/fixtures/recognize/o0121/README.md)
pins the real scalar uses and related forward transformations.

## Before and after

Save this as `Magnitude.cs`:

```csharp
public static class Magnitude {
  public static int F(int x) {
    int m = x >> 31;
    return (x ^ m) - m;
  }
}
```

```sh
polypiler translate Magnitude.cs --from csharp --to csharp --profile net10.0 --output out --explain-opt
```

In IR notation, before recognition:

```
%m = AShr.i32 %x, 31
%r = Sub.i32 Xor.i32(%x, %m), %m
```

After:

```
%r = AbsWrap.i32 %x
```

The proof report includes
`O0121 applied: Abs.i32[wrap] from xor-subtract [proof: smt-bitvector]`.
The backend chooses a legal native or helper form; a call to trapping
`Math.Abs(int.MinValue)` would not preserve this operation.

## Matched forms and widths

- `(x ^ m) - m` and `(x + m) ^ m`, including commuted XOR/addition operands.
- Signed comparisons with zero using `<`, `<=`, `>` or `>=`, zero on either
  side, and the corresponding branches selecting `-x` or `0 - x` versus `x`.
- Arithmetic sign shifts of that same value, through stable local definitions
  and signed casts that preserve the sign mask.

The tree IR performs arithmetic and shifts at 32 or 64 bits. Narrow source
values are promoted and explicitly converted back. For example:

```csharp
public static sbyte F(sbyte x) {
  sbyte m = (sbyte)(x >> 7);
  return (sbyte)((x ^ m) - m);
}
```

This becomes `AbsWrap.i8(x)`, with the actual promotions and truncations included
in its proof. A sign-extended narrow value can use either its original sign
position or the carrier's sign position. An arbitrary truncation of a wide
magnitude must not become magnitude of the truncated input: truncating
`abs(129)` to `i8` yields −127, whereas `abs((i8)129)` yields 127.

The value has no effects and cannot trap. The two mask uses must resolve to
the same shift; a changed input or a zero-extended byte mask is refused.

## Proof and library-host contract

`translate` supplies `SmtRecognitionProver`. A library host enables the rule with
`OptimizerOptions.Prover = new SmtRecognitionProver()`. Recognition has no
dependency on the verification project.

The adapter encodes the actual candidate, stable definitions, conversions,
arithmetic, signed/unsigned comparisons and selections. Its independent
definition of signed `AbsWrap` compares with zero and selects the modular
negation or the input; unsigned `AbsWrap` is identity. This definition does not
reuse the recognizer's mask matcher. Unsupported pure integer boundaries are
independent inputs, so a proof must hold for every value they can produce.

Each obligation has a five-second solver budget. Missing, unsupported, refuted,
unknown, unavailable or timed-out proofs leave the candidate unchanged.
Only proved obligations enter the host's bounded cache; a zero budget refuses
before consulting it. Successful proof notes are published only after the
optimizer commits the replacement.

## When it must not fire

- A logical mask shift, a shift below the sign position, different mask
  definitions, or a mask of another value.
- An unsigned comparison with zero.
- Checked negation or subtraction, even if a particular input would not trap.
- The reversed selection `x < 0 ? x : -x`.
- An operand that may trap or act, including repeated loads or calls.
- A library `Abs` operation, floating-point magnitude or a separate
  absolute-difference operation.
- A proof that cannot establish equivalence within its budget.

These are refusal notes, not source errors. Examples include
`O0121 not applied: logical right shift`,
`O0121 not applied: checked arithmetic` and
`O0121 not applied: proof timeout`.

## Verification and interaction

[AbsRecognitionTests](../../Polypiler.Tests/Recognize/AbsRecognitionTests.cs)
checks 22 operator/operand-order spellings at all four widths, including
zero, ±1, maximum and minimum. Every spelling is also checked exhaustively over
all 256 signed 8-bit patterns, both by the exported proof obligation and against
an independent BCL magnitude. At 16, 32 and 64 bits, every spelling compares
10,000 seeded inputs (seed 121) against that magnitude and the original IR:
660,000 wider-value comparisons.

[Proof-adapter tests](../../Polypiler.Tests/Verification/Proofs/SmtRecognitionProverTests.cs)
check signedness, unsigned identity, Boolean inputs, false rewrites and solver
refusals. The [acceptance feature](../../tests/features/recognize/rec-abs.feature)
imports executable source, verifies proof reports, minimum-value behavior,
near misses, disabling and timeout. Existing
[prototype fixture tests](../../Polypiler.Tests/Recognize/RecognitionFixtureTests.cs)
retain their C#/JavaScript differential checks and emission through every
profile of both backends.

The optimizer visits expressions before their operands, taking the largest
matched idiom first. Recognition runs before target legalization and follows
stable definitions without removing their other uses. Target cost selection
remains a backend concern; recovered magnitude is not a promise of a speedup.
`--opt none` and `--disable-opt O0121` disable matching, proof requests and reports.
