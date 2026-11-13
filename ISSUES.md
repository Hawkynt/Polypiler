# Writing issues

Every change to Polypiler starts as an issue, and an issue is a **work order**: whoever picks it up —
a maintainer, a contributor, a coding agent with no context beyond the issue text — must be able to
implement it correctly without asking a single question. If they would have to ask, the issue is not
finished.

This file defines what an issue must contain before work may start (the **Definition of Ready**), how
large work is cut into issues, and what "done" means. The skeletons to copy live in
[`.github/ISSUE_TEMPLATE/`](.github/ISSUE_TEMPLATE/). The planned backlog is kept as files under
[`backlog/`](backlog/README.md) and published to GitHub by script. The product contract they reference is
[`docs/PRD.md`](docs/PRD.md).

## Lifecycle

| State | Label | Meaning | Who moves it on |
| --- | --- | --- | --- |
| Draft | `state:draft` | Opened; sections missing or containing `TBD`. Must not be worked. | Author completes it |
| Ready | `state:ready` | Every Definition-of-Ready item below is ticked. | Implementer claims it |
| In flight | `in-flight` | Claimed; a branch `<number>-<slug>` exists. Replaces `state:ready`. | Implementer |
| Blocked | `state:blocked` | Cannot proceed; the last comment names the blocker and the issue that resolves it. | Whoever unblocks it |
| Done | *(closed)* | Closed by the squash merge of a PR whose body says `Closes #<number>`. | The merge |

An issue goes back to `state:draft` the moment an implementer finds an ambiguity. The fix is to
edit the issue, not to guess in the code.

**Dependencies are blockers.** An issue that depends on an open issue is `state:blocked`, however
complete its text is: it carries a **Blocked by** list at the top of its body and the same issues as
GitHub's native *blocked by* relation. When the last blocker closes, the `unblock` workflow replaces
`state:blocked` with `state:ready` — provided the issue was blocked only by its dependencies, which
its *Readiness review* records. Claim only `state:ready` issues; never start one whose blockers are
open, even if the code you need "is nearly there".

**Epics are never claimed.** An epic carries the `epic` label instead of a state. It closes when its
last sub-issue closes; the work is in the sub-issues.

## Applicability and readiness review

`ISSUES.md` is the canonical readiness policy; use the existing kind-specific templates. See the
[hypothetical Ready and Blocked examples](docs/examples/ISSUE-READINESS.md) for completed reviews.

**Ready is not Done.** Ready means the inputs, scope, acceptance contract and verification plan are
specified. No implementation or passing tests are required to be Ready. Passing tests, coverage,
artifacts and reviewed merge are completion evidence under the Definition of Done below.

**Owner:** the author supplies the evidence; the maintainer or a named delegate accepts applicability
rationales, resolves ambiguities and records the readiness decision and date. The implementer checks
the evidence before claiming the issue, replaces `state:ready` with `in-flight`, and creates
`<number>-<slug>`. Labels follow the Lifecycle table, not an independent issue-body-only workflow.

Write **N/A — specific reason** only for a genuinely irrelevant field. An unknown is not N/A; a bare
N/A is incomplete. Documentation-only work may mark foreign profiles, external fixtures, runtime
oracles and product diagnostics inapplicable, but still needs scope, positive and negative scenarios,
all four test-design axes (or a reason an axis does not apply), sourced measurements, evidence and
review. Its compiler target state is `N/A — documentation/process only`, never `verified`.

A complete work order with **zero** unmet prerequisites can be Ready. **One** unmet prerequisite is
enough to prevent readiness; **several** must each be recorded, never hidden behind a guessed default.
An applicable field needs evidence; an irrelevant field needs a rationale; an unknown field needs a
resolution. In particular, an unknown source/target dialect, bitness, calling convention, DLL, header
or independent oracle cannot be marked irrelevant merely to start an ABI task.

Changes to scope, fixture hash, source/target profile, ABI, dependency, licence or acceptance contract
**revoke** the affected readiness evidence. Review it again **before implementation continues**.
Missing or ambiguous requirements return to `state:draft`; an unavailable external prerequisite uses
`state:blocked`. Record each blocker, its Owner, the resolving issue and evidence needed to unblock.
If no resolving issue exists yet, explicitly record that fact; do not invent a number or claim Ready.
After resolution, the maintainer or delegate reviews the work order again before `state:ready`.

