# Numbers to text and back

**Status:** implemented for the prototype: the IR, the C# and the JavaScript frontend, the C# and the JavaScript backend in every profile ([#1315](https://github.com/Hawkynt/Polypiler/issues/1315)).

C# and JavaScript print the same shortest digits of a double - 199 884 random doubles gave identical
digits in .NET 10 and node 22 - but lay them out differently, spell truth values differently, round
fixed notation differently and parse by different grammars:

| | .NET (Core 3.0 and later, invariant culture) | ECMA-262 |
| --- | --- | --- |
| `1e21`, `1e-7`, `1.5e-6`, `1e20`, `-0` | `1E+21`, `1E-07`, `1.5E-06`, `1E+20`, `-0` | `1e+21`, `1e-7`, `0.0000015`, `100000000000000000000`, `0` |
| `true` | `True` | `true` |
| fixed, 2.5 to 0 digits; 0.125 to 2 | `F0`: `2`; `F2`: `0.12` (a tie to the even digit) | `toFixed(0)`: `3`; `toFixed(2)`: `0.13` (a tie to the larger) |
| `"1,000"`, `"0x10"`, `"infinity"` | 1000, `FormatException`, ∞ | NaN, 16, NaN (`Number`) |

Each rule is an operation of the IR of its own, so that a difference between .NET and node is a
defect and never "just formatting". `NumberTextOperations` (`Polypiler.Core.IR/String`) holds the
typing rules and the meaning the interpreter runs; the rules of .NET are those of the runtime the
interpreter runs on, .NET 10, the rules of ECMA-262 are written out there. Nothing depends on a
culture or a runtime version.

## Operations

| Operation | Signature | Rule |
| --- | --- | --- |
| `FormatEcma` | `f64 → str`, integer `→ str` | `Number::toString(x, 10)` (§6.1.6.1.20): the shortest digits; fixed while the exponent e of the first digit satisfies -7 < e < 21, else `d.ddde±n`; -0 is `0`; a 64-bit integer as `BigInt` prints it |
| `FormatDotNet` | `f64 \| f32 \| integer → str` | `ToString(CultureInfo.InvariantCulture)`: the shortest digits (of binary32 for `f32`); fixed while -5 < e < 17 (`f64`) or -5 < e < 9 (`f32`), else `d.dddE±nn` with two exponent digits at least; -0 is `-0` |
| `FormatBoolEcma`, `FormatBoolDotNet` | `bool → str` | `true`/`false`, `True`/`False` |
| `FormatRadix` | `(integer, i32 radix, i32 minDigits, bool upper, i32 mode) → str` | the digits of a radix padded with zeros; `TwoComplement` (radix 2, 8, 16) writes the bits of the type - `(-1).ToString("x")` is `ffffffff` - and `SignMagnitude` (radix 2 to 36) a minus sign before the magnitude - `(-1).toString(16)` is `-1`, `(-5).ToString("D3")` is `-005`. Every argument but the first is a constant |
| `FixedEcma` | `(f64, i32 0…100) → str` | `toFixed`: the exact value, a tie to the larger magnitude; `-` only below zero, so -0 is `0.00` and -0.001 is `-0.00`; from 10^21 on and for NaN, `FormatEcma` |
| `FixedDotNet` | `(f64, i32 0…15) → str` | `ToString("F<n>")`: the exact value, a tie to the even digit, never an exponent (`1e21` has 22 digits); the sign of every negative number stays (`-0.00`) |
| `ParseNumberEcma` | `str → f64` | `StringToNumber` (§7.1.4.1.1), which `Number(s)` and `+s` apply: without ECMA-262's white space at both ends; empty is 0; `0x`, `0o`, `0b` without a sign; a sign and `Infinity` or a decimal literal; anything else NaN |
| `ParseFloatEcma` | `str → f64` | `parseFloat`: the longest prefix that is a sign and `Infinity` or a decimal literal; NaN without one |
| `ParseIntEcma` | `(str) → f64`, `(str, i32 radix) → f64` | `parseInt`: a sign, `0x` without a radix or with 16, the longest run of digits; NaN without one or for a radix outside 2 to 36 |
| `ParseDotNet` | `str → integer \| f64 \| f32` | `T.Parse(s, CultureInfo.InvariantCulture)`: an integer by `NumberStyles.Integer`, a float by `NumberStyles.Float \| NumberStyles.AllowThousands` (`1,000` is 1000), `Infinity` and `NaN` in any case; beyond the range ∞ |

