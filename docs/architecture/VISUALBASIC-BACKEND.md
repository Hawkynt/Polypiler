# The classic Visual Basic backend

**Status:** implemented for integers, floating point, control flow, procedures, arrays, records,
strings, module state, exceptions and the dynamic type ([#1556](https://github.com/Hawkynt/Polypiler/issues/1556), [#1088](https://github.com/Hawkynt/Polypiler/issues/1088),
[#1085](https://github.com/Hawkynt/Polypiler/issues/1085), [#1086](https://github.com/Hawkynt/Polypiler/issues/1086),
[#1087](https://github.com/Hawkynt/Polypiler/issues/1087), [#1089](https://github.com/Hawkynt/Polypiler/issues/1089),
classic dialect), checked by LibreOffice Basic in VBA mode, the oracle of
[ADR 2600](../adr/2600-classic-visual-basic-oracle.md). Related: [classic Visual Basic frontend](VISUALBASIC-FRONTEND.md) ·
[backends](BACKENDS.md) · [exceptions](../reference/exceptions.md#visual-basic).

`polypiler translate <file> --to visualbasic --output <directory> [--profile visualbasic/vb6|vba6|vba7|vba7-64]`
writes one standard module, `<module>.bas`, with its `Attribute VB_Name` line, and the manifest beside it.

## Pipeline

```mermaid
flowchart LR
  IR[IR module] --> L[Legalize: float and integer library helpers]
  L --> C[Check: refusals PLY2655-2658]
  C --> W[Writer: VbModule tree]
  W --> P[Target passes: O0501]
  P --> T[VbPrinter: .bas text]
  T --> R[VbSyntaxReader: parse-back]
  T --> O[LibreOffice Basic oracle]
```

## Project

| Folder | What it holds |
| --- | --- |
| `Syntax/` | `VbSyntax.cs`, the tree of a standard module; `VbSyntaxRewriter` (walk, the shape two trees are compared by); `VbSyntaxReader`, which reads the printed subset back - keywords in any case, ` _` continuations, comments, the precedence of [MS-VBAL] 5.6.9 |
| `Printing/` | `VbPrinter`: four spaces, LF, parentheses exactly where precedence needs them, a right operand that starts with a minus sign always in parentheses, literals in the form Visual Basic reads (`&H80000000`, `65535&`, `1E+20`, `3#`, `0.5!`) |
| `Profiles/` | `VisualBasicProfile` (`visualbasic/vb6`, `vba6`, `vba7` - the default, which compiles on 32- and 64-bit Office - and `vba7-64`) and `VisualBasicCapabilities`: `LongLong` only on `vba7-64`, `LongPtr`/`PtrSafe` from VBA 7 |
| `Helpers/` | `VisualBasicHelpers`, the operations Visual Basic has no expression for, and `ArrayHelpers`, the length, creation, copy and fill of an array per element type (listed in [backends](BACKENDS.md#helpers-that-stay-with-a-backend)) |
| `Writing/` | the writer: module, names, types, constants, globals; statements; expressions and conversions; exceptions |
| `Passes/` | `VisualBasicTargetPasses`: O0501, which removes a helper nothing names |

## Representation

| IR | Visual Basic |
| --- | --- |
| `bool` | `Boolean` |
| `u8` | `Byte` |
| `i8`, `i16` | `Integer`, an `i8` held in its range |
| `u16`, `i32` | `Long` |
| `u32` | the bits of a `Long`; compared with the sign bit flipped |
| `i64`, `u64` | `LongLong` (`vba7-64`, its checked `+ - *`, `\` and `Mod` the operators of the type), otherwise refused with `PLY2656` |
| `IrBind`, an assignment as a value | the assignment before the expression, the variable in it |
| `f32`, `f64` | `Single`, `Double` |
| string | `String`; a character outside printable ASCII as `ChrW(n)` |
| array | a dynamic array from 0; an empty one has no bounds, so its length is `PlyLen_<T>` |
| struct | a `Public Type`; a parameter by reference, copied field by field where the body changes it |
| global, table | a module variable that `PlyInit` sets on the first call of a public procedure |
| `variant` ([variants](../reference/variants.md)) | `Variant`: `Empty`, `Null`, or the payload through its conversion function (`CInt(x)`, `CLng(x)`, `CDbl(x)`, …), which gives it its subtype also where a literal would have another; the tag `VarType(v)`; a payload the frontend tested `CInt(v)`, …, one it did not a test that raises error 13 first; a date `CDate(x)` and `CDbl(v)`; a currency `CCur(CDec(x) / 10000)` and `CLngLng(CDec(v) * 10000)`, only where the profile has `LongLong`; arrays of it `Variant()` |
| `FormatVisualBasic` | `PlyFmtR8(x, sep)`/`PlyFmtR4`: `CStr` with the run-time decimal separator replaced by the IR's and a three-digit exponent with a leading zero shortened, as LibreOffice writes `1E+020` |
| `ParseDotNet` of `f64`/`f32` | `PlyParseR8`: blanks and group separators removed, error 13 for a character no number has, then `Val`, which reads a point in every locale |

## The arithmetic

Visual Basic raises run-time error 6 for every integer result outside its type, so the IR's wrapping
`+`, `-`, `*` and negation of 32 bits are `PlyAdd32`, `PlySub32`, `PlyMul32` and `PlyNeg32`, exact in
`Double` and reduced modulo 2^32 (16-bit halves for the product); of two operands of at most 16 bits the
backend writes Visual Basic's own operator, which cannot overflow. A checked operation is Visual Basic's
operator through its conversion function, `CLng(a + b)`: LibreOffice Basic computes such intermediates
wider and checks only what is stored or converted, so the conversion makes both trap at the same place.
Signed division is `PlyDiv32`, which raises error 6 for `&H80000000 \ -1` itself; LibreOffice ends with
a processor fault there. Unsigned division and remainder, the shifts, and every narrowing conversion
that wraps or checks are helpers; a float is truncated with `Fix` before `CLng`, `CInt` or `CByte`,
which round half to even. Floating-point arithmetic is Visual Basic's own; a `Single` result is
rounded with `CSng`. Visual Basic raises error 6 or 11 where IEEE 754 gives an infinity or a NaN, so a
module whose floating-point results leave the finite range traps where the IR does not (see the known
differences of [exceptions](../reference/exceptions.md#visual-basic)).

## Control flow

`And` and `Or` evaluate both operands, so `&&` and `||` are an `If` that assigns a temporary unless the
right operand can neither trap nor change anything; a conditional is `IIf` through the type's conversion
function under the same condition, an `If` otherwise. Every loop is a `Do` loop: a test that needs
statements of its own is written inside it with `Exit Do`; `continue` is a `GoTo` to a label before the
loop's end; a `break` out of a switch a `GoTo` to a label after `End Select`. A try is
`On Error GoTo Catch`, whose label takes `Err.Number` and resumes (which clears `Err`) at a dispatch
that runs the first catch clause whose class the number stands for (11 divide by zero, 6 overflow,
9 index out of range, 13 invalid cast, 91 null reference, 5 argument) or raises the number again;
every jump out of a try puts the enclosing handler in force again. A throw of a library exception is
`Err.Raise` with its number.

## Verification

- `VisualBasicParseBackTests`: every prototype fixture in every profile reads back to the tree it was
  printed from and prints the same text again.
- `VisualBasicOracleTests` (layer `visualbasic`): the prototype fixtures translated from C# print, on
  LibreOffice, what the IR interpreter computes for the same calls; the classic fixtures print their
  documented values after the round trip through the IR; Adler-32 and CRC-32 give their published values.

## Visual Basic .NET

**Status:** implemented for the first verified subset ([#1082](https://github.com/Hawkynt/Polypiler/issues/1082),
the VB.NET parts of [#1088](https://github.com/Hawkynt/Polypiler/issues/1088), [#1085](https://github.com/Hawkynt/Polypiler/issues/1085),
[#1086](https://github.com/Hawkynt/Polypiler/issues/1086), [#1087](https://github.com/Hawkynt/Polypiler/issues/1087) and
[#1089](https://github.com/Hawkynt/Polypiler/issues/1089)), checked against the Visual Basic compiler and runtime
of the pinned `dotnet-sdk` (layer `visualbasic`, [runner](#verification-of-visual-basic-net)).

`polypiler translate <file> --to visualbasic --output <directory> --profile vbnet/vb<version>[/<framework>] [--vb-integer-checks]`
writes `<Module>.vb`, one `Public Module`, and `<Module>.vbproj`, a library for the profile's framework
and language version with every option stated, `TreatWarningsAsErrors`, and `RemoveIntegerChecks`,
which the code is written for: `true` unless `--vb-integer-checks` asks for a project with integer
checks on; for .NET Standard it embeds the core of the Visual Basic runtime (`VBRuntime=Embed`), which
that framework lacks. The generated code names every type
of the framework in full and calls nothing of `Microsoft.VisualBasic`.

| Profile | What it is |
| --- | --- |
| `vbnet/vb9` … `vbnet/vb16.9` | Visual Basic 9 (Visual Studio 2008), 10, 11, 12, 14, 15, 15.3, 15.5, 16 and 16.9, the last, on .NET 10 |
| `vbnet/vb<version>/<framework>` | the same for `net2.0`, `net3.5`, `net4.6`, `net4.8`, `netstandard2.0`, `netstandard2.1`, `netcoreapp3.1`, `net8.0` or `net10.0` |

The tree (`Net/VnSyntax.cs`) is built by the writer (`Net/VbNetWriter*.cs`) and printed by `VnPrinter`:
four spaces, LF, parentheses where the operator precedence of the language reference needs them, a
keyword name in brackets (`[Next]`) so that the compiled member keeps the IR's name. `VbNetCapabilities`
chooses by language version and framework:

| Construct | First | Below |
| --- | --- | --- |
| implicit line continuation (a long table over several lines) | Visual Basic 10 | ` _` at the end of each line |
| `BitConverter.SingleToInt32Bits`, `Int32BitsToSingle` | .NET Standard 2.1 | through `BitConverter.GetBytes` |
| `Math.Min`, `Math.Max` ordering -0 before +0 | .NET Core 3.0 | `PlyMin`, `PlyMax` |
| the shortest text of a `Double` (`ToString` in the invariant culture) | .NET Core 3.0 | refused, `PLY2657` |
| `Trim`, `TrimStart`, `TrimEnd` of `Char.IsWhiteSpace` | .NET Framework 4 | refused, `PLY2657` |

| IR | Visual Basic .NET |
| --- | --- |
| `i8` … `u64`, `f32`, `f64`, `bool`, string | `SByte` … `ULong`, `Single`, `Double`, `Boolean`, `String`; literals with their type suffix (`5UI`, `5L`, `CByte(5)`), the smallest values in hexadecimal |
| array, struct | `T()` from 0, `New T(n - 1) {}`; a `Structure` with a constructor of its fields; equality `PlyEquals` |
| wrapping `+ - *`, negation, shifts, `Divide`, `Remainder` | the operators: `\` and `Mod` throw as the IR traps; a logical shift of a signed value through its unsigned type |
| checked `+ - *`, negation, conversion | `PlyAddChk`, `PlySubChk`, `PlyMulChk`, `PlyNegChk`, `PlyChk<T>`: the exact result in `Decimal`, an `OverflowException` outside the type |
| floating point to integer | `PlySat<T>` (truncated, saturated, 0 for a NaN) or `PlyFix<T>` (truncated, an `OverflowException` for a NaN or outside the type) |
| a step or an assignment as a value, a throw as a value | `PlyStep`, `PlyStepChk`, `PlyBind`, `PlyThrow(Of T)` |
| a test of an exception's class | `TypeOf e Is T` |
| `&&`, `||`, conditional | `AndAlso`, `OrElse`, `If(c, a, b)` |
| loops, `continue` of a `for` with a step, switch, `break` | `Do … Loop`, a `GoTo` to a label before the step, `Select Case` with `Case Else` last, `Exit Do` / `Exit Select` |
| try, catch with filter, finally, throw, rethrow | `Try`, `Catch e As T When c`, `Finally`, `Throw`, `Throw` |
| globals, tables, the module's initializer | `Private` fields with initializers, `Private ReadOnly` arrays, `Sub New` |

With integer checks on (`--vb-integer-checks`) the operators and conversion functions throw, so the
IR's checked `+ - *` and negation are the operators and the wrapping ones are written out through
helpers that cannot overflow: in the next wider type for 32 bits, in 32-bit halves for 64 bits
(`PlyAddWrap`, `PlySubWrap`, `PlyMulWrap`, `PlyNegWrap` over `PlyBits`, `PlyAddBits`, `PlyMulBits`), a
conversion that keeps the low bits through `PlyWrap<T>` of the two's complement, the logical right
shift of a signed value through `PlyShrU`, a step through `PlyStepWrap`.

Refused: classes of the module's own and interfaces, the dynamic type, foreign functions, structs
without fields (`PLY2656`), and the string operations of ECMA-262 (split, pad) (`PLY2657`).

### Verification of Visual Basic .NET

- `VbNetDifferentialTests`: every prototype fixture, emitted for `vbnet/vb16.9` with `RemoveIntegerChecks`
  and with integer checks on, compiled by Roslyn in memory with the project's options and called,
  returns what the IR interpreter returns; `VbNetIntegerChecksTests` calls the wrapping operations at
  the boundaries of each type and SplitMix64 with integer checks on; the first
  fixtures compile without a warning in every language version. `VbNetBackendTests` compiles them
  against the reference assemblies of all nine frameworks.
- `VbNetOracleTests` (layer `visualbasic`): `VbNetRunner` builds the modules with a generated driver by
  `dotnet build` of the pinned SDK, offline, and runs it: the prototype fixtures in every language
  version, the Visual Basic .NET fixtures as written and after the round trip, and the generated project.
