---
name: Spike
about: A time-boxed question; produces a decision, never production code
title: "spike: <question>"
labels: [documentation, state:draft]
---

> Follow the [canonical applicability and readiness review](https://github.com/Hawkynt/Polypiler/blob/main/ISSUES.md#applicability-and-readiness-review).
> Use `N/A — specific reason` only for irrelevant fields; unknown is not N/A. Ready specifies the
> work and its verification plan; passing tests and ≥ 95 % line and branch coverage belong to Done.
> Name the readiness reviewer/date and blockers. Append any applicable required sections missing
> from this skeleton; keep the kind-specific prompts. Documentation target state: `N/A — documentation/process only`.

<!-- Rules: ISSUES.md. -->

## Question

<The one question to answer.>

## Why now

<What is blocked on the answer; PRD section.>

## Time box

<hours/days>

## Options

1. <…>
2. <…>

## Deciding evidence

<What measurement, prototype or source would settle it.>

## Deliverable

- [ ] `docs/adr/<nnnn>-<slug>.md` with the decision and its evidence
- [ ] Follow-up issues drafted and linked

## Readiness review

- **Reviewer / date:** <maintainer or delegate; date of decision>
- **Decision:** <state:draft | state:ready | state:blocked; rationale>
- **Blockers:** <none, or each prerequisite, owner, resolving issue and evidence needed>

Changes to scope, fixture hash, profile/ABI, dependency, licence or acceptance contract require fresh
review before implementation continues. Claim a Ready issue with `in-flight` and `<number>-<slug>`.
