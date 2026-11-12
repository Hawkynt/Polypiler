# PRD part 07 — Language and platform integration

**Status:** original PRD requirements; planned capabilities are not implementation claims.
Original section numbering is retained.

[PRD index](../PRD.md) · [Previous: Recovery acceptance and gates](06-recovery-acceptance-and-gates.md) · [Next: Interop and language semantics](08-interop-and-language-semantics.md)

**Architecture guides:** [Target profiles](../architecture/TARGETS.md) · [Language lowering](../architecture/LANGUAGE-LOWERING.md) · [Foreign bindings](../architecture/INTEROP.md)

<!-- PRD content begins -->
<a id="s25"></a>
## 25. Resource scopes, deterministic cleanup and high-level control-flow recovery (normative SDD)

<a id="s25-1"></a>
### 25.1 Recover and lower resource scopes bidirectionally

Polypiler.Core SHALL model acquisition, deterministic release, `try/finally`, exceptional/nonlocal exits, `using` statements/declarations, `await using`, C++ RAII and `defer` as semantic operations with explicit cleanup edges and source dialect policies. A common `ResourceScope`/`CleanupRegion` representation records acquisition evaluation, resource type/identity, null and conversion behavior, cleanup action, order, cleanup-exception effects, lifetime, escape/alias facts, and ordinary/exceptional/suspended exits. Scope recovery from generated code is proof-driven and optional; `try/finally` containing `Dispose()` is **not** automatically C# `using`.

**Backward:** Recognize compiler-generated `try/finally` disposal and related patterns from C# source, MSIL or lifted code only if acquisition and disposal semantics match. Use source spans or statement boundaries when required; preserve handler/cleanup order and the last exception that actually propagates. Do not move/discard an effectful disposal call, including when an external callee may inspect state or when disposal throws. Preserve value-type copy, boxing and pattern-based disposal differences.

**Forward:** Lower to target-native C# `using`, C++ RAII, Rust ownership/`Drop` where equivalent, or explicit cleanup CFG and generated helpers for BASIC/C/assembly. Rust's `Drop` on scope exit is not automatically equivalent to C# exception-initiated disposal; evaluate unwind/panic and destruction contracts. Async disposal is a suspendable cleanup action, not equivalent to synchronous `Dispose`. Nonlocal `return`, `break`, `continue`, `goto`, exception, cancellation and coroutine suspension need exact cleanup semantics.

<a id="s25-2"></a>
### 25.2 Tests and feature support

BDD scenarios SHALL cover normal completion, early return, nested reversed disposal, null resource, failed acquisition, cleanup throwing, exceptional body, byref/captured resource, dynamic conversion, value-type disposable, pattern-based `Dispose`, `await using`, and source-recovery false positives. NUnit and source/runtime differential oracles required. For an output lacking native exceptions, either synthesize a verified exception/cleanup mechanism or reject the specific semantics; do not claim to preserve exceptional paths while omitting them.

<a id="s26"></a>
## 26. LLVM IR import/export and compiler ecosystem integration (normative SDD/DDD/TDD)

<a id="s26-1"></a>
### 26.1 Scope and boundaries

Polypiler.Core SHALL implement **both** an LLVM IR frontend (`.ll` text, `.bc` bitcode) and backend (verifier-compliant LLVM modules). These are first-class interchange formats and separate from PolyIR's canonical semantic identity. The existing PB-Compiler `Ir/LlvmEmitter.cs`, SSA verifier, and `docs/IR.md` are candidates to extract/adapt. PB-Compiler already documents LLVM emission for modern targets and a separate in-house native x86-16 backend; do not rebuild or conflate them.

LLVM IR is lower-level than source language semantics. It may lose ownership, `using`, class hierarchy, generic source shape, original exception policy and source type identity. Polypiler.Core SHALL recover such features when proven, preserve external dependencies and ABI information when unknown, and never assume textual/binary IR re-import is lossless source reconstruction. PolyIR -> LLVM may need a minimal runtime closure; LLVM -> PolyIR may retain opaque intrinsic/call nodes and source metadata as evidence. LLVM `undef`/`poison`/UB semantics MUST NOT silently become deterministic source-language values.

