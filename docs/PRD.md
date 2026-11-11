# Polypiler — Full Product Requirements, System Design & Agent Execution Plan

**Status:** Proposed, unimplemented agent handoff specification (revision 4; not a claim of shipped features)  
**Date:** 2026-11-11  
**Scope:** Source, LLVM IR, tokenized-code, managed-bytecode and machine-code ingestion; target-source, LLVM IR, tokenized BASIC, assembly/bytecode and machine-code emission  
**Primary repository:** `Hawkynt/Polypiler` (create if authorized/absent; never assume it exists)  
**Related repositories:** `Hawkynt/PB-Compiler`, `Hawkynt/Hawkynt.github.io` (`Cipher/`, `BitBench/`), `Hawkynt/C--FrameworkExtensions`, `Hawkynt/Randomizer`, `Hawkynt/CompressionWorkbench`, `Hawkynt/PNGCrushCS`, `Hawkynt/MediaDroyd`  
**Owner acceptance:** A real developer can feed a supported source, bytecode, tokenized BASIC, or machine-code algorithm into a CLI and receive a **compiling, tested, semantically equivalent, target-native** implementation in another language or supported binary format; no claims of universal translation without verified coverage.

> **Naming contract:** **Polypiler** is the single product, repository, solution, public CLI and user-facing name. `Polypiler.Core`, `.Base`, `.Catalog`, `.Interop`, `.Permutation` and `.Adapters` are internal subsystems, not additional products or repositories. Retire any old `polycore`/`polylib` prototype CLI spellings from examples rather than presenting conflicting entry points.

**Execution contract:** This document is intended for an autonomous coding agent. Implement the phases in order; commit incremental verified work to feature branches, push branches and open reviewable PRs. Do not merely create stubs, plan files or claims of support. Feature gates, negative tests and actual target-runtime execution are required. Do not directly push to `main`, `master` or `trunk`.

---

## 1. Executive product definition

### 1.1 Product boundaries

**Polypiler.Core** is a compiler SDK with pluggable *source-language, tokenized-source, bytecode and machine-code frontends*, explicitly typed and effect-aware *semantic intermediate representations*, a *shared optimizing middle-end*, *representation legalization*, and pluggable *target-language, assembly, bytecode and machine-code backends*. It recognizes algorithms expressed in different syntaxes, proves and preserves source semantics, removes unnecessarily dynamic runtime machinery when safe, performs range/memory/dataflow optimizations, and emits target-native code plus tests and provenance.

**Polypiler.Base** is the minimal, language-neutral semantic and datatype foundation: integer/floating representations, bit operations, fixed/floating/decimal/posit formats, containers, sequence operations, pointers/views, memory ownership, logical vectors, matrices, tensors, and portable helpers. This is **not** a clone of .NET's BCL.

**Polypiler.Catalog** is the algorithm catalogue, provenance/verification store and generated package ecosystem; it hosts curated algorithms from existing repositories, preferably referencing upstream source as the maintained implementation until controlled migration. Algorithm sources may originate in any frontend language: C# is the first mature frontend, **not** the mandated canonical source language.

**Polypiler.Adapters** contains optional bridges to native libraries and compute runtimes such as NCNN/ONNX/OpenVINO/CoreML/QNN, GPUs and platform libraries. An adapter is not automatically portable algorithm source. Model definitions, weights and licenses are separately tracked.

### 1.2 User jobs

1. `translate`: Take a file/project/function in language X; produce a package/module/source in language Y with runnable tests.
2. `generate`: Find an existing algorithm by catalogue ID; choose target, runtime, optimizations, output form and dependencies.
3. `inspect`: Explain inferred types, detected patterns, narrowed ranges, eliminated allocations/checks and selected representations.
4. `verify`: Differentially execute source and generated targets; report bit-exact or tolerance-bounded results, unsupported cases and performance.
5. `package`: Emit minimal dependencies and idiomatic exports for NuGet/Cargo/npm/CPAN/PyPI/Maven/etc.; never publish externally without explicit authorization.
6. `integrate`: Replace PB-Compiler, Cipher and BitPerm's duplicated compiler functionality **incrementally** after proven parity; keep their distinct applications and interfaces.

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

### 1.4 Non-goals for first general-availability release

- Arbitrary whole-program translation of every feature of every language (reflection, metaprogramming, FFI, undefined behavior, arbitrary monkey patching, classloader features, all async models).
- Guaranteed faster output than every human-written implementation, or universal bitwise equality of transcendental math across unrelated runtimes.
- Automatic legal redistribution of third-party neural weights, libraries, shader code or algorithms.
- Treating GPU/NPU support as available on legacy PCs or C64-class machines.
- Emitting plausible-looking compilable code that silently changes source behavior.

---

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

## 3. Supported language and platform matrix

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

### 3.2 Frontend/backend implementation requirements

Each frontend MUST provide parsing; declaration binding; type/shape and effect information; numeric coercion/overflow/rounding; exception and evaluation-order contracts; deterministic module dependency graph; lowering into Semantic IR; source maps; unsupported-feature diagnostics; frontend conformance tests. Dynamic frontends MUST preserve runtime coercion/context and effects until safely specialized.

Each backend MUST provide target legality rules; target-native type/representation selection; control-flow, exception and memory lowering; minimal runtime helpers; idiomatic module/public API; formatting; compiler invocation; test-harness generation; dependency list; target profile diagnostics; reproducible artifact metadata.

### 3.3 Release support tiers

- **Tier A (first usable alpha):** C#, JS, PowerBASIC as *both* source and target for the supported numeric/array algorithm subset; Rust target plus Rust numeric frontend; C target plus C frontend. Real CLI and cross execution required.
- **Tier B (first requested workflow release):** Kotlin and Perl **both directions**, Python and C++ **both directions**; Kotlin CPU deinterlacer→Perl; C++ native NCNN source→Rust adapter proof; C# RNG→Rust.
- **Tier C (multi-language beta):** Java, Go, TS, Delphi/FreeBASIC, PHP, Ruby as **both directions**; refined target-specific optimizations.
- **Tier D (extended/legacy):** C64/vintage subsets and advanced tensor/device operator lowering, expanded runtime platforms.

No tier is completed simply by implementing N backends. Verify at least one real source→different target path **from every language in that tier** and a generated-output execution test **into every language in that tier**.

---

## 4. SDD — specification-driven development and system design

SDD here means **specification-driven development** with a versioned **software/system design document**. Each capability must have a machine-checkable semantic specification, fixtures, architecture decision and executable tests **before** release-quality implementation. Specifications are normative; README examples are not.

### 4.1 Required versioned specifications

Maintain `spec/` as the machine-readable and human-readable source of truth:

- `spec/ir/`: IR versioning, typed SSA, source semantics, canonical operations, memory, effects, exceptions, sequencing, ownership, representation, vector/tensor execution.
- `spec/types/`: parameterized type descriptors; signedness, bits/lanes, rounding, NaN/subnormal behavior, decimal, posit, MBF, fixed point, encoded integers/text, custom types.
- `spec/intrinsics/`: IntMath, FloatMath, Boolean, bitfield, bit counts, shuffles, PEXT/PDEP, saturation, FMA, trigonometry (including area functions), array/sequence/matrix/tensor operations; legality, side effects, reference algorithms.
- `spec/languages/{language}/`: precise source version and supported subset, coercion/evaluation order, exceptions, aliasing, memory model, default runtime; known unsupported syntax.
- `spec/targets/{language}/{profile}/`: physical types, ABI, alignment, pointers/unions, SIMD features, backend dependencies, runtime limits, packaging.
- `spec/recognition/`: source AST and typed SSA patterns, normalized patterns, preconditions, proof obligations, negative cases and replacement operations.
- `spec/rewrites/`: semantics-preserving transformations and required proof predicates; never enable unproven floating-point reassociation.
- `spec/verification/`: equivalence oracle, numerical tolerance models, input generators, deterministic seeds, cross-language harness and code coverage.
- `spec/binary/`: CPU instruction tables, CPU variant quirks, p-code/token tables, binary formats, stack machines, ABI and machine-state effects.
- `spec/effects/`: volatile/MMIO, external memory, unknown calls, executable-memory writes, mixed ASM, traps, order and preservation rules.
- `spec/permutations/`: bit routing, full permutations, generalized bit transforms, synthesis grammars, target cost rules and proof requirements.
- `spec/recovery/`: de-unrolling, expression graph slicing, compiler idiom recognition, speculative loop/structure reconstruction and proof modes.
- `spec/catalog/`: algorithm identity, provenance, source file/commit, exports, variants, capabilities, test vectors, license, redistributed artifacts.

Each versioned spec shall have ID, normative language, implementer/owner, tracked issue, test references, `introducedIn`, `deprecatedIn` if applicable, compatibility impact and reviewed revision. Generate human documentation from these specifications where practical.

### 4.2 Canonical semantic IR architecture

```
Frontend parse/bind OR decode/tokenize/lift machine & stack instructions
  -> Machine/Bytecode Effects IR (registers, flags, stack, memory, traps; when applicable)
  -> Source Semantic IR (full source contracts)
  -> Recognized IR (patterns + high-level operations)
  -> Typed SSA / Value IR (constants, ranges, known bits, congruences)
  -> Memory + Iteration IR (regions, alias/escape, index domains, shape/stride)
  -> Specialized IR (dedynamicization, narrowed storage, guarded versions)
  -> Representation IR (pointer/slice/union/vector layout and physical decisions)
  -> Execution IR (fused loops, SWAR, SIMD, threads, GPU/NPU where legal)
  -> Legalized Target IR (only target-supported constructs)
  -> Target source/assembly/bytecode/machine-code + tests + manifest
```

Layers are **logical**; implementation may share typed SSA nodes with analysis properties. Analyses must invalidate precisely and rerun to a controlled fixed point; use deterministic pass scheduling and optimization budgets. Preserve source locations, dialect provenance and semantic contracts through all stages.

### 4.3 Minimum mandatory core IR

Values/constants; arbitrary-fixed-width integers, signed/unsigned interpretation; floats with format identity; control flow, phi, calls, returns; structured aggregate/address-space memory; load/store/copy; checked/wrapping/saturating arithmetic; comparisons; conversions; explicit exceptions/traps; effect annotations; source mapping. IR verifier checks types, dominance, phi structure, memory bounds contracts, effect/order constraints, and stage legality. Define a stable serialized debugging form, **not** a permanently frozen serialized ABI for all internal nodes.

High-level operations may stay as intrinsics across multiple passes: `PopCount`, `DivMod`, `SinCos`, `Map`, `Filter`, `Reduce`, `Gather`, `Scatter`, `MatMul`, `Conv`, `Pshufb`-compatible `ByteShuffle` and `ParallelBitExtract` must remain recognizable until lowering. Canonicalization may also *recover* them from scalar loops and bit tricks.

### 4.4 Abstract interpretation / range facts

A shared query facade combines: signed/unsigned intervals, known-zero/one bits, congruences/modulus, finite value sets, path-sensitive relational bounds, scalar evolution, alignment, nullness, float domains, demanded bits, pointer provenance, shape/stride, alias/escape, ownership/lifetime, memory effects and source-language dynamic type/shape sets. Missing facts yield `Unknown`, never fabricated proof.

Track separately: **semantic type**, **logical minimum width**, **storage representation**, **execution register width**, **API/ABI type**. A public `long` may execute in 32-bit registers and temporarily store as bytes only if the required behavior is equivalent and the memory representation is not externally observable.

### 4.5 Recognition and semantic fusion

Source-language rules normalize comprehensions, LINQ, JS `map/filter/reduce`, Perl `map/grep`, iterators, index loops, pointer loops and image kernels into shared sequence/iteration operations. Typed SSA graph patterns recognize rotation, popcount loops, bit extract/deposit, CRC folding, endian loads, saturating idioms, multiple-precision add/carry, combined division+remainder, copy/fill and matrix kernels. Each recognition emits a proof/obligation and must have counterexamples (different width, overflow, aliasing, side effect, float rounding). Pattern matching never supersedes the source language's observable behavior.

### 4.6 De-dynamicization and speculative specialization

Support `Proven`, `Contextual`, `Guarded`, `Dynamic` inference states. JS: ToInt32/ToUint32, NaN, holes, getters, proxies, `Number` vs BigInt. Python: arbitrary `int`, protocols, dynamic attributes, descriptors, slices, iterators. Perl: scalar/list/void context, `undef`, numeric/string conversion, references, tie/magic where relevant. When unproven, generate runtime semantics or emit a precise unsupported-feature diagnostic; guarded paths must keep a valid fallback. Specialization budget limits code explosion. Source contract/annotations may be accepted with emitted guards or explicit user `--assume` diagnostics (never silently trusted).

### 4.7 Pointer, union and vector legalization

Model pointer as allocation identity + offset + address space + bounds + alignment + mutability + lifetime/provenance. Native pointer targets use native addresses when legal. Pointerless targets use owned/shared byte buffers, typed arrays or `(allocationHandle, offset)` fat pointers. Preserve aliasing, pointer identity, byte-vs-element offsets and lifetime. Pointer comparisons/casts with undefined or implementation-dependent source semantics require dialect-specific handling.

Model `BitCast`, `Reinterpret`, `OverlayView`, subvector extraction/insertion, `PackedField`, `LoadLE/BE`, `StoreLE/BE`. Emit C unions or `memcpy`, C++ `std::bit_cast`, C# explicit-layout unmanaged structs, Rust safe bitcast/controlled unsafe, JS `ArrayBuffer` views, Perl `pack`/`unpack` or generated accessor tables, and BASIC UDT overlays where **documented**. Honor C/C++ effective-type/aliasing and endian restrictions. `UInt4096` (with carries), `BitVector4096` and `Vector4096<byte>` are different types and cannot share arithmetic semantics by fiat.

### 4.8 Optimization and target selection

Required passes: constant/partial evaluation; SCCP; GVN/CSE; DCE; inlining; interprocedural effects; range/known-bit propagation; loop-invariant motion; iteration fusion; redundant allocation removal; bounds-check elimination; operation/type narrowing; storage/field packing; byte/bit packing; SWAR; SIMD vectorization/scalarization; exact FMA handling; strength reduction; copy elision; fusion of transposes/tensor operations; parallel reduction when associative; optional device placement. Each pass has an enable flag, legality proof, deterministic test fixtures, opt report and performance budget.

Cost model chooses native instruction vs sequence vs generated helper; unrolled vs loop; scalar vs SWAR/SIMD; packed vs unpacked storage; eager vs lazy; CPU vs thread/GPU/NPU. `--opt size|speed|memory|balanced`; stable defaults prioritize correctness. Target-specific PGO/autotuning may be later and must not be required to compile.

### 4.9 Runtime and package composition

Compute minimum symbol-level closure over generated source, helpers, target adapters and license obligations. Reuse target standard library only when semantics match. Polypiler.Base must remain modular. Public APIs are idiomatic but explicitly mapped to source contracts. All generated packages include reproducible build instructions, test runner, manifest, license/provenance file and compiler/IR version metadata.

---

## 5. DDD — domain-driven design

### 5.1 Bounded contexts

| Context | Owns | Must **not** own |
|---|---|---|
| Language Frontend | Source syntax, binding, source semantics, source maps | Machine-specific optimization decisions |
| Binary/Tokenized Frontend | File format decoding, instruction semantics, stack/register reconstruction, source maps/addresses | Guessed high-level types without proof |
| Machine Effects & External Calls | CPU state, flags, RAM/stack/MMIO/unknown memory, external ABI and effect dependencies | Assuming unknown memory or calls have no side effects |
| Semantic Recovery | Reconstruct arithmetic idioms, loops, data layouts, bit permutations under proof obligations | Claiming provenance or author intent without evidence |
| PolyPermutation | Canonical bit mappings, generalized bit Boolean transforms, candidate synthesis and target costs | Target text generation without typed legalization |
| Semantic Type System | Canonical type/encoding identities, conversions, numerical contracts | Source-language UI or packages |
| PolyIR | Operations, SSA, effects, memory, representation stage invariants | JS/PB syntax-specific rules |
| Pattern Recognition | Source and shared SSA patterns, proof preconditions | Final target text emission |
| Static Analysis | Range/known-bits/type/shape/alias/effects facts and invalidation | Silent semantic assumptions |
| Optimizer | Legal transformations, cost decisions and diagnostics | Reimplementation of every frontend |
| Legalization & Backend | Target capabilities, physical representation, source generation | Redefining source semantic meaning |
| Catalogue | Algorithm identities, versions, provenance, vectors | Compiler operation semantics |
| Verification | Oracles, differential tests, build/test adapters and evidence | Vendor-specific inference claims without tests |
| Package Composer | Export closure, source layout, manifests, dependency/license inventory | Algorithm correctness decisions |

### 5.2 Ubiquitous language

**Algorithm** (semantic identity and variants), **LiftedInstruction**, **MachineState**, **ExternalOperation**, **OpaqueInstruction**, **EffectSummary**, **ObservedMemoryRegion**, **SemanticRecovery**, **BitMapping**, **BitRouting**, **BitBooleanTransform**, **TargetSynthesisGrammar**, **CostEvidence**, **SourceArtifact** (repo+commit+path), **FrontendDialect**, **TargetProfile**, **SemanticType**, **PhysicalRepresentation**, **Intrinsic**, **RecognitionRule**, **RewriteProof**, **ValueFact**, **MemoryRegion**, **IterationDomain**, **Kernel**, **GeneratedArtifact**, **ConformanceRun**, **VerificationEvidence**, **LicenseObligation**, **CapabilityDiagnostic**.

Type identifiers are **not** arbitrary C# wrapper names; e.g. semantic `MBF64` and x87 extended are encoded formats with explicit conversion contracts. Gray and ZigZag are reversible encodings, not ordinary signed/unsigned arithmetic. FP8 E4M3, Posit, MBF and decimal formats cannot be merged because they occupy the same number of bytes.

### 5.3 Domain boundaries and contracts

Interfaces/ports should be narrowly scoped: `ISourceFrontend`, `IBinaryDecoder`, `IMachineLifter`, `IAssemblerFrontend`, `ITokenizedBasicCodec`, `ISemanticImporter`, `ITypeRegistry`, `IIntrinsicRegistry`, `IRecognitionEngine`, `IPermutationSynthesizer`, `IEquivalenceProver`, `IExternalApiResolver`, `IAnalysis`, `IPass`, `ITargetLegalizer`, `ITargetEmitter`, `IBuildAdapter`, `IVerificationOracle`, `IPackageComposer`. Each port declares capabilities and versioned contracts. Use immutable domain records for stable input/output, typed diagnostics and explicit result/error values; never have backends depend on frontend-specific AST classes.

Keep independently deployable modules and an anti-corruption layer around PB-Compiler and Cipher during migration. Preserve attribution and license on copied/derived code.

### 5.4 Recommended repository structure

