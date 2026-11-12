# PRD part 06 — Recovery acceptance and gates

**Status:** original PRD requirements; planned capabilities are not implementation claims.
Original section numbering is retained.

[PRD index](../PRD.md) · [Previous: Semantics and recovery](05-semantics-and-recovery.md) · [Next: Language and platform integration](07-language-and-platform-integration.md)

**Architecture guides:** [Semantic recovery](../architecture/SEMANTIC-RECOVERY.md) · [Worked translations](../architecture/WALKTHROUGHS.md) · [Verification gates](../quality/VERIFICATION.md)

<!-- PRD content begins -->
<a id="s22"></a>
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

<a id="s23"></a>
## 23. TDD, differential verification, cost and quality gates for added capabilities

<a id="s23-1"></a>
### 23.1 Independent oracles

Use **independent implementers/engines** where practical: native executable vs generated source, PB-Compiler oracle vs Polypiler.Core, Cipher original Node harness vs generated C#, VICE or another pinned C64 emulator vs regenerated PRG, pinned 6502 emulator vs machine-lifted code, CLR/JVM/WASM runtime vs bytecode translation, BitPerm raw mapping evaluation vs generated network, formal bitvector solver vs compiler rewrite. Never use Polypiler.Core's own optimized output as the sole expected result.

For machine code, compare not only function outputs but a configurable observable-state trace: buffer byte ranges, flags/registers when exposed, memory writes, I/O port transactions, external calls, traps, returns and target CPU cycles when in timing-sensitive mode. A raw machine memory trace is too strict for high-level targets unless declared observationally equivalent; compare at the semantic boundary, and verify raw trace for effects that must be preserved exactly.

<a id="s23-2"></a>
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

<a id="s23-3"></a>
### 23.3 Property-based/metamorphic tests

- Valid bijective P: `Inverse(P)(P(x)) == x` for every valid input; `Compose(P,Identity)==P`; routes with duplication are explicitly excluded from inverse property.
- Pure `BitRouting`: output basis provenance is structurally correct; for any candidate numeric trick with multiplication, recheck all relevant multi-bit combinations and SMT equivalence.
- Width-specific arithmetic: `WrapN(a+b)` agrees with exact modular oracle; `AddWithCarry` reflects carry-out and flags per ISA; signed and unsigned shifts use precise profile behavior.
- Source→IR→target output and source→IR→same-source output agree for supported algorithms; target→lifted IR→source round trips for selected binary snippets.
- Loop reconstruction preserves final buffers and observable memory effects for randomly generated valid alias patterns; where proof does not hold, rewrite must not fire.
- Type narrowing preserves source API type and external ABI/layout; generate hostile alias escape cases.
- Determinism: same input SHA, compiler hash, profile, seed and budget must generate the same target IR and files, excluding explicitly documented nondeterministic tool metadata.

<a id="s23-4"></a>
### 23.4 Fuzzing, proof and minimization

Fuzz parsers/decoders with corpus mutation and structure-aware generators. Fuzz SSA rewrites and effect regions against a reference interpreter/SMT model. Use bounded instruction-sequence generation on 6502/x86/ARM and compare reference emulators. Minimize every mismatch into a reproducible test fixture with preserved upstream source hashes. A failed or timed-out proof triggers fallback or a negative diagnostic, never success. Tests for arithmetic bit hacks must deliberately include values where carries overlap.

<a id="s23-5"></a>
### 23.5 Performance and solver metrics

For each target profile record estimated latency/throughput, generated instruction and byte counts, source/package size, register pressure, memory accesses, number of temporaries, peak RAM/stack, compile time, target runtime, search budget/state count and proof duration. `--opt speed|size|memory|balanced` applies to native and retro targets, but costs are derived from the actual CPU model or benchmark evidence. No stable benchmark number is asserted without a run on the pinned hardware/emulator config. For 6502, count cycle differences due to taken branches and page crossings where benchmark contract requests it; general functional verification does not imply cycle accuracy.

<a id="s23-6"></a>
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

<a id="s24"></a>
## 24. Agent work packages for binary, semantic recovery and BitPerm

The original P0–P6 stages remain the backbone. The following parallel **gated epics** extend them. They are dependent on verified typed SSA, effect model, reference interpreters, CLI and cross-toolchain runners; do not attempt all of them in the first sprint. Each work package becomes a small PR/issue with spec, positive and negative BDD, failing-then-green tests, emitted artifact, proof/effect report, correct docs and license review.

