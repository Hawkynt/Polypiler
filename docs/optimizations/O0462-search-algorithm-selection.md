# O0462 — Select the substring search by what is known about the needle

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0462`.

## What it does

A general substring search probes every position of the haystack. When the needle is a constant — and in
parsing code it almost always is — a better search is known at compile time: one code unit is an element
scan, two to four units are an element scan for the first unit plus a short verification, and five or
more justify Boyer–Moore–Horspool with a skip table built by the compiler instead of at every call.
PB-Compiler implements this as O0302. Polypiler has the element scan (`Memory.Find`) and the block
comparison (`Memory.Equal`) to build the fast forms from, but no substring-search operation to start
from. PRD §4.8, §17.3, §8 P4.

## Example

### Before

```
// provisional spelling: a library call kept as SourceCall (see Blocking questions)
%p = SourceCall.i16 "powerbasic:INSTR(STRING, STRING)" %s, string<latin1>("BEGIN")
```

### After

```
// m = 5: Horspool with the compile-time table  skip[B] = 4, skip[E] = 3, skip[G] = 2, skip[I] = 1, every other unit 5
global @skip.BEGIN: u8[256] = [5, 5, …] [readonly]
// loop: compare the unit under the needle's last position first, verify with Memory.Equal.u8 over 5 units,
//       advance by skip[haystack[pos + 4]]; stop when fewer than 5 units remain; result in the operation's convention
```

## When it applies

**String model:** `string<enc>` of [#379](https://github.com/Hawkynt/Polypiler/issues/379) — an immutable sequence of code units with value semantics, no identity and no capacity. (1) The needle is a constant of `m ≥ 1` code units of type `u8`; the haystack is any string of the same type.
(2) The rewritten search returns the position of the **first** occurrence at or after the start, in the
unit and base of the original operation, and the original's not-found value. (3) Positions where fewer
than `m` units remain are never probed, and no unit outside the haystack is read (`Memory.Find` and
`Memory.Equal` are issued with lengths inside it, [#189](https://github.com/Hawkynt/Polypiler/issues/189)). (4) Horspool: `skip[c] = m − 1 − i`
for the last index `i < m − 1` at which `c` occurs in the needle, `m` otherwise, saturated at 255; the
table is a read-only global ([#121](https://github.com/Hawkynt/Polypiler/issues/121)). (5) Searching a variable-width encoding by code units is
exact for well-formed UTF-8 (a well-formed needle cannot match at a non-boundary); a result expressed in
scalars must be converted from the code-unit position. (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) picks among the general search,
the three forms and a native search the profile lists. The operation that (2) refers to does not exist
yet; see below.

## How it is proven

`structural+interpreter` — each form is a search that examines a superset of the positions where a match can start, in ascending order; Horspool's skip never passes an occurrence. Checked against the naive search on the randomized run. No solver, no timeout path.

## When it must not fire

- An empty needle: languages disagree (the start position, 0, or an error); a specialized form must not pick one.
- A start position beyond the end, or zero in a one-based convention: the general operation clamps or returns not-found; the specialized form must do the same.
- A skip table entry computed from the needle's last unit as well (`skip[N] = 0` for `"BEGIN"`): the search never advances.
- Needle `"ABAB"` in `"ABAABAB"`: the first occurrence starts at the fourth unit; a candidate scan that resumes after a failed verification instead of one unit later skips it.
- A needle longer than the remaining haystack: the candidate scan must exclude those positions up front instead of reading past the end during verification.
- A 300-unit needle with an unsaturated `u8` table: a skip of 299 stored in a byte becomes 43 — still legal, but a skip of 256 becomes 0.

## Switching it off

`--disable-opt O0462`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

targets whose substring search is a plain loop or a slow runtime routine: assembly and machine-code backends, PowerBASIC, tokenized BASIC, C without a tuned `memmem`. Where the target's library search is already tuned (.NET `IndexOf` with ordinal comparison, Rust `str::find`, Go, JavaScript `indexOf`, glibc `memmem`), the profile lists it as native, the cost model keeps it, and the generated source stays one call.

## Interactions with other passes

would run late in `optimize` or in `legalize`, after `OPT-STR-CONCATFOLD` made needles constant; its `Memory.Find` is executed by `OPT-VEC-SWARSEARCH` where no native scan exists.