```
Polypiler/
  AGENTS.md
  README.md
  PRD.md
  LICENSE  # establish only after license decision
  Polypiler.slnx
  global.json
  Directory.Build.props
  spec/
    ir/ types/ intrinsics/ languages/ targets/ recognition/ rewrites/ verification/ catalog/
  docs/
    adr/ architecture/ prd/ migration/ language-matrix/ benchmarks/
  src/
    Polypiler.Core.Types/
    Polypiler.Core.IR/
    Polypiler.Core.Analysis/
    Polypiler.Core.Recognize/
    Polypiler.Core.Recovery/
    Polypiler.Permutation/
    Polypiler.Core.MachineSemantics/
    Polypiler.Interop/
    Polypiler.Core.BinaryFormats/
    Polypiler.Core.Optimize/
    Polypiler.Core.Legalize/
    Polypiler.Core.Frontends.CSharp/
    Polypiler.Core.Frontends.JavaScript/
    Polypiler.Core.Frontends.PowerBasic/
    Polypiler.Core.Frontends.Kotlin/
    Polypiler.Core.Frontends.Perl/
    Polypiler.Core.Frontends.Python/
    Polypiler.Core.Frontends.Rust/
    Polypiler.Core.Frontends.C/
    Polypiler.Core.Frontends.Cpp/
    Polypiler.Core.Frontends.Other/
    Polypiler.Core.Frontends.MSIL/
    Polypiler.Core.Frontends.JVM/
    Polypiler.Core.Frontends.Wasm/
    Polypiler.Core.Frontends.MOS6502/
    Polypiler.Core.Frontends.C64Basic/
    Polypiler.Core.Frontends.MachineCode/
    Polypiler.Core.Backends/
    Polypiler.Core.Backends.MOS6502/
    Polypiler.Core.Backends.TokenizedBasic/
    Polypiler.Core.Backends.Bytecode/
    Polypiler.Core.Verification/
    Polypiler.Core.Cli/
    Polypiler.Base/
    Polypiler.Catalog/
    Polypiler.Adapters.Ncnn/
  tests/
    Polypiler.Core.UnitTests/
    Polypiler.Core.ConformanceTests/
    Polypiler.Core.DifferentialTests/
    Polypiler.Core.IntegrationTests/
    Polypiler.Core.PerformanceTests/
    fixtures/
      pb-compiler/ cipher/ bitperm/ bitbench/ randomizer/ compression/ image/ kotlin/ native-ncnn/
      binary/ msil/ jvm/ wasm/ x86/ x64/ arm/ mos6502/ c64-basic/ gw-basic/
  examples/
    js-to-csharp/ pb-to-csharp/ kotlin-to-perl/ ncnn-to-rust/ csharp-to-rust/
    bitperm-to-rust/ 6502-to-csharp/ rust-to-6502/ c64-prg-to-csharp/ msil-to-php/
  tooling/
    corpus/ builds/ ci/ licensing/ report/
  .github/workflows/
```

This layout is a suggested initial modularization; split a module further only when dependency direction or lifecycle demands it.

---

## 6. BDD — executable behavior specifications

BDD scenarios reside in `tests/features/**/*.feature` (Gherkin or equivalent machine-readable scenarios), mapped to NUnit integration fixtures and real compiler/toolchain runners. Each story below is a required functional contract, **not** documentation-only prose.

### 6.1 CLI and supported features

```gherkin
Feature: Translate a real algorithm into a target-native library
  Scenario: JavaScript Cipher to C#
    Given an approved Cipher JavaScript algorithm with known-answer vectors
    And the JavaScript and C# semantic profiles are selected
    When I translate the algorithm to C# with --verify
    Then the CLI generates compiling C# source and NUnit tests
    And the generated library passes all applicable reference vectors
    And the manifest identifies source commit, source file, target profile and helpers
    And no JavaScript interpreter is required at runtime

  Scenario: Clearly reject unsupported source semantics
    Given a source function with a runtime feature unsupported by the target profile
    When I translate it in strict mode
    Then the command fails with a stable diagnostic code and source location
    And no artifact is labeled as verified
```

### 6.2 Source-language semantics and specialization

```gherkin
Feature: Remove unnecessary dynamic values without changing behavior
  Scenario: Proven homogeneous JavaScript byte data
    Given all callers pass a typed byte sequence
    When Polypiler.Core analyzes a bitwise transform
    Then it infers the source ToInt32 conversions and exact result range
    And it may generate a typed byte loop or a verified vectorized implementation
    And the output matches edge-case source values

  Scenario: JS array has a getter with side effects
    Given element access has observable side effects
    When Polypiler.Core considers loop fusion
    Then it preserves access order and count
    And it does not replace the getter with an unguarded direct buffer load

  Scenario: Python bigint exceeds 64 bits
    Given a Python integer operation can exceed 64 bits
    When generating a Rust implementation
    Then arbitrary precision is preserved or translation fails explicitly
    And no silent truncation occurs

  Scenario: Perl list context differs from scalar context
    Given a Perl operation produces context-dependent results
    When generating C# code
    Then the requested context is represented explicitly
    And context-specific expected results are preserved
```

### 6.3 Proof-driven optimization

```gherkin
Feature: Eliminate bounds checks only when proven safe
  Scenario: Destination is guarded and loop bounds are known
    Given source length is N and destination length is proven at least N
    When a loop accesses both buffers with index 0 through N-1
    Then the optimized IR carries a reusable bounds proof
    And the backend may emit unchecked pointer/array accesses where legal
    And emitted code remains memory safe under the target's lifetime rules

  Scenario: An unknown caller may shorten or mutate the collection
    Given the collection length or pointer validity can change at an unknown call
    When the optimizer reaches that call
    Then it invalidates affected facts
    And retains bounds checks or emits a valid revalidation guard

Feature: Narrow representations only when unobservable
  Scenario: Internal wide integer stores values 0 through 15
    Given the array is private and its layout does not escape
    When storage representation selection runs
    Then it may pack values into four-bit fields if profitable
    And all reads and writes preserve the values

  Scenario: An address of a public field escapes
    Given external code can observe the field layout
    When storage packing is considered
    Then the source-visible representation is preserved
```

### 6.4 Cross-language data and runtime cases

```gherkin
Feature: Emulate pointer-based algorithms on pointerless targets
  Scenario: A native byte pointer is converted to a Perl buffer-offset view
    Given the source uses a bounded pointer into a buffer
    When Polypiler.Core emits Perl
    Then it uses a verified buffer and offset representation
    And preserves overlapping writes, byte order and bounds

Feature: Preserve floating formats
  Scenario: MBF64 to C#
    Given a BASIC MBF64 value requiring more than 53 significant bits
    When translated to C#
    Then the result preserves the specified 56-bit significand behavior
    And it does not round-trip through an ordinary double as an equivalent conversion

Feature: Sequence fusion
  Scenario: Pure where-map-reduce over an array
    Given an array with known element type and length
    When a pure filter/map/reduce pipeline is optimized
    Then intermediate collections are absent from optimized IR
    And it produces the same result and exception behavior
```

### 6.5 Specific showcase scenarios

```gherkin
Feature: Port real MediaDroyd processing kernels
  Scenario: Kotlin CPU deinterlacer to Perl
    Given an isolated Kotlin CPU deinterlacing routine and approved frame fixtures
    When I compile it as a Perl 5 module
    Then the module executes under Perl
    And output frames match the reference within the kernel's declared exactness/tolerance
    And boundary, odd-width, stride and missing-line cases pass

  Scenario: Native NCNN motion interpolation to Rust adapter
    Given an approved NCNN native model provider and separately authorized model assets
    When I generate a Rust adapter
    Then Rust can invoke the provider through a documented stable ABI
    And model load, shape validation, interpolation and resource disposal are tested
    And the manifest clearly states a native NCNN dependency
    And the output never claims pure Rust inference

  Scenario: A truly pure Rust inference request
    Given a model using operators that have no verified Rust implementation
    When I request --backend pure-rust --no-native-deps
    Then the CLI lists missing operators or weight/license blockers
    And emits no falsely verified pure-Rust inference package
```

### 6.6 BDD evidence per scenario

Each scenario attaches: source hash, target code hash, generated manifest, tool versions, fixture values, stdout/stderr/exit code, reference result, target result, diff, exactness/tolerance policy, opt diagnostics, and CI link. Negative scenarios must cover counterexamples—not just positive happy paths.

---

## 7. TDD — testing strategy and release gates

### 7.1 Test pyramid (tests written with or before production change)

1. **Semantic unit tests** (NUnit, non-fluent `Assert.That`): IR values, types, conversion/overflow, float corner cases, aliases, cost predicates, parser normalization.
2. **Pass unit tests:** before/after IR snapshots, verifier, transformation preconditions, negative tests for overflow, aliasing, NaN, side effects, exception order.
3. **Property-based tests:** random fixed seeds, shrinking, bit-pattern generators across signed/unsigned widths, variable shapes, buffers, strides, holes, `undef`, bigint, NaNs and subnormals.
4. **Metamorphic tests:** equivalent source-language variants normalize to compatible IR; encode-decode roundtrips where lossless; `Translate(A→B→A)` behavior on supported domains.
5. **Differential tests:** compare against the real source interpreter/compiler and independent reference vectors, not a second helper using the same buggy implementation.
6. **Compiler integration tests:** invoke actual `dotnet`, `cargo`, `cc`/`clang`, `g++`/`clang++`, `node`, `perl`, `python`, `javac`, `kotlinc`, PB-Compiler or equivalent with pinned toolchains, as appropriate.
7. **Whole-repository algorithm corpus tests:** each approved upstream fixture is pinned to a commit and run in at least two languages.
8. **Benchmarks:** differential speed/memory/code-size analysis relative to source baseline, previous compiler version and simple reference lowering; record regressions rather than declaring every optimization a win.
9. **Supply-chain/security tests:** dependency/license metadata, sandbox limits, malicious input handling, FFI resource safety, crypto constant-time classification.

### 7.2 Coverage and acceptance policy

- ≥80% statement/branch coverage for reusable logic; ≥90% for critical type semantics/IR verification/range and rewrite legality; 100% of **documented opcode/type contract partitions** exercised by conformance fixtures. Coverage percentages do **not** replace behavioral validation.
- Every reported supported target pair must **compile and execute** selected generated code in CI (not merely parse generated syntax).
- Numeric exact algorithms: bit-exact oracle and published vectors. Floating-point: per-operation explicit exact/ULP/relative/absolute tolerance; log tolerance policy in manifest.
- Every optimization must have at least one legality-negative fixture and a disabling flag. Differential comparison of `--opt none` against `--opt speed` on randomized fixtures.
- Random/corpus failures retain seed, minimal repro, source and generated code artifacts.
- Output must preserve public API and observable effects or document explicit compatibility mode.
- All unimplemented feature requests fail cleanly with stable diagnostics; silent fallback to wrong arithmetic is a hard release blocker.
- No compiler/optimizer crashes, unbounded hangs or runaway memory on fuzzed malformed source; implement time/memory/input bounds for untrusted compilation.

### 7.3 Per-language CI matrix

| Layer | Required jobs |
|---|---|
| Core | .NET 9 build/test, warnings as errors, format/lint, coverage, IR verifier, spec validation |
| Native | Linux/macOS/Windows C/C++ compilation and address/UB sanitizers where available |
| Rust | Stable Rust pinned, `cargo fmt`, `clippy`, `cargo test`, Miri where applicable |
| JS/TS | Node LTS pinned, JS tests, TypeScript checker when supported |
| Python/Perl | Pinned Python/Perl version + module/runtime tests |
| Kotlin/Java | JDK + Kotlin compiler pinned, executable fixture comparison |
| BASIC/PB | PB-Compiler test runner, actual supported emulator/oracle differential tests; mark genuine commercial compiler oracle availability separately |
| Full cross corpus | Source→target compile+run, reproducible manifest, license validation |

Build images pin versions, use isolated temp directories, avoid relying on undocumented host software. CI workflow permissions minimal; secrets only in approved settings; no automatic package publication.

---

## 8. Detailed epics / implementation phases

**Execution rule:** One PR per reviewable vertical slice; phase gates below must be met before advertising milestone completion. Parallelize independent backends only after semantic IR and conformance harness stabilize. All checkboxes are to be checked by the agent **only after evidence exists**.

### P0 — Repository, baseline and contracts

**Tasks**
- [ ] Verify whether `Hawkynt/Polypiler` exists; if absent, create from approved standards/permissions. Protect default branch. Pin SDKs/toolchains and create AGENTS/CONTRIBUTING/ADR/CI/issue templates.
- [ ] Clone all referenced repositories at recorded commits into a non-vendored `corpus/upstream` workspace or fetch pinned fixtures; do not blindly copy whole repos into distributables.
- [ ] Audit concrete compiler/frontend/backend/type/intrinsic components across PB-Compiler, Cipher, FrameworkExtensions and BitBench; map existing tests and licenses.
- [ ] Record compatibility boundaries, source copyrights and model/weight licenses; block restricted redistribution by default.
- [ ] Define schema + validator for language capabilities, type descriptors, intrinsic contracts, pattern rules, corpus fixtures, target profiles and algorithm provenance.
- [ ] Document initial IR stage invariants, numerical overflow/rounding contracts, source↔target diagnostic model, and CLI design in ADRs.
- [ ] Create NUnit test infrastructure, deterministic cross-language runner, CI jobs and an executable trivial C#→JS→C# demonstration.

**Exit gate:** Clean clone/build/test on .NET 9; reproducible corpus pins, validated specs, ≥80% newly added core logic coverage, one compiled/executed cross-language smoke test, CI green, reviewer approval.

### P1 — Typed PolyIR core + proof infrastructure

**Tasks**
- [ ] Implement immutable typed SSA core, basic blocks/phi, instruction/effect contracts, stage verifier, canonical serialization and source maps.
- [ ] Implement integer/float/boolean types, explicit signedness, shifts, overflow, conversions, exceptional values and target type layouts; register PB's MBF/Extended80 numeric oracles.
- [ ] Implement memory regions, arrays, spans, slices, pointer provenance/address spaces, union/overlay, byte order and lifetimes.
- [ ] Port/adapt PB-Compiler interval, known-bit, alignment/nullness analyses and add congruence/reduced product, demand bits, per-program-point facts and precise invalidation.
- [ ] Implement reusable constant/partial evaluation, SCCP, GVN, DCE, fold/rewrite infrastructure and typed proof diagnostics; benchmark regression harness.
- [ ] Add BDD scenarios for safe/unsafe bounds removal, overflow and representation observability.

**Exit gate:** Verifier rejects malformed IR, value facts reproduce verified PB fixtures, target-neutral scalar arithmetic can be interpreted and constant-folded, generated optimization reports contain proof references.

### P2 — Minimum useful **bidirectional** compiler

**Tasks**
- [ ] Build C# frontend via Roslyn; JavaScript frontend by integrating Cipher typed IL through an anti-corruption adapter; PowerBASIC frontend via PB binder/IR adapter.
- [ ] Build C#, JS, PB emitters with minimal runtime helper closure and valid source structure; reuse current PB/Cipher backend work under test.
- [ ] Implement first canonical recognition rules: rotations, popcount, endian loads, byte XOR, clamp, array copy/fill, map/filter/reduce, integer `DivMod` where safe.
- [ ] Add CLI `translate`, `inspect`, `verify` and `capabilities`; select source version and target dialect; `--opt none` and `--opt balanced`.
- [ ] Migrate first Cipher real checksum/cipher and PB numeric fixtures; emit and run C#↔JS, PB→C#, JS→PB, C#→PB as supported by corpus.
- [ ] Proven typed arrays, static JS numeric coercion, C# span/array mapping, PB integer precision and 16-bit index constraints; reject unsupported features explicitly.

**Exit gate:** A fresh user can execute at least JS Cipher→C# and PB→C# from CLI, compile and run generated code without manual edits, match authoritative vectors, and inspect helper/proof manifests. At least one reverse direction into JS and PB also verified. Binary/machine and bit-permutation capabilities remain explicitly experimental until independently gated.

### P3 — Requested workflows and rich memory/data IR

**Tasks**
- [ ] Implement Rust and C source+target support including typed numeric kernels, functions, slices/pointers and compilation of generated packages.
- [ ] Implement C++ source+target using proven semantic parser/Clang bridge; add union/bitcast and pointer reinterpretation legalization.
- [ ] Implement Kotlin source+target focusing on arrays, loops, CPU image processing and safe extraction of deinterlacing kernel dependencies.
- [ ] Implement Perl source+target with context-aware AST and typed numeric/array subset, `pack`/`unpack`, CPAN module generation.
- [ ] Implement Python source+target with type/shape and bigint-aware specialization; port typed comprehensions and sequences.
- [ ] Implement sequence/iteration fusion, path-sensitive range/bounds checks, alias/escape, storage narrowing, SWAR, SIMD vectorization/scalarization and per-target cost model.
- [ ] Produce runnable E2E-CS-RS, E2E-KT-PL, E2E-PL-CS, E2E-PY-RS, E2E-CPP-JS and at least one native union/overlay fixture.
- [ ] Build the **NCNN→Rust adapter** as a distinct integration workstream; check exact license of native code and model assets, provider ABI, weight loader, device availability, shape contracts and safe resource lifecycle. Where allowed, run a real approved interpolation sample, otherwise preserve a blocker with source reference and test harness.

**Exit gate:** Each P3 language works at least once as both input and output under its declared subset; Kotlin deinterlacer→Perl is executable and frame-verified; NCNN→Rust has either a real provider-backed integration test or an explicit legal/technical blocker that prevents a false claim of completion. All cross workflows report reproducible manifests.

### P4 — Full planned modern-language bidirectionality

**Tasks**
- [ ] Implement remaining TypeScript, Java, Go, Delphi/Object Pascal, FreeBASIC, PHP and Ruby frontends and backends using versioned semantic profiles.
- [ ] Add JavaScript/Python/Perl guarded specialization when dynamic behavior requires it; contextual contracts and fallback generation.
- [ ] Complete FloatMath, IntMath and encoded type catalogue with representative microformat oracles, vector/tensor/collection intrinsics and selected native mappings.
- [ ] Implement Rust iterator, Java stream, Python comprehension, JS array method and Perl map/grep normalization to shared iteration IR.
- [ ] Expand package composer to Maven, NuGet, npm, Cargo, CPAN, PyPI and appropriate source layout with `--no-publish` default.
- [ ] Create compatibility dashboard built only from executed CI results and target version matrix.

**Exit gate:** At least one real algorithm per newly added language works as **frontend and backend**, with a multi-language roundtrip fixture and explicit unsupported-feature classifications. Broad language support does not imply general whole-program compatibility.

### P5 — Advanced compute, legacy and optimization maturity

**Tasks**
- [ ] Extend numerical type registry: decimal, FP4/FP8, configurable float/fixed point, Posit, 96/128/large integers, MBF/IBM/VAX, ZigZag/Gray, BCD. Preserve distinct semantic/storage/compute types.
- [ ] Complete logical vector widths 16/32/64/128/256/512/1024/2048/4096 and extensible, Boolean LUT, byte-shuffle, PEXT/PDEP, saturating and packed arithmetic, mixed-lane subviews, codegen fallback.
- [ ] Expand matrix/tensor operations with shape/stride/ownership, fusion and explicit CPU/GPU/NPU capability planning.
- [ ] Add basic C64/vintage BASIC constrained source+target subsets with RAM and numeric-range diagnostics.
- [ ] Define **optional, separately licensed** pure-Rust inference translation: enumerate NCNN graph ops, convert parameter/weight formats, implement/test operator coverage and precision, and only then remove native NCNN dependency. Unsupported model ops reject with precise diagnostics.
- [ ] Performance-profile on real PB graphics, Cipher crypto, compression, image filters and MediaDroyd workloads; reject transformations that regress the chosen target objective outside budgets.

