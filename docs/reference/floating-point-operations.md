# Floating-point operations

The floating-point operations of the IR, with their definition, contract, special values and
portable implementation. The comparison rules are those of
[ADR 0005](../adr/0005-floating-point-tolerance.md); the conversions those of
[ADR 0002](../adr/0002-numeric-contracts.md). The table in the code is
`Polypiler.Core.IR.FloatOperationTable`; this page describes it.

## Contracts

- **`exact`** — one result, the same bits on every target: the correctly rounded result of
  IEEE 754-2019 §5.4.1, a bit operation, or a fixed sequence of such operations. Compared bit for
  bit; a NaN matches any NaN.
- **`ulp(n)`** — within `n` units in the last place of the exact result. Only a native library call
  is compared under a bound; the portable implementation, which the interpreter runs and the
  backends emit, is the same on every target.

Every operation exists for `f32` and `f64`. The `f32` operation is the binary32 function, not the
binary64 one rounded.

## Portable implementations

`Polypiler.Core.IR.FloatOperations`, one file per family under `Polypiler.Core.IR/Float/Operations`,
written in the subset of C# that the C# frontend imports. The assembly carries the source
(`FloatOperationSources`), so the same code can be translated for a target that lacks a native
function. The interpreter calls the compiled methods; tests import the source with the C# frontend
and check that the interpreted IR gives the same bits.

## Reference vectors

`tests/vectors/float/<function>.<f32|f64>.txt`: the correctly rounded result computed with GNU MPFR,
and for an inexact one its neighbour and position, so the reference error of any computed value can
be measured. Per function and format: 1 000 core-range and 1 000 whole-domain inputs (SplitMix64,
seed 2026), the special values, domain edges, overflow and underflow thresholds, argument-reduction
cases for the trigonometric functions and up to 400 CORE-MATH worst cases. The generator is
`tests/vectors/float/generate/vectors.c`; tool versions are in `generate/versions.txt`.

## Exact arithmetic

| Operation | Definition | Special values | Source |
| --- | --- | --- | --- |
| `Sqrt(x)` | correctly rounded square root | `Sqrt(-0) = -0`, `Sqrt(x < 0) = NaN` | operator of every target |
| `FusedMultiplyAdd(x, y, z)` | `x * y + z`, rounded once | a product that underflows keeps its sign with a zero addend | musl `fma.c` / FreeBSD `s_fmaf.c` |
| `IeeeRemainder(x, y)` | `x - n * y`, `n` nearest to `x / y`, ties to even (§5.3.1) | a zero result has the sign of `x`; `y = 0`, `x = ±∞`: NaN | musl `remquo.c`, `remquof.c` |
| `Reciprocal(x)` | `1 / x`, rounded once | `1 / ±0 = ±∞` | division |

## Rounding

| Operation | Definition |
| --- | --- |
| `Floor`, `Ceiling`, `Truncate` | the integral number below, above, towards zero |
| `RoundEven`, `RoundAway`, `RoundHalfUp` | nearest integral number; a tie to even, away from zero, towards +∞ (ECMA-262 `Math.round`, -0 for -0.5 ≤ x < 0) |
| `RoundDigits(x, digits, mode)` | .NET's `Math.Round(x, digits, mode)`: for \|x\| < 10^16 (10^8 for `f32`) `round(x * 10^d, mode) / 10^d`, else `x`; `mode` a constant of `MidpointRounding`; `digits` outside 0..15 (0..6) traps |

## Sign and magnitude

| Operation | Definition | NaN | Zeros |
| --- | --- | --- | --- |
| `Abs`, `CopySign(x, y)` | magnitude; magnitude of `x` with the sign bit of `y` | `CopySign` copies the sign bit of a NaN too | sign bit |
| `Min`, `Max` | IEEE 754-2019 `minimum`, `maximum` | NaN if either is | -0 < +0 |
| `MinMagnitude`, `MaxMagnitude` | operand of the smaller (larger) magnitude | NaN if either is | of equal magnitude the negative (positive) one |
| `MinNumber`, `MaxNumber` | IEEE 754-2019 `minimumNumber`, `maximumNumber` | ignored; NaN only if both are | -0 < +0 |
| `Clamp(v, lo, hi)` | `v < lo ? lo : v > hi ? hi : v` (.NET `Math.Clamp`) | a NaN `v` is returned, a NaN bound ignored | as the comparisons give |
| `Sign(x)` | -1, 0 or 1 as `i32` | traps | 0 |

`Clamp` with `lo > hi` traps with an invalid operation, as `Math.Clamp` throws.

## Representation

