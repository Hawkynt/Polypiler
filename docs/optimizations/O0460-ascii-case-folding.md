# O0460 — Fold case conversion of a constant whose code units are all 7-bit ASCII

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0460`.

## What it does

Upper- and lower-casing cannot in general assume a 7-bit alphabet: code units above 127 belong to a code
page or to a multi-unit character, and many library functions consult a culture. A string *constant*,
however, carries its exact code units, and when all of them are below 128 a culture-independent case
conversion is a compile-time operation — keyword tables, protocol tokens and format strings are the usual
cases. PB-Compiler implements this as O0300. Polypiler cannot yet: its IR has no case-conversion
operation whose meaning the rule could rely on. PRD §4.8, §17.3, §8 P4.

## Example

### Before

```
// provisional spelling: a library call kept as SourceCall (see Blocking questions)
%u = SourceCall.string<latin1> "powerbasic:UCASE$(STRING)" string<latin1>("status: ok")
```

### After

```
%u = string<latin1>("STATUS: OK")
```

## When it applies

**String model:** `string<enc>` of [#379](https://github.com/Hawkynt/Polypiler/issues/379) — an immutable sequence of code units with value semantics, no identity and no capacity. (1) The operand is a constant and every code unit is below 0x80. (2) The conversion's identity is recorded as
ASCII-invariant: on input below 0x80 it maps exactly `a`–`z` to `A`–`Z` (or the reverse) and returns
every other unit unchanged, independent of culture, locale and code page. (3) The result has the same
length and type. (4) The call is pure for constant input under that record. Which record carries (2) is
the open question below; until it exists nothing may be folded.

## How it is proven

`structural+interpreter` — the fold is the recorded definition evaluated at compile time; each admitted identity is checked against its real library on all 128 ASCII units. No solver, no timeout path.

## When it must not fire

- .NET `"i".ToUpper()` under the culture `tr-TR` is `"İ"` (U+0130) and `"I".ToLower()` is `"ı"` (U+0131): pure ASCII input, non-ASCII output. Only the invariant-culture overloads qualify.
- C `toupper` under a locale other than `"C"`.
- A constant containing byte 0x84 in code page 437 (`ä`): whether it has an upper-case partner depends on the active code page, not on ASCII.
- Applying the 0x20 difference outside the letter ranges: `'@'` (0x40) would become `` '`' `` (0x60), `'['` would become `'{'`.
- A UTF-8 constant with a multi-unit character: its continuation units are ≥ 0x80 and the constant does not qualify, even though its ASCII letters could be mapped.

## Switching it off

`--disable-opt O0460`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

every target: a constant replaces a library call, and targets without a case-conversion routine (assembly, small BASICs) no longer need one for constant text. No readability concern.

## Interactions with other passes

would run after `OPT-STR-CONCATFOLD` and before `OPT-STR-LITIDENTITY`; the phase depends on the answer below (`recognize` on `SourceCall`, or `optimize` on a canonical operation).
