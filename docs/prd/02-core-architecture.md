# PRD part 02 — Core architecture

**Status:** original PRD requirements; planned capabilities are not implementation claims.
Original section numbering is retained.

[PRD index](../PRD.md) · [Previous: Product and targets](01-product-and-targets.md) · [Next: Behavior and testing](03-behavior-and-testing.md)

<!-- PRD content begins -->
<a id="s4"></a>
## 4. SDD — specification-driven development and system design

SDD here means **specification-driven development** with a versioned **software/system design document**. Each capability must have a machine-checkable semantic specification, fixtures, architecture decision and executable tests **before** release-quality implementation. Specifications are normative; README examples are not.

<a id="s4-1"></a>
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

<a id="s4-2"></a>
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

<a id="s4-3"></a>
### 4.3 Minimum mandatory core IR

Values/constants; arbitrary-fixed-width integers, signed/unsigned interpretation; floats with format identity; control flow, phi, calls, returns; structured aggregate/address-space memory; load/store/copy; checked/wrapping/saturating arithmetic; comparisons; conversions; explicit exceptions/traps; effect annotations; source mapping. IR verifier checks types, dominance, phi structure, memory bounds contracts, effect/order constraints, and stage legality. Define a stable serialized debugging form, **not** a permanently frozen serialized ABI for all internal nodes.

High-level operations may stay as intrinsics across multiple passes: `PopCount`, `DivMod`, `SinCos`, `Map`, `Filter`, `Reduce`, `Gather`, `Scatter`, `MatMul`, `Conv`, `Pshufb`-compatible `ByteShuffle` and `ParallelBitExtract` must remain recognizable until lowering. Canonicalization may also *recover* them from scalar loops and bit tricks.

<a id="s4-4"></a>
### 4.4 Abstract interpretation / range facts

A shared query facade combines: signed/unsigned intervals, known-zero/one bits, congruences/modulus, finite value sets, path-sensitive relational bounds, scalar evolution, alignment, nullness, float domains, demanded bits, pointer provenance, shape/stride, alias/escape, ownership/lifetime, memory effects and source-language dynamic type/shape sets. Missing facts yield `Unknown`, never fabricated proof.

Track separately: **semantic type**, **logical minimum width**, **storage representation**, **execution register width**, **API/ABI type**. A public `long` may execute in 32-bit registers and temporarily store as bytes only if the required behavior is equivalent and the memory representation is not externally observable.

<a id="s4-5"></a>
### 4.5 Recognition and semantic fusion

Source-language rules normalize comprehensions, LINQ, JS `map/filter/reduce`, Perl `map/grep`, iterators, index loops, pointer loops and image kernels into shared sequence/iteration operations. Typed SSA graph patterns recognize rotation, popcount loops, bit extract/deposit, CRC folding, endian loads, saturating idioms, multiple-precision add/carry, combined division+remainder, copy/fill and matrix kernels. Each recognition emits a proof/obligation and must have counterexamples (different width, overflow, aliasing, side effect, float rounding). Pattern matching never supersedes the source language's observable behavior.

<a id="s4-6"></a>
### 4.6 De-dynamicization and speculative specialization

Support `Proven`, `Contextual`, `Guarded`, `Dynamic` inference states. JS: ToInt32/ToUint32, NaN, holes, getters, proxies, `Number` vs BigInt. Python: arbitrary `int`, protocols, dynamic attributes, descriptors, slices, iterators. Perl: scalar/list/void context, `undef`, numeric/string conversion, references, tie/magic where relevant. When unproven, generate runtime semantics or emit a precise unsupported-feature diagnostic; guarded paths must keep a valid fallback. Specialization budget limits code explosion. Source contract/annotations may be accepted with emitted guards or explicit user `--assume` diagnostics (never silently trusted).

<a id="s4-7"></a>
### 4.7 Pointer, union and vector legalization

Model pointer as allocation identity + offset + address space + bounds + alignment + mutability + lifetime/provenance. Native pointer targets use native addresses when legal. Pointerless targets use owned/shared byte buffers, typed arrays or `(allocationHandle, offset)` fat pointers. Preserve aliasing, pointer identity, byte-vs-element offsets and lifetime. Pointer comparisons/casts with undefined or implementation-dependent source semantics require dialect-specific handling.