**Exit gate:** Semantic conformance across applicable target profiles, measured optimization wins on at least representative workloads, no unverified neural-framework or legacy-runtime portability claims.

### P6 — Controlled upstream replacement

**Tasks**
- [ ] Maintain opt-in adapters in PB-Compiler/Cipher with feature flags; do not delete legacy paths prematurely.
- [ ] Run upstream differential suites and optimize-stage comparisons on pinned corpus; investigate every mismatch.
- [ ] Compare correctness, diagnostics, runtime behavior, generation latency and code size before cutover.
- [ ] Gradually route supported PB optimizations and Cipher transpiler calls through Polypiler.Core; preserve original behavior for uncovered cases.
- [ ] Open separate PRs in upstream repositories with fallbacks, migration guides and rollback switches.
- [ ] Expand to Randomizer, CompressionWorkbench, PNGCrushCS, DrawingExtensions and MediaDroyd as catalog/algorithm sources.

**Exit gate:** Legacy implementation removed only after complete scope equivalence, green upstream CI, performance review, license clearance and human approval. Each repository remains usable independently during transition.

---

## 9. Agent work packages and task dependency graph

```
P0 specs/toolchains/corpus
  ├── P1 SSA + verifier ──> analyses ──> canonical optimizers
  ├── type registry ──────> numeric reference oracles
  └── test runner ────────> source/target differential matrix

P1 + P0 -> P2 (C# / JS / PB bidirectional)
P2 -> P3 Rust/C/C++ + Kotlin/Perl/Python + sequences/memory + PolyPermutation
P1+P2 -> B1/B2 managed bytecode and WASM lifting
P1+P2 -> R1/R2 tokenized BASIC and 6502 lifting/emission
P1+P3 -> B3/B4 x86/x64/ARM machine lifting
P2+P3 -> S1 semantic de-optimization / loop reconstruction
P3 -> P4 remaining modern languages and package ecosystem
P3 -> P5 advanced vectors/tensors/native inference bridge
P2+P3+P4+P5 -> P6 verified legacy replacement
```

Each work package/PR requires:

- **Why:** relevant user job and spec ID.
- **Scope:** exact public APIs, IR nodes, compiler stage and language profiles.
- **Preconditions:** prerequisites, test corpora, source commits and licensing.
- **BDD:** one positive and one unsafe/unsupported negative scenario at minimum.
- **TDD:** failing tests committed before or with implementation; green `dotnet build`/`dotnet test`, appropriate cross-language run.
- **Evidence:** generated outputs, oracle diffs, opt-report, coverage, benchmarks if performance-related.
- **Docs:** versioned specifications, README capability changes, migration note.
- **Review:** reviewer, CI green, no direct default-branch push; linear history.

**Smallest useful slices recommended for first agent sprint:** bootstrap and corpus script → typed integer + verifier → C# frontend simple numeric function → C# backend → JS import/op normalization → JS-to-C# fixture with real Cipher vectors → PB import → PB-to-C# fixture → Rust backend RNG fixture → memory/sequence optimization proof tests. Do not spend the first sprint implementing 15 emitters that only output placeholders.

---

## 10. CLI/API contracts

Canonical executable: `polypiler` (one CLI for translation and catalogue operations); keep this spelling stable.

```sh
polypiler translate Cipher/algorithms/checksum/<algorithm>.js \
  --from javascript --source-profile node-es2022 \
  --to csharp --target-profile net9 \
  --export <algorithm> --opt speed --verify \
  --output generated/cipher-csharp

polypiler translate path/to/algorithm.bas \
  --from powerbasic --source-profile pb35 \
  --to csharp --target-profile net9 --verify --output generated/pb-csharp

polypiler translate path/to/Deinterlace.kt \
  --from kotlin --to perl --target-profile perl5 \
  --export <entry> --verify --output generated/deinterlace-perl

polypiler translate path/to/native/ncnn_kernel.cpp \
  --from cpp --to rust --strategy native-adapter \
  --native-provider ncnn --verify --output generated/ncnn-rust

polypiler translate BitPerm/mapping.json --from bitmapping --to rust \
  --target-profile x86_64-v3 --verify --output generated/bitperm-rust

polypiler recover source/to/algorithm.asm --from mos6502 --cpu mos6502 \
  --entry 0x8000 --memory-map profiles/c64.json --to csharp \
  --verify --output generated/6502-csharp

polypiler translate algorithm.rs --from rust --to mos6502 --cpu mos6502 \
  --target-profile c64 --verify-emulator vice --output generated/6502-prg

polypiler translate PROGRAM.PRG --from c64-basic-v2 --to csharp \
  --verify --output generated/c64-csharp

polypiler translate source.dll --from msil --export Namespace.Checksum \
  --to php --target-profile php8 --verify --output generated/msil-php

polypiler translate Example.class --from jvm --to delphi \
  --verify --output generated/jvm-delphi

polypiler translate input.bc --from llvm --llvm-version pinned \
  --to csharp --target-profile net9 --verify --output generated/llvm-csharp

polypiler translate algorithm.cs --from csharp --to llvm \
  --target-triple x86_64-unknown-linux-gnu --verify --output generated/algorithm.ll

polypiler translate algorithm.cs --from csharp --to x86-16 \
  --target-profile dos-real-mode --verify-emulator dosbox-x \
  --output generated/algorithm.exe

polypiler inspect path/to/source --from javascript --show types,ranges,effects,patterns,ir,proofs
polypiler capabilities --from kotlin --to perl --source-profile jvm --target-profile perl5
polypiler verify generated/cipher-csharp --against corpus/cipher/<algorithm>.json
polypiler search "Floyd-Steinberg" --language rust
polypiler generate color.dithering.floyd-steinberg --to rust --verify --output generated/dithering
```

Placeholder paths and names in angle brackets are illustrative and **must be resolved to real pinned corpus files by the agent**. The actual CLI must support functions/exports and project dependencies; no manual copy-paste of anonymous snippets required.

### 10.1 Exit codes and diagnostic contract

`0` success and verification pass; distinct stable nonzero codes for unsupported construct, invalid source, semantic mismatch, target build failure, reference oracle failure, license blocker, missing native/model dependency, resource budget violation, internal compiler error. Structured JSON diagnostics include source range, rule/spec ID, cause, workaround/required capability and reproducible command. On failure, no package may be marked verified.

### 10.2 Generated manifest (minimum)

Schema includes: compiler and IR versions, requested CLI/config, input hashes and provenance, frontend profile, outputs/API, target/runtime/platform profile, intrinsics, helpers, native dependencies, licensing and model/weight restrictions, source semantic caveats, optimization passes/proofs, warnings, toolchain versions, exact verification results, benchmarks when measured, build/run commands. Distinguish `generated`, `built`, `executed`, and `verified` states.

---

## 11. Concrete corpus and test ladder

Avoid using the same algorithm family exclusively; select representative real upstream artifacts **after inspection** and pin a manifest entry for each.

| Corpus | Initial feature | Target evidence |
|---|---|---|
| Cipher simple checksum | JS bitwise coercions, shifts, byte arrays | JS→C#, JS→PB, JS→Rust; official vectors and source harness |
| Cipher block/stream cipher | Rotations, packed arithmetic, constant-time constraints | JS→C#, C, Rust, Perl; full known-answer vectors and effect checks |
| Randomizer RNG | Wrapping arithmetic, deterministic seed/state, vector lanes | C#→Rust, C, JS, Python; identical stream vectors |
| PB numeric fixtures | Signedness, DIV/MOD, x87/MBF, loops, near/far pointers | PB→C#, Rust, JS; source interpreter/native oracle where available |
| CompressionWorkbench bitstream/codec | Buffer bounds, bit packing, backreferences, state machines | C#→C/Rust/Perl; roundtrip and malformed stream cases |
| FrameworkExtensions numeric types | FP8/fixed/posit/MBF/ZigZag/Gray | Reference convert/encode roundtrips, selected cross-language implementations |
| BitPerm real mappings and synthesized output | Bit mapping, duplicated/inverted/don't-care output bits; shifts/rotates/mul/PEXT/PDEP/PSHUFB/RLWINM; preset cost models | BitPerm→C#/Rust/6502/PB; reverse code→mapping; exact bitvectors, oracle and proof evidence |
| BitBench type layouts | Byte order, signed/unsigned aliases, reinterpretation | Independent reference bit patterns and union legalizer tests |
| DrawingExtensions/PNGCrushCS | Strides, image borders, dithering dependencies | C#→Rust/Perl/C, PNG fixtures, spatial fidelity |
| MediaDroyd actual `Deinterlacer.kt` | Temporal deinterlacing (`YADIF`, `BWDIF`, `GREEDY_HM`, `GREEDY_2FRAME`, `TOMSMOCOMP`), field parity, planes | Kotlin→Perl exact plane fixture comparison, preserve upstream LGPL/licensing requirements |
| MediaDroyd Kotlin CPU stages | Chroma, deinterlace, filters and motion-adjacent pure kernels | Kotlin→Perl/C#/Rust, real frame hashes/PSNR when appropriate |
| PB-Compiler inline ASM / graphics tests | Mixed source+ASM, x86 instructions, address-space effects and hardware I/O | ASM→C# or native x86; unknown effect fallback; video-memory negative cases |
| C64 BASIC PRG / MOS 6502 | Tokenization, PETSCII, RAM/ROM/MMIO, register flags, 6502 calling conventions | PRG→C#, Rust→6502; emulator and trace oracle |
| MSIL/JVM/WASM actual compiled fixtures | Typed stack IR, BCL/JDK intrinsics, imports and traps | MSIL→PHP, JVM→Delphi, WASM→Rust; real VM oracles |
| x86/x64/ARM compiled fixtures | Stack/register SSA, flags, external calls and relocations | x86→C++, x64→Python, ARM→PB; native/emulated reference |
| MediaDroyd NCNN native | Model/provider integration, nonportable native objects | C++ source→Rust **native adapter**; pure inference tracked separately |

Each corpus fixture's manifest records **upstream commit SHA**, file path, exposed function and dependencies, target run command, baseline vectors, license, expected determinism, permitted numerical tolerance and supported profiles.

### 11.1 Differential matrix requirements

For the initial alpha, verify at least six independent X→Y paths, with JS→C#, PB→C#, C#→JS, JS→PB, C#→Rust and Rust→C# (or C→JS where Rust frontend blocked; document blocker). For Tier B, cover each new language as source and target, and the two MediaDroyd showcases. For Tier C, add one bidirectional exercised path per additional language and representative pairs, not an unbounded full N×N runtime matrix. Run pairwise semantic contract tests and use a smoke/rotating matrix for breadth.

### 11.2 Test corpus integrity

Never adjust expected vectors to match generated code. Preserve golden upstream output as read-only. Use independent specifications/test vectors where possible. If output differs because of legitimate float tolerance, record both results and a declared tolerance; don't blanket-ignore floating-point differences. For cryptography, exact outputs and constant-time constraints are distinct requirements.

---

## 12. Native NCNN and neural model plan (explicitly separate)

**Path A — Rust bindings to existing inference provider (required P3):** Analyze relevant MediaDroyd C++ functions and NCNN integration; choose a stable C ABI shim rather than binding undocumented C++ implementation details; provide safe Rust wrapper around model contexts, frames/tensors and resources; build/test with permitted provider binaries/source. Produce correct interpolated output using authorized sample model/weights. Handle unsupported architectures and accelerators explicitly.

**Path B — compiled operators with no NCNN dependency (future P5):** Parse model graph/parameters, check all operators, implement equivalent tensor layouts, padding, rounding/quantization and interpolation rules, convert weights preserving format/precision, run differential model outputs. Emit Rust source or crate **only if every operator and dependency is supported and the weights are distributable under appropriate permission**. Native-provider binding is not a successful pure-Rust model translation.

**Path C — learn algorithms from native C++ code:** Can translate self-contained motion estimation, warping and pre/post-processing kernels separately; pointer/union legalizer, tensor IR and target operations apply. Does not turn opaque NN architecture and third-party weights into portable Rust automatically.

Model license and commercial-use conditions are **release blockers**, not warnings that can be overridden by an optimization flag. Allow local use with user-provided weights only where legally and technically permitted.

---

## 13. Quality attributes and performance targets

### 13.1 Correctness first

- No known semantic divergences in passing released corpus fixtures; known unsupported features are consistently diagnosed.
- A generated source artifact compiles **without modifying generated code** using declared, pinned toolchains/dependencies.
- Stable, machine-readable verification summaries and optimization justifications.
- Full deterministic rebuild from compiler version, source SHA and profile.

### 13.2 Performance

Initial performance goal is to **remove avoidable overhead**, not require an impossible universal speedup. Track execution time, allocation count, peak memory, generated source size, binary size, build time and compilation cost, compare `--opt none`/`balanced`/`speed`, and publish target-specific results with representative inputs.

Acceptance for specific optimizations: reduced bounds checks/allocations/loops are shown in optimized IR; actual emitted target demonstrates the transformation when legal; correctness preserved; performance not materially worse on benchmark corpus unless the selected objective explicitly favors size/memory. Establish numeric regression thresholds after baseline measurement by ADR; never invent benchmarks.

### 13.3 Security, sandbox and reproducibility

Parse untrusted code with resource limits; never execute untrusted source outside an isolated configured runner. Restrict filesystem/network access of generated-code tests and native model loading. Pin external dependencies. Enforce crypto-sensitive effect/secret-taint/constant-time restrictions and prohibit secret-dependent speculative dispatch. Run native sanitizers and fuzzers. Generate SBOM and license/provenance notices for packages.

### 13.4 Developer experience

A new contributor should be able to add a language feature by editing a spec, adding one frontend semantic mapping, one backend lowering or fallback and conformance fixtures—not duplicating the entire optimizer. Docs include error catalog, type registry browser, IR dump viewer, before/after optimization graph and capability matrix.

---

## 14. Acceptance criteria / definition of usable maturity

### First **usable alpha** (a developer can really translate)

- [ ] `polypiler translate ... --from javascript --to csharp --verify` works on a pinned real Cipher algorithm, builds generated .NET library and passes authoritative vectors.
- [ ] PowerBASIC numeric source translates into C# and runs with differential verification.
- [ ] At least one C# numeric algorithm translates into JS and PB and compiles/runs.
- [ ] Rust numeric frontend and backend have one real executable cross-language path.
- [ ] IR verifier, range analysis, semantic diagnostics, reproducible manifest and minimum helper closure exist.
- [ ] Invalid or unsupported source yields a specific failure instead of invalid generated code marked success.
- [ ] Unit/BDD/differential CI green, documented source subset, pinned toolchains, reviewed PRs.

### First **requested showcase beta**

- [ ] Kotlin CPU deinterlacer translates into an actual Perl module; executable frame-level tests pass.
- [ ] Perl and Kotlin are also tested as frontends, not just backend labels.
- [ ] C++→Rust NCNN integration has a real licensed provider-backed demonstration or an explicit tracked release blocker; if blocked it is not advertised as supported.
- [ ] C#/JS/Rust/C/C++/Python/Perl/Kotlin/PB source+target support meets individually published conformance subsets.
- [ ] Source language patterns are recovered from nontrivial upstream loops, sequences and memory operations; target optimizations show validated bounds/representation changes.

### General availability for the **advertised subset**

- [ ] All Tier A–C languages advertised as supported have executed frontend→different backend AND different frontend→backend tests, with version profiles.
- [ ] Catalogue imports real algorithms from the identified source repositories; generated packages include full license/provenance and independent vectors.
- [ ] Source/target compatibility report is based on actual verified combinations, with known limitations and precise blockers.
- [ ] Both PB-Compiler and Cipher integrations can optionally delegate to Polypiler.Core for proven supported paths without forcing users onto the new implementation.
- [ ] Performance/size/memory comparisons and documented rollback controls exist.

---

## 15. Risks, decisions and change control

| Risk | Mitigation / decision |
|---|---|
| Unbounded language semantics | Tiered subsets, source contracts, real semantic diagnostics, no premature whole-program claims |
| IR becomes universal BCL clone | Small core + parameterized types + high-level semantic intrinsics + optional helpers |
| Duplicate PB and Cipher bugs | Independent vectors, keep old oracles, differential tests, proof obligations |
| Floating semantics differ | Explicit format/rounding/quantization, exact vs bounded-error profiles, forbid silent reassociation |
| Dynamic language specialization is unsound | Proven/contextual/guarded/dynamic states, side effect and alias checks, safe fallback |
| Pointerless targets miscompile aliases | Fat pointers, region/lifetime contracts, endian/layout verification, negative tests |
| SWAR crosses lane carries | Lane-isolation proof, width-aware shifts, reference vector tests |
| Native inference is mistaken for pure translation | Separate adapter/operator/model workflows; package manifests reveal native runtime/weights |
| License incompatibility | Per-file/model/weights provenance and explicit publication gates |
| Explosion of source×target combinations | Shared IR conformance plus strategically selected executable pairwise and roundtrip corpus |
| Optimizer complexity / nontermination | Pass budgets, stage verifier after each pass, deterministic scheduling and minimizable fuzz cases |
| Toolchain availability for retro targets | Reproducible emulator/native harness, limited supported profile and explicit oracle availability |
| Breaking source projects prematurely | Dual-run feature flags, independently testable integration, gradual cutover and rollback |

Decisions needing ADR before associated implementation: IR integer signedness policy; representation format/versioning; source evaluation and exception ordering; float strict/accurate/fast policy; optimizer proof granularity; Pointer provenance model; NCNN provider ABI strategy; dynamic object specialization guards; language parsing dependencies; split of source vs target version profile; package publication workflow.

---

## 16. Immediate agent instructions

1. Inspect `AGENTS.md` in **each** upstream repository and the project. Respect repository-specific build/licensing/PR rules.
2. Verify repository URLs, current branches and selected fixture paths. Replace illustrative paths/commands with **real** pinned SHA+path entries.
3. Create the Polypiler development repository/feature branch only with existing permissions; if creation or pushing is unavailable, create a fully reviewable local branch/patch and describe the blocker rather than claiming remote completion.
4. Start with executable test harness and a real JS Cipher→C# vertical slice. Build the minimum IR required for that slice without corrupting the future architecture.
5. Add PB→C# next, importing existing typed SSA/value facts where safe. Do not delete original code.
6. For each PR: update machine specs, failing/green BDD and NUnit tests, target compile+execution evidence, coverage, performance evidence for performance PRs, compatibility matrix and changelog.
7. Do not mock compilation, don't mark unsupported targets green, and don't replace NCNN inference with placeholder functions.
8. At the end of each phase, provide a short evidence-backed report with branch/PR, commit SHAs, tests actually run, compiler versions, artifacts, supported language pairs, observed limitations and next blocking issue.

