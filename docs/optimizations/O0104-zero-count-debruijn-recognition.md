# O0104 — Recognize trailing zero count and floor-log2 from de Bruijn multiply-and-lookup

<!-- status: implemented -->
> **Status: implemented and proof-gated.** `O0104ZeroCountDeBruijn` recognizes the de Bruijn bit scans at 32 and 64 bits in the current structured IR. Every replacement requires an SMT proof that includes the table. Disable it with `--disable-opt O0104`.

## What it does

The fastest portable bit scan isolates the lowest set bit, multiplies it by a de
Bruijn constant and looks the top `log2 w` bits of the product up in a table of
`w` entries. The same lookup of the smear of `x` — `x` or-ed with its right
shifts by 1, 2, 4, …, `w/2` — gives the floor of the base-2 logarithm. The rule
replaces the lookup by `TrailingZeroCount` or by `(w - 1) - LeadingZeroCount`,
which the backends write as `BitOperations.TrailingZeroCount`, `Math.clz32` or a
helper, and the table usually becomes dead.

For `x = 0` both lookups read the table's first entry, which no zero count
returns: `TrailingZeroCount(0)` is `w`. The replacement keeps that value with a
selection on `x == 0`.

The method is Leiserson, Prokop and Randall, "Using de Bruijn Sequences to Index
a 1 in a Computer Word" (1998); the constants in the fixtures are the
public-domain ones of Sean Anderson's *Bit Twiddling Hacks* (sourcing rung 3).
The [fixture provenance](../../tests/fixtures/recognize/o0104/README.md) records
where each table comes from.

## Before and after

Save this as `Scan.cs`:

```csharp
public static class Scan {
  private static readonly byte[] Table = { 0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8, 31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9 };

  public static int TrailingZeros(uint x) => Table[((x & (uint)-x) * 0x077CB531u) >> 27];
}
```

```sh
polypiler translate Scan.cs --from csharp --to csharp --profile net10.0 --output out --explain-opt
```

In IR notation, before recognition:

```
%r = Load.u8 @Table[LShr.u32(Mul.u32(And.u32(%x, Neg.u32 %x), 0x077CB531), 27)]
```

After:

```
%r = Select.u8 Eq(%x, 0), 0, Trunc.u8(TrailingZeroCount.u32 %x)
```

The proof report is `O0104 applied: TrailingZeroCount.u32 from de Bruijn lookup [proof: smt-bitvector]`.
The C# backend writes
`(x == 0u) ? (byte)0 : (byte)global::System.Numerics.BitOperations.TrailingZeroCount(x)`
and drops the table; the JavaScript backend writes
`(x === 0) ? 0 : (TrailingZeroCount32(x) & 0xFF)`.

## Matched forms and widths

- **trailing zeros:** `table[((x & -x) * C) >> (w - log2 w)]`, the negation
  written `-x`, `0 - x` or through a wider type, the operands of `&` and `*` in
  either order.
- **floor-log2:** `table[(smear(x) * C) >> (w - log2 w)]`, the smear's steps
  `v | v >> s` in either order, in one expression or — as it is usually written
  — as a run of assignments to one variable, which the rule reads as one value.
- Any constant `C` with any table of exactly `w` entries that passes the check
  below; the check is on the pair, not on a list of known constants.
- Widths 32 and 64 (shift 27 or 58); the element type of the table is the type
  of the result, converted as the lookup was.

The table must be a constant of the module. The frontends make one of an array
that nothing writes; an array written anywhere is a global and is refused, since
its contents at the time of the load are unknown.

**The table check** is complete: for a non-zero `x`, `x & -x` takes only the `w`
powers of two `2^i`, and the smear only the `w` values `2^(i+1) - 1`. The table
must hold `i` at the index each of them reaches; the first table index that
holds anything else is reported. The C# frontend takes array indices of at most
32 bits, so a 64-bit scan converts its index, which the rule looks through.

## Proof and library-host contract

`translate` supplies `SmtRecognitionProver`; a library host enables the rule with
`OptimizerOptions.Prover = new SmtRecognitionProver()`. The adapter encodes the
actual lookup — the isolation or smear, the multiplication, the shift and the
table itself, as the choice among its entries by the index — against its
independent definitions of the zero counts. The one obligation covers the zero
case and every non-zero input, so the table check is the reporting step and the
proof is the decision.

A load from a constant table is total when its index cannot pass the table's
end: a logical right shift of a `w`-bit value by `k` leaves at most `2^(w-k)`
values, a mask with a non-negative constant at most the mask, and a conversion
keeps the bound when it fits below the target's sign bit. `IrShape.IsTrapFree`
counts such a load as total, and only such a load is encoded.

Each obligation has a five-second solver budget. Missing, unsupported, refuted,
unknown, unavailable or timed-out proofs leave the lookup unchanged, and
successful proof notes are published only after the optimizer commits the
replacement.

## When it must not fire

- A table with a wrong entry: `O0104 not applied: table check failed at index 2`
  for the trailing-zero table with entry 2 changed from 28 to 27; `x = 0x10000000`
  reads that entry.
- A table of another length:
  `O0104 not applied: table check failed: 31 entries for 32 positions`.
- A table that is written anywhere: `O0104 not applied: table not constant`.
- `x & (x - 1)`, which clears the lowest bit instead of isolating it:
  `O0104 not applied: operand is not an isolated bit`.
- A shift other than `w - log2 w`: `O0104 not applied: index is not multiply-shift`.
- A 16-bit scan, which the IR computes at 32 bits and cuts to 16 before the
  shift: `O0104 not applied: unsupported width`.
- An operand that may trap or act: `O0104 not applied: operand may trap or have effects`.
- Any other table lookup — a mask, a multiplicative hash, a product of two
  variables, an or that is no smear: silent.

## What is left

- A fact that `x` is non-zero would let the selection go; the rule always keeps
  it. The tree IR has no range analysis to supply the fact at an expression, and
  the backends write the selection as one comparison.
- 8- and 16-bit de Bruijn scans are not planned (see above).

## Verification and interaction

[ZeroCountDeBruijnRecognitionTests](../../Polypiler.Tests/Recognize/ZeroCountDeBruijnRecognitionTests.cs)
checks both forms at both widths on zero, every single bit and the values just
below, above and around it, against the BCL's `BitOperations`; 10,000 seeded
inputs per form (seed 104) spread over every bit position; another de Bruijn
constant with its own table; and every refusal.
[TableReadTrapTests](../../Polypiler.Tests/Recognize/TableReadTrapTests.cs)
checks which table loads are total, and the
[proof-adapter tests](../../Polypiler.Tests/Verification/Proofs/SmtRecognitionProverTests.cs)
the table encoding. The [acceptance feature](../../tests/features/recognize/rec-zerocount-debruijn.feature)
imports executable source and checks proof reports, the zero case, the
counterexamples, disabling and timeout.

The rule runs after O0103 in the pass pipeline of `translate`. The optimizer
visits expressions before their operands, taking the largest matched idiom
first. Target cost selection remains a backend concern. `--opt none` and
`--disable-opt O0104` disable matching, proof requests and reports.
