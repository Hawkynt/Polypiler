---
name: "Implementation task (Definition of Ready)"
about: "Specify a bounded Polypiler change with verifiable acceptance criteria before implementation."
title: ""
labels: []
assignees: []
---

> Read the [Definition of Ready](https://github.com/Hawkynt/Polypiler/blob/main/docs/DEFINITION-OF-READY.md) and the [normative PRD / Definition of Done](https://github.com/Hawkynt/Polypiler/blob/main/docs/PRD.md). Replace prompts with evidence or **N/A — reason**. **Unknown is not N/A.** Do not check readiness boxes until a reviewer accepts the evidence. Ready is permission to start, **not** proof of implementation or tests.

**Readiness:** Draft
**Category:** Compiler/IR | ABI/runtime | Tooling/CI | Documentation/process
**Milestone / PRD section / spec ID:**
**Issue author / proposed implementer:**
**DoR reviewer / review date:** Not reviewed
**Blocking dependency / owner:** None identified / specify

## Purpose and scope

**Problem and value (R01):**

**Inputs → outputs, exact artifacts, affected compiler stages (R02):**

**Explicitly out of scope (R02):**

**Predecessor issues/PRs, existing APIs, environment, tools and feature branch (R03):**

## Language profiles and semantic contract

**Source language, dialect, version, input format (R04):**

**Destination language, runtime, OS, CPU, bitness, ABI and required capabilities (R04):**

**Types/widths, conversion, overflow/rounding, control flow, alias/memory/effects, ownership/lifetime, errors and externally observable behavior (R05):**

**Input fixture, repository/path and pinned commit or hash (R06):**

**Independent source oracle/vectors, license and redistribution restrictions (R06):**

**Explicit nonblocking assumptions and unresolved unknowns with owner:**

## Acceptance criteria — measurable results (R07)

- [ ] 
- [ ] 
- [ ] 

## Behavior scenarios (R08)

**Positive — Given / When / Then:**

**Negative, unsafe, unsupported, or edge case — Given / When / Then:**

## Verification plan (R09)

**Tests to add:** unit, integration, differential, property/fuzz, proof/analysis, target-compiler/emulator, or documentation/link checks as relevant.

**How to run / toolchain versions or required harness:**

**Expected evidence / diagnostics / capability status:**

> The tests and implementation **do not need to exist or pass yet** to declare Ready. They must be realistically executable under the specified constraints.

## Risk and delivery (R10–R11)

**Known platform/security/licensing/resource risks, unsupported subset and mitigation/owner:**

**One-PR slice, or linked ordered child issues:**

**Implementer, responsible readiness reviewer and expected PR reviewer:**

## Definition of Ready review (R12)

An item is checked only when the preceding evidence exists or the reviewer accepts **N/A — reason**. Correctness-critical unknowns block Ready.

- [ ] **R01** — Problem, value, milestone and specification reference.
- [ ] **R02** — Bounded deliverables, input/output and explicit exclusions.
- [ ] **R03** — Available prerequisites, tools, contracts and branch plan.
- [ ] **R04** — Applicable source/target/ABI profiles or reasoned N/A.
- [ ] **R05** — Semantics, memory/effects and observable behavior.
- [ ] **R06** — Pinned external source, reference oracle and provenance, or reasoned N/A.
- [ ] **R07** — Finite falsifiable acceptance criteria.
- [ ] **R08** — Positive and negative/edge behavioral tests.
- [ ] **R09** — Reproducible verification plan and expected evidence.
- [ ] **R10** — Relevant risks, unsupported features and blocker owners.
- [ ] **R11** — Reviewable size and clear implementer/reviewer roles.
- [ ] **R12** — Human reviewer approves Ready, records date, **no active blockers**.

**Reviewer decision:** Draft | Ready | Blocked  
**Reviewer identity / date:**  
**Reason for decision, blocker owner and permitted nonblocking assumptions:**

> Workflow: Draft → Ready → In Progress → Done, with Blocked as an alternate state for missing prerequisites. Any substantive change to corpus, semantic scope, target ABI, runtime, dependencies or acceptance criteria revokes readiness pending review. Done additionally requires reviewed merge and [PRD DoD](https://github.com/Hawkynt/Polypiler/blob/main/docs/PRD.md); no direct default-branch pushes.
