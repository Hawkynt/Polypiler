# O0122 — Recognize signum from comparison-difference, shift-or and select idioms

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0122Sign` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#579](https://github.com/Hawkynt/Polypiler/issues/579). Switched off with `--disable-opt O0122`.

## What it does

The sign of an integer is computed from two comparisons or from two shifts. The rule replaces both by
`Sign` of the IR, which the backends write as `Math.Sign`. Sourced from Warren, *Hacker's Delight*,
2nd ed., §2-7.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static int F(int x) => (x >> 31) | (int)((uint)-x >> 31);
```

### Before

With `--opt none`:

```csharp
public static int F(int x) => unchecked((x >> 31) | (int)((uint)(-x) >> 31));
```

### After

By default; `--explain-opt` prints `O0122 sign from comparisons or shifts: 'F' statement 1`:

```csharp
public static int F(int x) => global::System.Math.Sign(x);
```

## When it applies

- `(x > 0 ? 1 : 0) - (x < 0 ? 1 : 0)`, which is how C# writes `(x > 0) - (x < 0)`, with the comparisons
  in either spelling (`0 < x`);
- `(x >> (w - 1)) | (-x >>> (w - 1))` on a signed 32- or 64-bit `x`: the first shift arithmetic, the
  second logical and of the negation. Whether the operand of the second shift is the negation is decided
  on its weighted bits, so `0u - (uint)x` counts. The form is exact for the smallest value too: its
  negation is itself, the logical shift gives 1, the arithmetic one -1, and `-1 | 1` is -1.

The result, an `i32`, is converted to the type of the expression. `x` has no effect and cannot trap.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeSign.cs` and `sign.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- The swapped difference `(x < 0) - (x > 0)`, which is the negative sign.
- Comparisons of two different values.
- A logical shift of `x` instead of `-x`, or an arithmetic shift of `-x`.

## Switching it off

`--disable-opt O0122`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run.
