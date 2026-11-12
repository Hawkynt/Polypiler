# PRD part 04 — Roadmap and delivery

**Status:** original PRD requirements; planned capabilities are not implementation claims.
Original section numbering is retained.

[PRD index](../PRD.md) · [Previous: Behavior and testing](03-behavior-and-testing.md) · [Next: Semantics and recovery](05-semantics-and-recovery.md)

<!-- PRD content begins -->
<a id="s8"></a>
## 8. Detailed epics / implementation phases

**Execution rule:** One PR per reviewable vertical slice; phase gates below must be met before advertising milestone completion. Parallelize independent backends only after semantic IR and conformance harness stabilize. All checkboxes are to be checked by the agent **only after evidence exists**.

<a id="section-p0--repository-baseline-and-contracts"></a>
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

<a id="section-p1--typed-polyir-core--proof-infrastructure"></a>
### P1 — Typed PolyIR core + proof infrastructure

**Tasks**
- [ ] Implement immutable typed SSA core, basic blocks/phi, instruction/effect contracts, stage verifier, canonical serialization and source maps.
- [ ] Implement integer/float/boolean types, explicit signedness, shifts, overflow, conversions, exceptional values and target type layouts; register PB's MBF/Extended80 numeric oracles.
- [ ] Implement memory regions, arrays, spans, slices, pointer provenance/address spaces, union/overlay, byte order and lifetimes.
- [ ] Port/adapt PB-Compiler interval, known-bit, alignment/nullness analyses and add congruence/reduced product, demand bits, per-program-point facts and precise invalidation.
- [ ] Implement reusable constant/partial evaluation, SCCP, GVN, DCE, fold/rewrite infrastructure and typed proof diagnostics; benchmark regression harness.
- [ ] Add BDD scenarios for safe/unsafe bounds removal, overflow and representation observability.

**Exit gate:** Verifier rejects malformed IR, value facts reproduce verified PB fixtures, target-neutral scalar arithmetic can be interpreted and constant-folded, generated optimization reports contain proof references.

<a id="section-p2--minimum-useful-bidirectional-compiler"></a>
### P2 — Minimum useful **bidirectional** compiler

**Tasks**
- [ ] Build C# frontend via Roslyn; JavaScript frontend by integrating Cipher typed IL through an anti-corruption adapter; PowerBASIC frontend via PB binder/IR adapter.
- [ ] Build C#, JS, PB emitters with minimal runtime helper closure and valid source structure; reuse current PB/Cipher backend work under test.
- [ ] Implement first canonical recognition rules: rotations, popcount, endian loads, byte XOR, clamp, array copy/fill, map/filter/reduce, integer `DivMod` where safe.
- [ ] Add CLI `translate`, `inspect`, `verify` and `capabilities`; select source version and target dialect; `--opt none` and `--opt balanced`.
- [ ] Migrate first Cipher real checksum/cipher and PB numeric fixtures; emit and run C#↔JS, PB→C#, JS→PB, C#→PB as supported by corpus.
- [ ] Proven typed arrays, static JS numeric coercion, C# span/array mapping, PB integer precision and 16-bit index constraints; reject unsupported features explicitly.

**Exit gate:** A fresh user can execute at least JS Cipher→C# and PB→C# from CLI, compile and run generated code without manual edits, match authoritative vectors, and inspect helper/proof manifests. At least one reverse direction into JS and PB also verified. Binary/machine and bit-permutation capabilities remain explicitly experimental until independently gated.

<a id="section-p3--requested-workflows-and-rich-memorydata-ir"></a>
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

<a id="section-p4--full-planned-modern-language-bidirectionality"></a>
### P4 — Full planned modern-language bidirectionality

