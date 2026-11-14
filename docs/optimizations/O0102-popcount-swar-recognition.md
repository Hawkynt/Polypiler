# O0102 — Recognize population count from the SWAR mask-and-add sequence

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0102PopCountSwar` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#575](https://github.com/Hawkynt/Polypiler/issues/575). Switched off with `--disable-opt O0102`.

## What it does

The population count of Hacker's Delight 5-1 adds bits in parallel fields: pairs, nibbles, bytes,
then sums the bytes with a multiplication or with shifts and adds. The rule recognizes the whole
sequence and replaces it by `PopCount` of the IR. Sourced from Warren, *Hacker's Delight*, 2nd ed., §5-1.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static int F(uint x) { x = x - ((x >> 1) & 0x55555555u); x = (x & 0x33333333u) + ((x >> 2) & 0x33333333u); x = (x + (x >> 4)) & 0x0F0F0F0Fu; return (int)((x * 0x01010101u) >> 24); }
```

### Before

With `--opt none`:

```csharp
public static int F(uint x) {
  unchecked {
    x = x - ((x >> 1) & 1431655765u);
    x = (x & 858993459u) + ((x >> 2) & 858993459u);
    x = (x + (x >> 4)) & 252645135u;
    return (int)((x * 16843009u) >> 24);
  }
}
```

### After

By default; `--explain-opt` prints `O0102 population count from the SWAR sequence: 'F' statement 1`:

```csharp
public static int F(uint x) => unchecked((int)(uint)global::System.Numerics.BitOperations.PopCount(x));
```

## When it applies

The value is evaluated as weighted bits (`WeightEvaluator`). Written out in binary, each weight
puts a bit into the fields of the positions it has set; a field holding the sum of `c` bits occupies its
position and the bits above that `c` needs. A mask keeps or drops whole fields, a right shift moves them
down, a sum merges aligned fields - and each of these is taken only while no field can carry into the
next. The result is the population count when it ends as one field at bit 0 that counts every bit of one
value of at least 8 bits exactly once.

This holds for the 32- and 64-bit sequences with the final multiplication (`(x * 0x01010101) >> 24`) and
with shifts and adds (`x += x >> 8; x += x >> 16; x & 0x3F`), for the variant that adds both halves
masked (`(x & 0x55555555) + ((x >> 1) & 0x55555555)`), for JavaScript's `Math.imul` form, and for a
16-bit count. The sequence may stand in one expression, in locals that are assigned once, or as a run of
assignments to one variable (`Windows`): the run is composed into one expression of the variable's value
before it, and recognized either as its final value (`x = PopCount(x)`) or inside the return or
assignment that follows and reads it, when nothing reads the variable afterwards.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizePopCountSwar.cs` and `popcountswar.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- A wrong mask (`0x13333333`) or a missing step: some field is cut or overflows.
- A final multiplication whose field sum overflows its field: `(b * 0x1111) >> 12` on nibble counts of
  16 bits, which gives 17 for the value `0x0010`.
- A run whose variable is still read afterwards: the intermediate value would be lost.

## Switching it off

`--disable-opt O0102`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run. Runs on statement runs before the expression rules see the single steps.
