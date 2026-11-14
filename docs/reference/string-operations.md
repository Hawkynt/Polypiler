# String operations

**Status:** implemented for the prototype: the IR, the C# and the JavaScript frontend, the C# and the JavaScript backend in every profile ([#1314](https://github.com/Hawkynt/Polypiler/issues/1314)).

A string of the IR, `str`, is an immutable sequence of UTF-16 code units that is never null; a code
unit is a `u16`. An unpaired surrogate is a code unit like any other. Every comparison, search and
split is ordinal - by code unit, never by culture - and every index and length counts code units.
Only `for…of` and `codePointAt` of JavaScript see code points, and each has an operation of its own.
`StringOperations` (`Polypiler.Core.IR/String`) holds the typing rules and the meaning the
interpreter runs; each operation has a spec document under [`spec/intrinsics/string`](../../spec/intrinsics/string),
and the type is [`spec/types/text/str.yaml`](../../spec/types/text/str.yaml).

## Operations

| Operation | Signature | Meaning | Trap |
| --- | --- | --- | --- |
| `StrLength` | `str → i32` | number of code units | — |
| `StrCodeUnitAt` | `(str, i32) → u16` | the code unit at an index | `IndexOutOfRange` outside `[0, length)` |
| `StrConcat` | `(str, str, …) → str` | two strings or more, in order | — |
| `StrFromCodeUnit` | `u16 → str` | the string of one code unit | — |
| `StrFromCodeUnits` | `(u16[], i32, i32) → str`, `u16[] → str` | a range of an array, or all of it | `IndexOutOfRange` outside the array |
| `StrToCodeUnits` | `str → u16[]` | a new array of the code units | — |
| `StrRepeat` | `(u16, i32) → str`, `(str, i32) → str` | the value `count` times | `IntegerOverflow` for a negative count |
| `StrSubstring` | `(str, i32, i32) → str`, `(str, i32) → str` | a range, or from a start to the end | `IndexOutOfRange` outside the string |
| `StrEquals` | `(str, str) → bool` | the same code units in the same order | — |
| `StrCompareOrdinal` | `(str, str) → i32` | -1, 0 or 1 by the first differing code unit, a proper prefix first | — |
| `StrIndexOf` | `(str, str \| u16[, i32]) → i32` | the first start at or after `from` (0), or -1; an empty needle stands at `from` | `IndexOutOfRange` for `from` outside `[0, length]` |
| `StrLastIndexOf` | `(str, str \| u16[, i32]) → i32` | the last start at or before `from` (the length), or -1 | as `StrIndexOf` |
| `StrStartsWith`, `StrEndsWith`, `StrContains` | `(str, str \| u16) → bool` | by code unit | — |
| `StrReplaceAll` | `(str, str, str) → str`, `(str, u16, u16) → str` | every occurrence, left to right | `InvalidOperation` for an empty needle |
| `StrReplaceFirst` | `(str, str, str) → str` | the first occurrence; an empty needle stands at 0 | — |
| `StrSplit` | `(str, str \| u16, i32) → str[]` | ECMA-262's split: `""` is `[""]`, an empty separator splits between every two code units, a limit that is not negative keeps the first parts | — |
| `StrJoin` | `(str, str[]) → str` | the parts with the separator between each two | — |
| `StrTrim`, `StrTrimStart`, `StrTrimEnd` | `(str, i32 set) → str` | without the white space of the set, a constant | — |
| `StrPadStart`, `StrPadEnd` | `(str, i32, str) → str` | ECMA-262's padding: the filler repeated and its last copy cut; the string itself when it is long enough or the filler is empty | — |
| `StrCodePointAt` | `(str, i32) → i32` | a pair of surrogates as its code point, any other code unit as itself | `IndexOutOfRange` outside the string |
| `StrNextCodePoint` | `(str, i32) → i32` | 2 for a pair of surrogates at the index, 1 otherwise | `IndexOutOfRange` outside the string |

A string is storable in locals, parameters, results, module constants (`IrModuleConst.OfStrings`) and
arrays; an array of strings is always a reference array, never a typed array. `new str[n]` does not
exist: no string could fill it, and .NET fills it with `null`. A struct with a field of type `str` has
no default value, for the same reason.

### The two white-space sets

| Set | Code units | Source |
| --- | --- | --- |
| `DotNet` (0) | U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F, U+3000 | `char.IsWhiteSpace` (Unicode `White_Space`), as the .NET API reference lists it |
| `Ecma` (1) | U+0009–U+000D, U+0020, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F, U+3000, U+FEFF | ECMA-262 2022 §12.2 `WhiteSpace` and §12.3 `LineTerminator` |

