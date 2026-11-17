# polypiler verify

```text
polypiler verify <directory> [--against <vectors>] [--no-update] [--evidence <directory>]
```

`verify` re-establishes, from a generated directory and its `polypiler.manifest.json` alone, whether
the output still builds, runs and matches independent test vectors ([PRD §1.2](../prd/01-product-and-targets.md#s1-2),
[§10](../prd/04-roadmap-and-delivery.md#s10)). It is the only command that raises a manifest from `generated` to `verified` after the fact, and
the one that lowers it again when the output no longer passes.

| Option | Meaning |
| --- | --- |
| `--against <vectors>` | The vector file to compare with. Without it, the vector file the manifest recorded in `verification.oracle` the last time it was verified, relative to the directory. |
| `--no-update` | Leave the manifest as it is: the directory is not written at all. |
| `--evidence <directory>` | Write an evidence bundle ([PRD §6.6](../prd/03-behavior-and-testing.md#s6-6)) into `<directory>/polypiler-verify/<name of the generated directory>/`. |

## What it does

The steps run in this order, and the first error ends the run:

1. **Manifest.** `<directory>/polypiler.manifest.json` is read and checked against
   [`spec/schemas/manifest.schema.json`](../../spec/schemas/manifest.schema.json) and the state rule.
   No manifest is `PLY0016`; one that is not valid, or lists no output, is `PLY0053`; one that claims
   more than it records is `PLY0054`.
2. **Hashes.** The SHA-256 of every output the manifest lists is computed again. A file that is missing
   or differs from the recorded hash is `PLY0054`: the artefact on disk is not the one that was
   generated, and nothing is built or run. Files the manifest does not list are ignored.
3. **Vectors.** The vector file is one of the oracle framework (`*.vectors.json`, schema 1). One that
   does not exist is `PLY0004`, one that is not a vector file `PLY0017`. No vector file anywhere, one
   without vectors, and one whose values Polypiler produced (`generatedBy: polypiler`, or a file inside
   a generated directory) are `PLY0063`: there is no independent oracle.
4. **Build.** Every JavaScript source output (`.js`, `.mjs`, `.cjs` with the role `source`) is checked
   with `node --check` on the toolchain the manifest's `target.runtime` names, found on `PATH`.
   A missing toolchain is `PLY0021` and leaves the manifest as it was; a check that fails is `PLY0030`.
5. **Execute.** The vectors run through the cross-language runner under its limits (`PLY0031` to
   `PLY0033`). A vector file with `export` and `signature` calls that function of the first source
   output once per vector: the input image is decoded under the signature and passed as JavaScript
   values, and the result is encoded into the output image again. A vector file without them runs the
   first source output as a program that reads the input as hexadecimal text on standard input and
   prints its output the same way.
6. **Compare.** The outputs are compared with the oracle of the vector file under its exactness
   policy (`PLY0050` to `PLY0052`).

Standard output holds one line per vector compared and a summary for every run that got to the build:

```text
  empty: pass
  a: pass
  abc: FAIL expected 0x024D0128 actual 0x024D0127
generated/adler32: executed (2 of 3 vectors)
```

Integers are shown as `0x` and their digits at the width of their type; a call that threw shows as
`trap other:<constructor name>`.

## The manifest afterwards

Unless `--no-update` is given, the manifest's `build`, `execution`, `verification` and the toolchain
that ran are rewritten to what this run found, and its `state` follows from them — downwards too: a
manifest that said `verified` and now fails one vector says `executed`, with the failed vector counted
in `verification.failed`. `verification.oracle` records the vector file relative to the directory and
`verification.producer` the `generatedBy` of its provenance. A run that stops before the build leaves
the manifest as it was. The generated files and the vector file are never written.

## Calling a JavaScript function

The parameters of the signature must be `in` parameters, and every parameter and the result of one of
the types an integer (`u8` to `u64`, `i8` to `i64`), `bool`, `f32`, `f64`, `bytes`, `utf8` or
`utf16`; another signature is `PLY0017`. Integers of up to 32 bits are numbers, 64-bit integers
`BigInt`s, `bytes` a `Uint8Array`. A result that does not fit the declared type is the trap
`result-type`. A vector that expects a trap has no value to compare with and makes the oracle fail
(`PLY0050`).

## Exit codes

| Code | When |
| ---: | --- |
| 0 | every vector passed |
| 1 | no manifest, an invalid manifest, an edited output, a missing file or toolchain |
| 2 | the command line or the vector file is not valid |
| 5 | an output differs from the vectors |
| 6 | the build failed, or the module could not be run |
| 7 | there is no independent oracle |
| 10 | a time, memory or output limit was exceeded |

## Not yet

Only JavaScript output is run; any other target language is refused with `PLY0003`. The source
program is not run as a live oracle, performance is not reported, and there is no emulator choice yet.