**Tasks**
- [ ] Implement remaining TypeScript, Java, Go, Delphi/Object Pascal, FreeBASIC, PHP and Ruby frontends and backends using versioned semantic profiles.
- [ ] Add JavaScript/Python/Perl guarded specialization when dynamic behavior requires it; contextual contracts and fallback generation.
- [ ] Complete FloatMath, IntMath and encoded type catalogue with representative microformat oracles, vector/tensor/collection intrinsics and selected native mappings.
- [ ] Implement Rust iterator, Java stream, Python comprehension, JS array method and Perl map/grep normalization to shared iteration IR.
- [ ] Expand package composer to Maven, NuGet, npm, Cargo, CPAN, PyPI and appropriate source layout with `--no-publish` default.
- [ ] Create compatibility dashboard built only from executed CI results and target version matrix.

**Exit gate:** At least one real algorithm per newly added language works as **frontend and backend**, with a multi-language roundtrip fixture and explicit unsupported-feature classifications. Broad language support does not imply general whole-program compatibility.

<a id="section-p5--advanced-compute-legacy-and-optimization-maturity"></a>
### P5 — Advanced compute, legacy and optimization maturity

**Tasks**
- [ ] Extend numerical type registry: decimal, FP4/FP8, configurable float/fixed point, Posit, 96/128/large integers, MBF/IBM/VAX, ZigZag/Gray, BCD. Preserve distinct semantic/storage/compute types.
- [ ] Complete logical vector widths 16/32/64/128/256/512/1024/2048/4096 and extensible, Boolean LUT, byte-shuffle, PEXT/PDEP, saturating and packed arithmetic, mixed-lane subviews, codegen fallback.
- [ ] Expand matrix/tensor operations with shape/stride/ownership, fusion and explicit CPU/GPU/NPU capability planning.
- [ ] Add basic C64/vintage BASIC constrained source+target subsets with RAM and numeric-range diagnostics.
- [ ] Define **optional, separately licensed** pure-Rust inference translation: enumerate NCNN graph ops, convert parameter/weight formats, implement/test operator coverage and precision, and only then remove native NCNN dependency. Unsupported model ops reject with precise diagnostics.
- [ ] Performance-profile on real PB graphics, Cipher crypto, compression, image filters and MediaDroyd workloads; reject transformations that regress the chosen target objective outside budgets.

**Exit gate:** Semantic conformance across applicable target profiles, measured optimization wins on at least representative workloads, no unverified neural-framework or legacy-runtime portability claims.

<a id="section-p6--controlled-upstream-replacement"></a>
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

<a id="s9"></a>
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

<a id="s10"></a>
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

<a id="s10-1"></a>
### 10.1 Exit codes and diagnostic contract

`0` success and verification pass; distinct stable nonzero codes for unsupported construct, invalid source, semantic mismatch, target build failure, reference oracle failure, license blocker, missing native/model dependency, resource budget violation, internal compiler error. Structured JSON diagnostics include source range, rule/spec ID, cause, workaround/required capability and reproducible command. On failure, no package may be marked verified.

<a id="s10-2"></a>
### 10.2 Generated manifest (minimum)

Schema includes: compiler and IR versions, requested CLI/config, input hashes and provenance, frontend profile, outputs/API, target/runtime/platform profile, intrinsics, helpers, native dependencies, licensing and model/weight restrictions, source semantic caveats, optimization passes/proofs, warnings, toolchain versions, exact verification results, benchmarks when measured, build/run commands. Distinguish `generated`, `built`, `executed`, and `verified` states.

---

<a id="s11"></a>
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

<a id="s11-1"></a>
### 11.1 Differential matrix requirements

For the initial alpha, verify at least six independent X→Y paths, with JS→C#, PB→C#, C#→JS, JS→PB, C#→Rust and Rust→C# (or C→JS where Rust frontend blocked; document blocker). For Tier B, cover each new language as source and target, and the two MediaDroyd showcases. For Tier C, add one bidirectional exercised path per additional language and representative pairs, not an unbounded full N×N runtime matrix. Run pairwise semantic contract tests and use a smoke/rotating matrix for breadth.

<a id="s11-2"></a>
### 11.2 Test corpus integrity

Never adjust expected vectors to match generated code. Preserve golden upstream output as read-only. Use independent specifications/test vectors where possible. If output differs because of legitimate float tolerance, record both results and a declared tolerance; don't blanket-ignore floating-point differences. For cryptography, exact outputs and constant-time constraints are distinct requirements.

