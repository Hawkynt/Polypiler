# O0108 — Recognize single-bit test, set, clear and flip with a variable index

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0108SingleBit` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#558](https://github.com/Hawkynt/Polypiler/issues/558). Switched off with `--disable-opt O0108`.

## What it does

Single-bit operations with a position are written with a shifted one. The rule replaces them by
`TestBit`, `SetBit`, `ClearBit` and `ToggleBit` of the IR. Where the position cannot be outside the
width - a constant inside it, or a count masked to it, which is what the rule writes - the C# backend
writes the operation with the shift and the mask themselves; elsewhere it calls the helper that traps.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static bool F(uint x, int i) => ((x >> i) & 1) != 0;
```

### Before

With `--opt none`:

```csharp
public static bool F(uint x, int i) => ((x >> i) & 1u) != 0u;
```

### After

By default; `--explain-opt` prints `O0108 single-bit test, set, clear or toggle: 'F' statement 1`:

```csharp
public static bool F(uint x, int i) => unchecked((((x >> (i & 31)) & 1u) != 0));
```

## When it applies

- `((x >> i) & 1) != 0`, `== 0` and `== 1`, with an arithmetic or a logical shift - bit 0 of
  `x >> i` is bit `i` of `x` either way;
- `(x & (1 << i)) != 0` and `== 0`;
- `x | (1 << i)`, `x & ~(1 << i)`, `x ^ (1 << i)`, the shifted one on either side.

`x` is a 32- or 64-bit value (a narrower one widened before the shift counts as its widening), the one
has its width, and conversions between the signed and unsigned type of one width are looked through. A
shift of the IR takes its count modulo the width while these operations trap outside it, so the
position becomes `i & (w - 1)`, unless it is a constant or already masked so. Value and position have no
effect and cannot trap.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeSingleBit.cs` and `singlebit.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- More than one bit: `(x >> i) & 3`, `x | (3 << i)`.
- `(x & (1 << i)) == 1`, which is true only for bit 0.
- A one of another width: `x | (long)(uint)(1 << i)` on a `long`.
- A value that may trap: `a[0] | (1 << i)`.

## Switching it off

`--disable-opt O0108`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run. O0101 runs on a counting loop before this rule could turn its bit test into `TestBit`.
