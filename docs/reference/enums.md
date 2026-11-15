# Enums

**Status:** implemented for the prototype: the IR, the C# and the JavaScript frontend, the C# and the JavaScript backend in every profile ([#1318](https://github.com/Hawkynt/Polypiler/issues/1318)).

An enum is an integer with names. State machines, opcodes, modes, flags and error kinds are enums in
C# and frozen objects of named numbers in JavaScript (`const Color = Object.freeze({ Red: 0, Green: 5, Blue: 6 })`).
The names are observable in C#, so a translation that turns an enum into bare numbers loses them, and
one that turns it into strings changes the arithmetic:

| C# | Result |
| --- | --- |
| `((Perm)5).ToString()` of `[Flags] enum Perm { None = 0, Read = 1, Write = 2, Exec = 4, ReadWrite = 3 }` | `Read, Exec` |
| `((Perm)7).ToString()` | `ReadWrite, Exec` |
| `((Perm)8).ToString()` | `8` |
| `Enum.GetNames(typeof(S))` of `enum S { B = 2, Neg = -1, A = 0, C = 1 }` | `A, C, B, Neg` |
| `(Color)(c + 250)` of `enum Color : byte { Red, Green = 5, Blue }` with `c = Color.Blue` | `Red`: 256 wraps to 0 in the byte |
| `Enum.Parse(typeof(Perm), "read")` | `ArgumentException`: the match is case-sensitive unless asked otherwise |

## The type

`IrEnumType` (`Polypiler.Core.IR/Enum`) is a declaration of `IrModule.Types`: a name, the underlying
type - one of the eight integers from `i8` to `u64` - the members in declaration order, two of which
may have one value, whether it has `[Flags]`, and whether code outside the module may name it.
`IrType.Enum` gives its values a type of the **kind of the underlying type** with the enum as its
declaration: width, sign and wrap-around are the underlying type's, and the type is nominal - `Color`
is not `u8`. The interpreter carries a value of an enum as one of its underlying type, any of them,
also one no member has.

What the IR does with an enum is what C# does: the operators that stay on the enum are `==` and `!=`
of two values of one enum, and of an enum of 32 or 64 bits `&`, `|`, `^` and `~`; a conversion is to
and from exactly the underlying type, which changes no bit; a `switch` takes the values of the members
as labels. Everything else is computed on the underlying type: `c + n` of a byte enum is
`(Color)(byte)((int)(byte)c + n)`, `e - e` is the difference of the underlying values, `<` compares
them. The verifier refuses anything else on an enum.

## Operations

`EnumOperations` (`Polypiler.Core.IR/Enum`) holds the rules of .NET, from the API reference of
`System.Enum`, and the interpreter runs them; each is checked against .NET 10 itself. JavaScript has
no names for the values of a frozen object, so .NET's rules are the rules.

| Operation | Signature | Rule |
| --- | --- | --- |
| `EnumToString` | `E → str` | The name of the member with the value. Of a `[Flags]` enum: the zero member's name for 0 (`0` without one); a member with exactly the value; else the members taken greedily from the largest unsigned value down while all of a member's bits are set, written in ascending order and joined by `, `, when they cover every set bit (the order of `Enum.ToString` in dotnet/runtime, MIT). Otherwise the decimal digits of the value in the underlying type: `-1`, `8` |
| `EnumIsDefined` | `E → bool` | A member has exactly the value: `IsDefined(Perm, 5)` is false |
| `EnumParse` | `(str, bool ignoreCase) → E` | The text without the white space of `char.IsWhiteSpace` at its start; one that begins with a digit, `-` or `+` and is a number by `NumberStyles.Integer` is that number (outside the underlying type: trap `IntegerOverflow`); otherwise names separated by commas, each trimmed, every one a member's - ordinal, or as `OrdinalIgnoreCase`, the first in .NET's order that matches - and the result the bits of all of them. Any enum takes a list, also one without `[Flags]`. An empty text, an empty name and an unknown one trap `InvalidFormat`, where .NET throws `ArgumentException` |
| `EnumHasFlag` | `(E, E) → bool` | `(x & f) == f` |
| `EnumNames` | `E → str[]` | The names ordered by the values read as unsigned numbers of the underlying type, ties in declaration order; the argument only names the enum |
| `EnumValues` | `E → E[]` | The values in the same order |

