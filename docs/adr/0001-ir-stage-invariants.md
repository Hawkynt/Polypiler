# ADR 0001 — IR stage invariants

Area: foundation

**Status:** accepted · **Date:** 2026-11-13 · **Issue:** [#19](https://github.com/Hawkynt/Polypiler/issues/19) (`ADR-IR-STAGES`) · **Index:** [decision records](README.md)

**Decides:** [PRD §4.2](../prd/02-core-architecture.md#s4-2), [§4.3](../prd/02-core-architecture.md#s4-3) "stage legality", [§8 P0](../prd/04-roadmap-and-delivery.md#s8) "document initial IR stage invariants". Background: [IR and analysis contracts](../architecture/IR.md), [compilation pipeline](../architecture/PIPELINE.md).

Issue names in `code font` (`IR-STAGES`, `MACH-IR`, …) are the backlog identifiers of the issues that implement or consume this decision.

## Decision

1. **One node set, one stage tag per function.** Every stage of PRD §4.2 uses the same typed SSA records and the same opcode registry. A function carries a `Stage`; a table gives each opcode the first and last stage it may appear in and each verifier rule the stage range it runs in. There are no per-stage node classes and no separate machine IR.
2. **Nine stages in one total order**, named in the [stage list](#stage-list). The names are stable: they appear in the serialized form, in diagnostics and in specifications.
3. **A stage is a promise about which constructs a function contains, never about its meaning.** Advancing a function rewrites nothing: it succeeds when the function already satisfies the destination's invariants and fails otherwise. Movement is forward only. Stages may be skipped, because every invariant is stated as a stage range and therefore holds cumulatively.
4. **A stage is entered before the passes that work in its vocabulary run.** The invariants that *remove* constructs are checked on entry; the constructs a stage *admits* may be produced while the function is at that stage. The [phase table](#stages-and-optimizer-phases) fixes which stage each optimizer phase works at.
5. **`Legalized` is the only stage parameterized by a target.** It holds for a function together with one target capability set, and means every remaining operation is in that set.
6. **The verifier runs on every function a pass changed, at the function's recorded stage**, and on every stage transition at the destination stage.
7. **A serialized dump may be in any stage** and always states it; see [serialized dumps](#serialized-dumps).

This is option 1 of the work order, with the tag on the function instead of the module. Options 2, 3 and 4 are [rejected](#rejected-options).

## Stage list

| Order | `Stage` name | Specification id | PRD §4.2 layer | What the function may still contain |
| --- | --- | --- | --- | --- |
| 5 | `Machine` | `IR.stage.machine-effects` | Machine/Bytecode Effects IR | Registers, flags and machine control flow of one declared machine profile |
| 10 | `Source` | `IR.stage.source-semantic` | Source Semantic IR | Full source contracts: unresolved source calls, dynamic values, sequence value forms |
| 20 | `Recognized` | `IR.stage.recognized` | Recognized IR | As `Source`, plus the recovered iteration vocabulary |
| 30 | `Value` | `IR.stage.typed-value` | Typed SSA / Value IR | No unresolved source call |
| 40 | `MemoryIteration` | `IR.stage.memory-iteration` | Memory + Iteration IR | As `Value` |
| 50 | `Specialized` | `IR.stage.specialized` | Specialized IR | As `Value`, plus guards and parallel or placed regions |
| 60 | `Representation` | `IR.stage.representation` | Representation IR | No dynamic value, no unresolved flexible type; physical layout may be named |
| 70 | `Execution` | `IR.stage.execution` | Execution IR | As `Representation` |
| 90 | `Legalized` | `IR.stage.legalized-target` | Legalized Target IR | Only what the target capability set lists |

`Order` is a sort key, not an ordinal: `SPEC-IR` already fixes 10 and 90 for its two seed documents, and `Machine` precedes `Source`. The enum order, the `stage(<Name>)` text in a function header and the specification ids all follow this table. `legalized` in lower case, as some issues write it, is the capability state of PRD §23.6; the stage is spelled `Legalized`.

Two neighbouring pairs — `Value`/`MemoryIteration` and `Representation`/`Execution` — differ by no verifiable invariant today. PRD §4.2 separates them by the *facts* available (ranges and known bits, then regions, alias and index domains) and by the *decisions* made (layout, then execution strategy); neither is a property of the nodes. Both names stay: the PRD mapping remains one to one, the enum of `IR-STAGES` stands, an opcode may still take either as a range bound, and an unused boundary costs one table row.

### Entry points

- A machine-code lifter delivers `Machine`. Only `MachineRaise` (`MACH-BIN-RAISE`) establishes `Source` for such a function.
- A source frontend delivers `Source`. So does a stack-machine importer (MSIL, JVM bytecode, WebAssembly): its evaluation stack is typed and already verified, so it builds SSA values itself and never uses `Machine` (`MACH-IR`).
- An emitter accepts `Legalized` only, verified against its own capability set.

## Stage × invariant table

Each cell says what the verifier enforces. "Same" means the rule is identical to the cell on its left.

| Invariant | `Machine` | `Source` | `Recognized` | `Value`, `MemoryIteration` | `Specialized` | `Representation`, `Execution` | `Legalized` |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **Block structure** | One terminator, last in its block; branch targets exist; the entry block has no predecessor | Same | Same | Same | Same | Same | Same |
| **Types** | Operand and result types satisfy the opcode's type rule; a register or flag access has the width of its profile entry | Opcode type rule; dynamic and flexible types admitted | Same | Same | Same | Opcode type rule; every type is concrete | Same, and every (opcode, type) pair is in the capability set |
| **Dominance** | Every use of an SSA value is dominated by its definition. Register and flag *contents* are named storage, not values, until machine SSA construction | Every use is dominated by its definition; unreachable blocks are exempt | Same | Same | Same | Same | Same |
| **Phi structure** | Phis lead their block; one incoming value per predecessor, none from a non-predecessor, none twice | Same | Same | Same | Same | Same | Same |
| **Memory bounds contracts** | Every access states one access contract (`bounds`, `inbounds #id`, `unchecked`); `inbounds` names an existing bounds proof; statically known violations, overlapping `Copy` and use after lifetime are rejected | Same | Same | Same | Same | Same | Same |
| **Effect and order constraints** | Every effectful instruction is on the token chain of each channel it touches; a token has one writing consumer; the function's effect summary covers its body. The channels `Machine.Registers` and `Machine.Flags` exist | Same chains; no instruction touches a machine channel except a preserved operation | Same | Same | Same | Same | Same |
| **Machine-state residue** | The eight machine opcodes are admitted; the module declares the profile. After machine SSA construction only `LiveIn`, `LiveOut` and `IndirectJump` remain | None. Preserved opaque and external operations stay | Same | Same | Same | Same | Same; a preserved operation needs a capability entry |
| **Source-call residue** | None | `SourceCall` admitted | Same | None | None | None | None |
| **Dynamic-type residue** | None | `Dyn.*` operations, dynamic values and unresolved flexible integer types admitted | Same | Same | Same, plus `Guard.*` | No `Dyn.*`, no dynamic value, no unresolved flexible type; `Guard.*` admitted | `Guard.*` only with a capability entry |
| **High-level vocabulary** | None | Sequence value forms | Sequence value forms, `PolyIteration` | Same | Same, plus `ParallelFor`, `ParallelReduce`, `Placed` | Same | No sequence value form, no `PolyIteration`; parallel and placed regions only with a capability entry |
| **Physical layout** | Machine widths only | Abstract: no target-dependent layout, offset or byte order | Same | Same | Same | Natural-layout address operations, `SizeOf`/`OffsetOf`, optional layouts and folded byte order admitted | Same |
| **Target legality** | Not checked | Not checked | Not checked | Not checked | Not checked | Not checked | Every instruction is native in the capability set, a call to a recorded helper, or a preserved ABI reference |
| **Source mapping** | Every instruction has an origin | Same | Same | Same | Same | Same | Same |

Three properties of the table matter more than any cell:

- **Structure, dominance, phi, memory, effect and source-mapping rules do not depend on the stage**; the type rule only narrows which type families exist, and the effect rule only loses the machine channels. A stage never weakens them. What changes from stage to stage is residue: which opcode and type families may still be present.
- **Every stage-dependent cell is an opcode or type range.** The verifier therefore needs one generic rule — "this opcode is not legal at this stage" — plus the capability lookup at `Legalized`. No stage needs code of its own.
- **Every invariant is decidable from the function, its module header and, at `Legalized`, the capability set.** None needs an analysis beyond the dominator tree that `IR-VERIFIER` already builds. An invariant that would need range, alias or shape facts belongs to a pass's proof obligation, not to a stage.

Inside `Machine`, the attribute `[machine-ssa]` of `MACH-IR` marks a function whose registers and flags have become SSA values. It is a sub-state owned and verified by that issue, not a tenth stage: it admits fewer constructs than `Machine` and nothing later depends on it except the raise.

### What a stage does not promise

A stage says nothing about correctness. A `Source` function that models a JavaScript shift without the count mask verifies at every stage and is still wrong; that is the business of the equivalence oracles. Likewise `Recognized` does not promise that every idiom was found, and `Value` does not promise that any fact was computed. The capability states `lifted` and `legalized` of PRD §23.6 are claims that a function verifies at `Source`, respectively at `Legalized` for its target — no more.

## Stages and optimizer phases

`OPT-FRAMEWORK` runs six phases in the order `canonicalize`, `recover`, `recognize`, `optimize`, `specialize`, `legalize`. Before the first pass of a phase, the pass manager advances every function to the phase's working stage; a function already at or beyond it is left alone. A pass may emit any opcode legal at the function's current stage, and nothing else.

| Phase | Working stage | What the entry check removes | What becomes available to the phase's passes |
| --- | --- | --- | --- |
| `canonicalize` | `Source` | — | — |
| `recover`, `recognize` | `Recognized` | — | `PolyIteration` |
| `optimize` | `MemoryIteration` | `SourceCall` | — |
| `specialize` | `Specialized` | — | `Guard.*`, `ParallelFor`, `ParallelReduce`, `Placed` |
| `legalize` | `Execution` | `Dyn.*`, dynamic values, unresolved flexible types | Layout-dependent address operations, optional layouts, folded byte order |
| after `legalize` | `Legalized` | Sequence value forms, `PolyIteration`, everything outside the capability set | Target-only operations such as `UnalignedLoad` or `Frame.Push` |

Consequences:

- A phase that spans two stage names works at the later one, so the whole admitted vocabulary is available to it. `Value` and `Representation` are passed through; they remain valid targets for tools and tests that want exactly that contract.
- A function still at `Machine` when `recover` starts is not advanced by the pass manager and is skipped by all later phases. It cannot reach an emitter; the blocker that kept it at `Machine` is reported by the raise.
- A `recognize` sweep that the pass manager returns to from `optimize` runs on functions already at `MemoryIteration`. The stage does not move back; recognition rules emit nothing whose range ends before `MemoryIteration`.
- The entry check is where residue surfaces: a `SourceCall` that recognition left unresolved fails the advance into `optimize`, a `Dyn.*` that specialization left without a runtime lowering fails the advance into `legalize`.
- "A pass left the IR in a stage the phase does not allow" (PLY0357 of `OPT-FRAMEWORK`) means: the pass output contains an opcode that is not legal at the function's current stage.

## Serialized dumps

- A `.pir` dump may hold functions at any of the nine stages, and at different stages in one module. Every function header prints `stage(<Name>)`. On input an absent stage means `Source`.
- A reader accepts a dump only if each function verifies at the stage it states. No tool raises or lowers a stage while reading or writing.
- A module with a `Machine` function declares its machine profile.
- A `Legalized` function is verifiable only together with the target profile it was legalized for. A tool that reads one must be given that profile; without it the dump is rejected, not verified at a weaker stage.
- The dump is a debugging and fixture form for every stage (PRD §4.3). It is versioned by its `pir <n>` header and is not a compatibility promise for internal nodes between compiler versions.

## Evidence

### Worked example 1 — `RotL32`, JavaScript to C#

Source: `Hawkynt/Hawkynt.github.io@045397b4c934487bc2eb385125fdbd9589502f7f:Cipher/OpCodes.js`, lines 79–83.

```js
RotL32: function(value, positions) {
  value = value >>> 0; // Ensure unsigned 32-bit
  positions &= 31;
  return ((value << positions) | (value >>> (32 - positions))) >>> 0;
},
```

The IR below is illustrative; opcode spellings follow the implementing issues and are not fixed here. The parameters are typed from the `@param {uint32}` and `@param {int32}` annotations of the source.

| Stage | The function at that stage | A malformed module the stage rejects |
| --- | --- | --- |
| `Machine` | Not used: a source frontend enters at `Source` | — |
| `Source` | `%n = And.i32 %positions, 31` · `%l = Shl.u32[mask] %value, %n` · `%d = Sub.i32[wrap] 32, %n` · `%r = LShr.u32[mask] %value, %d` · `%o = Or.u32 %l, %r` · `Ret.u32 %o`. The `[mask]` flag carries the JavaScript rule that a shift count is taken modulo 32, which is what makes `positions = 0` return `value` | A `RegRead` in the body (machine residue); `Shl.u32` with a `u16` left operand (type rule) |
| `Recognized` | `%o = RotateLeft.u32 %value, %n` · `Ret.u32 %o` (rule of `REC-ROTATE`, masked variable form) | Nothing more than `Source`: the stage admits `PolyIteration`, it removes nothing |
| `Value`, `MemoryIteration` | Unchanged | A remaining `SourceCall`, for example an unresolved `Math.imul` |
| `Specialized` | Unchanged. Without the annotations the parameters would be dynamic and this stage would hold a guarded fast version beside the generic one | A `Guard.*` is legal from here on; before it is rejected |
| `Representation`, `Execution` | Unchanged: two scalars, no layout decision | Any remaining `Dyn.*` or dynamic value |
| `Legalized` (C#) | Unchanged if the profile lists `RotateLeft.u32` as native; otherwise the shift form again, written by a legalization rule | `RotateLeft.u32` on a profile that does not list it |

The function passes five stages without a single rewrite, which is the case the "advance rewrites nothing" rule is made for.

### Worked example 2 — a 6502 `ADC` sequence lifted to C#

Sequence: `CLC` · `LDA $10` · `ADC $11` · `STA $12`, an 8-bit add of two zero-page bytes.

| Stage | The function at that stage | A malformed module the stage rejects |
| --- | --- | --- |
| `Machine` | Per instruction, with its address and bytes as origin: `FlagWrite @c, false`; a load and `RegWrite.u8 @a` with writes of `@n` and `@z`; for `ADC` reads of `@a`, `@c` and `@d` (the decimal flag selects BCD arithmetic), an add with carry, writes of `@a`, `@c`, `@n`, `@z`, `@v`; a store of `@a` | An unknown register; a `u16` write to the 8-bit `@a`; a read of a flag that every path leaves undefined; a machine opcode in a module without a profile |
| `Machine` with `[machine-ssa]` | The accumulator and the flags are SSA values; `@d` is a `LiveIn` because nothing in the sequence defines it; `@a` and the written flags end in `LiveOut` | Any remaining `RegRead`, `RegWrite`, `FlagRead`, `FlagWrite` |
| `Source` | After the raise: the three zero-page cells are accesses to a bound region, `@d` is bound by an explicit contract (decimal mode clear), the flag results nobody reads are ordinary dead values. No machine opcode remains | A remaining `LiveIn` or `LiveOut`; an instruction on a `Machine.Flags` channel |
| `Recognized` | With the carry-in known to be false and the carry-out dead, the add with carry is `Add.u8[wrap]` | Nothing more than `Source` |
| `Value` … `Execution` | Unchanged | As in example 1 |
| `Legalized` (C#) | `Load.u8`, `Add.u8[wrap]`, `Store.u8`, each native in the profile | An operation or access form the profile does not list |

What the stage rules cannot express here: whether decimal mode really is clear. That is a contract of the raise, recorded in its report, not a property the verifier can read off the function. And until machine SSA construction, a register read is not a use of a value, so dominance says nothing about register contents — a read of a never-written register is legal and becomes a `LiveIn`.

### The same two examples under the other options

| Option | Rejects in addition | Cannot express |
| --- | --- | --- |
| 1. One node set, stage tag | — (reference) | Nothing the examples need |
| 2. Capability flags | Nothing: the same residue rules, keyed by property | The order itself. "`TargetLegal` implies no dynamic residue" must be added as an implication rule per property pair, which rebuilds the linear order by hand |
| 3. Separate IR per stage | Residue is unrepresentable instead of rejected: a `RegRead` cannot be written in a `Source` IR | "Advance without rewriting": `RotL32` would be copied five times unchanged. A module with functions at different stages calling each other needs references across IRs |
| 4. Separate machine IR | Machine residue after the raise is unrepresentable | A lifted body. The `ADC` above is one add, three loads or stores and a handful of bit operations around five register and flag accesses; all of the former would have to exist in both node sets. An opaque instruction that survives to `Legalized` would too |

### How PB-Compiler stages its IR

Read at `Hawkynt/PB-Compiler@0d73acbea185a2597357d53f7ce9acb44758ba76` (LGPL-3.0-or-later), files under `PowerBasic.Compiler/Ir/` and `PowerBasic.Compiler/Hir/`.

- `IrRepresentationStage` has six values: `Mir`, `Ssa`, `OptimizedSsa`, `LowIr`, `MachineSsa`, `MachineIr`. The tag sits on the module. The high-level IR has its own operation model and its own `HirVerifier`; `Mir` and the later forms share one module container.
- `IrRepresentationTransitions.TryAdvance` moves forward only and only to the adjacent stage, and only if the destination contract holds.
- `IrRepresentationContract.Verify` calls a stage "a semantic promise, not a progress counter" and is cumulative: from `Ssa` on the structural verifier must pass; from `LowIr` on every instruction must additionally have an effect contract. `MachineSsa` and `MachineIr` are refused for a module — machine forms are separate products.
- `IrVerifier` checks the same things at every stage: the entry block has no predecessor; no empty block; exactly one terminator, last; phis lead the block and their incoming blocks equal the reachable predecessors; operands dominate their uses, with phi operands checked at the predecessor; an operand belongs to this function and to a block; per-instruction type agreement for binary operations, comparisons, casts, loads, stores, address arithmetic, returns, conditional branches, selects, switches and calls.
- `IrPassManager.VerifyEachPass` is a switch. When on, the *function* is verified after each pass and a failure throws naming the pass. Stage contracts are checked only at the boundaries, not after each pass. Dominators use the Cooper–Harvey–Kennedy algorithm and are rebuilt for each verification.

Polypiler keeps the promise semantics, the forward-only rule, the cumulative contracts and function-granular verification after a pass. It differs in three places, each for a reason PB-Compiler does not have:

- **The tag is per function.** A lifted binary raises function by function, and a blocked function must not hold back the others.
- **Skipping is allowed.** With every invariant expressed as a range, verifying at the destination implies every stage in between.
- **Both ends share the node set.** PB-Compiler has one source language, so one separate high-level IR is cheap, and machine code is only its output. Polypiler has many frontends that must not share syntax classes (PRD §5.3) and takes machine code as *input*, so machine state has to be expressible in the IR the analyses run on.

### Node classes and verifier rules for the minimum core

Counted from the ready issues: the six model records of `IR-SSA` (module, function, block, instruction, value id, constant), its ten structural opcodes, and the verifier rules those issues give a diagnostic code — seven structural (`IR-VERIFIER`), four memory (`IR-MEMORY`), four effect (`IR-EFFECTS`), one stage legality (`IR-STAGES`). Integer and float type rules come on top in every option alike. Option 3 needs no stage rule, so it multiplies the other fifteen.

| Option | Node records | Opcode descriptors | Rule implementations | Extra machinery |
| --- | --- | --- | --- | --- |
| 1 | 6 | 10, each with a first and last stage | 16, each with a stage range | One enum, one transition gate |
| 2 | 6 | 10, each with a property set | 16, each keyed by properties | At least 6 properties for the six boundaries that differ; 64 combinations of which the pipeline reaches 7; 15 property pairs whose independence has to be decided and tested |
| 3 | 54 (6 × 9) | 90 (10 × 9) | 15 × 9 = 135, or one generic set over an abstraction — which is option 1 | 8 lowering translators that must map every opcode, changed or not; the 16 fact domains of PRD §4.4 once per representation |
| 4 | 12 | 20, plus the 8 machine opcodes | 15 × 2 + 1 = 31 | 1 translator; a second builder, printer and parser |

For comparison, PB-Compiler's class-per-instruction design has 18 instruction classes in `IrInstructions.cs` for one of its IRs; an option-3 design in that style multiplies classes, not descriptors.

### Does "verifier after each pass" stay linear?

Yes for every rule except dominator construction, and in practice for that too, under two conditions this decision imposes.

With *n* instructions, *o* operands, *b* blocks and *e* edges of one function: block structure is O(*n* + *e*); the type rule, the phi rule, the effect-chain rule and the stage rule are O(*o*), the stage and capability lookups being one table or hash probe per instruction; the dominance check is O(*o*) once the dominator tree carries pre- and post-order numbers. Building the tree with Cooper–Harvey–Kennedy is linear for the reducible graphs compilers produce but O(*b*²) in the worst case.

The conditions:

1. **Verify the function, not the module.** Functions are immutable and a rewrite returns a new instance, so "what changed" is a reference comparison. A pass that touches one function costs one function's verification.
2. **Reuse the dominator tree while the control-flow graph is unchanged.** It is cached on the function instance and dropped by the same CFG invalidation the analyses use.

The number of verifications per function is bounded by the pass manager: at most `MaxPhaseIterations` (8) sweeps of the passes registered in a phase, plus the bounded returns to `recognize`. Total verifier work is therefore O(passes × sweeps × function size) — linear in module size for a fixed pass list.

No timing exists yet; there is no verifier to measure. `IR-VERIFIER` records the time for a 10 000-instruction function and a 10 000-block chain, which gives the first number.

## Rejected options

**2 — capability flags instead of a linear stage.** The flexibility is unused: the six phases of `OPT-FRAMEWORK` run in a fixed order and every consumer in the backlog asks "is this function at stage X", never "does it have property P". The cost is real: properties are not independent (target legality without removed dynamic residue is meaningless), so each pair needs a decided relation and a test, and the legality table that is one range per opcode becomes a set per opcode. Where a genuine sub-state exists inside a stage, an attribute owned by one issue — as `[machine-ssa]` — covers it without opening the matrix.

**3 — separate IRs per stage.** It is the only option where residue is unrepresentable instead of rejected, and that is its whole advantage. Against it: PRD §4.2 calls the layers logical and lets them share nodes; the shared analyses of §4.4 would run on nine representations; a function that needs nothing done at a stage is copied anyway; mixed-stage modules — required for lifted binaries — need cross-IR references; and the table above multiplies every count by nine. Five of the eight boundaries would separate IRs that differ by no node or by a single opcode family.

**4 — hybrid with a separate machine IR.** A lifted instruction is mostly ordinary arithmetic, bit manipulation and memory access; `MACH-IR` adds eight opcodes and one module line, registers them in the shared registry and turns registers and flags into ordinary SSA values with the existing `ExtractBits`/`InsertBits`. A separate node set would duplicate everything else, and opaque instructions, which are legal from `Machine` to `Legalized`, would have to live in both. The one thing the hybrid buys — no machine state after the raise — the stage rule already enforces with one range.

**Stage tag on the module** (option 1 as worded). Rejected in favour of the function: raising is per function, guarded specialization keeps a generic version beside the fast one, and a `Call` may target a function at any stage.

**Collapsing the two empty boundaries into seven stages.** Rejected: see the note under the [stage list](#stage-list).

## Consequences for the backlog

The stage names, their order and the tables above are the contract `IR-STAGES` and `IR-VERIFIER` implement. Where ready issues disagreed with each other, this record decides, and the named issue changes.

| Issue | Change | Why |
| --- | --- | --- |
| `IR-VERIFIER` | Owns the `Stage` enum, since its public surface uses it and `IR-STAGES` depends on it, not the reverse. `Verify` gains a function-level entry point, verifies each function at its recorded stage unless a stage is passed explicitly, and accepts the target capability set that `Legalized` needs | Dependency cycle; modules hold functions at different stages; linear cost; `LEG-FRAMEWORK` uses the verifier as its `Legalized` oracle |
| `IR-STAGES` | Takes the enum from `IR-VERIFIER`. Range of `Guard.*` is `Specialized..Legalized`; range of `ParallelFor`, `ParallelReduce` and `Placed` is `Specialized..Legalized`. Records the phase table and the dump rules. States how a dump names the target profile of a `Legalized` function | The defining issue of `Guard.*` says so; the three parallel opcodes are produced in phase `specialize`, which works at `Specialized`; a `Legalized` dump is not verifiable without its profile |
| `OPT-PARLOOP`, `OPT-PARREDUCE`, `OPT-DEVICE` | Stage range `Specialized..Legalized` instead of `Execution..Legalized` | A pass in phase `specialize` cannot emit an opcode that is first legal two stages later, and `legalize`, which makes the representation decisions in between, has not run yet |
| `OPT-FRAMEWORK` | Adopts the phase table: the working stage per phase, the advance before the first pass, the skip of `Machine` functions, and the meaning of PLY0357 | "The stage reached after each phase is checked" names no stage |
| `SPEC-IR` | `source-semantic` is not the first stage of PRD §4.2; `machine-effects` precedes it with order 5. The seven remaining stage documents use the ids and orders of the stage list | The seed documents fix orders 10 and 90 |
| `IR-FLEXINT` | Width resolution runs as a required pass no later than the end of phase `specialize` | An unresolved flexible type fails the advance into `legalize` |
| `ISA-X86-IDIOMS` | Hand-off condition (1) reads "at `Source` or later, no machine opcode" | Checked after `recover`, the function is already at `Recognized` |
| `LEG-FRAMEWORK` and the issues that write stage `legalized` | Spell the stage `Legalized`; the capability type the verifier consumes is the opcode-and-type set of `IR-STAGES`, onto which the legalizer's richer capability model projects | One name per stage; the IR layer cannot depend on the legalizer |

Not changed, and checked against this record: `MACH-IR` (machine opcodes `Machine..Machine`, stack machines enter at `Source`), `MACH-BIN-RAISE` (the only producer of `Source` from `Machine`), `IR-SOURCECALL` (`Source..Recognized`), `IR-DYNAMIC` (`Source..Specialized`), `IR-POLYITER` (`Recognized..Execution`) with the `recover` rules that produce it, `IR-AGGREGATE`, `IR-OPTIONAL`, `IR-BYTEORDER`, `IR-SOURCEMAP`, and the frontend and backend issues that deliver `Source` and require `Legalized`.

## Follow-ups

- **Machine-readable form.** The stage documents and the opcode ranges are recorded under `spec/ir/` once the schema of `SPEC-IR` exists; this record is their source until then.
- **`spec/ir/` layout.** `SPEC-IR` allows only `operations/<mnemonic>.yaml`, `stages/<name>.yaml` and `version.yaml`, with the stage range inside each operation document. `IR-STAGES` and a dozen other issues write family files such as `spec/ir/stages.yaml` and `spec/ir/verifier-rules.yaml`. One of the two has to give; that is a decision about the specification layout, not about stage invariants, and is left to its own issue.
- **Target profile in a `Legalized` dump.** The text form has no place for it yet; `IR-STAGES` owns the function header and adds it.
- **No analysis is missing.** Every invariant above is decidable from structure, the opcode table, the dominator tree and the capability set, all of which are planned. No follow-up issue for a new analysis is needed.