---

<a id="s12"></a>
## 12. Native NCNN and neural model plan (explicitly separate)

**Path A — Rust bindings to existing inference provider (required P3):** Analyze relevant MediaDroyd C++ functions and NCNN integration; choose a stable C ABI shim rather than binding undocumented C++ implementation details; provide safe Rust wrapper around model contexts, frames/tensors and resources; build/test with permitted provider binaries/source. Produce correct interpolated output using authorized sample model/weights. Handle unsupported architectures and accelerators explicitly.

**Path B — compiled operators with no NCNN dependency (future P5):** Parse model graph/parameters, check all operators, implement equivalent tensor layouts, padding, rounding/quantization and interpolation rules, convert weights preserving format/precision, run differential model outputs. Emit Rust source or crate **only if every operator and dependency is supported and the weights are distributable under appropriate permission**. Native-provider binding is not a successful pure-Rust model translation.

**Path C — learn algorithms from native C++ code:** Can translate self-contained motion estimation, warping and pre/post-processing kernels separately; pointer/union legalizer, tensor IR and target operations apply. Does not turn opaque NN architecture and third-party weights into portable Rust automatically.

Model license and commercial-use conditions are **release blockers**, not warnings that can be overridden by an optimization flag. Allow local use with user-provided weights only where legally and technically permitted.

---

<a id="s13"></a>
## 13. Quality attributes and performance targets

<a id="s13-1"></a>
### 13.1 Correctness first

- No known semantic divergences in passing released corpus fixtures; known unsupported features are consistently diagnosed.
- A generated source artifact compiles **without modifying generated code** using declared, pinned toolchains/dependencies.
- Stable, machine-readable verification summaries and optimization justifications.
- Full deterministic rebuild from compiler version, source SHA and profile.

<a id="s13-2"></a>
### 13.2 Performance

Initial performance goal is to **remove avoidable overhead**, not require an impossible universal speedup. Track execution time, allocation count, peak memory, generated source size, binary size, build time and compilation cost, compare `--opt none`/`balanced`/`speed`, and publish target-specific results with representative inputs.

Acceptance for specific optimizations: reduced bounds checks/allocations/loops are shown in optimized IR; actual emitted target demonstrates the transformation when legal; correctness preserved; performance not materially worse on benchmark corpus unless the selected objective explicitly favors size/memory. Establish numeric regression thresholds after baseline measurement by ADR; never invent benchmarks.

<a id="s13-3"></a>
### 13.3 Security, sandbox and reproducibility

Parse untrusted code with resource limits; never execute untrusted source outside an isolated configured runner. Restrict filesystem/network access of generated-code tests and native model loading. Pin external dependencies. Enforce crypto-sensitive effect/secret-taint/constant-time restrictions and prohibit secret-dependent speculative dispatch. Run native sanitizers and fuzzers. Generate SBOM and license/provenance notices for packages.

<a id="s13-4"></a>
### 13.4 Developer experience

A new contributor should be able to add a language feature by editing a spec, adding one frontend semantic mapping, one backend lowering or fallback and conformance fixtures—not duplicating the entire optimizer. Docs include error catalog, type registry browser, IR dump viewer, before/after optimization graph and capability matrix.

---

<a id="s14"></a>
## 14. Acceptance criteria / definition of usable maturity

<a id="section-first-usable-alpha-a-developer-can-really-translate"></a>
### First **usable alpha** (a developer can really translate)

- [ ] `polypiler translate ... --from javascript --to csharp --verify` works on a pinned real Cipher algorithm, builds generated .NET library and passes authoritative vectors.
- [ ] PowerBASIC numeric source translates into C# and runs with differential verification.
- [ ] At least one C# numeric algorithm translates into JS and PB and compiles/runs.
- [ ] Rust numeric frontend and backend have one real executable cross-language path.
- [ ] IR verifier, range analysis, semantic diagnostics, reproducible manifest and minimum helper closure exist.
- [ ] Invalid or unsupported source yields a specific failure instead of invalid generated code marked success.
- [ ] Unit/BDD/differential CI green, documented source subset, pinned toolchains, reviewed PRs.

