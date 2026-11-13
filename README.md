# Polypiler

[![License](https://img.shields.io/github/license/Hawkynt/Polypiler)](https://github.com/Hawkynt/Polypiler/blob/main/LICENSE)
[![Language](https://img.shields.io/github/languages/top/Hawkynt/Polypiler?color=8957D5)](https://github.com/Hawkynt/Polypiler)

[![CI](https://github.com/Hawkynt/Polypiler/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/Polypiler/actions/workflows/ci.yml)
![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/Polypiler?branch=main)
![Activity](https://img.shields.io/github/commit-activity/m/Hawkynt/Polypiler)

[![Stars](https://img.shields.io/github/stars/Hawkynt/Polypiler?color=FFD700)](https://github.com/Hawkynt/Polypiler/stargazers)
[![Forks](https://img.shields.io/github/forks/Hawkynt/Polypiler?color=008080)](https://github.com/Hawkynt/Polypiler/network/members)
[![Issues](https://img.shields.io/github/issues/Hawkynt/Polypiler)](https://github.com/Hawkynt/Polypiler/issues)
![Code Size](https://img.shields.io/github/languages/code-size/Hawkynt/Polypiler?color=4CAF50)
![Repo Size](https://img.shields.io/github/repo-size/Hawkynt/Polypiler?color=FF9800)

[![Release](https://img.shields.io/github/v/release/Hawkynt/Polypiler)](https://github.com/Hawkynt/Polypiler/releases/latest)
[![Nightly](https://img.shields.io/github/v/release/Hawkynt/Polypiler?include_prereleases&sort=date&filter=nightly-*&label=nightly&color=FF9800)](https://github.com/Hawkynt/Polypiler/releases)
[![Downloads](https://img.shields.io/github/downloads/Hawkynt/Polypiler/total)](https://github.com/Hawkynt/Polypiler/releases)

> **Write or recover an algorithm in one language. Compile it into another. Preserve its semantics. Optimize for the target.** Polypiler is a cross-language optimizing compiler and transpiler that lifts algorithms from source code, bytecode or machine code into one verified intermediate representation and emits readable, tested, target-native implementations — from C#, JavaScript and Rust to PowerBASIC, LLVM IR and MOS 6502.

## 🧭 Vision

**Polypiler** is a planned, cross-language optimizing compiler and algorithm ecosystem. Its goal is to accept algorithm implementations written in supported source languages—or recovered from supported intermediate and machine-code formats—and generate readable, executable, verified implementations for other languages, runtimes, processors and operating environments.

It is designed to do more than translate syntax. Polypiler recognizes the *computation* represented by source code, type patterns, library operations, pointer arithmetic, inline assembly and machine instructions. It can reverse source-specific optimizations, reconstruct higher-level operations, then choose a different implementation suited to the destination.

**Project status: specification and initial implementation planning.** This README describes the intended product, not a finished transpiler. The commands and language combinations below are **planned interfaces and acceptance scenarios**, not claims that they already work. The one exception is a first prototype that translates a small subset of C# to JavaScript; [Quick start](#-quick-start) shows it and says where it ends. The detailed agent implementation contract is in [PRD.md](docs/PRD.md).

A correct algorithm should not need to be manually rewritten for every programming language and processor. Polypiler aims to make algorithm implementations portable through a common, semantically precise intermediate representation (IR) and a verifier-driven compiler pipeline.

The same engine should eventually support:

- **Source → source:** JavaScript Cipher implementation → C#, PowerBASIC → Rust, Kotlin deinterlacer → Perl.
- **Source → executable:** C# numeric algorithm → DOS x86-16 executable, Rust bit manipulation → MOS 6502 machine code.
- **Binary/bytecode → source:** MSIL → PHP, JVM bytecode → Delphi, x86 → C++, ARM → PowerBASIC, MOS 6502 → C#.
- **IR interchange:** LLVM `.ll` / `.bc` → Polypiler → other languages, and Polypiler → LLVM IR for compatible toolchains.
- **Algorithm distribution:** Import a tested algorithm from the catalogue and generate only its relevant implementation, runtime helpers, tests and dependencies.

Every pair is useful only when the supported language subset, source semantics, target runtime, external dependencies and ABI have been independently verified. Polypiler will report unsupported features instead of fabricating superficially plausible code.

## ✨ Features

All of the following is **planned** — nothing is advertised as working before its verification gate passes.

- **Source → source translation** between C#, JavaScript, Rust, C, C++, Kotlin, Perl, Python, PowerBASIC and more — every language planned as both input and output.
- **Binary and bytecode recovery** — MSIL, JVM, WebAssembly, LLVM IR, tokenized BASIC, x86/x64, ARM and 6502 lifted back to readable source.
- **Native executables** for constrained targets such as 8086 DOS and MOS 6502.
- **Semantic recovery and de-optimization** — popcount, min/max, rotations, bit permutations, unrolled and interleaved loops recognized regardless of how they were written.
- **Proof-driven optimization** — every rewrite backed by range/alias facts, exhaustive small-width tests or SMT bitvector proofs.
- **De-dynamicization** — JavaScript, Python and Perl specialized into typed code with guarded fallbacks.
- **Foreign libraries and ABIs preserved** — native bindings, binary-compatible UDTs and marshalling plans.
- **Rich language features lowered** — generics, classes, closures, exceptions, coroutines and async turned into dispatch tables, state machines and cleanup code for targets that lack them.
- **Verification built in** — generated code is compiled, executed and compared against independent oracles before a pair is called supported.

## 🧩 Support matrix

What works **today**, per direction. A frontend reads the language, a backend writes it; the two advance
separately, and supporting one is no evidence for the other. Every cell is backed by tests that run the
generated code — a row changes only in the pull request that makes it true.

`✅` full for the declared subset · `⚠️` partial, explained in the row · `—` not yet (planned).

### Languages

| Language | Dialect / version | Frontend | Backend | Notes |
| --- | --- | :---: | :---: | --- |
| C# | C# 14, .NET 10 | ⚠️ | — | Prototype subset: static methods over `int`, `uint`, `byte`, `bool` and one-dimensional arrays; `if`, `while`, `for`; no classes with state, strings or exceptions |
| JavaScript | ES2022 modules, Node | — | ⚠️ | Prototype subset: the constructs the C# frontend imports, with explicit 32-bit wrap-around and typed arrays |
| PowerBASIC | DOS 3.5 (`pb35`), `pb36` | — | — | |
| C | C11 / C17 / C23 | — | — | |
| C++ | C++17 / 20 / 23 | — | — | |
| Rust | 2024 edition | — | — | |
| Kotlin | JVM | — | — | |
| Perl | Perl 5 | — | — | |
| Python | 3.x | — | — | |
| TypeScript | current | — | — | |
| Java | JDK 17 / 21 | — | — | |
| Go | current | — | — | |
| Delphi / Object Pascal | Free Pascal in Delphi mode | — | — | |
| FreeBASIC | `-lang fb`, `-lang qb` | — | — | |
| PHP | 8.x | — | — | |
| Ruby | 3.x | — | — | |
| Visual Basic | VB.NET; classic VB6/VBA separately | — | — | |
| Commodore BASIC | V2 (C64); VIC-20 and C128 as profiles | — | — | |

### Bytecode, intermediate and machine-code formats

For these, the frontend *lifts* (decodes and recovers semantics) and the backend *emits* (encodes).

| Format / instruction set | Dialect / variant | Lift | Emit | Notes |
| --- | --- | :---: | :---: | --- |
| .NET MSIL | ECMA-335 assemblies | — | — | |
| JVM bytecode | class files, jars | — | — | |
| WebAssembly | core specification, binary modules | — | — | |
| LLVM IR | `.ll` text, `.bc` bitcode | — | — | |
| MOS 6502 | NMOS; 65C02 and 65C816 as variants | — | — | |
| Zilog Z80 / Intel 8080 | | — | — | |
| 8086 / x86-16 | real mode, DOS `MZ` and `COM` | — | — | |
| x86 (32-bit) / x64 | integer core, BMI/POPCNT/LZCNT | — | — | |
| ARM32 / Thumb, AArch64 | AAPCS, AAPCS64 | — | — | |
| Commodore BASIC tokenized | C64 PRG; VIC-20, C128 | — | — | |
| GW-BASIC tokenized | | — | — | |
| Microsoft BASIC p-code | per dialect, after an inventory | — | — | |

Every language and format above is planned in **both** directions. LLVM integration broadens access to
LLVM-based source frontends such as Clang; it **does not** automatically expose every GCC language
frontend. GCC GENERIC/GIMPLE requires a separate verified adapter.

## 📦 Installation

Nothing has been released yet. Once the first alpha lands, self-contained `polypiler` binaries for Windows and Linux are published on the [releases page](https://github.com/Hawkynt/Polypiler/releases) — dated releases and automatic nightlies. Until then, build from source (see [🛠️ Building](#️-building)); the only requirement is the .NET 10 SDK.

## 🚀 Quick start

One public command name is planned: `polypiler`. Today it has a single working command, the prototype below. Every other example in this section establishes the product interface and **will not run until the corresponding features have been implemented and verified.**

### Run the prototype: a small C# algorithm to a JavaScript module

```bash
# Works today, from a checkout with the .NET 10 SDK
dotnet run --project Polypiler.Cli -c Release -- translate tests/fixtures/prototype/Adler32.cs \
  --from csharp --to javascript --output ./generated

# The generated module runs as it is; this prints 300286872, the published Adler-32 of "Wikipedia"
node --input-type=module -e "import { Compute } from './generated/Adler32.mjs'; console.log(Compute(new TextEncoder().encode('Wikipedia')))"
```

This is a walking skeleton, not the product: one thin path from source to generated code, without optimization, analysis or `--verify`.

- **Input:** one C# file holding one `static class` of `static` methods over `int`, `uint`, `byte`, `bool` and one-dimensional arrays of the three integer types — arithmetic, bitwise, shift, comparison and logical operators, `?:`, casts, assignments, `++`/`--` as statements, `if`/`else`, `while`, `for`, `return` and calls within the class.
- **Output:** `<class>.mjs`, an ES module that exports every `public` method. `int` and `uint` wrap around at 32 bits as in C#, `byte[]`/`int[]`/`uint[]` become `Uint8Array`/`Int32Array`/`Uint32Array`, and a division by zero or an index outside its array throws a `RangeError`.
- **Everything else is refused, not approximated:** `long`, floating point, strings, `foreach`, `switch`, `break`, classes with state, generics, lambdas, exceptions and the rest are reported as `PLY1002` with their position and nothing is written. `uint` mixed with `int` is refused as well, because C# would compute it in 64 bits.
- **Exit codes:** 0 success, 2 usage error (`PLY0001`), 3 unsupported construct (`PLY1002`), 4 invalid C# (`PLY1000`, with the compiler's own message), 70 internal error.

The pair C# → JavaScript is `executed` on the fixtures in `tests/fixtures/prototype`, not `verified`.

### Translate an existing algorithm

```bash
# A JavaScript checksum from Cipher to a compiled C# library
polypiler translate path/to/Cipher/algorithms/checksum/example.js \
  --from javascript --to csharp --runtime net9.0 \
  --verify --output ./generated/checksum-csharp

# A PowerBASIC numeric kernel to Rust
polypiler translate path/to/example.bas \
  --from powerbasic --dialect pb35 --to rust \
  --verify --output ./generated/example-rust

# A Kotlin CPU deinterlacer to Perl
polypiler translate path/to/Deinterlacer.kt \
  --from kotlin --to perl --profile perl5 \
  --verify --output ./generated/deinterlace-perl
```

### Translate a lower-level representation

```bash
# LLVM bitcode, preserving supported ABI/effect contracts
polypiler translate kernel.bc --from llvm --to csharp \
  --verify --output ./generated/kernel-csharp

# A supported C# algorithm as an 8086 DOS executable
polypiler translate Checksum.cs --from csharp --to x86-16 \
  --os dos --cpu 8086 --verify --output ./generated/checksum-dos

# A 6502 binary routine to C# (machine profile/entry point required)
polypiler recover checksum.bin --from mos6502 --to csharp \
  --cpu mos6502 --entry 0x8000 --verify --output ./generated/checksum-csharp
```

### Inspect and verify optimizations

```bash
polypiler inspect path/to/algorithm.cs --from csharp \
  --show types,ranges,effects,patterns,ir,proofs

polypiler capabilities --from kotlin --to perl --profile perl5

polypiler verify ./generated/checksum-csharp \
  --against ./tests/fixtures/checksum-vectors.json
```

### Generate an existing catalogue algorithm

```bash
polypiler search "Floyd-Steinberg"
polypiler generate color.dithering.floyd-steinberg \
  --to rust --verify --output ./generated/floyd-steinberg
```

The catalogue and publishing workflows are later deliverables. A generated package is not automatically published to NuGet, crates.io, npm, CPAN, PyPI or any other registry.

## 📚 Documentation

Before starting work, follow the [issue-writing standard](ISSUES.md) and its
[Ready and Blocked examples](docs/examples/ISSUE-READINESS.md).

Start with the [documentation index](docs/README.md), then follow the [end-to-end compilation flow](docs/architecture/PIPELINE.md), [IR and analysis contracts](docs/architecture/IR.md), [semantic recovery](docs/architecture/SEMANTIC-RECOVERY.md), [foreign interoperability](docs/architecture/INTEROP.md), and [verification gates](docs/quality/VERIFICATION.md). The [full PRD](docs/PRD.md) remains the normative implementation plan; these pages describe the proposed design, not shipped features.

## 🔬 What makes it different

### Recover semantics before optimizing

An algorithm may already contain hand-written bit tricks, custom unrolling, interleaved computations, compiler-generated state machines, or inline assembly. Polypiler aims to recognize operations such as `PopCount`, `Min`, `Max`, multiplication, bit permutation, memory copy, matrix multiplication, loop maps/reductions and deterministic disposal regardless of the particular implementation pattern.

Recognition and instruction selection are separate. For example, the same recovered bit permutation might become PEXT/PDEP on a compatible CPU, masks and shifts on another, or an 8-bit assembly routine on a 6502. A recognized sequence is rewritten only when its equivalence preconditions and effects are proven.

### One optimization middle-end, many frontends and backends

The intended architecture is:

```mermaid
flowchart TB
  Inputs["Source, bytecode or machine code"] --> IR["Frontend and semantic lifting<br/>Typed IR with explicit effects"]
  IR --> Transform["Analyze and recover operations<br/>Prove legal rewrites"]
  Transform --> Target["Legalize target and ABI<br/>Emit source, IR or binary"]
  Target --> Verify["Compile, execute and compare"]
  Oracle["Independent source oracle"] --> Verify
  Verify --> Evidence["Manifest and support evidence"]
```

The IR must preserve source-language arithmetic, conversions, ownership, error handling, runtime identity and observable effects. Native operations and external code are neither discarded nor treated as permanently opaque if their semantics can be recovered.

### Specialize dynamic code into typed code

When an algorithm's actual values and call contracts permit it, Polypiler should recover concrete types from JavaScript, Python and Perl and replace dynamic objects, conversions and boxed collections with typed values, arrays, records, spans or buffers. Speculative fast paths require guards and valid fallbacks; only proven cases can be lowered without runtime checks.

### Support old and new machines

The target profile may describe .NET 9, .NET Framework 2.0-compatible helper code, modern x86/x64, ARM, GPU/NPU runtimes or 8086/6502-class systems. Bounds checks, integer widths, pointers, banked memory, CPU flags, stack behavior and memory-mapped I/O are part of the compilation problem—not implementation details the translator can ignore.

### Preserve foreign libraries and ABIs

Polypiler should generate native target-language bindings for recognized external calls: for example, C declarations using a DLL can become PowerBASIC `DECLARE ... LIB ... ALIAS` declarations for the same compatible Windows DLL.

It must also synthesize binary-compatible target UDTs, byte-backed records, `pack`/`unpack` marshaling, unaligned accessors and ABI bridge code when native target representations have different padding, alignment or calling conventions. A byte-compatible structure is not necessarily callable **by value** under another ABI. Unknown external calls or memory effects are preserved or explicitly diagnosed, never erased.

### Lower rich language features to constrained targets

Generics, classes, abstract classes, interfaces, multiple/diamond inheritance, anonymous types, structural records, enums, closures, overloads, operator overloads, variadic functions, type-switch and pattern-matching expressions, exceptions, `On Error Resume Next`, `using`, RAII, generators, coroutines, async/await and iterators should be represented semantically.

When the target lacks a feature, Polypiler can specialize it away or generate explicit UDTs, dispatch tables, state machines, cleanup control flow and minimal runtime helpers. Observable behavior must be preserved. A real-time DOS or 6502 target may reject capabilities its environment cannot implement within its resource constraints.

## 💡 Prior art and reference corpora

Polypiler is designed to build on, and later gradually replace duplicated compiler functionality in, existing projects. Those projects remain independently usable until verified migration gates pass.

| Project | Candidate contribution |
|---|---|
| [PB-Compiler](https://github.com/Hawkynt/PB-Compiler) | Typed SSA, range/known-bits passes, PowerBASIC semantics, LLVM emission, x86-16 backend and emulation oracles |
| [Cipher](https://github.com/Hawkynt/Hawkynt.github.io/tree/main/Cipher) | JavaScript type-aware frontend, AST/IL pipeline, language emitters, cryptographic algorithms and test vectors |
| [BitPerm](https://github.com/Hawkynt/Hawkynt.github.io/tree/main/BitPerm) | Bit-routing model, heuristic/Dijkstra synthesis, CPU cost profiles and building-block tests |
| [BitBench](https://github.com/Hawkynt/Hawkynt.github.io/tree/main/BitBench) | Bit-width, encoding, numeric and format catalogue |
| [C--FrameworkExtensions](https://github.com/Hawkynt/C--FrameworkExtensions) | Legacy and unusual numeric types, fixed point, Float80/MBF, BCD, Posit, image/color algorithms |
| [Randomizer](https://github.com/Hawkynt/Randomizer) | Deterministic RNG algorithms and independent test vectors |
| [CompressionWorkbench](https://github.com/Hawkynt/CompressionWorkbench) | Bitstream algorithms, compression codecs, tests and corpora |
| [PNGCrushCS](https://github.com/Hawkynt/PNGCrushCS) | Image format implementations and binary processing tests |
| [MediaDroyd](https://github.com/Hawkynt/MediaDroyd) | Kotlin CPU deinterlacing/motion algorithms, shaders and optional native NN integration |

The first Kotlin→Perl showcase targets the actual [MediaDroyd `Deinterlacer.kt`](https://github.com/Hawkynt/MediaDroyd/blob/main/core/player/src/main/kotlin/com/hawkynt/mediadroyd/player/Deinterlacer.kt), not a synthetic substitute. Derivative implementations must respect the file's documented FFmpeg/GStreamer licensing and attribution. NCNN-backed interpolation is initially a provider-backed **Rust adapter**; a pure Rust neural implementation additionally requires model/operator/weight conversion and appropriate licenses.

## 🗺️ Roadmap

The complete product contract, implementation phases, architecture and acceptance tests are in **[PRD.md](docs/PRD.md)**. That document is authoritative for agent work; this README is an overview and planned user experience.

The initial milestone is deliberately narrow: set up a reproducible repository and build an actual CLI that translates a pinned **Cipher JavaScript algorithm → C#** and a **PowerBASIC algorithm → C#**, compiling and executing generated code against independent known-answer vectors. Only expand the advertised language matrix when those gates pass.

At project bootstrap, the agent should:

1. Read `docs/PRD.md` and upstream `AGENTS.md` files, pin sources/toolchains, and inventory licenses and existing tests.
2. Create `Polypiler.slnx`, `.editorconfig`, `AGENTS.md`, versioned specifications, NUnit tests and CI on a feature branch.
3. Implement typed/effect-aware IR, verification, real JavaScript and PowerBASIC imports and C# emission; add runnable acceptance tests and negative diagnostics.
4. Merge only via reviewed, green PRs with linear history. Never push directly to `main`, `master` or `trunk`.

For regular compiler projects, target .NET 10 and current stable C# features; keep framework compatibility backports separate, with .NET Framework 2.0 as their compatibility baseline where applicable. The repository's C# style and testing conventions are specified in the PRD and `AGENTS.md`.

## 📈 Optimizations

Recognizing what code computes and choosing a better form for the target is the middle of the product.
Every optimization, recognition rule and lowering rewrite has an identifier (`O####`), a page with an
example before and after, the conditions under which it applies, the cases in which it must **not**
fire, and a switch to turn it off.

**[The optimization checklist](docs/optimizations/README.md)** lists all of them with their state:
0 of 146 are implemented today.

| Family | Planned |
| --- | ---: |
| [General passes](docs/optimizations/README.md#general-passes) — folding, propagation, dead code, loops, vectors | 30 |
| [De-dynamicization](docs/optimizations/README.md#de-dynamicization) — typing JavaScript, Python and Perl | 7 |
| [Legalization](docs/optimizations/README.md#legalization) — expressing what a target lacks | 10 |
| [Recognition and recovery](docs/optimizations/README.md#recognition-and-recovery) — rotates, popcounts, CRCs, de-unrolling | 57 |
| [Bit permutations](docs/optimizations/README.md#bit-permutations) | 10 |
| [Language-construct lowering and recovery](docs/optimizations/README.md#language-construct-lowering-and-recovery) — generics, classes, exceptions, coroutines | 30 |
| [Interop and layouts](docs/optimizations/README.md#interop-and-layouts) | 2 |

## 🏗️ Architecture

The planned monorepo contains these **internal components**, all under the Polypiler product name:

| Component | Responsibility |
|---|---|
| `Polypiler.Core` | IR, frontends/backends, type/range analysis, semantic recovery, optimization, legalization |
| `Polypiler.Base` | Portable semantic types, numbers, vectors, buffers, ownership, sequences and tensor operations |
| `Polypiler.Permutation` | BitPerm-derived bit mapping recovery, composition, synthesis and cost selection |
| `Polypiler.Interop` | Native bindings, foreign ABIs, layouts, UDT synthesis and marshalling plans |
| `Polypiler.Catalog` | Algorithm identities, reference vectors, verified target packages, provenance |
| `Polypiler.Adapters` | Optional NCNN and other native compute/runtime integrations |
| `Polypiler.Cli` | Translation, inspection, verification, capability reporting and catalogue commands |

The implementation is initially C# on **.NET 10**, with NUnit-based testing and a language-neutral, versioned IR specification. Older .NET and non-.NET output targets receive generated compatible code rather than depending on the compiler's own runtime.

### Semantic correctness is a release gate

Support states are reported at the granularity of source language/version, source construct, target language/version, target ABI/architecture/runtime and optimization configuration:

| State | Meaning |
|---|---|
| `planned` | Specified, not implemented |
| `experimental` | Prototype exists; full execution/equivalence gate not met |
| `compiled` | Destination compiler accepts the generated output; behavior not yet verified |
| `executed` | Output runs with test inputs; complete conformance not yet established |
| `verified` | Independent source/target oracles and required semantic/effect tests pass |
| `unsupported` / `blocked` | Not implemented or cannot be verified in the configured environment |

For optimizations, a successful pattern match is not a correctness proof. Polypiler will use proof preconditions, range and alias analysis, exhaustive small-width tests, differential testing and SMT bitvector reasoning where applicable. An unknown or timed-out proof must not silently authorize a rewrite.

## ⚠️ Limitations

- Translating an algorithm is not equivalent to translating an entire language runtime, OS, object model or native library.
- Foreign calls, inline assembly, volatile/memory-mapped I/O, arbitrary memory writes, exception semantics, reflection and runtime effects must be preserved or explicitly rejected.
- An ABI-compatible layout needs more than matching field names; padding, alignment, byte order, by-value classification, calling convention and lifetime are part of the contract.
- Some sources, especially neural implementations and model weights, have additional license restrictions. Their presence in an upstream repository does not authorize copying or redistributing them.
- Legacy hardware has concrete resource limits and potentially timing-sensitive behavior. Performance improvements are measured, never assumed.
- Untrusted source code and generated native artifacts must be tested in isolated environments with pinned toolchains and resource limits.
- Imported code, generated derivative works, model weights and native dependencies keep their own licences and require per-source provenance checks; the licences of PB-Compiler, Cipher, BitPerm, MediaDroyd and third-party libraries are not assumed to be interchangeable.

## 🛠️ Building

```bash
dotnet build Polypiler.slnx -c Release
dotnet test Polypiler.slnx -c Release                                 # everything
dotnet test Polypiler.slnx -c Release --filter "TestCategory!=Slow"   # the fast tier
```

Requires the .NET 10 SDK. [CONTRIBUTING.md](CONTRIBUTING.md) covers the test tiers, CI and releases.

## 🤝 Contributing

Contributions should start with an IR/semantic contract and a positive and negative test case, not just an emitter that produces plausible source. A supported translation requires a real target compiler/runtime, a separately executed reference oracle and documented limitations. The agent-specific workflow, branch rules, coverage targets and PR checklist are in [PRD.md](docs/PRD.md).

## ❤️ Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see [LICENSE](LICENSE).
