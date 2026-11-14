# O0110 — Recognize parity from xor folding, nibble lookup and toggling loops

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0110Parity` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#573](https://github.com/Hawkynt/Polypiler/issues/573). Switched off with `--disable-opt O0110`.

## What it does

The parity of a value is computed by folding it with exclusive or, halving the distance each time, or
as the lowest bit of its population count. The rule replaces both by `Parity` of the IR, which the
backends write with the population count where the target has one.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static int F(uint x) { x ^= x >> 16; x ^= x >> 8; x ^= x >> 4; x ^= x >> 2; x ^= x >> 1; return (int)(x & 1); }
```

### Before

With `--opt none`:

```csharp
public static int F(uint x) {
  unchecked {
    x = x ^ (x >> 16);
    x = x ^ (x >> 8);
    x = x ^ (x >> 4);
    x = x ^ (x >> 2);
    x = x ^ (x >> 1);
    return (int)(x & 1u);
  }
}
```

### After

By default; `--explain-opt` prints `O0110 parity from an exclusive-or fold: 'F' statement 1`:

```csharp
public static int F(uint x) => unchecked((int)(uint)(global::System.Numerics.BitOperations.PopCount((uint)x) & 1));
```

## When it applies

The fold is evaluated bit by bit as exclusive ors of atom bits (`XorEvaluator`), exact for shifts,
masks and exclusive or, the arithmetic shift of a signed value included. It is the parity when bit 0 is
the exclusive or of the bits 0 to `k - 1` of one value, `k` its width or 8, 16 or 32 of its low bits,
and every other bit is zero - in one expression or as a run of assignments followed by the masking
return (`Windows`), JavaScript's fold through a variable that holds a double included.
`PopCount(x) & 1` becomes `Parity(x)` directly.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeParity.cs` and `parity.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- A fold that skips a step, which leaves out some bits.
- A fold without the final mask, whose other bits are not zero.
- A run whose variable is still read afterwards.

## Switching it off

`--disable-opt O0110`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run. A `PopCount` that O0101 or O0102 produced and that the source masks with 1 becomes a parity.
