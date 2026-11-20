# O0108 — Recognize single-bit test, set, clear and flip with a variable index

<!-- status: implemented -->
> **Status: implemented and proof-gated.** `O0108SingleBit` recognizes single-bit test, set, clear and flip with a variable or constant position at 8, 16, 32 and 64 bits in the current structured IR. Every replacement requires an SMT proof. Disable it with `--disable-opt O0108`.

## What it does

Single-bit operations with a position are written with a shifted one. The rule
replaces them by `TestBit`, `SetBit`, `ClearBit` and `ToggleBit` of the IR. The
position it writes is always within the width — a constant inside it, or the
count masked to it — so the operation is total, and the backends write it with
the shift and the mask themselves or with the target's own bit instruction.

The identities are elementary (sourcing rung 3); no upstream implementation is
copied. The [fixture provenance](../../tests/fixtures/recognize/o0108/README.md)
pins the JavaScript `GetBit`/`SetBit` helpers and the PowerBASIC `BIT` spelling.

## Before and after

Save this as `Bits.cs`:

```csharp
public static class Bits {
  public static bool Test(uint x, int i) => ((x >> i) & 1) != 0;

  public static uint Set(uint x, int i) => x | (1u << i);
}
```

```sh
polypiler translate Bits.cs --from csharp --to csharp --profile net10.0 --output out --explain-opt
```

In IR notation, before recognition:

```
%t = Ne.u32 And.u32(LShr.u32(%x, %i), 1), 0
%s = Or.u32 %x, Shl.u32(1, %i)
```

After:

```
%t = TestBit.u32 %x, And.i32(%i, 31)
%s = SetBit.u32 %x, And.i32(%i, 31)
```

The proof reports are `O0108 applied: TestBit.u32 from shift-and-mask [proof: smt-bitvector]`
and `O0108 applied: SetBit.u32 from or-mask [proof: smt-bitvector]`. The C#
backend writes `((x >> (i & 31)) & 1u) != 0` and `x | (1u << (i & 31))`.

## Matched forms and widths

- **shift-and-mask:** `((x >> i) & 1) != 0`, also `== 0` (the negated test) and
  `== 1`, with the operands of `&` in either order, through a logical or an
  arithmetic shift: for a position within the width, bit 0 of the result is bit
  `i` of `x` either way.
- **mask-and-compare:** `(x & (1 << i)) != 0` and `== 0`, either order.
- **or-mask**, **and-not-mask**, **xor-mask:** `x | (1 << i)`, `x & ~(1 << i)`
  and `x ^ (1 << i)`, either order.

A shift of the tree IR takes its count modulo the width — as C#, Java and
JavaScript shift; `1u << 33` is `1u << 1` — while the intrinsics trap on a
position outside it. The rule therefore writes the position as `i & (w - 1)`,
unless it is a constant (reduced modulo the width) or already masked so. That
keeps the modulo behaviour and makes the operation total: `IrShape` treats a
single-bit operation as trap-free exactly when its position is a constant within
the width or an integer masked with such a constant.

The tree IR has only this one shift contract. The work order's `[strict]` shift,
which needs the range fact `0 ≤ n ≤ w-1` and its refusal
`index range unproven`, has no counterpart here.

Values of 8 and 16 bits are shifted after the frontend widens them to 32 bits,
and are matched there: `(byte)(x | (1 << n))` becomes `SetBit.i32` of the widened
value, converted back. This is exact for every position, including those beyond
the narrow width, where the original sets a bit the conversion then drops. The
constant one has the width of the operation; conversions between the signed and
unsigned type of one width change no bit and are looked through.

## Proof and library-host contract

`translate` supplies `SmtRecognitionProver`; a library host enables the rule with
`OptimizerOptions.Prover = new SmtRecognitionProver()`. The adapter encodes the
actual candidate and its truth-valued result (`==`, `!=`, logical not). Its
independent definitions build the single bit `1 << p` and test, or, and-not or
xor it with the value; they do not reuse the recognizer's matching. An operation
whose position may lie outside the width is not encoded at all.

Each obligation has a five-second solver budget. Missing, unsupported, refuted,
unknown, unavailable or timed-out proofs leave the candidate unchanged, and
successful proof notes are published only after the optimizer commits the
replacement.

## When it must not fire

- A mask shifted at 32 bits and widened: `x | (ulong)(1 << n)` on a `ulong` sets
  the whole upper half for `n = 31` and bit 8 for `n = 40`:
  `O0108 not applied: mask computed at another width`.
- A shifted constant other than one, `x | (2u << n)`:
  `O0108 not applied: shifted constant is not 1`.
- A tested field wider than one bit, `((x >> n) & 3) != 0`:
  `O0108 not applied: field wider than one bit`.
- A value or position that may trap or act:
  `O0108 not applied: operand may trap or have effects`.
- A constant mask, `x | 0x10`, which is already canonical, and
  `(x & (1 << i)) == 1`, which holds only for `i = 0`: silent.

## Verification and interaction

[SingleBitRecognitionTests](../../Polypiler.Tests/Recognize/SingleBitRecognitionTests.cs)
checks 16 spellings, signed and unsigned, at all four widths, on six values and
fifteen positions each, including 32, 33, 64, 65, −1 and the extremes of `int`,
against a definition computed with the BCL's shift operators. Every 8-bit
spelling is checked for all 256 values at every position from −2 to 70. At 16,
32 and 64 bits, every spelling compares 10,000 seeded value and position pairs
(seed 108): 960,000 comparisons.
[BitPositionTrapTests](../../Polypiler.Tests/Recognize/BitPositionTrapTests.cs)
checks which positions make the operations total.

[Proof-adapter tests](../../Polypiler.Tests/Verification/Proofs/SmtRecognitionProverTests.cs)
check the four definitions, a refuted shifted two and the refusal of an
unmasked position. The [acceptance feature](../../tests/features/recognize/rec-bittest.feature)
imports executable source, verifies proof reports, positions beyond the width,
the counterexamples, disabling and timeout. The
[prototype fixture tests](../../Polypiler.Tests/Recognize/RecognitionFixtureTests.cs)
retain their C#/JavaScript differential checks and emission through every
profile of both backends.

The optimizer visits expressions before their operands, taking the largest
matched idiom first. Target cost selection remains a backend concern.
`--opt none` and `--disable-opt O0108` disable matching, proof requests and
reports.