<a id="s26-2"></a>
### 26.2 Frontend obligations

- Pin a supported LLVM major/version range and toolchain (`llvm-config`, verifier, `llvm-as`, `llvm-dis`, `opt`, `llc`), with manifest, version-check diagnostics and reproducible fixtures. Text and bitcode import supported independently; LLVM bitcode compatibility varies by version.
- Import typed functions, blocks, phi and CFG, integers of arbitrary bit width, vector/aggregate types, data layout, address spaces, globals, aliases, calling conventions, function/parameter/result attributes, alignment, overflow flags, target triple, linkage and visibility.
- Accurately model `sret`, `byval`, `inalloca` where present, indirect calls, varargs, inline assembly, atomics, volatile memory, fences, exceptions/invoke/personality/landingpads or funclets, traps, TLS, COMDAT, target-specific and generic intrinsics, `undef` and `poison`, metadata, debug spans and source provenance.
- Map known LLVM intrinsics and pure patterns to canonical IntMath/FloatMath/Memory/PolyPermutation/PolyIteration IR only after proving equivalence. Preserve or reject unmapped operations according to the external-effect contract and requested target; never erase them.
- Native library calls appearing in LLVM IR remain first-class external calls. Retain symbol, calling convention, ABI, dependencies, memory effects, exception/fault behavior and platform constraints.

<a id="s26-3"></a>
### 26.3 Backend obligations

- Emit LLVM-valid typed SSA with correct target triple, `DataLayout`, calling convention, symbol visibility, pointer address space and ABI attributes. Re-run LLVM verifier after every emitted module.
- Legalize Polypiler.Core-specific numerics (MBF, Posit, custom float80), semantic resource scopes, dynamic shapes, runtime object models and unusual pointer regions explicitly. Emit verified target helpers when needed; unsupported cases produce capability diagnostics.
- Preserve FP strictness/rounding and signedness interpretations through operation and conversion semantics. LLVM integer IR types are signless, so signed/unsigned arithmetic remains encoded on operators, not by inventing separate LLVM integer types.
- Require generated `.ll` -> `llvm-as` -> `opt` / `llc` -> native linking/execution, and independently compare outputs and observable effects against original fixtures. Run `.bc` -> `llvm-dis` -> import round-trip checks with ABI and effect invariants; textual identity is not required.
- No claim that LLVM supports 8086 real-mode generation. Use PB-Compiler's dedicated x86-16 selector/assembler unless a separately verified LLVM-to-x86-16 bridge is introduced.

<a id="s26-4"></a>
### 26.4 Supported external compiler frontends — precise claims

- **Clang:** C, C++, Objective-C/C++, OpenCL and documented CUDA support can supply LLVM IR; prefer Clang AST/semantic import when high-level type/object semantics are required (LLVM IR may erase them).
- **Flang and other LLVM-based projects:** accept their emitted LLVM IR only when toolchain and runtime dependencies are validated. Such ingestion does not imply native support for complete source-language standard libraries.
- **GCC:** GCC frontends do **not** natively emit LLVM IR. GCC uses GENERIC/GIMPLE/RTL. Its languages (e.g., Ada, D, Go, Modula-2, Rust variants) are *not* automatically Polypiler.Core frontends by enabling LLVM. A future GCC-specific plugin/bridge may export an intentionally defined semantic subset to PolyIR or LLVM, with GCC version pinning and independent validation; GCC backend object code may be lifted with reduced semantics as a separate fallback.
- An LLVM or GCC frontend is usable as an **import path** only once actual output is imported, legalized, compiled, executed and compared with source/toolchain oracles. Installed compiler availability does not equal verified frontend support.

<a id="s26-5"></a>
### 26.5 New work packages

**L0 — LLVM contracts and existing-code audit (after P1):** inventory PB `LlvmEmitter`, LLVM type/ABI coverage, existing emitter tests; write `specs/llvm-ir-import.md`, `specs/llvm-ir-export.md`, version/profile policies, negative test fixtures and ADR. Document source semantic losses and a capability manifest.

**L1 — LLVM importer (after P2):** support restricted but executable arithmetic/control-flow/arrays/struct/pointer modules; independently import `.ll` and `.bc`; reconstruct PolyIR; compile to C# and Rust; test actual Clang-produced `.bc` and PB-produced `.ll` without source-code reimplementation.

