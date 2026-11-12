# PRD part 01 — Product and targets

**Status:** original PRD requirements; planned capabilities are not implementation claims.
Original section numbering is retained.

[PRD index](../PRD.md) · [Next: Core architecture](02-core-architecture.md)

**Architecture guides:** [Targets and profiles](../architecture/TARGETS.md) · [Worked translations](../architecture/WALKTHROUGHS.md)

<!-- PRD content begins -->
<a id="s1"></a>
## 1. Executive product definition

<a id="s1-1"></a>
### 1.1 Product boundaries

**Polypiler.Core** is a compiler SDK with pluggable *source-language, tokenized-source, bytecode and machine-code frontends*, explicitly typed and effect-aware *semantic intermediate representations*, a *shared optimizing middle-end*, *representation legalization*, and pluggable *target-language, assembly, bytecode and machine-code backends*. It recognizes algorithms expressed in different syntaxes, proves and preserves source semantics, removes unnecessarily dynamic runtime machinery when safe, performs range/memory/dataflow optimizations, and emits target-native code plus tests and provenance.

**Polypiler.Base** is the minimal, language-neutral semantic and datatype foundation: integer/floating representations, bit operations, fixed/floating/decimal/posit formats, containers, sequence operations, pointers/views, memory ownership, logical vectors, matrices, tensors, and portable helpers. This is **not** a clone of .NET's BCL.

**Polypiler.Catalog** is the algorithm catalogue, provenance/verification store and generated package ecosystem; it hosts curated algorithms from existing repositories, preferably referencing upstream source as the maintained implementation until controlled migration. Algorithm sources may originate in any frontend language: C# is the first mature frontend, **not** the mandated canonical source language.

**Polypiler.Adapters** contains optional bridges to native libraries and compute runtimes such as NCNN/ONNX/OpenVINO/CoreML/QNN, GPUs and platform libraries. An adapter is not automatically portable algorithm source. Model definitions, weights and licenses are separately tracked.

<a id="s1-2"></a>
### 1.2 User jobs

1. `translate`: Take a file/project/function in language X; produce a package/module/source in language Y with runnable tests.
2. `generate`: Find an existing algorithm by catalogue ID; choose target, runtime, optimizations, output form and dependencies.
3. `inspect`: Explain inferred types, detected patterns, narrowed ranges, eliminated allocations/checks and selected representations.
4. `verify`: Differentially execute source and generated targets; report bit-exact or tolerance-bounded results, unsupported cases and performance.
5. `package`: Emit minimal dependencies and idiomatic exports for NuGet/Cargo/npm/CPAN/PyPI/Maven/etc.; never publish externally without explicit authorization.
6. `integrate`: Replace PB-Compiler, Cipher and BitPerm's duplicated compiler functionality **incrementally** after proven parity; keep their distinct applications and interfaces.

<a id="s1-3"></a>
### 1.3 Primary end-to-end demonstrations

