# O0103 — Recognize leading and trailing zero counts from bit-scanning loops

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0103ZeroCountLoop` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#584](https://github.com/Hawkynt/Polypiler/issues/584). Switched off with `--disable-opt O0103`.

## What it does

Code without a bit-scan function counts zero bits in a loop until it meets a set bit. The rule
replaces such a loop by `TrailingZeroCount` or `LeadingZeroCount` of the IR, which the backends write as
`BitOperations.TrailingZeroCount`/`LeadingZeroCount`, `Math.clz32` or a helper.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static int F(uint x) { int n = 0; while (n < 32 && (x & 1) == 0) { x >>= 1; n++; } return n; }
```

### Before

With `--opt none`:

```csharp
public static int F(uint x) {
  unchecked {
    int n = 0;
    while ((n < 32) && ((x & 1u) == 0u)) {
      x = x >> 1;
      n = n + 1;
    }
    return n;
  }
}
```

### After

By default; `--explain-opt` prints `O0103 zero count from a bit-scanning loop: 'F' statement 1`:

```csharp
public static int F(uint x) {
  var n = global::System.Numerics.BitOperations.TrailingZeroCount(x);
  x = x >> n;
  return n;
}
```

## When it applies

The counter `n` starts at the constant 0 right before the loop; the condition is `n < w`, `w` the
width of the tested value, joined by `&&` with a test in either order; the body adds one to `n` and may
shift one variable by one (`x >>= 1`, `x <<= 1`, `x >>>= 1`). Every pass `r` below `w` is simulated: the
test must be `e == 0` with `e` exactly one bit of one value from before the loop - bit `r` for a trailing
count, bit `w - 1 - r` for a leading one. Shifting the variable (`(x & 1) == 0` with `x >>= 1`) and
testing a moving bit (`((x >> n) & 1) == 0`, `(x & (0x80000000 >> n)) == 0`) both qualify. The loop then
ends with `n` the number of zeros below (above) the first set bit, and `w` for zero, which is the count
of the IR; a shifted variable ends shifted by that count - also for zero, which shifts to zero either way.
The loop becomes `n = TrailingZeroCount(x); x = x >> n;` (or the leading count and `<<`).

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeZeroCount.cs` and `zerocount.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- A loop without the bound `n < w` (`while ((x & 1) == 0)`), which never ends for zero.
- A bound other than the width (`n < 31`), a counter that starts elsewhere (`n = 1`), or a test of a bit
  that is not the one the count reaches (`(x & 2) == 0`).

## Switching it off

`--disable-opt O0103`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run.
