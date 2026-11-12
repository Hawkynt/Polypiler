# PRD part 08 — Interop and language semantics

**Status:** original PRD requirements; planned capabilities are not implementation claims.
Original section numbering is retained.

[PRD index](../PRD.md) · [Previous: Language and platform integration](07-language-and-platform-integration.md) · [Next: Interop acceptance and execution](09-interop-acceptance-and-execution.md)

**Architecture guides:** [Foreign interoperability](../architecture/INTEROP.md) · [Language lowering](../architecture/LANGUAGE-LOWERING.md) · [Memory and effects](../architecture/IR.md)

<!-- PRD content begins -->
<a id="s29"></a>
## 29. PolyInterop — foreign library identity, type layout and ABI legalization (normative SDD)

<a id="s29-1"></a>
### 29.1 First-class foreign declarations and APIs

**Requirement INT-001:** A source-level import of a DLL, shared object, static object library, runtime entry point, COM interface, or arbitrary external symbol MUST survive translation as a typed `ForeignImport`/`ForeignCall` unless a verified semantic intrinsic replaces it. The original library name, exported symbol or ordinal, linkage mode, OS, architecture, ABI, visibility, source toolchain, version, calling convention, error behavior and observable effects are preserved. Do not mistake an unrecognized external function for pure code. Do not require translating the body of an external library to translate a caller.

A C header can declare an external function without specifying which DLL supplies it. Resolve the actual library name from import library, linker inputs, executable import table, module manifest, or an explicit user binding, rather than fabricating it. Distinguish static imports, delay-load imports, `dlopen`/`dlsym`, `LoadLibrary`/`GetProcAddress`, COM/Automation, callbacks and dynamic symbol lookup; static rewriting of dynamic loading is permitted only when observable load/failure timing is proven unchanged.

`ForeignFunction` MUST track parameter passing classification, return classification, argument order, caller/callee stack cleanup, register use, varargs ABI, pointer indirection, ownership, lifetimes, aliasing, encoding, exception/signal behavior, thread-local error state and memory effects. Emit target-native binding syntax when compatible: PowerBASIC Windows `DECLARE ... LIB ... ALIAS`, DOS PowerBASIC `DECLARE ... CDECL ... ALIAS` plus supported OMF linkage, C/C++ external declarations, Rust `extern`, C# P/Invoke/LibraryImport, Delphi `external`, Perl/Python FFI, etc. Never conflate DOS `.LIB`/OMF with Windows PE DLL imports.

**Requirement INT-002:** API recognition MAY replace a foreign runtime call with canonical IR only when a versioned semantic mapping proves equivalent behavior (overflow/rounding, mutation, exceptions, encoding, ABI-visible effects, domain constraints). Otherwise preserve the call, generate a verified binding or bridge, or issue a structured unsupported-capability diagnostic. A nominally similar method name is not proof.

<a id="s29-2"></a>
### 29.2 Layout schema and synthesized UDTs

**Requirement INT-003:** Define `ForeignLayout` independently from semantic type and internal representation. At minimum include total size, required alignment, field offsets and sizes in **bits**, field/storage encoding, signedness, bitfield placement and allocation scheme, explicit and implicit padding, union overlays, flexible array members, array stride, endianness, pointer width/address space, nested record and base subobject layout, ABI compiler identity/options, and packing pragmas. A type's layout identity is not its semantic identity.

A target representation is legal ONLY when its storage and call ABI match the verified source interface. The layout solver SHALL try, in order, native `pack`/alignment directives, explicit-field-offset UDTs, byte-backed UDTs, byte buffers with generated typed accessors, and native ABI bridge stubs. Inserting explicit padding is not a solution if the target compiler **already inserts excess padding**. If the original ABI demands 5 bytes with offsets `0:u8, 1:u32`, a default 8-byte UDT is invalid even if it has the same field names. A `[u8; 5]`-like storage with unaligned byte accessors and correct array stride may be valid for a by-pointer call, subject to pointer lifetime/alignment constraints.

