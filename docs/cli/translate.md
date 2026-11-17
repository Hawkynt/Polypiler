# `polypiler translate`

```
polypiler translate <input> --from <language> [--source-profile <id>]
                            --to <language>   [--target-profile <id>]
                            [--export <name>[,<name>]…] [--verify [--vectors <file>]]
                            --output <directory>
                            [--opt …] [--disable-opt …] [options of the chosen languages]
```

`--from`, `--to` and `--output` are required. `--profile` is the former name of `--target-profile`
and still accepted; giving both is `PLY0011`.

## Identifiers

Languages and profiles are matched exactly, as their registry spells them (ADR 0004, "Identifiers").
The languages are `csharp`, `javascript`, `python` and `rust`, which the command reads and writes, and
the further languages of PRD §3.1 (`c`, `cpp`, `delphi`, `freebasic`, `go`, `java`, `kotlin`, `perl`,
`php`, `ruby`, `typescript`), which it does not serve yet.

| Case | Diagnostic | Exit |
| --- | --- | --- |
| a language no registry knows (`klingon`, `CSharp`) | `PLY0006` unknown language | 2 |
| a profile the language does not have (`--target-profile es1`) | `PLY0006` unknown profile | 2 |
| a known language without a frontend or backend (`--to ruby`) | `PLY0014` | 3 |

A missing profile is the language's default: `net10.0` for C#, `es2022` for JavaScript, the newest
Python version, `rust2024` for Rust. Python also takes a version alone (`3.11` for `py3.11`).
`--source-profile` is checked and recorded; the frontends read the latest dialect of their language
whatever it names.

## Exports

Without `--export` every public function of the input is translated. With it only the named ones are
exported; the other functions stay in the module for the exported ones to call. A name the input does
not have, and an input without any public function, are `PLY0013` (exit 4). The JavaScript frontend
reads `--export` itself and reports a missing function as `PLY1104`.

## Verification

`--verify` builds, runs and compares the generated JavaScript through the cross-language runner and
the oracle framework; the other targets refuse it (`PLY0003`). `--vectors` names a vector file
(`spec/schemas/vectors.schema.json`) with an `export` and a `signature` of `bool` and integers of up to
32 bits, compared exactly. `--vectors` without `--verify` is `PLY0003`.

The steps, and the state each leaves in `polypiler.manifest.json`, which is rewritten after every step
and never ahead of it:

| Step | Failure | Exit | State |
| --- | --- | --- | --- |
| emit | — | — | `generated` |
| build: `node --check <module>` | `PLY0030` | 6 | `generated` |
| any step past a runner limit | `PLY0031`–`PLY0033` | 10 | `generated` |
| execute: a driver calls the export once per vector | the vectors it did not answer fail | 5 | `built` |
| compare with the vector file's expected values | `PLY0051` | 5 | `executed`, `verification.result: failed` |
| no vectors, none in the file, or vectors Polypiler produced | `PLY0063` | 7 | `executed` |
| every vector passed | — | 0 | `verified` |

`node` is taken from the search path (`PLY0021` when it is missing). With `--verify` the last line of
standard output is `<output>: <state>`, and once verified ` (<passed> of <total> vectors)` follows:
`out/sum32-js: verified (3 of 3 vectors)`.

## `requestedConfig`

The manifest stores the canonical command line of the request (ADR 0004, "Recorded command line"):
`translate`, the input as typed with `/`, the command's own options ordered by name with both profiles
written out, then `--opt` with its effective level and `--disable-opt`. Lists are sorted with
duplicates removed; flags are written bare. `--output`, `--explain-opt`, `--explain-types` and the
global options are left out, so every spelling of one request records one line:

```
translate tests/fixtures/cli/translate/sum32.cs --export Add --from csharp --source-profile net10.0 --target-profile es2022 --to javascript --opt default
```
