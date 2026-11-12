# PRD part 05 — Semantics and recovery

**Status:** original PRD requirements; planned capabilities are not implementation claims.
Original section numbering is retained.

[PRD index](../PRD.md) · [Previous: Roadmap and delivery](04-roadmap-and-delivery.md) · [Next: Recovery acceptance and gates](06-recovery-acceptance-and-gates.md)

<!-- PRD content begins -->
<a id="s17"></a>
## 17. Fundamental semantic-preservation contract (normative; applies to every pass)

<a id="s17-1"></a>
### 17.1 Non-negotiable transformation law

A translation/optimization may replace an operation only if it preserves the **declared observable behavior of the selected source profile**, subject to explicitly selected compatibility/approximation modes. Observable behavior includes return values; mutations to reachable memory and externally visible state; file/device/network interactions; external calls; exception/trap types and ordering; program termination; resource lifetime where visible; concurrency/happens-before; hardware I/O traces where required; and timing/cycle behavior **when** the program contract makes timing observable. Source undefined behavior remains undefined only where the source language/profile actually permits that conclusion; do not invent UB for defined legacy/managed operations.

The compiler SHALL retain a recognized but unlowerable instruction in canonical/machine IR. It SHALL NOT imply that retaining the IR means it can execute on every target. The target legalizer chooses `MapNative`, `GenerateHelper`, `PreserveABI`, `Emulate`, `DeferredResolution`, or `RejectWithDiagnostic`. For unknown operations, cross-architecture output may legitimately fail. **Preservation is compulsory; portability is conditional.**

Optimization decisions SHALL distinguish: (1) instruction/operation semantics known or opaque; (2) legality of a proposed rewrite proven/conditional/unknown; (3) execution support on the target native/helper/adapter/emulator/none. An operation may be known and nonreorderable; opaque but preservable on its source platform; or pure but unavailable on a target.

<a id="s17-2"></a>
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

<a id="s17-3"></a>
### 17.3 External API normalization registry

Every known BCL/JDK/CRT/PowerBASIC/Python/Perl/JS/NCNN call mapping has: `{LibraryIdentity, LibraryVersionRange, SymbolSignature, GenericInstantiation, SourceSemantics, Throws, Effects, Preconditions, CanonicalIntrinsic, TargetLowerings, Fallback, TestOracle, License}`. Examples:

- .NET `BitOperations.PopCount` and Java `Integer.bitCount` → width-specific `IntMath.PopCount`.
- Java `Integer.rotateLeft` and recognizable C/JS bit hacks → `RotateLeft` with exact shift-count normalization.
- C `memmove` → overlap-safe `Memory.Move`; **not** `memcpy` when overlap possible.
- `System.Array.Copy` → typed `Memory.Copy` only when array covariance and exception behavior cannot change result.
- .NET LINQ `Where/Select/Reduce`, Java streams, Python comprehensions, Perl `grep/map`, JS array methods → sequence operations after effect and laziness checks.
- PowerBASIC runtime calls → identified semantic operation, original runtime import with ABI, or a helper implementing the exact dialect behavior.

Unknown APIs must never be replaced by functions with vaguely similar names. The generated artifact's manifest states unresolved external symbols, exact target runtime requirements and any emulation bridge. A target that cannot resolve them fails with a stable diagnostic.

<a id="s17-4"></a>
### 17.4 Inline assembly is syntax, not a universal optimizer fence

Assembler frontends SHALL decode supported opcodes as ordinary machine-semantic constructs interleaved with surrounding source instructions. Register operands, embedded labels, jump targets, embedded data, clobbers, constraints, address spaces, instruction prefixes and assembler dialect are retained. The frontend must track source-level references into ASM and ASM-written values flowing into source code, including flags, carry and memory.

The optimizer SHALL be able to recover high-level semantics and erase all ASM when the complete block is provably portable; it SHALL ALSO be able to lower surrounding high-level code to target machine instructions and emit an entirely assembly implementation. A mixed output remains valid when target compilation supports inline ASM and the effect contract remains satisfiable. **Entirely high-level** and **entirely ASM** are choices, not unconditional success conditions.

