# O0121 — Recognize absolute value from sign-mask idioms and compare-select

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0121AbsWrap` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#555](https://github.com/Hawkynt/Polypiler/issues/555). Switched off with `--disable-opt O0121`.

## What it does

The absolute value of a signed integer is written as a selection or with the sign mask. Every one of
these idioms wraps on the smallest value, whose negation is itself, so the rule produces `AbsWrap` of
the IR - never `Abs`, which traps there. Sourced from Warren, *Hacker's Delight*, 2nd ed., §2-4.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static int F(int x) { int m = x >> 31; return (x ^ m) - m; }
```

### Before

With `--opt none`:

```csharp
public static int F(int x) {
  unchecked {
    var m = x >> 31;
    return (x ^ m) - m;
  }
}
```

### After

By default; `--explain-opt` prints `O0121 wrapping absolute value from a sign mask or a selection: 'F' statement 2`:

```csharp
public static int F(int x) {
  unchecked {
    var m = x >> 31;
    return (int)AbsWrap(x);
  }
}
```

## When it applies

- `x < 0 ? -x : x` and its mirrored forms (`<=`, `>`, `>=`, `0 > x`), the negation written `-x` or
  `0 - x`, on a signed 32- or 64-bit value;
- `(x ^ m) - m` and `(x + m) ^ m` with the sign mask `m = x >> (w - 1)`, an arithmetic shift, also when
  `m` is a local assigned once.

`x` has no effect and cannot trap.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeAbs.cs` and `abs.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- A negation that traps (`checked(-x)`): `AbsWrap` would drop the trap.
- A mask that is not the sign (`x >> 30`), or a logical shift.
- The swapped selection `x < 0 ? x : -x`, which is the negative absolute value.
- `Math.Abs`, which already is `Abs` and traps on the smallest value.

## Switching it off

`--disable-opt O0121`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run.