Every parse is correctly rounded. `ParseDotNet` traps `InvalidFormat` (new) on a malformed text and
`IntegerOverflow` on an integer out of its type, the format first: `"99999999999x"` is
`InvalidFormat`. A backend throws `FormatException` for `InvalidFormat`, in JavaScript the
generated `FormatError`, a `RangeError` ([exceptions](exceptions.md)).

The verifier checks the constants: the digits of a fixed notation within the bounds of its rule, and
the radix, padding (0 to 99), case and mode of `FormatRadix`.

## C# frontend

`CSharpNumberTextApi` is the table; every row has a test against .NET.

- `x.ToString(CultureInfo.InvariantCulture)` (or `NumberFormatInfo.InvariantInfo`), `ToString("G")`,
  `ToString("R")` of a float, and `Convert.ToString(x)` of every number type are `FormatDotNet`;
  `b.ToString()` and `"…" + b` are `FormatBoolDotNet`.
- `"…" + x`, `$"{x}"`, `string.Concat(…, x)` and every conversion without a provider are imported
  with the invariant culture, and the module carries the caveat **`cs-invariant-culture`**, which
  the generated header names and the command line writes as a note: .NET uses the culture of the
  thread there, `"a" + 2.5` is `a2,5` under de-DE. A hexadecimal format is the same in every culture.
- `ToString("F<n>")` (and `$"{x:F<n>}"`) with n from 0 to 15 is `FixedDotNet` - of a `float` through
  the double it is exactly, of an integer of up to 32 bits likewise; `ToString("D<n>")` and
  `ToString("x<n>")`/`"X<n>"` of an integer, and `Convert.ToString(v, 2 | 8 | 10 | 16)`, are `FormatRadix`.
- `T.Parse(s)` and `T.Parse(s, CultureInfo.InvariantCulture)` of every integer type, `float` and
  `double` are `ParseDotNet`.

