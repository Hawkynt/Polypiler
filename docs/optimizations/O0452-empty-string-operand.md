# O0452 — Drop empty operands from string concatenations

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0452`.

## What it does

`s + ""` survives translation surprisingly often: a default argument, a macro expansion, a format with an
empty separator, a constant that folded to nothing. On a target that executes concatenation as written it
allocates a copy of `s` for no reason. For immutable strings the identity is as simple as `x + 0 = x`, the
string counterpart of the integer identities of [#154](https://github.com/Hawkynt/Polypiler/issues/154). PRD §4.8, §8 P4.

## Example

### Before

```
%t = StrConcat %s, string<latin1>("")
%u = StrConcatN %a, string<latin1>(""), %b    // after O0451
```

### After

```
// every use of %t is replaced by %s
%u = StrConcat %a, %b
```

## When it applies

**String model:** `string<enc>` of [#379](https://github.com/Hawkynt/Polypiler/issues/379) — an immutable sequence of code units with value semantics, no identity and no capacity; two references to one string cannot be told apart, and nothing can mutate a string another name still reads. (1) The empty operand is the zero-length constant or is proven empty at the concatenation by a dominating
edge on which `ICmp.eq (StrLen[unit=codeunit] %e), 0` holds; a length that is merely unknown blocks the
rule. (2) The surviving operand has exactly the result's type, including the validation flag; otherwise
the concatenation also converts and is kept. (3) Because strings are immutable values without identity,
`%s` and a fresh copy of `%s` are indistinguishable: no later instruction can modify one and read the
other. (4) `StrConcat` cannot trap except by allocation failure, which the rewrite only removes; under
a profile where allocation failure is observable that is still a refinement the profile must permit —
the same flag as in `OPT-STR-CONCATN` — or the rule does not fire. (5) Operands are SSA values; dropping
the use of the empty one removes no effect.

## How it is proven

`structural+range-facts`: appending zero code units leaves the sequence unchanged. No solver, no timeout path.

## When it must not fire

- Ruby: `t = s + ""; t << "x"` leaves `s` unchanged because `+` returns a new mutable object. Imported as a mutable `list<u8>` the copy is real and this rule does not apply; imported as immutable `string<enc>` the append is not expressible at all.
- C#: `s + ""` with `s == null` is `""`, not `null`. The operand is then `optional<string>` and the frontend's null-coalescing select stays; the rule sees only the non-optional value.
- JavaScript `n + ""` converts a number to text: a `Dyn` operation, not a concatenation with nothing.
- `s + " "`: one space is not empty.
- An operand whose length is 0 on one path only: without the dominating test the other path loses its text.
- A concatenation whose result type is `[unvalidated]` while `%s` is validated: replacing it changes the type of every later use.

## Switching it off

`--disable-opt O0452`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

every target; the generated source becomes shorter and more readable (`s` instead of `s + ""`), so no cost gate beyond the trivial one is needed. The saved allocation matters on targets that execute concatenation as written: C, PowerBASIC, tokenized BASIC, assembly.

## Interactions with other passes

runs in phase `optimize` after `OPT-STR-CONCATFOLD` (which may produce empty constants) and before `OPT-STR-CONCATN` counts its leaves; cleans up `StrConcatN` again after other passes; enables [#158](https://github.com/Hawkynt/Polypiler/issues/158) on the surviving value.
