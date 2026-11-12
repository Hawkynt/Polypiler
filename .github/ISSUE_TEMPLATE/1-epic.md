---
name: Epic
about: A language, format or CPU too large for one pull request; implemented only through sub-issues
title: "lang(<language>): <language> as source and target"
labels: [enhancement, state:draft]
---

> Follow the [canonical applicability and readiness review](https://github.com/Hawkynt/Polypiler/blob/main/ISSUES.md#applicability-and-readiness-review).
> Use `N/A — specific reason` only for irrelevant fields; unknown is not N/A. Ready specifies the
> work and its verification plan; passing tests and ≥ 95 % line and branch coverage belong to Done.
> Name the readiness reviewer/date and blockers. Append any applicable required sections missing
> from this skeleton; keep the kind-specific prompts. Documentation target state: `N/A — documentation/process only`.

<!-- Rules: ISSUES.md. An epic carries no implementation; it closes when its last sub-issue closes. -->

## Why

<User job and PRD section.>

## User stories

- As a **<role from ISSUES.md>**, I want **<capability>**, so that **<benefit>**.

## Scope

**In scope**
- <…>

**Out of scope**
- <… → #issue or "not planned">

## Sub-issues

Link each as a GitHub sub-issue. A language needs at least the frontend and the backend.

- [ ] `frontend(<language>): …` #<n>
- [ ] `backend(<language>): …` #<n>
- [ ] `infra: <language> toolchain runner and CI job` #<n>
- [ ] `e2e(<source>→<target>): …` #<n>

## Done when

<The epic-level gate, e.g. "one real algorithm works as frontend and backend in a multi-language round trip" (PRD §8).>

## Target state

<highest PRD §23.6 state the epic reaches once all sub-issues are done>

## Readiness review

- **Reviewer / date:** <maintainer or delegate; date of decision>
- **Decision:** <state:draft | state:ready | state:blocked; rationale>
- **Blockers:** <none, or each prerequisite, owner, resolving issue and evidence needed>

Changes to scope, fixture hash, profile/ABI, dependency, licence or acceptance contract require fresh
review before implementation continues. Claim a Ready issue with `in-flight` and `<number>-<slug>`.
