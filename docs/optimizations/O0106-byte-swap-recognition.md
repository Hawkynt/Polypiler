# O0106 — Recognize byte swap from shift-and-mask expressions and byte loops

<!-- status: implemented -->
> **Status: implemented with SMT proofs.** `O0106` runs in `translate` on the structured tree IR of ADR 0009. Disable it with `--disable-opt O0106`.

## What it does

Byte order is reversed with shifts and masks. The rule replaces such an expression by `ByteSwap` of
the IR, which the C# backend writes as `BinaryPrimitives.ReverseEndianness` from .NET Standard 2.1 on and
as a helper of three swap steps before, and the JavaScript backend as the `$bswap` helper.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static uint F(uint x) => (x >> 24) | ((x >> 8) & 0xFF00u) | ((x << 8) & 0xFF0000u) | (x << 24);
```

### Before

With `--opt none`:

```csharp
public static uint F(uint x) => (((x >> 24) | ((x >> 8) & 65280u)) | ((x << 8) & 16711680u)) | (x << 24);
```

### After

By default; `--explain-opt` prints `O0106 byte swap from shifts and masks: 'F' statement 1`:

```csharp
public static uint F(uint x) => unchecked(global::System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(x));
```

## When it applies

The weighted bits (`WeightEvaluator`) of the expression must be exactly the bits of one value of 16,
32 or 64 bits, bit `8k + j` on bit `8(n - 1 - k) + j`: the shift-and-mask forms in any order and with
the masks before or after the shifts, a swap built from rotates, an `int` swapped through `uint`, and
the two-step swap of halves and of bytes, including a single assignment followed
by an explicit rotate. Disjoint terms may be combined with OR, XOR or ADD.

A pure counted loop of `width / 8` iterations that shifts one byte from the
source into an accumulator is unrolled and proved. Its final source and counter
values remain available to later code.

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

`PermutationRecognitionTests` checks OR, XOR and ADD assemblies, byte loops
at 32 and 64 bits, trip-count boundaries, effectful bodies and proof refusals.
Each wide byte loop is compared with the original interpreter and an independent
byte oracle on 10,000 random inputs plus four boundaries (seed 106).
`rec-byteswap.feature` imports executable fixtures from
`tests/fixtures/recognize/o0106/`, including the rotation-ending cascade and
unmasked sign-fill and word-swap counterexamples. The earlier prototype fixtures
continue to check equivalence and emission through every C# and JavaScript
profile. Byte swaps are proved at their actual widths; no 8-bit proof is claimed
for a byte permutation.

## When it must not fire

- A byte in the wrong place or a byte missing.
- Arithmetic right shifts whose sign copies survive the masks. A mask that removes them is legal.
- A counted loop with the wrong number of byte transfers or an observable body effect.
- A failed, unknown, timed out or unavailable proof.

## Switching it off

`--disable-opt O0106`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run. A 16-bit rotation by 8 is left to this rule by O0100.
