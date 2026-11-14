# Polypiler documentation

This index explains the **intended** architecture and development workflow. The repository is currently in its specification/bootstrap phase; diagrams and code snippets below are designs, **not implemented-feature claims**.

The [product requirements index](PRD.md) links ten topical parts carrying the normative specification.
Original chapter numbers and section links are retained. The pages here are a readable, cross-linked engineering guide; if a description conflicts with the PRD, create a design decision and reconcile the two before implementation.

## Read in order

1. [Compilation pipeline](architecture/PIPELINE.md) — input discovery to verified generated output.
2. [Intermediate representation and analysis](architecture/IR.md) — semantic types, effects, value facts and passes.
3. [Frontends, backends and targets](architecture/TARGETS.md) — what each importer/exporter must provide, including LLVM, DOS and binary inputs.
4. [Semantic recovery and bit permutations](architecture/SEMANTIC-RECOVERY.md) — undo bit hacks, loop unrolling, and target-specific optimizations.
5. [Foreign interop and ABI legalization](architecture/INTEROP.md) — preserve DLLs, synthesize layouts and marshal safely.
6. [High-level language lowering](architecture/LANGUAGE-LOWERING.md) — generics, interfaces, exceptions, resource scopes and coroutines.
7. [Worked translation walks](architecture/WALKTHROUGHS.md) — the first required cross-language showcases.
8. [Verification and release gates](quality/VERIFICATION.md) — the definition of "supported", test oracles and failure behavior.

## Reference

- [Integer and bit operations](reference/integer-operations.md) — every operation of the IR on every width, its edge cases and its form in each target profile.
- [Floating-point operations](reference/floating-point-operations.md) — every floating-point operation of the IR with its contract, special values and portable implementation.
- [Known runtime defects](reference/known-runtime-defects.md) — defects of a target runtime that change what correct generated code computes, their minimal reproductions and the forms the backends write around them.
- [Exit codes](cli/exit-codes.md) — the exit code of every failure category of `polypiler`, generated from the code; `polypiler help exit-codes` prints the same table.
- [Value records](reference/records.md) — structs, record structs and fixed-shape object literals: their value semantics, what each frontend imports and how each profile writes them.
- [Module state](reference/module-state.md) — static fields and module variables shared by every call: their initialisation rule, what each frontend imports or refuses and how each profile and module format writes them.
- [Views](reference/views.md) — `Span<T>`, `ReadOnlySpan<T>`, `stackalloc` and `subarray`: the view of the IR, its traps, what each frontend imports and how each profile writes it.
- [String operations](reference/string-operations.md) — strings of UTF-16 code units: every operation with its ordinal meaning, the two white-space sets, what each frontend imports or refuses and how each profile writes it.
- [Benchmarks](benchmarks/README.md) — how to add a benchmark, run it, read its result and compare two result sets against the budgets.
- [Audit of the PB-Compiler LLVM emitter](llvm/pb-emitter-audit.md) — what PB-Compiler's `--emit-llvm` writes at the pinned commit, what LLVM 23.1.3 accepts of it, and the gaps against PRD §26.3.
- [Error catalogue](errors/README.md) — every diagnostic code with its severity, exit code, cause and what to do, generated from `spec/diagnostics.yaml` by `polypiler-tooling report errors`.
- [Backends: tree, passes, printer](architecture/BACKENDS.md) — the syntax tree each backend builds of its language, the target passes over it and the printer that writes it; how to add a capability row or a pass.

## Work-item readiness

Use the [canonical issue-writing standard](../ISSUES.md), its
[kind-specific templates](../.github/ISSUE_TEMPLATE/), and the
[hypothetical Ready and Blocked examples](examples/ISSUE-READINESS.md). Ready defines the work;
Done requires the implementation evidence and reviewed merge.

## What to implement first

The PRD's P0/P1/P2 sequence is the executable plan. Begin with a typed/effect-aware IR and actual **JavaScript Cipher → C#** and **PowerBASIC → C#** algorithm translations, not a universal parser or an all-language compatibility runtime. Use pinned upstream fixtures and compile and run generated C# against independent source oracles before claiming a translation works.

## How to change the architecture

- Update the relevant architecture page **and** the normative PRD (or record an explicit design decision) when changing a semantic invariant. Decisions are kept as [architecture decision records](adr/README.md).
- A frontend owns its source-language rules; the shared middle-end owns cross-language optimizations; a backend owns target representation and runtime contracts.
- Optimizations must declare legality conditions and negative tests; target emitters must reject unsupported operations rather than silently approximate them.
- Prefer vertical slices that import, optimize, emit, compile and independently verify one real algorithm.
- Do not edit upstream projects as part of documentation or integration planning. Migration requires separately reviewed, verified changes.

Related: [repository README](../README.md) · [PRD](PRD.md).