<a id="section-f0--shared-machineeffect-semantics-mandatory-foundation-alongside-p1"></a>
### F0 — Shared machine/effect semantics (mandatory foundation, alongside P1)

- [ ] Define `MachineIR`, flags/register SSA, memory-region tokens, hardware I/O and external-call contracts with stage verifiers.
- [ ] Extend canonical SSA with `OpaqueInstruction`, `ExternalOperation`, `DeviceIO`, `MachineTrap`, `CodeWrite`, `RuntimeDependency` and source/ISA provenance.
- [ ] Implement machine-state interpreter for a minimal approved instruction subset; add explicit target legalization outcomes and stable diagnostics.
- [ ] Build negative regression suite for unknown stack/MMIO writes, self-modification and unknown API calls.

**Exit:** Mixed typed SSA and machine effects verify correctly; no unknown operation gets dropped or accidentally emitted for an incompatible target.

<a id="section-q0q1q2--bitperm-migration-p2p3-independent-vertical-slice"></a>
### Q0/Q1/Q2 — BitPerm migration (P2–P3, independent vertical slice)

- [ ] `Q0`: pin BitPerm SHA; export its mapping fixtures, 51 documented test cases, building-block registry and CPU preset values; audit exact executable test count and fix test harness CI integration.
- [ ] `Q0`: extract mapping, constants, inverted bits, don't-cares, duplication and width contracts into pure `Polypiler.Permutation` model. Preserve old UI independently.
- [ ] `Q1`: implement canonical mapping evaluator, typed candidate networks, greedy + bounded Dijkstra, shift/rotate/PEXT/PDEP/shuffle/LUT primitives and C# target; run exhaustive 8-bit and SMT equivalence tests.
- [ ] `Q1`: correct multiply routing by separating OR-based *possible landing* analysis from exact modular arithmetic with carries; include counterexample tests.
- [ ] `Q1`: reverse-recognize at least a real BitPerm shift/mask mapping, rotate, byte swap, a verified multiplication-based routing; re-synthesize to C# and Rust.
- [ ] `Q2`: implement target profiles and typed lowering for 6502, PB, x86-64 and ARM profiles; add butterfly/Beneš candidate families and optional solver-assisted synthesis; preserve exact cost/proof reporting.
- [ ] `Q2`: reconnect the BitPerm browser UI to shared engine/API after verified parity; expose rollback switch. Retain original UI experience, not force a CLI-only replacement.

**Exit:** `BitPerm mapping → PolyIR → target code → recover mapping` round-trip passes on real tools; non-bijective cases don't invert; benchmarks/optimality statements are correctly scoped; existing BitPerm tests remain green.

<a id="section-b1b2--stack-and-portable-bytecode-frontendsbackends-after-p2"></a>
### B1/B2 — Stack and portable bytecode frontends/backends (after P2)

- [ ] `B1-MSIL`: real IL reader+metadata resolution; stack-to-SSA; recognized BCL map; `MSIL→PHP` executed demonstration; IL emitter+round-trip subset.
- [ ] `B1-JVM`: classfile reader+stack maps; JVM shifts/overflows, invokes and exceptions; `JVM→Delphi` executed demonstration; classfile emitter+round-trip subset.
- [ ] `B2-WASM`: typed WASM stack+linear memory+trap semantics; `WASM→Rust` executed demonstration; WASM emitter+validator/runtime check.

**Exit:** Each declared input/output format has executable verification in both directions; unknown runtime imports fail or use explicit allowed helpers.

<a id="section-r1r2r3r4r5--historical-source-and-cpus-after-p2-and-f0"></a>
### R1/R2/R3/R4/R5 — Historical source and CPUs (after P2 and F0)

