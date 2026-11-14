# O0105 — Recognize bit reversal from swap cascades and shift loops

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0105BitReverse` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#557](https://github.com/Hawkynt/Polypiler/issues/557). Switched off with `--disable-opt O0105`.

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

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeBitReverse.cs` and `bitreverse.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- A cascade with a missing step, which swaps but does not reverse.
- A loop of fewer passes than the width, or one that keeps bits of its old result (`r` a parameter
  shifted only 16 times).

## Switching it off

`--disable-opt O0105`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run. Understands rotates and byte swaps that O0100 and O0106 produced inside a cascade.