**Requirement INT-004:** Separate `CompatibleStorageLayout`, `CompatiblePointerContract` and `CompatibleCallingConvention`. A byte-exact record passed **by pointer** may be legal when that record passed **by value** is not, because aggregate register/stack classification differs. Structs returned in registers or hidden return buffers, bitfields, unions, nested aggregates, callbacks, and variadic calls require real toolchain ABI oracles. Do not assume `repr(C)` or `StructLayout` alone establishes ABI compatibility across targets. Unaligned field access must use legal bytewise loads or compiler intrinsics, not invalid references.

`AbiMaterialize`, `AbiBorrow`, `AbiCopyBack`, `AbiRelease`, `AbiBridge` and `AbiCall` are IR-level transformations with explicit lifetime/effect constraints. Layout-dependent allocations are not optional when a foreign callee retains a pointer. ABI-exposed structures/array strides are observable and must not be silently narrowed; internal non-escaping copies may use more efficient representations.

<a id="s29-3"></a>
### 29.3 Data marshaling and multi-call fusion

**Requirement INT-005:** Generate a `MarshallingPlan` per foreign boundary: value transformations (`Encode`/`Decode`/`Convert`/`Quantize`), byte order, alignment, packing, pointer allocation, pinning, ownership and cleanup, `in/out/inout` copy-back, callbacks, error extraction and bridge dependencies. Perl may use `pack`/`unpack` to create C-compatible memory and an FFI provider for the actual native invocation; `pack` alone does **not** perform the call or establish pointer lifetime. Support typed arrays and byte-backed records for runtimes without native pointers.

Across multiple compatible foreign calls, analyze effects and escape to preserve packed buffers without redundant pack/unpack. Unpack only demanded fields; invalidate cached fields when an external callee can mutate them. Do not fuse across an observable mutation, retained pointer, error check, callback or synchronization boundary. The layout compiler may call `Polypiler.Permutation` for legal bitfield packing, bit reversal, byte order conversion and channel swizzling.

<a id="s29-4"></a>
### 29.4 Exotic numerics and legacy ABI examples

**Requirement INT-006:** Distinguish exact serialized floating-point representations from their parameter/return ABI. `Extended80`/x87 Float80 may use 80 significant stored bits in a 10-byte payload, optionally padded to target ABI size. Rust may synthesize an exact storage type and software operators, but must not blindly pass a 10-byte Rust struct as native `long double` by value. Use a verified compatible ABI or compiled bridge. Target toolchains may define `long double` differently (including binary64); source compiler identity and platform must be pinned.

A VB6 ActiveX DLL commonly exposes COM/Automation interfaces rather than plain C exports; do not assume standard Automation has an 80-bit float parameter. Inspect typelib/calling contract, preserve `VARIANT`, `BSTR`, `SAFEARRAY`, `Currency`, `Decimal`, HRESULT semantics and reference counting; use exact value conversion, byte buffer interface, or bridge only as supported. Respect 32/64-bit in-process compatibility. Different numeric representations must never be silently narrowed at the external boundary.

<a id="s29-5"></a>
### 29.5 Required native test fixtures

Create test C/C++/Rust/Delphi and target-compatible BASIC libraries with known symbols, plus a Windows 32-bit DLL when the toolchain is available. Test layout and invocation independently for: 5-byte packed record and 8-byte natural record, 1/2/4/8-byte alignment, unaligned fields, packed bitfields, unions, nested records, array strides, in/out buffers, pointer-retaining callees, by-value return and parameter, callbacks, `cdecl` versus `stdcall`, decorated names and ordinal imports, dynamic loading failure, C varargs, multibyte strings and ownership/error conventions. Compare `sizeof`, `alignof`, `offsetof`, byte snapshots, pointer stability, call traces, return values and mutation.

An external library's missing architecture/compiler or unverified ABI is a blocker for a *verified* result, even if a generated declaration compiles.