| Operation | Definition |
| --- | --- |
| `ScaleB(x, n)` | `x * 2^n`, rounded once (musl `scalbn`) |
| `ILogB(x)` | binary exponent as `i32`; `int.MinValue` for 0, `int.MaxValue` for ±∞ and NaN |
| `BitIncrement`, `BitDecrement` | next value up (down); `BitIncrement(-0) = ε`, `BitIncrement(-∞) = -MaxValue`, `BitDecrement(+∞) = MaxValue` |
| `Fraction(x)`, `Exponent(x)` | C `frexp`: `x = Fraction * 2^Exponent`, 0.5 ≤ \|Fraction\| < 1; 0, ±∞ and NaN give `(x, 0)` |
| `FloatBits`, `BitsFloat` | the IEEE 754 bits as `i64`/`i32` and back |

## Classification

`IsNaN`, `IsInfinity`, `IsFinite`, `IsNegative` (the sign bit, also of -0 and a NaN), `IsNormal`,
`IsSubnormal` (a zero is neither), `IsInteger` (finite and integral, -0 included), each a truth value.

## Angle conversion and interpolation

| Operation | Definition |
| --- | --- |
| `DegreesToRadians(x)` | `(x * π) / 180`, π rounded to the format, two roundings — .NET 10's definition |
| `RadiansToDegrees(x)` | `(x * 180) / π` |
| `Lerp(a, b, t)` | `a + (b - a) * t`, the three operations in that order |

.NET's `double.Lerp` is not this operation: it computes `MultiplyAddEstimate(a, 1 - t, b * t)`,
which is fused on hardware with FMA, and the C# frontend translates it to that: `FusedMultiplyAdd(a, 1 - t, b * t)`.

## Functions with a tolerance

Every one is `ulp(n)` (ADR 0005): a native function may be used where its measured error, rounded
up, is at most `n`; the portable implementation is correctly rounded on every reference vector
(distance 0), except `LogBase` (distance at most 2). Sources: CORE-MATH (MIT), commit
`018fa97cbdacf59924b899a9dfa422d532cb2bb7`, one `THIRD-PARTY-NOTICE.CORE-MATH.<Family>.txt` per family
beside the code.

| Family | Operations | `n` | Special values (exact in every mode) |
| --- | --- | ---: | --- |
| Trigonometric | `Sin`, `Cos`, `Tan` | 1 | `sin(-0) = -0`, `tan(-0) = -0`, `cos(±0) = 1`; ±∞ gives a NaN |
| Inverse trigonometric | `Asin`, `Acos`, `Atan`, `Atan2(y, x)` | 1 | `asin(±1) = ±π/2`, `acos(-1) = π`, `acos(1) = +0`; the sixteen sign and zero cases of `Atan2`: `atan2(-0, x < 0) = -π`, `atan2(-0, x > 0) = -0`, `atan2(±0, -0) = ±π`, `atan2(±∞, ±∞) = ±π/4, ±3π/4` |
| Hyperbolic | `Sinh`, `Cosh`, `Tanh` | 2 | `sinh(-0) = -0`, `tanh(-0) = -0`, `tanh(±∞) = ±1` |
| Inverse hyperbolic | `Asinh`, `Acosh`, `Atanh` | 1 | `acosh(1) = +0`, `atanh(±1) = ±∞`; outside the domain a NaN |
| Exponential | `Exp`, `Exp2`, `Exp10`, `ExpM1` | 1 | `exp(±0) = 1`, `exp(-∞) = +0`, `expm1(-0) = -0`, `expm1(-∞) = -1`; exact powers exact |
| Logarithm | `Log`, `Log2`, `Log10`, `Log1P` | 1 | `log(±0) = -∞`, `log(x < 0)` a NaN, `log(1) = +0`, `log1p(-1) = -∞`, `log1p(-0) = -0` |
| Logarithm to a base | `LogBase(a, b)` = .NET's `Math.Log(a, b)` | 2 | base 1 a NaN; base 0 or +∞ a NaN unless `a = 1` |
| Power | `Pow` (IEEE 754-2019 §9.2.1), `PowEcma` (ECMA-262) | 1 | `pow(x, ±0) = 1`, `pow(1, y) = 1`, `pow(-1, ±∞) = 1`, `pow(-0, odd < 0) = -∞`; `PowEcma` gives a NaN for a NaN exponent and for `(±1, ±∞)` |
| Roots | `Cbrt`, `Hypot`, `RootN(x, n)`, `ReciprocalSqrt` | 1 | `cbrt(-0) = -0`, `hypot(±∞, NaN) = +∞`, `rSqrt(-0) = +∞`; `RootN` as .NET's `Double.RootN` for 0, ±∞, a NaN and `n = 0` |

`RootN` has no licensed implementation to convert; it is CORE-MATH's `pow` with the exponent `1/n`
held as a double-double, and 256-bit `log` and `exp` where that does not decide the rounding.

### Native functions