| ID | Input | Requested output | Non-negotiable outcome |
|---|---|---|---|
| E2E-JS-CS | `Cipher/` JavaScript cipher/checksum/hash | C# class or .NET library | Compiles under .NET 9, matches reference vectors, preserves `ToInt32`/`ToUint32`, bytes and rotation semantics, only required dependencies |
| E2E-PB-CS | PB-Compiler's PowerBASIC source fixture/algorithm | C# class/library | Runs and matches PB source/differential oracle, including integer widths and defined dialect behavior |
| E2E-CS-RS | RNG from `Randomizer` in C# | Rust crate | Compiles with `cargo test`, deterministic vectors match |
| E2E-KT-PL | Self-contained CPU deinterlacing kernel extracted from MediaDroyd Kotlin | Perl module | Passes reference image/frame fixtures including boundary policy; optimized typed loops and no unnecessary object dispatch |
| E2E-NCNN-RS | NCNN-backed motion-interpolation component | Rust integration | **First** produce licensed Rust bindings/adapter using the native provider and executable integration tests; pure Rust inference requires separately scoped model/operator/weight conversion and numerical validation; unsupported paths fail explicitly |
| E2E-JS-PB | A constrained Cipher bitwise/checksum function | PowerBASIC module | Compiles and runs with PB-Compiler, integer/bit behavior matches source |
| E2E-PL-CS | A restricted typed Perl numeric algorithm | C# class | Demonstrates Perl as a real frontend, not just a generated-target backend |
| E2E-PY-RS | Typed Python numerical/sequence algorithm | Rust crate | Demonstrates proven or contract-assisted de-dynamicization |
| E2E-CPP-JS | Standalone C++ numeric kernel (not a native dependency wrapper) | JavaScript module | Compatible numeric/memory semantics and executed JS fixtures |
| E2E-BITPERM | Real `BitPerm/index.html` mappings/solver cases | C#/Rust/6502/PowerBASIC | Recover mapping from code, synthesize target implementation, prove equivalence, execute in real runtime or emulator |
| E2E-MIXASM | PB-Compiler mixed BASIC+inline x86 ASM fixture | C# and native x86 | Recover understood instructions; preserve opaque effects, correct runtime ABI or report unsupported target |
| E2E-C64-CS | Tokenized C64 BASIC V2 PRG | C# module | Parse valid PRG line links/tokens and preserve observable BASIC semantics in verified subset |
| E2E-CS-C64 | Simple C# numeric kernel | C64 BASIC V2 PRG | Produce valid tokenized PRG, run in emulator, compare output and side effects |
| E2E-6502-CS | MOS 6502 checksum routine in assembled binary or listing | C# | Lift ADC/flags/memory/control flow; differential test against 6502 emulator |
| E2E-RS-6502 | Rust checksum/bit-permutation kernel | MOS 6502 assembled program | Register/zero-page allocation and runtime/emulator tests, explicit resource ceilings |
| E2E-MSIL-PHP | Compiled C# checksum in MSIL | PHP module | Typed stack-to-SSA, recognized BCL intrinsics, PHP runtime tests |
| E2E-JVM-DELPHI | Java bytecode numeric or array kernel | Delphi unit | JVM instruction/exception semantics, API map, compile and run on Delphi toolchain |
| E2E-X86-CPP | x86 machine-code bit-manipulation routine | C++ | Register/flag SSA, idiom recognition, executable equivalence |
| E2E-X64-PY | x64 array transformation routine | Python module | Recover fixed-width semantics, memory effects, verify against native executable |
| E2E-ARM-PB | ARM32/AArch64 freestanding pure checksum kernel | PowerBASIC source | Architecture-specific flags/ABI lifted; PB runtime and ARM emulator match |
| E2E-WASM-RS | WASM numeric kernel | Rust | Typed WASM memory/trap semantics preserved and verified |

**The direction is not restricted to high-level source.** The long-term system additionally targets `ARM→PowerBASIC`, `x86→C++`, `x64→Python`, `MSIL→PHP`, `JVM→Delphi`, `6502→C#`, `Rust→6502`, C64 tokenized BASIC round trips, recognized mixed inline assembly, and canonical bit-permutation recovery/re-synthesis. These must be phased and tested as explicitly supported subsets.

**No fabricated success:** If a listed input is not isolatable, legal to port, or within the currently supported subset, record a precise blocker, create a smallest representative fixture from the same repository and keep the original demonstration as an open release gate. Never silently replace the requested NCNN motion-interpolation use case with a toy convolution. The starter repository must be usable after each merged milestone, even if its supported language subset is initially small.

<a id="s1-4"></a>
### 1.4 Non-goals for first general-availability release

- Arbitrary whole-program translation of every feature of every language (reflection, metaprogramming, FFI, undefined behavior, arbitrary monkey patching, classloader features, all async models).
- Guaranteed faster output than every human-written implementation, or universal bitwise equality of transcendental math across unrelated runtimes.
- Automatic legal redistribution of third-party neural weights, libraries, shader code or algorithms.
- Treating GPU/NPU support as available on legacy PCs or C64-class machines.
- Emitting plausible-looking compilable code that silently changes source behavior.

---

<a id="s2"></a>
## 2. Research baseline and source assets (verify when work begins)

The following assets were inspected in GitHub repository documentation/directories. They are **candidate reuse**, not proof of build health or legal compatibility. The agent must clone specific commits, inspect complete files and run existing tests before integrating anything.

