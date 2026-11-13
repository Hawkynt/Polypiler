# O0450 — Fold concatenations of string constants into one constant

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0450`.

## What it does

Source code builds fixed text from pieces — `"<" + tag + ">"` with a constant tag, a path from named
constants, a message split over lines — and after inlining and constant propagation both operands of a
`StrConcat` are often constants. Each one left in place is an allocation and a copy at run time on every
target that has no compiler behind it to fold it, and it hides the constant from every later string rule.
[#154](https://github.com/Hawkynt/Polypiler/issues/154) folds integer operations only. PRD §4.8 (constant folding), §8 P4.

## Example

### Before

```
%t = StrConcat string<utf8>("<"), string<utf8>("html")
%s = StrConcat %t, string<utf8>(">")
```

### After

```
%s = string<utf8>("<html>")                   // %t has no other user and is removed by O0005
```

## When it applies

**String model:** `string<enc>` of [#379](https://github.com/Hawkynt/Polypiler/issues/379) — an immutable sequence of code units with value semantics, no identity and no capacity; two references to one string cannot be told apart, and nothing can mutate a string another name still reads. (1) Both operands are constants of exactly the result's type (same encoding, same validation flag). (2) The
result is the code units of the left operand followed by those of the right — no decoding, no
normalization, no terminator handling; an embedded zero unit is an ordinary unit. (3) For a validated type
the result is well formed because both operands are ([#379](https://github.com/Hawkynt/Polypiler/issues/379)); for an `[unvalidated]` type the units
are joined as they are, even where the join completes or breaks a surrogate pair. (4) The result has at
most 65 536 code units; a larger one is not created (the limit bounds module size and pass time and is
charged to the work-unit budget of [#157](https://github.com/Hawkynt/Polypiler/issues/157), one unit per code unit). (5) `StrConcat` is pure
([#128](https://github.com/Hawkynt/Polypiler/issues/128)); removing it changes no effect. No source language can observe the difference: identity of
the result is not part of the model.

## How it is proven

`structural+interpreter`: the fold is the definition of `StrConcat` evaluated at compile time; equality with the interpreter's evaluator is checked on every fixture and on the randomized run. No solver, no timeout path.

## When it must not fire

- `string<utf16le>[unvalidated]` operands with the units `D83D` and `DE00`: the result is the two units `D83D DE00`. A folder that round-trips through a validating host API produces `FFFD FFFD`.
- `string<latin1>("caf\xE9")` + `"!"`: five units `63 61 66 E9 21`. Folding through a UTF-8 host string turns `E9` into two units.
- `"e"` + `"\u{301}"` in UTF-8: three units `65 CC 81`; composing to U+00E9 (`C3 A9`) changes length and every later index.
- `"a\x00"` + `"b"`: three units. A helper that treats the zero as a terminator yields `"ab"`.
- Doubling a 40 000-unit constant in a chain of twenty concatenations: without the limit the module holds a 40-gigabyte literal.
- An intermediate `%t` that is also returned: it must survive; only the fold of `%s` happens.

## Switching it off

`--disable-opt O0450`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

every target. Mature compilers fold literal concatenation themselves, but they see it only if it still is one after translation, and the fold is what lets later string rules fire. In generated source the result is a single literal, which reads at least as well as the pieces; a source backend remains free to break a long literal across lines. The gain in allocations and copies is real on targets with no optimizing compiler behind them (PowerBASIC, tokenized BASIC, 6502, assembly).

## Interactions with other passes

runs in phase `optimize` after [#164](https://github.com/Hawkynt/Polypiler/issues/164), [#162](https://github.com/Hawkynt/Polypiler/issues/162) and [#159](https://github.com/Hawkynt/Polypiler/issues/159) (which expose constant operands) and before `OPT-STR-CONCATN`; enables `OPT-STR-LITIDENTITY` and `OPT-STR-EMPTYOPERAND`; dead operands are removed by [#156](https://github.com/Hawkynt/Polypiler/issues/156).
