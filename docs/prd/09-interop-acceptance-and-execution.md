# PRD part 09 — Interop acceptance and execution

**Status:** original PRD requirements; planned capabilities are not implementation claims.
Original section numbering is retained.

[PRD index](../PRD.md) · [Previous: Interop and language semantics](08-interop-and-language-semantics.md) · [Next: References](10-references.md)

<!-- PRD content begins -->
<a id="s31"></a>
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

<a id="s32"></a>
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

<a id="s33"></a>
## 33. Agent execution addenda and revised critical path

<a id="s33-1"></a>
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

<a id="s33-2"></a>
### 33.2 Highest-priority demonstration ladder

1. JS Cipher→C# and PowerBASIC numeric→C# on real, pinned source fixtures. Build and execute all target output.
2. Bidirectional C#/JS/PB numeric algorithms with full range/proof/effect reports and BitPerm mapping synthesis.
3. C header/record/DLL→PowerBASIC native binding and exact byte layout on a supported 32-bit Windows test toolchain; if not available in CI, keep a dedicated externally executable acceptance project and mark the scenario blocked.
4. C# generic overload/interface numerical class→C/PB UDT and Kotlin MediaDroyd deinterlacer→Perl.
5. LLVM `.ll`/`.bc` import/export, C#→x86-16 DOS, Rust→6502, 6502→C#, MSIL→PHP, JVM→Delphi, tokenized BASIC round-trips, guarded by their previously defined epics.
6. More languages and platform/runtime compatibility, FFI expansion, dynamic object models and accelerator inference only after evidence of earlier milestones.

<a id="s33-3"></a>
### 33.3 Repository and handoff invariants

- Product/repository/CLI is **Polypiler**; avoid user-facing split into separate PolyCore/PolyLib products. The optional catalogue is a subsystem in the same repository.
- The agent shall create or fork the repository only with permissions available and shall **not** overwrite an existing unrelated `Hawkynt/Polypiler` repository.
- Bootstrap with `README.md`, `PRD.md`, `AGENTS.md`, editorconfig, solution/SDK pins, build/test scripts, initial CI and a release-status manifest. License file must reflect an explicit chosen repository license; upstream code/model licensing remains separately enforced.
- Use modern C#, .NET 9 for normal projects, latest stable language features, 2-space K&R style, no global usings, NUnit with classic `Assert.That`, latest compiler where compatible, .NET Framework 2.0 compatibility for backport packages where applicable.
- Keep documentation source-aligned: examples of `polypiler` CLI are a **specification** until a real CLI exists; never imply it is installed. Source implementations are external references until vendored/imported under approved licenses.
- Each PR must include implementation, why, spec/tests, generated test artifacts, reproducibility, performance evidence if claimed, corpus/source SHAs, license notes and supported/blocked feature manifest. `dotnet build`, `dotnet test`, every relevant target toolchain/emulator and CI must pass; maintain linear history, one reviewer, no direct pushes to default branch.
- Goal of the agent's **first** usable alpha is not 'all languages': it is a verified working `polypiler translate` from **real Cipher JS and PB code to C#** with a reusable canonical IR and clear unsupported diagnostics. Subsequent milestones grow the explicit verified support matrix.


---

<!-- PRD content ends -->

[PRD index](../PRD.md) · [Previous: Interop and language semantics](08-interop-and-language-semantics.md) · [Next: References](10-references.md)
