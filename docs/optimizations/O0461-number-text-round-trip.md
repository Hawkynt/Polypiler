# O0461 — Cancel an integer-to-text-to-number round trip whose intermediate text is unobservable

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0461`.

## What it does

Code that moves values through a textual interface — a generic field accessor, a configuration layer, a
translated `VAL(STR$(n))` — formats a number only to parse it again. When the text has no other reader the
pair is a numeric conversion: for integers, formatting is exact and parsing its output yields the same
number. PB-Compiler implements the integer case as part of O0301 and documents why every other direction
stays. Polypiler has no operations for formatting or parsing numbers, so the rule has nothing to match
yet. PRD §4.8, §17.3, §8 P4.

## Example

### Before

```
// provisional spelling: library calls kept as SourceCall (see Blocking questions)
%t = SourceCall.string<latin1> "powerbasic:STR$(LONG)" %n          // %n: i32
%v = SourceCall.f64 "powerbasic:VAL(STRING)" %t
```

### After

```
%v = IntToFloat.f64[rne] %n
```

## When it applies

**String model:** `string<enc>` of [#379](https://github.com/Hawkynt/Polypiler/issues/379) — an immutable sequence of code units with value semantics, no identity and no capacity. (1) The formatter takes an integer and its identity is recorded as *exact*: its output denotes the operand's
mathematical value for every value of the type. (2) The parser's identity is recorded as the inverse on
that output: it accepts every string the formatter produces (sign, leading space, digits) and returns the
denoted number, correctly rounded if its result type is a float. (3) The formatter's signedness equals
the operand type's; a `u32` passed to a signed formatter is a different function. (4) The text has
exactly one user, the parser; it is immutable ([#379](https://github.com/Hawkynt/Polypiler/issues/379)) and therefore unobservable. (5) The result
is the conversion of the integer to the parser's result type with the parser's rounding
([#117](https://github.com/Hawkynt/Polypiler/issues/117)); for 64-bit integers into `f64` that is round-to-nearest-even unless the record says
otherwise. (6) Neither call has another effect (allocation aside). Which record carries (1) and (2) is the
open question below.

## How it is proven

`structural+interpreter` — composition of two recorded inverse functions; each admitted pair is checked against its real toolchain on the boundary values of every width. No solver, no timeout path.

## When it must not fire

- A `u32` value 4 000 000 000 through a signed 32-bit formatter prints `-294967296`; replacing the pair by an unsigned conversion yields 4e9.
- An `i64` value 9 007 199 254 740 993 parsed into `f64` is 9 007 199 254 740 992; the replacement must round the same way, not keep the integer.
- JavaScript `Number(String(-0))` is `+0`: a float round trip is not the identity.
- PowerBASIC `STR$` of a single-precision value prints about seven digits: `VAL(STR$(x!))` loses bits.
- The text is also printed: with two users the formatter must stay.
- A culture whose formatter inserts group separators or uses a different minus sign: the parser of another culture does not invert it.

## Switching it off

`--disable-opt O0461`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

every target: one numeric conversion replaces a formatter, a parser and an allocation. No readability concern.

## Interactions with other passes

would run after [#159](https://github.com/Hawkynt/Polypiler/issues/159) and before [#156](https://github.com/Hawkynt/Polypiler/issues/156); the phase depends on the answer below.
