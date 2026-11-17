# Pipeline options

Four options steer every command that runs the pass pipeline the same way: `--opt`, `--disable-opt`,
`--assume` and `--strict`. They are defined once, in `GlobalOptionSet` of `Polypiler.Cli`; a command
attaches them with `GlobalOptionSet.AddTo` and reads one validated `GlobalOptions` with
`GlobalOptionSet.Read`. Today `translate` attaches them. They are command options
([ADR 0004](../adr/0004-cli-design.md)): they stand after the command name, and before it, or on a
command that did not attach them, they are `PLY0003` (unknown option).

| Option | Kind | Values |
| --- | --- | --- |
| `--opt <level>` | single, default `balanced` | `none`, `balanced`, `speed`, `size`, `memory` |
| `--disable-opt O####[,O####]…` | list, a set: repeatable, duplicates collapse | optimization IDs the pipeline has |
| `--assume "<fact>"` | repeat-only: one fact per occurrence, never split at a comma | a fact of the grammar below |
| `--strict` | flag | — |

`--opt none` runs no optimization. The other four levels run every optimization that
`--disable-opt` does not switch off; which of them a level leaves out is each optimization's own
decision, stated on its page under [`docs/optimizations/`](../optimizations/README.md). A required
transformation, such as the specialization of Python's integers, runs at every level.

## Facts

```
fact    := path "in" "[" int "," int "]"      (Range, bounds inclusive)
         | path "!=" "null"                    (NonNull)
         | path "is" type                      (Type)
         | path "noalias" path                 (NoAlias)
path    := ident ("." ident)*                  e.g. Checksum.data.length
ident   := [A-Za-z_][A-Za-z0-9_]*
int     := ["-"] ( [0-9]+ | "0x" [0-9A-Fa-f]+ )
type    := "u8"|"u16"|"u32"|"u64"|"i8"|"i16"|"i32"|"i64"|"f32"|"f64"|"bool"
```

Whitespace between tokens is optional, identifiers are case-sensitive and integers have no bound. A
fact is normalized to single spaces around `in`, `!=`, `is` and `noalias` and to `[lo, hi]` with
decimal integers: `Sum.n in[0x1,31]` is `Sum.n in [1, 31]`.

A fact is never trusted silently: every accepted `--assume` reports the warning `PLY0009` once, in
command-line order, the manifest of `translate` lists it among its warnings and records it in its
command line. A text that contains `:` is a [caller contract](../reference/caller-contracts.md)
(`<target>: <contract>`), checked at run time or refused; `--assume-unchecked` and `--assume-file`
state contracts too.

## Strict mode

With `--strict`, every warning that `spec/diagnostics.yaml` marks `strict: true` is reported as the
error `PLY0010` instead, which carries the original code and message
(`strict mode: PLY0009 assumption 'Sum.n in [1, 31]' is trusted without proof`); the run exits with
3. Strict mode is not "warnings as errors": a warning without `strict: true` stays a warning.

## Canonical form

`GlobalOptions.ToCanonicalArguments()` writes `--opt <level>` (the default too), then `--disable-opt`
with the IDs ascending and comma-joined if any, then one `--assume "<fact>"` per fact in ordinal order
of its normalized text, then `--strict` if set. Two equivalent command lines give the same rendering:

```
polypiler translate a.cs … --disable-opt O0100,O0001 --disable-opt O0100 --opt speed
polypiler translate a.cs … --opt speed --disable-opt O0001,O0100
```

both record `--opt speed --disable-opt O0001,O0100`. The manifest of `translate` records the pipeline
options in this form after the command's own options whenever the command line gives one of them.

## Diagnostics

| Code | Exit | Message |
| --- | --- | --- |
| [`PLY0007`](../errors/PLY0007.md) | 2 | `'<text>' is not a valid assumption: <reason>`: `empty assumption`, `expected 'in', '!=', 'is' or 'noalias' after the path`, `lower bound <lo> exceeds upper bound <hi>`, `unknown type '<t>'`, `'<x>' is not an integer`, and the other places the grammar breaks |
| [`PLY0008`](../errors/PLY0008.md) | 2 | `'<id>' is not an optimization ID (expected O followed by four digits)` · `unknown optimization '<id>'` |
| [`PLY0009`](../errors/PLY0009.md) | warning, strict | `assumption '<fact>' is trusted without proof` |
| [`PLY0010`](../errors/PLY0010.md) | 3 | `strict mode: <code> <message>` |
| [`PLY0012`](../errors/PLY0012.md) | 2 | `'<level>' is not an optimization level; expected one of: none, balanced, speed, size, memory` |

An empty item of `--disable-opt` (`O0001,,O0100`, a trailing comma, `--disable-opt=`) and `--opt`
given twice are refused by the command-line foundation as `PLY0003` before any of these checks.