Keep each template's kind-specific sections. A short epic, spike or bug skeleton does not waive the
applicable required sections below: append the missing sections before declaring an issue Ready.

## How work is cut into issues

**One issue = one pull request = one reviewable concern.** When an issue cannot be done in one pull
request, it is an **epic**: it carries no implementation of its own, lists its parts as GitHub
sub-issues, and closes when the last sub-issue closes.

| Work | Shape |
| --- | --- |
| A new **language** (C#, Kotlin, Perl, …) | Epic `lang(<language>)`, with **at least** two sub-issues: `frontend(<language>)` and `backend(<language>)`. Add more as needed: the toolchain runner and CI job, the runtime-helper library, a semantic profile per supported language version. A frontend or backend too big for one PR becomes an epic itself, split by construct family (scalars and arithmetic · control flow · arrays and memory · functions · records and classes · exceptions · …). |
| A **binary, bytecode or token format**, or a **CPU** (MSIL, WASM, 6502, x86, C64 PRG, …) | Epic `isa(<name>)` or `format(<name>)` with at least `decoder`/`lifter` (input) and `encoder`/`emitter` (output) sub-issues, plus `semantics` for instruction semantics and `oracle` for the emulator or runtime runner. |
| **Every optimization** — rewrite, recognition rule, analysis-driven transformation | **Its own issue**, never bundled. One rule, one issue, one optimization ID `O####` and one document `docs/optimizations/O####-<slug>.md`. Two rules that "always go together" are still two issues; one depends on the other. |
| An **analysis** (ranges, known bits, aliasing, escape, …) | Its own issue. The optimizations that consume it are separate issues that depend on it. |
| An **IR construct** or **type family** | One issue per family (fixed-width integers, IEEE floats, MBF, spans, unions, …). |
| An **end-to-end translation pair** (`JS → C#` on a pinned Cipher algorithm) | Its own `e2e(<source>→<target>)` issue per fixture, depending on the frontend and backend issues. It adds no compiler features — it only proves them. |
| A **CLI command** or flag | Its own `cli` issue. |
| **Infrastructure** — CI job, corpus tooling, runner | Its own `infra` issue. |
| A **specification or ADR** | Its own `spec` issue; its deliverable is the document plus validators where applicable. |
| A **question nobody can answer yet** | A time-boxed `spike` issue; its deliverable is a decision document, never production code. Implementation follows in new issues. |
| A **defect** | A `bug` issue with a failing reproduction. |

Split further whenever an issue needs more than **seven acceptance scenarios**, spans **more than one
bounded context** (PRD §5.1), or depends on something not yet closed that it does not itself deliver.

## Titles

`<scope>: <outcome, imperative, specific>`. The scope is one of `lang(x)`, `frontend(x)`, `backend(x)`,
`isa(x)`, `format(x)`, `optimization`, `analysis`, `ir`, `types`, `intrinsics`, `legalize`, `interop`,
`objects`, `permutation`, `runtime`, `verify`, `catalog`, `package`, `adapter`, `upstream`, `e2e(a→b)`,
`cli`, `infra`, `spec`, `spike`, `bug`.

- `lang(kotlin): Kotlin as source and target`
- `frontend(kotlin): import IntArray/LongArray, for-loops and integer arithmetic`
- `optimization: O0007 recognize 32-bit rotate-left from shift/or pairs`
- `e2e(js→cs): Cipher Adler-32 translates, compiles and matches its vectors`

Not: `Kotlin support`, `Improve optimizer`, `Fix stuff`.

## Required sections (Definition of Ready)

An issue is **ready** when every section below is present, specific and free of `TBD`. The skeletons
already contain the headings; each kind adds the extra sections listed under
[Kind-specific sections](#kind-specific-sections).

### 1. Why

One paragraph: the user job served, the PRD section or spec ID it implements, and what is impossible
until it lands.

### 2. User stories

At least one, in the fixed form *As a **role**, I want **capability**, so that **benefit**.* Use only
these roles, so stories stay comparable:

| Role | Who that is |
| --- | --- |
| **Algorithm porter** | A developer translating an algorithm from one language to another |
| **Reverse engineer** | Recovers source from bytecode or machine code |
| **Retro developer** | Targets or reads 8/16-bit machines, DOS, tokenized BASIC |
| **Library maintainer** | Maintains an upstream repository (PB-Compiler, Cipher, BitPerm, …) that Polypiler feeds on or replaces |
| **Catalogue consumer** | Pulls a verified algorithm from the catalogue into a project |
| **Compiler contributor** | Extends Polypiler itself — a pass, a frontend, a backend |
| **Release manager** | Decides what may be advertised as supported |

### 3. Scope

Two lists, both mandatory: **In scope** and **Out of scope**. Out of scope names the adjacent things
an implementer would be tempted to do, and the issue (or "not planned") where they belong instead.

### 4. Preconditions and inputs

- **Depends on:** issue numbers, each closed — or the reason the work can proceed in parallel.
- **Fixtures:** every input pinned as `owner/repo@<full SHA>:<path>`, with its licence. No "some
  checksum from Cipher".
- **Oracle:** the *independent* implementation that produces expected results — the real source
  compiler/interpreter, an emulator, published vectors. Polypiler's own output is never an oracle
  (PRD §23.1).
- **Toolchains:** every compiler, runtime or emulator the tests invoke, named by its key in
  `tooling/toolchains.lock`. That file is the single place an exact version is pinned; an issue that
  needs a toolchain not yet in it adds the entry as part of its scope. Versions are not copied into
  issues, where they would go stale.
- **Sourcing:** the rung of the implementation-sourcing ladder in [`AGENTS.md`](AGENTS.md) that
  applies, with the source and its licence.

### 5. Specification

What exactly will exist afterwards — concrete enough that two implementers would produce the same
public surface:

- Public types/members, IR nodes, CLI syntax and options, with their names.
- Diagnostic codes, exit codes (PRD §10.1) and the message for each rejected input.
- Files and folders to create or change.
- Semantics the code must preserve: overflow, signedness, rounding, evaluation order, effects,
  exceptions — whichever apply, stated, not implied.

### 6. Acceptance criteria (Gherkin)

Executable scenarios, copied verbatim into `tests/features/<area>/<issue-slug>.feature` and bound to
NUnit fixtures. Rules:

- At least **one positive** and **one negative** scenario. The negative one proves the feature
  refuses what it must refuse — unsupported construct, unsafe rewrite, wrong dialect, corrupt input.
- Concrete values, not placeholders: `Given the 32-bit value 0x80000001`, not `Given some value`.
  Use `Scenario Outline` with an `Examples` table for value families.
- Every `Then` is observable from outside: output, exit code, diagnostic code, generated file, IR
  dump, manifest field. Never "Then it works".

```gherkin
Feature: Recognize 32-bit rotate-left
  Scenario Outline: A shift/or pair with complementary amounts becomes a rotate
    Given the C# expression "(x << <n>) | (x >> <m>)" over uint x
    When it is lifted and optimized with --opt balanced
    Then the IR contains exactly one RotateLeft32 with amount <n>
    And the opt-report lists O0007 with proof class "exhaustive-16bit+smt"

    Examples:
      | n | m  |
      | 1 | 31 |
      | 8 | 24 |

  Scenario: Non-complementary amounts are not rewritten
    Given the C# expression "(x << 3) | (x >> 30)" over uint x
    When it is lifted and optimized with --opt balanced
    Then the IR contains no RotateLeft32
    And the opt-report lists O0007 as "not applicable: amounts 3+30 != 32"
```

### 7. Test design

Every issue states its tests **before** implementation, as a table with one row per test case, and
the table covers all four techniques — a technique that genuinely does not apply says so and why:

| Technique | What it demands |
| --- | --- |
| **Happy path** | The intended use with representative, realistic input produces the specified result. |
| **Exceptions and error paths** | Every way the feature can refuse or fail — invalid input, unsupported construct, missing toolchain, I/O failure, timeout, budget exceeded — produces the specified diagnostic, exception or exit code, and leaves no partial or falsely verified artefact. |
| **Boundaries** | Each limit tested on, just inside and just outside: 0, 1, max, max+1; `int.MinValue`/`MaxValue`; empty and single-element collections; shift by 0, width−1, width; first and last opcode; NaN, ±0, ±∞, subnormals; empty file, truncated file. |
| **Equivalence classes** | The input domain partitioned into classes the code treats alike (signed/unsigned, each numeric width, each dialect, each operand kind, valid/invalid) with at least one test per class, valid and invalid alike. |

```markdown
| # | Technique | Class / boundary | Input | Expected |
| --- | --- | --- | --- | --- |
| 1 | Happy path | rotate by a mid-range amount | x=0x12345678, n=8 | 0x34567812 |
| 2 | Boundary | amount 0 | x=0x12345678, n=0 | unchanged, no RotateLeft32 emitted |
| 3 | Boundary | amount width−1 | x=0x80000001, n=31 | 0xC0000000 |
| 4 | Equivalence | non-complementary amounts | n=3, m=30 | not rewritten |
| 5 | Exception | non-constant amount without range fact | n unknown | not rewritten, opt-report reason |
```

**Coverage: at least 95 % of all lines and at least 95 % of all conditional branches**, measured over
the whole solution and enforced by the `coverage gate` job in CI. A pull request that lowers either
number below 95 % does not merge. This is stricter than PRD §7.2 (80 %/90 %) and supersedes it. Code
that cannot be reached by a test is a design smell to fix — extract the logic so it can be called
directly — not a reason to exclude it.

### 8. Measurements

A table with one row per metric: **what** is measured, **how** (the exact command or tool), the
**threshold**, and **where** the number is reported. Mandatory rows:

| Metric | How | Threshold | Source | Reported in |
| --- | --- | --- | --- | --- |
| Line coverage, whole solution | `coverage gate` CI job (coverlet) | ≥ 95 % | ISSUES.md §7 | CI |
| Branch coverage, whole solution | `coverage gate` CI job (coverlet) | ≥ 95 % | ISSUES.md §7 | CI |
| Acceptance scenarios | `dotnet test --filter Category=Acceptance` | 100 % pass | ISSUES.md | CI |
| Contract partitions | conformance fixtures | 100 % of documented opcode/type partitions exercised | PRD §7.2 | PR description |

Add kind-specific rows (below). **Every threshold names its source**: the PRD section, an ADR, or a
measured baseline. Where no source exists — performance budgets usually, before their ADR — the row
says `record only, no gate` and links the issue that will set the budget. Never invent a number.

### 9. Evidence to attach

What the pull request must show (PRD §6.6): source and generated-code hashes, generated manifest, tool
versions, fixture values, stdout/stderr/exit code, reference vs actual result and diff, tolerance
policy for floating point, opt diagnostics, CI link. List only what applies.

### 10. Implementation plan

Ordered steps, small enough to tick off, test-first:

1. Add the `.feature` file and failing NUnit tests.
2. … the production steps, naming the files …
3. Run the measurements; paste the numbers into the PR.

Plus **Do not**: the shortcuts that would make the tests green without doing the work — mocked
compilers, placeholder emitters, hard-coded expected output, weakened assertions, skipped negative
cases.

### 11. Target state

The highest gate state from PRD §23.6 this issue reaches (`parsed` · `lifted` · `recognized` ·
`proved` · `legalized` · `emitted` · `compiled` · `executed` · `verified` ·
`performance-validated`). The capability report may advertise exactly that state and nothing more.
Documentation/process work uses `N/A — documentation/process only`; it adds no compiler capability.

### 12. Labels and milestone

The type (`enhancement`, `bug`, `documentation`), every `area:*` that applies, `state:draft` or
`state:ready`, and the phase milestone `P0`…`P6`.

### Definition of Ready — checklist

Tick in the issue body; the implementer verifies it before claiming the issue.

- [ ] Title follows `<scope>: <outcome>`
- [ ] Why, user story with a listed role, in-scope and out-of-scope lists present
- [ ] Every dependency is closed or explained; epics have their sub-issues linked
- [ ] Every applicable fixture pinned to a full SHA with a licence; oracle independent, or reasoned N/A
- [ ] Toolchains named by their `tooling/toolchains.lock` key
- [ ] Specification names the public surface, diagnostics and files
- [ ] ≥ 1 positive and ≥ 1 negative Gherkin scenario with concrete values
- [ ] Test-design table covers happy path, exceptions, boundaries and equivalence classes
- [ ] Every measurement has how, threshold and the threshold's source
- [ ] Implementation plan is test-first and lists the forbidden shortcuts
- [ ] Target state chosen (or documentation/process N/A); fits one pull request
- [ ] Labels and milestone set

## Kind-specific sections

### Frontend

- **Language profile:** language and exact version(s), dialect, the parser/semantic model used and
  where it comes from (Roslyn, Cipher's typed IL, PB-Compiler's binder, …).
- **Construct table:** construct → PolyIR mapping → `supported` / `diagnosed` (rejected with a code).
  Every construct of the family is in one of the two columns; nothing is silently skipped.
- **Semantic traps** of this language for this family: numeric coercion, overflow behaviour, integer
  division and modulo signs, string encoding, evaluation order, truthiness.
- Extra measurement: share of the family's constructs covered by fixtures (target 100 %).

### Backend

- **Target profile:** language/runtime version, dialect, pinned toolchain.
- **Lowering table:** PolyIR construct → emitted form → helper required (yes/no).
- **Runtime helpers:** which, and why the target has no native equivalent.
- **Output rules:** the generated code compiles without edits, is idiomatic for the target and
  carries no dependency on Polypiler at runtime.
- Extra measurements: compile success on the pinned toolchain (100 %), generated source size,
  helper count.

### Optimization

- **ID and document:** `O####` and `docs/optimizations/O####-<slug>.md`, following PB-Compiler's
  optimization documents.
- **Before/after IR** for the canonical case.
- **Legality preconditions**, formally: widths, ranges, alias and effect facts required. Anything not
  proven blocks the rewrite.
- **Proof class:** exhaustive small-width, SMT bitvector, range/alias facts — and what happens on a
  timeout (the rewrite does not fire).
- **Counterexamples:** at least one input where a naive version of the rewrite is wrong, as a negative
  scenario. For arithmetic, include values where carries overlap.
- **Switch:** the flag that disables exactly this optimization, and its `--opt` levels.
- **Interactions:** passes that must run before it, passes it enables.
- Extra measurements: differential `--opt none` vs optimized on randomized fixtures with a fixed seed
  (0 mismatches); speed, allocations and code size per target against `--opt none` (thresholds from
  the performance ADR, else `record only`).

### End-to-end pair

- Source fixture and its vectors pinned; the target toolchain; the exact `polypiler` command.
- Exactness policy: bit-exact, or ULP/relative/absolute tolerance per floating-point operation.
- Extra measurements: vectors passed (100 %), manual edits to generated code (0).

### Decoder / lifter / emitter (binary, bytecode, CPU, token format)

- Format or ISA specification by name and version; opcode/mode coverage table.
- Observable-state contract: which registers, flags, memory ranges, I/O and calls are compared
  against the emulator/runtime (PRD §23.1).
- Negative inputs: invalid or undocumented opcodes, truncated or corrupt files, wrong dialect.

### Spike

- The question, the time box, the options to evaluate and the evidence that would decide between
  them. Deliverable: a decision document (ADR), and the follow-up issues drafted.

### Bug

- Reproduction: exact input, command, toolchain versions. Expected vs actual. The failing test is
  the first commit of the fix.

## Definition of Done

An issue is done when its pull request is merged and:

- every Gherkin scenario and every row of the test-design table is automated and green; line and
  branch coverage are each ≥ 95 %; all measurements meet their thresholds and the
  numbers are in the PR;
- the specification documents, the capability report and the README support matrix reflect the
  achieved target state — and nothing beyond it;
- `dotnet build` and `dotnet test` pass, CI is green, every commit is signed and follows the commit
  rules in [`AGENTS.md`](AGENTS.md);
- licence and provenance notices are in place for anything taken from elsewhere;
- follow-up work discovered on the way exists as new issues, not as TODO comments.
