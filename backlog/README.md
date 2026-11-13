# Backlog

The planned work for Polypiler, one Markdown file per issue, written to the standard in
[`ISSUES.md`](../ISSUES.md). The files are the source; `tooling/backlog/publish.py` turns them into
GitHub issues, and `tooling/backlog/lint.py` checks them first.

```bash
python3 tooling/backlog/lint.py                # check every file
python3 tooling/backlog/publish.py             # dry run: what would be created
python3 tooling/backlog/publish.py --apply     # create/update the issues, link sub-issues
```

Publishing is idempotent: `backlog/.published.json` records which file became which issue number.
The issue files themselves are not versioned — GitHub is where they live once published; this
directory is the publisher's working copy.

What the publisher adds to every issue, so the files do not have to repeat it:

- on top, **Part of** (the parent epic) and **Blocked by** (every `depends` entry as `#number`);
- the same dependencies as GitHub's native *blocked by* relation, and the parent as a sub-issue link;
- at the end, the `## Readiness review` section the templates require, derived from `state:` and
  `depends:`;
- the label: `epic` for epics; `state:draft` for drafts; otherwise `state:blocked` while any
  dependency is open and `state:ready` when none is. The `unblock` workflow keeps that label current
  as issues close.

## File format

`backlog/<area>/<ID>.md` — the file name is the ID.

```markdown
---
id: FE-KT-ARRAYS
title: "frontend(kotlin): import IntArray/LongArray, indexing and array loops"
template: 2-frontend            # which skeleton in .github/ISSUE_TEMPLATE the body follows
labels: [enhancement, area:frontend]
milestone: P3                   # P0 … P6
state: ready                    # ready | draft
parent: FE-KT                   # the epic this is a sub-issue of; omit for top-level issues
depends: [IR-MEMORY, RUN-KT]    # IDs that must be closed first; [] if none
---

## Why
…
```

- The body is the template's body with every placeholder replaced. No `<…>`, no `TBD`.
- Refer to other issues as `[[ID]]`; the publisher rewrites that to `#<number>`. In the
  *Preconditions* section write `**Depends on:** [[IR-MEMORY]], [[RUN-KT]]` — the same IDs as the
  front matter.
- `state: ready` means the Definition of Ready is met and its checklist is ticked (`- [x]`).
  `state: draft` is allowed only when a real unknown remains; the body then has a
  `## Blocking questions` section naming each unknown and the spike issue `[[ID]]` that resolves it,
  and the checklist is left unticked where it is not met.
- Epics (`template: 1-epic`) list their sub-issues as `- [ ] [[ID]]` under `## Sub-issues`; every
  listed ID has `parent:` pointing back.
- Labels: one of `enhancement` / `bug` / `documentation`, plus every `area:*` that applies. The
  `state:*` label is derived from `state:`. Never write `state:*` or `in-flight` into `labels`.

## Shared conventions

So that five hundred issues describe one product:

**Projects and paths.** Project folders sit at the repository root, named as in PRD §5.4 without the
`src/` prefix: `Polypiler.Core.IR/`, `Polypiler.Core.Types/`, `Polypiler.Core.Analysis/`,
`Polypiler.Core.Recognize/`, `Polypiler.Core.Recovery/`, `Polypiler.Core.Optimize/`,
`Polypiler.Core.Legalize/`, `Polypiler.Core.MachineSemantics/`, `Polypiler.Core.BinaryFormats/`,
`Polypiler.Core.Frontends.<Name>/`, `Polypiler.Core.Backends.<Name>/`, `Polypiler.Core.Verification/`,
`Polypiler.Permutation/`, `Polypiler.Interop/`, `Polypiler.Base/`, `Polypiler.Catalog/`,
`Polypiler.Adapters.<Name>/`, `Polypiler.Cli/`. Unit tests: `Polypiler.Tests/<Area>/…`. Tests that
invoke a real toolchain: `Polypiler.Tests.Acceptance/`, category `Acceptance` plus the slow-tier
category that applies (`ExternalInterop`, `PolyglotInterop`, `EndToEnd`, `Performance`). Feature
files: `tests/features/<area>/<ID lower-case>.feature`. Fixtures: `tests/fixtures/<area>/`.
Specifications: `spec/<area>/…`. Optimization documents: `docs/optimizations/O####-<slug>.md`. ADRs:
`docs/adr/<nnnn>-<slug>.md`.

