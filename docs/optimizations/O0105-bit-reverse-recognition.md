# O0105 — Recognize bit reversal from swap cascades and shift loops

<!-- status: implemented -->
> **Status: implemented with SMT proofs.** `O0105` runs in `translate` on the structured tree IR of ADR 0009. Disable it with `--disable-opt O0105`.

## What it does

Bit reversal is written as the swap cascade of Hacker's Delight 7-1 or as a loop that moves one bit
per pass. The rule replaces both by `BitReverse` of the IR. Sourced from Warren, *Hacker's Delight*,
2nd ed., §7-1.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static uint F(uint x) { uint r = 0; for (int i = 0; i < 32; i++) { r = (r << 1) | (x & 1); x >>= 1; } return r; }
```

### Before

With `--opt none`:

```csharp
public static uint F(uint x) {
  unchecked {
    uint r = 0;
    {
      int i = 0;
      while (i < 32) {
        r = (r << 1) | (x & 1u);
        x = x >> 1;
        i = i + 1;
      }
    }
    return r;
  }
}
```

### After

By default; `--explain-opt` prints `O0105 bit reversal from a swap cascade or a bit loop: 'F' statement 2.1`:

```csharp
public static uint F(uint x) {
  unchecked {
    uint r = 0;
    {
      r = (uint)BitReverseBits((ulong)x, 32);
      x = 0;
    }
    return r;
  }
}
```

## When it applies

- The cascade - five steps on 32 bits, six on 64, three on a byte - in one expression, in locals,
  or as a run of assignments to one variable (`Windows`): its weighted bits (`WeightEvaluator`) must be
  exactly the bits of one value, bit `j` on bit `w - 1 - j`. The low bits of a wider value reversed into
  the low bits, the rest zero, are the reversal of that value cut to their width.
- A counted loop (`CounterLoop`) of `w` passes such as `r = (r << 1) | (x & 1); x >>= 1;`: unrolled
  symbolically, the result variable must end as the reversal of a value from before the loop - its own
  old bits shifted out, or its known start 0 - and every other variable it writes as a constant or one
  shift of its old value. The loop becomes those final assignments: `r = BitReverse(x); x = 0;`.

## How it is proven

Exact weighted-bit provenance first checks the permutation. The host-supplied
`IRecognitionProver` then checks the actual candidate against the intrinsic;
`translate` supplies `SmtRecognitionProver` and the pinned Z3 binding. Stable
local definitions are expanded, integer conversions keep their extension or
truncation, and shifts keep signedness and the tree IR's masked-count contract.
The intrinsic is independently expanded by bit positions, without using the
recognizer's weighted-bit implementation. Counted loops are unrolled before
proof, and every live exit local and counter is preserved.

Only `unsat` authorizes replacement. The budget is five seconds per obligation;
unknown, timeout, unsupported expressions and a missing solver leave the
candidate unchanged. Only proved obligations enter the bounded host-local
cache. `--explain-opt` reports `smt-bitvector` or the refusal. Library callers
must supply `OptimizerOptions.Prover`; no prover means no application of this
rule. `SmtRecognitionProver.CreateObligation` exports the runtime obligation for
independent verification.

`PermutationRecognitionTests` covers all 870 cascade step orders across
8, 16, 32 and 64 bits, the 40/41-instruction cascade boundary, proof failure and
disable paths, and 10,000 random inputs plus boundaries at each wide width
(seeds 105 for bit reversal and 106 for byte swap). The 8-bit bit-reversal
obligation is also checked exhaustively over all 256 inputs. Executable source
fixtures under `tests/fixtures/recognize/` are imported into tree IR by
`rec-bitreverse.feature` and `rec-byteswap.feature`; the prototype fixtures
continue to check equivalence and emission through every C# and JavaScript
profile. All input orders are proved at their actual widths; there is no
8-bit proof claimed for a byte permutation.

## When it must not fire

- A cascade with a missing step, which swaps but does not reverse.
- A swap cascade with more than `8 * log2(width)` distinct instructions, counting stable definitions and shared nodes once. Explicit bit-by-bit assemblies remain supported by exact provenance.
- A failed, unknown, timed out or unavailable proof.
- A loop of fewer passes than the width, or one that keeps bits of its old result (`r` a parameter
  shifted only 16 times).

## Switching it off

`--disable-opt O0105`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run. Understands explicit rotates and byte swaps inside a cascade, including those produced by O0100 and O0106.