### Initial deliverables checklist (the first reviewable PR sequence)

- [ ] `docs/prd/Polypiler.Core-Polypiler.Catalog-PRD.md` (this file), versioned spec schemas and ADR index.
- [ ] `tooling/corpus` importer with manifest locking, license scanner and repeatable upstream fixture discovery.
- [ ] `Polypiler.Core.IR`, `Polypiler.Core.Types`, verifier and tests for fixed-width integer, source numerical coercion and memory views.
- [ ] `Polypiler.Core.Frontends.JavaScript` adapter and C# backend supporting the first pinned Cipher algorithm.
- [ ] CLI translate/verify and CI integration compiling/running the generated C# library.
- [ ] Published `capabilities` output clearly identifying all other language directions as planned or unsupported until tests pass.

**The deliverable is not a diagram and not a collection of emitter file names. It is a verified cross-language executable translation pipeline, grown incrementally into the supported source×target matrix.**

---

## 17. Fundamental semantic-preservation contract (normative; applies to every pass)

### 17.1 Non-negotiable transformation law

A translation/optimization may replace an operation only if it preserves the **declared observable behavior of the selected source profile**, subject to explicitly selected compatibility/approximation modes. Observable behavior includes return values; mutations to reachable memory and externally visible state; file/device/network interactions; external calls; exception/trap types and ordering; program termination; resource lifetime where visible; concurrency/happens-before; hardware I/O traces where required; and timing/cycle behavior **when** the program contract makes timing observable. Source undefined behavior remains undefined only where the source language/profile actually permits that conclusion; do not invent UB for defined legacy/managed operations.

The compiler SHALL retain a recognized but unlowerable instruction in canonical/machine IR. It SHALL NOT imply that retaining the IR means it can execute on every target. The target legalizer chooses `MapNative`, `GenerateHelper`, `PreserveABI`, `Emulate`, `DeferredResolution`, or `RejectWithDiagnostic`. For unknown operations, cross-architecture output may legitimately fail. **Preservation is compulsory; portability is conditional.**

Optimization decisions SHALL distinguish: (1) instruction/operation semantics known or opaque; (2) legality of a proposed rewrite proven/conditional/unknown; (3) execution support on the target native/helper/adapter/emulator/none. An operation may be known and nonreorderable; opaque but preservable on its source platform; or pure but unavailable on a target.

### 17.2 Effects are data dependencies, not giant barriers

Represent partial state explicitly, preferably by SSA tokens per effect class/region. Core effect dimensions:

| Effect | Must describe | Required behavior |
|---|---|---|
| `Memory.Read`/`Memory.Write` | allocation identity, offset, range, address space, alias set, volatility | Reorder only under proven non-interference |
| `Memory.Unknown` | unresolved pointer and possible alias domains | Invalidate affected facts, not all unrelated pure arithmetic |
| `Memory.ExecutableWrite` | executable pages/regions and possible decoding invalidation | Prevent unsafe predecoded code caching/reordering |
| `Device.Read`/`Device.Write` | port, MMIO register, observable order, width, device profile | Retain I/O trace and side effects |
| `External.Call` | symbol/version/ABI, arguments, return, clobbers, memory, exceptions, thread effects | Map only to exact known equivalent contract or retain dependency |
| `Machine.Flags`/`Machine.Registers` | architecture-mode-specific defs/uses, reserved bits and status state | Carry/overflow/condition code SSA and demanded-flags elimination |
| `Stack` | SP/FP, call frames, return addresses, red zones/near-far/calling convention | Never assume every stack slot is a local variable |
| `Trap`/`Exception` | cause, condition, ordering, handler state | Never silently erase observable failures |
| `Synchronize` | fences, atomics, volatile accesses, interrupts | Preserve ordering and required visibility |
| `Runtime.State` | allocator/GC/class-init/dynamic loader/VM state | Explicit mapping or required runtime helper |
| `Timing` | cycles, interrupt/raster interactions where specified | Cycle-accurate equivalence or reject/fallback |

Local stores are optimizable only after proving the region belongs to program storage with appropriate lifetime/alias bounds. Writing to unclassified memory, stack metadata, I/O or executable memory must retain effects until proven otherwise. A write to `0xA0000` on VGA and a store to a private C# array are *not* interchangeable; neither is a C64 VIC-II register store equivalent to a byte-array assignment. Memory maps and CPU modes are target profile assets, not implicit constants.

Unrecognized instructions preserve their original bytes/text plus a conservative effect summary and architecture context; unrecognized calls preserve symbolic identity and call-site ABI. If an inline assembly block makes an undeclared or unmodelled effect possible, the frontend must conservatively widen its effect set. Pure SSA arithmetic may still optimize across it when independent.

### 17.3 External API normalization registry

Every known BCL/JDK/CRT/PowerBASIC/Python/Perl/JS/NCNN call mapping has: `{LibraryIdentity, LibraryVersionRange, SymbolSignature, GenericInstantiation, SourceSemantics, Throws, Effects, Preconditions, CanonicalIntrinsic, TargetLowerings, Fallback, TestOracle, License}`. Examples:

- .NET `BitOperations.PopCount` and Java `Integer.bitCount` → width-specific `IntMath.PopCount`.
- Java `Integer.rotateLeft` and recognizable C/JS bit hacks → `RotateLeft` with exact shift-count normalization.
- C `memmove` → overlap-safe `Memory.Move`; **not** `memcpy` when overlap possible.
- `System.Array.Copy` → typed `Memory.Copy` only when array covariance and exception behavior cannot change result.
- .NET LINQ `Where/Select/Reduce`, Java streams, Python comprehensions, Perl `grep/map`, JS array methods → sequence operations after effect and laziness checks.
- PowerBASIC runtime calls → identified semantic operation, original runtime import with ABI, or a helper implementing the exact dialect behavior.

Unknown APIs must never be replaced by functions with vaguely similar names. The generated artifact's manifest states unresolved external symbols, exact target runtime requirements and any emulation bridge. A target that cannot resolve them fails with a stable diagnostic.

### 17.4 Inline assembly is syntax, not a universal optimizer fence

Assembler frontends SHALL decode supported opcodes as ordinary machine-semantic constructs interleaved with surrounding source instructions. Register operands, embedded labels, jump targets, embedded data, clobbers, constraints, address spaces, instruction prefixes and assembler dialect are retained. The frontend must track source-level references into ASM and ASM-written values flowing into source code, including flags, carry and memory.

The optimizer SHALL be able to recover high-level semantics and erase all ASM when the complete block is provably portable; it SHALL ALSO be able to lower surrounding high-level code to target machine instructions and emit an entirely assembly implementation. A mixed output remains valid when target compilation supports inline ASM and the effect contract remains satisfiable. **Entirely high-level** and **entirely ASM** are choices, not unconditional success conditions.

Example recognition target: `MOV; BSWAP; MOV` → `ReverseBytes32`; `CLC; LDA; ADC; STA` → `AddWithCarry8` and affected flags; x86 `ROL` → `Rotate` if flag consumers are dead or modeled; packed XOR/shift idioms → logical bit-routing. Unknown `INT`, `OUT`, `SYS`, `POKE`, custom runtime calls or self-modifying instruction bytes remain represented unless platform and effect proofs permit other lowering.

### 17.5 Required negative tests

- Store into unknown pointer cannot be dropped; no speculative pure-array alias assumption.
- Stack-address access overlapping a saved return address cannot be renamed as private local storage.
- DOS `OUT`/`INT` or C64 `POKE` to MMIO cannot be folded merely because later ordinary memory reads miss that address.
- Unrecognized runtime call that can mutate an array prevents reuse of invalidated bounds/array-content facts but does not block independent constant folding.
- A call with mismatched calling convention cannot be rebound without verified bridge.
- A 6502 self-modifying code write invalidates impacted instruction decoding or forces emulator fallback.
- Volatile repeated reads and writes remain separately observable and in order.
- A known x86 opcode without target ARM equivalent is preserved in IR but not emitted as working ARM source without a legalizer/helper.
- Interrupt-visible/raster-visible writes cannot be fused solely based on final framebuffer equality.

---

## 18. Binary, bytecode and historical-language recovery (SDD)

### 18.1 Three source paths and two lowering horizons

