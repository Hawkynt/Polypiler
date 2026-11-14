# O0106 — Recognize byte swap from shift-and-mask expressions and byte loops

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0106ByteSwap` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#559](https://github.com/Hawkynt/Polypiler/issues/559). Switched off with `--disable-opt O0106`.

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
the two-step swap of halves and of bytes as a run of assignments (`Windows`).

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeByteSwap.cs` and `byteswap.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- A byte in the wrong place or a byte missing.
- Arithmetic right shifts, which drag in copies of the sign.

## Switching it off

`--disable-opt O0106`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run. A 16-bit rotation by 8 is left to this rule by O0100.