<a id="s30"></a>
## 30. High-level language constructs, object semantics and structural recovery (normative SDD/DDD)

<a id="s30-1"></a>
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

<a id="s30-2"></a>
### 30.2 Specialization and runtime closure

**Requirement OBJ-002:** Prefer *elimination* of unnecessary object machinery before emulation. Devirtualize dispatch when receiver type is proved; monomorphize generics when closed instantiations are known; scalarize nonescaping objects; resolve overloads before target source emission; collapse constant type switches; remove unused metadata; fuse generators with consumers when evaluation order and laziness allow. Do not eliminate identity, reflection-visible shape, finalizers, weak references, dynamic class loading, `is`/`as` outcomes or foreign ABI layout when observable.

**Requirement OBJ-003:** Emit a **demand-driven runtime closure**, not an entire source standard library. The runtime dependency graph records minimal generated services for allocation/GC/reference counting, virtual dispatch, error unwinding, metadata, resumable execution and scheduling. Report source feature support by semantics and environment, not syntactic keyword recognition. DOS/6502/resource-constrained targets must reject impossible runtime or memory requirements, or offer an explicit separately selected emulation profile.

<a id="s30-3"></a>
### 30.3 Coroutine and async recovery

**Requirement COR-001:** `Suspend`, `Resume`, `Yield`, `Receive`, `Await`, `Complete`, `Cancel`, `Dispose` and exceptional unwinding form separate semantic events. Coroutines are not automatically threads, futures or `async` tasks; an iterator need not allocate a scheduler. A frontend recognizing a compiler-generated state machine may recover a `Coroutine`/`Generator` IR node only after proving state/transition, value, lifetime and exception equivalence. The inverse lowering into manual state machines must preserve local captures, re-entry, ordering and cleanup across every suspension point.

A C# `await using` scope may suspend **during cleanup**. A PowerBASIC output may use generated state records and entry functions; a real-mode DOS target has no implicit thread/task environment. Concurrency/scheduling must be explicit in target profiles. A coroutine containing an external unknown call is not automatically movable or synchronous.

<a id="s30-4"></a>
### 30.4 Exceptions, error recovery and deterministic resources

**Requirement ERR-001:** Preserve source **statement boundaries** required for classic VB `On Error Resume Next`: an error may resume at the next statement, and `Err` state can be observed. This is not equivalent to a single `try/catch` wrapped around a whole function, nor to blindly ignoring all errors. `Resume`, handler enabling/disabling, nested procedure boundaries and error state inspection must be tested under the pinned VB dialect.

**Requirement RES-001:** `ResourceScope` and `CleanupRegion` are recovered from equivalent low-level `try/finally`, `defer`, RAII and disposal patterns only with proof of acquisition, exactly-once disposal, null behavior, normal and exceptional exits, cleanup order and cleanup errors. `using` is not a general macro for arbitrary `Dispose` calls. Backends without destructors generate explicit control-flow cleanup. Resource identity/pointer ownership and foreign allocations remain tracked across calls and suspension points.

<a id="s30-5"></a>
### 30.5 First-class bidirectional tests

Use a real C++ diamond with **both virtual and nonvirtual** inheritance, a Python MRO fixture, a C# generic interface/anonymous-record/type-switch/overload fixture, a Kotlin coroutine/generator fixture, a VB6 statement-level error-recovery fixture where genuine toolchain availability permits, a C variadic ABI fixture and a Rust/Perl generated state machine. Target PowerBASIC/C/Rust/Delphi backends must compile the supported subset and compare dispatch results, object identity when relevant, cleanup order, suspension/resumption traces and error states. Mark exact unsupported semantics as blocked, not silently simplified.

<!-- PRD content ends -->

[PRD index](../PRD.md) · [Previous: Language and platform integration](07-language-and-platform-integration.md) · [Next: Interop acceptance and execution](09-interop-acceptance-and-execution.md)
