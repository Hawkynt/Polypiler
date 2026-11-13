# O0459 — Fold comparisons of a string with itself and of string constants, using identity as the first proof

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0459`.

## What it does

After inlining and constant propagation a mode test such as `mode == "fast"` often compares a constant with
a constant, or a value with itself. PB-Compiler's O0299 set out to compare such operands by address at run
time and found that the profitable part is the compile-time one: identical operands are equal without
looking at a byte, different constants are decided by their contents, and nothing reaches the runtime.
The run-time shortcut is sound in one direction only — the same storage proves equality, different storage
proves nothing — and identity is not part of [#379](https://github.com/Hawkynt/Polypiler/issues/379)'s model at all. PRD §4.8, §8 P4.

## Example

### Before

```
%e = StrEq %m, %m
%f = StrEq string<utf8>("fast"), string<utf8>("slow")
%c = StrCmp.i32[ordinal] string<utf8>("ab"), string<utf8>("abc")
%l = ICmp.slt.i32 %c, 0
```

### After

```
%e = true
%f = false
%l = true                                     // a proper prefix is smaller
```

## When it applies

**String model:** `string<enc>` of [#379](https://github.com/Hawkynt/Polypiler/issues/379) — an immutable sequence of code units with value semantics, no identity and no capacity; two references to one string cannot be told apart, and nothing can mutate a string another name still reads. (1) Same value: both operands are the same SSA value of a non-optional string type. Equality of a string with
itself has no exception (there is no string counterpart of NaN), validated or not. (2) Constants: both
operands are constants of one type. If they are the same uniqued constant of the module they are equal
without inspection; otherwise the code units are compared — equal iff same length and same units.
Different constant objects with the same units are **equal**. (3) `StrCmp` of the same value or of equal
constants is exactly 0. For constants that differ, only the sign is specified by this rule: every user
must be an `ICmp` against 0 (`StringFacts.ZeroTestUsers` of `OPT-STR-EMPTYCMP`) and each is replaced by
its truth value; the order is that of the first differing code unit, compared as an unsigned number of
the encoding's unit width, with a proper prefix smaller. (4) No conclusion is drawn for a non-constant
operand from what it was copied or built from; that is constant propagation's business, not this
rule's. (5) The fold removes pure instructions only ([#128](https://github.com/Hawkynt/Polypiler/issues/128)).

## How it is proven

`structural+interpreter`: reflexivity of code-unit equality and compile-time evaluation of the operation's definition, checked against the interpreter on every fixture and on the randomized run. No solver, no timeout path.

## When it must not fire

- `m = copy of "fast"` held in a different buffer than the literal: comparing storage says 'different', the contents are equal. Identity proves equality, never inequality.
- Java `new String("fast") == "fast"` is `false`: that is reference equality, not `StrEq`; folding it to `true` changes the program.
- Ordering depends on the encoding: U+FF5E against U+1F600 is positive in UTF-16 (`FF5E` > `D83D`) and negative in UTF-8 (`EF` < `F0`). A fold that compares scalars gets one of them wrong.
- A three-way result of two different constants that is stored: whether it is −1 or −3 is not fixed by this rule, so it is left to the operation.
- An `optional<string>` compared with itself in a language where null is not equal to null under the comparison used: the operands are not `string<enc>` values and the rule does not see them.

## Switching it off

`--disable-opt O0459`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

every target: the result is a Boolean constant that lets [#164](https://github.com/Hawkynt/Polypiler/issues/164) remove whole branches, which no target compiler can do once the comparison has become a runtime call. Generated source loses a comparison whose outcome was fixed; nothing becomes less readable.

## Interactions with other passes

runs in phase `optimize` after [#158](https://github.com/Hawkynt/Polypiler/issues/158) (which makes equal values the same SSA value) and `OPT-STR-CONCATFOLD` (which produces constants), before `OPT-STR-LENGUARD`; its constants feed [#164](https://github.com/Hawkynt/Polypiler/issues/164) and [#156](https://github.com/Hawkynt/Polypiler/issues/156).
