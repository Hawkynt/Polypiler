# O0123 — Recognize clamp from nested minimum/maximum and compare-select chains

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0123Clamp` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#272](https://github.com/Hawkynt/Polypiler/issues/272). Switched off with `--disable-opt O0123`.

## What it does

A value kept between two bounds is written as nested minimum and maximum or as a chain of
comparisons. With constant bounds in order the rule replaces both by `Clamp` of the IR. The C# backend
writes a clamp between constant ordered bounds as `Math.Min(Math.Max(x, lo), hi)`, which cannot cross;
the helper that traps on crossed bounds stays for every other clamp.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static int F(int x) => x < 0 ? 0 : x > 255 ? 255 : x;
```

### Before

With `--opt none`:

```csharp
public static int F(int x) => (x < 0) ? 0 : ((x > 255) ? 255 : x);
```

### After

By default; `--explain-opt` prints `O0123 clamp from nested minimum and maximum: 'F' statement 1`:

```csharp
public static int F(int x) => unchecked(global::System.Math.Min(global::System.Math.Max(x, 0), 255));
```

## When it applies

- `Min(Max(x, lo), hi)` and `Max(Min(x, hi), lo)` with the constants on either side;
- `x < lo ? lo : x > hi ? hi : x` and `x > hi ? hi : x < lo ? lo : x` in every spelling O0120 accepts,
  also once O0120 has turned the inner selection into `Min` or `Max`, on every integer type.

`lo <= hi` in the signedness of the type: then `Clamp` never traps and both nestings agree with it. In
the chain `x` is evaluated more than once and must have no effect and not trap.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeClamp.cs` and `clamp.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- Bounds out of order: `Min(Max(x, 255), 0)` is always 0, `Clamp` would trap.
- A bound that is not a constant.
- Two minimums.

## Switching it off

`--disable-opt O0123`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run. Follows O0120, which turns the inner selection of a chain into `Min` or `Max`.
