# O0110 — Recognize parity from xor folding, nibble lookup and toggling loops

<!-- status: implemented -->
> **Status: implemented and proof-gated.** `O0110Parity` recognizes the parity of 8-, 16-, 32- and 64-bit integers from the exclusive-or fold, the nibble table and `PopCount(x) & 1` in the current structured IR. Every replacement requires an SMT proof. Disable it with `--disable-opt O0110`. The toggling loops of the work order are not recognized yet (see below).

## What it does

The parity of a value — 1 for an odd number of set bits — is computed by
folding the value with exclusive or, halving the distance each time; by folding
it to four bits and reading the parity of that nibble from the sixteen-bit table
`0x6996`; or as the lowest bit of its population count. The rule replaces each
by `Parity` of the IR, which the backends write with the population count where
the target has one.

The work order names `And(PopCount(x), 1)` as the canonical form. The IR has a
parity operation of its own, `Parity`, which the prototype already emitted and
every backend writes; the rule keeps it, so `PopCount(x) & 1` is itself one of
the recognized spellings rather than the target.

The mathematical source is Warren, *Hacker's Delight*, 2nd ed., §5-2. No
upstream implementation is copied; the table `0x6996` is copied as a constant.
The [fixture provenance](../../tests/fixtures/recognize/o0110/README.md) pins the
parity and LFSR helpers of the Cipher project.

## Before and after

Save this as `Parity.cs`:

```csharp
public static class Parity {
  public static int Fold(uint x) {
    x ^= x >> 16;
    x ^= x >> 8;
    x ^= x >> 4;
    x ^= x >> 2;
    x ^= x >> 1;
    return (int)(x & 1);
  }

  public static int Nibble(uint x) {
    x ^= x >> 16;
    x ^= x >> 8;
    x ^= x >> 4;
    return (0x6996 >> (int)(x & 0xF)) & 1;
  }
}
```

```sh
polypiler translate Parity.cs --from csharp --to csharp --profile net10.0 --output out --explain-opt
```

In IR notation, before recognition (the fold):

```
%a = Xor.u32 %x, LShr.u32(%x, 16)
%b = Xor.u32 %a, LShr.u32(%a, 8)
%c = Xor.u32 %b, LShr.u32(%b, 4)
%d = Xor.u32 %c, LShr.u32(%c, 2)
%e = Xor.u32 %d, LShr.u32(%d, 1)
%r = And.u32 %e, 1
```

After:

```
%r = Parity.u32 %x
```

The proof reports are `O0110 applied: Parity.u32 from xor-fold [proof: smt-bitvector]`
and `O0110 applied: Parity.u32 from nibble-lookup [proof: smt-bitvector]`; the
C# backend writes `BitOperations.PopCount(x) & 1` for both.

## Matched forms and widths

- **xor-fold:** the fold by halves down to one bit, in one expression or as a run
  of assignments followed by the masking statement, through logical or
  arithmetic shifts, the mask on either side.
- **nibble-lookup:** the fold down to four bits, then `(0x6996 >> (y & 15)) & 1`;
  conversions around the index are looked through, since only its four lowest
  bits are read.
- **popcount-mask:** `PopCount(x) & 1`.

The fold is evaluated bit by bit as exclusive ors of atom bits (`XorEvaluator`),
exact for shifts, masks and exclusive or, the arithmetic shift of a signed value
included. It is the parity when bit 0 is the exclusive or of every bit of one
value — or of its low 8, 16 or 32 bits, which becomes the parity of that value cut
to the width — and every other bit is zero. The table is the parity table when
entry `v`, at bit `v`, is the parity of `v`.

A fold that leaves out its *first* step is therefore not refused: on a `uint`,
the fold from `>> 8` down is exactly the parity of the low 16 bits and becomes
`Parity((ushort)x)`, proved like any other candidate. The work order lists that
spelling as a counterexample because it is not the parity of the whole word;
the rule keeps it, as the prototype did, because it is the parity of the low half
— the same reading lets a `ushort` copied into an `int` local be recognized.

The fold has at most `4·log2 w + 4` operations, each shared step counted once.
The value has no effect and cannot trap.

## Proof and library-host contract

`translate` supplies `SmtRecognitionProver`; a library host enables the rule with
`OptimizerOptions.Prover = new SmtRecognitionProver()`. The adapter encodes the
actual candidate. Its definitions are the textbook ones and share no code with
the recognizer: `Parity` as bit 0 of the halving fold with logical shifts,
`PopCount` as a balanced sum of the isolated bits. An arithmetic-shift fold or a
table lookup is thus proved against a different spelling. Flat chains over every
bit leave Z3 without a proof of 64-bit candidates; the halving definition proves
each 64-bit fold and lookup in well under a second.

`PopCount(x) & 1` on a 64-bit value is the exception: the carries of the count
against the exclusive-or definition are not proved within the budget, and the
candidate stays as written with `O0110 not applied: proof timeout`. At 8 to 32
bits it is proved.

Each obligation has a five-second solver budget. Missing, unsupported, refuted,
unknown, unavailable or timed-out proofs leave the candidate unchanged, and
successful proof notes are published only after the optimizer commits the
replacement.

## When it must not fire

- A fold that skips a middle step, which leaves bits out of every low part:
  without `x ^= x >> 4`, `x = 0x10` gives 0.
- The complement table `0x9669`, which gives the even parity bit: `x = 1` gives 0.
- A final mask of 3, or no mask, which leaves other bits.
- A fold larger than `4·log2 w + 4` operations: `O0110 not applied: expression too large`.
- A run whose variable is still read afterwards.

The first three are reported as `O0110 not applied: not equivalent to parity`
when the expression masks one value with 1, 2 or 3 and combines at least four of
its bits; other masked expressions are silent.

## Not yet recognized: toggling loops

The loops `p = 0; while (v != 0) { p ^= 1; v &= v - 1; }` and
`while (v != 0) { p ^= v & 1; v >>>= 1; }` run a number of times that depends on
the value. The prover unrolls only counted loops, so their equivalence cannot be
proved yet, and without a proof the rule may not replace them. Their refusal
reasons in the work order — `arithmetic right shift`, `loop body has effects`,
`loop value escapes` — have no producer until then.

## Verification and interaction

[ParityRecognitionTests](../../Polypiler.Tests/Recognize/ParityRecognitionTests.cs)
checks six spellings, signed and unsigned, at all four widths (the 64-bit
population count apart), on nine edge values. Every 8-bit spelling is checked
exhaustively over all 256 values, both by the exported proof obligation and
against the BCL's `BitOperations.PopCount`. At 16, 32 and 64 bits, every spelling
compares 10,000 seeded values (seed 110) against the BCL and the original IR:
340,000 comparisons. The fold-size bound is checked at 24 and 25 operations.

[Proof-adapter tests](../../Polypiler.Tests/Verification/Proofs/SmtRecognitionProverTests.cs)
check the two definitions against each other and a refuted complement. The
[acceptance feature](../../tests/features/recognize/rec-parity.feature) imports
executable source, verifies proof reports, the counterexamples, disabling and
timeout. The
[prototype fixture tests](../../Polypiler.Tests/Recognize/RecognitionFixtureTests.cs)
retain their C#/JavaScript differential checks and emission through every
profile of both backends.

The optimizer visits expressions before their operands, taking the largest
matched idiom first. A `PopCount` that O0101 or O0102 produced and that the
source masks with 1 becomes a parity. `--opt none` and `--disable-opt O0110`
disable matching, proof requests and reports.
