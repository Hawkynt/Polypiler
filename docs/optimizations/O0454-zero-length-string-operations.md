# O0454 — Fold substring and construction operations of proven zero length to the empty string

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0454`.

## What it does

A substring of length zero is the empty string — unless taking it traps. Parsing code clamps its lengths
(`min(n, len − start)`), and once range analysis proves a clamped length to be zero on some path, the
slice there is an allocation and a call that produce nothing. Folding it needs more care than the integer
identities: in Rust and C# a zero-length slice at a start beyond the end fails, and in UTF-8 a zero-length
slice in the middle of a character does too. PB-Compiler split this from its empty-operand rule (O0266
from O0178); so does this backlog. PRD §4.8, §8 P4.

## Example

### Before

```
// range fact: %n in [0, 0];  %i in [0, 5];  StrLen %s = 5
%t = StrSlice[unit=codeunit, bounds, boundary=allow] %s, %i, %n
```

### After

```
%t = string<latin1>("")
```

## When it applies

**String model:** `string<enc>` of [#379](https://github.com/Hawkynt/Polypiler/issues/379) — an immutable sequence of code units with value semantics, no identity and no capacity; two references to one string cannot be told apart, and nothing can mutate a string another name still reads. (1) `%len` has the range `[0, 0]` at the slice ([#94](https://github.com/Hawkynt/Polypiler/issues/94)). (2) **Bounds:** under `inbounds #k` or `unchecked`
the access is not checked and the fold is free. Under `bounds` (optionally with a label) the slice traps
when `start > StrLen(s)` in the slice's unit; the fold needs `start ≤ StrLen(s)` proven
([#94](https://github.com/Hawkynt/Polypiler/issues/94), [#349](https://github.com/Hawkynt/Polypiler/issues/349)) — a zero-length slice at exactly the end is legal. (3) **Boundary:**
under `boundary=allow`, with `unit=scalar`, or in a fixed-width encoding (`ascii`, `latin1`) every start
is acceptable. Under `boundary=trap <label>` with `unit=codeunit` in `utf8` or `utf16le` the start must be
proven a scalar boundary: the constant 0, or equal to the code-unit length. (4) The result is the empty
constant of the slice's result type (an `allow` slice may be typed `[unvalidated]`). (5) For
`StrFromUnits`: the input slice's length is proven 0; neither `validate` nor `lossy` can act on an empty
input. (6) The operands are SSA values; their producers stay if they have effects or may trap
([#115](https://github.com/Hawkynt/Polypiler/issues/115)) and are otherwise removed by [#156](https://github.com/Hawkynt/Polypiler/issues/156).

## How it is proven

`range-facts+structural`: with no code unit selected the result is the zero-length sequence, and (2)–(3) exclude both traps a zero-length slice can raise. No solver, no timeout path.

## When it must not fire

- Rust `&s[7..7]` on a five-byte string panics; C# `"abc".Substring(4, 0)` throws while `Substring(3, 0)` returns `""`. A fold without the start proof makes the failing call succeed.
- Rust `&"né"[2..2]`: byte 2 is inside `é` (`6E C3 A9`), and even an empty slice there panics.
- A length that is zero only after a clamp the frontend did **not** emit: Python `s[7:9]` on a five-character string is `""` because Python clamps; the same indices under a `bounds` contract trap.
- `%len` with the range `[0, 1]`: zero is possible, not proven.
- A start computed by a call with effects: the fold removes the use, not the call.
- `StrFromUnits[validate]` of a slice that is empty on one path and `C3 28` on the other: only the proven-empty path may fold.

## Switching it off

`--disable-opt O0454`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

every target: the result is a constant, which no backend prints worse than a call. The saved call and allocation matter most where the substring is a runtime routine (PowerBASIC, C helpers, assembly); mature compilers cannot fold it themselves because the zero is known to Polypiler's range analysis, not to them.

## Interactions with other passes

runs in phase `optimize` after [#163](https://github.com/Hawkynt/Polypiler/issues/163) and [#164](https://github.com/Hawkynt/Polypiler/issues/164) (which sharpen the length) and after `OPT-STR-EMPTYCMP` (whose length tests refine ranges); its empty constants feed `OPT-STR-EMPTYOPERAND`, `OPT-STR-CONCATFOLD` and `OPT-STR-LITIDENTITY`.
