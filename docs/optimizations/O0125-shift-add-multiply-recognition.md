# O0125 — Recognize multiplication by a constant from shift-add and shift-subtract chains

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0125ShiftAddMultiply` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#572](https://github.com/Hawkynt/Polypiler/issues/572). Switched off with `--disable-opt O0125`.

## What it does

Multiplication by a constant is written as a sum of shifted copies. The rule replaces such a sum by one
multiplication, which every target computes at least as fast.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static int F(int x) => (x << 3) + (x << 1);
```

### Before

With `--opt none`:

```csharp
public static int F(int x) => unchecked((x << 3) + (x << 1));
```

### After

By default; `--explain-opt` prints `O0125 multiplication by a constant from shifts and adds: 'F' statement 1`:

```csharp
public static int F(int x) => unchecked(x * 10);
```

## When it applies

Addition, subtraction, negation and shifts to the left are exact on weighted bits modulo the width
(`WeightEvaluator`). A sum or difference of one value's shifted copies - `(x << 3) + (x << 1)`,
`(x << 4) - x`, longer chains, `h += h << 10` - is `c · x` when every bit `j` of the one value has the
weight `c · 2^j`; for a narrower signed value, which is sign-extended, the top bit the weight
`-c · 2^j`. The result is `x * c` with the value converted to the type of the expression. Only wrapping
arithmetic takes part. The value is evaluated once instead of several times and must have no effect and
not trap.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeMultiply.cs` and `multiply.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- A factor of 0, 1, -1 or a power of two, which stays a shift or a negation.
- Two different values: `(x << 3) + (y << 1)`.
- Trapping arithmetic (`checked`): the trap would move.
- An expression without an addition or subtraction - a lone shift.

## Switching it off

`--disable-opt O0125`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run.
