# O0102 — Recognize population count from the SWAR mask-and-add sequence

<!-- status: implemented -->
> **Status: implemented and proof-gated.** `O0102PopCountSwar` recognizes the 16-, 32- and 64-bit SWAR population count in the current structured IR. A structural gate selects the candidate; an SMT proof decides. Disable it with `--disable-opt O0102`.

## What it does

The population count of Hacker's Delight adds bits in parallel fields — pairs,
nibbles, bytes — then sums the bytes with a multiplication or with shifts and
adds. The rule recognizes the whole sequence and replaces it by `PopCount` of the
IR, which the backends write as `BitOperations.PopCount` or the target's own
instruction. The sequence is textbook: Warren, *Hacker's Delight*, 2nd ed.,
Fig. 5-2 (sourcing rung 3); no implementation is copied. The
[fixture provenance](../../tests/fixtures/recognize/o0102/README.md) records that
no upstream file at the work order's pins contains the idiom in isolation.

## Before and after

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0 --explain-opt` on

```csharp
public static int F(uint x) {
  x = x - ((x >> 1) & 0x55555555u);
  x = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
  x = (x + (x >> 4)) & 0x0F0F0F0Fu;
  return (int)((x * 0x01010101u) >> 24);
}
```

In IR notation, before recognition:

```
%a = Sub.u32 %x, And.u32(LShr.u32(%x, 1), 0x55555555)
%b = Add.u32 And.u32(%a, 0x33333333), And.u32(LShr.u32(%a, 2), 0x33333333)
%c = And.u32 Add.u32(%b, LShr.u32(%b, 4)), 0x0F0F0F0F
%r = LShr.u32 Mul.u32(%c, 0x01010101), 24
```

After:

```
%r = PopCount.u32 %x
```

The proof report is `O0102 applied: PopCount.u32 from SWAR sequence [proof: smt-bitvector]`,
and the C# backend writes
`unchecked((int)(uint)global::System.Numerics.BitOperations.PopCount(x))`.

## Matched forms and widths

- The first step as a subtraction, `x - ((x >> 1) & 0x55…)`, or as two masked
  adds, `(x & 0x55…) + ((x >> 1) & 0x55…)`.
- The final fold as a multiplication, `(x * 0x0101…) >> (w - 8)`, or as shift-adds
  followed by a mask, `x += x >> 8; x += x >> 16; x & 0x3F`.
- Masks of 16, 32 and 64 bits; a 16-bit value counted in the 32-bit arithmetic
  C# widens it to; signed values, whose arithmetic shifts the masks clear.
- The sequence in one expression, through locals assigned once, or as a run of
  assignments to one variable, which `Windows` composes into one expression;
  JavaScript's `Math.imul` form.

## How it decides

Recognition is by proof. A **structural gate** selects a candidate: the root is
a final fold — a right shift by at least 8, or a mask of at most 8 bits — and the
expression, followed through stable locals, holds the three SWAR mask families
`0x55…`, `0x33…` and `0x0F…`. Every other expression, including each step of a
sequence on its own, is left alone silently. The candidate must then:

- read exactly one variable — otherwise `O0102 not applied: more than one variable`;
- neither trap nor act — otherwise `O0102 not applied: operand may trap or have effects`;
- have at most 24 operations — otherwise `O0102 not applied: expression too large`;
- use `0x55…` masks of 16, 32 or 64 bits — otherwise `O0102 not applied: unsupported width`
  (the 8-bit SWAR count is out of the work order's scope).

The **proof** then decides: the host's `IRecognitionProver` must show the actual
expression, with its conversions, equal to `PopCount` of the counted value. The
counted value is the one variable at its own width — a `ushort` widened to 32
bits is counted as the `ushort` — and the proof covers the widening.
`SmtRecognitionProver` defines `PopCount`
independently as the sum of every bit, one at a time; it shares nothing with the
SWAR steps. A refuted proof is reported as `O0102 not applied: not equivalent to PopCount`;
a timed-out, unknown or unavailable proof keeps its own report. Each obligation
has a five-second budget, and a successful proof note is published only after
the optimizer commits the replacement.

The tree IR has no 128-bit or vector type, so the work order's 128-bit and
per-lane forms have no counterpart here.

## When it must not fire

- A wrong mask, `0x33333332` in place of `0x33333333`: `x = 4` gives 0. (The work
  order states `x = 1`, but the cleared mask bit applies to bit 2 of the pair
  counts, so `x = 1` still counts 1.)
- 32-bit masks over a 64-bit value: `x = 0x0000000100000000` gives 0.
- A final shift of 16 instead of 24: `x = 1` gives `0x0101`.
- A second variable in the sequence, `(y >> 1) & 0x55555555`.
- A value that may trap, such as an array element.

## Verification

[PopCountSwarRecognitionTests](../../Polypiler.Tests/Recognize/PopCountSwarRecognitionTests.cs)
checks the four spellings (subtraction or masked adds, multiply or shift-add fold),
signed and unsigned, at 16, 32 and 64 bits on seven edge values against the BCL's
`BitOperations.PopCount`. Every 16-bit spelling discharges its exported obligation
exhaustively over all 65,536 inputs and agrees with the BCL on every one. At 32
and 64 bits, every spelling compares 10,000 seeded inputs (seed 102): 160,000
comparisons. The size limit is checked on both sides of 24 operations, and every
counterexample, unrelated expression, refusal and the disabling switch has its
case. The [acceptance feature](../../tests/features/recognize/rec-popcount-swar.feature)
imports executable source and verifies the proof reports, boundary values,
counterexamples, disabling and timeout. The
[prototype fixture tests](../../Polypiler.Tests/Recognize/RecognitionFixtureTests.cs)
retain their C#/JavaScript differential checks and emission through every
profile of both backends.

## Switching it off

`--disable-opt O0102`; on at `--opt default`, off at `--opt none`, which disable
matching, proof requests and reports.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the
frontend and the backend, with the other rules in the order of their numbers.
Runs on statement runs before the expression rules see the single steps; a run
is replaced only where nothing reads its variable afterwards.
