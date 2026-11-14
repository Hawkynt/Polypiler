# O0140 — Recognize little- and big-endian loads from byte loads combined by shifts

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0140EndianLoad` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#275](https://github.com/Hawkynt/Polypiler/issues/275). Switched off with `--disable-opt O0140`.

## What it does

Portable code reads a multi-byte integer one byte at a time and joins the bytes with shifts. The rule
replaces 2, 4 or 8 such loads by `LoadLittle` or `LoadBig` of the IR, which the C# backend writes as
`BinaryPrimitives.ReadUInt32LittleEndian` and its siblings from .NET Standard 2.1 on and as a helper
before, and the JavaScript backend as the `$load` helper.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static uint F(byte[] b, int i) => (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24);
```

### Before

With `--opt none`:

```csharp
public static uint F(byte[] b, int i) => unchecked((uint)(((b[i] | (b[i + 1] << 8)) | (b[i + 2] << 16)) | (b[i + 3] << 24)));
```

### After

By default; `--explain-opt` prints `O0140 endian load from byte loads: 'F' statement 1`:

```csharp
public static uint F(byte[] b, int i) => unchecked(global::System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(new global::System.ReadOnlySpan<byte>(b, i, 4)));
```

## When it applies

The expression is evaluated as weighted bits (`WeightEvaluator`) whose atoms are the byte loads. Each
byte must land, all eight bits in order, on its own byte slot, and the slots must fill the whole width of
the expression - so a pair of bytes inside a 32-bit sum waits for the whole sum. The bytes come from one
`byte[]` variable at one base index plus consecutive constants (`i`, `i + 1`, …, constant indices from
0), joined by `|`, `+` or `^`; the slot of offset `k` is `k` for little-endian and `n - 1 - k` for
big-endian. The combined load traps, before reading anything, exactly when one of the byte loads would,
with the same kind of trap, and the byte loads have no effect that the earlier trap could skip. An index
computed in JavaScript's doubles traps with an overflow where it passes the largest `int32`; such indices
are accepted only when the load at the base itself is evaluated first and no offset is negative, so
that an index that large has already failed as out of range.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeEndianLoad.cs` and `endianload.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- A gap (`i + 3` after `i + 1`), a byte out of order, or the same byte twice.
- Bytes of two arrays.
- Signed bytes (`sbyte[]`), which the idiom masks.
- Fewer bytes than the width of the expression: `b[i] | b[i + 1] << 8` as an `int`.
- In JavaScript, the highest byte loaded first (`b[i + 3] << 24 | … | b[i]`): its index could overflow
  before the base fails.

## Switching it off

`--disable-opt O0140`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run.
