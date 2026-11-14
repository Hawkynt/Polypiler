# O0109 — Recognize sign extension from shift pairs, xor-subtract and compare-select idioms

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0109SignExtend` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#580](https://github.com/Hawkynt/Polypiler/issues/580). Switched off with `--disable-opt O0109`.

## What it does

The sign extension of the low `b` bits is written as a shift pair or with xor and subtract. The rule
replaces both by `SignExtend` of the IR. With a constant bit count the C# backend writes the operation
as the shift pair of the signed type; elsewhere it calls the helper that traps on a count outside the
width. Sourced from Warren, *Hacker's Delight*, 2nd ed., §2-6.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static int F(int x) => (x << 20) >> 20;
```

### Before

With `--opt none`:

```csharp
public static int F(int x) => (x << 20) >> 20;
```

### After

By default; `--explain-opt` prints `O0109 sign extension from a shift pair or xor-subtract: 'F' statement 1`:

```csharp
public static int F(int x) => unchecked(((x << (32 - 12)) >> (32 - 12)));
```

## When it applies

- `(x << k) >> k` with a constant `0 < k < w` and an arithmetic right shift - the shift of a signed
  32- or 64-bit value - is `SignExtend(x, w - k)`.
- `(x ^ m) - m` with `m = 1 << (b - 1)` is `SignExtend(x, b)` when the bits of `x` from `b` on are
  known to be zero: a mask (`(x & 0xFF) ^ 0x80`), a zero-extended byte. The weighted bits of `x`
  (`WeightEvaluator`) show that: every field ends below `b`.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeSignExtend.cs` and `signextend.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- An unmasked `(x ^ 0x80) - 0x80`: bits above the field pass through unchanged.
- A logical right shift (`(x << 20) >> 20` on a `uint`), which extends with zeros.
- Different amounts (`(x << 20) >> 19`), or a constant that is no power of two (`0x81`).

## Switching it off

`--disable-opt O0109`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run.