Refused (`PLY1002`), by name: a provider other than the invariant culture, naming it; a format other
than those (`N2`, `E3`, `G17`, `C`, custom) or one that is no constant; `NumberStyles`; `TryParse`,
whose result is an out parameter (#216); `Convert.ToString` with another radix; an interpolation with
an alignment.

## JavaScript frontend

`JavaScriptNumberTextApi` is the table. A number, a `BigInt` or a truth value joined to a string by
`+`, in a template hole, in `concat`, in `String(x)` and by `x.toString()` is `FormatEcma` or
`FormatBoolEcma`; a proven integer keeps its integer type. `n.toString(r)` with a literal radix of a
proven integer or a `BigInt` is `FormatRadix` with a sign. `n.toFixed(d)` with a literal `d` is
`FixedEcma`. `Number(s)`, `+s`, `parseFloat(s)` and `parseInt(s[, r])` with a literal radix are the
parsers; a parameter without JSDoc that `Number`, `parseFloat` or `parseInt` parses is a string.

Refused (`PLY1102`), by name: `toString(r)` of a number with a fraction and with a computed radix,
`toFixed` with computed digits, `parseInt` with a computed radix, `toPrecision`, `toExponential`,
`toLocaleString`, and a number given to a method of a string, which JavaScript would convert to text
first.

## C# backend

Every conversion is a row of `CSharpCapabilities` (`CSharpNumberTextRows`). .NET Framework writes 15
significant digits for `ToString()`, has known defects of `"R"`, rounds `"F"` away from zero after 15
digits and does not round `Parse` correctly; a .NET Standard library may run on it. So the rules of
.NET Core 3.0 are the framework's own from `netcoreapp3.1` on and helpers before.

| IR | net2.0 … net4.8, netstandard2.0, netstandard2.1 | netcoreapp3.1, net8.0, net10.0 |
| --- | --- | --- |
| `FormatDotNet` of `f64`, `f32` | helper `NumberText` (Ryu and the layout of .NET) | `x.ToString(CultureInfo.InvariantCulture)` |
| `FormatDotNet`, `FormatEcma` of an integer | `x.ToString(CultureInfo.InvariantCulture)` | same |
| `FormatEcma` of `f64`, `FixedEcma` | helper `NumberText` | same |
| `FormatBool…` | `(b ? "True" : "False")`, `(b ? "true" : "false")` | same |
| `FormatRadix` hexadecimal, two's complement, padding 0, 2, 4, 8, 16 | `x.ToString("x8")` | same |
| `FormatRadix`, any other | helper `FormatRadix` | same |
| `FixedDotNet` | helper `NumberText` (the exact value, a tie to even) | `x.ToString("F2", CultureInfo.InvariantCulture)` |
| `ParseDotNet` of an integer | `int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)` | same |
| `ParseDotNet` of `f64`, `f32` | helper `ParseText` (the grammar and the exact value) | `double.Parse(s, NumberStyles.Float \| NumberStyles.AllowThousands, CultureInfo.InvariantCulture)` |
| `ParseNumberEcma`, `ParseFloatEcma`, `ParseIntEcma` | helper `ParseText` | same |

`NumberText` finds the shortest digits with Ryu (Ulf Adams, Apache License 2.0), converted to
C# 2 with its small tables copied exactly; a float goes through the same computation with its own
mantissa. Its fixed notation and `ParseText` compute with the exact decimal value in integers of many
digits. `parseInt` of the radices other than 2, 4, 8, 10, 16 and 32, which ECMA-262 lets an
implementation approximate, follows V8. A generated file that carries one of these helpers names the
code it was derived from, with its licence, in its header.

## JavaScript backend

| IR | es5 | es2015 · es2017 | es2020 · es2022 |
| --- | --- | --- | --- |
| `FormatEcma`, `FormatDotNet` of a 32-bit integer, `FormatBoolEcma` | `String(x)` | same | same |
| `FormatEcma`, `FormatDotNet` of a 64-bit integer | helper `$radix64` over the halves | same | `String(x)` of the `BigInt` |
| `FormatDotNet` of `f64` | helper `$fmtNet`: `String(x)` laid out as .NET lays it out, -0 by `1 / x < 0` | same | same |
| `FormatDotNet` of `f32` | helper `$fmtNet32`: the first `toPrecision(p)` that `fround` reads back, a tie to the even digit | same | same |
| `FormatBoolDotNet` | `b ? "True" : "False"` | same | same |
| `FormatRadix` | `x.toString(r)`, of `x >>> 0` or `x & 0xFF` for two's complement; helper `$radix` for padding or upper case; `$radix64` for 64 bits | same | `BigInt.asUintN(64, x).toString(r)` |
| `FixedEcma` | `x.toFixed(d)` | same | same |
| `FixedDotNet` | helper `$fixedNet` (the exact digits by `$exact`, a tie to even, the sign of -0) | same | same |
| `ParseNumberEcma`, `ParseFloatEcma`, `ParseIntEcma` | `Number(s)`, `parseFloat(s)`, `parseInt(s, r)` | same | same |
| `ParseDotNet` | helpers `$parseNet` (the grammar, digits through `Number`, a float decided by the exact digits where it falls on a midpoint), `$parseNetInt`, `$parseNet64` | same | `$parseNet64` through `BigInt` |

## Checks

- `tests/vectors/numtext/shortest.txt`: 20 000 doubles and 10 000 floats of xorshift64 from the seed
  0x2026 with what .NET 10 and node write, with the generator and the tool versions. The interpreter,
  the C# helpers and the JavaScript helpers write them all; the C# helpers also write what this
  runtime writes for 200 000 doubles and 100 000 floats of the same generator.
- Every operation is written and run in every profile against the interpreter
  (`CSharpNumberTextEmissionTests`); the fixtures of `tests/fixtures/prototype/numtext` run in every
  profile of the other language against .NET and node (`NumberTextTests`), and the scenarios of
  `tests/features/ir/ir-number-text.feature` check the values the issue states.

## Interpretations

- `ParseIntEcma` with a radix computes as V8 does where ECMA-262 lets it approximate; every other
  parse is correctly rounded.
- `FormatDotNet` of a `float` with `"R"` is the same as without a format from .NET Core 3.0 on.
- A trap is the exception each backend throws until the traps are mapped; `InvalidFormat` is not yet
  catchable as `FormatException` in the IR.