**L2 — LLVM exporter (after P2; reuse PB emitter):** generate and verify LLVM modules from C#, JS and PB numeric algorithms; execute via an LLVM target toolchain and prove differential equivalence. Expand intrinsics, exception support, attributes, vectors and foreign ABI gradually. Verify `.ll` and `.bc` round trips.

**G1 — GCC integration feasibility (after L1, research gate):** inspect GCC plugins/export methods, licensing/version compatibility and language frontends. Only implement a GIMPLE/GENERIC bridge if incremental independent tests show it safely preserves the requested semantics; otherwise keep a clearly documented unsupported status. Do not claim GCC-frontends-for-free.

<a id="s26-6"></a>
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

<a id="s27"></a>
## 27. C# → 8086/x86-16 DOS via semantic lowering (normative SDD + BDD + TDD)

<a id="s27-1"></a>
### 27.1 Product definition

Implement a **supported, explicit subset** of C# compiled directly to DOS `.COM`/`.EXE` or verified assembly/OMF units via Polypiler.Core's semantic IR, PB-Compiler x86-16 native machine pipeline, linker and runtime. This is not a normal .NET assembly and does not require a CLR if the surviving algorithm and minimal generated runtime are fully lowered. Target profile MUST specify 8086/186/286/386 variant, real-mode ABI, near/far/huge address spaces, stack limits, memory model, DOS services and allowed runtime surface. A successful numeric kernel is not a claim of complete .NET compatibility.

<a id="s27-2"></a>
### 27.2 Lowering rules

- Support a first slice: C# integer/bitvector arithmetic, methods, value structs and enums, `for`/`while`/`switch`, fixed arrays, known pure helpers, direct external DOS interfaces. Later add scalarized classes, static initialization, monomorphized generics, delegates, interface devirtualization, optimized `Span<T>`-style views and byte buffers.
- Lower deterministic disposal to exact cleanup edges or synthesize minimal DOS-safe helpers. For supported file/device wrappers, bind or generate DOS interrupt/runtime operations while preserving errors, handles, cleanup timing and non-ownership effects; unknown .NET BCL APIs remain explicit unresolved dependencies.
- Support exceptions, GC, coroutines, reflection and async **only** per independently verified capability profile; do not silently turn off exceptional cleanup, aliasing, arbitrary object identity, scheduling or reachable reflection. Generate minimal runtime when permitted and feasible; otherwise fail with structured diagnostics.
- Use the PB x86-16 virtual-register/instruction-selection/assembler pipeline, preserve segment address spaces and near/far pointer provenance, and validate emitted executable layout and relocation rules. LLVM's modern x86 backend is not a drop-in 16-bit real-mode target.
- Use range/type/layout analysis to narrow 32/64-bit C# operations where proven; otherwise synthesize multiword implementations. Keep effects for `volatile`, external runtime APIs, unknown I/O, inline assembly and arbitrary memory references.

<a id="s27-3"></a>
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

<a id="s27-4"></a>
### 27.4 Work packages

**D0 (after P2):** C# -> PB-style semantic/SSA importer alignment, x86-16 target profile, checksum/bit hack/array test harness using emulator, no unsupported-construct optimism.

**D1 (after D0):** safe deterministic cleanup, known DOS file calls and exceptions where implemented, distinct capability states and generated resource helpers.

**D2 (after D1):** selective class/generic/iterator lowering and runtime synthesis; optimize against PB x86-16 reference emitted code and measure memory footprint/cycles.

<a id="s27-5"></a>
### 27.5 Definition of supported

A C# -> DOS profile is `verified` **only** if a genuine DOS executable runs in a pinned emulator and independently matches return values, buffers, external effects, cleanup order and error outcomes for the published subset. Report numeric/algorithmic subset separately from full managed-language semantics; no generic `.NET -> DOS` claim.

<a id="s28"></a>
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

<!-- PRD content ends -->

[PRD index](../PRD.md) · [Previous: Recovery acceptance and gates](06-recovery-acceptance-and-gates.md) · [Next: Interop and language semantics](08-interop-and-language-semantics.md)