U+0085 is white space only to .NET and U+FEFF only to ECMA-262; U+180E and U+200B are neither.
The tests compare both tables with the Unicode data of the runtime.

## C# frontend

`CSharpStringApi` is the table; every row has a test. `string` locals, parameters, results, `const`
and `static readonly` fields and `string[]`; literals of every kind (the compiler folds them, `\U`
as a pair of surrogates); `s.Length`, `s[i]`; `+` and `+=` on strings and chars, `string.Concat`,
interpolation whose holes are strings or chars; `==`, `!=`, `Equals` (also with
`StringComparison.Ordinal`); `string.CompareOrdinal(a, b)` and `string.Compare(a, b,
StringComparison.Ordinal)` only against 0; `Substring`; `IndexOf` of a char, or of a string with
`StringComparison.Ordinal`; `LastIndexOf(char)`; `Contains`; `StartsWith`/`EndsWith` of a char or
with `StringComparison.Ordinal`; `Replace` of chars and of strings; `Split` at one char or a
constant string; `string.Join`; `Trim`, `TrimStart`, `TrimEnd` (the `DotNet` set); `PadLeft`,
`PadRight`; `new string(char, int)` and from a `char[]`; `ToCharArray`; `char.ToString`. A `switch`
on a string chooses its section by an index computed from `StrEquals` in source order, so that every
`break` and `continue` keeps its meaning; `foreach (char c in s)` is a loop over the code units.

Refused, each by name (`PLY1002`): the overloads that compare by culture - `IndexOf(string)`,
`LastIndexOf(string)`, `StartsWith(string)`, `EndsWith(string)`, `string.Compare(a, b)`, `CompareTo`
and every `StringComparison` but `Ordinal` - with the advice to pass `StringComparison.Ordinal`
(`"é".StartsWith("e")` is false in .NET and true with `Ordinal`); the value of `CompareOrdinal`
other than its sign, which .NET documents only by its sign; `Split` with a count, whose last part
keeps the rest of the string; case mapping (`ToUpper`, `ToLower`, the invariant forms), `Normalize`,
`string.Format`, `StringBuilder`, `null` of the type string, `new string[n]`; a number joined to text
and an interpolation with a number, until the conversion of numbers to text exists. `PadLeft` with a
negative width throws in .NET; the translation returns the string, as ECMA-262 does.

## JavaScript frontend

`JavaScriptStringApi` is the table; every row has a test against what node printed. A variable,
parameter or result that only strings reach is `str`: `@param {string}`, a literal, a method of a
string, and for a parameter without JSDoc a method only strings have (`charCodeAt`, `substring`,
`trim`, `split`, …) or a `for…of` over it. String literals and templates with string holes; `length`;
`charCodeAt` (`NaN` outside, written out); `s[i]` and `at` (which trap outside, the `js-oob-read`
caveat); `charAt` (`""` outside); `+`, `+=`, `concat`; `===`, `!==`, `==`, `!=` and `<`, `<=`, `>`,
`>=` between two strings; `substring` and `slice` with their clamping and swapping spelled out with
`Min` and `Max`; `indexOf` and `lastIndexOf` with a clamped position; `includes`, `startsWith`,
`endsWith`; `replace` and `replaceAll` with a constant replacement, whose `$$` and `$&` are
expanded; `split` with a limit through `ToUint32`; `join` of an array of strings; `trim`,
`trimStart`, `trimEnd` (the `Ecma` set); `padStart`, `padEnd`; `repeat`; `String.fromCharCode`;
`codePointAt` (which traps outside); `for (const ch of s)` over the code points; a `switch` on a
string, the first of two equal labels winning. An operand that the translation reads twice has to
be free of effects.

Refused, each by name (`PLY1102`): `localeCompare` and the locale methods, `toUpperCase`,
`toLowerCase`, `normalize`, tagged templates (`String.raw`), regular expressions, the patterns
`` $` ``, `$'` and `$n` and a replacement that is no constant, `replaceAll` with a needle that may be
empty (JavaScript inserts between every two code units), `String.fromCodePoint`, `new String`, and
a number joined to text.

## C# backend