- [ ] `R1`: C64 BASIC V2 tokenizer/detokenizer; PRG loader/writer; PETSCII/`REM`/`DATA` correctness; emulator roundtrip with user programs that do not require device emulation.
- [ ] `R2`: MOS 6502 decoded/typed lifting, instruction semantics, flags, stack/zero-page model; `6502→C#` differential test; `Rust→6502` via assembler+emulator, deterministic buffer contracts and memory budgets.
- [ ] `R2`: mixed C64 BASIC+`SYS` reference linked to 6502 code; preserve device and executable-memory effects.
- [ ] `R3`: GW-BASIC tokenized files, dialect-specific numeric encodings and oracle; optional token emission if verified.
- [ ] `R4`: 65C02/65C816/Z80/8080 support where individual CPU semantics and toolchains are verified; source and target independently gated.
- [ ] `R5`: concrete Microsoft p-code dialect inventory, opcode reference documentation and samples, then one verified decoder and subsequently writer. If evidence is insufficient, block with clear unsupported-version diagnostics.

**Exit:** C64 PRG→C# and C#→PRG run; 6502→C# and Rust→6502 run; no general p-code claim based on BASIC token parsing.

<a id="section-b3b4--native-machine-code-lifting-after-f0--p3"></a>
### B3/B4 — Native machine-code lifting (after F0 + P3)

- [ ] `B3-x86`: decode x86 mode and flags, caller-specified ABI, memory effects, relocations; `x86→C++` executable checksum/bit-permutation fixture.
- [ ] `B3-x64`: x64 ABI, partial registers, pointer/provenance, stack/register SSA; `x64→Python` numeric-array routine with same fixed-width results and observable buffer effects.
- [ ] `B4-ARM`: ARM32/Thumb and AArch64 distinct profiles, decoded flags/conditional instructions; `ARM→PowerBASIC` freestanding checksum with ARM emulator oracle.
- [ ] Add x86/x64/ARM assembly writers/backends for verified supported instructions/functions; target binary execution must be independently tested. Unsupported whole-program dynamic linking/OS behavior stays explicit.

**Exit:** Required showcase paths compile and execute and match native/emulated oracles. Instruction coverage is reported per architecture/version, not advertised as full binary decompilation.

<a id="section-s1s5--algorithmic-recovery-and-de-optimization-after-p2"></a>
### S1–S5 — Algorithmic recovery and de-optimization (after P2)

- [ ] `S1`: SSA slicing and affine unroll recognition for 2/4/8 statements; direct high-level loop or vector IR after proof.
- [ ] `S2`: source-specific bit hacks (`min`, `max`, `swap`, `mul`, rotate, PEXT/PDEP, bit reversal) with exact width/range/effect predicates.
- [ ] `S3`: interleaved arithmetic DAG recovery; partial loop unrolling and independent-chain vectorization.
- [ ] `S4`: software-pipeline/peel/epilogue recovery and representation/layout optimization under memory ordering proofs.
- [ ] `S5`: mixed ASM+source algebraic fusion and high-level↔ASM output; capability and side-effect diagnostics.

**Exit:** Every recovered semantic intrinsic has a positive fixture, unsafe counterexample, proof/certified rule, runtime differential test and emitted code that supports the target option.

<a id="section-end-to-end-priority-and-protected-migration"></a>
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

<a id="s24-1"></a>
### 24.1 Project-wide definition of done

A PR is complete only when: production code exists; normative spec updated; at least one positive and one negative behavior scenario automated; NUnit/core coverage meets ≥80% for newly added logic; actual target compiler/assembler or interpreter executes output; independent oracle and effects match; vulnerability/license and provenance checks pass; optimization PR includes measured benchmark or explicit no-performance-claim rationale; `dotnet build`, `dotnet test`, relevant foreign toolchain tests and CI pass; reviewer approval recorded; feature branch history linear; no direct default-branch commits; no secrets, unlicensed weights or copyrighted upstream code accidentally redistributed. A cross-language pair remains **unsupported or experimental** until its actual acceptance gate is satisfied.

<a id="s24-2"></a>
### 24.2 Agent reporting format

After each PR, provide `PR/branch + commits`, source corpus SHA and file, touched spec/IR modules, language/CPU profile, unsupported features, verification state (`lifted`, `proved`, `compiled`, `executed`, `verified`), commands actually executed, toolchain versions, test counts/coverage, before/after semantic graph for optimization work, performance results when measured, license notes, blockers and next dependency. Do not promise work outside the agent's current invocation or imply a background process is running.

---

<!-- PRD content ends -->

[PRD index](../PRD.md) · [Previous: Semantics and recovery](05-semantics-and-recovery.md) · [Next: Language and platform integration](07-language-and-platform-integration.md)