Example recognition target: `MOV; BSWAP; MOV` → `ReverseBytes32`; `CLC; LDA; ADC; STA` → `AddWithCarry8` and affected flags; x86 `ROL` → `Rotate` if flag consumers are dead or modeled; packed XOR/shift idioms → logical bit-routing. Unknown `INT`, `OUT`, `SYS`, `POKE`, custom runtime calls or self-modifying instruction bytes remain represented unless platform and effect proofs permit other lowering.

<a id="s17-5"></a>
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

<a id="s18"></a>
## 18. Binary, bytecode and historical-language recovery (SDD)

<a id="s18-1"></a>
### 18.1 Three source paths and two lowering horizons

1. **Text/source path:** parse → bind → source semantic IR (C#, JS, TS, Python, Perl, Kotlin, Rust, C/C++, BASIC, Java, Go, Delphi, PHP, Ruby).
2. **Token/p-code path:** decode dialect+version → source or stack instruction representation → dialect semantics → canonical IR (C64 BASIC V2, C128, GW-BASIC, specific Microsoft p-code variants).
3. **Bytecode/machine path:** decode/disassemble → recover control flow → stack/register SSA → memory/effect modeling → canonical IR (MSIL, JVM, WASM, 6502, x86/x64, ARM32/Thumb/AArch64, later Z80/etc.).

**Fast horizon:** recover a verified *function-level, closed computational kernel*, with explicit imports and argument buffers. **Extended horizon:** translate executable modules with multiple functions, state, classes and external dependencies under documented platform contracts. Arbitrary whole-application decompilation and OS/runtime recreation are not first-release promises.

<a id="s18-2"></a>
### 18.2 Binary input manifest and discovery

A binary frontend is invoked with a structured manifest when code/data boundaries cannot be inferred safely. Inputs include executable/firmware/PRG path and hash, ISA variant, endianness, mode, origin/address, entrypoints, code/data ranges, relocation map, import resolution, calling convention, stack profile, CPU features, memory map, return/output contract, and reference runner. For native binaries, indirect branches and jump tables need validated discovery; uncertain reachable bytes remain undecoded until analyzed, not guessed into instructions.

Function boundaries can be recovered using symbols/debug information, signatures, stack analysis or caller-supplied hints. Discovered boundaries receive confidence/proof status; only proven semantic facts may authorize aggressive rewrites. Missing dispatch targets, executable code generation and relocations must block claiming a verified standalone translation unless safely emulated.

<a id="s18-3"></a>
### 18.3 Managed and portable bytecode

**MSIL:** read PE/CLI metadata, method signatures, generic instantiations, typed evaluation stack, instructions (`conv`, `box`, `unbox`, `call`, `callvirt`, `ldtoken`, `newarr`, `ldelema`, exception regions), static initialization and managed memory. Normalize known .NET BCL calls. Unsupported reflection, dynamic assembly load, runtime code generation, unmanaged interop and high-level object-model mismatches require helpers or diagnostics. Deliver source outputs including `MSIL→PHP`; also support IL emission as a backend profile.

**JVM:** parse classfile constant pool, stack maps, method bytecodes, `invokedynamic`, exception tables, class init, object identities, arrays and exception/error behavior. Normalize known JDK operations. Model integer overflow and Java shift counts rather than target-language defaults. Deliver `JVM→Delphi` demo; backend JVM bytecode generation via verifier-clean classfile.

**WASM:** parse core modules with typed stack, control nesting, imports, globals, tables, linear memory, traps and defined widths; only proposal features explicitly enabled in profile. WASM memory access must preserve little-endian semantics and trap conditions. Support WASM frontend and backend; test with real runtime.

<a id="s18-4"></a>
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

<a id="s18-5"></a>
### 18.5 Tokenized BASIC, p-code and mixed loaders

C64 BASIC V2 PRG decode: load address (commonly but not universally BASIC start address), line links, line numbers, token values, quoted strings, `REM`/`DATA` tokenization rules and PETSCII; preserve source text/line metadata. Source back-end may output listing; binary back-end emits valid PRG with resolved links, line numbers and terminators. Validate with independent tokenization and emulator execution. `POKE`, `PEEK`, `SYS`, `USR`, ports and hardware accesses use effectful memory/runtime ops, never guessed ordinary variables.

GW-BASIC token files differ from C64 PRG; detect by format/profile and parse numeric token encodings. **Microsoft p-code** denotes a family of compiler-specific intermediate formats rather than one universal format. Every supported p-code profile SHALL identify exact compiler generation/runtime and opcode definitions with verified decode/encode fixtures; no invented generic decoder. Where only decode is supported, mark backend planned and reject p-code emission.

A BASIC loader invoking `SYS` or `CALL` can be linked to a known address/embedded machine-code segment; lift both into a shared module with explicit address-space/calling contracts. When the target is C#, hardware-specific behavior may require an emulator/device abstraction. When targeting the original machine, preserve raw I/O operations, with timing fidelity selected by profile.

<a id="s18-6"></a>
### 18.6 Target output constraints

Emit either (a) a portable equivalent implementation, (b) an idiomatic package with documented/verified runtime helper, (c) target assembly and linker objects, (d) a tokenized PRG, (e) a bytecode module, or (f) a precise failure. The requested target format and dependency mode (native-only, helper allowed, emulator allowed, none) are explicit. For constrained platforms, perform memory-size checks, allocation planning, calling-convention matching, code-size budgeting and required CPU feature checks before declaring output viable.

---

<a id="s19"></a>
## 19. Semantic recovery, de-optimization and loop reconstruction (SDD)

<a id="s19-1"></a>
### 19.1 Purpose and core invariants

Reconstruct a simpler **semantic** operation from compiler-specific or hand-optimized expressions, interleaved instruction streams, loop unrolling, software pipelining and specialized primitives. Optimizations are allowed in *both* directions: high-level → low-level and low-level → high-level. Algorithm recognition is not evidence of the original author's subjective intent; generated diagnostics should say “proven equivalent to X”, not “author intended X”.

All recovered high-level ops preserve a machine-/source-level fallback graph with provenance and proof certificates where possible. Recovery is optional when its cost is worse; the target cost model may reselect the original representation after normalization. Source ordering, memory effects, exceptions, rounding, signedness and lane isolation constrain rewrites.

<a id="s19-2"></a>
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

<a id="s19-3"></a>
### 19.3 Specific recovery contracts

**Shift-add multiplication**: `(x<<3)+(x<<1)` can recover `Mul(x,10)` where integer width, shift count, overflow and signedness are compatible. Return to shifts on targets without fast multiply; do not assume algebra valid for checked overflow or undefined signed C overflow.

**Min/max idioms**: Recover `Min(a,b)`/`Max(a,b)` only if sign extension, comparison, arithmetic overflow and source evaluation count match. Branchless mask selection can map to `Select` and later SIMD blend.

**Swap**: XOR-swap may become `Swap(a,b)` only when aliasing, volatility, distinct addresses and intermediate writes permit it. Same-address XOR-swap is a mandatory negative fixture.

**Interleaving**: Independently scheduled register SSA chains may be sliced into expressions, then recombined into SIMD/SWAR lanes. Do not reorder across effects/traps that constrain observation.

**De-unroll**: For repeated statements with provably affine addresses and equivalent iteration bodies, recover `IterationDomain(start,count,step)` plus body, remainder and tails. Preserve original step-dependent errors and aliasing. Unrolled stores aren't necessarily a loop when intermediate writes are observed.

**Software pipeline**: Identify loop-carried dependence distances and prologue/steady-state/epilogue. Do not collapse a pipeline until overlap of iterations, possible faults and dependency order are proven equivalent.

**Float**: FMA recognition is legal only if source had fused semantics or an explicit permissive accuracy mode. Strict IEEE/x87/MBF operations retain specified intermediate rounding, precision and exception behavior.

**Crypto**: Secret-dependent branches/table accesses cannot be introduced by optimized fallback. Branchless input must remain constant-time where the declared security contract requires it; SIMD/SWAR changes are verified for leaks and memory access behavior, not just values.

<a id="s19-4"></a>
### 19.4 Analysis and proof engines

Use pure-expression e-graphs/equality saturation with width- and signedness-typed rewrite rules and limited extraction budgets; SSA DAG reconstruction for interleaving; MemorySSA and alias/escape analysis for effects; affine/scalar-evolution analysis for iteration; bitvector SMT for finite-width idioms; bounded symbolic execution for intricate small basic blocks; translation validation for optimized output where feasible. No `Unknown`/timeout/failing proof may be treated as `Proven`.

Every pass returns `(candidate, proofStatus, obligations, fallback, costEvidence)` and stores a structured optimization report. For transformation that lacks full automatic proof, permit only a specification-driven, previously validated rule with explicitly checked preconditions and rigorous differential/property tests; label proof class accurately. Proof checker should be independent of candidate generator for high-risk transformations.

<a id="s19-5"></a>
### 19.5 Loop recovery milestones

- `S1`: recognize straight-line 2/4/8-way fully unrolled pure array loops; emit equivalent `PolyIteration` and re-unroll with target profiles.
- `S2`: recognize partial unrolling and peeled/remainder loops, including constant loops in PB graphics and cipher byte operations.
- `S3`: reconstruct interleaved arithmetic and vectorized motifs; preserve demanded flags.
- `S4`: recognize selected software pipelines, Duff's device and compiler lowering artifacts with proof/evidence.
- `S5`: cross-function and mixed ASM/iteration synthesis subject to call-effects constraints.

Acceptance evidence includes a before/after SSA/iteration dump, target-generated code, an equivalence proof or validity status, source/target differential executions, and target-specific code-size/speed comparisons.

---

<a id="s20"></a>
## 20. PolyPermutation — bidirectional BitPerm integration (SDD + algorithm contracts)

<a id="s20-1"></a>
### 20.1 Existing implementation and migration boundary

`Hawkynt/Hawkynt.github.io/BitPerm/index.html` currently contains the routing UI and JavaScript `BUILDING_BLOCKS` registry, `apply`, `canRoute`, `findRoutings`, `genCode`, `generateAllOperations`, `solveExhaustive`, `analyzeBitMovement`, `verifyOperationReport`, greedy search and CPU cost presets; its README documents 51 tests across 10 sections. Extract these as test-backed **candidate functionality**, not a validated complete solver. Keep the UI working with the existing engine until the replacement passes the same fixtures; then connect it to a versioned Polypiler.Core library/API and retain a rollback path.

`https://programming.sirrida.de/bit_perm.html` supplies additional *external algorithmic references* for butterfly/Beneš networks, PEXT/PDEP, shuffle/unshuffle and multi-stage permutation synthesis; examine licensing before copying any code or tables. Implement independently where necessary; reference ideas/algorithms and cite provenance rather than assuming redistribution rights.

<a id="s20-2"></a>
### 20.2 Formal semantic hierarchy

- **`BitPermutation<W>`:** total bijection over W bit positions, exactly one input bit per output and exactly one output per input. Can invert and compose.
- **`BitRouting<W,InW>`:** each output bit is an input bit, optionally inverted, or constant 0/1; permits duplication, dropping inputs and partial/don't-care constrained outputs; not generally invertible.
- **`BitBooleanTransform`**: outputs are Boolean expressions of input bits; includes GF(2) XOR matrices and nonlinear logic; must retain truth-table/circuit semantics beyond routing.
- **`BitOperationNetwork`**: implementation graph that realizes a mapping/transform with registers/temporaries, target instructions, masks and costs; **not** the same thing as semantic mapping.
- **`BitCondition`**: allowed input domain and constrained output bits; don't-care means result unconstrained on specified bits, not undefined source behavior outside its contract.

A compact descriptor may use `OutputBit[i] = Source(j) | NotSource(j) | Const(0/1) | DontCare` plus domain constraints; full Boolean transforms require expression DAG/BDD/AIG or equivalent. Every element records widths, shift semantics, endianness when mapping to bytes, sublane boundaries and evaluation predicates.

<a id="s20-3"></a>
### 20.3 Recovery (reverse direction)

1. Extract the pure bitvector computation by SSA slicing. Stop at unmodelled external effects; use exact captured inputs/outputs and widths.
2. Propagate symbolic bit provenance through AND/OR/XOR/NOT, shifts/rotates, byte reversal, bitfield extraction/insertion, `PEXT`/`PDEP`, PSHUFB and target shuffle instructions.
3. For multiply-and-shift bit hacks, use the **actual modular multiply** with carry propagation; never model its semantics as OR of shifted copies. The existing BitPerm `mul.apply` uses such an OR for routing analysis only. Candidate recovery requires symbolic proof/independent execution.
4. Build a bit-routing candidate where every constrained output is a source bit or constant; if any output depends on multiple bits, classify as `BitBooleanTransform` or leave as generic SSA.
5. Prove candidate is equivalent to original under source input constraints (and target-defined behavior). Basis-vector tests reveal candidates only; they are **insufficient proof** for nonlinear arithmetic.
6. Recognize higher-level ops (`ReverseBits`, `ReverseBytes`, `Interleave`, `Deinterleave`, `BitCompress`, `BitExpand`, `ByteShuffle`, butterfly/Beneš network). Keep exact mapping as ground truth.

<a id="s20-4"></a>
### 20.4 Canonical algebra and optimization

For a bijection P: `P^{-1}(P(x))=x`; `P₂(P₁(x))=(P₂∘P₁)(x)`; `P(a XOR b)=P(a) XOR P(b)` where operands share the same width/domain; masks and Boolean ops may be pushed through P by permuting constants. For `BitRouting` with duplication or constants, do not assume inversion or distribute transformations without checking the precise operation algebra. Compose adjacent layout changes, propagate arrangement across compatible pure loops and vector operations, and eliminate pack→unpack when external ABI/layout boundaries remain satisfied.

Extend layout propagation to `Vector<T>`, `BitVector`, tensor shape/stride/transpose and planar/packed video/pixel formats; coordinate with memory-SSA so layout changes never reorder observable I/O or alias-sensitive writes.

<a id="s20-5"></a>
### 20.5 Synthesis (forward direction)

Candidate instruction grammars: shifts+masks+OR/XOR; rotations; single/dual PEXT/PDEP; byte shuffle (`PSHUFB`, `TBL`); bit reversal (`RBIT`); byte reversal; bitfield extract/insert; RLWINM and similar combine operations; multiplication tricks with exact carry-aware proof; LUT; SWAR; SIMD; butterfly network; Beneš network; GF(2) matrix/Boolean circuit synthesis where needed. Generate **typed Target IR**, not C fragments via string concatenation; backend performs language syntax and target ABI legalization.

Synthesis is deterministic for fixed seed/budget/profile. Search strategies: fast greed, dynamic programming for constrained patterns, Dijkstra/A* over verified candidate-state transitions, bounded SMT/superoptimization for small widths, decomposition/heuristics for larger widths. For multiplication, actual numeric semantics must be checked after the proposed mask/shift combination. An upper-bound search budget cannot justify a claim of global optimality; report `heuristic`, `best-found`, or `optimal-under-grammar` with proof and search bounds. Candidate cost includes instruction latency, throughput, ports/micro-op count where available, code bytes, temporaries/register pressure, table traffic, branch costs and platform constraints. Measured hardware results may supersede estimates only when reproducible and tagged to exact CPU model.

<a id="s20-6"></a>
### 20.6 Source-to-source and inverse tests

Test these independently in C#, Rust, C++, PowerBASIC, JavaScript and 6502 where profile allows: identity, reverse, byte swap, halves swap, arbitrary bijection, duplicate source bits, invert selected bits, set constants, output don't-cares, permutation composition, inverse cancellation, PEXT/PDEP, PSHUFB, RLWINM, carry-producing multiply, and 128/256/512/1024/2048/4096-bit software fallback. Widths above native registers are logical aggregates; no false claim of single-register ISA support.

For every synthesized network N representing mapping M, use independent oracle `Evaluate(M,input)` and check `Evaluate(N,input)` across exhaustive 8-bit inputs, appropriately selected exhaustive 16-bit cases, generated/properties for bigger widths and SMT where feasible. Check all declared input-domain constraints. For a proven bijection, test inverse and composition. Verify generated code compiles/runs in actual target toolchain, not merely that its emitted text resembles expected instructions. Compare with current BitPerm browser results; disagreements are investigated, not resolved by changing independent expected mappings.

<a id="s20-7"></a>
### 20.7 Prohibition of unsafe normalization

- Do not automatically rewrite a modular multiply to independent-bit routing when carries can affect selected outputs.
- Do not assume a `PSHUFB` shuffles arbitrary bits when it shuffles bytes with target-specific zeroing/lane behavior.
- Do not assume `PEXT/PDEP` can duplicate an input bit; they implement specific compact/extract/deposit semantics.
- Do not claim 128-bit source ops are automatically legal on an 8-bit 6502 without software lowering.
- Do not claim “optimal” simply because bounded Dijkstra terminated before its search limit; the explored candidate grammar must be complete for the claimed bound.
- Do not conflate “don't care output bit” with externally observable unknown or nondeterministic source state.

<a id="s20-8"></a>
### 20.8 Standalone PolyPermutation CLI/API

Minimum machine-readable input: `{width,inputWidth,mapping,requiredOutputMask,inputDomain,targetProfile,objective,allowedPrimitives,proofMode}`. Outputs: exact semantic mapping, candidate typed networks, cost estimates and measured evidence when available, proof result, generated code and runnable tests. `polypiler permute recover` accepts source or machine IR and reports recovered routing/Boolean transformation. `polypiler permute synthesize` accepts a mapping. `polypiler permute verify` verifies a candidate against a reference. `polypiler permute explain` reports why a candidate was chosen/rejected and its limitations.

---

<a id="s21"></a>
## 21. Machine-code and retro target-specific design (SDD + DDD)

<a id="s21-1"></a>
### 21.1 MOS 6502 as both compiler input and output

**Frontend:** support ca65-style or another named 6502 assembler dialect plus raw 6502 machine-code entrypoint. Assemble reference snippets with pinned assembler; lift resulting code with NMOS 6502 CPU-model semantics. Reconstruct register/flag SSA, zero-page allocation identity, program stack writes and status-dependent branches, and recognize known arithmetic/permutation loops. Illegal opcode policy explicit per CPU model. Lift to C#, Rust or PowerBASIC with emulated memory and I/O only as needed.

**Backend:** lower typed arithmetic and memory/iteration IR into 6502 instruction selections with register use and spill planning; choose zero-page temporaries by actual address availability and side-effect constraints. Implement multi-byte arithmetic via ADC/SBC carry chains; 16/32/64-bit arithmetic by helper routines when required; no 6502 multiplication instruction assumed. Emit assembly, symbol table and binary/PRG through pinned assembler/linker; assert code/data/stack and zero-page budgets against target memory profile. Separate `mos6502` generic bare-metal and `c64` runtime profiles (BASIC loader, PRG load address and device map).

Cycle-aware mode may optimize for raster/interrupt timing under a declared timing contract. Normal mode is functional equivalence, not cycle accuracy. Hardware-mapped reads/writes are not ordinary array accesses. Tests include branch crossing, zero-page wrap, decimal mode, flags, stack and self-modifying code negative cases.

<a id="s21-2"></a>
### 21.2 8086/x86 integration with PB-Compiler

Use PB-Compiler's established typed IR, x86-16 code generation, near/far pointer address spaces, MBF/x87 oracle and dialect behavior as reference and candidate reuse. A shared Polypiler.Core representation must cover segmented memory, compiler runtime calls and 16-bit ABI while also supporting x64/ARM flat and other address spaces. Do not break PB-Compiler's native output or test harness during adoption. Mixed inline ASM/control flow must be imported as semantic instructions with explicit register and memory effects; target may emit high-level C# or preserve/integrate x86 assembly if supported.

<a id="s21-3"></a>
### 21.3 Backend capability graph

Model capabilities as constraints and alternatives, not a monolithic yes/no: `Integer.Wide(bits)`, `Pointer.Native`, `Pointer.Fat`, `Union.Overlay`, `Stack.Memory`, `ByteSwap`, `SIMD.Width`, `MemoryMappedIO`, `Interrupt`, `TargetAsm`, `Runtime.External(symbol)`, `Exceptions`, `Threads`, `GPU`, `NPU`. Legalization rewrites until all IR ops are native, have a verified runtime helper, or produce a structured blocker. Keep track of helper closure, ABI, license, code size and compile/test evidence.

<a id="s21-4"></a>
### 21.4 Domain ports and dependency rules

New boundaries: `IBinaryCodec`, `IDisassembler`, `IInstructionSemantics`, `IProgramBoundaryResolver`, `IMachineStateModel`, `IExternalSymbolResolver`, `ITokenizedBasicDialect`, `IAssemblerEmitter`, `IBinaryWriter`, `IEmulatorOracle`, `ITargetCycleModel`, `IPermutationMappingSolver`. Machine-specific modules may depend on canonical IR/effect/type contracts; canonical optimizer must not depend on 6502/x86/ARM opcode enums or source ASTs. Native runtime adapters implement the semantic APIs; they do not redefine them.

---

<!-- PRD content ends -->

[PRD index](../PRD.md) · [Previous: Roadmap and delivery](04-roadmap-and-delivery.md) · [Next: Recovery acceptance and gates](06-recovery-acceptance-and-gates.md)