**Code rules.** C# on .NET 10, no global usings, NUnit with `Assert.That`, K&R braces, 2-space
indent ([`AGENTS.md`](../AGENTS.md)). Gherkin is bound with Reqnroll (issue `INFRA-BDD`).

**IR notation.** Until `IR-SERIAL` fixes the debugging form, IR in issues is written one instruction
per line as `%result = Op.type operand, operand`, types `u8 u16 u32 u64 i8 … f32 f64`, blocks as
`label:`, intrinsics in PascalCase (`RotateLeft.u32 %x, %n`). Shifts state their amount contract:
`Shl.u32[mask]` (amount taken modulo the width) or `Shl.u32[strict]` (amount ≥ width is a verifier
error unless a range fact excludes it).

**Diagnostics.** Stable codes `PLY####`, registered in `spec/diagnostics.yaml` (issue
`ADR-DIAGNOSTICS`). Each area allocates from its own block:

| Block | Area | Block | Area |
| --- | --- | --- | --- |
| 0001–0099 | CLI, infrastructure, verification | 0600–0699 | Interop |
| 0100–0199 | IR, verifier | 0700–0799 | Object model |
| 0200–0299 | Types, intrinsics | 0800–0899 | Permutation |
| 0300–0399 | Analysis, optimization | 0900–0999 | Catalogue, packaging, adapters |
| 0400–0499 | Recognition, recovery | 1000–2999 | Languages, 100 per language |
| 0500–0599 | Legalization, de-dynamicization | 3000–3999 | Formats and CPUs, 100 per family |

Language blocks: C# 1000, JavaScript 1100, PowerBASIC 1200, C 1300, C++ 1400, Rust 1500, Kotlin 1600,
Perl 1700, Python 1800, TypeScript 1900, Java 2000, Go 2100, Delphi 2200, FreeBASIC 2300, PHP 2400,
Ruby 2500, Visual Basic 2600, Commodore/vintage BASIC source 2700. Inside a block: frontend `x00–x49`,
backend `x50–x89`, runner `x90–x99`. Format blocks: MSIL 3000, JVM 3100, WebAssembly 3200, LLVM 3300,
65xx 3400, Z80/8080/68000 3500, x86-16/DOS 3600, x86/x64 3700, ARM 3800, tokenized BASIC and p-code
3900. Machine and effect semantics (`MACH-`, `ASM-`, binary containers) 4000–4099.

**Optimization IDs.** General passes `O0001–O0059`, de-dynamicization and legalization `O0060–O0099`,
recognition rules `O0100–O0169`, recovery/de-optimization `O0170–O0199`, bit permutations
`O0200–O0229`, object-model lowering and recovery `O0230–O0259`, interop/layout `O0260–O0279`,
vector/tensor/execution `O0280–O0299`. Each is switched off with `--disable-opt O####`.

**Toolchains.** Named by key; exact versions live only in `tooling/toolchains.lock` (issue
`INFRA-TOOLCHAINS`). Keys in use: `dotnet-sdk`, `node`, `typescript`, `rust`, `gcc`, `clang`, `llvm`,
`jdk`, `kotlinc`, `perl`, `python`, `php`, `ruby`, `go`, `fpc` (Free Pascal in Delphi mode — the CI
stand-in; a genuine Delphi compiler is a separately reported oracle), `fbc`, `pb-compiler`
(`Hawkynt/PB-Compiler`, also the PowerBASIC oracle), `z3`, `wasmtime`, `wabt`, `qemu-user`, `vice`,
`dosbox-x`, `ilspy-cli`/`ilverify`, `nasm`. An issue that needs another adds the key in its scope.

**Pinned upstream sources.** All LGPL-3.0-or-later unless a file says otherwise; third-party notices
inside them apply per file. **Exception: `Cipher/` in `Hawkynt/Hawkynt.github.io` carries its own
`Cipher/LICENSE` with the GPL-3.0 text** — until that is changed upstream, Cipher code is rung 2 of the
sourcing ladder: run it out of process or read it as reference, derive specification and tests, copy
nothing. Its test vectors are facts and may be used.

