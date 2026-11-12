# Definition of Ready (DoR)

**Status:** normative work-item intake policy for Polypiler. Applies to compiler, runtime, interop, tooling, documentation and process issues.

**Authority:** [PRD](PRD.md) §§8 (P0), 9 (work packages), 17 (semantic preservation), 24.1 (**Definition of Done**), and 33.3 (repository rules). DoR determines **whether work may begin**; DoD determines **whether a reviewed PR may be considered finished**. If a proposed task conflicts with the PRD, resolve the conflict before declaring it Ready.

Start with the [issue template](../.github/ISSUE_TEMPLATE/implementation.md) and consult the [Ready and Blocked examples](examples/DEFINITION-OF-READY-EXAMPLES.md).

## Ready is not Done

An issue is **Ready** if a developer or agent can begin a bounded implementation **without inventing correctness-critical requirements**, selecting arbitrary ABI semantics, or assuming unavailable test fixtures. An implementation, passing tests, built artifacts and green CI are **not preconditions of readiness**.

They are completion evidence under [PRD §24.1](PRD.md): applicable positive and negative tests, actual target compiler or interpreter execution, independent oracle comparison, ≥80% newly added core-logic coverage, green CI and at least one human reviewer. Work stays on feature branches; never push directly to `main`, `master` or `trunk`.

## Mandatory checklist

For each criterion below, document the evidence in the issue. Mark it complete only when evidence is supplied or the criterion is **N/A with a reason**. Merely checking a box does not satisfy a gate.

| ID | Gate | Before an issue is Ready |
|---|---|---|
| **R01** | Purpose | A concrete user/compiler problem, intended value, milestone P0–P6, PRD section or spec/ADR reference |
| **R02** | Bounded scope | Exact inputs/outputs, affected stages, deliverables, artifacts, exclusions and one reviewable PR or ordered child issues |
| **R03** | Dependencies | Preceding PRs/issues, existing contracts/APIs, reachable tools, source access, branch plan, identified blockers |
| **R04** | Profiles | Source language/dialect/version, destination language/runtime/OS/CPU/ABI/bitness and relevant capabilities, or justified N/A |
| **R05** | Semantic contract | Applicable types/widths, signedness/overflow/rounding, ordering, state/effects, memory/aliasing, ownership/lifetime, failures and external observations |
| **R06** | Provenance/oracle | Imported program fixture path **plus pinned revision/hash**, independently executable reference oracle or vectors, and copyright/license/redistribution rules, or justified N/A |
| **R07** | Acceptance | Finite, observable, falsifiable behavior and artifacts (including diagnostics and capability status where appropriate) |
| **R08** | Behavior scenarios | A positive case and a negative/unsafe/unsupported/edge case; optimization tasks specify when a rewrite **must not** happen |
| **R09** | Verification plan | Tests, relevant executable toolchains/commands, comparison or proof approach, failure reporting and expected CI evidence. No requirement that tests have already passed |
| **R10** | Risk/constraints | Platform/security/licensing/performance concerns where relevant, unsupported subsets, explicit owners of unresolved risks |
| **R11** | Size/ownership | One coherent reviewable change or split dependencies; implementer/decision owner and responsible human reviewer identified |
| **R12** | Decision | R01–R11 satisfied or reasoned N/A, **no active prerequisite blocker**, reviewer/maintainer records decision and date |

### Applicability and unknowns

- Write **`N/A — <specific reason>`** for a genuinely irrelevant dimension. **Unknown is not N/A**.
- A documentation-only task may mark language/CPU/ABI, foreign fixtures and toolchain oracles N/A, but still needs objective acceptance checks, an edge/failure scenario, document references, scope, dependencies and review.
- A self-contained IR change may mark an *external* source corpus N/A when independently specified unit/property tests cover its semantics.
- For foreign ABIs, actual bitness, calling convention, library identity and aggregate layout are not optional details; an unknown here normally blocks work.
- Missing machine services or unsupported surviving source semantics must result in a precise diagnostic or a verified compatible adapter, not fabricated output.
- If an external dependency is missing, name the missing item, accountable owner, required decision/artifact and how readiness will be revisited. Nonblocking assumptions must be recorded and shown not to affect the acceptance contract.
- Avoid specifying incidental implementation details in BDD scenarios; assert observable behavior, not a particular class or pass name.

## Readiness workflow and ownership

Until a label automation is deliberately established, the **issue body is authoritative**. Record:

`**Readiness:** Draft | Ready | In Progress | Blocked | Done`

`**DoR reviewed by / date:** ...` and `**Blockers:** ...`

Optional labels can mirror this state only when those labels exist and are maintained consistently. GitHub's open/closed status alone is not a DoR decision.

| State | Entry | Next state |
|---|---|---|
| **Draft** | Issue created or information is incomplete, unclear or awaiting refinement | Ready after R01–R12 review; Blocked when an external prerequisite prevents a valid contract |
| **Ready** | Reviewer/maintainer confirms all gates, date and absence of active blockers | In Progress when implementation begins; back to Draft/Blocked if facts change |
| **In Progress** | A Ready issue is actively implemented on a feature branch | Done only after PRD DoD and reviewed merge; Blocked if a prerequisite is lost; Draft if rescoping invalidates readiness |
| **Blocked** | A named unresolved prerequisite or decision prevents safe meaningful progress | Draft for refinement, then Ready only after fresh gate review |
| **Done** | Implementation accepted in a reviewed, merged PR with DoD evidence | Further requirements belong in another issue/reopened review |

**Issue author or proposed implementer:** supplies R01–R11 evidence and proposes a state. **Designated human reviewer/maintainer:** checks the evidence, challenges assumptions, records R12 and decides Ready/Blocked. Do not self-certify a disputed semantic assumption. PR review and approval remain separate from the DoR decision.

A **change to scope, source hash, compiler or runtime version, CPU/ABI, dependency, licensing, semantic behavior or acceptance conditions** invalidates the affected readiness evidence. Return to Draft (details need refinement) or Blocked (a necessary external decision/artifact is unavailable), and re-review before proceeding. This also applies after work has begun.

## Readiness review procedure

1. Classify the task: compiler/IR, ABI/machine/runtime, tooling/CI or documentation/process.
2. Check R01–R11, accepting explicit N/A only with a rationale tied to the task's observable behavior.
3. Check that positive and negative scenarios and the test strategy could actually detect a wrong implementation.
4. Split overlarge tasks into ordered independent PR-sized work items.
5. Confirm no dependency prevents the selected scope; have a human reviewer record R12, date and Ready/Blocked rationale.
6. Once Ready, implement **only the approved scope**. In the PR report actual tests and versions; link the issue and use the PRD Definition of Done before merge.

A failing proof or unavailable test oracle is not permission to silently report `verified`. Documentation-only work need not run `dotnet build` in a repository without a solution; say what was actually checked.

## Worked examples

- [Ready P0 work item and Blocked foreign-ABI work item](examples/DEFINITION-OF-READY-EXAMPLES.md)
- [Reusable implementation issue template](../.github/ISSUE_TEMPLATE/implementation.md)
- [Verification and release gates](quality/VERIFICATION.md)
