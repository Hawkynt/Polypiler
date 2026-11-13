---
name: Decoder / lifter / emitter
about: Binary, bytecode, CPU or token-format input or output
title: "<isa|format>(<name>): <decoder|lifter|encoder|emitter|semantics|oracle> for <subset>"
labels: [enhancement, area:recovery, state:draft]
---

> Follow the [canonical applicability and readiness review](https://github.com/Hawkynt/Polypiler/blob/main/ISSUES.md#applicability-and-readiness-review).
> Use `N/A — specific reason` only for irrelevant fields; unknown is not N/A. Ready specifies the
> work and its verification plan; passing tests and ≥ 95 % line and branch coverage belong to Done.
> Name the readiness reviewer/date and blockers. Append any applicable required sections missing
> from this skeleton; keep the kind-specific prompts. Documentation target state: `N/A — documentation/process only`.

<!-- Rules: ISSUES.md. Replace every <…>; an issue containing <…> or TBD stays state:draft. -->

## Why

<What cannot be read or produced until this lands. PRD section / spec ID.>

## User stories

- As a **<role from ISSUES.md>**, I want **<capability>**, so that **<benefit>**.

## Scope

**In scope**
- <…>

**Out of scope**
- <… → #issue or "not planned">

## Preconditions and inputs

- **Depends on:** #<n> (closed) <or why it can run in parallel>
- **Fixtures:** `<owner/repo>@<full SHA>:<path>` — licence <SPDX>
- **Oracle:** <independent implementation that produces expected results>
- **Toolchains:** `<key in tooling/toolchains.lock>`, …
- **Sourcing:** rung <1–5> of the AGENTS.md ladder — <source, licence>

## Format / ISA

- **Specification:** <name, version, URL>
- **Coverage table:**

| Opcode / mode / record | Status (`supported` / `diagnosed` + code) |
| --- | --- |
| <…> | <…> |

- **Observable-state contract:** <registers, flags, memory ranges, I/O, external calls compared against the emulator/runtime (PRD §23.1)>
- **Negative inputs:** <invalid/undocumented opcodes, truncated or corrupt input, wrong dialect>

## Specification

- **Public surface:** <types/members, IR nodes, CLI syntax>
- **Diagnostics:** <code — when — message>
- **Files:** <paths created/changed>
- **Semantics preserved:** <overflow, signedness, rounding, order, effects, exceptions — as applicable>

## Acceptance criteria

`tests/features/<area>/<issue-slug>.feature`

```gherkin
Feature: <name> <component>
  Scenario: <positive case with concrete values>
    Given <…>
    When <…>
    Then <observable result>

  Scenario: <negative case: what must be refused>
    Given <…>
    When <…>
    Then <diagnostic code / no rewrite / non-zero exit>
```

## Test design

| # | Technique | Class / boundary | Input | Expected |
| --- | --- | --- | --- | --- |
| 1 | Happy path | <…> | <…> | <…> |
| 2 | Exception | <each refusal/failure path> | <…> | <diagnostic / exception / exit code> |
| 3 | Boundary | <on / inside / outside each limit> | <…> | <…> |
| 4 | Equivalence | <one row per input class, valid and invalid> | <…> | <…> |

## Measurements

| Metric | How | Threshold | Source of threshold | Reported in |
| --- | --- | --- | --- | --- |
| Line coverage, whole solution | `coverage gate` CI job | ≥ 95 % | ISSUES.md §7 | CI |
| Branch coverage, whole solution | `coverage gate` CI job | ≥ 95 % | ISSUES.md §7 | CI |
| Acceptance scenarios | `dotnet test --filter Category=Acceptance` | 100 % pass | ISSUES.md | CI |
| Documented opcodes/modes exercised | conformance fixtures | 100 % | PRD §7.2 | PR |
| Mismatches vs emulator/runtime | differential runner, seed <n> | 0 | PRD §23.1 | CI |

## Evidence to attach

- <hashes, manifest, tool versions, expected vs actual, diff, CI link — as applicable (PRD §6.6)>

## Implementation plan

1. Add the `.feature` file and failing NUnit tests.
2. <…>
3. Run the measurements and paste the numbers into the PR.

**Do not:** mock compilers or runtimes, emit placeholders, hard-code expected output, weaken assertions, drop the negative scenarios.

## Target state

<parsed | lifted | recognized | proved | legalized | emitted | compiled | executed | verified | performance-validated> (PRD §23.6)

## Definition of Ready

- [ ] Title follows `<scope>: <outcome>`
- [ ] Why, user story with a listed role, in-scope and out-of-scope lists present
- [ ] Every dependency is closed or explained; epics have their sub-issues linked
- [ ] Every fixture pinned to a full SHA with a licence; the oracle is independent
- [ ] Toolchains named by their `tooling/toolchains.lock` key
- [ ] Specification names the public surface, diagnostics and files
- [ ] ≥ 1 positive and ≥ 1 negative Gherkin scenario with concrete values
- [ ] Test-design table covers happy path, exceptions, boundaries and equivalence classes
- [ ] Every measurement has how, threshold and the threshold's source
- [ ] Implementation plan is test-first and lists the forbidden shortcuts
- [ ] Target state chosen; fits one pull request
- [ ] Labels and milestone set

## Readiness review

- **Reviewer / date:** <maintainer or delegate; date of decision>
- **Decision:** <state:draft | state:ready | state:blocked; rationale>
- **Blockers:** <none, or each prerequisite, owner, resolving issue and evidence needed>

Changes to scope, fixture hash, profile/ABI, dependency, licence or acceptance contract require fresh
review before implementation continues. Claim a Ready issue with `in-flight` and `<number>-<slug>`.
