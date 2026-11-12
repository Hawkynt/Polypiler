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


The PRD is organized into the parts below. Together they retain the full requirements and original
section numbers; this index replaces the single long page. The architecture guides explain the
intended design, while these parts retain the normative requirements.

## Parts

| Part | Sections | Contents |
| --- | --- | --- |
| 01 | §1–3 | [Product and targets](prd/01-product-and-targets.md) |
| 02 | §4–5 | [Core architecture](prd/02-core-architecture.md) |
| 03 | §6–7 | [Behavior and testing](prd/03-behavior-and-testing.md) |
| 04 | §8–16 | [Roadmap and delivery](prd/04-roadmap-and-delivery.md) |
| 05 | §17–21 | [Semantics and recovery](prd/05-semantics-and-recovery.md) |
| 06 | §22–24 | [Recovery acceptance and gates](prd/06-recovery-acceptance-and-gates.md) |
| 07 | §25–28 | [Language and platform integration](prd/07-language-and-platform-integration.md) |
| 08 | §29–30 | [Interop and language semantics](prd/08-interop-and-language-semantics.md) |
| 09 | §31–33 | [Interop acceptance and execution](prd/09-interop-acceptance-and-execution.md) |
| 10 | §34 | [References](prd/10-references.md) |

## Reading routes

- Start with [product and targets](prd/01-product-and-targets.md), then
  [core architecture](prd/02-core-architecture.md) for the product boundaries and semantic IR.
