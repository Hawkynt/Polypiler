# O0127 — Fuse a division and a remainder of the same operands into DivMod

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0127QuotientRemainder` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#274](https://github.com/Hawkynt/Polypiler/issues/274). Switched off with `--disable-opt O0127`.

## What it does

A remainder `a % b` that follows the quotient `q = a / b` of the same operands needs no second
division: `a - q * b` is the same. The rule makes that replacement where the target's capability table
says it is cheaper than a second division (`RuleTarget.RemainderFromQuotient`): the C# table says so,
because the .NET JIT emits one division instruction per operator; the JavaScript table does not, because
an engine computes the remainder of two integers in one instruction and the product needs `Math.imul`.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static int F(int a, int b) { int q = a / b; int r = a % b; return q * 1000 + r; }
```

### Before

With `--opt none`:

```csharp
public static int F(int a, int b) {
  unchecked {
    var q = a / b;
    var r = a % b;
    return (q * 1000) + r;
  }
}
```

### After

By default; `--explain-opt` prints `O0127 remainder from the quotient of the same operands: 'F' statement 1`:

```csharp
public static int F(int a, int b) {
  unchecked {
    var q = a / b;
    var r = a - (q * b);
    return (q * 1000) + r;
  }
}
```

## When it applies

The quotient is declared or assigned to a variable; the remainder stands in a later statement of
the same block, in the expressions that statement evaluates itself. In between, and before the remainder
within its statement, nothing writes the quotient's variable or a variable of the operands, and both
operands have no effect and cannot trap. The quotient has trapped already where the remainder would - a
zero divisor, the smallest value divided by -1 - so the remainder from it, computed with wrapping
arithmetic, is the same everywhere it is reached. Where the table does not prefer it, both divisions
stay: their operands are variables already, read once each.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeQuotient.cs` and `quotient.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- An operand changed between the two (`a = a + 1`).
- Different operands (`a % c`), or the remainder before the quotient.
- A remainder inside a nested block (`if (q > 0) return a % b;`).
- JavaScript's `(a % b) | 0`, which is a remainder of doubles.

## Switching it off

`--disable-opt O0127`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run.
