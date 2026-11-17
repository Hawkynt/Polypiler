# Caller contracts

A caller contract states what the user knows and the compiler cannot prove about a value at the
boundary of a module: "`transform` is only ever called with bytes", "`n` is at most 65535" (PRD
§4.6). A contract is never trusted silently. It is either **checked** — a test of constant cost at
the boundary refuses every call that violates it, before any effect of the callee — or
**unchecked**, and then it says so: the warning `PLY0512`, and an entry with `checked: false` in the
`assumptions` section of the manifest.

```
polypiler translate sum.cs --from csharp --to javascript --output out \
  --assume "Sum.n: int[0, 65535]" \
  --assume-unchecked "Sum.data: seq<u8>[0..1024]" \
  --assume-file contracts.txt
```

`--assume` (checked) and `--assume-unchecked` take one contract per occurrence; a text with a `:` is
a contract, one without is a value fact of the [pipeline options](../cli/pipeline-options.md).
`--assume-file <path>` reads one checked contract per line; `#` starts a comment, blank lines are
skipped.

## Grammar

Whitespace between tokens is insignificant.

```
assumption := target ":" contract
target     := function "." parameter | function ".return" | "global" "." name
contract   := scalar | "seq<" contract ">" [ "[" length "]" ] | record
            | "nullable" "(" contract ")" | "nonnull" "(" contract ")"
record     := "record" "{" field ":" contract { "," field ":" contract } "}"
field      := identifier
scalar     := "u8" | "u16" | "u32" | "u64" | "i8" | "i16" | "i32" | "i64" | "f32" | "f64" | "bool" | "text"
            | "int" "[" integer "," integer "]"
length     := integer | integer ".." integer
```

Examples: `transform.data: seq<u8>`, `sum.n: int[0, 65535]`, `crc.table: seq<u32>[256]`,
`area.shape: record{ w: int[0, 65535], h: nullable(int[0, 65535]) }`.

Every contract excludes the absent value unless it is wrapped in `nullable(…)`; `nonnull(c)` is `c`.
`record{…}` states that the value has at least the listed fields, each satisfying its contract. The
language-native spellings are accepted for a source of their language only: the typed arrays of
JavaScript (`Uint8Array` = `seq<u8>`, `Int32Array` = `seq<i32>`, …), Python's `bytes` = `seq<u8>` and
`Optional[c]` = `nullable(c)`, Perl's `arrayref<c>` = `seq<c>` and `hashref{…}` = `record{…}`.

## What a contract does

`ContractEnforcer` (`Polypiler.Core.Analysis.Dynamic`) runs on the imported module, before the
optimizer. It resolves the target against the exported functions and the globals and compares the
contract with the type the module proves for it:

| The type of the target | Checked (`--assume`) | Unchecked (`--assume-unchecked`) |
| --- | --- | --- |
| guarantees the contract (`u8` for `int[0, 255]`, `u8[]` for `seq<u8>`) | `PLY0515`, nothing to check; listed `checked: true` | the same |
| admits no value that satisfies it (`str` for `i32`) | `PLY0513`, nothing is written | the same |
| narrower by a test of constant cost: a fixed-width integer against a range or a narrower integer scalar, an array against a length | the test at the boundary; listed `checked: true` | `PLY0512`; listed `checked: false` |
| narrower only by a test that is not of constant cost or not built | `PLY0516` | `PLY0512`; listed `checked: false` |

A parameter is tested at the entry of its function, in parameter order, before any other statement;
a result before every `return`. A violating value throws an `ArgumentException` whose message is
`ContractViolation: <target>: <contract>`; each backend writes it as its own exception.

Not of constant cost, or not built yet, and therefore `PLY0516` when checked: the elements of a
sequence, an integer contract on a floating-point or arbitrary-precision value, `f32` on an `f64`, a
non-nullable contract on an optional value, the fields of a record, and a global, which no call
passes.

## Diagnostics

| Code | Severity | Message |
| --- | --- | --- |
| [`PLY0512`](../errors/PLY0512.md) | warning, strict | `assumption '<target>: <contract>' is trusted without a run-time check; results are undefined for calls that violate it` |
| [`PLY0513`](../errors/PLY0513.md) | error, exit 4 | `assumption '<target>: <contract>' contradicts a proven fact: <fact> at <location>` |
| [`PLY0514`](../errors/PLY0514.md) | error, exit 2 | `invalid assumption '<text>': <problem>` |
| [`PLY0515`](../errors/PLY0515.md) | warning | `assumption '<target>: <contract>' had no effect` |
| [`PLY0516`](../errors/PLY0516.md) | error, exit 2 | `assumption '<target>: <contract>' cannot be checked at run time in O(1); use --assume-unchecked to accept it explicitly` |

Under `--strict` an unchecked contract is the error `PLY0010`: a strict build rests on no trusted
assumption.

## Manifest

The `assumptions` section lists every accepted contract and every value fact of `--assume`, each with
`target`, `contract`, `checked`, `origin` (`--assume`, `--assume-unchecked`,
`--assume-file <path>:<line>`) and `usedBy`, the number of facts derived from it. A value fact is
listed with `checked: false`: nothing checks it.
