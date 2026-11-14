# O0100 — Recognize rotate-left and rotate-right from shift pairs

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0100Rotate` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#279](https://github.com/Hawkynt/Polypiler/issues/279). Switched off with `--disable-opt O0100`.

## What it does

Hash functions and ciphers rotate constantly, and few source languages have a rotate operator,
so the rotation arrives as two shifts joined by `|`, `^` or `+`. The rule turns such an expression into
`RotateLeft` or `RotateRight` of the IR, which the C# backend writes as `BitOperations.RotateLeft` from
.NET Core 3.0 on (and `byte.RotateLeft`/`ushort.RotateLeft` from .NET 7 on) and as a helper with the same
shift pair elsewhere, and the JavaScript backend as the `$rotl`/`$rotr` helper. PRD §4.5, §8 P2, §19.3.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static uint F(uint x) => (x << 8) | (x >> 24);
```

### Before

With `--opt none`:

```csharp
public static uint F(uint x) => (x << 8) | (x >> 24);
```

### After

By default; `--explain-opt` prints `O0100 rotate from a shift pair: 'F' statement 1`:

```csharp
public static uint F(uint x) => global::System.Numerics.BitOperations.RotateLeft(x, 8);
```

## When it applies

The expression is evaluated as weighted bits (`WeightEvaluator`): a constant plus each bit of each
*atom* - a value the evaluation does not look into - times a weight. Shifts by a constant, masks,
conversions between integer types, negation and sums without carries are exact on that form. The
expression is a rotation when its value is exactly the bits of one atom of its own width, each moved by
the same amount `k` modulo the width, `0 < k < w`:

- `(x << n) | (x >> (w - n))` with constant `n`, also with `^` or `+`, the shifts in either order;
- every integer width: an 8- or 16-bit value widened to `int` and cut back (`(ushort)((x << 3) | (x >> 13))`),
  an `int` shifted through `uint` (`(x << 5) | (int)((uint)x >> 27)`), JavaScript's
  `(x << n) | (x >>> (32 - n))` on an `int32` or `uint32`;
- a variable count `n` - plain (`(x << n) | (x >> (32 - n))`), masked (`n & 31`, `-n & 31`) or negated: every
  shift of the IR uses only the low five or six bits of its count, so the expression is evaluated for each
  of the 64 residues of `n`, and the rule applies only when every residue gives the rotation by that residue
  (`RotateLeft(x, n)`) or by its negation (`RotateRight(x, n)`). `|` passes this test at `n = 0`, where both
  shifts are by 0 and `x | x = x`; `^` and `+` do not, since `x ^ x = 0` and `x + x = 2x`;
- through a local that is assigned once and holds such a value (`uint t = a + b; … (t << 7) | (t >> 25)`).

The direction follows the first shift the source wrote: a left shift gives `RotateLeft(x, k)`, a right
shift `RotateRight(x, w - k)`. The rotated value is evaluated once instead of twice, so it must have no
effect and cannot trap.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeRotate.cs` and `rotate.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- An arithmetic right shift: `(x << 8) | (x >> 24)` on an `int` drags copies of the sign into the
  low byte.
- Amounts that do not add up to the width: `(x << 8) | (x >> 23)`.
- Two different values: `(x << 8) | (y >> 24)`.
- `^` or `+` with a variable count, which is not a rotation at `n ≡ 0`.
- A narrow value with an unmasked variable count: `(ushort)((x << n) | (x >> (16 - n)))` at `n = 17` gives 0.
- A rotation by 0, and a 16-bit rotation by 8, which is a byte swap and left to O0106.
- A rotated value that may trap: `(a[i] << 8) | (a[i] >> 24)`, `((x / d) << 8) | ((x / d) >> 24)`.

## Switching it off

`--disable-opt O0100`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run. Runs in the pass pipeline of `translate` with the other rules of the table, in the order of their
numbers. O0105 and O0106 understand a rotation it produced, so a swap cascade whose last step became a
rotate is still recognized.