| Source | Observed concrete assets | First use |
|---|---|---|
| [`PB-Compiler`](https://github.com/Hawkynt/PB-Compiler) | `PowerBasic.Compiler/Ir/` typed SSA, `IrRepresentationStage.cs`, verifier, lowering, passes, LLVM/C/x86-16-related backends; `Ir/Analysis/IrRangeAnalysis.cs`, `IrKnownBitsAnalysis.cs`, `IrValueFacts.cs`; [`docs/IR.md`](https://github.com/Hawkynt/PB-Compiler/blob/main/docs/IR.md) | SSA/memory/range contract, PB frontend and source oracle, backend/test reuse |
| PB-Compiler | [`O0016`](https://github.com/Hawkynt/PB-Compiler/blob/main/docs/optimizations/O0016-value-fact-analysis.md) documents interval+known bits; congruence/reduced product not yet in current IR; [`O0323`](https://github.com/Hawkynt/PB-Compiler/blob/main/docs/optimizations/O0323-structure-packing-by-range.md) implements proven packing of private fixed-size records | Implement analysis gaps, representation selection and negative safety tests |
| PB-Compiler numerics | `Extended80.cs`, `Mbf64.cs`, `FloatRounding.cs`, with explicit x87/MBF precision and special rounding behavior | Reference model for exact numeric conversions and constant folding |
| [`Cipher`](https://github.com/Hawkynt/Hawkynt.github.io/tree/main/Cipher) | `type-aware-transpiler.js`, `codingplugins/ReadMe.md`: JS AST → typed IL → target AST → emitter; `TranspilerTestCases.js`, `OpCodes.js`, algorithm test harness, `transpile.js` | JS frontend, semantic normalization, algorithm fixtures, language emitters |
| Cipher coding plugins | C, C++, C#, BASIC, Delphi, Go, Java, JavaScript, Kotlin, Perl, PHP, Python, Ruby, Rust, TypeScript files | Backend inventory, tests, possible code reuse; completeness not inferred from file existence |
| [`FrameworkExtensions DataTypes`](https://github.com/Hawkynt/C--FrameworkExtensions/tree/main/Corlib.Extensions/System/DataTypes) | Configurable fixed/binary float, FP8/FP4/BFloat/decimal/posit/MBF/VAX/IBM float, 96-bit ints, Q formats, BCD, ZigZag/Gray, time/text/audio formats | Semantic type registry and independent implementation tests |
| [`BitBench`](https://github.com/Hawkynt/Hawkynt.github.io/tree/main/BitBench) | Type-reference tables, binary layout/alias/endianness data, bit tools and code export | Type naming, alias maps, layout fixtures; not automatically an implementation oracle |
| [`BitPerm`](https://github.com/Hawkynt/Hawkynt.github.io/tree/main/BitPerm) | `BitPerm/index.html`, `BitPerm/README.md`; `BUILDING_BLOCKS`, `apply`, `findRoutings`, `genCode`, `solveExhaustive`, `analyzeBitMovement`, 51 documented console tests, target CPU cost presets | Seed forward bit-routing synthesis, independent test corpus and future UI integration; normalize JS BigInt and verify arithmetic correctly |
| [`Randomizer`](https://github.com/Hawkynt/Randomizer) | RNG library + `RandomNumberGenerators.Tests` | Deterministic first cross-language kernel |
| [`CompressionWorkbench`](https://github.com/Hawkynt/CompressionWorkbench) | Compression libraries, test corpus, checksum/hash modules and codec registry | Bitstream, loops, buffers, compression second-wave coverage |
| [`PNGCrushCS`](https://github.com/Hawkynt/PNGCrushCS) | Image format implementations and integration with compression code | Binary/image formats and alias/stride safety |
| [`DrawingExtensions`](https://github.com/Hawkynt/C--FrameworkExtensions) | Color processing, dithering, quantizing, scaling and sampling | Image pipeline fusion and error-diffusion dependencies |
| MediaDroyd deinterlacer | `core/player/src/main/kotlin/com/hawkynt/mediadroyd/player/Deinterlacer.kt` implements temporal YADIF, BWDIF, Greedy variants and TomsMoComp over `Plane(IntArray)`; `stage/src/main/kotlin/com/hawkynt/mediadroyd/stage/Deinterlacing.kt` controls rendering; see `core/player/THIRD_PARTY_NOTICES.md` for upstream notices | **Pin** CPU Kotlin implementation for Kotlin→Perl; require attribution and license review before redistribution |
| [`MediaDroyd`](https://github.com/Hawkynt/MediaDroyd) | Kotlin image/video stages; native `native/src/` including `networks.*`, `xvfi.cpp`, `warp_layer.*`, `dain_layers.*`, `flavr.cpp`; third-party notices | Kotlin→Perl CPU stages; optional NCNN/Rust native binding and later tensor operators |

**Licensing checkpoint:** Record license at repository/file/algorithm/model/weight level. Third-party native frameworks and research-use-only motion-interpolation implementations/weights may prohibit redistribution or commercial use. A successful compilation does **not** imply redistribution permission. Require explicit legal approval before shipping restricted model code or weights.

---

<a id="s3"></a>
## 3. Supported language and platform matrix

<a id="s3-1"></a>
### 3.1 All languages are planned in both directions

A frontend and backend are **different deliverables**. A file named `PythonEmitter.js` does not count as a Python frontend. Every row below must gain a working parser/binder/semantic translator **and** compiling emitter with executable conformance tests. Each target profile defines dialect and version.

| Language / dialect | Frontend implementation basis | Backend implementation basis | First verified subset | Target phase |
|---|---|---|---|---|
| C# (latest stable syntax) | Roslyn | Roslyn SyntaxFactory / native syntax emitter | methods, numeric types, arrays/spans, classes, local functions, generics as supported | P2 |
| PowerBASIC DOS 3.5 / PB36 | Existing PB binder/IR | Existing PB source writer + native runtime | numeric functions, arrays, UDTs, loops, near/far memory contracts | P2–P3 |
| JavaScript (ES5+; Node/browser profiles) | Existing Cipher parser / typed IL; complement with maintained parser when necessary | Existing JS plugin | bitwise, typed arrays, objects, functions, modules | P2 |
| Rust (2024 edition profile) | Rust parser/semantic subset | Rust emitter/Cargo composer | functions, structs, slices, iterators, explicit ownership | P3 |
| C (C11/C17/C23 profiles) | C parser+semantic subset, or Clang AST import | C emitter | numeric kernels, structs/unions, pointers, arrays | P3 |
| C++ (C++17/20/23 profiles) | Clang-based semantic import or parser | C++ emitter | templates after monomorphization, containers, numeric kernels | P3–P4 |
| Kotlin (JVM version pinned) | Kotlin compiler/PSI/FIR metadata import or dedicated semantic frontend | Kotlin emitter | numeric loops, arrays, data classes, simple image kernels | P3 |
| Perl (Perl 5, version pinned) | Perl parser + explicit context/type semantics | Perl emitter/CPAN composer | numeric functions, arrays, hashes, loops, modules | P3 |
| Python (3.x profile pinned) | Python AST + resolver | Python emitter/PyPI composer | typed or inferable functions, arrays/lists, comprehensions | P3 |
| TypeScript (version pinned) | TS compiler API typed AST | TS emitter | structural object shapes, typed numerical functions | P4 |
| Java (JDK 17/21 profiles) | Java compiler AST / parser with semantic binding | Java emitter | numerical libraries, arrays, records/classes | P4 |
| Go (modern pinned release) | `go/parser`/`go/types` integration | Go emitter | functions, slices, structs, numerical loops | P4 |
| Delphi / Object Pascal | language parser + binder | Delphi emitter | numeric/procedural subset | P4 |
| FreeBASIC / generic BASIC | dialect parser and explicit semantics | Basic emitter | arrays, UDTs, numeric algorithms | P4 |
| PHP | PHP parser + runtime/coercion model | PHP emitter | typed algorithm subset and byte processing | P4 |
| Ruby | Ruby parser + shape/type specialization | Ruby emitter | numeric arrays, pure functions | P4 |
| C64 BASIC and constrained vintage dialects | dialect-specific parser | tiny BASIC emitters | resource-bounded integer/string algorithms | P5 |

<a id="s3-1a"></a>
### 3.1a Planned non-source formats — both ingestion and emission

These are **additional representation formats**, not a promise that arbitrary binaries can be restored to original source. A decoder/front-end and assembler/encoder/back-end must be tested separately. A source backend may retain emulation/FFI shims where a native operation cannot be lowered away. Format version, ABI, processor variant, memory map and runtime are mandatory profile inputs.

| Format / frontend and backend | Input contract | Output contract | First milestone |
|---|---|---|---|
| .NET MSIL / CLI assemblies | PE/CLI metadata, typed evaluation stack, IL, managed runtime calls | Valid IL assembly or source via common target emitters | B1 (after P2) |
| JVM class / jar bytecode | Constant pool, verifier types, stack, classes, invokes | Valid class/jar or source on other target | B1 |
| WebAssembly modules | Typed stack, imports, linear memory and traps | WASM module via verified writer / source | B2 |
| MOS 6502 / 65C02 assembly and raw code | Defined address/entry/CPU/memory profile; registers and flags | Assembly + assembled executable, linker placement and memory map | R2 |
| 65C816, Z80, 8080 | Defined CPU subtype and executable memory model | Assembly/binary for supported instruction subset | R4 |
| x86/8086/x64 | Architecture/mode/ABI/relocations/imports; discovered functions | Assembly/object/binary plus source outputs | B3 |
| ARM32/Thumb/AArch64 | Encoding, CPU/ABI/mode, memory and external calling contracts | Assembly/object/binary plus source outputs | B4 |
| C64/VIC-20/C128 BASIC tokenized programs | PRG header, dialect tokens, line links, PETSCII and runtime context | Valid tokenized PRG plus source text | R1 |
| GW-BASIC token files | Exact version/dialect, token tables and encoded constants | Token file and source, where format verified | R3 |
| Microsoft BASIC p-code variants | Exact dialect/compiler/runtime format; do not conflate with tokenized BASIC | Format-specific bytecode writer only when ABI/encoding understood | R5 |
| LLVM IR (`.ll`, `.bc`, version pinned) | LLVM verifier-compliant module, exact target triple/data layout, attributes, intrinsics, effects and ABI | Verifier-compliant `.ll`/`.bc` plus toolchain execution and roundtrip tests | L1/L2 (after P2; existing PB emitter as seed) |
| GCC GIMPLE/GENERIC (conditional, separate bridge) | GCC-version-specific supported export/adapter, language and runtime semantic manifest; no assumed direct LLVM compatibility | Optional GCC integration only with proven GCC toolchain plugin/adapter | G1 (research after L1) |

**Unsupported output is an explicit diagnostic**, never guessed bytes or a source translation mislabeled as equivalent machine code. Binary frontends are separately gated from modern-language bidirectionality.

**Target architecture profiles:** .NET 9 default, .NET Framework 2.0 compatible portable code where feasible, .NET 4.x, x86-16 DOS, x86/x64, ARM/ARM64, WASM/JS, platform OS/CPU SIMD variants. .NET 10/native vector intrinsics may be enabled on an explicitly pinned supported SDK/runtime profile. Separate backend **language** from runtime/ABI/CPU/features/memory/dialect.

**Language support is granular:** Report support for each `(frontend version, source construct, algorithm, backend version, runtime, architecture, optimization mode)`. Mark every combination `verified`, `experimental`, `fallback`, `unsupported` or `blocked`; do not publish blanket checkmarks.

<a id="s3-2"></a>
### 3.2 Frontend/backend implementation requirements

Each frontend MUST provide parsing; declaration binding; type/shape and effect information; numeric coercion/overflow/rounding; exception and evaluation-order contracts; deterministic module dependency graph; lowering into Semantic IR; source maps; unsupported-feature diagnostics; frontend conformance tests. Dynamic frontends MUST preserve runtime coercion/context and effects until safely specialized.

Each backend MUST provide target legality rules; target-native type/representation selection; control-flow, exception and memory lowering; minimal runtime helpers; idiomatic module/public API; formatting; compiler invocation; test-harness generation; dependency list; target profile diagnostics; reproducible artifact metadata.

<a id="s3-3"></a>
### 3.3 Release support tiers

- **Tier A (first usable alpha):** C#, JS, PowerBASIC as *both* source and target for the supported numeric/array algorithm subset; Rust target plus Rust numeric frontend; C target plus C frontend. Real CLI and cross execution required.
- **Tier B (first requested workflow release):** Kotlin and Perl **both directions**, Python and C++ **both directions**; Kotlin CPU deinterlacer→Perl; C++ native NCNN source→Rust adapter proof; C# RNG→Rust.
- **Tier C (multi-language beta):** Java, Go, TS, Delphi/FreeBASIC, PHP, Ruby as **both directions**; refined target-specific optimizations.
- **Tier D (extended/legacy):** C64/vintage subsets and advanced tensor/device operator lowering, expanded runtime platforms.

No tier is completed simply by implementing N backends. Verify at least one real source→different target path **from every language in that tier** and a generated-output execution test **into every language in that tier**.

---

<!-- PRD content ends -->

[PRD index](../PRD.md) · [Next: Core architecture](02-core-architecture.md)
