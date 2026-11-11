# Polypiler

> **Write or recover an algorithm in one language. Compile it into another. Preserve its semantics. Optimize for the target.**

**Polypiler** is a planned, cross-language optimizing compiler and algorithm ecosystem. Its goal is to accept algorithm implementations written in supported source languages—or recovered from supported intermediate and machine-code formats—and generate readable, executable, verified implementations for other languages, runtimes, processors and operating environments.

It is designed to do more than translate syntax. Polypiler recognizes the *computation* represented by source code, type patterns, library operations, pointer arithmetic, inline assembly and machine instructions. It can reverse source-specific optimizations, reconstruct higher-level operations, then choose a different implementation suited to the destination.

**Project status: specification and initial implementation planning.** This README describes the intended product, not a finished transpiler. The commands and language combinations below are **planned interfaces and acceptance scenarios**, not claims that they already work. The detailed agent implementation contract is in [PRD.md](docs/PRD.md).

## Vision

A correct algorithm should not need to be manually rewritten for every programming language and processor. Polypiler aims to make algorithm implementations portable through a common, semantically precise intermediate representation (IR) and a verifier-driven compiler pipeline.

The same engine should eventually support:

- **Source → source:** JavaScript Cipher implementation → C#, PowerBASIC → Rust, Kotlin deinterlacer → Perl.
- **Source → executable:** C# numeric algorithm → DOS x86-16 executable, Rust bit manipulation → MOS 6502 machine code.
- **Binary/bytecode → source:** MSIL → PHP, JVM bytecode → Delphi, x86 → C++, ARM → PowerBASIC, MOS 6502 → C#.
- **IR interchange:** LLVM `.ll` / `.bc` → Polypiler → other languages, and Polypiler → LLVM IR for compatible toolchains.
- **Algorithm distribution:** Import a tested algorithm from the catalogue and generate only its relevant implementation, runtime helpers, tests and dependencies.

Every pair is useful only when the supported language subset, source semantics, target runtime, external dependencies and ABI have been independently verified. Polypiler will report unsupported features instead of fabricating superficially plausible code.

## What makes it different

### Recover semantics before optimizing

An algorithm may already contain hand-written bit tricks, custom unrolling, interleaved computations, compiler-generated state machines, or inline assembly. Polypiler aims to recognize operations such as `PopCount`, `Min`, `Max`, multiplication, bit permutation, memory copy, matrix multiplication, loop maps/reductions and deterministic disposal regardless of the particular implementation pattern.

Recognition and instruction selection are separate. For example, the same recovered bit permutation might become PEXT/PDEP on a compatible CPU, masks and shifts on another, or an 8-bit assembly routine on a 6502. A recognized sequence is rewritten only when its equivalence preconditions and effects are proven.

### One optimization middle-end, many frontends and backends

The intended architecture is:

```text
Input: C# | C | C++ | JavaScript | TypeScript | Kotlin | Rust | Perl | Python | BASIC | ...
       MSIL | JVM | WebAssembly | LLVM IR | tokenized BASIC | 6502 | x86 | ARM | ...
                                 |
                       Frontend + semantic lifting
                                 |
                   Typed/effect-aware canonical Polypiler IR
                                 |
              Type inference | values/ranges | pointer provenance
              Call effects | object model | iteration | vectors/tensors
              Algorithm recovery | de-optimization | bit permutations
                                 |
                   Proof-driven optimization and legalization
                                 |
Output: C# | C | C++ | JavaScript | Rust | Perl | Python | Kotlin | PowerBASIC | ...
        LLVM IR | MSIL | JVM | WebAssembly | 6502 | x86-16/DOS | ...
                                 |
                     Actual compile/run/differential tests
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

## Planned language support

**Every language listed here is intended to become both a frontend and backend**, but the implementations and verification maturity will advance separately. Supporting a backend is not evidence that its frontend works, and vice versa.

| Source/target family | Initial emphasis | Planned maturity |
|---|---|---|
| C# | Roslyn, numeric algorithms, arrays, classes; native source emission | First usable alpha |
| JavaScript | Existing Cipher AST/type-aware transpiler, bitwise and typed-array algorithms | First usable alpha |
| PowerBASIC DOS 3.5 / experimental PB36 | Existing PB-Compiler frontend, native x86-16 and BASIC output | First usable alpha; DOS output separately gated |
| C, Rust | Numeric, memory and collection algorithms | Early alpha/beta |
| C++, Kotlin, Perl, Python | C++ numeric and native kernels; Kotlin image processing; Perl/Python typed algorithm subsets | Requested workflow beta |
| TypeScript, Java, Go, Delphi, FreeBASIC, PHP, Ruby | Semantic source importer and native target emitter per language | Later beta |
| Commodore BASIC and other vintage dialects | Tokenized BASIC, source recovery, constrained target generation | Extended support |

Additional input/output representations are planned independently: **LLVM IR, MSIL, JVM bytecode, WebAssembly, C64 PRG/tokenized BASIC, Microsoft BASIC p-code variants, MOS 6502/65C02, 8086/x86/x64, ARM32 and AArch64**. Broader CPU families such as Z80, 65C816 and 68000 are candidates for later adapters.

LLVM integration broadens access to LLVM-based source frontends such as Clang; it **does not** automatically expose every GCC language frontend. GCC GENERIC/GIMPLE requires a separate verified adapter.

## Planned CLI

The examples establish the product interface for the agent. **They will not run until the corresponding features have been implemented and verified.** One public command name is planned: `polypiler`.

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

## Architecture

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

The implementation is initially C# on **.NET 9**, with NUnit-based testing and a language-neutral, versioned IR specification. Older .NET and non-.NET output targets receive generated compatible code rather than depending on the compiler's own runtime.

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

## Existing projects and reference corpora

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

## Development and agent handoff

The complete product contract, implementation phases, architecture and acceptance tests are in **[PRD.md](docs/PRD.md)**. That document is authoritative for agent work; this README is an overview and planned user experience.

The initial milestone is deliberately narrow: set up a reproducible repository and build an actual CLI that translates a pinned **Cipher JavaScript algorithm → C#** and a **PowerBASIC algorithm → C#**, compiling and executing generated code against independent known-answer vectors. Only expand the advertised language matrix when those gates pass.

At project bootstrap, the agent should:

1. Read `docs/PRD.md` and upstream `AGENTS.md` files, pin sources/toolchains, and inventory licenses and existing tests.
2. Create `Polypiler.slnx`, `.editorconfig`, `AGENTS.md`, versioned specifications, NUnit tests and CI on a feature branch.
3. Implement typed/effect-aware IR, verification, real JavaScript and PowerBASIC imports and C# emission; add runnable acceptance tests and negative diagnostics.
4. Merge only via reviewed, green PRs with linear history. Never push directly to `main`, `master` or `trunk`.

For regular compiler projects, target .NET 9 and current stable C# features; keep framework compatibility backports separate, with .NET Framework 2.0 as their compatibility baseline where applicable. The repository's C# style and testing conventions are specified in the PRD and `AGENTS.md`.

## Scope, limitations and safety

- Translating an algorithm is not equivalent to translating an entire language runtime, OS, object model or native library.
- Foreign calls, inline assembly, volatile/memory-mapped I/O, arbitrary memory writes, exception semantics, reflection and runtime effects must be preserved or explicitly rejected.
- An ABI-compatible layout needs more than matching field names; padding, alignment, byte order, by-value classification, calling convention and lifetime are part of the contract.
- Some sources, especially neural implementations and model weights, have additional license restrictions. Their presence in an upstream repository does not authorize copying or redistributing them.
- Legacy hardware has concrete resource limits and potentially timing-sensitive behavior. Performance improvements are measured, never assumed.
- Untrusted source code and generated native artifacts must be tested in isolated environments with pinned toolchains and resource limits.

## Contributing

Contributions should start with an IR/semantic contract and a positive and negative test case, not just an emitter that produces plausible source. A supported translation requires a real target compiler/runtime, a separately executed reference oracle and documented limitations. The agent-specific workflow, branch rules, coverage targets and PR checklist are in [PRD.md](docs/PRD.md).

## License

**Repository license: to be selected before publishing implementation code.** The agent must not silently choose a license or assume that the licenses of PB-Compiler, Cipher, BitPerm, MediaDroyd and third-party libraries are interchangeable. Imported code, generated derivative works, model weights and native dependencies require per-source provenance and licensing checks.
