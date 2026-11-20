# O0103 — Recognize leading and trailing zero counts from bit-scanning loops

<!-- status: implemented -->
> **Status: implemented and proof-gated.** `O0103ZeroCountLoop` recognizes guarded, unguarded and bit-length scanning loops over 8-, 16-, 32- and 64-bit values in the current structured IR. Every replacement requires an SMT proof. Disable it with `--disable-opt O0103`.

## What it does

Code without a bit-scan function counts zero bits in a loop until it meets a
set bit, or counts the bits of a value by shifting them out. The rule replaces
such a loop by `TrailingZeroCount`, `LeadingZeroCount` or `w - LeadingZeroCount`
of the IR, which the backends write as `BitOperations.TrailingZeroCount`/
`LeadingZeroCount`, `Math.clz32` or a helper. Both counts are the width for
zero.

The loops are textbook (Warren, *Hacker's Delight*, 2nd ed., §5-3 and §5-4;
sourcing rung 3). No upstream implementation is copied. The
[fixture provenance](../../tests/fixtures/recognize/o0103/README.md) pins the
range-coder consumer of these counts.

## Before and after

Save this as `Scan.cs`:

```csharp
public static class Scan {
  public static int Trailing(uint x) {
    if (x == 0)
      return 32;

    int c = 0;
    while ((x & 1) == 0) {
      x >>= 1;
      c++;
    }

    return c;
  }

  public static int Length(uint v) {
    int n = 0;
    while (v != 0) {
      v >>= 1;
      n++;
    }

    return n;
  }
}
```

```sh
polypiler translate Scan.cs --from csharp --to csharp --profile net10.0 --output out --explain-opt
```

The proof reports are
`O0103 applied: TrailingZeroCount.u32 from unguarded loop [proof: smt-bitvector]` and
`O0103 applied: LeadingZeroCount.u32 from bit-length loop [proof: smt-bitvector]`;
the C# backend writes:

```csharp
public static int Trailing(uint x) {
  if (x == 0u) {
    return 32;
  }
  var c = global::System.Numerics.BitOperations.TrailingZeroCount(x);
  x = x >> c;
  return c;
}

public static int Length(uint v) {
  unchecked {
    var n = 32 - global::System.Numerics.BitOperations.LeadingZeroCount(v);
    v = 0;
    return n;
  }
}
```

## Matched forms and widths

The counter starts at the constant 0 right before the loop and steps by one;
the body may also shift one variable by one (`x >>= 1`, `x <<= 1`, `x >>>= 1`)
and do nothing else.

- **guarded:** `while (n < w && e == 0)`, the test in either order. In every pass
  `r` below `w` the test must read exactly one bit of one value from before the
  loop: bit `r` for a trailing count, bit `w - 1 - r` for a leading one. Shifting
  the variable (`(x & 1) == 0` with `x >>= 1`) and testing a moving bit
  (`((x >> n) & 1) == 0`, `((x << n) & TOP) == 0`) both qualify.
- **unguarded:** the same loop without the bound `n < w`. It does not end for
  zero, so it is rewritten only where the value is known to be non-zero: after
  `if (x == 0) return …;` in the same block, or inside `if (x != 0) { … }`
  without an `else`, with nothing in between mentioning `x`. Widening and
  same-width conversions of `x` in that test are looked through; a narrowing one
  is not, since `(byte)x == 0` says nothing about `x`.
- **bit-length:** `while (v != 0) { v >>= 1; n++; }` with a logical shift
  becomes `n = w - LeadingZeroCount(v); v = 0;`. A narrow unsigned value shifted
  after its widening counts as logical; an arithmetic shift of a signed value
  does not end for a negative one.

Values of 8 and 16 bits are scanned on the promoted operations the frontend
wrote. A shifted variable ends shifted by the count — also for zero, which
shifts to zero either way — so a variable read after the loop keeps its value:
the loop becomes `n = TrailingZeroCount(x); x = x >> n;` (or the leading count
and `<<`).

## Proof and library-host contract

`translate` supplies `SmtRecognitionProver`; a library host enables the rule
with `OptimizerOptions.Prover = new SmtRecognitionProver()`. The loop is
unrolled symbolically: for each pass the condition and the values the body
writes are expressions of the values before the loop, and the final value of
each variable is the chain `c0 ? (c1 ? … : v1) : v0` of the passes' exit values.
The host proves the final counter equal to the count and the final shifted
variable equal to the value shifted by the count. The unguarded form is proved
under its non-zero fact (`x != 0 ? chain : count`), and the bit-length form only
after the bits show the shifted value is zero after `w` passes, so that the loop
has ended by then. The adapter's independent definitions of the counts test
every bit position in turn; they do not reuse the loop simulation.

Each obligation has a five-second solver budget. Missing, unsupported, refuted,
unknown, unavailable or timed-out proofs leave the loop unchanged, and
successful proof notes are published only after the optimizer commits the
replacement.

## When it must not fire

- An unguarded loop whose input may be zero, including a guard on another
  variable, a guard `x < 0`, or a write to `x` after the guard:
  `O0103 not applied: input may be zero`.
- A bit-length loop with an arithmetic shift: `O0103 not applied: arithmetic right shift`.
- A bound other than the width (`n < 31`, `n < 33`, `n < 32` over a `ulong`):
  `O0103 not applied: bound differs from width`.
- A counter that starts elsewhere than 0 (a scan from bit 1 gives 32 for 1), a
  test of a bit the count does not reach, a shift by two, a second shifted
  variable, a counter that steps by two, a left-shifting `v != 0` loop or a
  narrow signed bit-length loop: `O0103 not applied: unrecognized loop form`.
- A store or a call in the body: `O0103 not applied: loop body has effects`.
- Loops that do not scan bits — a search `while (i < n && a[i] != 0)`, a plain
  counted loop — are silent.

The SSA notion "a loop value escapes" has no counterpart here: the rule keeps
the final value of every variable the loop writes.

## Verification and interaction

[ZeroCountLoopRecognitionTests](../../Polypiler.Tests/Recognize/ZeroCountLoopRecognitionTests.cs)
imports seven forms from C# for each of the eight integer types (the narrow
signed bit-length loop is a counterexample instead) and checks nine edge values
each, including zero, one, the top bit and all ones, against the BCL's
`BitOperations` counts. Every 8-bit form is checked for all 256 values. At 16,
32 and 64 bits every form compares 10,000 seeded inputs (seed 103), each a random
value shifted by a random amount so that every count is reached: 410,000
comparisons.

[Proof-adapter tests](../../Polypiler.Tests/Verification/Proofs/SmtRecognitionProverTests.cs)
check the two definitions. The
[acceptance feature](../../tests/features/recognize/rec-zerocount-loop.feature)
imports executable source, verifies proof reports, the zero input, the
counterexamples, disabling and timeout. The
[prototype fixture tests](../../Polypiler.Tests/Recognize/RecognitionFixtureTests.cs)
retain their C#/JavaScript differential checks and emission through every
profile of both backends.

Recognition runs before target legalization; target cost selection remains a
backend concern. `--opt none` and `--disable-opt O0103` disable matching, proof
requests and reports.