| Repository | Pin | Local checkout for browsing |
| --- | --- | --- |
| `Hawkynt/PB-Compiler` | `0d73acbea185a2597357d53f7ce9acb44758ba76` | `../PB-Compiler.Hawkynt` |
| `Hawkynt/Hawkynt.github.io` (Cipher, BitPerm, BitBench) | `045397b4c934487bc2eb385125fdbd9589502f7f` | `../Hawkynt.github.io` |
| `Hawkynt/MediaDroid` (MediaDroyd) | `7f8b6b995cbe54a6afb290a900748393bf01e272` | `../MediaDroyd.Hawkynt` |
| `Hawkynt/CompressionWorkbench` | `21e609396e8fceb80e49185cc34cab7bce7f6483` | `../CompressionWorkbench.Hawkynt` |
| `Hawkynt/PNGCrushCS` | `9d8117695e12457bf0c1158d1fc6ebccc53acc45` | `../PNGCrushCS.Hawkynt` (`git show <pin>:<path>`) |
| `Hawkynt/Randomizer` | `656a1560350f1551de65c1e4bab179e3b048e282` | — |
| `Hawkynt/C--FrameworkExtensions` | `be3e13cbbe28dd54cc973c64b84cd71e1147f95f` | — |

A fixture is written `Hawkynt/PB-Compiler@0d73acbea185a2597357d53f7ce9acb44758ba76:path/to/file`, and
the path must exist at that pin. Fixtures an issue creates itself are written
`tests/fixtures/<area>/<file>` and marked "created by this issue".

**Thresholds.** Line and branch coverage ≥ 95 % each (ISSUES.md §7). Acceptance scenarios 100 %.
Differential mismatches 0. Performance numbers are `record only, no gate` until the budgets ADR
(`ADR-PERF-BUDGETS`) sets them; an issue never invents a performance threshold.

## Areas, ID prefixes and anchor issues

Anchors are issues other areas depend on. Their IDs are fixed; the owning area writes them.