Model `BitCast`, `Reinterpret`, `OverlayView`, subvector extraction/insertion, `PackedField`, `LoadLE/BE`, `StoreLE/BE`. Emit C unions or `memcpy`, C++ `std::bit_cast`, C# explicit-layout unmanaged structs, Rust safe bitcast/controlled unsafe, JS `ArrayBuffer` views, Perl `pack`/`unpack` or generated accessor tables, and BASIC UDT overlays where **documented**. Honor C/C++ effective-type/aliasing and endian restrictions. `UInt4096` (with carries), `BitVector4096` and `Vector4096<byte>` are different types and cannot share arithmetic semantics by fiat.

<a id="s4-8"></a>
### 4.8 Optimization and target selection

Required passes: constant/partial evaluation; SCCP; GVN/CSE; DCE; inlining; interprocedural effects; range/known-bit propagation; loop-invariant motion; iteration fusion; redundant allocation removal; bounds-check elimination; operation/type narrowing; storage/field packing; byte/bit packing; SWAR; SIMD vectorization/scalarization; exact FMA handling; strength reduction; copy elision; fusion of transposes/tensor operations; parallel reduction when associative; optional device placement. Each pass has an enable flag, legality proof, deterministic test fixtures, opt report and performance budget.

Cost model chooses native instruction vs sequence vs generated helper; unrolled vs loop; scalar vs SWAR/SIMD; packed vs unpacked storage; eager vs lazy; CPU vs thread/GPU/NPU. `--opt size|speed|memory|balanced`; stable defaults prioritize correctness. Target-specific PGO/autotuning may be later and must not be required to compile.

<a id="s4-9"></a>
### 4.9 Runtime and package composition

Compute minimum symbol-level closure over generated source, helpers, target adapters and license obligations. Reuse target standard library only when semantics match. Polypiler.Base must remain modular. Public APIs are idiomatic but explicitly mapped to source contracts. All generated packages include reproducible build instructions, test runner, manifest, license/provenance file and compiler/IR version metadata.

---

<a id="s5"></a>
## 5. DDD — domain-driven design

<a id="s5-1"></a>
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

<a id="s5-2"></a>
### 5.2 Ubiquitous language

**Algorithm** (semantic identity and variants), **LiftedInstruction**, **MachineState**, **ExternalOperation**, **OpaqueInstruction**, **EffectSummary**, **ObservedMemoryRegion**, **SemanticRecovery**, **BitMapping**, **BitRouting**, **BitBooleanTransform**, **TargetSynthesisGrammar**, **CostEvidence**, **SourceArtifact** (repo+commit+path), **FrontendDialect**, **TargetProfile**, **SemanticType**, **PhysicalRepresentation**, **Intrinsic**, **RecognitionRule**, **RewriteProof**, **ValueFact**, **MemoryRegion**, **IterationDomain**, **Kernel**, **GeneratedArtifact**, **ConformanceRun**, **VerificationEvidence**, **LicenseObligation**, **CapabilityDiagnostic**.

Type identifiers are **not** arbitrary C# wrapper names; e.g. semantic `MBF64` and x87 extended are encoded formats with explicit conversion contracts. Gray and ZigZag are reversible encodings, not ordinary signed/unsigned arithmetic. FP8 E4M3, Posit, MBF and decimal formats cannot be merged because they occupy the same number of bytes.

<a id="s5-3"></a>
### 5.3 Domain boundaries and contracts

Interfaces/ports should be narrowly scoped: `ISourceFrontend`, `IBinaryDecoder`, `IMachineLifter`, `IAssemblerFrontend`, `ITokenizedBasicCodec`, `ISemanticImporter`, `ITypeRegistry`, `IIntrinsicRegistry`, `IRecognitionEngine`, `IPermutationSynthesizer`, `IEquivalenceProver`, `IExternalApiResolver`, `IAnalysis`, `IPass`, `ITargetLegalizer`, `ITargetEmitter`, `IBuildAdapter`, `IVerificationOracle`, `IPackageComposer`. Each port declares capabilities and versioned contracts. Use immutable domain records for stable input/output, typed diagnostics and explicit result/error values; never have backends depend on frontend-specific AST classes.

Keep independently deployable modules and an anti-corruption layer around PB-Compiler and Cipher during migration. Preserve attribution and license on copied/derived code.

<a id="s5-4"></a>
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

<!-- PRD content ends -->

[PRD index](../PRD.md) · [Previous: Product and targets](01-product-and-targets.md) · [Next: Behavior and testing](03-behavior-and-testing.md)