`--float accurate` (the default) uses a native function where `NativeAccuracy` admits it: the larger
of ADR 0005's measurement and the published error of the runtime's library, rounded up, is at most
`n` and at most 4. A `f32` operation may use the `f64` function rounded to `f32` (slack 1). The bound
of the comparison is `n + N + max(n, N)`. `--float strict` uses none.

| Target | Admitted for `f64` (slack) | Admitted for `f32` | Not admitted |
| --- | --- | --- | --- |
| C# (`dotnet-sdk`) | `Sin`, `Cos`, `Log`, `Pow`, `Atan2` from `net2.0`, `Log2` from `netcoreapp3.1`, `Hypot` from `net8.0` (1); `Cosh`, `Tanh` from `net2.0` (1, `n = 2`) | `Math.X((double)x)` cast back for `Sin`, `Cos`, `Tan`, `Exp`, `Log`, `Log2`, `Log10`, `Pow`, `Atan2`, `Cosh`, `Tanh`, `Cbrt`, `Hypot` | `Tan` (Microsoft's C runtime, 1.32), `Exp` (1.50), `Log10` (1.62), `Cbrt` (3.67), `Sinh` (glibc 2.44, 1.94·10¹⁹), every function without a measurement |
| JavaScript (`node`) | `Math.sin`, `cos`, `tan`, `exp`, `log`, `pow` (`PowEcma`) from ES5; `log2`, `cbrt` from ES2015 (1); `sinh`, `cosh`, `tanh` from ES2015 (2, `n = 2`) | `Math.fround(Math.x(x))` for the measured functions, `log10`, `atan2` and `hypot` included | `log10` (1.62), `atan2` (1.20), `hypot` (1.70) of `f64`; `Pow` of IEEE 754 (`Math.pow` is ECMA-262's); every function without a measurement |

Measured on the reference vectors (development host, .NET 10.0.112, node 22.23.3): every admitted
function is within its bound; the largest distance from the correctly rounded result was 1, for V8's
`tanh` 2. A file that calls a native function lists each one with its contract, slack and bound in its
header; a file written with `--float strict` says that its results are the same bits on every target.

### Exact operations in the backends

C#: `Math.FusedMultiplyAdd`, `CopySign`, `MinMagnitude`, `MaxMagnitude`, `ScaleB`, `ILogB`,
`BitIncrement`, `BitDecrement` from `netcoreapp3.1`, `Math.IEEERemainder` from `net2.0`
(`MathF.IEEERemainder` from `netstandard2.1`), `double.MinNumber`, `MaxNumber`, `IsInteger` from
`net8.0`, `double.IsNaN`, `IsInfinity` from `net2.0`, `IsFinite`, `IsNegative`, `IsNormal`,
`IsSubnormal` from `netstandard2.1`, `Math.Clamp` from `netstandard2.1` (a helper that throws as it
does before); `RoundDigits` is a helper in every profile. JavaScript has none of these: each is the
portable implementation, `Clamp` and `RoundDigits` helpers that throw a `RangeError`.

## Portable implementations in generated code

`FloatLegalization` replaces every operation a target does not write itself by a call of its portable
implementation, copied from the module the C# frontend imports from `FloatOperationSources`, with the
functions it calls and the tables it reads, under names no name of the module has; their own operations
are legalized the same way (in JavaScript the fused multiply-add inside `Sin` is a helper as well). The
backends then write that code like any other: generated files stay self-contained, and the interpreter,
the C# file and the JavaScript file of a strict translation agree bit for bit.

## Source members

- C#: `CSharpFloatApi` in the C# frontend - one entry per member of `Math`, `MathF`, `double` and
  `float` and the operations it becomes. Members .NET 10 defines as a sequence of other operations
  become that sequence: `double.Exp2(x)` is `Pow(2, x)`, `ExpM1` is `Exp(x) - 1`, `LogP1` is
  `Log(x + 1)`, `AsinPi` is `Asin(x) / π`, `RootN` is .NET's sequence over `Sqrt`, `Cbrt` and `Pow`,
  `Lerp` is `FusedMultiplyAdd(a, 1 - t, b * t)`. `Math.Round(x, mode)` is `RoundEven`, `RoundAway`,
  `Truncate`, `Floor` or `Ceiling` by the constant mode; with digits it is `RoundDigits`. A member
  outside the table, a computed mode and an estimate are refused with `PLY1002`, naming the member.
- JavaScript: `JavaScriptFloatApi` in the JavaScript frontend - every function of `Math` but `random`
  (refused with `PLY1102`), `Math.round` as `RoundHalfUp`, `Math.pow` and `**` as `PowEcma`,
  `Math.hypot` of more than two arguments as a left fold of `Hypot`, the predicates of `Number`, the
  constants of `Math` and `Number`.
