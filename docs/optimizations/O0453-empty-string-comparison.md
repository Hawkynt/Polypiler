# O0453 — Compare a string with the empty string by testing its length

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0453`.

## What it does

`s == ""` ends every input loop of DOS-era code and guards half of all parsing functions. Executed as a
general comparison it calls a routine that walks two strings to decide something the length already says:
a string equals the empty string exactly when it has no code units. Where the target has no optimizer to
see that — or where the comparison is a library call with setup cost — the length test is one load and one
compare, and it lets the general comparison routine drop out of small programs entirely. PRD §4.8, §8 P4.

## Example

### Before

```
%e = StrEq %s, string<latin1>("")
Br %e, done, more
```

### After

```
%n = StrLen.uptr[unit=codeunit] %s
%e = ICmp.eq.uptr %n, 0
Br %e, done, more
```

## When it applies

**String model:** `string<enc>` of [#379](https://github.com/Hawkynt/Polypiler/issues/379) — an immutable sequence of code units with value semantics, no identity and no capacity; two references to one string cannot be told apart, and nothing can mutate a string another name still reads. (1) The comparison is `StrEq` or `StrCmp[ordinal]`: code-unit comparison without normalization. (2) Both operands
are non-optional strings of one type; a `%s` obtained by `Unwrap` keeps its `Unwrap` (and its
`NullReference` trap) in front of the length test. (3) The other operand is proven empty. (4) The length
is taken in **code units**: zero code units iff zero scalars, and the code-unit length is stored, whereas
a scalar count of UTF-8 text is a scan. (5) Only `ICmp.eq`/`ICmp.ne` of the unsigned length against 0 are
produced — never a signed relational test of a length. (6) For `StrCmp %s, e` every user must be an
`ICmp` of the result against the constant 0; the replacements are: `eq 0` → length `eq 0`; `ne 0` →
length `ne 0`; `sgt 0` → length `ne 0`; `sge 0` → `true`; `slt 0` → `false`; `sle 0` → length `eq 0`
(a non-empty string is greater than its empty prefix). For `StrCmp e, %s` the signs mirror: `slt 0` →
length `ne 0`; `sle 0` → `true`; `sgt 0` → `false`; `sge 0` → length `eq 0`. Any other user (the value
stored, returned, compared with 1) blocks the rule. (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155): applied when the profile prices
`StrEq` with a constant operand above `StrLen` plus a compare; a profile whose native equality is cheap
and idiomatic (`s == ""`) keeps the comparison.

## How it is proven

`structural+interpreter`: equality with the zero-length sequence holds iff the length is zero; the sign table follows from 'a proper prefix is smaller' and is enumerated by the interpreter over all strings up to three units. No solver, no timeout path.

## When it must not fire

- C#: `s == ""` is false for `s == null`, while `s.Length == 0` throws. The IR type is `optional<string>`; the test must stay behind the null check the frontend emitted.
- A fixed-length field of four spaces compared with `""` under padded comparison is equal, yet its length is 4.
- A string of one zero unit (`CHR$(0)`) has length 1 and is not empty; a terminated-string test `s[0] == 0` would call it empty.
- .NET culture-sensitive comparison ignores U+00AD (its documentation equates `"animal"` with `"ani\u00ADmal"`); by the same rule a string of soft hyphens compares equal to `""` although its length is not 0.
- A 16-bit signed length compared with `> 0`: a string of 40 000 bytes has a negative signed length. Only `eq`/`ne` against 0 are exact.
- `StrCmp(s, "")` stored in a variable that is later compared with 1: the result's magnitude is then observed, not only its sign.

## Switching it off

`--disable-opt O0453`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

targets where a string comparison is a call: assembly and machine-code backends, C (`strcmp`/`memcmp`), PowerBASIC 3.5-class compilers, tokenized BASIC. For C#, Java, JavaScript, Python, Rust and Go the profile prices the native comparison low, the rule does not fire, and the source keeps the idiomatic `s == ""`; where a backend prefers `IsEmpty`-style spellings it chooses them itself from the unchanged `StrEq`.

## Interactions with other passes

runs in phase `optimize` after `OPT-STR-CONCATFOLD` and `OPT-STR-EMPTYOPERAND`; the `ICmp` feeds branch refinement of [#94](https://github.com/Hawkynt/Polypiler/issues/94) and thereby `OPT-STR-ZEROSLICE`; [#158](https://github.com/Hawkynt/Polypiler/issues/158) merges repeated `StrLen` of one value; runs before `OPT-STR-LENGUARD`, which reuses its classification of `StrCmp` users.
