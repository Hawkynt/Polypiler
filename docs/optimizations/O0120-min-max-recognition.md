# O0120 — Recognize minimum and maximum from branchless mask selection and compare-select

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0120MinMax` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#571](https://github.com/Hawkynt/Polypiler/issues/571). Switched off with `--disable-opt O0120`.

## What it does

The smaller or the larger of two integers is selected by a comparison, or without a branch by a mask.
The rule replaces both by `Min` or `Max` of the IR, which the backends write as `Math.Min`/`Math.Max`.
Sourced from Warren, *Hacker's Delight*, 2nd ed., §2-18.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static int F(int a, int b) => b ^ ((a ^ b) & -(a < b ? 1 : 0));
```

### Before

With `--opt none`:

```csharp
public static int F(int a, int b) => unchecked(b ^ ((a ^ b) & -((a < b) ? 1 : 0)));
```

### After

By default; `--explain-opt` prints `O0120 minimum or maximum from a comparison and selection: 'F' statement 1`:

```csharp
public static int F(int a, int b) => global::System.Math.Min(a, b);
```

## When it applies

- `a < b ? a : b` and every mirrored form: `<=`, `>`, `>=`, the operands of the comparison swapped,
  the branches swapped. Equal operands give the same value either way. Operands widened without loss
  before the comparison - bytes compared as `int` - compare as their original values, and a constant
  compared after the widening is the same constant of the narrow type.
- The branchless `b ^ ((a ^ b) & -(a < b ? 1 : 0))`, with the mask also written `0 - (c ? 1 : 0)` or
  `c ? -1 : 0`: where the comparison holds it selects the other operand, elsewhere the kept one.

Both operands are evaluated once instead of up to twice and must have no effect and not trap.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeMinMax.cs` and `minmax.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- Floating-point operands, where NaN and the signed zeros make the forms differ from `Math.Min`.
- A comparison for equality, or branches that are not the compared operands.
- An operand that may trap: `values[0] < a ? values[0] : a`.

## Switching it off

`--disable-opt O0120`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run. O0123 recognizes a compare-select chain whose inner selection this rule turned into `Min` or `Max`.
