# ADR 2600 — The oracle of classic Visual Basic (VB6 / VBA): LibreOffice Basic in VBA mode

Area: languages

| | |
| --- | --- |
| Status | accepted |
| Index | [decision records](README.md) |
| Date | 2026-11-27 |
| Issue | [#1097](https://github.com/Hawkynt/Polypiler/issues/1097), for [#1090](https://github.com/Hawkynt/Polypiler/issues/1090) and [#1096](https://github.com/Hawkynt/Polypiler/issues/1096) |
| Evidence | [`evidence/2600`](evidence/2600), probe [`tests/fixtures/visualbasic/classic/ClassicProbe.bas`](../../tests/fixtures/visualbasic/classic/ClassicProbe.bas) |

## Question

Which compiler or interpreter of classic Visual Basic 6 / VBA standard modules can be the oracle of
the classic dialect, in CI, and where does it disagree with Visual Basic 6 / Office VBA on the numeric
and error-handling rules the profiles `visualbasic/vb6`, `vba6`, `vba7` and `vba7-64` claim?

## Options

| Option | Runs unattended in CI | Licence | Verdict |
| --- | --- | --- | --- |
| 1. Genuine VB6 / Office VBA | no: Windows and a licensed Office, no command line | proprietary | reference for the documented behaviour only |
| 2. LibreOffice Basic, `Option VBASupport 1`, `soffice --headless` | yes: Linux, a macro URL, 0.3 s per run | MPL-2.0, run as an external process | **chosen** |
| 3. twinBASIC | no: Windows only, the free edition has no unattended build for CI | proprietary | rejected |
| 4. VB.NET (`dotnet-sdk`, Roslyn VB) | yes | MIT | rejected in advance by #1097: 32-bit `Integer`, no `Variant`/`Currency`, different `On Error` clearing; it is the oracle of `vbnet` (#1096), not of the classic dialect |
| 5. No oracle | — | — | not needed |

## How the oracle is run

`VisualBasicRunner` (`Polypiler.Core.Verification/Runners`) writes, into the run's directory, which
is the run's `HOME`, a LibreOffice user profile (`.config/libreoffice/4/user`) whose `Standard`
library holds the modules and a generated driver module, and whose `registrymodifications.xcu`
marks the profile as set up (`ooSetupInstCompleted`) - without the flag the office replaces the
library with its preset on first start. It starts `soffice --headless --invisible --norestore
--nolockcheck macro:///Standard.PlyDriver.Main` as one plan of the cross-language runner and collects
`out.txt`, one line per call: the value through `CStr`, or `!` and `Err.Number`. Every call runs in a
function of its own under `On Error GoTo`. A module is loaded as the VBA editor imports a `.bas`
file: its `Attribute` lines are dropped and `Option VBASupport 1` is put in front. The office's locale is
stated, never the host's: `registrymodifications.xcu` sets `ooSetupSystemLocale` (`en-US` unless a run
names another, #1565), which decides the separators `CStr` writes and `CDbl` reads. The toolchain is
the lock key `libreoffice` (26.8.3.1), installed in CI from the Debian packages of the Document
Foundation's archive (`tooling/ci/visualbasic/install.sh`, job `layer-visualbasic`).

## Probe results

`ClassicProbe.bas` has one function per row of the construct table of #1090 and the error-handling
probes of #1096; [`evidence/2600/ClassicProbe.libreoffice-26.8.3.1.txt`](evidence/2600/ClassicProbe.libreoffice-26.8.3.1.txt)
is its output on LibreOffice 26.8.3.1. The column "VB6 / VBA" is the behaviour the Visual Basic 6
language reference and [MS-VBAL] document.

| Probe | LibreOffice | VB6 / VBA | Agrees |
| --- | --- | --- | --- |
| `Integer` 32767 + 1 stored in an `Integer` | 6 | 6 | yes |
| `Long` 2147483647 + 1, `Byte` 0 - 1 | 6, 6 | 6, 6 | yes |
| `Integer * Integer` (200 * 200) stored in a `Long` | 0, result 40000 | 6 (the product is an `Integer`) | **no** |
| `Integer + 1` (32767 + 1) stored in a `Long` | 0 | 6 | **no** |
| `Long * Long` (100000²) stored in a `Double` | 0 | 6 | **no** |
| `CInt(200 * 200)` | 6 | 6 | yes |
| `-32768 \ -1` as `Integer` | 6 | 6 | yes |
| `&H80000000 \ -1` as `Long` | **the office process dies (SIGFPE)** | 6 | **no** |
| `5 \ 0`, `5 / 0` | 11, 11 | 11, 11 | yes |
| `0 / 0` | 11 | 6 | **no** |
| `1E+300 * 1E+300` | 6 | 6 | yes |
| `2 ^ 1024` | an error no handler catches: a dialog | 6 | **no** |
| `a(4)` of `a(1 To 3)` | 9 | 9 | yes |
| `CLng("abc")` | 13 | 13 | yes |
| error in a called procedure without handler | 11 in the caller | 11 | yes |
| `Err` after a procedure with `On Error Resume Next` that raised 5 returns | 5 | 0 (cleared at exit) | **no** |
| handler of `On Error GoTo` reads `Err.Number` | 111 (100 + 11) | 111 | yes |
| `Resume Next` in a handler | continues after the failing statement | same | yes |
| `Resume Next` without an error | 20 | 20 | yes |
| `Err` after `Resume Next`, after `On Error GoTo 0` | 0, 0 | 0, 0 | yes |
| `Err` after a later successful statement | 11 (kept) | 11 | yes |
| error in an `If` condition under `Resume Next` | goes on inside the block | same | yes |
| parameter without `ByVal` | by reference | by reference | yes |
| `Dim a(1 To 3)`, `Dim b(3)` bounds | 1, 3, 0, 3 | same | yes |
| `And` evaluates both operands | yes (11) | yes | yes |
| `5 \ 2`, `-7 \ 2`, `-7 Mod 2`, `7 Mod -2`, `7.5 \ 2`, `5.5 Mod 2` | 2, -3, -1, 1, 4, 0 | same | yes |
| `CInt` 2.5, 3.5, -2.5, 0.5, 1.5; `CLng` 2.5, 3.5, -3.5; `CByte` 2.5, 254.5 | 2, 4, -2, 0, 2; 2, 4, -4; 2, 254 | same (half to even) | yes |
| `Fix(-2.5)`, `Int(-2.5)`, `Round(2.5)`, `Round(3.5)` | -2, -3, 2, 4 | same | yes |
| `Hex(255)`, `Hex(-1)`, `&HFFFF`, `&HFFFF&` | FF, FFFF, -1, 65535 | same | yes |
| `5 And 3`, `5 Or 3`, `5 Xor 3`, `Not 5`, `(5 Eqv 3) And 15` | 1, 7, 6, -6, 9 | same | yes |
| `TypeName(5 \ 2)` | Integer | Integer | yes |
| `TypeName(32767 + 1)`, `TypeName(CLng(1) * 2)`, `TypeName(2 ^ 2)`, `TypeName(CByte(1) + CByte(1))` | Long, Double, Integer, Double | Overflow, Long, Double, Byte | **no** |
| `CStr(1E+20)`, `CStr(CSng(1 / 3))` | 1E+020, 0.333333343 | 1E+20, 0.3333333 | **no** |
| `LongLong`, `LongPtr` | no such type (a compile error) | `LongLong` on Win64, `LongPtr` in VBA 7 | **no** |
| a function `As Long()` | a compile error | allowed since VB6 | **no** |
| a record assigned from a record parameter passed by reference, then changed | changes the caller's record too (an alias) | a copy | **no** |

### Variant (#1556)

[`tests/fixtures/visualbasic/classic/Variants.bas`](../../tests/fixtures/visualbasic/classic/Variants.bas)
as written prints [`evidence/2600/Variants.libreoffice-26.8.3.1.txt`](evidence/2600/Variants.libreoffice-26.8.3.1.txt);
the documented values are `Variants.expected.txt`, which the round trip prints on LibreOffice, since
the backend writes VBA's rules out as helpers over `VarType` and the conversion functions.

| Probe | LibreOffice | VB6 / VBA | Agrees |
| --- | --- | --- | --- |
| the value of `Integer` 32767 + 1, `Long` 2147483647 + 1, `Byte` 0 - 1 in a `Variant` | 32768, 2147483648, -1 | same (promoted) | yes |
| `TypeName` of those results | `Double` each | `Long`, `Double`, `Integer` | **no** |
| `TypeName`/`VarType`/`IsEmpty`/`IsNull` of an assigned or an unassigned `Variant` | `Integer 2`, `String 8`, `Boolean 11`, `Null 1`, `Empty 0` | same | yes |
| `Empty + 1`, `Empty & "a"`, `Empty = 0`, `Empty = ""` | 1, a, True, True | same | yes |
| `Null + 1`, `Null = 1`, `Null & Null` | `Null` | `Null` | yes |
| `Null & "a"` | error 13 | `"a"` | **no** |
| `Null` assigned to an `Integer` | error 13 | error 94 | **no** |
| `If Null Then` | the `Else` branch | the `Else` branch | yes |
| `Null And False`, `Null Or True` | `Null`, `Null` | `False`, `True` | **no** |
| `TypeName(Integer \ 2)` of `Variant`s | `Long` | `Integer` | **no** |
| `Null \ 0` | error 13 | `Null` | **no** |
| number below string in a `Variant` comparison (`10 < "9"`) | True | True | yes |

The frontend imports VBA's rules and the backend writes them out, so the round trip agrees with the
documented column on every row; the fixture as written is compared with LibreOffice's own column.

### Locale, Currency and Date, bitwise Variants, inference (#1564 - #1567)

Each fixture of [`tests/fixtures/visualbasic/classic`](../../tests/fixtures/visualbasic/classic) -
`Locale.bas` (in `en-US` and `de-DE`), `CurrencyDate.bas`, `Bitwise.bas`, `Inference.bas` - prints its
cases as written into the evidence files `<Fixture>[.<locale>].libreoffice-26.8.3.1.txt`; the
documented values are the `.expected.txt` files. The interpreter prints the documented values, and so
do C#, node and CPython; the round trip through the IR (vb6, vba7) prints them on every row of
`Bitwise.bas` and `Inference.bas`, and on the rows of `Locale.bas` where the office's own `CStr`, which
the round trip calls, agrees. `CurrencyDate.bas` has no round trip: a currency is an `i64`, which only
vba7-64 writes. The rows on which LibreOffice disagrees:

| Probe | LibreOffice | VB6 / VBA | Agrees |
| --- | --- | --- | --- |
| `CStr(1E+20)`, `CStr(1E+15)`, `CStr(0.00001)` | `1E+020`, `1000000000000000`, `0.00001` | `1E+20`, `1E+15`, `1E-05` (`%.15G`) | **no** |
| `CStr(123456789012344.5)` (a tie at the 15th digit) | `123456789012345` | `123456789012344` (a tie to even, as the C runtime's `%G`) | **no** |
| `CStr` of a `Single` (`CSng(1 / 3)`, `1E+7`, `0.1`) | `0.333333343`, `10000000`, `0.100000001` | `0.3333333`, `1E+07`, `0.1` | **no** |
| `CStr(1 / 3)`, `0.1 + 0.2`, `-0#`, `1.5E-100`, `0.0001` | `0.333333333333333`, `0.3`, `0`, `1.5E-100`, `0.0001` | same | yes |
| `CDbl("1,000.5")`, `" -1.5e3 "`, `"1D2"`, `"&H10"`, `".5"`, `"1,5"` (en-US) | 1000.5, -1500, 100, 16, 0.5, 15 | same | yes |
| `CDbl("1.5x")`, `""`, `"-"` | error 13 | error 13 | yes |
| `CDbl("1e400")` | error 13 | error 6 (out of range, [MS-VBAL] 5.5.1.2.4) | **no** |
| `Val("1,5")`, `Val("1d3")` | 15, 1 | 1, 1000 | **no** |
| `Val(" 1 2 3")`, `"&HFFFF"`, `"abc"`, `"2.5.3"` | 123, -1, 0, 2.5 | same | yes |
| a `String` declared, compared with a number (`"9" < 10`) | `False` (as strings) | `True` (as numbers) | **no** |
| de-DE: `CStr(1.5)`, `CDbl("1.000,5")`, `"2.5" * 2` in a `Variant` | `1,5`, `1000,5`, `50` | same | yes |
| `CStr` of a `Currency` | four decimals (`1.5000`) | the digits without trailing zeros (`1.5`) | **no** |
| `CCur(1) * 2.5`, `CCur(1) / CCur(3)` | `Currency` | `Double` ([MS-VBAL] 5.6.9.3.3, 5.6.9.3.5) | **no** |
| `CCur(0.00005)`; `CCur(1.0001) * CCur(1.5)` (a tie in ten thousandths) | 0.0001; 1.5001 | 0 (`x * 10000` half to even); 1.5002 (a tie to even) | **no** |
| `CCur` overflow, `CLng` of a `Currency` 2.5/3.5/-2.5 | error 6; 2, 4, -2 | same | yes |
| `DateSerial` normalisation, two-digit years, `Year`/`Month`/`Day`/`Weekday` of dates from 1899-12-30 on, `#yyyy-mm-dd#`, `TimeSerial` | same | same | yes |
| a date before 1899-12-30 (`CDate(-1.25)`) | linear (12/28/1899 18:00) | the day `Fix(v)`, the time `Abs(v - Fix(v))` (12/29/1899 06:00) | **no** |
| `Second` of 12:00:00.518 | 0 (truncated) | 1 (rounded) | **no** |
| `#12/31/9999# + 1` in a `Date` | 2958466 | error 6 | **no** |
| a date literal with a time (`#1/2/2000 6:00:00 PM#`) | a compile error | a `Date` | **no** |
| the type of a bitwise operator of `Variant`s (`CInt(12) And CInt(10)`, `Not CByte(200)`) | `Long` | `Integer`, `Byte` ([MS-VBAL] 5.6.9.8) | **no** |
| `0 And Null`, `5 Or Null` | `Null` | 0, 5 (the operator pages of [MS-VBAL] 5.6.9.8) | **no** |
| `Variant` arrays, `Array(…)`, `ReDim` of one | same | same | yes |
| `TypeName` of an untyped function's result, `Integer` 32767 + 1 | `Double` | `Long` | **no** |
| a `ByVal` `Variant` parameter assigned a `String` (`a = a & "!"` of `CDbl(1)`, #1591) | `1!` | `1!` | yes |

Beyond the table: a module LibreOffice cannot compile, and a run-time error no handler catches, stop
the headless office at a dialog; the runner reports `PLY2691` when the run's time limit (60 s) ends.

## Decision

LibreOffice Basic in VBA compatibility mode is the oracle of the classic dialect for every row it
agrees on. It computes the intermediate results of narrow integer and `Single` arithmetic in a wider
type and checks the range only where a value is stored or converted, so it is **no** oracle for an
overflow inside an expression, nor for the type of an expression, nor for the text of a floating-point
number, nor for `LongLong`. What follows:

1. **The backend writes code on which both agree**: every result of `Integer`, `Long`, `Byte` and
   `Single` arithmetic goes through its conversion function (`CInt(a + b)`, `CLng(…)`, `CSng(…)`),
   which traps where Visual Basic's operator does; signed 32-bit division is the helper `PlyDiv32`,
   which raises error 6 itself before `\` could fault; an array is returned in a `Variant`, which holds
   the typed array in every dialect; a record is copied out of a record parameter field by field. The differential tests therefore compare the documented semantics.
2. **The frontend imports the documented semantics** - `Integer * Integer` traps, 0/0 is error 6 -
   and the tests that compare an import with the oracle use only the rows that agree.
3. **Not verified, and documented as such**: `LongLong` and so `Currency` (profile `vba7-64`:
   emitted, compiled by no oracle), clearing `Err` at the exit of a procedure (not modelled by the
   frontend), and the text of a floating-point number on the rows above, where LibreOffice's `CStr`
   departs from `%.15G`.
4. **Parser**: the grammars found are GPL-3.0 (ANTLR `grammars-v4/vba`, Rubberduck), which the
   repository's LGPL cannot absorb, so the parser is written from [MS-VBAL] (rung 3 of the sourcing
   ladder) and LibreOffice is the oracle of its results.

## Consequences

- #1090 is implemented as the profiles `visualbasic/vb6`, `vba6`, `vba7` and `vba7-64` of
  `Polypiler.Core.Frontends.VisualBasic` and `Polypiler.Core.Backends.VisualBasic`; `visualbasic/classic`
  of the issue text is that set. VB.NET (`vbnet`, #1082, #1083) becomes a further profile of the same pair.
- #1096 gains its classic half: `VisualBasicRunner` on `libreoffice`; the `dotnet-sdk` half (the
  `vbnet` error table) stays open.
- The CI layer `visualbasic` (tooling/ci/layers/visualbasic.yaml) runs the oracle tests on Linux.