<a id="section-first-requested-showcase-beta"></a>
### First **requested showcase beta**

- [ ] Kotlin CPU deinterlacer translates into an actual Perl module; executable frame-level tests pass.
- [ ] Perl and Kotlin are also tested as frontends, not just backend labels.
- [ ] C++→Rust NCNN integration has a real licensed provider-backed demonstration or an explicit tracked release blocker; if blocked it is not advertised as supported.
- [ ] C#/JS/Rust/C/C++/Python/Perl/Kotlin/PB source+target support meets individually published conformance subsets.
- [ ] Source language patterns are recovered from nontrivial upstream loops, sequences and memory operations; target optimizations show validated bounds/representation changes.

<a id="section-general-availability-for-the-advertised-subset"></a>
### General availability for the **advertised subset**

- [ ] All Tier A–C languages advertised as supported have executed frontend→different backend AND different frontend→backend tests, with version profiles.
- [ ] Catalogue imports real algorithms from the identified source repositories; generated packages include full license/provenance and independent vectors.
- [ ] Source/target compatibility report is based on actual verified combinations, with known limitations and precise blockers.
- [ ] Both PB-Compiler and Cipher integrations can optionally delegate to Polypiler.Core for proven supported paths without forcing users onto the new implementation.
- [ ] Performance/size/memory comparisons and documented rollback controls exist.

---

<a id="s15"></a>
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

<a id="s16"></a>
## 16. Immediate agent instructions

1. Inspect `AGENTS.md` in **each** upstream repository and the project. Respect repository-specific build/licensing/PR rules.
2. Verify repository URLs, current branches and selected fixture paths. Replace illustrative paths/commands with **real** pinned SHA+path entries.
3. Create the Polypiler development repository/feature branch only with existing permissions; if creation or pushing is unavailable, create a fully reviewable local branch/patch and describe the blocker rather than claiming remote completion.
4. Start with executable test harness and a real JS Cipher→C# vertical slice. Build the minimum IR required for that slice without corrupting the future architecture.
5. Add PB→C# next, importing existing typed SSA/value facts where safe. Do not delete original code.
6. For each PR: update machine specs, failing/green BDD and NUnit tests, target compile+execution evidence, coverage, performance evidence for performance PRs, compatibility matrix and changelog.
7. Do not mock compilation, don't mark unsupported targets green, and don't replace NCNN inference with placeholder functions.
8. At the end of each phase, provide a short evidence-backed report with branch/PR, commit SHAs, tests actually run, compiler versions, artifacts, supported language pairs, observed limitations and next blocking issue.

<a id="section-initial-deliverables-checklist-the-first-reviewable-pr-sequence"></a>
### Initial deliverables checklist (the first reviewable PR sequence)

- [ ] `docs/prd/Polypiler.Core-Polypiler.Catalog-PRD.md` (this file), versioned spec schemas and ADR index.
- [ ] `tooling/corpus` importer with manifest locking, license scanner and repeatable upstream fixture discovery.
- [ ] `Polypiler.Core.IR`, `Polypiler.Core.Types`, verifier and tests for fixed-width integer, source numerical coercion and memory views.
- [ ] `Polypiler.Core.Frontends.JavaScript` adapter and C# backend supporting the first pinned Cipher algorithm.
- [ ] CLI translate/verify and CI integration compiling/running the generated C# library.
- [ ] Published `capabilities` output clearly identifying all other language directions as planned or unsupported until tests pass.

**The deliverable is not a diagram and not a collection of emitter file names. It is a verified cross-language executable translation pipeline, grown incrementally into the supported source×target matrix.**

---

<!-- PRD content ends -->

[PRD index](../PRD.md) · [Previous: Behavior and testing](03-behavior-and-testing.md) · [Next: Semantics and recovery](05-semantics-and-recovery.md)