The text, the names and the values of an enum whose members repeat a value .NET leaves open - which
name `ToString` prints, and in which order a sort of more than sixteen members leaves the ties - so the
verifier refuses `EnumToString`, `EnumNames` and `EnumValues` of such an enum, and the C# frontend
refuses the constructs that would need them.

## C# frontend

`CSharpEnumApi` lists every construct; each supported row runs against .NET running the original.

| Construct | IR | Status |
| --- | --- | --- |
| `enum E [: integer type] { … }` at the top of the file or in the static class, `[Flags]`, explicit and implicit values | `IrEnumType`, public when C# makes it visible | supported |
| `E.A`, locals, parameters, results, fields, constants and arrays of `E`, casts to and from integers | `IrConst`, `IrConvert` | supported |
| `&`, `\|`, `^`, `~`; `e + n`, `e - n`, `e - e`; `==`, `!=`, `<`, `<=`, `>`, `>=`; compound assignments, `++`, `--` | as above | supported |
| `switch` on `E` | `IrSwitch` | supported |
| `e.ToString()`, `string + e`, an interpolation `{e}` | `EnumToString` | supported |
| `Enum.IsDefined(typeof(E), v)`, `Enum.IsDefined<E>(v)` | `EnumIsDefined` | supported |
| `(E)Enum.Parse(typeof(E), s[, ignoreCase])`, `Enum.Parse<E>(s[, ignoreCase])` | `EnumParse` | supported |
| `e.HasFlag(f)` | `EnumHasFlag` | supported |
| `Enum.GetNames(typeof(E))`, `Enum.GetNames<E>()`, `(E[])Enum.GetValues(typeof(E))`, `Enum.GetValues<E>()` | `EnumNames`, `EnumValues` | supported |
| `e.ToString("D" \| "X" \| "G" \| "F")`, `{e:G}`, `Enum.Format`, `Enum.GetName`, the other members of `System.Enum` | — | `PLY1002`, naming the member |
| `Enum.TryParse` | — | `PLY1002`: its result is an `out` parameter (#216) |
| `ToString`, `GetNames`, `GetValues` of an enum whose members repeat a value | — | `PLY1002` |
| `Enum.IsDefined(typeof(E), v)` with `v` neither `E` nor its underlying type, `HasFlag` of another enum | — | `PLY1002`: .NET throws |
| an attribute on an enum other than `[Flags]`, an enum of another assembly | — | `PLY1002` |

## JavaScript frontend

An *enum object* is a `const` of the scope that encloses the functions whose value is
`Object.freeze({ A: <integer literal>, … })`, every member named and a distinct 32-bit integer. It is
an enum over `i32`, public where the file exports it (`export const`, `export { X }`) or is a script.
`JavaScriptEnumApi` lists every construct.

| Construct | IR | Status |
| --- | --- | --- |
| an enum object; `X.A` | `IrEnumType` over `i32`; a read is the member | supported |
| a variable, parameter or result that only members of one enum reach, and literals equal to a member | typed as the enum, so that `===`, `!==` and `switch` compare members | supported |
| any other use of a member: arithmetic, `X.A \| X.B`, `x < X.A` | the member's `i32` value, `(int)X.A` in C# | supported |
| a plain object of numbers, a string member, two members of one value, a member that is no 32-bit integer, a computed key or a method in the literal, `X[k]`, the object as a value (`Object.keys(X)`, `f(X)`), a name that is no member | — | `PLY1102`, naming the object |

```js
export const Color = Object.freeze({ Red: 0, Green: 5, Blue: 6 });
function weight(c) { return c === Color.Blue ? 3 : 1; }   // only members reach c
export function mix(a) { return a | Color.Green; }        // a member used as a number
```

```csharp
public enum Color : int { Red = 0, Green = 5, Blue = 6 }
private static int weight(Color c) { return (c == Color.Blue) ? 3 : 1; }
public static int mix(int a) { return a | (int)Color.Green; }
```

## C# backend

An enum is a C# enum beside the class: `[global::System.Flags]` where it has the attribute, over its
underlying type, its members in declaration order; public where the module exports it or a public
function or a struct names it, internal otherwise. A constant of an enum is the first member with its
value, or the number cast to the enum (`(S)(-5)`). Every operation is a row of the capability table:

| IR | net2.0 · net3.5 | net4.6 · net4.8 · netstandard2.0 | netstandard2.1 · netcoreapp3.1 | net8.0 · net10.0 |
| --- | --- | --- | --- | --- |
| `EnumToString` | `x.ToString()` | same | same | same |
| `EnumIsDefined` | `Enum.IsDefined(typeof(E), x)` | same | same | `Enum.IsDefined<E>(x)` (.NET 5) |
| `EnumParse` | `(E)Enum.Parse(typeof(E), s, ignoreCase)` | same | `Enum.Parse<E>(s, ignoreCase)` (.NET Core 2.0, .NET Standard 2.1) | same |
| `EnumNames`, `EnumValues` | `Enum.GetNames(typeof(E))`, `(E[])Enum.GetValues(typeof(E))` | same | same | `Enum.GetNames<E>()`, `Enum.GetValues<E>()` (.NET 5) |
| `EnumHasFlag` | `(x & f) == f`; `(~x & f) == 0` where `f` is computed, so that it is computed once | same | same | same |

The emitter never writes `Enum.HasFlag`, which boxes before .NET Core 2.1 (the row records it as
.NET Framework 4.6, the first profile whose framework has it). A failed parse throws .NET's
`ArgumentException`, where the IR names the trap `InvalidFormat`.

## JavaScript backend

| IR | es5 (± `--typed-arrays`) | es2015 · es2017 | es2020 · es2022 |
| --- | --- | --- | --- |
| an enum of 8 to 32 bits | `var Color = Object.freeze({ Red: 0, Green: 5, Blue: 6 });` (`Object.freeze`: ES5) | `const …` | same |
| an enum of 64 bits | no object: its members are pairs of halves where they are used; a **public** one is `PLY1152` ("the public 64-bit enum E"), nothing written | same | `const Big = Object.freeze({ Max: 18446744073709551615n, One: 1n });` |
| a constant of the enum | `Color.Green`, also as a `case` label | same | same |
| export | `exports.Color = Color` (cjs), a member of the returned object (iife) | `export const` (esm) | same |
| `EnumToString`, `EnumIsDefined`, `EnumParse` | `$enumName`, `$enumDefined`, `$enumParse` over a private table `$names$E = [[name, low(, high)], …]` of the members in .NET's order, emitted only when used; `$enumName64` and `$enumParse64` for 64 bits | same | same |
| `EnumNames`, `EnumValues` | the array of the names, or of the members (`new Int32Array([S.A, S.C, S.B, S.Neg])`) | same | same |
| `EnumHasFlag` | `(x & f) === f`, `(~x & f) === 0` where `f` is computed, on the values of the underlying type (`>>> 0` for `u32`, halves or `BigInt` for 64 bits) | same | same |
| arithmetic | the underlying integer's ([integer operations](integer-operations.md)) | same | same |

The helpers trim with .NET's white space (`$trimNet`), read numbers by .NET's grammar (`$parseNetInt`,
`$parseNet64`), compare names ignoring case as `OrdinalIgnoreCase` does (one code unit at a time) and
throw the `RangeError` of the IR's traps.

## Checks

- `Polypiler.Tests/Enums`: every rule against .NET 10 - every value 0…255 of two flags enums, the
  boundaries of every underlying type, every class of text `Enum.Parse` distinguishes; the verifier;
  both frontends' tables; a module of every operation over every underlying type compiled for all nine
  C# profiles and written at every JavaScript level.
- `Polypiler.Tests.Acceptance`: node runs that module and the fixtures of `tests/fixtures/prototype/enums`
  at every level, module format and typed-array setting against the interpreter and .NET;
  `tests/features/ir/ir-enums.feature` states the scenarios of the issue.

## Not yet

String enums and TypeScript-style enums in JavaScript (refused); `Enum.TryParse` and `Enum.GetName`,
which need `out` parameters (#216); format strings for enums; closed enumerations with exhaustiveness
facts (#200).