Every operation is a row of `CSharpCapabilities` (`CSharpStringRows`); a row may hold a condition
on the node, which chooses the form of a trim by its white-space set and of a split by its limit.
Every search and comparison is written with its ordinal member; a code unit is a `ushort` and is
cast to `char` where a member takes one.

| IR | net2.0 · net3.5 | net4.6 · net4.8 · netstandard2.0 | netstandard2.1 and later |
| --- | --- | --- | --- |
| `StrConcat` | `string.Concat(a, b, …)` | same | same |
| `StrEquals`, `StrCompareOrdinal` | `string.Equals`, `string.CompareOrdinal(a, b) <op> 0` | same | same |
| `StrIndexOf` of a string | `IndexOf(n, from, StringComparison.Ordinal)` | same | same |
| `StrStartsWith`/`StrEndsWith` of a char | helper | helper | `StartsWith(char)`/`EndsWith(char)` |
| `StrContains` of a char | `IndexOf(c) >= 0` | same | `Contains(char)` |
| `StrSplit` at a char, no limit | `Split(new char[] { c })` | same | `Split(c)` |
| `StrTrim` of `DotNet` | helper with the table (Framework 2 and 3.5 trim another list) | `Trim()` | `Trim()` |
| `StrTrim` of `Ecma`, `StrReplaceFirst`, `StrPadStart`, `StrCodePointAt`, `StrLastIndexOf` of a string, `StrSplit` at a string or with a limit, `StrRepeat` of a string | helper | helper | helper |
| `StrNextCodePoint` | `(char.IsSurrogatePair(s, i) ? 2 : 1)` | same | same |

The helpers are C# 2 for .NET Framework 2.0, like every helper of the backend. Strings are always
joined with `string.Concat`, never with an interpolation, which every profile compiles to the same.

## JavaScript backend

A `str` is a JavaScript string and a `str[]` a plain `Array` at every level, with or without
`--typed-arrays`. Each row of `JavaScriptCapabilities.Standard` names what a level has.

| IR | es5 | es2015 · es2017 | es2020 | es2022 |
| --- | --- | --- | --- | --- |
| `StrConcat`, `StrEquals` | `a + b`, `a === b` | same | same | same |
| `StrCompareOrdinal` against 0 | `a < b` (ECMA-262 §7.2.13) | same | same | same |
| `StrStartsWith`, `StrEndsWith`, `StrContains` | helpers, `indexOf(n) !== -1` | `.startsWith`, `.endsWith`, `.includes` | same | same |
| `StrReplaceAll` | `s.split(n).join(r)`; a helper for a needle that may be empty | same | same | `.replaceAll(n, r)` for constants, `$` written `$$` |
| `StrReplaceFirst` | `.replace(n, r)` for a constant replacement, `$` written `$$`; a helper otherwise | same | same | same |
| `StrTrim` of `Ecma` | `.trim()` | same | same | same |
| `StrTrimStart`/`StrTrimEnd` of `Ecma` | helper | helper | `.trimStart()`/`.trimEnd()` | same |
| `StrTrim…` of `DotNet` | helper `$trimNet` with the table | same | same | same |
| `StrPadStart`/`StrPadEnd` | helper | `.padStart`/`.padEnd` from es2017 | same | same |
| `StrRepeat` | helper | helper around `.repeat` | same | same |
| `StrCodeUnitAt`, `StrSubstring`, `StrIndexOf` from a position | helpers that throw `RangeError("index out of range")` | same | same | same |
| `StrCodePointAt` | helper | helper around `.codePointAt` | same | same |

A helper checks what the IR traps on and JavaScript would clamp or answer with `NaN` or `undefined`.
String literals are written in ASCII with `\uXXXX` escapes in both backends, so an unpaired surrogate
survives the file.

## Interpretations

- A trap is the exception each backend already throws (`IndexOutOfRangeException`,
  `ArgumentOutOfRangeException` from a member, `OverflowException` from a helper; `RangeError`) until
  the traps are mapped; the differential tests compare a trap as a trap.
- `StrLastIndexOf` takes the last start at or before its position, as ECMA-262 does; .NET's
  `LastIndexOf(string, int)` counts from the end of the match, so only `LastIndexOf(char)` and
  `LastIndexOf(string, StringComparison.Ordinal)` of C# import, and a helper writes the string form.
- `Split(char[], int)` of C# keeps the rest of the string in its last part, which no operation does:
  it is refused rather than given ECMA-262's limit.
- The C# backend writes no interpolated string and the JavaScript backend no template literal; both
  forms mean the same as the concatenation they write.