- Before implementing, read [behavior and testing](prd/03-behavior-and-testing.md),
  [roadmap and delivery](prd/04-roadmap-and-delivery.md), and the
  [work-package completion gates](prd/06-recovery-acceptance-and-gates.md#s24-1).
- For lifting and optimization, follow [semantics and recovery](prd/05-semantics-and-recovery.md)
  and its [acceptance and proof gates](prd/06-recovery-acceptance-and-gates.md).
- For platform and runtime boundaries, read [language/platform integration](prd/07-language-and-platform-integration.md),
  [interop semantics](prd/08-interop-and-language-semantics.md), and
  [interop acceptance](prd/09-interop-acceptance-and-execution.md).

See also the [architecture reading order](README.md), [repository overview](../README.md), and
[canonical issue-writing standard](../ISSUES.md).

## Original section links

Existing `PRD.md#section` URLs still land here; follow their link to the corresponding part.
Chapter and subsection numbering is unchanged.

<details>
<summary>All original chapters and subsections</summary>

- <a id="1-executive-product-definition"></a> [1. Executive product definition](prd/01-product-and-targets.md#s1)
- <a id="11-product-boundaries"></a> [1.1 Product boundaries](prd/01-product-and-targets.md#s1-1)
- <a id="12-user-jobs"></a> [1.2 User jobs](prd/01-product-and-targets.md#s1-2)
- <a id="13-primary-end-to-end-demonstrations"></a> [1.3 Primary end-to-end demonstrations](prd/01-product-and-targets.md#s1-3)
- <a id="14-non-goals-for-first-general-availability-release"></a> [1.4 Non-goals for first general-availability release](prd/01-product-and-targets.md#s1-4)
- <a id="2-research-baseline-and-source-assets-verify-when-work-begins"></a> [2. Research baseline and source assets (verify when work begins)](prd/01-product-and-targets.md#s2)
- <a id="3-supported-language-and-platform-matrix"></a> [3. Supported language and platform matrix](prd/01-product-and-targets.md#s3)
- <a id="31-all-languages-are-planned-in-both-directions"></a> [3.1 All languages are planned in both directions](prd/01-product-and-targets.md#s3-1)
- <a id="31a-planned-non-source-formats--both-ingestion-and-emission"></a> [3.1a Planned non-source formats — both ingestion and emission](prd/01-product-and-targets.md#s3-1a)
- <a id="32-frontendbackend-implementation-requirements"></a> [3.2 Frontend/backend implementation requirements](prd/01-product-and-targets.md#s3-2)
- <a id="33-release-support-tiers"></a> [3.3 Release support tiers](prd/01-product-and-targets.md#s3-3)
- <a id="4-sdd--specification-driven-development-and-system-design"></a> [4. SDD — specification-driven development and system design](prd/02-core-architecture.md#s4)
- <a id="41-required-versioned-specifications"></a> [4.1 Required versioned specifications](prd/02-core-architecture.md#s4-1)
- <a id="42-canonical-semantic-ir-architecture"></a> [4.2 Canonical semantic IR architecture](prd/02-core-architecture.md#s4-2)
- <a id="43-minimum-mandatory-core-ir"></a> [4.3 Minimum mandatory core IR](prd/02-core-architecture.md#s4-3)
- <a id="44-abstract-interpretation--range-facts"></a> [4.4 Abstract interpretation / range facts](prd/02-core-architecture.md#s4-4)
- <a id="45-recognition-and-semantic-fusion"></a> [4.5 Recognition and semantic fusion](prd/02-core-architecture.md#s4-5)
- <a id="46-de-dynamicization-and-speculative-specialization"></a> [4.6 De-dynamicization and speculative specialization](prd/02-core-architecture.md#s4-6)
- <a id="47-pointer-union-and-vector-legalization"></a> [4.7 Pointer, union and vector legalization](prd/02-core-architecture.md#s4-7)
- <a id="48-optimization-and-target-selection"></a> [4.8 Optimization and target selection](prd/02-core-architecture.md#s4-8)
- <a id="49-runtime-and-package-composition"></a> [4.9 Runtime and package composition](prd/02-core-architecture.md#s4-9)
- <a id="5-ddd--domain-driven-design"></a> [5. DDD — domain-driven design](prd/02-core-architecture.md#s5)
- <a id="51-bounded-contexts"></a> [5.1 Bounded contexts](prd/02-core-architecture.md#s5-1)
- <a id="52-ubiquitous-language"></a> [5.2 Ubiquitous language](prd/02-core-architecture.md#s5-2)
- <a id="53-domain-boundaries-and-contracts"></a> [5.3 Domain boundaries and contracts](prd/02-core-architecture.md#s5-3)
- <a id="54-recommended-repository-structure"></a> [5.4 Recommended repository structure](prd/02-core-architecture.md#s5-4)
- <a id="6-bdd--executable-behavior-specifications"></a> [6. BDD — executable behavior specifications](prd/03-behavior-and-testing.md#s6)
- <a id="61-cli-and-supported-features"></a> [6.1 CLI and supported features](prd/03-behavior-and-testing.md#s6-1)
- <a id="62-source-language-semantics-and-specialization"></a> [6.2 Source-language semantics and specialization](prd/03-behavior-and-testing.md#s6-2)
- <a id="63-proof-driven-optimization"></a> [6.3 Proof-driven optimization](prd/03-behavior-and-testing.md#s6-3)
- <a id="64-cross-language-data-and-runtime-cases"></a> [6.4 Cross-language data and runtime cases](prd/03-behavior-and-testing.md#s6-4)
- <a id="65-specific-showcase-scenarios"></a> [6.5 Specific showcase scenarios](prd/03-behavior-and-testing.md#s6-5)
- <a id="66-bdd-evidence-per-scenario"></a> [6.6 BDD evidence per scenario](prd/03-behavior-and-testing.md#s6-6)
- <a id="7-tdd--testing-strategy-and-release-gates"></a> [7. TDD — testing strategy and release gates](prd/03-behavior-and-testing.md#s7)
- <a id="71-test-pyramid-tests-written-with-or-before-production-change"></a> [7.1 Test pyramid (tests written with or before production change)](prd/03-behavior-and-testing.md#s7-1)
- <a id="72-coverage-and-acceptance-policy"></a> [7.2 Coverage and acceptance policy](prd/03-behavior-and-testing.md#s7-2)
- <a id="73-per-language-ci-matrix"></a> [7.3 Per-language CI matrix](prd/03-behavior-and-testing.md#s7-3)
- <a id="8-detailed-epics--implementation-phases"></a> [8. Detailed epics / implementation phases](prd/04-roadmap-and-delivery.md#s8)
- <a id="p0--repository-baseline-and-contracts"></a> [P0 — Repository, baseline and contracts](prd/04-roadmap-and-delivery.md#section-p0--repository-baseline-and-contracts)
- <a id="p1--typed-polyir-core--proof-infrastructure"></a> [P1 — Typed PolyIR core + proof infrastructure](prd/04-roadmap-and-delivery.md#section-p1--typed-polyir-core--proof-infrastructure)
- <a id="p2--minimum-useful-bidirectional-compiler"></a> [P2 — Minimum useful **bidirectional** compiler](prd/04-roadmap-and-delivery.md#section-p2--minimum-useful-bidirectional-compiler)
- <a id="p3--requested-workflows-and-rich-memorydata-ir"></a> [P3 — Requested workflows and rich memory/data IR](prd/04-roadmap-and-delivery.md#section-p3--requested-workflows-and-rich-memorydata-ir)
- <a id="p4--full-planned-modern-language-bidirectionality"></a> [P4 — Full planned modern-language bidirectionality](prd/04-roadmap-and-delivery.md#section-p4--full-planned-modern-language-bidirectionality)
- <a id="p5--advanced-compute-legacy-and-optimization-maturity"></a> [P5 — Advanced compute, legacy and optimization maturity](prd/04-roadmap-and-delivery.md#section-p5--advanced-compute-legacy-and-optimization-maturity)
- <a id="p6--controlled-upstream-replacement"></a> [P6 — Controlled upstream replacement](prd/04-roadmap-and-delivery.md#section-p6--controlled-upstream-replacement)
- <a id="9-agent-work-packages-and-task-dependency-graph"></a> [9. Agent work packages and task dependency graph](prd/04-roadmap-and-delivery.md#s9)
- <a id="10-cliapi-contracts"></a> [10. CLI/API contracts](prd/04-roadmap-and-delivery.md#s10)
- <a id="101-exit-codes-and-diagnostic-contract"></a> [10.1 Exit codes and diagnostic contract](prd/04-roadmap-and-delivery.md#s10-1)
- <a id="102-generated-manifest-minimum"></a> [10.2 Generated manifest (minimum)](prd/04-roadmap-and-delivery.md#s10-2)
- <a id="11-concrete-corpus-and-test-ladder"></a> [11. Concrete corpus and test ladder](prd/04-roadmap-and-delivery.md#s11)
- <a id="111-differential-matrix-requirements"></a> [11.1 Differential matrix requirements](prd/04-roadmap-and-delivery.md#s11-1)
- <a id="112-test-corpus-integrity"></a> [11.2 Test corpus integrity](prd/04-roadmap-and-delivery.md#s11-2)
- <a id="12-native-ncnn-and-neural-model-plan-explicitly-separate"></a> [12. Native NCNN and neural model plan (explicitly separate)](prd/04-roadmap-and-delivery.md#s12)
- <a id="13-quality-attributes-and-performance-targets"></a> [13. Quality attributes and performance targets](prd/04-roadmap-and-delivery.md#s13)
- <a id="131-correctness-first"></a> [13.1 Correctness first](prd/04-roadmap-and-delivery.md#s13-1)
- <a id="132-performance"></a> [13.2 Performance](prd/04-roadmap-and-delivery.md#s13-2)
- <a id="133-security-sandbox-and-reproducibility"></a> [13.3 Security, sandbox and reproducibility](prd/04-roadmap-and-delivery.md#s13-3)
- <a id="134-developer-experience"></a> [13.4 Developer experience](prd/04-roadmap-and-delivery.md#s13-4)
- <a id="14-acceptance-criteria--definition-of-usable-maturity"></a> [14. Acceptance criteria / definition of usable maturity](prd/04-roadmap-and-delivery.md#s14)
- <a id="first-usable-alpha-a-developer-can-really-translate"></a> [First **usable alpha** (a developer can really translate)](prd/04-roadmap-and-delivery.md#section-first-usable-alpha-a-developer-can-really-translate)
- <a id="first-requested-showcase-beta"></a> [First **requested showcase beta**](prd/04-roadmap-and-delivery.md#section-first-requested-showcase-beta)
- <a id="general-availability-for-the-advertised-subset"></a> [General availability for the **advertised subset**](prd/04-roadmap-and-delivery.md#section-general-availability-for-the-advertised-subset)
- <a id="15-risks-decisions-and-change-control"></a> [15. Risks, decisions and change control](prd/04-roadmap-and-delivery.md#s15)
- <a id="16-immediate-agent-instructions"></a> [16. Immediate agent instructions](prd/04-roadmap-and-delivery.md#s16)
- <a id="initial-deliverables-checklist-the-first-reviewable-pr-sequence"></a> [Initial deliverables checklist (the first reviewable PR sequence)](prd/04-roadmap-and-delivery.md#section-initial-deliverables-checklist-the-first-reviewable-pr-sequence)
- <a id="17-fundamental-semantic-preservation-contract-normative-applies-to-every-pass"></a> [17. Fundamental semantic-preservation contract (normative; applies to every pass)](prd/05-semantics-and-recovery.md#s17)
- <a id="171-non-negotiable-transformation-law"></a> [17.1 Non-negotiable transformation law](prd/05-semantics-and-recovery.md#s17-1)
- <a id="172-effects-are-data-dependencies-not-giant-barriers"></a> [17.2 Effects are data dependencies, not giant barriers](prd/05-semantics-and-recovery.md#s17-2)
- <a id="173-external-api-normalization-registry"></a> [17.3 External API normalization registry](prd/05-semantics-and-recovery.md#s17-3)
- <a id="174-inline-assembly-is-syntax-not-a-universal-optimizer-fence"></a> [17.4 Inline assembly is syntax, not a universal optimizer fence](prd/05-semantics-and-recovery.md#s17-4)
- <a id="175-required-negative-tests"></a> [17.5 Required negative tests](prd/05-semantics-and-recovery.md#s17-5)
- <a id="18-binary-bytecode-and-historical-language-recovery-sdd"></a> [18. Binary, bytecode and historical-language recovery (SDD)](prd/05-semantics-and-recovery.md#s18)
- <a id="181-three-source-paths-and-two-lowering-horizons"></a> [18.1 Three source paths and two lowering horizons](prd/05-semantics-and-recovery.md#s18-1)
- <a id="182-binary-input-manifest-and-discovery"></a> [18.2 Binary input manifest and discovery](prd/05-semantics-and-recovery.md#s18-2)
- <a id="183-managed-and-portable-bytecode"></a> [18.3 Managed and portable bytecode](prd/05-semantics-and-recovery.md#s18-3)
- <a id="184-machine-code-lifter-architecture"></a> [18.4 Machine-code lifter architecture](prd/05-semantics-and-recovery.md#s18-4)
- <a id="185-tokenized-basic-p-code-and-mixed-loaders"></a> [18.5 Tokenized BASIC, p-code and mixed loaders](prd/05-semantics-and-recovery.md#s18-5)
- <a id="186-target-output-constraints"></a> [18.6 Target output constraints](prd/05-semantics-and-recovery.md#s18-6)
- <a id="19-semantic-recovery-de-optimization-and-loop-reconstruction-sdd"></a> [19. Semantic recovery, de-optimization and loop reconstruction (SDD)](prd/05-semantics-and-recovery.md#s19)
- <a id="191-purpose-and-core-invariants"></a> [19.1 Purpose and core invariants](prd/05-semantics-and-recovery.md#s19-1)
- <a id="192-recognition-hierarchy"></a> [19.2 Recognition hierarchy](prd/05-semantics-and-recovery.md#s19-2)
- <a id="193-specific-recovery-contracts"></a> [19.3 Specific recovery contracts](prd/05-semantics-and-recovery.md#s19-3)
- <a id="194-analysis-and-proof-engines"></a> [19.4 Analysis and proof engines](prd/05-semantics-and-recovery.md#s19-4)
- <a id="195-loop-recovery-milestones"></a> [19.5 Loop recovery milestones](prd/05-semantics-and-recovery.md#s19-5)
- <a id="20-polypermutation--bidirectional-bitperm-integration-sdd--algorithm-contracts"></a> [20. PolyPermutation — bidirectional BitPerm integration (SDD + algorithm contracts)](prd/05-semantics-and-recovery.md#s20)
- <a id="201-existing-implementation-and-migration-boundary"></a> [20.1 Existing implementation and migration boundary](prd/05-semantics-and-recovery.md#s20-1)
- <a id="202-formal-semantic-hierarchy"></a> [20.2 Formal semantic hierarchy](prd/05-semantics-and-recovery.md#s20-2)
- <a id="203-recovery-reverse-direction"></a> [20.3 Recovery (reverse direction)](prd/05-semantics-and-recovery.md#s20-3)
- <a id="204-canonical-algebra-and-optimization"></a> [20.4 Canonical algebra and optimization](prd/05-semantics-and-recovery.md#s20-4)
- <a id="205-synthesis-forward-direction"></a> [20.5 Synthesis (forward direction)](prd/05-semantics-and-recovery.md#s20-5)
- <a id="206-source-to-source-and-inverse-tests"></a> [20.6 Source-to-source and inverse tests](prd/05-semantics-and-recovery.md#s20-6)
- <a id="207-prohibition-of-unsafe-normalization"></a> [20.7 Prohibition of unsafe normalization](prd/05-semantics-and-recovery.md#s20-7)
- <a id="208-standalone-polypermutation-cliapi"></a> [20.8 Standalone PolyPermutation CLI/API](prd/05-semantics-and-recovery.md#s20-8)
- <a id="21-machine-code-and-retro-target-specific-design-sdd--ddd"></a> [21. Machine-code and retro target-specific design (SDD + DDD)](prd/05-semantics-and-recovery.md#s21)
- <a id="211-mos-6502-as-both-compiler-input-and-output"></a> [21.1 MOS 6502 as both compiler input and output](prd/05-semantics-and-recovery.md#s21-1)
- <a id="212-8086x86-integration-with-pb-compiler"></a> [21.2 8086/x86 integration with PB-Compiler](prd/05-semantics-and-recovery.md#s21-2)
- <a id="213-backend-capability-graph"></a> [21.3 Backend capability graph](prd/05-semantics-and-recovery.md#s21-3)
- <a id="214-domain-ports-and-dependency-rules"></a> [21.4 Domain ports and dependency rules](prd/05-semantics-and-recovery.md#s21-4)
- <a id="22-additional-required-bdd-scenarios-executable-specifications"></a> [22. Additional required BDD scenarios (executable specifications)](prd/06-recovery-acceptance-and-gates.md#s22)
- <a id="23-tdd-differential-verification-cost-and-quality-gates-for-added-capabilities"></a> [23. TDD, differential verification, cost and quality gates for added capabilities](prd/06-recovery-acceptance-and-gates.md#s23)
- <a id="231-independent-oracles"></a> [23.1 Independent oracles](prd/06-recovery-acceptance-and-gates.md#s23-1)
- <a id="232-required-test-categories"></a> [23.2 Required test categories](prd/06-recovery-acceptance-and-gates.md#s23-2)
- <a id="233-property-basedmetamorphic-tests"></a> [23.3 Property-based/metamorphic tests](prd/06-recovery-acceptance-and-gates.md#s23-3)
- <a id="234-fuzzing-proof-and-minimization"></a> [23.4 Fuzzing, proof and minimization](prd/06-recovery-acceptance-and-gates.md#s23-4)
- <a id="235-performance-and-solver-metrics"></a> [23.5 Performance and solver metrics](prd/06-recovery-acceptance-and-gates.md#s23-5)
- <a id="236-release-gate-states"></a> [23.6 Release gate states](prd/06-recovery-acceptance-and-gates.md#s23-6)
- <a id="24-agent-work-packages-for-binary-semantic-recovery-and-bitperm"></a> [24. Agent work packages for binary, semantic recovery and BitPerm](prd/06-recovery-acceptance-and-gates.md#s24)
- <a id="f0--shared-machineeffect-semantics-mandatory-foundation-alongside-p1"></a> [F0 — Shared machine/effect semantics (mandatory foundation, alongside P1)](prd/06-recovery-acceptance-and-gates.md#section-f0--shared-machineeffect-semantics-mandatory-foundation-alongside-p1)
- <a id="q0q1q2--bitperm-migration-p2p3-independent-vertical-slice"></a> [Q0/Q1/Q2 — BitPerm migration (P2–P3, independent vertical slice)](prd/06-recovery-acceptance-and-gates.md#section-q0q1q2--bitperm-migration-p2p3-independent-vertical-slice)
- <a id="b1b2--stack-and-portable-bytecode-frontendsbackends-after-p2"></a> [B1/B2 — Stack and portable bytecode frontends/backends (after P2)](prd/06-recovery-acceptance-and-gates.md#section-b1b2--stack-and-portable-bytecode-frontendsbackends-after-p2)
- <a id="r1r2r3r4r5--historical-source-and-cpus-after-p2-and-f0"></a> [R1/R2/R3/R4/R5 — Historical source and CPUs (after P2 and F0)](prd/06-recovery-acceptance-and-gates.md#section-r1r2r3r4r5--historical-source-and-cpus-after-p2-and-f0)
- <a id="b3b4--native-machine-code-lifting-after-f0--p3"></a> [B3/B4 — Native machine-code lifting (after F0 + P3)](prd/06-recovery-acceptance-and-gates.md#section-b3b4--native-machine-code-lifting-after-f0--p3)
- <a id="s1s5--algorithmic-recovery-and-de-optimization-after-p2"></a> [S1–S5 — Algorithmic recovery and de-optimization (after P2)](prd/06-recovery-acceptance-and-gates.md#section-s1s5--algorithmic-recovery-and-de-optimization-after-p2)
- <a id="end-to-end-priority-and-protected-migration"></a> [End-to-end priority and protected migration](prd/06-recovery-acceptance-and-gates.md#section-end-to-end-priority-and-protected-migration)
- <a id="241-project-wide-definition-of-done"></a> [24.1 Project-wide definition of done](prd/06-recovery-acceptance-and-gates.md#s24-1)
- <a id="242-agent-reporting-format"></a> [24.2 Agent reporting format](prd/06-recovery-acceptance-and-gates.md#s24-2)
- <a id="25-resource-scopes-deterministic-cleanup-and-high-level-control-flow-recovery-normative-sdd"></a> [25. Resource scopes, deterministic cleanup and high-level control-flow recovery (normative SDD)](prd/07-language-and-platform-integration.md#s25)
- <a id="251-recover-and-lower-resource-scopes-bidirectionally"></a> [25.1 Recover and lower resource scopes bidirectionally](prd/07-language-and-platform-integration.md#s25-1)
- <a id="252-tests-and-feature-support"></a> [25.2 Tests and feature support](prd/07-language-and-platform-integration.md#s25-2)
- <a id="26-llvm-ir-importexport-and-compiler-ecosystem-integration-normative-sdddddtdd"></a> [26. LLVM IR import/export and compiler ecosystem integration (normative SDD/DDD/TDD)](prd/07-language-and-platform-integration.md#s26)
- <a id="261-scope-and-boundaries"></a> [26.1 Scope and boundaries](prd/07-language-and-platform-integration.md#s26-1)
- <a id="262-frontend-obligations"></a> [26.2 Frontend obligations](prd/07-language-and-platform-integration.md#s26-2)
- <a id="263-backend-obligations"></a> [26.3 Backend obligations](prd/07-language-and-platform-integration.md#s26-3)
- <a id="264-supported-external-compiler-frontends--precise-claims"></a> [26.4 Supported external compiler frontends — precise claims](prd/07-language-and-platform-integration.md#s26-4)
- <a id="265-new-work-packages"></a> [26.5 New work packages](prd/07-language-and-platform-integration.md#s26-5)
- <a id="266-llvm-acceptance-bdd"></a> [26.6 LLVM acceptance BDD](prd/07-language-and-platform-integration.md#s26-6)
- <a id="27-c--8086x86-16-dos-via-semantic-lowering-normative-sdd--bdd--tdd"></a> [27. C# → 8086/x86-16 DOS via semantic lowering (normative SDD + BDD + TDD)](prd/07-language-and-platform-integration.md#s27)
- <a id="271-product-definition"></a> [27.1 Product definition](prd/07-language-and-platform-integration.md#s27-1)
- <a id="272-lowering-rules"></a> [27.2 Lowering rules](prd/07-language-and-platform-integration.md#s27-2)
- <a id="273-end-to-end-tests"></a> [27.3 End-to-end tests](prd/07-language-and-platform-integration.md#s27-3)
- <a id="274-work-packages"></a> [27.4 Work packages](prd/07-language-and-platform-integration.md#s27-4)
- <a id="275-definition-of-supported"></a> [27.5 Definition of supported](prd/07-language-and-platform-integration.md#s27-5)
- <a id="28-llvm-and-dos-phase-integration-and-release-gates"></a> [28. LLVM and DOS phase integration and release gates](prd/07-language-and-platform-integration.md#s28)
- <a id="29-polyinterop--foreign-library-identity-type-layout-and-abi-legalization-normative-sdd"></a> [29. PolyInterop — foreign library identity, type layout and ABI legalization (normative SDD)](prd/08-interop-and-language-semantics.md#s29)
- <a id="291-first-class-foreign-declarations-and-apis"></a> [29.1 First-class foreign declarations and APIs](prd/08-interop-and-language-semantics.md#s29-1)
- <a id="292-layout-schema-and-synthesized-udts"></a> [29.2 Layout schema and synthesized UDTs](prd/08-interop-and-language-semantics.md#s29-2)
- <a id="293-data-marshaling-and-multi-call-fusion"></a> [29.3 Data marshaling and multi-call fusion](prd/08-interop-and-language-semantics.md#s29-3)
- <a id="294-exotic-numerics-and-legacy-abi-examples"></a> [29.4 Exotic numerics and legacy ABI examples](prd/08-interop-and-language-semantics.md#s29-4)
- <a id="295-required-native-test-fixtures"></a> [29.5 Required native test fixtures](prd/08-interop-and-language-semantics.md#s29-5)
- <a id="30-high-level-language-constructs-object-semantics-and-structural-recovery-normative-sddddd"></a> [30. High-level language constructs, object semantics and structural recovery (normative SDD/DDD)](prd/08-interop-and-language-semantics.md#s30)
- <a id="301-shared-semantic-feature-registry"></a> [30.1 Shared semantic feature registry](prd/08-interop-and-language-semantics.md#s30-1)
- <a id="302-specialization-and-runtime-closure"></a> [30.2 Specialization and runtime closure](prd/08-interop-and-language-semantics.md#s30-2)
- <a id="303-coroutine-and-async-recovery"></a> [30.3 Coroutine and async recovery](prd/08-interop-and-language-semantics.md#s30-3)
- <a id="304-exceptions-error-recovery-and-deterministic-resources"></a> [30.4 Exceptions, error recovery and deterministic resources](prd/08-interop-and-language-semantics.md#s30-4)
- <a id="305-first-class-bidirectional-tests"></a> [30.5 First-class bidirectional tests](prd/08-interop-and-language-semantics.md#s30-5)
- <a id="31-bdd--foreign-abi-marshalling-and-language-semantics"></a> [31. BDD — foreign ABI, marshalling and language semantics](prd/09-interop-acceptance-and-execution.md#s31)
- <a id="32-tdd-and-release-gates-for-polyinterop-and-semantic-feature-recovery"></a> [32. TDD and release gates for PolyInterop and semantic feature recovery](prd/09-interop-acceptance-and-execution.md#s32)
- <a id="33-agent-execution-addenda-and-revised-critical-path"></a> [33. Agent execution addenda and revised critical path](prd/09-interop-acceptance-and-execution.md#s33)
- <a id="331-additional-work-packages"></a> [33.1 Additional work packages](prd/09-interop-acceptance-and-execution.md#s33-1)
- <a id="332-highest-priority-demonstration-ladder"></a> [33.2 Highest-priority demonstration ladder](prd/09-interop-acceptance-and-execution.md#s33-2)
- <a id="333-repository-and-handoff-invariants"></a> [33.3 Repository and handoff invariants](prd/09-interop-acceptance-and-execution.md#s33-3)
- <a id="34-reference-links"></a> [34. Reference links](prd/10-references.md#s34)

</details>
