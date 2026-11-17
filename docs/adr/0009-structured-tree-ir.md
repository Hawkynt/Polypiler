# ADR 0009 — The structured typed tree IR is the IR

Area: foundation

**Status:** accepted · **Date:** 2026-11-17 · **Supersedes in part:** [ADR 0001](0001-ir-stage-invariants.md) · **Index:** [decision records](README.md)

**Decides:** [PRD §4.2](../prd/02-core-architecture.md#s4-2) and [§4.3](../prd/02-core-architecture.md#s4-3) "minimum mandatory core IR", [§17.2](../prd/05-semantics-and-recovery.md#s17-2) "effects are data dependencies", and the storage form ADR 0001 assumed. Background: [IR and analysis contracts](../architecture/IR.md), [compilation pipeline](../architecture/PIPELINE.md).

## Context

ADR 0001 was written before any IR existed. It assumed the IR would be typed SSA — blocks, phis, a
dominator tree, effect tokens threaded through every effectful instruction — carried through nine
named stages, each with its own row of verifier rules, and written out as a `.pir` text dump.

What was built is different. `Polypiler.Core.IR` is a structured, typed tree: functions hold
statements (`IrBlock`, `IrIf`, `IrWhile`, `IrDoWhile`, `IrFor`, `IrSwitch`, `IrBreak`, `IrContinue`,
`IrReturn`, assignments and stores) over typed expressions (`IrBinary`, `IrConvert`, `IrCall`,
`IrIntrinsic`, field and array access, …) that name mutable locals (`IrLocal`). On it, today:

- four frontends import into it — C# (Roslyn), JavaScript (Acornima), Rust and Python;
- `IrVerifier` checks every module before anything interprets or emits it (`PLY0100`);
- `IrInterpreter` runs it and is the reference oracle of the differential tests;
- every optimizer pass (`O0001`, `O0002`, `O0003`, `O0005`, `O0006`, `O0023`, `O0463`, `O0464`) and
  the required `PyIntSpecialization` rewrite it in place of an SSA form;
- `IrEffects` gives each function an effect summary — places read and written, allocation, trap,
  termination — computed bottom-up over the call graph;
- `Polypiler.Core.Legalize` replaces what a target profile lacks by calls into the one IR library,
  driven by each backend's capability table;
- the C#, JavaScript, Rust and Python backends build a target syntax tree straight from the
  structured control flow and print it, with no control-flow reconstruction step;
- `IrModuleFormat` serializes a module exactly, as a versioned binary form.

None of it uses a stage tag, a phi, a basic block or an effect token. The question is whether to
convert this to the SSA design of ADR 0001 or to make the tree the architecture.

## Decision

1. **The structured typed tree of `Polypiler.Core.IR` is the storage form of the IR**, for every
   frontend, pass, analysis and backend. There is no SSA storage form, no basic block and no phi in
   the IR, and no issue is to introduce one.
2. **Effects are per-function summaries, not tokens.** `IrEffects` (what a function may read and
   write outside its own variables, whether it allocates, may trap or may run forever) is the effect
   model. Ordering inside a function follows the tree's evaluation order, which the verifier and the
   interpreter fix; a pass that reorders proves non-interference from the summaries of the calls and
   the places the expressions touch. Finer channels (volatile, MMIO, device I/O, machine registers
   and flags) extend the summary's place kinds, not a token chain.
3. **Facts are keyed by tree node or program point.** An analysis attaches its result to an
   `IrExpr`, an `IrStmt` or a position between statements, as `O0003` keeps known constants per
   local along the tree. Invalidation follows the rewrite: a pass that returns a new node drops the
   facts of the node it replaced.
4. **An SSA view may be derived where an analysis needs one** — value numbering, a sparse dataflow
   problem, a machine lifter's register and flag recovery. It is built from the tree, used, and its
   results are mapped back to tree nodes or lowered back into the tree. It is never the form a module
   is stored, passed between passes, serialized or emitted in, and nothing outside the analysis that
   builds it depends on it.
5. **Stages are capability and legality sets, not IR dialects.** One node set covers a module from
   import to emission. What a module may still contain is checked where it matters: the verifier
   checks types, declarations and structure for every module; legalization and the backend's
   capability table check, per target profile, that every remaining operation has a native form or a
   helper, and report what has neither. The nine stage names of ADR 0001, its stage × invariant
   table and its phase-to-stage table are withdrawn. Should a sub-state ever need marking (a machine
   body before raising, for instance), it is an attribute owned by the issue that needs it. The
   stage documents under `spec/ir/stages/` and the `stages` range of an operation document stay as
   the names of these legality sets; they no longer imply a dialect or a stage tag on a function.
6. **Debugging and fixture form.** `IrModuleFormat` is the serialized form; a readable text dump may
   be added for debugging but is not a compatibility promise. The `.pir` text grammar with
   `stage(<Name>)` headers is withdrawn.

## What stays from ADR 0001

The parts that do not depend on SSA stand: one node set rather than one IR per layer; the verifier
runs on every module a pass changed and before any emitter; a target-legality check is parameterized
by the target profile; and a stage — now a legality set — is a promise about which constructs are
present, never about meaning. ADR 0001's rejection of separate IRs per stage and of a separate
machine IR stands as argued there.

## Why the tree

- **All four source languages are structured.** C#, JavaScript, Rust and Python give loops, `break`,
  `continue`, `switch` and `try` directly; the tree keeps them, so import is a one-to-one mapping and
  source locations stay on the node they came from.
- **Every backend prints structured code.** C#, JavaScript, Rust and Python have no `goto` in common
  use (Python and Rust none at all). An SSA/CFG IR would need control-flow structuring and SSA
  destruction in front of every backend, with its own failure cases (irreducible graphs, dispatch
  loops, extra flags); the tree needs neither.
- **The verifier and the interpreter exist and are the oracle.** Both are written against the tree
  and back every differential test. Converting them would rebuild the foundation every working path
  rests on, for no capability the backlog needs now.
- **The passes that matter work on the tree.** Folding, constant propagation, call evaluation, dead
  code, inlining, unrolling, lambda specialisation and call merging run on it today, with effect
  summaries as their legality check. Where a pass would genuinely profit from SSA (GVN, sparse
  conditional propagation over large functions), decision 4 allows a derived view without changing
  the storage form.
- **Effect summaries cover the reorderings in scope.** Within one function the tree fixes evaluation
  order; across calls the summary is what a token chain would tell, at function granularity.

## What changes for the backlog

- Issues whose deliverable is SSA storage, basic blocks, phis, effect tokens, a stage enum with nine
  dialects or the `.pir` grammar are closed as not planned, each with a pointer to what covers the
  need on main.
- Issues whose need survives in tree form — a verifier rule, an analysis fact domain, an effect kind,
  a legality check — are re-scoped to that form and stay open.
- Issues that were blocked only by the SSA foundation are unblocked: the foundation they waited for
  is the tree on main.
- Machine and bytecode lifters (`MACH-*`, MSIL, JVM, Wasm) may recover registers, flags and the
  evaluation stack through a derived SSA view internally; what they deliver is the tree.

## Rejected option

**Convert to the SSA design of ADR 0001.** It would make SSA-based algorithms from the literature
directly applicable and give a uniform data-flow form for future machine lifting. Against it: every
frontend, pass, the verifier, the interpreter, the binary format and all four backends would be
rewritten; each backend would gain control-flow structuring and SSA destruction; nothing the four
languages need today is blocked by the tree; and the one real advantage is available through a
derived view (decision 4) where an analysis needs it.