| Directory | Prefixes | Anchors |
| --- | --- | --- |
| `infra/` | `INFRA-`, `SPEC-`, `ADR-`, `CLI-`, `VER-` | `VER-ROUNDTRIP`, `ADR-FLOAT-TOLERANCE`, `CLI-FOUNDATION`, `INFRA-TOOLCHAINS`, `INFRA-CORPUS`, `INFRA-RUNNER`, `INFRA-BDD`, `INFRA-BENCH`, `INFRA-FUZZ`, `SPEC-SCHEMAS`, `SPEC-CAPABILITIES`, `ADR-DIAGNOSTICS`, `ADR-PERF-BUDGETS`, `CLI-TRANSLATE`, `CLI-RECOVER`, `CLI-INSPECT`, `CLI-VERIFY`, `CLI-CAPABILITIES`, `CLI-SEARCH`, `CLI-GENERATE`, `CLI-PACKAGE`, `VER-ORACLE`, `VER-MANIFEST`, `VER-SMT`, `VER-EXHAUSTIVE` |
| `ir/` | `IR-`, `TYPE-`, `BASE-` | `IR-SSA`, `IR-VERIFIER`, `IR-SERIAL`, `IR-INTERP`, `IR-SOURCEMAP`, `IR-STAGES`, `IR-INT`, `IR-FLOAT`, `IR-MEMORY`, `IR-POINTER`, `IR-AGGREGATE`, `IR-EFFECTS`, `IR-EXCEPT`, `IR-ITERATION`, `IR-VECTOR`, `IR-TENSOR`, `IR-KERNEL`, `IR-ROLES`, `IR-SOURCECALL`, `IR-DYNAMIC`, `IR-OVERFLOW`, `IR-LIST`, `IR-ENUM`, `IR-POLYITER`, `IR-BIGINT`, `IR-MAP`, `IR-OPTIONAL`, `IR-RESOURCE`, `TYPE-REGISTRY`, `TYPE-EXTENDED80`, `TYPE-MBF` |
| `intrinsics/` | `INT-`, `ANA-` | `INT-REGISTRY`, `INT-BITCOUNT`, `INT-ROTATE`, `INT-BITFIELD`, `INT-PERMUTE`, `INT-BOOLEAN`, `INT-ENDIAN`, `INT-SATURATE`, `INT-CARRY`, `INT-DIVMOD`, `INT-MINMAX`, `INT-FMA`, `INT-ROUND`, `INT-EXPLOG`, `INT-TRIG`, `INT-HYPERBOLIC`, `INT-ROOTS`, `INT-COPYFILL`, `INT-SEQUENCE`, `INT-MATRIX`, `ANA-FACADE`, `ANA-RANGE`, `ANA-KNOWNBITS`, `ANA-ALIAS`, `ANA-EFFECTS`, `ANA-PROVENANCE` |
| `optimize/` | `OPT-`, `DYN-`, `LEG-` | `OPT-FRAMEWORK`, `OPT-COST`, `LEG-FRAMEWORK`, `LEG-STRUCTURE`, `LEG-PARAMFACTS`, `LEG-BASEOPS`, `LEG-HELPERS`, `LEG-TRAPS`, `DYN-INFER` |
| `recognize/` | `REC-` | `REC-ENGINE` |
| `lang/<code>/` | `LANG-<C>`, `FE-<C>…`, `BE-<C>…`, `RUN-<C>` | see below |
| `formats/` | `FMT-` | `FMT-MSIL`, `FMT-JVM`, `FMT-WASM`, `FMT-LLVM` |
| `retro/` | `ISA-`, `FMT-`, `DOS-` | `ISA-6502`, `ISA-65C02`, `ISA-65C816`, `ISA-Z80`, `ISA-8080`, `FMT-CBMBASIC`, `FMT-GWBASIC`, `FMT-PCODE`, `ISA-X86-16` |
| `native/` | `MACH-`, `ISA-`, `ASM-` | `MACH-IR`, `MACH-OPAQUE`, `MACH-INTERP`, `MACH-APIREG`, `MACH-TRACE`, `MACH-REGIONS`, `MACH-LEGALIZE`, `ASM-IMPORT`, `ISA-X86`, `ISA-X64`, `ISA-ARM32`, `ISA-A64` |
| `permutation/` | `PERM-` | `PERM-MODEL` |
| `interop/` | `IOP-` | `IOP-SPEC`, `IOP-LAYOUT` |
| `objects/` | `OBJ-`, `PROF-` | `OBJ-CONTRACTS`, `PROF-FRAMEWORK` |
| `e2e/` | `E2E-` | — |
| `catalog/` | `CAT-`, `PKG-`, `ADP-`, `UP-` | `CAT-MODEL`, `PKG-COMPOSER` |

**Languages.** Codes: `CS`, `JS`, `PB`, `C`, `CPP`, `RS`, `KT`, `PL`, `PY`, `TS`, `JAVA`, `GO`,
`DELPHI`, `FB`, `PHP`, `RB`, `VB`, `CBM`. Every language has exactly this tree:

```
LANG-<C>                 epic: the language as source and target
├── RUN-<C>              toolchain runner + CI job (compile, execute, compare)
├── FE-<C>               epic: frontend
│   ├── FE-<C>-SCALARS   literals, numeric types, arithmetic, conversions, coercion
│   ├── FE-<C>-CONTROL   branches, loops, switch/match, jumps, evaluation order
│   ├── FE-<C>-MEMORY    arrays, strings/bytes, slices/spans, pointers/references
│   ├── FE-<C>-FUNCTIONS functions, parameters, modules/namespaces, closures
│   └── FE-<C>-TYPES     records/structs/classes, enums, generics, error handling
└── BE-<C>               epic: backend, with the same five families BE-<C>-…
```

Language-independent lowering of rich constructs (generics, inheritance, coroutines, …) belongs to
`objects/`; a language's `-TYPES` issue imports or emits what its first verified subset (PRD §3.1)
names and depends on the `OBJ-` issue for anything beyond it.
