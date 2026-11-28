# Registered options

Generated from `RegisteredOptionsDocument` and `Program.CreateContributions()` in `Polypiler.Cli`.
Do not edit it by hand: a test regenerates it and fails when this file differs.

An area adds an option to a command it does not own, or an identifier to `--from` and `--to`,
by registering one contributor in `Program.CreateContributions()`
([ADR 0004](../adr/0004-cli-design.md), "Adding a command or an option later").

## Planned register

The one list of owners: where another issue names a different owner for an option, this table
decides. An issue that adds an option adds its row and states the option's kind, set or sequence,
default, allowed values, whether it is recorded and the normal spelling of its value.

| Option or identifier | Commands | Owner | Purpose |
| --- | --- | --- | --- |
| `--opt`, `--disable-opt`, `--assume`, `--strict` | every pipeline command | #179 | optimization level, switches, value facts, strict mode |
| `--assume-unchecked`, `--assume-file`; contract form of `--assume` | `translate`, `generate` | #777 | caller contracts, checked or explicitly unchecked |
| `--specialization-budget` | `translate`, `generate` | #492 | limits on specialized versions, growth and guards |
| `--manifest` | `recover` | #944 | binary manifest describing image, map and entries |
| `--mode`, `--byte-order` | `recover` | #947 | profile of a raw blob without a manifest |
| `--dependency-mode`, `--allow-unresolved` | `translate`, `recover` | #250 | how external machine dependencies are satisfied |
| `--asm-policy` | `translate`, `recover` | #911 | whether inline assembly is recovered, kept or required |
| `--verify-passes` | `translate`, `recover` | #140 | run the protected-effect checker after each pass |
| `--allow-untrusted-decoding` | `recover` | #946 | accept decoding that left the trusted subset |
| `--abi`, `--import-header` | `translate` | #319 | ABI identity and headers for foreign functions |
| `--native-provider`, `--strategy native-adapter` | `translate` | #286 | bind to a native inference provider instead of translating it |
| identifier `bitmapping` (`--from` and `--to`) | `translate` | #961 | a bit-mapping document as input or output |
| `--cpu`, `--entry`, `--load-address`, `--memory-map`, `--verify-emulator` | `recover`, `translate` | #880 | machine-code input and emulator selection |
| `--target-triple` | `translate` | #851 | target triple an `llvm` output is compiled, linked and executed for (PRD §10) |
| `--llvm-version` | `translate` | #860 | LLVM version an `llvm` input or output is held to (PRD §10: `--llvm-version pinned`) |
| `--type-report` | `translate` | #1590 | a JSON report of how statically typed the translation is: places per lattice level and certainty, the static ratio, why each variant stays one |

## Registered options

No option is contributed yet.

## Registered identifiers

No identifier is contributed yet.
