# O0101 — Recognize population count from bit-counting loops

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0101PopCountLoop` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#278](https://github.com/Hawkynt/Polypiler/issues/278). Switched off with `--disable-opt O0101`.

## What it does

Code without a population-count function counts set bits in a loop. The rule replaces two loop
forms by `PopCount` of the IR, which the C# backend writes as `BitOperations.PopCount` from .NET Core 3.0
on and as the SWAR helper before, and the JavaScript backend as the `$pop` helper.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static int F(uint x) { int c = 0; while (x != 0) { c++; x &= x - 1; } return c; }
```

### Before

With `--opt none`:

```csharp
public static int F(uint x) {
  unchecked {
    int c = 0;
    while (x != 0u) {
      c = c + 1;
      x = x & (x - 1u);
    }
    return c;
  }
}
```

### After

By default; `--explain-opt` prints `O0101 population count from a counting loop: 'F' statement 2`:

```csharp
public static int F(uint x) {
  unchecked {
    int c = 0;
    c = c + global::System.Numerics.BitOperations.PopCount(x);
    x = 0;
    return c;
  }
}
```

## When it applies

- Kernighan's loop `while (x != 0) { c++; x &= x - 1; }`, the two statements in either order, `x` a
  32- or 64-bit variable, the condition `x != 0` (or `x > 0` for an unsigned `x`), `x - 1` in any spelling
  that has the same bits (JavaScript's `(x & (x - 1)) >>> 0`). Each pass clears the lowest set bit, so the
  loop runs exactly as often as `x` has set bits, for every value: it becomes `c = c + PopCount(x); x = 0;`.
- A counted loop over the width: `for (i = 0; i < w; i++) c += (x >> i) & 1;`, also as
  `if (((x >> i) & 1) != 0) c++;` or shifting `x` by one per pass, a JavaScript loop counting in doubles
  included. The counter loop (`CounterLoop`) starts at a constant, compares with a constant bound and adds
  one at the end of the body, whose statements only assign. The body is unrolled symbolically; the
  counted variable must end as its old value - or the constant it is known to start from - plus every
  bit of one value from before the loop, once each, and every other variable as a constant or as one
  shift of its old value. The loop becomes the assignments of those final values; the loop counter gets
  its final value, or disappears with its initialization when nothing reads it afterwards.

The counter is incremented with wrapping arithmetic; a count of at most 64 added at once wraps the same
way.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizePopCountLoop.cs` and `popcountloop.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- A loop over fewer passes than the width (`i < 31`), which counts only some bits.
- A test of two bits per pass (`(x >> i) & 3`), which counts some bits twice.
- A step other than `x & (x - 1)` (`x &= x - 2`).
- A counter that traps on overflow (`checked { c++; }`): the trap would move.

## Switching it off

`--disable-opt O0101`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run. Runs before the expression rules inside the loop, which would otherwise turn the bit test into
`TestBit` (O0108) first.