1. **Text/source path:** parse → bind → source semantic IR (C#, JS, TS, Python, Perl, Kotlin, Rust, C/C++, BASIC, Java, Go, Delphi, PHP, Ruby).
2. **Token/p-code path:** decode dialect+version → source or stack instruction representation → dialect semantics → canonical IR (C64 BASIC V2, C128, GW-BASIC, specific Microsoft p-code variants).
3. **Bytecode/machine path:** decode/disassemble → recover control flow → stack/register SSA → memory/effect modeling → canonical IR (MSIL, JVM, WASM, 6502, x86/x64, ARM32/Thumb/AArch64, later Z80/etc.).

**Fast horizon:** recover a verified *function-level, closed computational kernel*, with explicit imports and argument buffers. **Extended horizon:** translate executable modules with multiple functions, state, classes and external dependencies under documented platform contracts. Arbitrary whole-application decompilation and OS/runtime recreation are not first-release promises.

### 18.2 Binary input manifest and discovery

A binary frontend is invoked with a structured manifest when code/data boundaries cannot be inferred safely. Inputs include executable/firmware/PRG path and hash, ISA variant, endianness, mode, origin/address, entrypoints, code/data ranges, relocation map, import resolution, calling convention, stack profile, CPU features, memory map, return/output contract, and reference runner. For native binaries, indirect branches and jump tables need validated discovery; uncertain reachable bytes remain undecoded until analyzed, not guessed into instructions.

Function boundaries can be recovered using symbols/debug information, signatures, stack analysis or caller-supplied hints. Discovered boundaries receive confidence/proof status; only proven semantic facts may authorize aggressive rewrites. Missing dispatch targets, executable code generation and relocations must block claiming a verified standalone translation unless safely emulated.

### 18.3 Managed and portable bytecode

**MSIL:** read PE/CLI metadata, method signatures, generic instantiations, typed evaluation stack, instructions (`conv`, `box`, `unbox`, `call`, `callvirt`, `ldtoken`, `newarr`, `ldelema`, exception regions), static initialization and managed memory. Normalize known .NET BCL calls. Unsupported reflection, dynamic assembly load, runtime code generation, unmanaged interop and high-level object-model mismatches require helpers or diagnostics. Deliver source outputs including `MSIL→PHP`; also support IL emission as a backend profile.

**JVM:** parse classfile constant pool, stack maps, method bytecodes, `invokedynamic`, exception tables, class init, object identities, arrays and exception/error behavior. Normalize known JDK operations. Model integer overflow and Java shift counts rather than target-language defaults. Deliver `JVM→Delphi` demo; backend JVM bytecode generation via verifier-clean classfile.

**WASM:** parse core modules with typed stack, control nesting, imports, globals, tables, linear memory, traps and defined widths; only proposal features explicitly enabled in profile. WASM memory access must preserve little-endian semantics and trap conditions. Support WASM frontend and backend; test with real runtime.

### 18.4 Machine-code lifter architecture

- Decode per CPU variant and instruction mode; model complete official opcode semantics before optimizing.
- Lower architectural registers/subregisters to SSA with partial-register writes, flags to separate SSA values, and stack to explicit memory operations.
- Reconstruct control flow and calls; infer effects and calling convention by proof or user contract.
- Distinguish machine-width arithmetic from source-language types; never assume `EAX` means C# `int` or a 6502 `A` means standalone variable.
- Recover abstract pointer provenance only when allocation identity/range can be shown. Device addresses, unknown pointers and code writes retain effects.
- Recognize algorithmic idioms after lifting, not by unsafe byte-string matches.
- Preserve machine-state capabilities (carry, saturating flags, exceptions, status mode bits) until consumers are proven dead.

**6502**: A/X/Y, stack pointer, PC, status flags, 8-bit arithmetic, decimal mode behavior per NMOS/CMOS model, addressing modes, zero page, page crossing, indirect jumps, and unmodified behavior of official opcodes. Define policy for undocumented opcodes: decode+semantics only with a pinned CPU model and tests, otherwise explicit unsupported diagnostic. 65C02/65C816 are distinct profiles. Use an emulator oracle for every emitted/decoded machine routine.

**x86/x64**: address/operand size, subregisters, flags (including undefined ones), SIMD state, segmentation where relevant, FPU/x87, ABI and alignment; preserve `volatile` hardware I/O and privileged/trapping instructions. **ARM32/Thumb/AArch64**: condition codes, ISA/mode transitions, endian profile, banked registers where applicable, load/store semantics, condition flags and ABI. Never equate architecture-level instruction equivalence with identical cycle counts.

### 18.5 Tokenized BASIC, p-code and mixed loaders

C64 BASIC V2 PRG decode: load address (commonly but not universally BASIC start address), line links, line numbers, token values, quoted strings, `REM`/`DATA` tokenization rules and PETSCII; preserve source text/line metadata. Source back-end may output listing; binary back-end emits valid PRG with resolved links, line numbers and terminators. Validate with independent tokenization and emulator execution. `POKE`, `PEEK`, `SYS`, `USR`, ports and hardware accesses use effectful memory/runtime ops, never guessed ordinary variables.

GW-BASIC token files differ from C64 PRG; detect by format/profile and parse numeric token encodings. **Microsoft p-code** denotes a family of compiler-specific intermediate formats rather than one universal format. Every supported p-code profile SHALL identify exact compiler generation/runtime and opcode definitions with verified decode/encode fixtures; no invented generic decoder. Where only decode is supported, mark backend planned and reject p-code emission.

A BASIC loader invoking `SYS` or `CALL` can be linked to a known address/embedded machine-code segment; lift both into a shared module with explicit address-space/calling contracts. When the target is C#, hardware-specific behavior may require an emulator/device abstraction. When targeting the original machine, preserve raw I/O operations, with timing fidelity selected by profile.

### 18.6 Target output constraints

Emit either (a) a portable equivalent implementation, (b) an idiomatic package with documented/verified runtime helper, (c) target assembly and linker objects, (d) a tokenized PRG, (e) a bytecode module, or (f) a precise failure. The requested target format and dependency mode (native-only, helper allowed, emulator allowed, none) are explicit. For constrained platforms, perform memory-size checks, allocation planning, calling-convention matching, code-size budgeting and required CPU feature checks before declaring output viable.

---

## 19. Semantic recovery, de-optimization and loop reconstruction (SDD)

### 19.1 Purpose and core invariants

Reconstruct a simpler **semantic** operation from compiler-specific or hand-optimized expressions, interleaved instruction streams, loop unrolling, software pipelining and specialized primitives. Optimizations are allowed in *both* directions: high-level → low-level and low-level → high-level. Algorithm recognition is not evidence of the original author's subjective intent; generated diagnostics should say “proven equivalent to X”, not “author intended X”.

All recovered high-level ops preserve a machine-/source-level fallback graph with provenance and proof certificates where possible. Recovery is optional when its cost is worse; the target cost model may reselect the original representation after normalization. Source ordering, memory effects, exceptions, rounding, signedness and lane isolation constrain rewrites.

### 19.2 Recognition hierarchy

| Level | Examples | Technique |
|---|---|---|
| Source-specific | LINQ, JS `map`, Perl `grep`, Python comprehension, PB DRAW | Frontend AST/semantic normalization |
| Expression | shift-add multiplication, min/max bit hacks, XOR swap, rotates, high/low multiply, saturate | SSA matching + range/alias proof |
| Instruction | `POPCNT`, `BSWAP`, PEXT/PDEP, RLWINM, `ADC`, `RBIT` | Typed machine-instruction semantics |
| Graph | Interleaved independent arithmetic chains, register scheduling | Program slicing, def-use DAG isomorphism |
| Loop | Full/partial unroll, loop peeling, Duff's device, vectorized copies | Affine address and induction analysis |
| Dataflow | Software pipelining, loop fusion/fission, reductions, scans | Dependence vectors, recurrence and effect analysis |
| Algorithm | CRC (with parameters), popcount, endian loads, sort networks, matrix multiplication | Parametric semantics+independent oracle |
| Layout | Packed fields, planar/packed pixels, byte/bit shuffle, transposes | Symbolic bit provenance, access-set analysis |
| Binary architecture | Calling convention thunks, compiler-generated stack moves, multiply/divide sequences | ISA-specific lift and shared canonical operations |

### 19.3 Specific recovery contracts

**Shift-add multiplication**: `(x<<3)+(x<<1)` can recover `Mul(x,10)` where integer width, shift count, overflow and signedness are compatible. Return to shifts on targets without fast multiply; do not assume algebra valid for checked overflow or undefined signed C overflow.

**Min/max idioms**: Recover `Min(a,b)`/`Max(a,b)` only if sign extension, comparison, arithmetic overflow and source evaluation count match. Branchless mask selection can map to `Select` and later SIMD blend.

**Swap**: XOR-swap may become `Swap(a,b)` only when aliasing, volatility, distinct addresses and intermediate writes permit it. Same-address XOR-swap is a mandatory negative fixture.

**Interleaving**: Independently scheduled register SSA chains may be sliced into expressions, then recombined into SIMD/SWAR lanes. Do not reorder across effects/traps that constrain observation.

**De-unroll**: For repeated statements with provably affine addresses and equivalent iteration bodies, recover `IterationDomain(start,count,step)` plus body, remainder and tails. Preserve original step-dependent errors and aliasing. Unrolled stores aren't necessarily a loop when intermediate writes are observed.

**Software pipeline**: Identify loop-carried dependence distances and prologue/steady-state/epilogue. Do not collapse a pipeline until overlap of iterations, possible faults and dependency order are proven equivalent.

**Float**: FMA recognition is legal only if source had fused semantics or an explicit permissive accuracy mode. Strict IEEE/x87/MBF operations retain specified intermediate rounding, precision and exception behavior.

**Crypto**: Secret-dependent branches/table accesses cannot be introduced by optimized fallback. Branchless input must remain constant-time where the declared security contract requires it; SIMD/SWAR changes are verified for leaks and memory access behavior, not just values.

### 19.4 Analysis and proof engines

Use pure-expression e-graphs/equality saturation with width- and signedness-typed rewrite rules and limited extraction budgets; SSA DAG reconstruction for interleaving; MemorySSA and alias/escape analysis for effects; affine/scalar-evolution analysis for iteration; bitvector SMT for finite-width idioms; bounded symbolic execution for intricate small basic blocks; translation validation for optimized output where feasible. No `Unknown`/timeout/failing proof may be treated as `Proven`.

Every pass returns `(candidate, proofStatus, obligations, fallback, costEvidence)` and stores a structured optimization report. For transformation that lacks full automatic proof, permit only a specification-driven, previously validated rule with explicitly checked preconditions and rigorous differential/property tests; label proof class accurately. Proof checker should be independent of candidate generator for high-risk transformations.

### 19.5 Loop recovery milestones

- `S1`: recognize straight-line 2/4/8-way fully unrolled pure array loops; emit equivalent `PolyIteration` and re-unroll with target profiles.
- `S2`: recognize partial unrolling and peeled/remainder loops, including constant loops in PB graphics and cipher byte operations.
- `S3`: reconstruct interleaved arithmetic and vectorized motifs; preserve demanded flags.
- `S4`: recognize selected software pipelines, Duff's device and compiler lowering artifacts with proof/evidence.
- `S5`: cross-function and mixed ASM/iteration synthesis subject to call-effects constraints.

Acceptance evidence includes a before/after SSA/iteration dump, target-generated code, an equivalence proof or validity status, source/target differential executions, and target-specific code-size/speed comparisons.

---

## 20. PolyPermutation — bidirectional BitPerm integration (SDD + algorithm contracts)

### 20.1 Existing implementation and migration boundary

`Hawkynt/Hawkynt.github.io/BitPerm/index.html` currently contains the routing UI and JavaScript `BUILDING_BLOCKS` registry, `apply`, `canRoute`, `findRoutings`, `genCode`, `generateAllOperations`, `solveExhaustive`, `analyzeBitMovement`, `verifyOperationReport`, greedy search and CPU cost presets; its README documents 51 tests across 10 sections. Extract these as test-backed **candidate functionality**, not a validated complete solver. Keep the UI working with the existing engine until the replacement passes the same fixtures; then connect it to a versioned Polypiler.Core library/API and retain a rollback path.

`https://programming.sirrida.de/bit_perm.html` supplies additional *external algorithmic references* for butterfly/Beneš networks, PEXT/PDEP, shuffle/unshuffle and multi-stage permutation synthesis; examine licensing before copying any code or tables. Implement independently where necessary; reference ideas/algorithms and cite provenance rather than assuming redistribution rights.

### 20.2 Formal semantic hierarchy

- **`BitPermutation<W>`:** total bijection over W bit positions, exactly one input bit per output and exactly one output per input. Can invert and compose.
- **`BitRouting<W,InW>`:** each output bit is an input bit, optionally inverted, or constant 0/1; permits duplication, dropping inputs and partial/don't-care constrained outputs; not generally invertible.
- **`BitBooleanTransform`**: outputs are Boolean expressions of input bits; includes GF(2) XOR matrices and nonlinear logic; must retain truth-table/circuit semantics beyond routing.
- **`BitOperationNetwork`**: implementation graph that realizes a mapping/transform with registers/temporaries, target instructions, masks and costs; **not** the same thing as semantic mapping.
- **`BitCondition`**: allowed input domain and constrained output bits; don't-care means result unconstrained on specified bits, not undefined source behavior outside its contract.

A compact descriptor may use `OutputBit[i] = Source(j) | NotSource(j) | Const(0/1) | DontCare` plus domain constraints; full Boolean transforms require expression DAG/BDD/AIG or equivalent. Every element records widths, shift semantics, endianness when mapping to bytes, sublane boundaries and evaluation predicates.

### 20.3 Recovery (reverse direction)

1. Extract the pure bitvector computation by SSA slicing. Stop at unmodelled external effects; use exact captured inputs/outputs and widths.
2. Propagate symbolic bit provenance through AND/OR/XOR/NOT, shifts/rotates, byte reversal, bitfield extraction/insertion, `PEXT`/`PDEP`, PSHUFB and target shuffle instructions.
3. For multiply-and-shift bit hacks, use the **actual modular multiply** with carry propagation; never model its semantics as OR of shifted copies. The existing BitPerm `mul.apply` uses such an OR for routing analysis only. Candidate recovery requires symbolic proof/independent execution.
4. Build a bit-routing candidate where every constrained output is a source bit or constant; if any output depends on multiple bits, classify as `BitBooleanTransform` or leave as generic SSA.
5. Prove candidate is equivalent to original under source input constraints (and target-defined behavior). Basis-vector tests reveal candidates only; they are **insufficient proof** for nonlinear arithmetic.
6. Recognize higher-level ops (`ReverseBits`, `ReverseBytes`, `Interleave`, `Deinterleave`, `BitCompress`, `BitExpand`, `ByteShuffle`, butterfly/Beneš network). Keep exact mapping as ground truth.

### 20.4 Canonical algebra and optimization

For a bijection P: `P^{-1}(P(x))=x`; `P₂(P₁(x))=(P₂∘P₁)(x)`; `P(a XOR b)=P(a) XOR P(b)` where operands share the same width/domain; masks and Boolean ops may be pushed through P by permuting constants. For `BitRouting` with duplication or constants, do not assume inversion or distribute transformations without checking the precise operation algebra. Compose adjacent layout changes, propagate arrangement across compatible pure loops and vector operations, and eliminate pack→unpack when external ABI/layout boundaries remain satisfied.

Extend layout propagation to `Vector<T>`, `BitVector`, tensor shape/stride/transpose and planar/packed video/pixel formats; coordinate with memory-SSA so layout changes never reorder observable I/O or alias-sensitive writes.

### 20.5 Synthesis (forward direction)

Candidate instruction grammars: shifts+masks+OR/XOR; rotations; single/dual PEXT/PDEP; byte shuffle (`PSHUFB`, `TBL`); bit reversal (`RBIT`); byte reversal; bitfield extract/insert; RLWINM and similar combine operations; multiplication tricks with exact carry-aware proof; LUT; SWAR; SIMD; butterfly network; Beneš network; GF(2) matrix/Boolean circuit synthesis where needed. Generate **typed Target IR**, not C fragments via string concatenation; backend performs language syntax and target ABI legalization.

Synthesis is deterministic for fixed seed/budget/profile. Search strategies: fast greed, dynamic programming for constrained patterns, Dijkstra/A* over verified candidate-state transitions, bounded SMT/superoptimization for small widths, decomposition/heuristics for larger widths. For multiplication, actual numeric semantics must be checked after the proposed mask/shift combination. An upper-bound search budget cannot justify a claim of global optimality; report `heuristic`, `best-found`, or `optimal-under-grammar` with proof and search bounds. Candidate cost includes instruction latency, throughput, ports/micro-op count where available, code bytes, temporaries/register pressure, table traffic, branch costs and platform constraints. Measured hardware results may supersede estimates only when reproducible and tagged to exact CPU model.

### 20.6 Source-to-source and inverse tests

Test these independently in C#, Rust, C++, PowerBASIC, JavaScript and 6502 where profile allows: identity, reverse, byte swap, halves swap, arbitrary bijection, duplicate source bits, invert selected bits, set constants, output don't-cares, permutation composition, inverse cancellation, PEXT/PDEP, PSHUFB, RLWINM, carry-producing multiply, and 128/256/512/1024/2048/4096-bit software fallback. Widths above native registers are logical aggregates; no false claim of single-register ISA support.

For every synthesized network N representing mapping M, use independent oracle `Evaluate(M,input)` and check `Evaluate(N,input)` across exhaustive 8-bit inputs, appropriately selected exhaustive 16-bit cases, generated/properties for bigger widths and SMT where feasible. Check all declared input-domain constraints. For a proven bijection, test inverse and composition. Verify generated code compiles/runs in actual target toolchain, not merely that its emitted text resembles expected instructions. Compare with current BitPerm browser results; disagreements are investigated, not resolved by changing independent expected mappings.

### 20.7 Prohibition of unsafe normalization

- Do not automatically rewrite a modular multiply to independent-bit routing when carries can affect selected outputs.
- Do not assume a `PSHUFB` shuffles arbitrary bits when it shuffles bytes with target-specific zeroing/lane behavior.
- Do not assume `PEXT/PDEP` can duplicate an input bit; they implement specific compact/extract/deposit semantics.
- Do not claim 128-bit source ops are automatically legal on an 8-bit 6502 without software lowering.
- Do not claim “optimal” simply because bounded Dijkstra terminated before its search limit; the explored candidate grammar must be complete for the claimed bound.
- Do not conflate “don't care output bit” with externally observable unknown or nondeterministic source state.

### 20.8 Standalone PolyPermutation CLI/API

Minimum machine-readable input: `{width,inputWidth,mapping,requiredOutputMask,inputDomain,targetProfile,objective,allowedPrimitives,proofMode}`. Outputs: exact semantic mapping, candidate typed networks, cost estimates and measured evidence when available, proof result, generated code and runnable tests. `polypiler permute recover` accepts source or machine IR and reports recovered routing/Boolean transformation. `polypiler permute synthesize` accepts a mapping. `polypiler permute verify` verifies a candidate against a reference. `polypiler permute explain` reports why a candidate was chosen/rejected and its limitations.

---

## 21. Machine-code and retro target-specific design (SDD + DDD)

### 21.1 MOS 6502 as both compiler input and output

**Frontend:** support ca65-style or another named 6502 assembler dialect plus raw 6502 machine-code entrypoint. Assemble reference snippets with pinned assembler; lift resulting code with NMOS 6502 CPU-model semantics. Reconstruct register/flag SSA, zero-page allocation identity, program stack writes and status-dependent branches, and recognize known arithmetic/permutation loops. Illegal opcode policy explicit per CPU model. Lift to C#, Rust or PowerBASIC with emulated memory and I/O only as needed.

**Backend:** lower typed arithmetic and memory/iteration IR into 6502 instruction selections with register use and spill planning; choose zero-page temporaries by actual address availability and side-effect constraints. Implement multi-byte arithmetic via ADC/SBC carry chains; 16/32/64-bit arithmetic by helper routines when required; no 6502 multiplication instruction assumed. Emit assembly, symbol table and binary/PRG through pinned assembler/linker; assert code/data/stack and zero-page budgets against target memory profile. Separate `mos6502` generic bare-metal and `c64` runtime profiles (BASIC loader, PRG load address and device map).

Cycle-aware mode may optimize for raster/interrupt timing under a declared timing contract. Normal mode is functional equivalence, not cycle accuracy. Hardware-mapped reads/writes are not ordinary array accesses. Tests include branch crossing, zero-page wrap, decimal mode, flags, stack and self-modifying code negative cases.

### 21.2 8086/x86 integration with PB-Compiler

Use PB-Compiler's established typed IR, x86-16 code generation, near/far pointer address spaces, MBF/x87 oracle and dialect behavior as reference and candidate reuse. A shared Polypiler.Core representation must cover segmented memory, compiler runtime calls and 16-bit ABI while also supporting x64/ARM flat and other address spaces. Do not break PB-Compiler's native output or test harness during adoption. Mixed inline ASM/control flow must be imported as semantic instructions with explicit register and memory effects; target may emit high-level C# or preserve/integrate x86 assembly if supported.

### 21.3 Backend capability graph

Model capabilities as constraints and alternatives, not a monolithic yes/no: `Integer.Wide(bits)`, `Pointer.Native`, `Pointer.Fat`, `Union.Overlay`, `Stack.Memory`, `ByteSwap`, `SIMD.Width`, `MemoryMappedIO`, `Interrupt`, `TargetAsm`, `Runtime.External(symbol)`, `Exceptions`, `Threads`, `GPU`, `NPU`. Legalization rewrites until all IR ops are native, have a verified runtime helper, or produce a structured blocker. Keep track of helper closure, ABI, license, code size and compile/test evidence.

### 21.4 Domain ports and dependency rules

New boundaries: `IBinaryCodec`, `IDisassembler`, `IInstructionSemantics`, `IProgramBoundaryResolver`, `IMachineStateModel`, `IExternalSymbolResolver`, `ITokenizedBasicDialect`, `IAssemblerEmitter`, `IBinaryWriter`, `IEmulatorOracle`, `ITargetCycleModel`, `IPermutationMappingSolver`. Machine-specific modules may depend on canonical IR/effect/type contracts; canonical optimizer must not depend on 6502/x86/ARM opcode enums or source ASTs. Native runtime adapters implement the semantic APIs; they do not redefine them.

---

## 22. Additional required BDD scenarios (executable specifications)

Place each feature in a versioned `tests/features/` suite and bind to NUnit and real target oracles. Every `Then` must be checked by an assertion against execution/effect traces or structural IR proof, not just a substring search of generated code.

```gherkin
Feature: Import machine code without inventing source semantics
  Scenario: Lift a MOS 6502 add-with-carry checksum to C#
    Given an assembled NMOS 6502 routine with documented entrypoint and memory map
    And the input and output buffer contract is pinned
    When I translate it to C# with --verify
    Then generated C# compiles and executes
    And outputs and observable memory effects match a 6502 emulator
    And carry-flag-dependent behavior matches for boundary inputs

  Scenario: Keep a hardware port write observable
    Given an x86 routine writes a value to an I/O port
    When I import it into PolyIR
    Then the I/O write has a device effect and its original order is retained
    And a target without a suitable port/device contract rejects or emulates it

  Scenario: Reject unresolved indirect control flow
    Given a native binary has an indirect jump whose targets cannot be enumerated
    When I request standalone source emission
    Then the compiler reports an unresolved control-flow dependency
    And does not label the artifact verified
```

```gherkin
Feature: Recognize runtime APIs without importing entire standard libraries
  Scenario: MSIL PopCount translates to a PHP helper or native expression
    Given a C# checksum assembly uses a recognized BitOperations.PopCount overload
    When I translate the MSIL method to PHP
    Then the IL stack and call signature are preserved in semantic IR
    And the canonical IntMath.PopCount operation is used
    And executable PHP matches the original .NET reference results

  Scenario: Preserve unrecognized BCL symbol
    Given an IL method calls a library method without a semantic mapping
    When I translate it to PHP without runtime-bridge permission
    Then the exact library identity and signature appear in diagnostics
    And the generated artifact is not marked executable or verified

  Scenario: JVM bitwise algorithm translates to Delphi
    Given a verified Java class file implementing a numeric array transform
    When I translate a selected method to Delphi
    Then the Delphi compiler accepts the output
    And test vectors match the JVM, including signed shifts and overflow
```

```gherkin
Feature: Decode and re-emit historical BASIC
  Scenario: C64 BASIC PRG to C#
    Given a valid tokenized BASIC V2 PRG with PETSCII string and line metadata
    When I translate its supported numeric routine to C#
    Then the original token stream can be decoded without losing token boundaries
    And the C# result matches emulator output under the declared dialect profile

  Scenario: Modern source to tokenized C64 BASIC
    Given a fixed-resource C# integer procedure within BASIC V2 capabilities
    When I emit a C64 PRG
    Then a separate PRG decoder accepts line links and tokens
    And an emulator executes it with the expected output

  Scenario: BASIC loader calls embedded 6502 code
    Given a BASIC SYS entry into a known machine-code segment
    When the mixed program is lifted
    Then both BASIC and 6502 effects are visible in the shared module
    And hardware effects are preserved or the target explicitly requires an emulator

  Scenario: Unknown Microsoft p-code version
    Given a file that cannot be matched to a verified p-code format
    When I import it
    Then it fails with an unknown-format diagnostic
    And it does not guess a C64/GW-BASIC token table
```

```gherkin
Feature: Recover inline assembly as ordinary program semantics
  Scenario: Remove all assembly from a portable bit swap
    Given a source method uses recognized x86 MOV and BSWAP instructions
    When I translate that method into pure C#
    Then the assembly operations are semantically recovered as ReverseBytes32
    And generated C# contains no inline assembly or native interpreter dependency
    And differential tests match the original source

  Scenario: Entire method may become target assembly
    Given a mixed PowerBASIC and inline x86 arithmetic method
    And its operations can legally lower to the x86 target
    When I request target assembly
    Then the output is a full assembly procedure with a verified calling convention
    And no missing high-level runtime effects are silently dropped

  Scenario: Unknown assembly opcode with external memory effects
    Given an inline ASM block has unrecognized instructions and arbitrary memory writes
    When SSA optimization crosses that block
    Then affected alias and range facts are invalidated
    And unrelated pure computations may still be optimized
    And incompatible target backends report the unresolved opcode/effect
```

```gherkin
Feature: De-optimize optimized source into canonical operations
  Scenario: Recover multiplication from interleaved arithmetic
    Given two independent calculations implement multiply-by-ten with shifts and adds
    And their low-level instructions are interleaved
    When expression reconstruction runs
    Then two independent Multiply(x,10) operations are recognized
    And target code may re-interleave or vectorize them
    And a verifier confirms equivalence with wrapping and shift semantics

  Scenario: Reconstruct a fully unrolled pure loop
    Given four equivalent pure element transforms access consecutive byte positions
    When loop recovery runs
    Then a counted PolyIteration body with four iterations is identified
    And generated code can choose loop, scalar unroll or vector form
    And all array access effects and exception behavior remain equivalent

  Scenario: Do not replace aliased XOR swap
    Given an XOR-swap pattern may reference the same storage location twice
    When a Swap rewrite is considered
    Then the non-alias proof fails
    And the original sequence is retained

  Scenario: Do not reassociate strict floating arithmetic
    Given a strict source computes a multiply and a separately rounded add
    When FMA recognition runs
    Then an FMA replacement is rejected unless target semantics are equivalent
```

```gherkin
Feature: BitPerm mappings recover and synthesize bidirectionally
  Scenario: Recover a pure bit permutation
    Given a bitwise source implementation uses masks, shifts and rotates
    When I recover its semantic mapping
    Then every output bit is traced to exactly one input bit
    And inverse composition equals identity
    And mapping execution matches the original for all 8-bit inputs

  Scenario: A multiplier has carry-dependent output
    Given an operation contains a modular integer multiplication
    And a candidate bit-routing model ORs shifted copies
    When the mapping validator compares them
    Then any carry-caused counterexample rejects the routing equivalence

  Scenario: Synthesize for a CPU without native PEXT
    Given a mapping was represented by a PEXT/PDEP construction
    And the target CPU has no BMI2 instruction capability
    When I synthesize an equivalent representation
    Then legal shifts, masks, rotations or software helpers are emitted
    And target tests match the same mapping oracle

  Scenario: Compose away inverse permutations
    Given two consecutive inverse bit permutations have no externally visible boundary
    When the layout optimizer runs
    Then the pair is removed
    And all source-level observable effects are preserved

  Scenario: Do not claim global optimality after bounded search
    Given the permutation solver stops at the configured state or time limit
    When it reports the best candidate
    Then it marks the result best-found or heuristic
    And it does not claim proven global optimality

  Scenario: Rust to 6502 uses a verified software lowering
    Given a Rust numeric kernel uses operations unavailable as 6502 instructions
    When I compile it for the MOS 6502 target
    Then the backend generates supported instruction sequences and required helpers
    And a 6502 emulator matches the Rust reference outputs
    And the emitted program respects code, RAM, zero-page and stack budgets
```

---

## 23. TDD, differential verification, cost and quality gates for added capabilities

### 23.1 Independent oracles

Use **independent implementers/engines** where practical: native executable vs generated source, PB-Compiler oracle vs Polypiler.Core, Cipher original Node harness vs generated C#, VICE or another pinned C64 emulator vs regenerated PRG, pinned 6502 emulator vs machine-lifted code, CLR/JVM/WASM runtime vs bytecode translation, BitPerm raw mapping evaluation vs generated network, formal bitvector solver vs compiler rewrite. Never use Polypiler.Core's own optimized output as the sole expected result.

For machine code, compare not only function outputs but a configurable observable-state trace: buffer byte ranges, flags/registers when exposed, memory writes, I/O port transactions, external calls, traps, returns and target CPU cycles when in timing-sensitive mode. A raw machine memory trace is too strict for high-level targets unless declared observationally equivalent; compare at the semantic boundary, and verify raw trace for effects that must be preserved exactly.

### 23.2 Required test categories

| Category | Positive cases | Negative cases | Runner |
|---|---|---|---|
| Instruction semantics | All official opcodes/modes and representative flags | Invalid/undocumented opcodes, mode/variant mismatch | CPU oracle/emulator |
| Stack-bytecode lifting | MSIL/JVM/WASM stack validity, exceptions, imports | Bad stack maps, unsupported opcodes or imports | Real CLR/JVM/WASM runtime |
| Token codecs | Valid PRG line chain, quoted string, `REM`, `DATA`, PETSCII | Corrupt links, wrong dialect, truncated tokens, invalid addresses | Independent decoder/emulator |
| Inline assembly | Mixed source/ASM dataflow, recognized bit hacks | Unknown clobbers, volatile MMIO, indirect control flow | Real assembler + runtime oracle |
| Memory effects | Private arrays, disjoint memory, explicit addresses | Unknown pointers, aliasing, stack metadata, self-modifying code | IR interpreter + execution trace |
| De-unrolling | 2/4/8 repeated pure operations and peeled loops | Boundary exceptions, overlapping writes, volatile iteration side effects | NUnit + runtime differential |
| BitPerm recovery | Rotate, reverse, compress/expand, layout composition | Nonlinear carries, invalid mapping, duplicate bits treated as bijection | Exact mapping evaluator + SMT |
| BitPerm synthesis | 8–4096 bit logical widths, legal target profile | Missing instruction, wrong sublane, wrong endianness | Target compiler + runtime/emulator |
| Costs | Deterministic, profile-specific ranking | “optimal” without exhaustive proof, machine not matching cost preset | Bench harness + structured report |
| Licensing | Source/model provenance and notice propagation | Restricted model redistribution, invalid third-party attribution | License scanner and review gate |

### 23.3 Property-based/metamorphic tests

- Valid bijective P: `Inverse(P)(P(x)) == x` for every valid input; `Compose(P,Identity)==P`; routes with duplication are explicitly excluded from inverse property.
- Pure `BitRouting`: output basis provenance is structurally correct; for any candidate numeric trick with multiplication, recheck all relevant multi-bit combinations and SMT equivalence.
- Width-specific arithmetic: `WrapN(a+b)` agrees with exact modular oracle; `AddWithCarry` reflects carry-out and flags per ISA; signed and unsigned shifts use precise profile behavior.
- Source→IR→target output and source→IR→same-source output agree for supported algorithms; target→lifted IR→source round trips for selected binary snippets.
- Loop reconstruction preserves final buffers and observable memory effects for randomly generated valid alias patterns; where proof does not hold, rewrite must not fire.
- Type narrowing preserves source API type and external ABI/layout; generate hostile alias escape cases.
- Determinism: same input SHA, compiler hash, profile, seed and budget must generate the same target IR and files, excluding explicitly documented nondeterministic tool metadata.

### 23.4 Fuzzing, proof and minimization

Fuzz parsers/decoders with corpus mutation and structure-aware generators. Fuzz SSA rewrites and effect regions against a reference interpreter/SMT model. Use bounded instruction-sequence generation on 6502/x86/ARM and compare reference emulators. Minimize every mismatch into a reproducible test fixture with preserved upstream source hashes. A failed or timed-out proof triggers fallback or a negative diagnostic, never success. Tests for arithmetic bit hacks must deliberately include values where carries overlap.

### 23.5 Performance and solver metrics

For each target profile record estimated latency/throughput, generated instruction and byte counts, source/package size, register pressure, memory accesses, number of temporaries, peak RAM/stack, compile time, target runtime, search budget/state count and proof duration. `--opt speed|size|memory|balanced` applies to native and retro targets, but costs are derived from the actual CPU model or benchmark evidence. No stable benchmark number is asserted without a run on the pinned hardware/emulator config. For 6502, count cycle differences due to taken branches and page crossings where benchmark contract requests it; general functional verification does not imply cycle accuracy.

### 23.6 Release gate states

- `parsed`: source/bytecode accepted into a frontend.
- `lifted`: input semantics represented, verifier passes.
- `recognized`: candidate canonical intrinsic/iteration/permutation found.
- `proved`: transformation has accepted proof class and checked preconditions.
- `legalized`: target IR contains supported constructs or identified permitted helpers.
- `emitted`: files produced, not yet validated.
- `compiled`: real target compiler/assembler passed.
- `executed`: produced artifact ran in declared runtime/emulator.
- `verified`: independent oracle and observable-effect contract passed.
- `performance-validated`: benchmark evidence attached.

A requested language/CPU pair SHALL be advertised only at its highest **actually achieved** state. For P0/P1 planned platforms, listing them as a target is not equivalent to support. Compatibility matrix entries are generated from CI evidence, never handcrafted marketing claims.

---

## 24. Agent work packages for binary, semantic recovery and BitPerm

The original P0–P6 stages remain the backbone. The following parallel **gated epics** extend them. They are dependent on verified typed SSA, effect model, reference interpreters, CLI and cross-toolchain runners; do not attempt all of them in the first sprint. Each work package becomes a small PR/issue with spec, positive and negative BDD, failing-then-green tests, emitted artifact, proof/effect report, correct docs and license review.

### F0 — Shared machine/effect semantics (mandatory foundation, alongside P1)

- [ ] Define `MachineIR`, flags/register SSA, memory-region tokens, hardware I/O and external-call contracts with stage verifiers.
- [ ] Extend canonical SSA with `OpaqueInstruction`, `ExternalOperation`, `DeviceIO`, `MachineTrap`, `CodeWrite`, `RuntimeDependency` and source/ISA provenance.
- [ ] Implement machine-state interpreter for a minimal approved instruction subset; add explicit target legalization outcomes and stable diagnostics.
- [ ] Build negative regression suite for unknown stack/MMIO writes, self-modification and unknown API calls.

**Exit:** Mixed typed SSA and machine effects verify correctly; no unknown operation gets dropped or accidentally emitted for an incompatible target.

### Q0/Q1/Q2 — BitPerm migration (P2–P3, independent vertical slice)

- [ ] `Q0`: pin BitPerm SHA; export its mapping fixtures, 51 documented test cases, building-block registry and CPU preset values; audit exact executable test count and fix test harness CI integration.
- [ ] `Q0`: extract mapping, constants, inverted bits, don't-cares, duplication and width contracts into pure `Polypiler.Permutation` model. Preserve old UI independently.
- [ ] `Q1`: implement canonical mapping evaluator, typed candidate networks, greedy + bounded Dijkstra, shift/rotate/PEXT/PDEP/shuffle/LUT primitives and C# target; run exhaustive 8-bit and SMT equivalence tests.
- [ ] `Q1`: correct multiply routing by separating OR-based *possible landing* analysis from exact modular arithmetic with carries; include counterexample tests.
- [ ] `Q1`: reverse-recognize at least a real BitPerm shift/mask mapping, rotate, byte swap, a verified multiplication-based routing; re-synthesize to C# and Rust.
- [ ] `Q2`: implement target profiles and typed lowering for 6502, PB, x86-64 and ARM profiles; add butterfly/Beneš candidate families and optional solver-assisted synthesis; preserve exact cost/proof reporting.
- [ ] `Q2`: reconnect the BitPerm browser UI to shared engine/API after verified parity; expose rollback switch. Retain original UI experience, not force a CLI-only replacement.

**Exit:** `BitPerm mapping → PolyIR → target code → recover mapping` round-trip passes on real tools; non-bijective cases don't invert; benchmarks/optimality statements are correctly scoped; existing BitPerm tests remain green.

### B1/B2 — Stack and portable bytecode frontends/backends (after P2)

- [ ] `B1-MSIL`: real IL reader+metadata resolution; stack-to-SSA; recognized BCL map; `MSIL→PHP` executed demonstration; IL emitter+round-trip subset.
- [ ] `B1-JVM`: classfile reader+stack maps; JVM shifts/overflows, invokes and exceptions; `JVM→Delphi` executed demonstration; classfile emitter+round-trip subset.
- [ ] `B2-WASM`: typed WASM stack+linear memory+trap semantics; `WASM→Rust` executed demonstration; WASM emitter+validator/runtime check.

**Exit:** Each declared input/output format has executable verification in both directions; unknown runtime imports fail or use explicit allowed helpers.

### R1/R2/R3/R4/R5 — Historical source and CPUs (after P2 and F0)

- [ ] `R1`: C64 BASIC V2 tokenizer/detokenizer; PRG loader/writer; PETSCII/`REM`/`DATA` correctness; emulator roundtrip with user programs that do not require device emulation.
- [ ] `R2`: MOS 6502 decoded/typed lifting, instruction semantics, flags, stack/zero-page model; `6502→C#` differential test; `Rust→6502` via assembler+emulator, deterministic buffer contracts and memory budgets.
- [ ] `R2`: mixed C64 BASIC+`SYS` reference linked to 6502 code; preserve device and executable-memory effects.
- [ ] `R3`: GW-BASIC tokenized files, dialect-specific numeric encodings and oracle; optional token emission if verified.
- [ ] `R4`: 65C02/65C816/Z80/8080 support where individual CPU semantics and toolchains are verified; source and target independently gated.
- [ ] `R5`: concrete Microsoft p-code dialect inventory, opcode reference documentation and samples, then one verified decoder and subsequently writer. If evidence is insufficient, block with clear unsupported-version diagnostics.

**Exit:** C64 PRG→C# and C#→PRG run; 6502→C# and Rust→6502 run; no general p-code claim based on BASIC token parsing.

### B3/B4 — Native machine-code lifting (after F0 + P3)

- [ ] `B3-x86`: decode x86 mode and flags, caller-specified ABI, memory effects, relocations; `x86→C++` executable checksum/bit-permutation fixture.
- [ ] `B3-x64`: x64 ABI, partial registers, pointer/provenance, stack/register SSA; `x64→Python` numeric-array routine with same fixed-width results and observable buffer effects.
- [ ] `B4-ARM`: ARM32/Thumb and AArch64 distinct profiles, decoded flags/conditional instructions; `ARM→PowerBASIC` freestanding checksum with ARM emulator oracle.
- [ ] Add x86/x64/ARM assembly writers/backends for verified supported instructions/functions; target binary execution must be independently tested. Unsupported whole-program dynamic linking/OS behavior stays explicit.

**Exit:** Required showcase paths compile and execute and match native/emulated oracles. Instruction coverage is reported per architecture/version, not advertised as full binary decompilation.

### S1–S5 — Algorithmic recovery and de-optimization (after P2)

- [ ] `S1`: SSA slicing and affine unroll recognition for 2/4/8 statements; direct high-level loop or vector IR after proof.
- [ ] `S2`: source-specific bit hacks (`min`, `max`, `swap`, `mul`, rotate, PEXT/PDEP, bit reversal) with exact width/range/effect predicates.
- [ ] `S3`: interleaved arithmetic DAG recovery; partial loop unrolling and independent-chain vectorization.
- [ ] `S4`: software-pipeline/peel/epilogue recovery and representation/layout optimization under memory ordering proofs.
- [ ] `S5`: mixed ASM+source algebraic fusion and high-level↔ASM output; capability and side-effect diagnostics.

**Exit:** Every recovered semantic intrinsic has a positive fixture, unsafe counterexample, proof/certified rule, runtime differential test and emitted code that supports the target option.

### End-to-end priority and protected migration

**Agent run order for fastest user-visible maturity:** P0 corpus/test harness → P1/F0 core SSA and effects → P2 actual JS Cipher→C# and PB→C# → Q0/Q1 BitPerm→C# and reverse → P3 Kotlin Deinterlacer→Perl plus Rust/C and proper NCNN adapter → R1/R2 6502 and tokenized C64 BASIC → B1/B2 MSIL/JVM/WASM → B3/B4 native x86/x64/ARM → Q2/S1–S5 optimization breadth → P4/P5 other languages/target profiles → P6 gradual upstream replacement. P4 and P5 may run in parallel once their required core contracts are stable; no milestone can mark an unsupported path verified.

**Required immediate issue backlog (create actual issues with test-based descriptions):**

1. Pin and license-audit all source repositories, including `BitPerm/index.html` and MediaDroyd `Deinterlacer.kt`/notices.
2. Version language/target/binary/effect/bit-permutation schema and publish a generated capability report with no fake support statuses.
3. Implement core SSA/effect verifier and an interpreter oracle with machine effect tracing.
4. Complete the real JS Cipher→C# and PB→C# vertical slices with runnable generated outputs.
5. Port BitPerm mapping + solver under a pure API; add carry-collision negative cases and formal verification.
6. Implement bit-permutation recognition from C# and JS and generate C#/Rust/PB target code.
7. Implement a verified C64 BASIC token codec plus standalone C# runner comparison.
8. Implement complete supported NMOS 6502 instruction semantics and emulator differential fixture, then Rust→6502.
9. Implement a mixed inline ASM fixture, including an unknown external call and MMIO store that must survive.
10. Implement MSIL→PHP and JVM→Delphi executed pipelines with minimal known API mapping.
11. Implement x86→C++, x64→Python, ARM→PB numeric kernels (with ABI and emulator proofs).
12. Add source-level de-unroll / instruction interleaving recognition with explicit negative alias/effect cases.
13. Reconnect BitPerm UI and opt-in PB-Compiler/Cipher integration only when parity gates pass.

### 24.1 Project-wide definition of done

A PR is complete only when: production code exists; normative spec updated; at least one positive and one negative behavior scenario automated; NUnit/core coverage meets ≥80% for newly added logic; actual target compiler/assembler or interpreter executes output; independent oracle and effects match; vulnerability/license and provenance checks pass; optimization PR includes measured benchmark or explicit no-performance-claim rationale; `dotnet build`, `dotnet test`, relevant foreign toolchain tests and CI pass; reviewer approval recorded; feature branch history linear; no direct default-branch commits; no secrets, unlicensed weights or copyrighted upstream code accidentally redistributed. A cross-language pair remains **unsupported or experimental** until its actual acceptance gate is satisfied.

### 24.2 Agent reporting format

After each PR, provide `PR/branch + commits`, source corpus SHA and file, touched spec/IR modules, language/CPU profile, unsupported features, verification state (`lifted`, `proved`, `compiled`, `executed`, `verified`), commands actually executed, toolchain versions, test counts/coverage, before/after semantic graph for optimization work, performance results when measured, license notes, blockers and next dependency. Do not promise work outside the agent's current invocation or imply a background process is running.

---

## 25. Resource scopes, deterministic cleanup and high-level control-flow recovery (normative SDD)

### 25.1 Recover and lower resource scopes bidirectionally

Polypiler.Core SHALL model acquisition, deterministic release, `try/finally`, exceptional/nonlocal exits, `using` statements/declarations, `await using`, C++ RAII and `defer` as semantic operations with explicit cleanup edges and source dialect policies. A common `ResourceScope`/`CleanupRegion` representation records acquisition evaluation, resource type/identity, null and conversion behavior, cleanup action, order, cleanup-exception effects, lifetime, escape/alias facts, and ordinary/exceptional/suspended exits. Scope recovery from generated code is proof-driven and optional; `try/finally` containing `Dispose()` is **not** automatically C# `using`.

**Backward:** Recognize compiler-generated `try/finally` disposal and related patterns from C# source, MSIL or lifted code only if acquisition and disposal semantics match. Use source spans or statement boundaries when required; preserve handler/cleanup order and the last exception that actually propagates. Do not move/discard an effectful disposal call, including when an external callee may inspect state or when disposal throws. Preserve value-type copy, boxing and pattern-based disposal differences.

**Forward:** Lower to target-native C# `using`, C++ RAII, Rust ownership/`Drop` where equivalent, or explicit cleanup CFG and generated helpers for BASIC/C/assembly. Rust's `Drop` on scope exit is not automatically equivalent to C# exception-initiated disposal; evaluate unwind/panic and destruction contracts. Async disposal is a suspendable cleanup action, not equivalent to synchronous `Dispose`. Nonlocal `return`, `break`, `continue`, `goto`, exception, cancellation and coroutine suspension need exact cleanup semantics.

### 25.2 Tests and feature support

BDD scenarios SHALL cover normal completion, early return, nested reversed disposal, null resource, failed acquisition, cleanup throwing, exceptional body, byref/captured resource, dynamic conversion, value-type disposable, pattern-based `Dispose`, `await using`, and source-recovery false positives. NUnit and source/runtime differential oracles required. For an output lacking native exceptions, either synthesize a verified exception/cleanup mechanism or reject the specific semantics; do not claim to preserve exceptional paths while omitting them.

## 26. LLVM IR import/export and compiler ecosystem integration (normative SDD/DDD/TDD)

### 26.1 Scope and boundaries

Polypiler.Core SHALL implement **both** an LLVM IR frontend (`.ll` text, `.bc` bitcode) and backend (verifier-compliant LLVM modules). These are first-class interchange formats and separate from PolyIR's canonical semantic identity. The existing PB-Compiler `Ir/LlvmEmitter.cs`, SSA verifier, and `docs/IR.md` are candidates to extract/adapt. PB-Compiler already documents LLVM emission for modern targets and a separate in-house native x86-16 backend; do not rebuild or conflate them.

LLVM IR is lower-level than source language semantics. It may lose ownership, `using`, class hierarchy, generic source shape, original exception policy and source type identity. Polypiler.Core SHALL recover such features when proven, preserve external dependencies and ABI information when unknown, and never assume textual/binary IR re-import is lossless source reconstruction. PolyIR -> LLVM may need a minimal runtime closure; LLVM -> PolyIR may retain opaque intrinsic/call nodes and source metadata as evidence. LLVM `undef`/`poison`/UB semantics MUST NOT silently become deterministic source-language values.

### 26.2 Frontend obligations

- Pin a supported LLVM major/version range and toolchain (`llvm-config`, verifier, `llvm-as`, `llvm-dis`, `opt`, `llc`), with manifest, version-check diagnostics and reproducible fixtures. Text and bitcode import supported independently; LLVM bitcode compatibility varies by version.
- Import typed functions, blocks, phi and CFG, integers of arbitrary bit width, vector/aggregate types, data layout, address spaces, globals, aliases, calling conventions, function/parameter/result attributes, alignment, overflow flags, target triple, linkage and visibility.
- Accurately model `sret`, `byval`, `inalloca` where present, indirect calls, varargs, inline assembly, atomics, volatile memory, fences, exceptions/invoke/personality/landingpads or funclets, traps, TLS, COMDAT, target-specific and generic intrinsics, `undef` and `poison`, metadata, debug spans and source provenance.
- Map known LLVM intrinsics and pure patterns to canonical IntMath/FloatMath/Memory/PolyPermutation/PolyIteration IR only after proving equivalence. Preserve or reject unmapped operations according to the external-effect contract and requested target; never erase them.
- Native library calls appearing in LLVM IR remain first-class external calls. Retain symbol, calling convention, ABI, dependencies, memory effects, exception/fault behavior and platform constraints.

### 26.3 Backend obligations

- Emit LLVM-valid typed SSA with correct target triple, `DataLayout`, calling convention, symbol visibility, pointer address space and ABI attributes. Re-run LLVM verifier after every emitted module.
- Legalize Polypiler.Core-specific numerics (MBF, Posit, custom float80), semantic resource scopes, dynamic shapes, runtime object models and unusual pointer regions explicitly. Emit verified target helpers when needed; unsupported cases produce capability diagnostics.
- Preserve FP strictness/rounding and signedness interpretations through operation and conversion semantics. LLVM integer IR types are signless, so signed/unsigned arithmetic remains encoded on operators, not by inventing separate LLVM integer types.
- Require generated `.ll` -> `llvm-as` -> `opt` / `llc` -> native linking/execution, and independently compare outputs and observable effects against original fixtures. Run `.bc` -> `llvm-dis` -> import round-trip checks with ABI and effect invariants; textual identity is not required.
- No claim that LLVM supports 8086 real-mode generation. Use PB-Compiler's dedicated x86-16 selector/assembler unless a separately verified LLVM-to-x86-16 bridge is introduced.

### 26.4 Supported external compiler frontends — precise claims

- **Clang:** C, C++, Objective-C/C++, OpenCL and documented CUDA support can supply LLVM IR; prefer Clang AST/semantic import when high-level type/object semantics are required (LLVM IR may erase them).
- **Flang and other LLVM-based projects:** accept their emitted LLVM IR only when toolchain and runtime dependencies are validated. Such ingestion does not imply native support for complete source-language standard libraries.
- **GCC:** GCC frontends do **not** natively emit LLVM IR. GCC uses GENERIC/GIMPLE/RTL. Its languages (e.g., Ada, D, Go, Modula-2, Rust variants) are *not* automatically Polypiler.Core frontends by enabling LLVM. A future GCC-specific plugin/bridge may export an intentionally defined semantic subset to PolyIR or LLVM, with GCC version pinning and independent validation; GCC backend object code may be lifted with reduced semantics as a separate fallback.
- An LLVM or GCC frontend is usable as an **import path** only once actual output is imported, legalized, compiled, executed and compared with source/toolchain oracles. Installed compiler availability does not equal verified frontend support.

### 26.5 New work packages

**L0 — LLVM contracts and existing-code audit (after P1):** inventory PB `LlvmEmitter`, LLVM type/ABI coverage, existing emitter tests; write `specs/llvm-ir-import.md`, `specs/llvm-ir-export.md`, version/profile policies, negative test fixtures and ADR. Document source semantic losses and a capability manifest.

**L1 — LLVM importer (after P2):** support restricted but executable arithmetic/control-flow/arrays/struct/pointer modules; independently import `.ll` and `.bc`; reconstruct PolyIR; compile to C# and Rust; test actual Clang-produced `.bc` and PB-produced `.ll` without source-code reimplementation.

**L2 — LLVM exporter (after P2; reuse PB emitter):** generate and verify LLVM modules from C#, JS and PB numeric algorithms; execute via an LLVM target toolchain and prove differential equivalence. Expand intrinsics, exception support, attributes, vectors and foreign ABI gradually. Verify `.ll` and `.bc` round trips.

**G1 — GCC integration feasibility (after L1, research gate):** inspect GCC plugins/export methods, licensing/version compatibility and language frontends. Only implement a GIMPLE/GENERIC bridge if incremental independent tests show it safely preserves the requested semantics; otherwise keep a clearly documented unsupported status. Do not claim GCC-frontends-for-free.

### 26.6 LLVM acceptance BDD

```gherkin
Feature: LLVM bidirectional interchange
  Scenario: Clang C checksum bitcode to C#
    Given a pinned Clang-built LLVM bitcode checksum with its original C oracle
    When I translate it from LLVM bitcode to C#
    Then the generated C# project compiles and executes
    And all known-answer vectors and error/effect traces match

  Scenario: C# numeric method to runnable LLVM bitcode
    Given a supported typed C# numeric method
    When I export LLVM IR and assemble it to bitcode
    Then llvm-as and the LLVM verifier accept the module
    And compiled native output matches the C# reference

  Scenario: Unknown LLVM intrinsic
    Given imported LLVM IR with an unsupported target intrinsic that affects memory
    When the target cannot represent or bind that intrinsic
    Then translation fails with an explicit symbol, ABI and source-location diagnostic
    And no success-status output is published

  Scenario: LLVM does not imply GCC frontend support
    Given a GCC frontend producing only GCC-specific GIMPLE
    When a user requests LLVM import without a compatible bridge
    Then the compiler reports the unsupported input format and required adapter
```

## 27. C# → 8086/x86-16 DOS via semantic lowering (normative SDD + BDD + TDD)

### 27.1 Product definition

Implement a **supported, explicit subset** of C# compiled directly to DOS `.COM`/`.EXE` or verified assembly/OMF units via Polypiler.Core's semantic IR, PB-Compiler x86-16 native machine pipeline, linker and runtime. This is not a normal .NET assembly and does not require a CLR if the surviving algorithm and minimal generated runtime are fully lowered. Target profile MUST specify 8086/186/286/386 variant, real-mode ABI, near/far/huge address spaces, stack limits, memory model, DOS services and allowed runtime surface. A successful numeric kernel is not a claim of complete .NET compatibility.

### 27.2 Lowering rules

- Support a first slice: C# integer/bitvector arithmetic, methods, value structs and enums, `for`/`while`/`switch`, fixed arrays, known pure helpers, direct external DOS interfaces. Later add scalarized classes, static initialization, monomorphized generics, delegates, interface devirtualization, optimized `Span<T>`-style views and byte buffers.
- Lower deterministic disposal to exact cleanup edges or synthesize minimal DOS-safe helpers. For supported file/device wrappers, bind or generate DOS interrupt/runtime operations while preserving errors, handles, cleanup timing and non-ownership effects; unknown .NET BCL APIs remain explicit unresolved dependencies.
- Support exceptions, GC, coroutines, reflection and async **only** per independently verified capability profile; do not silently turn off exceptional cleanup, aliasing, arbitrary object identity, scheduling or reachable reflection. Generate minimal runtime when permitted and feasible; otherwise fail with structured diagnostics.
- Use the PB x86-16 virtual-register/instruction-selection/assembler pipeline, preserve segment address spaces and near/far pointer provenance, and validate emitted executable layout and relocation rules. LLVM's modern x86 backend is not a drop-in 16-bit real-mode target.
- Use range/type/layout analysis to narrow 32/64-bit C# operations where proven; otherwise synthesize multiword implementations. Keep effects for `volatile`, external runtime APIs, unknown I/O, inline assembly and arbitrary memory references.

### 27.3 End-to-end tests

```gherkin
Feature: Modern C# algorithms on DOS
  Scenario: Checked C# numeric kernel to DOS executable
    Given a pure C# checksum using fixed-width integers, loops and arrays
    When I compile it for the 8086 DOS real-mode profile
    Then the native DOS executable runs in a pinned emulator
    And generated outputs match the C# oracle at integer and buffer edge cases

  Scenario: Recover a using scope and preserve cleanup on early return
    Given a C# method with resource acquisition, a return and a Dispose side effect
    When I lower the method into a supported DOS cleanup-region representation
    Then exactly one Dispose occurs before the observable return
    And nested resource cleanup order matches C#

  Scenario: Unsupported exceptional disposal semantics
    Given a C# using scope whose body can throw
    And no target exception-unwinding runtime has been implemented
    When I request a DOS executable
    Then the compiler fails with a precise unsupported-capability diagnostic
    And it does not emit a silently changed normal-return-only implementation
```

### 27.4 Work packages

**D0 (after P2):** C# -> PB-style semantic/SSA importer alignment, x86-16 target profile, checksum/bit hack/array test harness using emulator, no unsupported-construct optimism.

**D1 (after D0):** safe deterministic cleanup, known DOS file calls and exceptions where implemented, distinct capability states and generated resource helpers.

**D2 (after D1):** selective class/generic/iterator lowering and runtime synthesis; optimize against PB x86-16 reference emitted code and measure memory footprint/cycles.

### 27.5 Definition of supported

A C# -> DOS profile is `verified` **only** if a genuine DOS executable runs in a pinned emulator and independently matches return values, buffers, external effects, cleanup order and error outcomes for the published subset. Report numeric/algorithmic subset separately from full managed-language semantics; no generic `.NET -> DOS` claim.

## 28. LLVM and DOS phase integration and release gates

- Add **ResourceScope/CleanupRegion** semantics and recovery to P1 control-flow contracts; source-level `using` and recovered `try/finally` get separate positive/negative proof tests.
- Add **L0** to the first IR baseline audit, **L1/L2** as independent, after-P2 LLVM frontend/backend deliverables, and optional **G1** after L1. Distinguish source-level frontends from LLVM/GCC import adapters.
- Add **D0** after C#/PB bidirectional P2 baseline and x86-16 backend validation; D1/D2 incrementally, with emulator gates.
- Extend `polypiler capabilities` to query `from=llvm` and `to=llvm`, LLVM version, target triple, permitted intrinsics and source-level metadata confidence; add C#→x86-16 DOS matrix entries with exact language/runtime/CPU limitations.
- Add test fixtures imported from PB-Compiler's LLVM emitter/x86-16 backend and from Clang-produced LLVM modules; pin upstream SHA and tool versions. Tests use independent source/executable oracles and never infer correctness solely from verifier success.
- Cross-IR transformation proofs must preserve LLVM poison/undef/UB and trap, target data layout, calling convention, memory effects, source and target exception/disposal semantics. Any unknown attribute or effect must not silently disappear.
- For user claims of "more languages", emit the exact frontend toolchain and verified subset; supporting LLVM bytecode ingestion is **not equivalent** to parsing/compiling its originating language from source.

---

---

## 29. PolyInterop — foreign library identity, type layout and ABI legalization (normative SDD)

### 29.1 First-class foreign declarations and APIs

**Requirement INT-001:** A source-level import of a DLL, shared object, static object library, runtime entry point, COM interface, or arbitrary external symbol MUST survive translation as a typed `ForeignImport`/`ForeignCall` unless a verified semantic intrinsic replaces it. The original library name, exported symbol or ordinal, linkage mode, OS, architecture, ABI, visibility, source toolchain, version, calling convention, error behavior and observable effects are preserved. Do not mistake an unrecognized external function for pure code. Do not require translating the body of an external library to translate a caller.

A C header can declare an external function without specifying which DLL supplies it. Resolve the actual library name from import library, linker inputs, executable import table, module manifest, or an explicit user binding, rather than fabricating it. Distinguish static imports, delay-load imports, `dlopen`/`dlsym`, `LoadLibrary`/`GetProcAddress`, COM/Automation, callbacks and dynamic symbol lookup; static rewriting of dynamic loading is permitted only when observable load/failure timing is proven unchanged.

`ForeignFunction` MUST track parameter passing classification, return classification, argument order, caller/callee stack cleanup, register use, varargs ABI, pointer indirection, ownership, lifetimes, aliasing, encoding, exception/signal behavior, thread-local error state and memory effects. Emit target-native binding syntax when compatible: PowerBASIC Windows `DECLARE ... LIB ... ALIAS`, DOS PowerBASIC `DECLARE ... CDECL ... ALIAS` plus supported OMF linkage, C/C++ external declarations, Rust `extern`, C# P/Invoke/LibraryImport, Delphi `external`, Perl/Python FFI, etc. Never conflate DOS `.LIB`/OMF with Windows PE DLL imports.

**Requirement INT-002:** API recognition MAY replace a foreign runtime call with canonical IR only when a versioned semantic mapping proves equivalent behavior (overflow/rounding, mutation, exceptions, encoding, ABI-visible effects, domain constraints). Otherwise preserve the call, generate a verified binding or bridge, or issue a structured unsupported-capability diagnostic. A nominally similar method name is not proof.

### 29.2 Layout schema and synthesized UDTs

**Requirement INT-003:** Define `ForeignLayout` independently from semantic type and internal representation. At minimum include total size, required alignment, field offsets and sizes in **bits**, field/storage encoding, signedness, bitfield placement and allocation scheme, explicit and implicit padding, union overlays, flexible array members, array stride, endianness, pointer width/address space, nested record and base subobject layout, ABI compiler identity/options, and packing pragmas. A type's layout identity is not its semantic identity.

A target representation is legal ONLY when its storage and call ABI match the verified source interface. The layout solver SHALL try, in order, native `pack`/alignment directives, explicit-field-offset UDTs, byte-backed UDTs, byte buffers with generated typed accessors, and native ABI bridge stubs. Inserting explicit padding is not a solution if the target compiler **already inserts excess padding**. If the original ABI demands 5 bytes with offsets `0:u8, 1:u32`, a default 8-byte UDT is invalid even if it has the same field names. A `[u8; 5]`-like storage with unaligned byte accessors and correct array stride may be valid for a by-pointer call, subject to pointer lifetime/alignment constraints.

**Requirement INT-004:** Separate `CompatibleStorageLayout`, `CompatiblePointerContract` and `CompatibleCallingConvention`. A byte-exact record passed **by pointer** may be legal when that record passed **by value** is not, because aggregate register/stack classification differs. Structs returned in registers or hidden return buffers, bitfields, unions, nested aggregates, callbacks, and variadic calls require real toolchain ABI oracles. Do not assume `repr(C)` or `StructLayout` alone establishes ABI compatibility across targets. Unaligned field access must use legal bytewise loads or compiler intrinsics, not invalid references.

`AbiMaterialize`, `AbiBorrow`, `AbiCopyBack`, `AbiRelease`, `AbiBridge` and `AbiCall` are IR-level transformations with explicit lifetime/effect constraints. Layout-dependent allocations are not optional when a foreign callee retains a pointer. ABI-exposed structures/array strides are observable and must not be silently narrowed; internal non-escaping copies may use more efficient representations.

### 29.3 Data marshaling and multi-call fusion

**Requirement INT-005:** Generate a `MarshallingPlan` per foreign boundary: value transformations (`Encode`/`Decode`/`Convert`/`Quantize`), byte order, alignment, packing, pointer allocation, pinning, ownership and cleanup, `in/out/inout` copy-back, callbacks, error extraction and bridge dependencies. Perl may use `pack`/`unpack` to create C-compatible memory and an FFI provider for the actual native invocation; `pack` alone does **not** perform the call or establish pointer lifetime. Support typed arrays and byte-backed records for runtimes without native pointers.

Across multiple compatible foreign calls, analyze effects and escape to preserve packed buffers without redundant pack/unpack. Unpack only demanded fields; invalidate cached fields when an external callee can mutate them. Do not fuse across an observable mutation, retained pointer, error check, callback or synchronization boundary. The layout compiler may call `Polypiler.Permutation` for legal bitfield packing, bit reversal, byte order conversion and channel swizzling.

### 29.4 Exotic numerics and legacy ABI examples

**Requirement INT-006:** Distinguish exact serialized floating-point representations from their parameter/return ABI. `Extended80`/x87 Float80 may use 80 significant stored bits in a 10-byte payload, optionally padded to target ABI size. Rust may synthesize an exact storage type and software operators, but must not blindly pass a 10-byte Rust struct as native `long double` by value. Use a verified compatible ABI or compiled bridge. Target toolchains may define `long double` differently (including binary64); source compiler identity and platform must be pinned.

A VB6 ActiveX DLL commonly exposes COM/Automation interfaces rather than plain C exports; do not assume standard Automation has an 80-bit float parameter. Inspect typelib/calling contract, preserve `VARIANT`, `BSTR`, `SAFEARRAY`, `Currency`, `Decimal`, HRESULT semantics and reference counting; use exact value conversion, byte buffer interface, or bridge only as supported. Respect 32/64-bit in-process compatibility. Different numeric representations must never be silently narrowed at the external boundary.

### 29.5 Required native test fixtures

Create test C/C++/Rust/Delphi and target-compatible BASIC libraries with known symbols, plus a Windows 32-bit DLL when the toolchain is available. Test layout and invocation independently for: 5-byte packed record and 8-byte natural record, 1/2/4/8-byte alignment, unaligned fields, packed bitfields, unions, nested records, array strides, in/out buffers, pointer-retaining callees, by-value return and parameter, callbacks, `cdecl` versus `stdcall`, decorated names and ordinal imports, dynamic loading failure, C varargs, multibyte strings and ownership/error conventions. Compare `sizeof`, `alignof`, `offsetof`, byte snapshots, pointer stability, call traces, return values and mutation.

An external library's missing architecture/compiler or unverified ABI is a blocker for a *verified* result, even if a generated declaration compiles.

## 30. High-level language constructs, object semantics and structural recovery (normative SDD/DDD)

### 30.1 Shared semantic feature registry

**Requirement OBJ-001:** Every frontend SHALL preserve applicable semantic features into canonical representations until proven safe to specialize or lower; every backend SHALL either emit native syntax with matching semantics, synthesize data/procedures/runtime support, or reject with a precise diagnostic. Do not simulate source syntax while silently losing runtime semantics. Recovery from low-level code is permissible when proved; compiler-generated shapes are not sufficient evidence by themselves.

| Construct | Required semantic information | Fallback representation |
|---|---|---|
| Generic types/methods, associated types, trait constraints | Type argument binding, reification, constraints, variance, specialization | Monomorphization; dictionary passing; erased generic metadata when observable |
| Overloaded functions/operators/conversions | Resolution set and ranking, implicit conversions, evaluation order | Early binding, specialized/mangled functions, dispatch trees |
| Named/default/optional parameters | Omitted-argument behavior and default-evaluation context | Explicit arguments and generated overload wrappers |
| C ellipsis, C# `params`, Java varargs, JS rest, Python `*args/**kwargs`, Perl `@_` | Each language's argument collection, conversions, context and ABI | Fixed arity specialization, arrays, tuples, dictionaries or native vararg bridge |
| Classes, interfaces, abstract classes | Object identity, fields, method tables, dispatch, constructors, visibility | UDT/struct + methods + virtual/interface tables |
| C++ multiple/diamond inheritance and Python multiple inheritance | Virtual/nonvirtual base subobject graph, method resolution, pointer adjustment, C3 as applicable | Explicit base records and policy-specific dispatch |
| Anonymous/structural types, records, tuples | Structural vs nominal identity, equality, layout and escape | Synthesized named records, flattened SSA fields |
| Tagged unions, ADTs, enums, flags, discriminated unions | Variant tags, exhaustiveness, underlying representations | Tagged UDTs, integer tags and generated accessors |
| Type-switch and pattern-switch expressions | Runtime type identity, deconstruction, guards, null, exhaustiveness, result/evaluation order | Decision trees and type/tag tests |
| Delegates, closures, lambdas, partial application, currying | Environment capture by value/ref, lifetime, invocation/identity semantics | Lifted functions, context UDT, function pointers |
| Traits, mixins, extension methods, multiple dispatch | Source resolution rules and possible runtime selection | Composition, helper functions, dispatch tables |
| Properties/indexers, events | Accessor execution, event invocation, side effects | Getter/setter/adaptor functions, invocation lists |
| Static/global/thread-local members | Storage lifetime, initialization order, lazy/thread-safe initialization | Module storage, TLS helpers, initialization guards |
| Reflection, dynamic invocation, runtime metadata | Exact observable metadata and discovery capabilities | Generated metadata/dispatch for finite reachable set or runtime provider |
| Exceptions/filters/finally/defer/RAII | Handler selection, cleanup and propagation, suppression and failure order | Explicit handler CFG, error results/stack, cleanup regions |
| `On Error Resume Next` / `On Error GoTo` / `Resume` | Dialect-specific statement granularity, error state, recovery point | Explicit error state and per-statement recovery edges |
| Coroutines, generators, async/await, async streams | Suspension/resumption, state, values passed both ways, completion, cancellation, errors | Resumable state machine + minimal scheduler/iterator protocol |
| Channels, actor/message passing, synchronization | Ordering, isolation, wait/block/atomic memory semantics | Target primitives or synthesized runtime service |
| Tail calls and recursion | Stack behavior and observable recursion/overflow contract | Tail-call optimization, loop, trampoline or explicit stack |
| Compiler-specific annotations and conditional compilation | Compile-time and runtime metadata/effects | Frontend specialization plus retained observable metadata |

### 30.2 Specialization and runtime closure

**Requirement OBJ-002:** Prefer *elimination* of unnecessary object machinery before emulation. Devirtualize dispatch when receiver type is proved; monomorphize generics when closed instantiations are known; scalarize nonescaping objects; resolve overloads before target source emission; collapse constant type switches; remove unused metadata; fuse generators with consumers when evaluation order and laziness allow. Do not eliminate identity, reflection-visible shape, finalizers, weak references, dynamic class loading, `is`/`as` outcomes or foreign ABI layout when observable.

**Requirement OBJ-003:** Emit a **demand-driven runtime closure**, not an entire source standard library. The runtime dependency graph records minimal generated services for allocation/GC/reference counting, virtual dispatch, error unwinding, metadata, resumable execution and scheduling. Report source feature support by semantics and environment, not syntactic keyword recognition. DOS/6502/resource-constrained targets must reject impossible runtime or memory requirements, or offer an explicit separately selected emulation profile.

### 30.3 Coroutine and async recovery

**Requirement COR-001:** `Suspend`, `Resume`, `Yield`, `Receive`, `Await`, `Complete`, `Cancel`, `Dispose` and exceptional unwinding form separate semantic events. Coroutines are not automatically threads, futures or `async` tasks; an iterator need not allocate a scheduler. A frontend recognizing a compiler-generated state machine may recover a `Coroutine`/`Generator` IR node only after proving state/transition, value, lifetime and exception equivalence. The inverse lowering into manual state machines must preserve local captures, re-entry, ordering and cleanup across every suspension point.

A C# `await using` scope may suspend **during cleanup**. A PowerBASIC output may use generated state records and entry functions; a real-mode DOS target has no implicit thread/task environment. Concurrency/scheduling must be explicit in target profiles. A coroutine containing an external unknown call is not automatically movable or synchronous.

### 30.4 Exceptions, error recovery and deterministic resources

**Requirement ERR-001:** Preserve source **statement boundaries** required for classic VB `On Error Resume Next`: an error may resume at the next statement, and `Err` state can be observed. This is not equivalent to a single `try/catch` wrapped around a whole function, nor to blindly ignoring all errors. `Resume`, handler enabling/disabling, nested procedure boundaries and error state inspection must be tested under the pinned VB dialect.

**Requirement RES-001:** `ResourceScope` and `CleanupRegion` are recovered from equivalent low-level `try/finally`, `defer`, RAII and disposal patterns only with proof of acquisition, exactly-once disposal, null behavior, normal and exceptional exits, cleanup order and cleanup errors. `using` is not a general macro for arbitrary `Dispose` calls. Backends without destructors generate explicit control-flow cleanup. Resource identity/pointer ownership and foreign allocations remain tracked across calls and suspension points.

### 30.5 First-class bidirectional tests

Use a real C++ diamond with **both virtual and nonvirtual** inheritance, a Python MRO fixture, a C# generic interface/anonymous-record/type-switch/overload fixture, a Kotlin coroutine/generator fixture, a VB6 statement-level error-recovery fixture where genuine toolchain availability permits, a C variadic ABI fixture and a Rust/Perl generated state machine. Target PowerBASIC/C/Rust/Delphi backends must compile the supported subset and compare dispatch results, object identity when relevant, cleanup order, suspension/resumption traces and error states. Mark exact unsupported semantics as blocked, not silently simplified.

## 31. BDD — foreign ABI, marshalling and language semantics

```gherkin
Feature: Foreign C library to PowerBASIC native bindings
  Scenario: C pointer parameter with tightly packed record
    Given a Windows 32-bit DLL exports a cdecl function with an input/output pointer to a 5-byte packed record
    And the original record contains a u8 at offset 0 and a u32 at offset 1
    When Polypiler translates its C declaration and caller into PowerBASIC Windows
    Then a native LIB/ALIAS declaration refers to the same resolved DLL and exported symbol
    And its pointer parameter addresses exactly 5 consecutive bytes with field offsets 0 and 1
    And the DLL observes identical bytes and mutates exactly the expected fields
    And no target-language padding is introduced in that foreign buffer

  Scenario: Source header has no DLL identity
    Given a C header only declares an imported symbol
    And no linker import library, PE import, manifest or user binding resolves its DLL
    When I request PowerBASIC Windows code generation
    Then I receive an unresolved foreign library diagnostic
    And the compiler does not fabricate a library filename

  Scenario: Packed by-value structure without verified ABI
    Given a compiler ABI passes an unusual packed record by value
    And the destination toolchain cannot express its aggregate argument classification
    When I generate an FFI call without an approved native bridge
    Then translation rejects the call with an ABI-specific diagnostic
    And no executable artifact is claimed as verified

  Scenario: DOS PowerBASIC cannot load a Windows DLL
    Given a C program imports a Win32 DLL
    When I request a DOS PowerBASIC 3.5 target with no bridge
    Then the compiler reports a platform/linkage incompatibility
    And does not rewrite the call as a DOS OMF library call

Feature: Perl marshaling across multiple C calls
  Scenario: One packed buffer survives adjacent native calls
    Given a Perl function owns a value record mapped to an exact C byte layout
    And two consecutive C calls borrow the record without retaining the pointer
    And neither call requires observing an unpacked Perl representation between calls
    When Polypiler optimizes the marshalling plan
    Then it may pack once and reuse the stable buffer across both calls
    And generated FFI calls use the correct function ABI
    And all post-call fields and external side effects match the reference

  Scenario: Callee modifies an aliased field
    Given an external call may write a field through an alias
    When the subsequent Perl code reads that field
    Then stale cached or unpacked values are invalidated
    And the generated code observes the foreign mutation

Feature: Float80 and legacy library compatibility
  Scenario: Rust storage layout is not a by-value Float80 ABI guarantee
    Given Rust can store an exact 80-bit floating payload
    And the DLL requires an x87 extended value in a special return register
    When native Rust aggregate calling semantics cannot be proved compatible
    Then the compiler emits a tested native bridge or an explicit ABI diagnostic
    And it does not pass a 10-byte struct by value as a guessed equivalent

  Scenario: VB6 Automation does not expose an x87 parameter type
    Given a type library exposes a COM Automation method accepting Double
    When a Rust caller holds an exact Float80 value
    Then conversion to Double is explicit and reports the loss policy
    And the compiler does not claim exact Float80 round-trip fidelity

Feature: High-level semantics on procedural targets
  Scenario: C++ nonvirtual diamond to BASIC UDTs
    Given D derives from B and C and both contain distinct A base subobjects
    When I translate the class operations to a BASIC profile without inheritance
    Then the generated UDT and methods preserve both A subobject identities
    And pointer adjustments and dispatched operations match the C++ oracle

  Scenario: C++ virtual diamond to BASIC UDTs
    Given D virtually inherits one shared A through B and C
    When I lower its supported object operations
    Then both base paths refer to the same A subobject
    And construction and dispatch behavior match the reference

  Scenario: C# generic overload and type switch to C
    Given a closed generic numeric class with overloaded methods and type patterns
    When I specialize its reachable instantiations and lower the resolved calls to C
    Then the generated C compiler accepts the implementation
    And dispatch, conversions, type tests and errors match the C# oracle

  Scenario: Kotlin coroutine to Perl state machine
    Given a resumable coroutine with two suspension points and a finally block
    When I translate its supported state graph to Perl
    Then each yield, resume, failure and disposal transition matches the Kotlin oracle
    And cleanup executes exactly as specified

  Scenario: VB error-resume to Rust control flow
    Given VB code uses On Error Resume Next across two error-prone statements
    When I generate Rust code preserving a statement-level error state
    Then the second statement executes after an error in the first
    And observable Err values and subsequent Resume/handler behavior match the dialect oracle
```

## 32. TDD and release gates for PolyInterop and semantic feature recovery

**Tests MUST be written before or alongside implementation and kept executable in CI.** For each exported translator feature, test native syntax and lowering to a lacking-feature target, and (when recoverable) inverse pattern recognition; verify final target behavior, not just the transformed IR graph. Separate `source behavior preserved` from `high-level intention recognized`: the former is mandatory for success; the latter is optional and evidence-based.

- **ABI oracle tests:** compile real C/Delphi/PowerBASIC-compatible fixtures under pinned compilers, print/return `sizeof`, offsets, bytes, alignment and call traces; generate target wrappers, invoke library and compare results. Test by-value and by-pointer independently.
- **Marshalling property tests:** random bit patterns; endian variants; signed zero/NaNs; record padding; nested and overlapping fields; buffer ownership; aliasing and callee-retained pointers. Compare exact bytes and observable results. Verify no invalid unaligned references.
- **Opaque-call negative tests:** unknown DLL, unsupported COM type, wrong calling convention, missing architecture-matched toolchain, unverified native library version and pointer escape. All must fail safely or leave supported opaque dependencies intact.
- **Dispatch/object tests:** virtual/nonvirtual diamond, C3 MRO, overloaded implicit conversion ranking, method hiding, interface identity, vtable call, anonymous-type equality, variant switching, unreachable branches, static initialization order, closure capture and lifetime.
- **Varargs tests:** C ellipsis ABI, width promotions, source and target endian/architecture, C# `params`, JS rest, Python keyword packs, Perl context. Never substitute one for another without a verified adapter.
- **Error/coroutine tests:** every exit edge, nested finally, exception in cleanup, yield/return/cancel, resource captured across suspension, reentrant invocation, `On Error Resume Next` statement boundaries, error state inspection and suppression policy.
- **Generated runtime tests:** demand-only inclusion of helpers, no unused RTTI/GC/scheduler, no invalid downgrade of unsupported platform features, no leakage of one algorithm's internal API into unrelated exports.
- **Differential targets:** at minimum Windows C→PowerBASIC if suitable toolchain available, C→C#, C→Rust, Perl→C via FFI, Kotlin→Perl, C++→C for object lowering, plus real DOS emulator for C#→x86-16. Missing proprietary toolchain is a tracked blocker, not a waived correctness gate.
- **Coverage:** at least 80% reusable logic and at least 90% critical ABI/layout/legalization/error-recovery semantics, plus explicit negative BDD tests for every unsupported behavior. Do not interpret coverage as proof of ABI equivalence.

A feature reports `verified` only when the relevant source oracle and target compiler/runtime produce matching results including exceptions, memory writes, external-call traces, object identities where observable, error propagation and required cleanup. If target runtime cannot preserve the contract, produce diagnostic and mark `unsupported`/`blocked`.

## 33. Agent execution addenda and revised critical path

### 33.1 Additional work packages

**I0 (P0/P1):** create `spec/interop` with foreign identity, API recognition, layout schema, ABI-classification contracts, effect model and byte-level packing oracles. Audit PB-Compiler's OMF/DECLARE support and distinct PowerBASIC-for-Windows syntax. Record compiler/OS/ABI/version exactness. Do not infer DLL names from headers.

**I1 (after P2):** implement C external declaration importer and PowerBASIC-Windows UDT/binding generator for known x86 32-bit ABI; native C oracle DLL and exact packing tests. Provide byte-backed fallback and unaligned accessors. No claimed support without real compiler/interpreter and DLL execution.

**I2 (after I1):** implement Perl/Python FFI marshalling and buffer pinning/lifetime; multi-call pack/unpack fusion, writeback effects and ownership tests. Add Rust FFI generated structs/bridges and explicit Float80 support profiles.

**I3 (after I2):** COM/Automation and rich ABI types, verified x87 by-value bridges, callbacks, varargs and dynamic linking, prioritized by real corpus requirements. Do not force this into the first alpha.

**O0 (P1):** add `TypeShape`, `ObjectSemantic`, `ResourceScope`, `CleanupRegion`, `Suspend` and `ErrorRecovery` contracts, with dialect policies and effect representation. Treat these as metadata+IR capabilities until the first frontend exercises them; avoid speculative full runtime implementation.

**O1 (after P2):** compile-time overload and generic resolution, record/tuple/enum/anonymous-type lowering, first classes/interfaces via UDT + explicit procedure dispatch; verify C#→C/PB/Rust numerical classes.

**O2 (after O1):** diamond inheritance (virtual/nonvirtual), MRO policies, virtual/interface tables and exact identity/pointer contracts; generated class fallbacks for procedural targets.

**O3 (after O1):** coroutines/generators/async into state machines, `using`/RAII, error recovery, source statement boundaries and generated runtime closure. Test Kotlin→Perl coroutine and supported VB→Rust error recovery with pinned oracles.

**O4 (after O2/O3):** reverse-recognition of manually compiled state machines, dispatch tables, alloc/free+finally and interop adapters. Require proof before claiming source-level construct recovery.

### 33.2 Highest-priority demonstration ladder

1. JS Cipher→C# and PowerBASIC numeric→C# on real, pinned source fixtures. Build and execute all target output.
2. Bidirectional C#/JS/PB numeric algorithms with full range/proof/effect reports and BitPerm mapping synthesis.
3. C header/record/DLL→PowerBASIC native binding and exact byte layout on a supported 32-bit Windows test toolchain; if not available in CI, keep a dedicated externally executable acceptance project and mark the scenario blocked.
4. C# generic overload/interface numerical class→C/PB UDT and Kotlin MediaDroyd deinterlacer→Perl.
5. LLVM `.ll`/`.bc` import/export, C#→x86-16 DOS, Rust→6502, 6502→C#, MSIL→PHP, JVM→Delphi, tokenized BASIC round-trips, guarded by their previously defined epics.
6. More languages and platform/runtime compatibility, FFI expansion, dynamic object models and accelerator inference only after evidence of earlier milestones.

### 33.3 Repository and handoff invariants

- Product/repository/CLI is **Polypiler**; avoid user-facing split into separate PolyCore/PolyLib products. The optional catalogue is a subsystem in the same repository.
- The agent shall create or fork the repository only with permissions available and shall **not** overwrite an existing unrelated `Hawkynt/Polypiler` repository.
- Bootstrap with `README.md`, `PRD.md`, `AGENTS.md`, editorconfig, solution/SDK pins, build/test scripts, initial CI and a release-status manifest. License file must reflect an explicit chosen repository license; upstream code/model licensing remains separately enforced.
- Use modern C#, .NET 9 for normal projects, latest stable language features, 2-space K&R style, no global usings, NUnit with classic `Assert.That`, latest compiler where compatible, .NET Framework 2.0 compatibility for backport packages where applicable.
- Keep documentation source-aligned: examples of `polypiler` CLI are a **specification** until a real CLI exists; never imply it is installed. Source implementations are external references until vendored/imported under approved licenses.
- Each PR must include implementation, why, spec/tests, generated test artifacts, reproducibility, performance evidence if claimed, corpus/source SHAs, license notes and supported/blocked feature manifest. `dotnet build`, `dotnet test`, every relevant target toolchain/emulator and CI must pass; maintain linear history, one reviewer, no direct pushes to default branch.
- Goal of the agent's **first** usable alpha is not 'all languages': it is a verified working `polypiler translate` from **real Cipher JS and PB code to C#** with a reusable canonical IR and clear unsupported diagnostics. Subsequent milestones grow the explicit verified support matrix.


---

## 34. Reference links
- [PB-Compiler IR design](https://github.com/Hawkynt/PB-Compiler/blob/main/docs/IR.md)
- [PB-Compiler value facts / O0016](https://github.com/Hawkynt/PB-Compiler/blob/main/docs/optimizations/O0016-value-fact-analysis.md)
- [PB-Compiler structure packing / O0323](https://github.com/Hawkynt/PB-Compiler/blob/main/docs/optimizations/O0323-structure-packing-by-range.md)
- [PB-Compiler numerics](https://github.com/Hawkynt/PB-Compiler/tree/main/PowerBasic.Compiler/Numerics)
- [Cipher source and tests](https://github.com/Hawkynt/Hawkynt.github.io/tree/main/Cipher)
- [Cipher coding-plugin design](https://github.com/Hawkynt/Hawkynt.github.io/blob/main/Cipher/codingplugins/ReadMe.md)
- [FrameworkExtensions numeric/data types](https://github.com/Hawkynt/C--FrameworkExtensions/tree/main/Corlib.Extensions/System/DataTypes)
- [BitBench type reference](https://github.com/Hawkynt/Hawkynt.github.io/tree/main/BitBench)
- [BitPerm implementation and solver](https://github.com/Hawkynt/Hawkynt.github.io/tree/main/BitPerm)
- [Bit Permutations (Sirrida reference)](https://programming.sirrida.de/bit_perm.html)
- [MediaDroyd CPU deinterlacer](https://github.com/Hawkynt/MediaDroyd/blob/main/core/player/src/main/kotlin/com/hawkynt/mediadroyd/player/Deinterlacer.kt)
- [Randomizer](https://github.com/Hawkynt/Randomizer)
- [CompressionWorkbench](https://github.com/Hawkynt/CompressionWorkbench)
- [PNGCrushCS](https://github.com/Hawkynt/PNGCrushCS)
- [MediaDroyd](https://github.com/Hawkynt/MediaDroyd)
- [PB-Compiler x86-16 backend design](https://github.com/Hawkynt/PB-Compiler/blob/main/docs/X86-BACKEND.md)
- [LLVM language reference](https://llvm.org/docs/LangRef.html)
- [LLVM frontend/compiler integration](https://llvm.org/docs/FAQ.html)
- [Clang LLVM-based frontend](https://clang.llvm.org/)
- [GCC GIMPLE/GENERIC architecture](https://gcc.gnu.org/onlinedocs/gcc-14.2.0/gccint/Tree-SSA.html)
- [C# using statement semantics](https://learn.microsoft.com/dotnet/csharp/language-reference/statements/using)
- [PB-Compiler OMF linker / foreign calls](https://github.com/Hawkynt/PB-Compiler/blob/main/docs/LINKER.md)
- [MediaDroyd deinterlacer third-party notices](https://github.com/Hawkynt/MediaDroyd/blob/main/core/player/THIRD_PARTY_NOTICES.md)
- [LLVM bitcode/IR reference](https://llvm.org/docs/LangRef.html)
